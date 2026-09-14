namespace LogiBuddy.Core.Updates;

/// A newer release found by IUpdateChecker. InstallerAssetUrl is the
/// download URL for the release's *-setup.exe asset — validated to be an
/// https://github.com/... URL, never blindly trusted from the API response —
/// or null if the release has no such asset (e.g. a zip-only release) or the
/// asset's URL failed that validation; callers fall back to HtmlUrl (opening
/// the release page) in that case. InstallerAssetSha256 is the asset's
/// published sha256 digest (lowercase hex, no "sha256:" prefix) for the
/// downloader to verify against before executing anything, or null if GitHub
/// didn't publish one (older assets) or published a non-sha256 digest.
public record UpdateInfo(string Version, string ReleaseNotes, string HtmlUrl, string? InstallerAssetUrl, string? InstallerAssetSha256);
