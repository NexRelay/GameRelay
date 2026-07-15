// GameRelay CLI — the Linux (and cross-platform) client.
// Same tunnel engine and config format as the Windows app; config lives in
// ~/.config/GameRelay/config.json on Linux.

using System.Reflection;
using GameRelay.Core;

string version = Assembly.GetExecutingAssembly().GetName().Version is { } v
    ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

return args.Length == 0 ? await RunAsync() : args[0].ToLowerInvariant() switch
{
    "run" => await RunAsync(),
    "setup" => Setup(),
    "add" => Add(args),
    "preset" => Preset(args),
    "presets" => Presets(),
    "list" => List(),
    "remove" => Remove(args),
    "enable" => SetEnabled(args, true),
    "disable" => SetEnabled(args, false),
    "version" or "--version" or "-v" => Version(),
    _ => Help(),
};

// ----------------------------------------------------------------- helpers

static void Ok(string msg) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(msg); Console.ResetColor(); }
static void Warn(string msg) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(msg); Console.ResetColor(); }
static void Err(string msg) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine(msg); Console.ResetColor(); }
static void Line(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");

static string? Flag(string[] args, string name)
{
    for (int i = 1; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static (AppConfig config, bool failed) LoadConfig()
{
    var r = ConfigStore.Load();
    if (r.LoadFailed) Err($"warning: could not read saved settings ({r.Error})");
    return (r.Config, r.LoadFailed);
}

int Version()
{
    Console.WriteLine($"gamerelay {version} (protocol v{ControlMessage.ProtocolVersion})");
    Console.WriteLine($"config: {ConfigStore.ConfigPath}");
    return 0;
}

int Help()
{
    Console.WriteLine($"""
        GameRelay CLI v{version} — host game servers from home, no port forwarding.

        USAGE
          gamerelay [run]              connect and keep all enabled tunnels open
          gamerelay setup              interactive first-time configuration
          gamerelay presets            list the built-in game presets
          gamerelay preset <name> [--public N] [--local-port N] [--local-host H]
                                       add tunnels from a preset (e.g. "minecraft java")
          gamerelay add --name X --proto tcp|udp --public N --local-port N [--local-host H]
                                       add a custom tunnel
          gamerelay list               show configured tunnels
          gamerelay enable  <name|id>  enable a tunnel
          gamerelay disable <name|id>  disable a tunnel
          gamerelay remove  <name|id>  delete a tunnel
          gamerelay version            print version and config path

        Config file: {ConfigStore.ConfigPath}
        """);
    return 0;
}

// ------------------------------------------------------------------ setup

int Setup()
{
    var (cfg, failed) = LoadConfig();
    if (failed) { Err("fix or delete the config file first"); return 1; }

    Console.Write($"Relay server address [{cfg.ServerHost}]: ");
    string host = Console.ReadLine()?.Trim() ?? "";
    if (host != "") cfg.ServerHost = host;

    Console.Write($"Control port [{(cfg.ControlPort > 0 ? cfg.ControlPort : 7000)}]: ");
    string portS = Console.ReadLine()?.Trim() ?? "";
    if (portS != "" && int.TryParse(portS, out int p)) cfg.ControlPort = p;
    if (cfg.ControlPort <= 0) cfg.ControlPort = 7000;

    Console.Write($"Shared secret{(cfg.Secret.Length >= 16 ? " [keep current]" : "")}: ");
    string secret = Console.ReadLine()?.Trim() ?? "";
    if (secret != "") cfg.Secret = secret;

    if (string.IsNullOrWhiteSpace(cfg.ServerHost) || cfg.Secret.Length < 16)
    {
        Err("server address is required and the secret must be at least 16 characters.");
        return 1;
    }
    ConfigStore.Save(cfg);
    Ok($"saved to {ConfigStore.ConfigPath}");
    Console.WriteLine("next: add a tunnel, e.g.  gamerelay preset \"minecraft java\"");
    return 0;
}

// ----------------------------------------------------------------- tunnels

int Presets()
{
    Console.WriteLine("Available presets:");
    foreach (var p in GamePresets.All)
    {
        string ports = string.Join(", ", p.Ports.Select(x => $"{x.Port}/{x.Protocol}"));
        Console.WriteLine($"  {p.Name,-22} {ports}");
    }
    return 0;
}

int Preset(string[] args)
{
    if (args.Length < 2) { Err("usage: gamerelay preset <name> [--public N] [--local-port N] [--local-host H]"); return 1; }
    string name = args[1];
    var preset = GamePresets.All.FirstOrDefault(p =>
        p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    if (preset is null) { Err($"no preset matches '{name}' — see: gamerelay presets"); return 1; }

    int? pub = int.TryParse(Flag(args, "--public"), out int pv) ? pv : null;
    var (cfg, failed) = LoadConfig();
    if (failed) return 1;

    var tunnels = GamePresets.Expand(preset, pub);
    foreach (var t in tunnels)
    {
        if (Flag(args, "--local-host") is { } lh) t.LocalHost = lh;
        if (int.TryParse(Flag(args, "--local-port"), out int lp) && tunnels.Count == 1) t.LocalPort = lp;
        cfg.Tunnels.Add(t);
        Ok($"added: {t.Name}  public :{t.PublicPort}/{t.Protocol} -> {t.LocalHost}:{t.LocalPort}");
    }
    ConfigStore.Save(cfg);
    return 0;
}

int Add(string[] args)
{
    string? name = Flag(args, "--name");
    string proto = Flag(args, "--proto") ?? "tcp";
    if (proto is not ("tcp" or "udp")) { Err("--proto must be tcp or udp"); return 1; }
    if (!int.TryParse(Flag(args, "--public"), out int pub) ||
        !int.TryParse(Flag(args, "--local-port"), out int lp))
    { Err("usage: gamerelay add --name X --proto tcp|udp --public N --local-port N [--local-host H]"); return 1; }

    var (cfg, failed) = LoadConfig();
    if (failed) return 1;
    var t = new TunnelConfig
    {
        Name = name ?? $"{proto.ToUpper()} {pub}",
        Protocol = proto,
        PublicPort = pub,
        LocalPort = lp,
        LocalHost = Flag(args, "--local-host") ?? "127.0.0.1",
    };
    cfg.Tunnels.Add(t);
    ConfigStore.Save(cfg);
    Ok($"added: {t.Name}  public :{t.PublicPort}/{t.Protocol} -> {t.LocalHost}:{t.LocalPort}");
    return 0;
}

int List()
{
    var (cfg, _) = LoadConfig();
    if (cfg.Tunnels.Count == 0) { Console.WriteLine("no tunnels configured. add one with: gamerelay preset <name>"); return 0; }
    Console.WriteLine($"{"NAME",-26} {"PROTO",-6} {"PUBLIC",-8} {"LOCAL",-22} {"ENABLED",-8} ID");
    foreach (var t in cfg.Tunnels)
        Console.WriteLine($"{t.Name,-26} {t.Protocol,-6} {t.PublicPort,-8} {t.LocalHost + ":" + t.LocalPort,-22} {(t.Enabled ? "yes" : "no"),-8} {t.Id[..8]}");
    return 0;
}

TunnelConfig? Find(AppConfig cfg, string key) =>
    cfg.Tunnels.FirstOrDefault(t => t.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase))
    ?? cfg.Tunnels.FirstOrDefault(t => t.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
    ?? cfg.Tunnels.FirstOrDefault(t => t.Name.Contains(key, StringComparison.OrdinalIgnoreCase));

int Remove(string[] args)
{
    if (args.Length < 2) { Err("usage: gamerelay remove <name|id>"); return 1; }
    var (cfg, failed) = LoadConfig();
    if (failed) return 1;
    var t = Find(cfg, args[1]);
    if (t is null) { Err($"no tunnel matches '{args[1]}'"); return 1; }
    cfg.Tunnels.Remove(t);
    ConfigStore.Save(cfg);
    Ok($"removed: {t.Name}");
    return 0;
}

int SetEnabled(string[] args, bool enabled)
{
    if (args.Length < 2) { Err($"usage: gamerelay {(enabled ? "enable" : "disable")} <name|id>"); return 1; }
    var (cfg, failed) = LoadConfig();
    if (failed) return 1;
    var t = Find(cfg, args[1]);
    if (t is null) { Err($"no tunnel matches '{args[1]}'"); return 1; }
    t.Enabled = enabled;
    ConfigStore.Save(cfg);
    Ok($"{t.Name}: {(enabled ? "enabled" : "disabled")}");
    return 0;
}

// --------------------------------------------------------------------- run

async Task<int> RunAsync()
{
    var (cfg, failed) = LoadConfig();
    if (failed) return 1;
    if (string.IsNullOrWhiteSpace(cfg.ServerHost) || cfg.Secret.Length < 16)
    {
        Err("not configured yet — run: gamerelay setup");
        return 1;
    }
    if (cfg.Tunnels.Count == 0)
        Warn("no tunnels configured; connecting anyway. add some with: gamerelay preset <name>");

    Console.WriteLine($"GameRelay CLI v{version} — relay {cfg.ServerHost}:{cfg.ControlPort}");
    Console.WriteLine("press Ctrl+C to stop");

    await using var client = new RelayClient();
    client.Log += m => Line(m);
    client.StateChanged += (state, detail) =>
    {
        string s = state switch
        {
            RelayState.Connected => "CONNECTED",
            RelayState.Connecting => $"connecting{(detail is null ? "" : $" ({detail})")}",
            _ => "disconnected",
        };
        if (state == RelayState.Connected) Ok($"[{DateTime.Now:HH:mm:ss}] {s}");
        else Line(s);
    };
    client.TunnelChanged += rt =>
    {
        string status = rt.Status switch
        {
            TunnelStatus.Active => "active",
            TunnelStatus.Starting => "starting",
            TunnelStatus.Error => $"ERROR: {rt.ErrorReason}",
            _ => "stopped",
        };
        Line($"tunnel '{rt.Config.Name}' (:{rt.Config.PublicPort}/{rt.Config.Protocol}) -> {status}");
    };

    client.Start(cfg.ServerHost, cfg.ControlPort, cfg.Secret, cfg.Tunnels);

    var done = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };

    // Periodic one-line stats while running.
    _ = Task.Run(async () =>
    {
        while (!done.Task.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            if (client.State != RelayState.Connected) continue;
            foreach (var rt in client.Tunnels.Where(t => t.Status == TunnelStatus.Active))
                Line($"stats '{rt.Config.Name}': down {Fmt(rt.BytesIn)}, up {Fmt(rt.BytesOut)}, {rt.ActiveConnections} conn, ping {client.LatencyMs} ms");
        }
    });

    await done.Task;
    Line("stopping…");
    await client.StopAsync();
    return 0;

    static string Fmt(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1048576 => $"{b / 1024.0:0.#} KB",
        < 1073741824 => $"{b / 1048576.0:0.#} MB",
        _ => $"{b / 1073741824.0:0.##} GB",
    };
}
