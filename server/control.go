package main

import (
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"log/slog"
	"net"
	"sync"
	"time"
)

const (
	// handshakeTimeout bounds the challenge/response exchange.
	handshakeTimeout = 10 * time.Second
	// controlReadTimeout: the client pings every 15s; if nothing arrives
	// for this long the connection is considered dead.
	controlReadTimeout = 90 * time.Second
	// pendingConnTTL is how long a public TCP visitor waits for the
	// client to dial back a data connection.
	pendingConnTTL = 15 * time.Second
)

// Server is the relay: one control client, N tunnels, pending visitor conns.
type Server struct {
	cfg *Config
	log *slog.Logger

	mu      sync.Mutex
	control *controlConn
	tunnels map[string]*Tunnel      // tunnel id -> tunnel
	byPort  map[string]string       // "tcp:25565" -> tunnel id
	pending map[string]*pendingConn // conn id -> waiting visitor

	udp *udpRelay

	listener net.Listener
	closed   chan struct{}
}

// controlConn wraps the authenticated control channel with a write lock.
type controlConn struct {
	conn net.Conn
	wmu  sync.Mutex
}

func (cc *controlConn) send(msg *Message) error {
	cc.wmu.Lock()
	defer cc.wmu.Unlock()
	_ = cc.conn.SetWriteDeadline(time.Now().Add(10 * time.Second))
	return WriteFrame(cc.conn, msg)
}

// pendingConn is a public TCP visitor waiting to be paired with a client
// data connection.
type pendingConn struct {
	conn     net.Conn
	tunnelID string
	deadline time.Time
}

// NewServer builds a Server from cfg.
func NewServer(cfg *Config, log *slog.Logger) *Server {
	s := &Server{
		cfg:     cfg,
		log:     log,
		tunnels: map[string]*Tunnel{},
		byPort:  map[string]string{},
		pending: map[string]*pendingConn{},
		closed:  make(chan struct{}),
	}
	s.udp = newUDPRelay(s)
	return s
}

// Run starts the control listener and UDP carrier and blocks until Close.
func (s *Server) Run() error {
	addr := fmt.Sprintf("%s:%d", s.cfg.ListenAddr, s.cfg.ControlPort)
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return fmt.Errorf("listen control %s: %w", addr, err)
	}
	s.listener = ln
	if err := s.udp.start(); err != nil {
		ln.Close()
		return err
	}
	go s.janitor()
	s.log.Info("relay server started",
		"control", addr, "udp_port", s.cfg.UDPPort)

	for {
		conn, err := ln.Accept()
		if err != nil {
			select {
			case <-s.closed:
				return nil
			default:
				return fmt.Errorf("accept: %w", err)
			}
		}
		go s.handleConn(conn)
	}
}

// Close shuts everything down.
func (s *Server) Close() {
	select {
	case <-s.closed:
		return
	default:
		close(s.closed)
	}
	if s.listener != nil {
		s.listener.Close()
	}
	s.udp.stop()
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.control != nil {
		s.control.conn.Close()
	}
	for _, t := range s.tunnels {
		t.close()
	}
	for _, p := range s.pending {
		p.conn.Close()
	}
}

// handleConn performs the handshake and routes the connection to its role.
func (s *Server) handleConn(conn net.Conn) {
	setNoDelay(conn)
	_ = conn.SetDeadline(time.Now().Add(handshakeTimeout))

	nonce, err := newNonce()
	if err != nil {
		conn.Close()
		return
	}
	if err := WriteFrame(conn, &Message{Type: MsgChallenge, Nonce: nonce}); err != nil {
		conn.Close()
		return
	}
	msg, err := ReadFrame(conn)
	if err != nil {
		conn.Close()
		return
	}
	if msg.Type != MsgAuth || !verifyMAC(s.cfg.Secret, nonce, msg.MAC) {
		s.log.Warn("auth failed", "remote", conn.RemoteAddr().String())
		_ = WriteFrame(conn, &Message{Type: MsgError, Reason: "authentication failed"})
		conn.Close()
		return
	}
	if msg.Version != ProtocolVersion {
		_ = WriteFrame(conn, &Message{Type: MsgError,
			Reason: fmt.Sprintf("protocol version mismatch: server=%d client=%d", ProtocolVersion, msg.Version)})
		conn.Close()
		return
	}
	_ = conn.SetDeadline(time.Time{})

	switch msg.Role {
	case "control":
		s.runControl(conn)
	case "data":
		s.pairData(conn, msg.ConnID)
	default:
		conn.Close()
	}
}

// runControl promotes conn to the active control channel. A new control
// connection replaces the previous one (single-client system).
func (s *Server) runControl(conn net.Conn) {
	cc := &controlConn{conn: conn}
	if err := cc.send(&Message{Type: MsgAuthOK, UDPPort: s.cfg.UDPPort, Version: ProtocolVersion}); err != nil {
		conn.Close()
		return
	}

	s.mu.Lock()
	if s.control != nil {
		s.log.Info("replacing existing control connection")
		s.control.conn.Close()
	}
	s.control = cc
	s.mu.Unlock()

	remote := conn.RemoteAddr().String()
	s.log.Info("client connected", "remote", remote)

	for {
		_ = conn.SetReadDeadline(time.Now().Add(controlReadTimeout))
		msg, err := ReadFrame(conn)
		if err != nil {
			break
		}
		s.handleControlMsg(cc, msg)
	}

	s.mu.Lock()
	stillActive := s.control == cc
	if stillActive {
		s.control = nil
	}
	s.mu.Unlock()
	conn.Close()

	// Only tear tunnels down when the disconnecting connection is still
	// the active one; a replaced connection must not kill the new state.
	if stillActive {
		s.log.Info("client disconnected, closing tunnels", "remote", remote)
		s.closeAllTunnels()
	}
}

func (s *Server) handleControlMsg(cc *controlConn, msg *Message) {
	switch msg.Type {
	case MsgPing:
		_ = cc.send(&Message{Type: MsgPong, TS: msg.TS})
	case MsgOpenTunnel:
		s.openTunnel(cc, msg)
	case MsgCloseTunnel:
		s.closeTunnel(msg.TunnelID)
	default:
		s.log.Debug("unknown control message", "type", msg.Type)
	}
}

// openTunnel validates and starts a tunnel, replying tunnel_ok/tunnel_fail.
func (s *Server) openTunnel(cc *controlConn, msg *Message) {
	fail := func(reason string) {
		_ = cc.send(&Message{Type: MsgTunnelFail, TunnelID: msg.TunnelID, Reason: reason})
	}
	if msg.TunnelID == "" {
		fail("missing tunnel id")
		return
	}
	if msg.Proto != "tcp" && msg.Proto != "udp" {
		fail("proto must be tcp or udp")
		return
	}
	if !s.cfg.PortAllowed(msg.PublicPort) {
		fail(fmt.Sprintf("port %d not allowed by server config", msg.PublicPort))
		return
	}
	key := fmt.Sprintf("%s:%d", msg.Proto, msg.PublicPort)

	s.mu.Lock()
	if _, dup := s.tunnels[msg.TunnelID]; dup {
		s.mu.Unlock()
		// Idempotent: the client re-opens tunnels after reconnecting.
		_ = cc.send(&Message{Type: MsgTunnelOK, TunnelID: msg.TunnelID})
		return
	}
	if _, used := s.byPort[key]; used {
		s.mu.Unlock()
		fail(fmt.Sprintf("public %s port %d already in use by another tunnel", msg.Proto, msg.PublicPort))
		return
	}
	s.mu.Unlock()

	t, err := s.startTunnel(msg.TunnelID, msg.Proto, msg.PublicPort)
	if err != nil {
		fail(err.Error())
		return
	}

	s.mu.Lock()
	s.tunnels[t.ID] = t
	s.byPort[key] = t.ID
	s.mu.Unlock()

	s.log.Info("tunnel opened", "id", t.ID, "proto", t.Proto, "public_port", t.PublicPort)
	_ = cc.send(&Message{Type: MsgTunnelOK, TunnelID: t.ID})
}

func (s *Server) closeTunnel(id string) {
	s.mu.Lock()
	t, ok := s.tunnels[id]
	if ok {
		delete(s.tunnels, id)
		delete(s.byPort, fmt.Sprintf("%s:%d", t.Proto, t.PublicPort))
	}
	s.mu.Unlock()
	if ok {
		t.close()
		s.udp.dropTunnelSessions(uint16(t.PublicPort))
		s.log.Info("tunnel closed", "id", id)
	}
}

func (s *Server) closeAllTunnels() {
	s.mu.Lock()
	ts := make([]*Tunnel, 0, len(s.tunnels))
	for _, t := range s.tunnels {
		ts = append(ts, t)
	}
	s.tunnels = map[string]*Tunnel{}
	s.byPort = map[string]string{}
	pend := s.pending
	s.pending = map[string]*pendingConn{}
	s.mu.Unlock()

	for _, t := range ts {
		t.close()
		s.udp.dropTunnelSessions(uint16(t.PublicPort))
	}
	for _, p := range pend {
		p.conn.Close()
	}
}

// sendToClient forwards a message over the current control channel, if any.
func (s *Server) sendToClient(msg *Message) bool {
	s.mu.Lock()
	cc := s.control
	s.mu.Unlock()
	if cc == nil {
		return false
	}
	return cc.send(msg) == nil
}

// newConnID returns a random 16-byte hex id for visitor pairing.
func newConnID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// janitor expires pending visitor connections and idle UDP sessions.
func (s *Server) janitor() {
	tick := time.NewTicker(5 * time.Second)
	defer tick.Stop()
	for {
		select {
		case <-s.closed:
			return
		case now := <-tick.C:
			s.mu.Lock()
			for id, p := range s.pending {
				if now.After(p.deadline) {
					p.conn.Close()
					delete(s.pending, id)
					s.log.Debug("pending visitor expired", "conn_id", id)
				}
			}
			s.mu.Unlock()
			s.udp.expireSessions(now)
		}
	}
}
