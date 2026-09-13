namespace SpotifyGameRadio.Core.Updates;

public interface IUpdateChecker
{
    /// Returns the latest release's info if it's newer than currentVersion,
    /// or null if there's definitively no newer release (already up to date,
    /// or the repo has no releases published yet). Anything else that goes
    /// wrong (offline, rate-limited, malformed response) throws — callers
    /// decide whether to swallow that (a silent startup check) or report it
    /// (a manual "Check for Updates" click).
    Task<UpdateInfo?> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default);
}
