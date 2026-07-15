// Headless test of ServerProvisioner against a real VPS.
// Usage: GameRelay.ProvisionTest <host> <user> <keyPath> <assetsDir>
//    or: GameRelay.ProvisionTest detect     (tests GameDetector)

using System.Net;
using System.Net.Sockets;
using GameRelay.Core;

if (args.Length == 1 && args[0] == "detect")
{
    // Mimic a real Minecraft server on a NON-default port bound to all
    // interfaces, and confirm GameDetector spots it via the range heuristic.
    var listener = new TcpListener(IPAddress.Any, 25568);
    listener.Start();
    var found = GameDetector.Detect([]);
    listener.Stop();
    foreach (var g in found)
        Console.WriteLine($"detected: {g.Name} {g.Port}/{g.Protocol}");
    bool ok = found.Any(g => g.Port == 25568 && g.Name.Contains("Minecraft Java"));
    Console.WriteLine(ok ? "DETECT PASS" : "DETECT FAIL");
    return ok ? 0 : 1;
}

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: <host> <user> <keyPath> <assetsDir>");
    return 2;
}

var result = await ServerProvisioner.ProvisionAsync(
    args[0], args[1], args[2], args[3], 7000,
    line => Console.WriteLine($"[prov] {line}"),
    CancellationToken.None);

if (result.Success)
{
    Console.WriteLine($"SUCCESS — secret: {result.Secret}  port: {result.ControlPort}");
    return 0;
}
Console.WriteLine($"FAILED — {result.Error}");
return 1;
