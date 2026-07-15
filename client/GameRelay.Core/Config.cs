using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameRelay.Core;

/// <summary>One tunnel definition as persisted to disk.</summary>
public sealed class TunnelConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>"tcp" or "udp".</summary>
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "tcp";
    [JsonPropertyName("public_port")] public int PublicPort { get; set; }
    [JsonPropertyName("local_host")] public string LocalHost { get; set; } = "127.0.0.1";
    [JsonPropertyName("local_port")] public int LocalPort { get; set; }
    /// <summary>Whether the tunnel should be opened when connected.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    public TunnelConfig Clone() => (TunnelConfig)MemberwiseClone();
}

/// <summary>Client application configuration.</summary>
public sealed class AppConfig
{
    [JsonPropertyName("server_host")] public string ServerHost { get; set; } = "";
    [JsonPropertyName("control_port")] public int ControlPort { get; set; } = 7000;
    [JsonPropertyName("secret")] public string Secret { get; set; } = "";
    [JsonPropertyName("auto_connect")] public bool AutoConnect { get; set; } = true;
    [JsonPropertyName("tunnels")] public List<TunnelConfig> Tunnels { get; set; } = [];
}

/// <summary>Result of loading the config file.</summary>
public sealed class ConfigLoadResult
{
    public required AppConfig Config { get; init; }
    /// <summary>True when config data exists but none of the copies could be read.</summary>
    public bool LoadFailed { get; init; }
    /// <summary>True when the primary copy failed and the backup was used.</summary>
    public bool RestoredFromBackup { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Loads and saves AppConfig redundantly: primary copy in %APPDATA%\GameRelay
/// and a backup in %LOCALAPPDATA%\GameRelay. Every startup is traced to
/// startup.log so field problems can be diagnosed.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Resolves a special folder robustly. On Linux GetFolderPath returns ""
    /// when the underlying directory (e.g. ~/.config) doesn't exist yet, so
    /// ask it to create the directory and fall back to $HOME if needed.
    /// </summary>
    private static string BaseDir(Environment.SpecialFolder sf, string unixFallback)
    {
        string p;
        try { p = Environment.GetFolderPath(sf, Environment.SpecialFolderOption.Create); }
        catch { p = Environment.GetFolderPath(sf); }
        if (!string.IsNullOrEmpty(p)) return p;
        string home = Environment.GetEnvironmentVariable("HOME")
                      ?? Environment.GetEnvironmentVariable("USERPROFILE")
                      ?? AppContext.BaseDirectory;
        return Path.Combine(home, unixFallback);
    }

    public static string ConfigDir =>
        Path.Combine(BaseDir(Environment.SpecialFolder.ApplicationData, ".config"), "GameRelay");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static string LocalDir =>
        Path.Combine(BaseDir(Environment.SpecialFolder.LocalApplicationData, ".local/share"), "GameRelay");

    public static string BackupPath => Path.Combine(LocalDir, "config.backup.json");

    public static string DiagnosticLogPath => Path.Combine(LocalDir, "startup.log");

    public static ConfigLoadResult Load()
    {
        var diag = new System.Text.StringBuilder();
        diag.AppendLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} exe={Environment.ProcessPath}");
        diag.AppendLine($"primary: {ConfigPath} exists={File.Exists(ConfigPath)}");
        diag.AppendLine($"backup:  {BackupPath} exists={File.Exists(BackupPath)}");

        bool anyCopyExists = File.Exists(ConfigPath) || File.Exists(BackupPath);
        if (!anyCopyExists)
        {
            diag.AppendLine("result: no config anywhere -> first run");
            WriteDiagnostics(diag);
            return new ConfigLoadResult { Config = new AppConfig() };
        }

        // Primary, with generous retries: antivirus/indexer can hold the
        // file for a while right when the user double-clicks the app.
        var primary = TryRead(ConfigPath, maxAttempts: 10, retryDelayMs: 300, diag);
        if (primary is not null)
        {
            diag.AppendLine("result: loaded primary");
            WriteDiagnostics(diag);
            EnsureBackup(primary);
            return new ConfigLoadResult { Config = primary };
        }

        // Primary unreadable — fall back to the backup copy.
        var backup = TryRead(BackupPath, maxAttempts: 3, retryDelayMs: 200, diag);
        if (backup is not null)
        {
            diag.AppendLine("result: loaded BACKUP (primary unreadable) — restoring primary");
            WriteDiagnostics(diag);
            try { Save(backup); } catch { /* primary still locked; next run heals it */ }
            return new ConfigLoadResult { Config = backup, RestoredFromBackup = true };
        }

        diag.AppendLine("result: FAILED — both copies unreadable");
        WriteDiagnostics(diag);
        return new ConfigLoadResult
        {
            Config = new AppConfig(),
            LoadFailed = true,
            Error = "both the settings file and its backup could not be read (see startup.log)",
        };
    }

    private static AppConfig? TryRead(string path, int maxAttempts, int retryDelayMs,
        System.Text.StringBuilder diag)
    {
        if (!File.Exists(path)) return null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
                if (cfg is null) throw new InvalidDataException("file deserialized to null");
                diag.AppendLine($"  read ok: {path} (attempt {attempt})");
                return cfg;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < maxAttempts)
            {
                diag.AppendLine($"  attempt {attempt}: {ex.GetType().Name}: {ex.Message} — retrying");
                Thread.Sleep(retryDelayMs);
            }
            catch (System.Text.Json.JsonException ex)
            {
                diag.AppendLine($"  corrupt json in {path}: {ex.Message}");
                try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
                return null;
            }
            catch (Exception ex)
            {
                diag.AppendLine($"  attempt {attempt}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
        return null;
    }

    /// <summary>Writes the config to both locations (each one atomically).</summary>
    public static void Save(AppConfig config)
    {
        string json = JsonSerializer.Serialize(config, JsonOpts);
        WriteAtomic(ConfigDir, ConfigPath, json);
        try { WriteAtomic(LocalDir, BackupPath, json); }
        catch { /* backup is best-effort */ }
    }

    private static void EnsureBackup(AppConfig config)
    {
        try
        {
            if (!File.Exists(BackupPath))
                WriteAtomic(LocalDir, BackupPath, JsonSerializer.Serialize(config, JsonOpts));
        }
        catch { }
    }

    private static void WriteAtomic(string dir, string path, string content)
    {
        Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static void WriteDiagnostics(System.Text.StringBuilder diag)
    {
        try
        {
            Directory.CreateDirectory(LocalDir);
            // Keep the log from growing without bound.
            if (File.Exists(DiagnosticLogPath) && new FileInfo(DiagnosticLogPath).Length > 256 * 1024)
                File.Delete(DiagnosticLogPath);
            File.AppendAllText(DiagnosticLogPath, diag.ToString());
        }
        catch { }
    }
}
