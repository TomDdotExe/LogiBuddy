using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace LogiBuddy.Core.Updates;

public class HttpUpdateDownloader : IUpdateDownloader
{
    private readonly HttpClient _http;

    public HttpUpdateDownloader(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
    }

    public async Task DownloadAsync(string url, string destinationPath, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        // Downloads to a temp file alongside the destination first, then
        // moves it into place — a failed/cancelled/hash-mismatched download
        // never leaves a partial or stale file sitting at destinationPath,
        // and never overwrites a previously-good download there either.
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

            if (expectedSha256 is not null)
            {
                var actualSha256 = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
                if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Downloaded file's SHA256 ({actualSha256}) did not match the published digest ({expectedSha256}).");
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
