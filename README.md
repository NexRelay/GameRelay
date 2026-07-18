# GameRelay

**Host game servers from home — no port forwarding.**

GameRelay is a lightweight, self-hosted relay that exposes game servers running
on your own machine to the internet through a tiny relay you run on a cloud VPS.
Your home PC connects *outward* to the relay, so **no ports are ever opened on
your router** — it works behind CGNAT, mobile hotspots and strict ISPs, and your
home IP stays hidden. Think of it as your own private, open-source alternative to
hosted tunnel services: no accounts, no subscriptions, no limits.

**العربية: [README.ar.md](README.ar.md)**

```
   players  ───────────▶   your VPS relay   ───────────▶   your PC at home
     join IP:port          (public IP,               (game server on
                            Go, ~3 MB)                 127.0.0.1:25565…)

   the PC dials OUT to the relay ─ no router ports, home IP hidden
```

## Features

- **TCP + UDP tunnels** — Minecraft Java & Bedrock, Palworld, Terraria, FiveM,
  Rust, Valheim, or any generic TCP/UDP service, with built-in presets.
- **One-click server setup** — the Windows app can install and configure the
  relay on your VPS for you over SSH: it uploads the binary, runs the installer,
  opens the host firewall and fills in your secret automatically. A guided manual
  path is included too.
- **Windows GUI + Linux CLI** — a polished WinUI 3 desktop app and a
  single-binary Linux client (x64 + ARM64), sharing the same protocol, presets
  and config format.
- **No port forwarding** — the home machine connects out to the VPS; players
  connect to the VPS's public IP.
- **Auto-reconnect** — exponential backoff, heartbeat watchdog, tunnels re-open
  by themselves after an outage; active player connections survive brief
  control-channel blips.
- **Low latency** — TCP_NODELAY everywhere, zero-copy UDP forwarding, one extra
  hop only (player → VPS → you).
- **Cryptographic auth** — challenge/response HMAC-SHA256 over a shared secret;
  the secret itself never crosses the wire, and the relay accepts exactly one
  client: yours.
- **Quality-of-life** — auto-detect game servers running locally, a share button
  that copies the exact address players type, live per-tunnel traffic/latency,
  redundant settings storage with auto-restore, and single-instance protection.
- **Tiny, hardened server** — a single static ~3 MB Go binary (stdlib only) that
  uses under 10 MB of RAM and runs as a locked-down systemd service.

## Repository layout

| Path | What it is |
|---|---|
| `server/` | Go relay server (stdlib only) + integration tests |
| `server/deploy/` | `install.sh` + systemd unit for the VPS |
| `client/GameRelay.Core/` | .NET 9 tunnel engine — protocol, reconnect, UDP carrier, SSH provisioner, game detector |
| `client/GameRelay.App/` | WinUI 3 desktop app (Windows) |
| `client/GameRelay.Cli/` | Cross-platform CLI client (Linux, macOS, Windows) |
| `client/GameRelay.SmokeTest/` | End-to-end test harness (real client vs real server) |
| `client/GameRelay.ProvisionTest/` | Headless test for the SSH provisioner and game detector |
| `scripts/` | Build scripts (`build-server.ps1`, `build-client.ps1`) |
| `docs/` | Setup guide, protocol spec, game port reference |

## How it works

GameRelay has two halves:

- **The relay server** — a single Go binary on a cloud VPS. It owns the public
  IP and ports that players connect to.
- **The client** — runs on the machine hosting your game servers. It connects
  *outward* to the relay and forwards traffic to your local game servers.

A public TCP visitor is parked on the relay and paired with a fresh outbound data
connection dialed back by the client. UDP is multiplexed over a single carrier
socket with per-player sessions. See [docs/PROTOCOL.md](docs/PROTOCOL.md) for the
full wire protocol.

## Quick start

You need a Linux VPS — the **Oracle Cloud Always Free tier works and costs $0
forever**. The full walkthrough (creating the VM, the network, and the firewall)
is in **[docs/SETUP.md](docs/SETUP.md)**.

### Easiest: let the Windows app do it (recommended)

1. Build or grab the Windows app and run it.
2. Click **Set up server**, choose **Automatic**, enter the VPS IP and browse to
   your SSH key. Use the per-step **Test** buttons to verify each stage as you go.
   The app installs the relay, opens the host firewall and fills in the secret for
   you.
3. Open the cloud provider's firewall — the wizard generates a ready-to-paste
   Oracle Cloud Shell command that does it in one step, or shows the manual rules.
   Then **Add tunnel** / **Scan for games**. Players join `your-vps-ip:port`.

### Manual (any platform)

On the VPS (Ubuntu, as root):

```bash
scp dist/gamerelay-server-linux-* server/deploy/* user@your-vps:~/gamerelay/
ssh user@your-vps
cd gamerelay && sudo bash install.sh     # prints the generated secret
```

Then point a client at it — the Windows app (Settings → server address + secret),
or the Linux CLI:

```bash
sudo bash install.sh                     # from the Linux bundle
gamerelay setup                          # server address, port, secret
gamerelay preset "minecraft java"
gamerelay run
```

## Build from source

Requirements: [Go](https://go.dev/dl/) 1.26+ and the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```powershell
# Relay server binaries (linux amd64/arm64, windows) — runs vet + tests
scripts\build-server.ps1

# Windows app (self-contained, no .NET needed on the target)
scripts\build-client.ps1

# Linux CLI
dotnet publish client/GameRelay.Cli -c Release -r linux-x64   --self-contained
dotnet publish client/GameRelay.Cli -c Release -r linux-arm64 --self-contained
```

## Tests

```bash
# Go: auth, TCP end-to-end, UDP end-to-end, reconnect/replacement, port policy
cd server && go test ./...

# .NET end-to-end: TCP echo, 1 MiB round-trip, UDP echo, counters, stop/start,
# and a kill-the-server reconnect drill (needs a running relay)
dotnet run --project client/GameRelay.SmokeTest -- 127.0.0.1 7000 <secret>
```

## Security notes

- The relay accepts exactly **one** authenticated client. A new login replaces
  the old session; nobody else can use your relay.
- The handshake is HMAC-SHA256 challenge/response — the shared secret never
  crosses the wire, and a captured handshake can't be replayed (fresh nonce per
  connection).
- Tunnel traffic itself is **not** encrypted by the relay (games do their own
  encryption). Don't tunnel plaintext admin panels.
- The SSH setup wizard uses your key locally to install the server; your key file
  never leaves your PC.
- Keep `config.json` at mode `0600` (the installer does this) and treat the
  secret like a password. Rotate it by editing both sides.

## License

Released under the [MIT License](LICENSE).
