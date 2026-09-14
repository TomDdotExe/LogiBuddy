using System.IO;
using System.Net.Http;

namespace LogiBuddy.Core.Updates;

public class HttpUpdateDownloader : IUpdateDownloader
{
    private readonly HttpClient _http;

    public HttpUpdateDownloader(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
    }

    public async Task DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default)
    {
        // Downloads to a temp file alongside the destination first, then
        // moves it into place — a failed/cancelled download never leaves a
        // partial or stale file sitting at destinationPath.
        var tempPath = destinationPath + ".download";
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var fileStream = File.Create(tempPath))
            await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                await responseStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }
}
