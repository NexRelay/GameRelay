using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace GameRelay.Core;

/// <summary>
/// The single UDP socket that carries all UDP tunnel traffic between this
/// client and the relay server. Each public visitor (player) is one session;
/// per session we hold one local UDP socket to the game server.
/// </summary>
public sealed class UdpCarrier : IAsyncDisposable
{
    private const int SessionIdleSeconds = 90;

    private readonly UdpClient _sock;
    private readonly string _secret;
    private readonly Func<ushort, TunnelRuntime?> _resolveTunnel;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<uint, UdpSession> _sessions = new();
    private CancellationTokenSource? _cts;
    private volatile bool _registered;

    private sealed class UdpSession(uint id, TunnelRuntime tunnel, UdpClient local)
    {
        public uint Id { get; } = id;
        public TunnelRuntime Tunnel { get; } = tunnel;
        public UdpClient Local { get; } = local;
        public long LastActiveTicks = Environment.TickCount64;
        public void Touch() => Interlocked.Exchange(ref LastActiveTicks, Environment.TickCount64);
    }

    public UdpCarrier(string host, int udpPort, string secret,
        Func<ushort, TunnelRuntime?> resolveTunnel, Action<string> log)
    {
        _secret = secret;
        _resolveTunnel = resolveTunnel;
        _log = log;
        _sock = new UdpClient();
        DisableConnReset(_sock);
        _sock.Connect(host, udpPort);
    }

    /// <summary>True once the server has acknowledged our AUTH datagram.</summary>
    public bool Registered => _registered;

    public void Start(CancellationToken outerCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = _cts.Token;
        _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
        _ = Task.Run(() => AuthLoopAsync(ct), ct);
        _ = Task.Run(() => CleanupLoopAsync(ct), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _sock.Dispose();
        foreach (var s in _sessions.Values)
            CloseSession(s);
        _sessions.Clear();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends AUTH every 15 seconds. It doubles as the NAT keepalive and
    /// heals the server-side carrier address after a server restart or a
    /// NAT rebinding.
    /// </summary>
    private async Task AuthLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                byte[] pkt = Auth.BuildUdpAuth(_secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await _sock.SendAsync(pkt, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Transient send failure; retry on the next tick.
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] buf;
            try
            {
                var result = await _sock.ReceiveAsync(ct).ConfigureAwait(false);
                buf = result.Buffer;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            if (buf.Length < 2 || buf[0] != UdpProtocol.Magic) continue;

            switch (buf[1])
            {
                case UdpProtocol.TypeAuthOk:
                    if (!_registered)
                    {
                        _registered = true;
                        _log("udp carrier registered with relay");
                    }
                    break;

                case UdpProtocol.TypeData:
                    if (buf.Length >= UdpProtocol.HeaderLen)
                        HandleData(buf, ct);
                    break;
            }
        }
    }

    /// <summary>Server → client: payload for session (or a new session).</summary>
    private void HandleData(byte[] frame, CancellationToken ct)
    {
        uint sid = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(2, 4));
        ushort publicPort = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6, 2));

        if (!_sessions.TryGetValue(sid, out var session))
        {
            var tunnel = _resolveTunnel(publicPort);
            if (tunnel is null) return; // no active UDP tunnel on that port

            var local = new UdpClient();
            DisableConnReset(local);
            try
            {
                local.Connect(tunnel.Config.LocalHost, tunnel.Config.LocalPort);
            }
            catch (Exception ex)
            {
                _log($"udp session dial failed ({tunnel.Config.Name}): {ex.Message}");
                local.Dispose();
                return;
            }
            session = new UdpSession(sid, tunnel, local);
            if (!_sessions.TryAdd(sid, session))
            {
                local.Dispose();
                if (!_sessions.TryGetValue(sid, out session!)) return;
            }
            else
            {
                tunnel.ConnOpened();
                _ = Task.Run(() => SessionReceiveLoopAsync(session, ct), ct);
            }
        }

        session.Touch();
        int payloadLen = frame.Length - UdpProtocol.HeaderLen;
        try
        {
            session.Local.Send(frame.AsSpan(UdpProtocol.HeaderLen));
            session.Tunnel.AddBytesIn(payloadLen);
        }
        catch (Exception)
        {
            // Local game server unreachable right now; drop the datagram.
        }
    }

    /// <summary>Local game server → carrier → relay, framed per session.</summary>
    private async Task SessionReceiveLoopAsync(UdpSession session, CancellationToken ct)
    {
        byte[] header = new byte[UdpProtocol.HeaderLen];
        header[0] = UdpProtocol.Magic;
        header[1] = UdpProtocol.TypeData;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(2, 4), session.Id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)session.Tunnel.Config.PublicPort);

        while (!ct.IsCancellationRequested)
        {
            byte[] payload;
            try
            {
                var result = await session.Local.ReceiveAsync(ct).ConfigureAwait(false);
                payload = result.Buffer;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; } // session expired
            catch (SocketException) { continue; }

            byte[] frame = new byte[UdpProtocol.HeaderLen + payload.Length];
            header.CopyTo(frame, 0);
            payload.CopyTo(frame, UdpProtocol.HeaderLen);
            try
            {
                await _sock.SendAsync(frame, ct).ConfigureAwait(false);
                session.Touch();
                session.Tunnel.AddBytesOut(payload.Length);
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            long now = Environment.TickCount64;
            foreach (var (sid, session) in _sessions)
            {
                if (now - Interlocked.Read(ref session.LastActiveTicks) > SessionIdleSeconds * 1000L)
                {
                    if (_sessions.TryRemove(sid, out var removed))
                        CloseSession(removed);
                }
            }
        }
    }

    private static void CloseSession(UdpSession s)
    {
        s.Tunnel.ConnClosed();
        s.Local.Dispose();
    }

    /// <summary>
    /// On Windows, a UDP socket throws ConnectionReset on receive after an
    /// ICMP port-unreachable. Games restart all the time; ignore it.
    /// </summary>
    private static void DisableConnReset(UdpClient sock)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        try
        {
            sock.Client.IOControl(SIO_UDP_CONNRESET, [0], null);
        }
        catch
        {
            // Not fatal; the receive loop also tolerates SocketException.
        }
    }
}
