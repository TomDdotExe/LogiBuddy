using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LogiBuddy.Core.Updates;

/// Checks a public GitHub repo's latest release against the app's own
/// version. Unauthenticated (works against the GitHub REST API's public
/// rate limit — fine for an occasional startup/manual check).
public class GitHubUpdateChecker : IUpdateChecker
{
    private readonly HttpClient _http;
    private readonly string _repoOwner;
    private readonly string _repoName;

    public GitHubUpdateChecker(string repoOwner, string repoName, HttpMessageHandler? handler = null)
    {
        _repoOwner = repoOwner;
        _repoName = repoName;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // GitHub's API rejects requests with no User-Agent.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LogiBuddy", "1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// Returns null only when there's definitively no newer release (up to
    /// date, or the repo has no releases published yet — a 404 from this
    /// endpoint). Anything else that goes wrong (offline, rate-limited, bad
    /// JSON) throws, so a caller can choose to swallow it (silent startup
    /// check) or report it (manual "Check for Updates" click).
    public async Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases/latest";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no releases published yet
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(tagName)) return null;
        if (!IsNewer(currentVersion, tagName)) return null;

        var notes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
        var htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? "" : "";
        var installerAssetUrl = FindInstallerAssetUrl(root);
        return new UpdateInfo(tagName.TrimStart('v', 'V'), notes, htmlUrl, installerAssetUrl);
    }

    /// Finds the download URL of the release asset produced by
    /// build/make-installer.ps1 (OutputBaseFilename ends "-setup"), or null
    /// if the release has no such asset (e.g. zip-only).
    private static string? FindInstallerAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (name is null || !name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;

            return asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
        }
        return null;
    }

    /// True if latestTag (e.g. "v1.2.0" or "1.2.0") parses to a version
    /// strictly greater than currentVersion. Either side failing to parse is
    /// treated as "not newer" rather than throwing, so a malformed tag can't
    /// produce a false "update available".
    public static bool IsNewer(string currentVersion, string latestTag)
    {
        var latest = latestTag.TrimStart('v', 'V');
        if (!Version.TryParse(NormalizeForVersion(currentVersion), out var current)) return false;
        if (!Version.TryParse(NormalizeForVersion(latest), out var latestVersion)) return false;
        return latestVersion > current;
    }

    /// System.Version requires at least a Major.Minor; pads a bare "1" or
    /// strips a semver pre-release/build suffix ("1.2.0-rc1" -> "1.2.0") so
    /// ordinary release tags parse instead of throwing.
    private static string NormalizeForVersion(string version)
    {
        var core = version.Split('-', '+')[0].Trim();
        return core.Count(c => c == '.') == 0 ? core + ".0" : core;
    }
}
