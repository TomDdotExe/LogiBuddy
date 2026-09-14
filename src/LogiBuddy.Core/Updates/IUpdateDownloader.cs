namespace LogiBuddy.Core.Updates;

public interface IUpdateDownloader
{
    /// Downloads url's response body to destinationPath, overwriting
    /// whatever is there. Throws (HttpRequestException, IOException) on
    /// failure rather than leaving a partial file — callers should treat any
    /// throw as "download failed, nothing to clean up".
    Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default);
}
