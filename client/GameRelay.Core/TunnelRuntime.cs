namespace GameRelay.Core;

/// <summary>Live status of one tunnel.</summary>
public enum TunnelStatus
{
    Stopped,
    Starting,
    Active,
    Error,
}

/// <summary>Runtime state and counters for one tunnel.</summary>
public sealed class TunnelRuntime(TunnelConfig config)
{
    public TunnelConfig Config { get; } = config;

    public TunnelStatus Status { get; internal set; } = TunnelStatus.Stopped;
    public string? ErrorReason { get; internal set; }

    private long _bytesIn, _bytesOut;
    private int _activeConnections;

    /// <summary>Bytes received from players (public → local).</summary>
    public long BytesIn => Interlocked.Read(ref _bytesIn);

    /// <summary>Bytes sent to players (local → public).</summary>
    public long BytesOut => Interlocked.Read(ref _bytesOut);

    /// <summary>Live TCP connections or UDP sessions.</summary>
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    internal void AddBytesIn(long n) => Interlocked.Add(ref _bytesIn, n);
    internal void AddBytesOut(long n) => Interlocked.Add(ref _bytesOut, n);
    internal void ConnOpened() => Interlocked.Increment(ref _activeConnections);
    internal void ConnClosed() => Interlocked.Decrement(ref _activeConnections);
    internal void ResetCounters()
    {
        Interlocked.Exchange(ref _bytesIn, 0);
        Interlocked.Exchange(ref _bytesOut, 0);
    }
}

/// <summary>Connection state of the relay client.</summary>
public enum RelayState
{
    Disconnected,
    Connecting,
    Connected,
}
