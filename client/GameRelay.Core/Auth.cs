using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GameRelay.Core;

/// <summary>Shared-secret HMAC helpers matching the Go relay server.</summary>
public static class Auth
{
    /// <summary>hex(HMAC-SHA256(secret, message)) for the TCP handshake.</summary>
    public static string ComputeMac(string secret, string message)
    {
        byte[] mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>
    /// Builds a UDP carrier AUTH datagram:
    /// [magic][0x01][ts 8B BE][HMAC-SHA256(secret, "udp-auth" || ts) 32B].
    /// </summary>
    public static byte[] BuildUdpAuth(string secret, long unixSeconds)
    {
        byte[] pkt = new byte[2 + 8 + 32];
        pkt[0] = UdpProtocol.Magic;
        pkt[1] = UdpProtocol.TypeAuth;
        BinaryPrimitives.WriteUInt64BigEndian(pkt.AsSpan(2, 8), (ulong)unixSeconds);

        byte[] msg = new byte[8 + 8];
        Encoding.ASCII.GetBytes("udp-auth").CopyTo(msg, 0);
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(8, 8), (ulong)unixSeconds);
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), msg);
        mac.CopyTo(pkt, 10);
        return pkt;
    }
}
