namespace LogiBuddy.Core.Updates;

public interface IUpdateDownloader
{
    /// Downloads url's response body to destinationPath, overwriting
    /// whatever is there. When expectedSha256 is given (lowercase hex, no
    /// "sha256:" prefix), the download is hashed and compared before it's
    /// moved into place — a mismatch throws InvalidOperationException and
    /// destinationPath is left untouched, same as any other failure. Throws
    /// (HttpRequestException, IOException, InvalidOperationException) on
    /// failure rather than leaving a partial file — callers should treat any
    /// throw as "download failed, nothing to clean up, do not execute
    /// anything at destinationPath".
    Task DownloadAsync(string url, string destinationPath, string? expectedSha256 = null, CancellationToken cancellationToken = default);
}
