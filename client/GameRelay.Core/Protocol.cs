using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameRelay.Core;

/// <summary>
/// JSON envelope for every control-channel frame. Field names must match
/// the Go relay server exactly.
/// </summary>
public sealed class ControlMessage
{
    public const int ProtocolVersion = 1;

    [JsonPropertyName("type")] public string Type { get; set; } = "";

    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("conn_id")] public string? ConnId { get; set; }
    [JsonPropertyName("version")] public int Version { get; set; }

    [JsonPropertyName("udp_port")] public int UdpPort { get; set; }

    [JsonPropertyName("tunnel_id")] public string? TunnelId { get; set; }
    [JsonPropertyName("proto")] public string? Proto { get; set; }
    [JsonPropertyName("public_port")] public int PublicPort { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }

    [JsonPropertyName("remote_addr")] public string? RemoteAddr { get; set; }

    [JsonPropertyName("ts")] public long Ts { get; set; }
}

/// <summary>Control message type constants (mirror of the Go side).</summary>
public static class MessageTypes
{
    public const string Challenge = "challenge";
    public const string Auth = "auth";
    public const string AuthOk = "auth_ok";
    public const string Error = "error";
    public const string OpenTunnel = "open_tunnel";
    public const string CloseTunnel = "close_tunnel";
    public const string TunnelOk = "tunnel_ok";
    public const string TunnelFail = "tunnel_fail";
    public const string ConnRequest = "conn_request";
    public const string Ping = "ping";
    public const string Pong = "pong";
}

/// <summary>
/// Length-prefixed JSON framing: 4-byte big-endian length + UTF-8 JSON.
/// </summary>
public static class Framing
{
    public const int MaxFrameSize = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(Stream stream, ControlMessage msg, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOpts);
        if (body.Length > MaxFrameSize)
            throw new InvalidOperationException($"frame too large: {body.Length}");
        byte[] buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)body.Length);
        body.CopyTo(buf, 4);
        await stream.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    public static async Task<ControlMessage> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] head = new byte[4];
        await stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        uint len = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (len == 0 || len > MaxFrameSize)
            throw new InvalidDataException($"invalid frame size {len}");
        byte[] body = new byte[len];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ControlMessage>(body, JsonOpts)
               ?? throw new InvalidDataException("null frame");
    }
}

/// <summary>UDP carrier datagram constants (mirror of the Go side).</summary>
public static class UdpProtocol
{
    public const byte Magic = 0xC7;
    public const byte TypeAuth = 0x01;
    public const byte TypeAuthOk = 0x02;
    public const byte TypeKeepAlive = 0x03;
    public const byte TypeData = 0x04;
    public const int HeaderLen = 1 + 1 + 4 + 2; // magic + type + session + port
}
