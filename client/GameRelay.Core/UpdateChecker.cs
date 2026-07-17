using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GameRelay.Core;

/// <summary>An available update discovered on GitHub Releases.</summary>
public sealed record UpdateInfo(string Version, string Tag, string HtmlUrl, string? Notes);

/// <summary>
/// Checks the project's GitHub Releases for a newer version than the running
/// one. No dependencies, no token — the public releases API is enough.
/// </summary>
public static class UpdateChecker
{
    private const string LatestUrl = "https://api.github.com/repos/NexRelay/GameRelay/releases/latest";

    /// <summary>
    /// Returns update info if the latest published release is newer than
    /// <paramref name="current"/>, otherwise null. Never throws.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GameRelay", current.ToString()));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            string json = await http.GetStringAsync(LatestUrl, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                return null;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseTag(tag, out var latest)) return null;
            if (latest <= current) return null;

            return new UpdateInfo(
                latest.ToString(),
                tag,
                root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "",
                root.TryGetProperty("body", out var b) ? b.GetString() : null);
        }
        catch
        {
            return null; // offline, rate-limited, or malformed — never bother the user
        }
    }

    /// <summary>Parses "v1.4.0" / "1.4" into a Version.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        string s = tag.TrimStart('v', 'V').Trim();
        // Version.Parse needs at least major.minor.
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out version!);
    }
}
