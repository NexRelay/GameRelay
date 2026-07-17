// Headless test of ServerProvisioner against a real VPS.
// Usage: GameRelay.ProvisionTest <host> <user> <keyPath> <assetsDir>
//    or: GameRelay.ProvisionTest detect     (tests GameDetector)

using System.Net;
using System.Net.Sockets;
using GameRelay.Core;

if (args.Length == 1 && args[0] == "oci")
{
    // Print the generated Cloud Shell script so we can validate it externally.
    Console.Write(OciCommand.BuildSecurityListScript());
    return 0;
}

if (args.Length >= 1 && args[0] == "update")
{
    // Pretend we're an old version so a real release counts as "newer".
    var cur = args.Length > 1 ? Version.Parse(args[1]) : new Version(1, 0, 0);
    var info = await UpdateChecker.CheckAsync(cur, CancellationToken.None);
    if (info is null) { Console.WriteLine($"no update newer than {cur}"); return 0; }
    Console.WriteLine($"UPDATE: {info.Version} (tag {info.Tag}) -> {info.HtmlUrl}");
    return 0;
}

if (args.Length >= 3 && args[0] == "testport")
{
    var r = await ConnectivityTester.TestPortAsync(args[1], int.Parse(args[2]), TimeSpan.FromSeconds(8));
    Console.WriteLine($"{(r.Ok ? "OK" : "FAIL")}: {r.Message}");
    return r.Ok ? 0 : 1;
}

if (args.Length >= 4 && args[0] == "testrelay")
{
    var r = await ConnectivityTester.TestRelayAsync(args[1], int.Parse(args[2]), args[3], TimeSpan.FromSeconds(8));
    Console.WriteLine($"{(r.Ok ? "OK" : "FAIL")}: {r.Message}");
    return r.Ok ? 0 : 1;
}

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
