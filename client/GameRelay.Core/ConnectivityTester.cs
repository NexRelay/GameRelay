using System.Net.Sockets;

namespace GameRelay.Core;

/// <summary>Result of a connectivity check: a pass/fail plus a human message.</summary>
public sealed record CheckResult(bool Ok, string Message)
{
    public static CheckResult Pass(string m) => new(true, m);
    public static CheckResult Fail(string m) => new(false, m);
}

/// <summary>
/// Small, dependency-free probes the setup wizard uses for its per-step
/// "Test" buttons: is a TCP port reachable, and does the relay actually
/// answer with the right shared secret.
/// </summary>
public static class ConnectivityTester
{
    /// <summary>Can we open a TCP connection to host:port?</summary>
    public static async Task<CheckResult> TestPortAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return CheckResult.Pass($"port {port} is reachable");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CheckResult.Fail(
                $"timed out reaching {host}:{port} — the firewall (cloud Security List and/or host) is likely still closed");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return CheckResult.Fail($"{host}:{port} refused the connection — nothing is listening there");
        }
        catch (Exception ex)
        {
            return CheckResult.Fail($"could not reach {host}:{port}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the control port and completes the HMAC handshake to prove the
    /// relay is running and the shared secret matches.
    /// </summary>
    public static async Task<CheckResult> TestRelayAsync(
        string host, int port, string secret, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            var challenge = await Framing.ReadAsync(stream, cts.Token).ConfigureAwait(false);
            if (challenge.Type != MessageTypes.Challenge || string.IsNullOrEmpty(challenge.Nonce))
                return CheckResult.Fail("the server answered but didn't speak the GameRelay protocol");

            await Framing.WriteAsync(stream, new ControlMessage
            {
                Type = MessageTypes.Auth,
                Mac = Auth.ComputeMac(secret, challenge.Nonce),
                Role = "control",
                Version = ControlMessage.ProtocolVersion,
            }, cts.Token).ConfigureAwait(false);

            var reply = await Framing.ReadAsync(stream, cts.Token).ConfigureAwait(false);
            return reply.Type switch
            {
                MessageTypes.AuthOk => CheckResult.Pass("relay is up and the shared secret is correct"),
                MessageTypes.Error => CheckResult.Fail(
                    reply.Reason?.Contains("version", StringComparison.OrdinalIgnoreCase) == true
                        ? $"protocol mismatch: {reply.Reason}"
                        : "the shared secret is wrong (server rejected authentication)"),
                _ => CheckResult.Fail($"unexpected reply from the relay: {reply.Type}"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CheckResult.Fail($"timed out talking to {host}:{port} — is the control port open in the firewall?");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return CheckResult.Fail($"{host}:{port} refused the connection — the relay service isn't running");
        }
        catch (Exception ex)
        {
            return CheckResult.Fail($"connection test failed: {ex.Message}");
        }
    }
}
