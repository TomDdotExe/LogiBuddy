using System.Net;
using System.Text;
using LogiBuddy.Core.Updates;
using Xunit;

namespace LogiBuddy.Core.Tests.Updates;

public class HttpUpdateDownloaderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[]? _body;
        public FakeHandler(HttpStatusCode status, byte[]? body) { _status = status; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status);
            if (_body is not null) response.Content = new ByteArrayContent(_body);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task DownloadAsync_WritesResponseBodyToDestination()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Encoding.UTF8.GetBytes("fake installer bytes"));
        var downloader = new HttpUpdateDownloader(handler);
        var dest = Path.Combine(Path.GetTempPath(), "HttpUpdateDownloaderTests_" + Guid.NewGuid() + ".exe");

        try
        {
            await downloader.DownloadAsync("https://example.com/setup.exe", dest);

            Assert.True(File.Exists(dest));
            Assert.Equal("fake installer bytes", await File.ReadAllTextAsync(dest));
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadAsync_OverwritesExistingFileAtDestination()
    {
        var dest = Path.Combine(Path.GetTempPath(), "HttpUpdateDownloaderTests_" + Guid.NewGuid() + ".exe");
        await File.WriteAllTextAsync(dest, "stale content from a previous download");
        var handler = new FakeHandler(HttpStatusCode.OK, Encoding.UTF8.GetBytes("new bytes"));
        var downloader = new HttpUpdateDownloader(handler);

        try
        {
            await downloader.DownloadAsync("https://example.com/setup.exe", dest);

            Assert.Equal("new bytes", await File.ReadAllTextAsync(dest));
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }

    [Fact]
    public async Task DownloadAsync_ServerError_ThrowsAndLeavesNoFile()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, null);
        var downloader = new HttpUpdateDownloader(handler);
        var dest = Path.Combine(Path.GetTempPath(), "HttpUpdateDownloaderTests_" + Guid.NewGuid() + ".exe");

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync("https://example.com/setup.exe", dest));
            Assert.False(File.Exists(dest));
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }
}
