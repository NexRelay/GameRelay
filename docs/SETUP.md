# Setup guide — Oracle Cloud VPS + Windows client

This walks through a complete first-time deployment. Time: ~15 minutes.

## 1. VPS: install the relay server

Works on any Linux VPS; the steps below assume Ubuntu on Oracle Cloud
(both x86 `VM.Standard.E2` and ARM `VM.Standard.A1` free-tier shapes work —
use the matching binary).

```bash
# from this repo, on your PC:
scp dist/gamerelay-server-linux-amd64 \
    dist/gamerelay-server-linux-arm64 \
    server/deploy/install.sh server/deploy/gamerelay.service \
    ubuntu@YOUR_VPS_IP:~/gamerelay/

ssh ubuntu@YOUR_VPS_IP
cd ~/gamerelay
sudo bash install.sh
```

The installer:

- creates a `gamerelay` system user and installs to `/opt/gamerelay`,
- generates a **random shared secret** and writes `/opt/gamerelay/config.json`,
- enables the `gamerelay` systemd service (auto-start on boot, restart on crash),
- prints the secret at the end — **copy it**, you'll paste it into the
  Windows app.

Useful commands:

```bash
sudo systemctl status gamerelay        # is it running?
sudo journalctl -u gamerelay -f        # live logs
sudo nano /opt/gamerelay/config.json   # change settings
sudo systemctl restart gamerelay       # apply changes
```

### Server config reference (`/opt/gamerelay/config.json`)

```json
{
  "listen_addr": "0.0.0.0",
  "control_port": 7000,
  "udp_port": 7000,
  "secret": "…",
  "allowed_ports": [ { "from": 1024, "to": 65535 } ],
  "log_level": "info"
}
```

| Key | Meaning |
|---|---|
| `control_port` | TCP port the Windows client connects to (control + data) |
| `udp_port` | UDP port used as the carrier for all UDP tunnels |
| `secret` | shared secret, min 16 chars. Env `GAMERELAY_SECRET` overrides |
| `allowed_ports` | which public ports tunnels may claim |
| `log_level` | `debug`, `info`, `warn`, `error` |

## 2. Oracle Cloud: open the firewall (two layers!)

Oracle blocks everything by default in **two** places. You must open both.

### a) VCN Security List (cloud console)

Networking → Virtual Cloud Networks → your VCN → Security Lists → Default
→ **Add Ingress Rules**:

| Source CIDR | Protocol | Dest. port | Purpose |
|---|---|---|---|
| 0.0.0.0/0 | TCP | 7000 | relay control/data |
| 0.0.0.0/0 | UDP | 7000 | relay UDP carrier |
| 0.0.0.0/0 | TCP | 25565 (etc.) | each TCP game port you tunnel |
| 0.0.0.0/0 | UDP | 19132 (etc.) | each UDP game port you tunnel |

(Or open a range like 25000–31000 once and stay inside it when adding
tunnels; tighten `allowed_ports` in the server config to match.)

### b) The instance's own firewall

Ubuntu images on Oracle ship iptables rules that reject traffic:

```bash
# example: relay ports + Minecraft Java + Bedrock
sudo iptables -I INPUT -p tcp --dport 7000  -j ACCEPT
sudo iptables -I INPUT -p udp --dport 7000  -j ACCEPT
sudo iptables -I INPUT -p tcp --dport 25565 -j ACCEPT
sudo iptables -I INPUT -p udp --dport 19132 -j ACCEPT
sudo netfilter-persistent save     # persist across reboots
```

If `netfilter-persistent` is missing: `sudo apt install iptables-persistent`.

## 3. Windows: install and configure the client

1. Build once: `powershell -File scripts\build-client.ps1`
   → output in `dist\GameRelay-win-x64\GameRelay.exe`
   (self-contained: no .NET install needed). Copy the folder anywhere.
2. Start `GameRelay.exe` → the ⚙ settings dialog:
   - **Relay server address** — your VPS public IP (or a DNS name).
   - **Control port** — `7000` unless you changed it.
   - **Shared secret** — the one `install.sh` printed.
3. Click **Add tunnel**, pick a game preset (see
   [GAMES.md](GAMES.md)), adjust ports if your local server uses
   non-default ones, and save.

The status dot turns green when connected; each tunnel shows Active plus
live traffic counters. Config is stored at `%APPDATA%\GameRelay\config.json`.

## 4. Point players at the VPS

Players connect to `YOUR_VPS_IP:PUBLIC_PORT` — e.g. Minecraft Java
`140.10.20.30:25565`. Optionally put a DNS A record on the IP and give
players a hostname instead.

## Troubleshooting

| Symptom | Fix |
|---|---|
| App stuck on “Connecting…” | Wrong IP/port, or TCP 7000 blocked (check both firewall layers); `sudo journalctl -u gamerelay -f` shows nothing → traffic never arrives |
| “authentication failed” in log | Secret mismatch — re-copy it from `/opt/gamerelay/config.json` |
| Tunnel shows “port … not allowed” | Public port outside `allowed_ports`, or it collides with `control_port` |
| Tunnel Active but players can't join | Game port not opened in **both** Oracle layers; or local game server not running / wrong local port |
| UDP game unreachable, TCP fine | UDP 7000 (carrier) not open, or the game's UDP port not open |
| Laggy | Pick a VPS region close to you and your players; check `ping VPS_IP` |
