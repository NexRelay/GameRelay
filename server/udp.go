package main

import (
	"crypto/hmac"
	"encoding/binary"
	"fmt"
	"net"
	"sync"
	"time"
)

const (
	// udpSessionTTL: a visitor session with no traffic for this long is dropped.
	udpSessionTTL = 90 * time.Second
	// udpMaxDatagram fits any game packet incl. our 8-byte header.
	udpMaxDatagram = 65535
)

// udpSession maps one public visitor (player) to a session id.
type udpSession struct {
	id         uint32
	publicPort uint16
	visitor    *net.UDPAddr
	sock       *net.UDPConn // the tunnel's public socket (replies go out from it)

	mu         sync.Mutex
	lastActive time.Time
}

func (u *udpSession) touch(t time.Time) {
	u.mu.Lock()
	u.lastActive = t
	u.mu.Unlock()
}

func (u *udpSession) idleSince(now time.Time) time.Duration {
	u.mu.Lock()
	defer u.mu.Unlock()
	return now.Sub(u.lastActive)
}

// udpRelay owns the carrier socket to the client and all visitor sessions.
type udpRelay struct {
	srv     *Server
	carrier *net.UDPConn

	mu         sync.Mutex
	clientAddr *net.UDPAddr
	sessions   map[uint32]*udpSession
	byVisitor  map[string]uint32 // "publicPort|visitorAddr" -> session id
	nextID     uint32
}

func newUDPRelay(s *Server) *udpRelay {
	return &udpRelay{
		srv:       s,
		sessions:  map[uint32]*udpSession{},
		byVisitor: map[string]uint32{},
	}
}

func (r *udpRelay) start() error {
	addr := fmt.Sprintf("%s:%d", r.srv.cfg.ListenAddr, r.srv.cfg.UDPPort)
	uaddr, err := net.ResolveUDPAddr("udp", addr)
	if err != nil {
		return err
	}
	sock, err := net.ListenUDP("udp", uaddr)
	if err != nil {
		return fmt.Errorf("bind udp carrier %s: %w", addr, err)
	}
	r.carrier = sock
	go r.readCarrier()
	return nil
}

func (r *udpRelay) stop() {
	if r.carrier != nil {
		r.carrier.Close()
	}
}

// readCarrier processes datagrams from the Windows client.
func (r *udpRelay) readCarrier() {
	buf := make([]byte, udpMaxDatagram)
	for {
		n, from, err := r.carrier.ReadFromUDP(buf)
		if err != nil {
			return // socket closed
		}
		if n < 2 || buf[0] != udpMagic {
			continue
		}
		switch buf[1] {
		case udpTypeAuth:
			r.handleAuth(buf[:n], from)
		case udpTypeKeepAliv:
			if r.isClient(from) {
				// Echo back so the client can measure liveness.
				_, _ = r.carrier.WriteToUDP([]byte{udpMagic, udpTypeKeepAliv}, from)
			}
		case udpTypeData:
			if n >= udpHeaderLen && r.isClient(from) {
				r.handleClientData(buf[:n])
			}
		}
	}
}

// handleAuth validates [magic][auth][ts 8B][mac 32B] and registers the
// sender as the carrier peer.
func (r *udpRelay) handleAuth(pkt []byte, from *net.UDPAddr) {
	if len(pkt) != 2+8+32 {
		return
	}
	ts := int64(binary.BigEndian.Uint64(pkt[2:10]))
	drift := time.Since(time.Unix(ts, 0))
	if drift < -udpAuthWindow || drift > udpAuthWindow {
		r.srv.log.Warn("udp auth rejected: timestamp drift", "from", from.String())
		return
	}
	want := udpAuthMAC(r.srv.cfg.Secret, ts)
	if !hmac.Equal(want, pkt[10:42]) {
		r.srv.log.Warn("udp auth rejected: bad mac", "from", from.String())
		return
	}
	r.mu.Lock()
	changed := r.clientAddr == nil || r.clientAddr.String() != from.String()
	r.clientAddr = from
	r.mu.Unlock()
	if changed {
		r.srv.log.Info("udp carrier registered", "client", from.String())
	}
	_, _ = r.carrier.WriteToUDP([]byte{udpMagic, udpTypeAuthOK}, from)
}

func (r *udpRelay) isClient(from *net.UDPAddr) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.clientAddr != nil &&
		r.clientAddr.IP.Equal(from.IP) && r.clientAddr.Port == from.Port
}

// servePublicSocket reads player packets from a UDP tunnel's public socket
// and forwards them to the client over the carrier.
func (r *udpRelay) servePublicSocket(t *Tunnel) {
	port := uint16(t.PublicPort)
	// Player payload is read straight into the frame after the header so
	// forwarding costs zero copies.
	frame := make([]byte, udpMaxDatagram)
	frame[0] = udpMagic
	frame[1] = udpTypeData
	binary.BigEndian.PutUint16(frame[6:8], port)

	for {
		n, visitor, err := t.udpSock.ReadFromUDP(frame[udpHeaderLen:])
		if err != nil {
			return // tunnel closed
		}
		sess := r.getOrCreateSession(port, visitor, t.udpSock)
		if sess == nil {
			continue
		}
		sess.touch(time.Now())

		r.mu.Lock()
		client := r.clientAddr
		r.mu.Unlock()
		if client == nil {
			continue // client carrier not registered yet
		}
		binary.BigEndian.PutUint32(frame[2:6], sess.id)
		_, _ = r.carrier.WriteToUDP(frame[:udpHeaderLen+n], client)
	}
}

// handleClientData forwards a client DATA frame to the visitor it belongs to.
func (r *udpRelay) handleClientData(pkt []byte) {
	sid := binary.BigEndian.Uint32(pkt[2:6])
	r.mu.Lock()
	sess := r.sessions[sid]
	r.mu.Unlock()
	if sess == nil {
		return
	}
	sess.touch(time.Now())
	_, _ = sess.sock.WriteToUDP(pkt[udpHeaderLen:], sess.visitor)
}

func (r *udpRelay) getOrCreateSession(port uint16, visitor *net.UDPAddr, sock *net.UDPConn) *udpSession {
	key := fmt.Sprintf("%d|%s", port, visitor.String())
	r.mu.Lock()
	defer r.mu.Unlock()
	if id, ok := r.byVisitor[key]; ok {
		return r.sessions[id]
	}
	r.nextID++
	sess := &udpSession{
		id:         r.nextID,
		publicPort: port,
		visitor:    visitor,
		sock:       sock,
		lastActive: time.Now(),
	}
	r.sessions[sess.id] = sess
	r.byVisitor[key] = sess.id
	r.srv.log.Debug("udp session created",
		"session", sess.id, "public_port", port, "visitor", visitor.String())
	return sess
}

// expireSessions drops sessions idle longer than udpSessionTTL.
func (r *udpRelay) expireSessions(now time.Time) {
	r.mu.Lock()
	defer r.mu.Unlock()
	for id, sess := range r.sessions {
		if sess.idleSince(now) > udpSessionTTL {
			delete(r.sessions, id)
			delete(r.byVisitor, fmt.Sprintf("%d|%s", sess.publicPort, sess.visitor.String()))
		}
	}
}

// dropTunnelSessions removes every session belonging to a closed tunnel.
func (r *udpRelay) dropTunnelSessions(port uint16) {
	r.mu.Lock()
	defer r.mu.Unlock()
	for id, sess := range r.sessions {
		if sess.publicPort == port {
			delete(r.sessions, id)
			delete(r.byVisitor, fmt.Sprintf("%d|%s", sess.publicPort, sess.visitor.String()))
		}
	}
}
