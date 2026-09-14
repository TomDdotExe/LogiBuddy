namespace LogiBuddy.Core.Updates;

/// A newer release found by IUpdateChecker. InstallerAssetUrl is the
/// download URL for the release's *-setup.exe asset, or null if the release
/// has no such asset (e.g. a zip-only release) — callers fall back to
/// HtmlUrl (opening the release page) in that case.
public record UpdateInfo(string Version, string ReleaseNotes, string HtmlUrl, string? InstallerAssetUrl);
