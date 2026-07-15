package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
)

// newNonce returns 32 random bytes hex-encoded for the TCP handshake.
func newNonce() (string, error) {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return hex.EncodeToString(b), nil
}

// computeMAC returns hex(HMAC-SHA256(secret, message)).
func computeMAC(secret, message string) string {
	m := hmac.New(sha256.New, []byte(secret))
	m.Write([]byte(message))
	return hex.EncodeToString(m.Sum(nil))
}

// verifyMAC compares in constant time.
func verifyMAC(secret, message, gotHex string) bool {
	want := computeMAC(secret, message)
	got, err := hex.DecodeString(gotHex)
	if err != nil {
		return false
	}
	wantRaw, _ := hex.DecodeString(want)
	return hmac.Equal(wantRaw, got)
}

// udpAuthMAC computes the MAC used inside a UDP carrier AUTH datagram:
// HMAC-SHA256(secret, "udp-auth" || ts_be_bytes).
func udpAuthMAC(secret string, ts int64) []byte {
	var tsb [8]byte
	binary.BigEndian.PutUint64(tsb[:], uint64(ts))
	m := hmac.New(sha256.New, []byte(secret))
	m.Write([]byte("udp-auth"))
	m.Write(tsb[:])
	return m.Sum(nil)
}
