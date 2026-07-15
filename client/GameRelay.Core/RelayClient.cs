using System.Collections.Concurrent;
using System.Net.Sockets;

namespace GameRelay.Core;

/// <summary>
/// Client side of the relay protocol: maintains the control channel with
/// automatic reconnection, answers conn_requests with TCP data connections,
/// and runs the UDP carrier.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TunnelRuntime> _tunnels = new();

    private CancellationTokenSource? _cts;
    private CancellationToken _clientCt;
    private Task? _loopTask;
    private volatile bool _sessionReachedConnected;
    private TcpClient? _control;
    private NetworkStream? _stream;
    private UdpCarrier? _carrier;
    private long _lastPongTicks;
    private volatile int _latencyMs = -1;

    private string _host = "";
    private int _port;
    private string _secret = "";

    public RelayState State { get; private set; } = RelayState.Disconnected;

    /// <summary>Round-trip time of the last control ping, or -1.</summary>
    public int LatencyMs => _latencyMs;

    public IReadOnlyCollection<TunnelRuntime> Tunnels => (IReadOnlyCollection<TunnelRuntime>)_tunnels.Values;

    public event Action<RelayState, string?>? StateChanged;
    public event Action<string>? Log;
    public event Action<TunnelRuntime>? TunnelChanged;

    private void SetState(RelayState s, string? detail = null)
    {
        State = s;
        StateChanged?.Invoke(s, detail);
    }

    private void Info(string msg) => Log?.Invoke(msg);

    /// <summary>Starts the connection loop. Idempotent.</summary>
    public void Start(string host, int controlPort, string secret, IEnumerable<TunnelConfig> tunnels)
    {
        if (_loopTask is { IsCompleted: false }) return;
        _host = host;
        _port = controlPort;
        _secret = secret;

        _tunnels.Clear();
        foreach (var t in tunnels)
            _tunnels[t.Id] = new TunnelRuntime(t.Clone());

        _cts = new CancellationTokenSource();
        _clientCt = _cts.Token;
        _loopTask = Task.Run(() => ConnectionLoopAsync(_cts.Token));
    }

    /// <summary>Stops the loop and closes everything.</summary>
    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;
        cts.Cancel();
        _control?.Close();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); } catch { }
        }
        _loopTask = null;
        _cts = null;
        SetState(RelayState.Disconnected, "stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ---------------------------------------------------------------- loop

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(RelayState.Connecting, attempt == 0 ? null : $"retry #{attempt}");
                _sessionReachedConnected = false;
                await RunSessionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Info($"connection lost: {ex.Message}");
            }

            // Sessions almost always end by throwing (the read loop breaks when
            // the link drops), so the backoff reset must be flag-based: a session
            // that reached Connected starts the next retry ladder from scratch.
            if (_sessionReachedConnected)
                attempt = 0;

            foreach (var rt in _tunnels.Values)
            {
                if (rt.Status is TunnelStatus.Active or TunnelStatus.Starting)
                {
                    rt.Status = TunnelStatus.Stopped;
                    TunnelChanged?.Invoke(rt);
                }
            }
            _latencyMs = -1;

            if (ct.IsCancellationRequested) break;
            attempt++;
            // Exponential backoff with jitter, capped at 30s.
            int delayMs = Math.Min(30_000, 1000 << Math.Min(attempt - 1, 5));
            delayMs += Random.Shared.Next(0, 500);
            SetState(RelayState.Connecting, $"reconnecting in {delayMs / 1000.0:0.#}s");
            try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        SetState(RelayState.Disconnected);
    }

    /// <summary>One connected session: handshake, tunnels, read pump.</summary>
    private async Task RunSessionAsync(CancellationToken outerCt)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = sessionCts.Token;

        using var control = new TcpClient { NoDelay = true };
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        await control.ConnectAsync(_host, _port, connectTimeout.Token).ConfigureAwait(false);

        var stream = control.GetStream();
        _control = control;
        _stream = stream;

        // Challenge/response handshake.
        var challenge = await Framing.ReadAsync(stream, ct).ConfigureAwait(false);
        if (challenge.Type != MessageTypes.Challenge || string.IsNullOrEmpty(challenge.Nonce))
            throw new InvalidDataException("expected challenge from server");
        await Framing.WriteAsync(stream, new ControlMessage
        {
            Type = MessageTypes.Auth,
            Mac = Auth.ComputeMac(_secret, challenge.Nonce),
            Role = "control",
            Version = ControlMessage.ProtocolVersion,
        }, ct).ConfigureAwait(false);

        var authResp = await Framing.ReadAsync(stream, ct).ConfigureAwait(false);
        if (authResp.Type == MessageTypes.Error)
            throw new UnauthorizedAccessException(authResp.Reason ?? "authentication failed");
        if (authResp.Type != MessageTypes.AuthOk)
            throw new InvalidDataException($"unexpected handshake reply: {authResp.Type}");

        Info($"connected to {_host}:{_port}");
        _sessionReachedConnected = true;
        SetState(RelayState.Connected);
        Interlocked.Exchange(ref _lastPongTicks, Environment.TickCount64);

        // UDP carrier for all UDP tunnels.
        await using var carrier = new UdpCarrier(_host, authResp.UdpPort, _secret, ResolveUdpTunnel, Info);
        _carrier = carrier;
        carrier.Start(ct);

        // Open every enabled tunnel.
        foreach (var rt in _tunnels.Values)
        {
            if (rt.Config.Enabled)
                await SendOpenTunnelAsync(rt, ct).ConfigureAwait(false);
        }

        // Heartbeat: ping every 15s, declare the link dead after 45s of silence.
        using var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var pingTask = Task.Run(async () =>
        {
            while (await pingTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastPongTicks) > 45_000)
                {
                    Info("heartbeat timeout — forcing reconnect");
                    control.Close();
                    return;
                }
                await SendAsync(new ControlMessage { Type = MessageTypes.Ping, Ts = Environment.TickCount64 }, ct)
                    .ConfigureAwait(false);
            }
        }, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await Framing.ReadAsync(stream, ct).ConfigureAwait(false);
                await HandleMessageAsync(msg, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            sessionCts.Cancel();
            _carrier = null;
            _control = null;
            _stream = null;
            try { await pingTask.ConfigureAwait(false); } catch { }
        }
    }

    private async Task HandleMessageAsync(ControlMessage msg, CancellationToken ct)
    {
        switch (msg.Type)
        {
            case MessageTypes.Pong:
                Interlocked.Exchange(ref _lastPongTicks, Environment.TickCount64);
                if (msg.Ts > 0)
                    _latencyMs = (int)Math.Max(0, Environment.TickCount64 - msg.Ts);
                break;

            case MessageTypes.ConnRequest:
                if (msg.TunnelId is not null && msg.ConnId is not null &&
                    _tunnels.TryGetValue(msg.TunnelId, out var rt))
                {
                    // Data connections use the client-lifetime token, not the
                    // session token: players stay connected through a brief
                    // control-channel reconnect.
                    _ = Task.Run(() => ServeDataConnectionAsync(rt, msg.ConnId, _clientCt), _clientCt);
                }
                break;

            case MessageTypes.TunnelOk:
                if (msg.TunnelId is not null && _tunnels.TryGetValue(msg.TunnelId, out var ok))
                {
                    ok.Status = TunnelStatus.Active;
                    ok.ErrorReason = null;
                    TunnelChanged?.Invoke(ok);
                    Info($"tunnel '{ok.Config.Name}' active on public port {ok.Config.PublicPort}/{ok.Config.Protocol}");
                }
                break;

            case MessageTypes.TunnelFail:
                if (msg.TunnelId is not null && _tunnels.TryGetValue(msg.TunnelId, out var fail))
                {
                    fail.Status = TunnelStatus.Error;
                    fail.ErrorReason = msg.Reason;
                    TunnelChanged?.Invoke(fail);
                    Info($"tunnel '{fail.Config.Name}' failed: {msg.Reason}");
                }
                break;

            case MessageTypes.Error:
                throw new InvalidOperationException($"server error: {msg.Reason}");
        }
        await Task.CompletedTask;
    }

    private async Task SendAsync(ControlMessage msg, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Framing.WriteAsync(stream, msg, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendOpenTunnelAsync(TunnelRuntime rt, CancellationToken ct)
    {
        rt.Status = TunnelStatus.Starting;
        TunnelChanged?.Invoke(rt);
        await SendAsync(new ControlMessage
        {
            Type = MessageTypes.OpenTunnel,
            TunnelId = rt.Config.Id,
            Proto = rt.Config.Protocol,
            PublicPort = rt.Config.PublicPort,
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- tunnels

    /// <summary>Adds (or replaces) a tunnel; opens it if connected+enabled.</summary>
    public async Task UpsertTunnelAsync(TunnelConfig config)
    {
        bool existed = _tunnels.TryRemove(config.Id, out var old);
        if (existed && old!.Status is TunnelStatus.Active or TunnelStatus.Starting)
            await TryCloseOnServerAsync(config.Id).ConfigureAwait(false);

        var rt = new TunnelRuntime(config.Clone());
        _tunnels[config.Id] = rt;
        if (config.Enabled && State == RelayState.Connected)
        {
            try { await SendOpenTunnelAsync(rt, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Info($"open tunnel failed: {ex.Message}"); }
        }
        TunnelChanged?.Invoke(rt);
    }

    /// <summary>Removes a tunnel entirely.</summary>
    public async Task RemoveTunnelAsync(string id)
    {
        if (_tunnels.TryRemove(id, out var rt))
        {
            await TryCloseOnServerAsync(id).ConfigureAwait(false);
            rt.Status = TunnelStatus.Stopped;
            TunnelChanged?.Invoke(rt);
        }
    }

    /// <summary>Enables or disables a tunnel (start/stop button).</summary>
    public async Task SetTunnelEnabledAsync(string id, bool enabled)
    {
        if (!_tunnels.TryGetValue(id, out var rt)) return;
        rt.Config.Enabled = enabled;
        if (enabled)
        {
            if (State == RelayState.Connected)
            {
                try { await SendOpenTunnelAsync(rt, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Info($"open tunnel failed: {ex.Message}"); }
            }
        }
        else
        {
            await TryCloseOnServerAsync(id).ConfigureAwait(false);
            rt.Status = TunnelStatus.Stopped;
            rt.ErrorReason = null;
            TunnelChanged?.Invoke(rt);
        }
    }

    private async Task TryCloseOnServerAsync(string id)
    {
        if (State != RelayState.Connected) return;
        try
        {
            await SendAsync(new ControlMessage { Type = MessageTypes.CloseTunnel, TunnelId = id },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private TunnelRuntime? ResolveUdpTunnel(ushort publicPort)
    {
        foreach (var rt in _tunnels.Values)
        {
            if (rt.Config.Protocol == "udp" && rt.Config.PublicPort == publicPort &&
                rt.Status == TunnelStatus.Active)
                return rt;
        }
        return null;
    }

    // ----------------------------------------------------- data connections

    /// <summary>
    /// Answers one conn_request: dials the relay back (role=data) and the
    /// local game server, then pipes bytes both ways.
    /// </summary>
    private async Task ServeDataConnectionAsync(TunnelRuntime rt, string connId, CancellationToken ct)
    {
        using var relay = new TcpClient { NoDelay = true };
        using var local = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await relay.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            var rs = relay.GetStream();

            var challenge = await Framing.ReadAsync(rs, timeout.Token).ConfigureAwait(false);
            if (challenge.Type != MessageTypes.Challenge || string.IsNullOrEmpty(challenge.Nonce))
                return;
            await Framing.WriteAsync(rs, new ControlMessage
            {
                Type = MessageTypes.Auth,
                Mac = Auth.ComputeMac(_secret, challenge.Nonce),
                Role = "data",
                ConnId = connId,
                Version = ControlMessage.ProtocolVersion,
            }, timeout.Token).ConfigureAwait(false);
            var resp = await Framing.ReadAsync(rs, timeout.Token).ConfigureAwait(false);
            if (resp.Type != MessageTypes.AuthOk)
            {
                Info($"data connection rejected: {resp.Reason}");
                return;
            }

            await local.ConnectAsync(rt.Config.LocalHost, rt.Config.LocalPort, timeout.Token).ConfigureAwait(false);
            var ls = local.GetStream();

            rt.ConnOpened();
            TunnelChanged?.Invoke(rt);
            try
            {
                // relay -> local counts as BytesIn, local -> relay as BytesOut.
                var a = PumpAsync(rs, ls, rt.AddBytesIn, ct);
                var b = PumpAsync(ls, rs, rt.AddBytesOut, ct);
                await Task.WhenAny(a, b).ConfigureAwait(false);
                // Let the other direction drain (half-close), but never hang
                // forever on a dead peer.
                await Task.WhenAny(Task.WhenAll(a, b),
                    Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None)).ConfigureAwait(false);
            }
            finally
            {
                rt.ConnClosed();
                TunnelChanged?.Invoke(rt);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Info($"data connection error ({rt.Config.Name}): {ex.Message}");
        }
    }

    private static async Task PumpAsync(NetworkStream src, NetworkStream dst,
        Action<long> onBytes, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (true)
            {
                int n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                onBytes(n);
            }
            // Propagate EOF so the far side can finish draining.
            try { dst.Socket.Shutdown(SocketShutdown.Send); } catch { }
        }
        catch
        {
            // Either side dropped: closing is handled by the caller.
        }
    }
}
