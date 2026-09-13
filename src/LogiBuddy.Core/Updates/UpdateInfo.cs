namespace LogiBuddy.Core.Updates;

/// A newer release found by IUpdateChecker.
public record UpdateInfo(string Version, string ReleaseNotes, string HtmlUrl);
