using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace AmpTray;

public sealed record UpdateInfo(Version Version, string Tag, string Url, string Name);

/// <summary>
/// Asks GitHub for the latest (non-prerelease) release of this project and says whether it is newer
/// than the running build. One anonymous GET to api.github.com per call; nothing is sent about the PC.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "borexola/AmpTray";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static Version Current => Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            // AMPTRAY_UPDATE_REPO lets a developer point the check at another repository to exercise the banner.
            string repo = Environment.GetEnvironmentVariable("AMPTRAY_UPDATE_REPO") is { Length: > 0 } r ? r : Repo;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd($"AmpTray/{Current}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
            if (tag.Length == 0 || url.Length == 0 || !TryParseVersion(tag, out var version)) return null;

            return version > Current ? new UpdateInfo(version, tag, url, string.IsNullOrWhiteSpace(name) ? tag : name) : null;
        }
        catch
        {
            // Offline, rate-limited or odd JSON: no update banner this time.
            return null;
        }
    }

    /// <summary>"v1.2.3", "1.2", "v1.2.3-beta" → 1.2.3.0 (prerelease suffix ignored).</summary>
    internal static bool TryParseVersion(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        string s = tag.Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (dash >= 0) s = s[..dash];
        if (s.Count(c => c == '.') == 0) s += ".0";
        if (!Version.TryParse(s, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
