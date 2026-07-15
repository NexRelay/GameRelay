using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GameRelay.Core;

/// <summary>Outcome of an automatic server provisioning run.</summary>
public sealed class ProvisionResult
{
    public bool Success { get; init; }
    /// <summary>The shared secret read back from the server on success.</summary>
    public string? Secret { get; init; }
    public int ControlPort { get; init; } = 7000;
    public string? Error { get; init; }
}

/// <summary>
/// Installs and configures the relay server on a fresh Linux VPS over SSH,
/// using the user's private key. Uploads the matching architecture binary,
/// runs the installer, opens the host firewall and reads back the generated
/// shared secret — so the app can finish setup with zero terminal work.
///
/// Note: this cannot open Oracle's VCN Security List (that lives in the cloud
/// console, not on the host); the UI shows those steps separately.
/// </summary>
public sealed class ServerProvisioner
{
    /// <summary>Files that must exist in the assets directory.</summary>
    public static readonly string[] RequiredAssets =
    {
        "gamerelay-server-linux-amd64",
        "gamerelay-server-linux-arm64",
        "install.sh",
        "gamerelay.service",
    };

    /// <summary>Returns missing asset filenames, empty if all present.</summary>
    public static IReadOnlyList<string> MissingAssets(string assetsDir) =>
        RequiredAssets.Where(f => !File.Exists(Path.Combine(assetsDir, f))).ToList();

    /// <summary>
    /// Connects, installs and configures the relay. <paramref name="log"/>
    /// receives human-readable progress lines.
    /// </summary>
    public static async Task<ProvisionResult> ProvisionAsync(
        string host, string user, string keyPath, string assetsDir,
        int controlPort, Action<string> log, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => Provision(host, user, keyPath, assetsDir, controlPort, log, ct), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ProvisionResult { Success = false, Error = "cancelled" };
        }
        catch (SshAuthenticationException ex)
        {
            return new ProvisionResult { Success = false, Error =
                $"authentication failed — check the username and that this is the right key file ({ex.Message})" };
        }
        catch (Exception ex)
        {
            return new ProvisionResult { Success = false, Error = ex.Message };
        }
    }

    private static ProvisionResult Provision(
        string host, string user, string keyPath, string assetsDir,
        int controlPort, Action<string> log, CancellationToken ct)
    {
        var missing = MissingAssets(assetsDir);
        if (missing.Count > 0)
            return new ProvisionResult { Success = false, Error = $"missing bundled files: {string.Join(", ", missing)}" };

        log($"loading key {Path.GetFileName(keyPath)}…");
        var keyFile = new PrivateKeyFile(keyPath);
        var auth = new PrivateKeyAuthenticationMethod(user, keyFile);
        var conn = new ConnectionInfo(host, 22, user, auth) { Timeout = TimeSpan.FromSeconds(20) };

        using var ssh = new SshClient(conn);
        log($"connecting to {user}@{host}…");
        ssh.Connect();
        log("connected.");
        ct.ThrowIfCancellationRequested();

        // 1. Detect architecture and pick the matching binary.
        string arch = Run(ssh, "uname -m", log, echo: false).Trim();
        string binName = arch switch
        {
            "x86_64" => "gamerelay-server-linux-amd64",
            "aarch64" or "arm64" => "gamerelay-server-linux-arm64",
            _ => throw new InvalidOperationException($"unsupported server architecture: {arch}"),
        };
        log($"server architecture: {arch} → {binName}");

        // 2. Upload the binary + installer + unit file.
        Run(ssh, "mkdir -p ~/gamerelay", log, echo: false);
        using (var sftp = new SftpClient(conn))
        {
            sftp.Connect();
            foreach (var (local, remote) in new[]
            {
                (Path.Combine(assetsDir, binName), $"gamerelay/{binName}"),
                (Path.Combine(assetsDir, "install.sh"), "gamerelay/install.sh"),
                (Path.Combine(assetsDir, "gamerelay.service"), "gamerelay/gamerelay.service"),
            })
            {
                ct.ThrowIfCancellationRequested();
                log($"uploading {Path.GetFileName(local)}…");
                // Normalise CRLF→LF on the shell script so bash is happy.
                if (local.EndsWith(".sh") || local.EndsWith(".service"))
                {
                    string text = File.ReadAllText(local).Replace("\r\n", "\n");
                    using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
                    sftp.UploadFile(ms, remote, canOverride: true);
                }
                else
                {
                    using var fs = File.OpenRead(local);
                    sftp.UploadFile(fs, remote, canOverride: true);
                }
            }
            sftp.Disconnect();
        }

        // 3. Run the installer (non-interactive; Oracle images allow passwordless sudo).
        log("running installer (this sets up the systemd service)…");
        Run(ssh, "cd ~/gamerelay && sudo bash install.sh", log, echo: true);

        // 4. Open the host firewall idempotently and persist it.
        log("opening the host firewall…");
        Run(ssh,
            "sudo iptables -C INPUT -p tcp --dport 1024:65535 -j ACCEPT 2>/dev/null || " +
            "sudo iptables -I INPUT -p tcp --dport 1024:65535 -j ACCEPT; " +
            "sudo iptables -C INPUT -p udp --dport 1024:65535 -j ACCEPT 2>/dev/null || " +
            "sudo iptables -I INPUT -p udp --dport 1024:65535 -j ACCEPT; " +
            "sudo DEBIAN_FRONTEND=noninteractive apt-get install -y iptables-persistent >/dev/null 2>&1 || true; " +
            "sudo netfilter-persistent save >/dev/null 2>&1 || true; echo firewall-ok",
            log, echo: false);

        // 5. Read back the generated secret.
        log("reading the generated shared secret…");
        string configJson = Run(ssh, "sudo cat /opt/gamerelay/config.json", log, echo: false);
        string? secret = null;
        int port = controlPort;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.TryGetProperty("secret", out var s)) secret = s.GetString();
            if (doc.RootElement.TryGetProperty("control_port", out var p)) port = p.GetInt32();
        }
        catch { /* fall through to the error below */ }

        ssh.Disconnect();

        if (string.IsNullOrWhiteSpace(secret))
            return new ProvisionResult { Success = false, Error = "installed, but could not read the secret back from the server" };

        log("done — server is installed and running.");
        return new ProvisionResult { Success = true, Secret = secret, ControlPort = port };
    }

    /// <summary>Runs a command, streams non-empty output lines, throws on non-zero exit.</summary>
    private static string Run(SshClient ssh, string command, Action<string> log, bool echo)
    {
        using var cmd = ssh.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromMinutes(3);
        string output = cmd.Execute();
        if (echo)
            foreach (var l in output.Split('\n'))
                if (l.Trim().Length > 0) log($"  {l.TrimEnd()}");
        if (cmd.ExitStatus != 0)
        {
            string err = cmd.Error.Trim();
            throw new InvalidOperationException(
                $"remote command failed (exit {cmd.ExitStatus}): {(err.Length > 0 ? err : command)}");
        }
        return output;
    }
}
