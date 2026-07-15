namespace GameRelay.Core;

/// <summary>A ready-made port template for a well-known game.</summary>
public sealed record GamePreset(string Name, string Icon, params PresetPort[] Ports);

/// <summary>One port a preset needs. Protocol is "tcp", "udp" or "both".</summary>
public sealed record PresetPort(string Label, string Protocol, int Port);

/// <summary>Built-in presets for popular game servers.</summary>
public static class GamePresets
{
    public static readonly IReadOnlyList<GamePreset> All =
    [
        new("Minecraft Java", "⛏",
            new PresetPort("Game", "tcp", 25565)),
        new("Minecraft Bedrock", "\U0001F9F1",
            new PresetPort("Game", "udp", 19132)),
        new("Palworld", "\U0001F43E",
            new PresetPort("Game", "udp", 8211)),
        new("Terraria", "\U0001F333",
            new PresetPort("Game", "tcp", 7777)),
        new("FiveM (GTA V)", "\U0001F697",
            new PresetPort("Game", "both", 30120)),
        new("Rust", "\U0001F529",
            new PresetPort("Game", "udp", 28015),
            new PresetPort("Rust+ App", "tcp", 28082)),
        new("Valheim", "⚔",
            new PresetPort("Game", "udp", 2456),
            new PresetPort("Query", "udp", 2457)),
        new("Custom (TCP)", "\U0001F527",
            new PresetPort("Service", "tcp", 0)),
        new("Custom (UDP)", "\U0001F527",
            new PresetPort("Service", "udp", 0)),
        new("Custom (TCP+UDP)", "\U0001F527",
            new PresetPort("Service", "both", 0)),
    ];

    /// <summary>
    /// Expands a preset into concrete tunnel configs ("both" becomes one
    /// TCP and one UDP tunnel on the same port).
    /// </summary>
    public static List<TunnelConfig> Expand(GamePreset preset, int? portOverride = null)
    {
        var result = new List<TunnelConfig>();
        for (int i = 0; i < preset.Ports.Length; i++)
        {
            var p = preset.Ports[i];
            // The override replaces the primary (first) port; extra ports
            // like query/companion ports keep their defaults.
            int port = i == 0 && portOverride is > 0 ? portOverride.Value : p.Port;
            foreach (string proto in p.Protocol == "both" ? new[] { "tcp", "udp" } : [p.Protocol])
            {
                result.Add(new TunnelConfig
                {
                    Name = preset.Ports.Length > 1 ? $"{preset.Name} — {p.Label}" : preset.Name,
                    Protocol = proto,
                    PublicPort = port,
                    LocalPort = port,
                });
            }
        }
        return result;
    }
}
