#!/usr/bin/env bash
# GameRelay server installer for a fresh Ubuntu/Debian VPS (run as root).
#
# Usage:
#   1. Copy the right binary next to this script as `gamerelay-server`
#      (gamerelay-server-linux-amd64 or -arm64, see ../dist/).
#   2. sudo bash install.sh
#
# The script installs to /opt/gamerelay, creates a service user, generates
# a random shared secret, writes the config, and enables the systemd unit.

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "run as root: sudo bash install.sh" >&2
    exit 1
fi

DIR="$(cd "$(dirname "$0")" && pwd)"
BIN="$DIR/gamerelay-server"
if [[ ! -f "$BIN" ]]; then
    # Fall back to an arch-suffixed binary lying next to the script.
    ARCH=$(uname -m)
    case "$ARCH" in
        x86_64)  BIN="$DIR/gamerelay-server-linux-amd64" ;;
        aarch64) BIN="$DIR/gamerelay-server-linux-arm64" ;;
    esac
fi
if [[ ! -f "$BIN" ]]; then
    echo "gamerelay-server binary not found next to install.sh" >&2
    exit 1
fi

echo "==> installing to /opt/gamerelay"
id -u gamerelay &>/dev/null || useradd --system --no-create-home --shell /usr/sbin/nologin gamerelay
mkdir -p /opt/gamerelay
install -m 0755 "$BIN" /opt/gamerelay/gamerelay-server

if [[ ! -f /opt/gamerelay/config.json ]]; then
    SECRET=$(head -c 32 /dev/urandom | sha256sum | cut -d' ' -f1)
    cat > /opt/gamerelay/config.json <<EOF
{
  "listen_addr": "0.0.0.0",
  "control_port": 7000,
  "udp_port": 7000,
  "secret": "$SECRET",
  "allowed_ports": [ { "from": 1024, "to": 65535 } ],
  "log_level": "info"
}
EOF
    chmod 0600 /opt/gamerelay/config.json
    echo "==> generated config with a random secret"
else
    echo "==> keeping existing /opt/gamerelay/config.json"
fi
chown -R gamerelay:gamerelay /opt/gamerelay

install -m 0644 "$DIR/gamerelay.service" /etc/systemd/system/gamerelay.service
systemctl daemon-reload
systemctl enable --now gamerelay

echo
echo "==> done. status:"
systemctl --no-pager --lines 5 status gamerelay || true
echo
echo "Shared secret (enter this in the Windows app):"
grep -o '"secret": "[^"]*"' /opt/gamerelay/config.json | cut -d'"' -f4
echo
echo "Remember to open the firewall (see docs/SETUP.md):"
echo "  - TCP 7000 (control + game TCP tunnels' ports)"
echo "  - UDP 7000 (carrier + game UDP tunnels' ports)"
