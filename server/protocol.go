package main

// Wire protocol shared between the relay server and the Windows client.
//
// Control / data channel (TCP):
//   Every frame is a 4-byte big-endian length followed by a JSON object.
//   The connection starts with a challenge/response HMAC handshake, after
//   which the connection either becomes the control channel (role=control)
//   or a raw data pipe for one visitor connection (role=data).
//
// UDP carrier (single UDP socket on the client side):
//   Every datagram starts with [magic 0xC7][type byte].
//   DATA frames add [session id uint32 BE][public port uint16 BE][payload].

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"time"
)

const (
	// ProtocolVersion is bumped on any incompatible wire change.
	ProtocolVersion = 1

	// MaxFrameSize bounds a single control frame. Control messages are
	// small; anything larger indicates a broken or malicious peer.
	MaxFrameSize = 64 * 1024

	// UDP carrier datagram types.
	udpMagic        byte = 0xC7
	udpTypeAuth     byte = 0x01
	udpTypeAuthOK   byte = 0x02
	udpTypeKeepAliv byte = 0x03
	udpTypeData     byte = 0x04

	// udpHeaderLen is the DATA frame header: magic+type+session+port.
	udpHeaderLen = 1 + 1 + 4 + 2

	// udpAuthWindow is how far a carrier AUTH timestamp may drift.
	udpAuthWindow = 5 * time.Minute
)

// Message is the single JSON envelope for every control-channel frame.
// Only the fields relevant to each Type are populated.
type Message struct {
	Type string `json:"type"`

	// challenge / auth
	Nonce   string `json:"nonce,omitempty"`
	MAC     string `json:"mac,omitempty"`
	Role    string `json:"role,omitempty"`    // "control" or "data"
	ConnID  string `json:"conn_id,omitempty"` // for role=data
	Version int    `json:"version,omitempty"`

	// auth_ok
	UDPPort int `json:"udp_port,omitempty"`

	// tunnels
	TunnelID   string `json:"tunnel_id,omitempty"`
	Proto      string `json:"proto,omitempty"` // "tcp" or "udp"
	PublicPort int    `json:"public_port,omitempty"`
	Reason     string `json:"reason,omitempty"`

	// conn_request
	RemoteAddr string `json:"remote_addr,omitempty"`

	// ping / pong
	TS int64 `json:"ts,omitempty"`
}

// Control message types.
const (
	MsgChallenge   = "challenge"
	MsgAuth        = "auth"
	MsgAuthOK      = "auth_ok"
	MsgError       = "error"
	MsgOpenTunnel  = "open_tunnel"
	MsgCloseTunnel = "close_tunnel"
	MsgTunnelOK    = "tunnel_ok"
	MsgTunnelFail  = "tunnel_fail"
	MsgConnRequest = "conn_request"
	MsgPing        = "ping"
	MsgPong        = "pong"
)

// WriteFrame serialises msg and writes one length-prefixed frame.
func WriteFrame(w io.Writer, msg *Message) error {
	body, err := json.Marshal(msg)
	if err != nil {
		return err
	}
	if len(body) > MaxFrameSize {
		return fmt.Errorf("frame too large: %d bytes", len(body))
	}
	buf := make([]byte, 4+len(body))
	binary.BigEndian.PutUint32(buf[:4], uint32(len(body)))
	copy(buf[4:], body)
	_, err = w.Write(buf)
	return err
}

// ReadFrame reads one length-prefixed JSON frame.
func ReadFrame(r io.Reader) (*Message, error) {
	var head [4]byte
	if _, err := io.ReadFull(r, head[:]); err != nil {
		return nil, err
	}
	n := binary.BigEndian.Uint32(head[:])
	if n == 0 || n > MaxFrameSize {
		return nil, fmt.Errorf("invalid frame size %d", n)
	}
	body := make([]byte, n)
	if _, err := io.ReadFull(r, body); err != nil {
		return nil, err
	}
	var msg Message
	if err := json.Unmarshal(body, &msg); err != nil {
		return nil, err
	}
	return &msg, nil
}

// setNoDelay disables Nagle's algorithm where possible to keep latency low.
func setNoDelay(c net.Conn) {
	if tc, ok := c.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}
}
