package main

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"io"
	"log/slog"
	"net"
	"sync/atomic"
	"testing"
	"time"
)

const testSecret = "test-secret-0123456789abcdef"

// freePort asks the OS for an unused TCP port.
func freePort(t *testing.T) int {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer ln.Close()
	return ln.Addr().(*net.TCPAddr).Port
}

// startTestServer runs a relay on loopback with random ports.
func startTestServer(t *testing.T) *Server {
	t.Helper()
	cfg := &Config{
		ListenAddr:  "127.0.0.1",
		ControlPort: freePort(t),
		UDPPort:     freePort(t),
		Secret:      testSecret,
		LogLevel:    "error",
	}
	if err := cfg.Validate(); err != nil {
		t.Fatal(err)
	}
	log := slog.New(slog.NewTextHandler(io.Discard, nil))
	srv := NewServer(cfg, log)
	go func() { _ = srv.Run() }()
	t.Cleanup(srv.Close)

	// Wait for the control listener to come up.
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		c, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", cfg.ControlPort))
		if err == nil {
			c.Close()
			return srv
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatal("server did not start")
	return nil
}

// testClient is a minimal reference implementation of the client protocol.
type testClient struct {
	t       *testing.T
	srv     *Server
	conn    net.Conn
	udpPort int
	// localTCPPort is where data connections are piped (the fake game server).
	localTCPPort int
	stopped      atomic.Bool
}

// handshake dials the control port and completes challenge/response.
func handshake(t *testing.T, srv *Server, role, connID string) (net.Conn, *Message) {
	t.Helper()
	conn, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", srv.cfg.ControlPort))
	if err != nil {
		t.Fatal(err)
	}
	ch, err := ReadFrame(conn)
	if err != nil || ch.Type != MsgChallenge {
		t.Fatalf("expected challenge, got %v err=%v", ch, err)
	}
	err = WriteFrame(conn, &Message{
		Type: MsgAuth, MAC: computeMAC(testSecret, ch.Nonce),
		Role: role, ConnID: connID, Version: ProtocolVersion,
	})
	if err != nil {
		t.Fatal(err)
	}
	resp, err := ReadFrame(conn)
	if err != nil {
		t.Fatal(err)
	}
	return conn, resp
}

// connectControl authenticates a control channel and starts the read pump.
func connectControl(t *testing.T, srv *Server, localTCPPort int) *testClient {
	t.Helper()
	conn, resp := handshake(t, srv, "control", "")
	if resp.Type != MsgAuthOK {
		t.Fatalf("expected auth_ok, got %+v", resp)
	}
	tc := &testClient{t: t, srv: srv, conn: conn, udpPort: resp.UDPPort, localTCPPort: localTCPPort}
	t.Cleanup(func() { tc.stopped.Store(true); conn.Close() })
	return tc
}

// openTunnel sends open_tunnel and waits for the reply.
func (tc *testClient) openTunnel(id, proto string, publicPort int) *Message {
	tc.t.Helper()
	if err := WriteFrame(tc.conn, &Message{Type: MsgOpenTunnel, TunnelID: id, Proto: proto, PublicPort: publicPort}); err != nil {
		tc.t.Fatal(err)
	}
	for {
		msg, err := ReadFrame(tc.conn)
		if err != nil {
			tc.t.Fatal(err)
		}
		if msg.Type == MsgTunnelOK || msg.Type == MsgTunnelFail {
			return msg
		}
	}
}

// pump answers conn_requests by dialing the local fake game server.
func (tc *testClient) pump() {
	for {
		msg, err := ReadFrame(tc.conn)
		if err != nil {
			return
		}
		switch msg.Type {
		case MsgConnRequest:
			go tc.serveData(msg.ConnID)
		case MsgPing:
			_ = WriteFrame(tc.conn, &Message{Type: MsgPong, TS: msg.TS})
		}
	}
}

func (tc *testClient) serveData(connID string) {
	dataConn, resp := handshake(tc.t, tc.srv, "data", connID)
	if resp.Type != MsgAuthOK {
		if !tc.stopped.Load() {
			tc.t.Errorf("data handshake failed: %+v", resp)
		}
		return
	}
	local, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", tc.localTCPPort))
	if err != nil {
		dataConn.Close()
		return
	}
	pipe(dataConn, local)
}

// startTCPEcho runs a TCP echo server on a random port.
func startTCPEcho(t *testing.T) int {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { ln.Close() })
	go func() {
		for {
			c, err := ln.Accept()
			if err != nil {
				return
			}
			go func() { _, _ = io.Copy(c, c); c.Close() }()
		}
	}()
	return ln.Addr().(*net.TCPAddr).Port
}

// startUDPEcho runs a UDP echo server on a random port.
func startUDPEcho(t *testing.T) int {
	t.Helper()
	sock, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { sock.Close() })
	go func() {
		buf := make([]byte, 65535)
		for {
			n, from, err := sock.ReadFromUDP(buf)
			if err != nil {
				return
			}
			_, _ = sock.WriteToUDP(buf[:n], from)
		}
	}()
	return sock.LocalAddr().(*net.UDPAddr).Port
}

func TestAuthRejectsBadSecret(t *testing.T) {
	srv := startTestServer(t)
	conn, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", srv.cfg.ControlPort))
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	ch, err := ReadFrame(conn)
	if err != nil {
		t.Fatal(err)
	}
	_ = WriteFrame(conn, &Message{
		Type: MsgAuth, MAC: computeMAC("wrong-secret-wrong-secret", ch.Nonce),
		Role: "control", Version: ProtocolVersion,
	})
	resp, err := ReadFrame(conn)
	if err != nil {
		t.Fatal(err)
	}
	if resp.Type != MsgError {
		t.Fatalf("expected error frame, got %+v", resp)
	}
}

func TestTCPTunnelEndToEnd(t *testing.T) {
	srv := startTestServer(t)
	echoPort := startTCPEcho(t)
	tc := connectControl(t, srv, echoPort)

	publicPort := freePort(t)
	if resp := tc.openTunnel("tun-tcp-1", "tcp", publicPort); resp.Type != MsgTunnelOK {
		t.Fatalf("open tunnel failed: %+v", resp)
	}
	go tc.pump()

	visitor, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", publicPort))
	if err != nil {
		t.Fatal(err)
	}
	defer visitor.Close()

	payload := []byte("hello through the relay tunnel!")
	if _, err := visitor.Write(payload); err != nil {
		t.Fatal(err)
	}
	got := make([]byte, len(payload))
	_ = visitor.SetReadDeadline(time.Now().Add(5 * time.Second))
	if _, err := io.ReadFull(visitor, got); err != nil {
		t.Fatalf("read echo: %v", err)
	}
	if !bytes.Equal(got, payload) {
		t.Fatalf("echo mismatch: %q != %q", got, payload)
	}

	// Second concurrent visitor must work too.
	v2, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", publicPort))
	if err != nil {
		t.Fatal(err)
	}
	defer v2.Close()
	if _, err := v2.Write([]byte("second")); err != nil {
		t.Fatal(err)
	}
	got2 := make([]byte, 6)
	_ = v2.SetReadDeadline(time.Now().Add(5 * time.Second))
	if _, err := io.ReadFull(v2, got2); err != nil {
		t.Fatalf("second visitor: %v", err)
	}
}

func TestUDPTunnelEndToEnd(t *testing.T) {
	srv := startTestServer(t)
	echoPort := startUDPEcho(t)
	// Note: no pump() here — UDP tunnels never receive conn_request, and
	// openTunnel reads the control stream directly.
	tc := connectControl(t, srv, 0)

	// Register the UDP carrier.
	carrier, err := net.DialUDP("udp", nil,
		&net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: tc.udpPort})
	if err != nil {
		t.Fatal(err)
	}
	defer carrier.Close()

	ts := time.Now().Unix()
	auth := make([]byte, 2+8+32)
	auth[0], auth[1] = udpMagic, udpTypeAuth
	binary.BigEndian.PutUint64(auth[2:10], uint64(ts))
	copy(auth[10:], udpAuthMAC(testSecret, ts))
	if _, err := carrier.Write(auth); err != nil {
		t.Fatal(err)
	}
	ack := make([]byte, 16)
	_ = carrier.SetReadDeadline(time.Now().Add(3 * time.Second))
	n, err := carrier.Read(ack)
	if err != nil || n != 2 || ack[0] != udpMagic || ack[1] != udpTypeAuthOK {
		t.Fatalf("carrier auth failed: n=%d err=%v", n, err)
	}

	publicPort := freePort(t)
	if resp := tc.openTunnel("tun-udp-1", "udp", publicPort); resp.Type != MsgTunnelOK {
		t.Fatalf("open tunnel failed: %+v", resp)
	}

	// Client side: relay carrier DATA frames to the local UDP echo server.
	go func() {
		locals := map[uint32]*net.UDPConn{}
		buf := make([]byte, 65535)
		for {
			_ = carrier.SetReadDeadline(time.Now().Add(10 * time.Second))
			n, err := carrier.Read(buf)
			if err != nil {
				return
			}
			if n < udpHeaderLen || buf[0] != udpMagic || buf[1] != udpTypeData {
				continue
			}
			sid := binary.BigEndian.Uint32(buf[2:6])
			local, ok := locals[sid]
			if !ok {
				local, err = net.DialUDP("udp", nil,
					&net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: echoPort})
				if err != nil {
					return
				}
				locals[sid] = local
				// Pump echo replies back into the carrier for this session.
				go func(sid uint32, local *net.UDPConn) {
					rbuf := make([]byte, 65535)
					head := make([]byte, udpHeaderLen)
					head[0], head[1] = udpMagic, udpTypeData
					binary.BigEndian.PutUint32(head[2:6], sid)
					binary.BigEndian.PutUint16(head[6:8], uint16(publicPort))
					for {
						_ = local.SetReadDeadline(time.Now().Add(10 * time.Second))
						rn, err := local.Read(rbuf)
						if err != nil {
							return
						}
						_, _ = carrier.Write(append(append([]byte{}, head...), rbuf[:rn]...))
					}
				}(sid, local)
			}
			_, _ = local.Write(buf[udpHeaderLen:n])
		}
	}()

	// Visitor sends a datagram to the public UDP port and expects the echo.
	visitor, err := net.DialUDP("udp", nil,
		&net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: publicPort})
	if err != nil {
		t.Fatal(err)
	}
	defer visitor.Close()

	payload := []byte("udp ping through relay")
	got := make([]byte, 1024)
	deadline := time.Now().Add(5 * time.Second)
	for {
		if time.Now().After(deadline) {
			t.Fatal("no udp echo received")
		}
		if _, err := visitor.Write(payload); err != nil {
			t.Fatal(err)
		}
		_ = visitor.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
		n, err := visitor.Read(got)
		if err == nil {
			if !bytes.Equal(got[:n], payload) {
				t.Fatalf("udp echo mismatch: %q", got[:n])
			}
			return
		}
	}
}

func TestReconnectReplacesControl(t *testing.T) {
	srv := startTestServer(t)
	echoPort := startTCPEcho(t)

	tc1 := connectControl(t, srv, echoPort)
	publicPort := freePort(t)
	if resp := tc1.openTunnel("tun-1", "tcp", publicPort); resp.Type != MsgTunnelOK {
		t.Fatalf("open tunnel: %+v", resp)
	}

	// Second control connection replaces the first (simulated reconnect).
	tc2 := connectControl(t, srv, echoPort)
	// Re-opening the same tunnel id must be idempotent.
	if resp := tc2.openTunnel("tun-1", "tcp", publicPort); resp.Type != MsgTunnelOK {
		t.Fatalf("re-open tunnel: %+v", resp)
	}
	go tc2.pump()

	visitor, err := net.Dial("tcp", fmt.Sprintf("127.0.0.1:%d", publicPort))
	if err != nil {
		t.Fatal(err)
	}
	defer visitor.Close()
	if _, err := visitor.Write([]byte("after reconnect")); err != nil {
		t.Fatal(err)
	}
	got := make([]byte, 15)
	_ = visitor.SetReadDeadline(time.Now().Add(5 * time.Second))
	if _, err := io.ReadFull(visitor, got); err != nil {
		t.Fatalf("tunnel dead after reconnect: %v", err)
	}
}

func TestPortNotAllowed(t *testing.T) {
	srv := startTestServer(t)
	srv.cfg.AllowedPorts = []PortRange{{From: 30000, To: 31000}}
	tc := connectControl(t, srv, 0)
	if resp := tc.openTunnel("tun-x", "tcp", 25565); resp.Type != MsgTunnelFail {
		t.Fatalf("expected tunnel_fail for disallowed port, got %+v", resp)
	}
}
