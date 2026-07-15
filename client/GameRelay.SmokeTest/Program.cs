// End-to-end smoke test: drives the real GameRelay.Core client against a
// locally running Go relay server.
//
// Usage: GameRelay.SmokeTest <serverHost> <controlPort> <secret>
// Exit code 0 = all checks passed.

using System.Net;
using System.Net.Sockets;
using System.Text;
using GameRelay.Core;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: GameRelay.SmokeTest <host> <controlPort> <secret>");
    return 2;
}
string host = args[0];
int controlPort = int.Parse(args[1]);
string secret = args[2];

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

// ---- local fake "game servers" -------------------------------------------
var tcpEcho = new TcpListener(IPAddress.Loopback, 0);
tcpEcho.Start();
int tcpEchoPort = ((IPEndPoint)tcpEcho.LocalEndpoint).Port;
_ = Task.Run(async () =>
{
    while (true)
    {
        var c = await tcpEcho.AcceptTcpClientAsync();
        _ = Task.Run(async () =>
        {
            using (c)
            {
                var s = c.GetStream();
                var buf = new byte[8192];
                int n;
                while ((n = await s.ReadAsync(buf)) > 0)
                    await s.WriteAsync(buf.AsMemory(0, n));
            }
        });
    }
});

var udpEcho = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
int udpEchoPort = ((IPEndPoint)udpEcho.Client.LocalEndPoint!).Port;
_ = Task.Run(async () =>
{
    while (true)
    {
        var r = await udpEcho.ReceiveAsync();
        await udpEcho.SendAsync(r.Buffer, r.Buffer.Length, r.RemoteEndPoint);
    }
});

Console.WriteLine($"local echo servers: tcp={tcpEchoPort} udp={udpEchoPort}");

// ---- relay client ----------------------------------------------------------
const int publicTcpPort = 27015;
const int publicUdpPort = 27016;

var tcpTunnel = new TunnelConfig
{
    Name = "smoke-tcp", Protocol = "tcp",
    PublicPort = publicTcpPort, LocalPort = tcpEchoPort,
};
var udpTunnel = new TunnelConfig
{
    Name = "smoke-udp", Protocol = "udp",
    PublicPort = publicUdpPort, LocalPort = udpEchoPort,
};

await using var client = new RelayClient();
client.Log += m => Console.WriteLine($"  [client] {m}");
client.Start(host, controlPort, secret, [tcpTunnel, udpTunnel]);

// Wait for both tunnels to become active.
bool WaitFor(Func<bool> cond, int seconds)
{
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < deadline)
    {
        if (cond()) return true;
        Thread.Sleep(100);
    }
    return false;
}

Check(WaitFor(() => client.State == RelayState.Connected, 10), "control channel connected");
Check(WaitFor(() => client.Tunnels.All(t => t.Status == TunnelStatus.Active), 10), "tunnels active");

// ---- visitor: TCP through the relay ---------------------------------------
{
    using var visitor = new TcpClient { NoDelay = true };
    await visitor.ConnectAsync(host, publicTcpPort);
    var vs = visitor.GetStream();
    byte[] payload = Encoding.UTF8.GetBytes("hello over relay tcp tunnel");
    await vs.WriteAsync(payload);
    byte[] got = new byte[payload.Length];
    var readTask = vs.ReadExactlyAsync(got).AsTask();
    bool done = await Task.WhenAny(readTask, Task.Delay(5000)) == readTask;
    Check(done && got.SequenceEqual(payload), "tcp echo through tunnel");

    // Big transfer both ways (1 MiB) to exercise the pumps.
    byte[] big = new byte[1024 * 1024];
    Random.Shared.NextBytes(big);
    var writeTask = vs.WriteAsync(big).AsTask();
    byte[] bigGot = new byte[big.Length];
    var bigRead = vs.ReadExactlyAsync(bigGot).AsTask();
    done = await Task.WhenAny(Task.WhenAll(writeTask, bigRead), Task.Delay(15000)) != null
           && bigRead.IsCompletedSuccessfully;
    Check(done && bigGot.SequenceEqual(big), "1 MiB tcp round-trip");
}

// ---- visitor: UDP through the relay ---------------------------------------
{
    using var visitor = new UdpClient();
    visitor.Connect(host, publicUdpPort);
    byte[] payload = Encoding.UTF8.GetBytes("udp ping via relay");
    bool ok = false;
    for (int attempt = 0; attempt < 10 && !ok; attempt++)
    {
        await visitor.SendAsync(payload);
        var recvTask = visitor.ReceiveAsync();
        if (await Task.WhenAny(recvTask, Task.Delay(500)) == recvTask)
            ok = recvTask.Result.Buffer.SequenceEqual(payload);
    }
    Check(ok, "udp echo through tunnel");
}

// ---- stats ------------------------------------------------------------------
Check(client.Tunnels.First(t => t.Config.Protocol == "tcp").BytesIn > 1024 * 1024,
    "tcp tunnel byte counters advanced");
Check(client.Tunnels.First(t => t.Config.Protocol == "udp").BytesOut > 0,
    "udp tunnel byte counters advanced");

// ---- tunnel stop/start ------------------------------------------------------
await client.SetTunnelEnabledAsync(tcpTunnel.Id, false);
await Task.Delay(500);
// Windows retries refused loopback connects for a few seconds, so give it
// time; anything other than a successful connect proves the port is closed.
bool refused;
try
{
    using var v2 = new TcpClient();
    var t = v2.ConnectAsync(host, publicTcpPort);
    await Task.WhenAny(t, Task.Delay(6000));
    refused = !t.IsCompletedSuccessfully;
}
catch { refused = true; }
Check(refused, "disabled tunnel refuses connections");

await client.SetTunnelEnabledAsync(tcpTunnel.Id, true);
Check(WaitFor(() => client.Tunnels.First(t => t.Config.Id == tcpTunnel.Id).Status == TunnelStatus.Active, 5),
    "tunnel re-enabled");

// ---- reconnect test ---------------------------------------------------------
// With --reconnect-test the harness kills and restarts the relay while we
// watch the client heal itself.
if (args.Contains("--reconnect-test"))
{
    Console.WriteLine("RECONNECT: waiting for the harness to restart the relay...");
    Check(WaitFor(() => client.State != RelayState.Connected, 60), "client noticed the outage");
    Check(WaitFor(() => client.State == RelayState.Connected, 90), "client reconnected");
    Check(WaitFor(() => client.Tunnels.Where(t => t.Config.Enabled)
        .All(t => t.Status == TunnelStatus.Active), 30), "tunnels re-established");

    using var visitor = new TcpClient { NoDelay = true };
    await visitor.ConnectAsync(host, publicTcpPort);
    var vs = visitor.GetStream();
    byte[] payload = Encoding.UTF8.GetBytes("post-reconnect traffic");
    await vs.WriteAsync(payload);
    byte[] got = new byte[payload.Length];
    var rt = vs.ReadExactlyAsync(got).AsTask();
    bool ok = await Task.WhenAny(rt, Task.Delay(5000)) == rt;
    Check(ok && got.SequenceEqual(payload), "tcp traffic after reconnect");
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
