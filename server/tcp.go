package main

import (
	"fmt"
	"io"
	"net"
	"sync"
	"time"
)

// Tunnel is one exposed public port (TCP listener or UDP socket).
type Tunnel struct {
	ID         string
	Proto      string
	PublicPort int

	listener net.Listener // tcp tunnels
	udpSock  *net.UDPConn // udp tunnels

	closeOnce sync.Once
	done      chan struct{}
}

func (t *Tunnel) close() {
	t.closeOnce.Do(func() {
		close(t.done)
		if t.listener != nil {
			t.listener.Close()
		}
		if t.udpSock != nil {
			t.udpSock.Close()
		}
	})
}

// startTunnel binds the public port and starts serving visitors.
func (s *Server) startTunnel(id, proto string, publicPort int) (*Tunnel, error) {
	t := &Tunnel{ID: id, Proto: proto, PublicPort: publicPort, done: make(chan struct{})}
	addr := fmt.Sprintf("%s:%d", s.cfg.ListenAddr, publicPort)

	switch proto {
	case "tcp":
		ln, err := net.Listen("tcp", addr)
		if err != nil {
			return nil, fmt.Errorf("bind public port: %w", err)
		}
		t.listener = ln
		go s.acceptVisitors(t)
	case "udp":
		uaddr, err := net.ResolveUDPAddr("udp", addr)
		if err != nil {
			return nil, err
		}
		sock, err := net.ListenUDP("udp", uaddr)
		if err != nil {
			return nil, fmt.Errorf("bind public port: %w", err)
		}
		t.udpSock = sock
		go s.udp.servePublicSocket(t)
	}
	return t, nil
}

// acceptVisitors handles inbound player connections on a public TCP port.
// Each visitor is parked as pending and the client is asked (over the
// control channel) to dial back a data connection carrying the conn id.
func (s *Server) acceptVisitors(t *Tunnel) {
	for {
		conn, err := t.listener.Accept()
		if err != nil {
			return // listener closed
		}
		setNoDelay(conn)

		connID := newConnID()
		s.mu.Lock()
		s.pending[connID] = &pendingConn{
			conn:     conn,
			tunnelID: t.ID,
			deadline: time.Now().Add(pendingConnTTL),
		}
		s.mu.Unlock()

		ok := s.sendToClient(&Message{
			Type:       MsgConnRequest,
			TunnelID:   t.ID,
			ConnID:     connID,
			RemoteAddr: conn.RemoteAddr().String(),
		})
		if !ok {
			s.mu.Lock()
			delete(s.pending, connID)
			s.mu.Unlock()
			conn.Close()
			continue
		}
		s.log.Debug("visitor waiting for client",
			"tunnel", t.ID, "conn_id", connID, "remote", conn.RemoteAddr().String())
	}
}

// pairData joins an authenticated client data connection with its waiting
// visitor and splices the two together.
func (s *Server) pairData(clientConn net.Conn, connID string) {
	s.mu.Lock()
	p, ok := s.pending[connID]
	if ok {
		delete(s.pending, connID)
	}
	s.mu.Unlock()

	if !ok {
		_ = WriteFrame(clientConn, &Message{Type: MsgError, Reason: "unknown or expired conn_id"})
		clientConn.Close()
		return
	}
	if err := WriteFrame(clientConn, &Message{Type: MsgAuthOK}); err != nil {
		clientConn.Close()
		p.conn.Close()
		return
	}

	s.log.Debug("visitor paired", "conn_id", connID)
	pipe(clientConn, p.conn)
}

// pipe copies bytes in both directions until either side closes.
func pipe(a, b net.Conn) {
	var wg sync.WaitGroup
	wg.Add(2)
	copyHalf := func(dst, src net.Conn) {
		defer wg.Done()
		buf := make([]byte, 64*1024)
		_, _ = io.CopyBuffer(dst, src, buf)
		// Half-close where supported so the other direction can drain.
		if tc, ok := dst.(*net.TCPConn); ok {
			_ = tc.CloseWrite()
		} else {
			dst.Close()
		}
	}
	go copyHalf(a, b)
	go copyHalf(b, a)
	wg.Wait()
	a.Close()
	b.Close()
}
