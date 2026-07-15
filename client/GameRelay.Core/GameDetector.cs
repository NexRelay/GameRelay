using System.Net;
using System.Net.NetworkInformation;

namespace GameRelay.Core;

/// <summary>A game detected listening locally.</summary>
public sealed record DetectedGame(string Name, string Protocol, int Port);

/// <summary>
/// Scans the machine's active TCP/UDP listeners for known game servers,
/// including Minecraft Java on non-default ports (25565–25599), and offers
/// to tunnel them. Only recognised games are returned — generic system
/// services are ignored to keep the list clean.
/// </summary>
public static class GameDetector
{
    private static readonly Dictionary<(int port, string proto), string> Known = BuildKnown();

    private static Dictionary<(int, string), string> BuildKnown()
    {
        var map = new Dictionary<(int, string), string>();
        foreach (var preset in GamePresets.All)
        {
            if (preset.Name.StartsWith("Custom")) continue;
            foreach (var p in preset.Ports)
            {
                if (p.Port <= 0) continue;
                foreach (var proto in p.Protocol == "both" ? new[] { "tcp", "udp" } : [p.Protocol])
                    map[(p.Port, proto)] = preset.Name;
            }
        }
        return map;
    }

    /// <summary>
    /// Returns known game servers listening locally that aren't already
    /// covered by an existing tunnel.
    /// </summary>
    public static IReadOnlyList<DetectedGame> Detect(IEnumerable<TunnelConfig> existing)
    {
        var taken = new HashSet<(int, string)>();
        foreach (var t in existing)
        {
            taken.Add((t.LocalPort, t.Protocol));
            taken.Add((t.PublicPort, t.Protocol));
        }

        var seen = new HashSet<(int, string)>();
        var results = new List<DetectedGame>();

        void Consider(IPEndPoint ep, string proto)
        {
            int port = ep.Port;
            if (taken.Contains((port, proto)) || !seen.Add((port, proto))) return;

            string? name = Known.GetValueOrDefault((port, proto));
            // Minecraft Java servers commonly run on 25565–25599 (non-default).
            if (name is null && proto == "tcp" && port is >= 25565 and <= 25599)
                name = "Minecraft Java";
            if (name is null) return; // only recognised games

            results.Add(new DetectedGame(name, proto, port));
        }

        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var ep in props.GetActiveTcpListeners()) Consider(ep, "tcp");
            foreach (var ep in props.GetActiveUdpListeners()) Consider(ep, "udp");
        }
        catch
        {
            // Enumerating listeners can fail in restricted environments.
        }

        return results.OrderBy(g => g.Port).ToList();
    }
}
