# GameRelay wire protocol (v1)

Two transports between client (Windows) and server (VPS):

1. **TCP** on `control_port` — control channel + per-connection data pipes.
2. **UDP** on `udp_port` — a single "carrier" socket for all UDP tunnels.

Authentication on both is HMAC-SHA256 over the shared secret; the secret
never travels on the wire.

## TCP framing

Every message is one frame: `uint32 big-endian length` + UTF-8 JSON body
(max 64 KiB). After a data connection is paired, framing stops and the
socket becomes a raw byte pipe.

## TCP handshake (both control and data roles)

```
server → client   {"type":"challenge","nonce":"<64 hex chars>"}
client → server   {"type":"auth","mac":hex(HMAC-SHA256(secret, nonce)),
                   "role":"control"|"data","conn_id":"…(data only)",
                   "version":1}
server → client   {"type":"auth_ok","udp_port":7000,"version":1}   (control)
                  {"type":"auth_ok"}                               (data)
        or        {"type":"error","reason":"…"} + close
```

Only one control connection exists at a time; a newly authenticated
control connection **replaces** the previous one (reconnect case).

## Control messages

| Type | Direction | Fields | Meaning |
|---|---|---|---|
| `open_tunnel` | C→S | `tunnel_id`, `proto` (`tcp`/`udp`), `public_port` | bind a public port. Idempotent per `tunnel_id` |
| `tunnel_ok` / `tunnel_fail` | S→C | `tunnel_id`, `reason?` | result |
| `close_tunnel` | C→S | `tunnel_id` | unbind |
| `conn_request` | S→C | `tunnel_id`, `conn_id`, `remote_addr` | a TCP visitor arrived; dial back within 15 s |
| `ping` / `pong` | C→S / S→C | `ts` | heartbeat; client pings every 15 s, server read-timeout 90 s, client declares dead after 45 s without pong |
| `error` | S→C | `reason` | fatal; connection closes |

## TCP tunnel data path

1. Player connects to the public port; server parks the socket as
   *pending* under a random 16-byte `conn_id` (TTL 15 s) and sends
   `conn_request` on the control channel.
2. Client opens a **new** TCP connection to `control_port`, handshakes
   with `role":"data", conn_id`, gets `auth_ok`.
3. Server splices the two sockets (`TCP_NODELAY`, 64 KiB buffers,
   half-close aware). Client meanwhile connects to the local game server
   and pipes bytes both ways, counting traffic.

## UDP carrier

All datagrams start with magic `0xC7` + type byte:

| Type | Layout after magic+type | Meaning |
|---|---|---|
| `0x01` AUTH | `ts uint64 BE` + `HMAC-SHA256(secret, "udp-auth"‖ts) 32B` | registers the client's UDP address; ts must be within ±5 min. Client re-sends every 15 s (doubles as NAT keepalive and heals server restarts) |
| `0x02` AUTH_OK | — | server ack |
| `0x03` KEEPALIVE | — | echoed by the server (optional) |
| `0x04` DATA | `session uint32 BE` + `public_port uint16 BE` + payload | tunneled datagram |

Server side: each UDP tunnel binds its public port. A datagram from a new
player address gets a new `session id`; the payload is forwarded to the
client's registered carrier address with the session header. The client
keeps one local UDP socket per session towards the game server and sends
replies back with the same header; the server routes them to the player
from the tunnel's public socket. Sessions expire after 90 s idle.

## Version negotiation

`version` mismatch in `auth` → server replies `error` and closes. Bump
`ProtocolVersion` (Go) / `ControlMessage.ProtocolVersion` (C#) together.
