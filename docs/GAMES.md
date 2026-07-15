# Game port reference

The app's **Add tunnel** presets create these automatically; this table is
for manual setups and sanity checks. "Public port" can be anything you
like — it's what players type; "local port" must match your game server.

| Game | Protocol | Default port(s) | Notes |
|---|---|---|---|
| Minecraft Java | TCP | 25565 | one TCP tunnel is all you need |
| Minecraft Bedrock | UDP | 19132 | phones/consoles need the default port to appear in LAN list; friends can join any port via Add Server |
| Palworld | UDP | 8211 | community servers also use 8212 (REST) — usually not needed |
| Terraria | TCP | 7777 | |
| FiveM (GTA V) | TCP **+** UDP | 30120 | needs BOTH protocols on the same port (preset creates two tunnels) |
| Rust | UDP | 28015 | +28082/TCP for the Rust+ companion app (preset includes it) |
| Valheim | UDP | 2456 + 2457 | 2456 game, 2457 query; both UDP (preset includes both) |
| Generic TCP | TCP | — | web servers, SSH, anything TCP |
| Generic UDP | UDP | — | voice servers, other games |
| Generic TCP+UDP | both | — | when you're not sure what the game uses |

## Tips

- **Same port both sides** is the least confusing setup (public 25565 →
  local 25565), but any mapping works, e.g. public 40000 → local 25565.
- Every public port must be inside the server's `allowed_ports` ranges
  and opened in the Oracle firewall (both layers — see
  [SETUP.md](SETUP.md#2-oracle-cloud-open-the-firewall-two-layers)).
- Multiple tunnels can run at the same time; each public port can only be
  used by one tunnel of the same protocol.
- The relay adds one hop: player → VPS → your PC. Latency ≈ player→VPS
  ping + VPS→you ping. Choose the VPS region accordingly.
