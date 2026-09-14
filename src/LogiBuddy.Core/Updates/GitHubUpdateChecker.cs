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
        var (installerAssetUrl, installerAssetSha256) = FindInstallerAsset(root);
        return new UpdateInfo(tagName.TrimStart('v', 'V'), notes, htmlUrl, installerAssetUrl, installerAssetSha256);
    }

    /// Finds the release asset produced by build/make-installer.ps1
    /// (OutputBaseFilename ends "-setup") and returns its download URL and
    /// sha256 digest. Both come back null if there's no such asset (e.g.
    /// zip-only release) or its browser_download_url fails IsTrustedAssetUrl
    /// — this app is about to execute whatever that URL points to, so an
    /// untrusted host or non-https URL is treated the same as "no asset
    /// found" rather than passed through. The digest is separately dropped
    /// (URL still returned) if it isn't sha256, since HttpUpdateDownloader
    /// can only verify that algorithm.
    private static (string? url, string? sha256) FindInstallerAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return (null, null);

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (name is null || !name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
            if (!IsTrustedAssetUrl(url)) return (null, null);

            var digest = asset.TryGetProperty("digest", out var digestProp) ? digestProp.GetString() : null;
            const string sha256Prefix = "sha256:";
            var sha256 = digest is not null && digest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase)
                ? digest[sha256Prefix.Length..]
                : null;

            return (url, sha256);
        }
        return (null, null);
    }

    /// This app is about to download and silently execute whatever
    /// browser_download_url points to — require it to actually be an
    /// absolute https://github.com/... URL (the real, observed shape of a
    /// GitHub release asset URL) rather than trusting the API response's
    /// string verbatim, in case a malformed/tampered response ever points
    /// elsewhere.
    private static bool IsTrustedAssetUrl(string? url) =>
        url is not null
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

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
