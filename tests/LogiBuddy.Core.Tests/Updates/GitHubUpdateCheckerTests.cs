using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using LogiBuddy.Core.Updates;
using Xunit;

namespace LogiBuddy.Core.Tests.Updates;

public class GitHubUpdateCheckerTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string? _body;
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHandler(HttpStatusCode status, string? body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(_status);
            if (_body is not null) response.Content = new StringContent(_body, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }

    private static string ReleaseJson(string tag, string body = "notes", string url = "https://github.com/x/y/releases/tag/v1", string? assetsJson = null) =>
        $$"""{ "tag_name": "{{tag}}", "body": "{{body}}", "html_url": "{{url}}"{{(assetsJson is null ? "" : $", \"assets\": {assetsJson}")}} }""";

    private static string AssetsJson(params (string name, string url, string? digest)[] assets) =>
        "[" + string.Join(",", assets.Select(a =>
            $$"""{ "name": "{{a.name}}", "browser_download_url": "{{a.url}}"{{(a.digest is null ? "" : $", \"digest\": \"{a.digest}\"")}} }""")) + "]";

    [Theory]
    [InlineData("0.4.0", "v0.5.0", true)]
    [InlineData("0.4.0", "0.5.0", true)]
    [InlineData("0.4.0", "v0.4.0", false)]
    [InlineData("0.5.0", "v0.4.0", false)]
    [InlineData("1", "v1.1", true)]
    [InlineData("0.4.0", "v0.4.1-rc1", true)]
    [InlineData("0.4.0", "not-a-version", false)]
    public void IsNewer_ComparesSemverStyleTags(string current, string latestTag, bool expected)
    {
        Assert.Equal(expected, GitHubUpdateChecker.IsNewer(current, latestTag));
    }

    [Fact]
    public async Task CheckForUpdateAsync_NewerReleasePublished_ReturnsUpdateInfo()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson("v0.5.0", "Patch notes here", "https://github.com/x/y/releases/tag/v0.5.0"));
        var checker = new GitHubUpdateChecker("x", "y", handler);

        var result = await checker.CheckForUpdateAsync("0.4.0");

        Assert.NotNull(result);
        Assert.Equal("0.5.0", result!.Version);
        Assert.Equal("Patch notes here", result.ReleaseNotes);
        Assert.Equal("https://github.com/x/y/releases/tag/v0.5.0", result.HtmlUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_UpToDate_ReturnsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson("v0.4.0"));
        var checker = new GitHubUpdateChecker("x", "y", handler);

        var result = await checker.CheckForUpdateAsync("0.4.0");

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoReleasesYet_ReturnsNullInsteadOfThrowing()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound, null);
        var checker = new GitHubUpdateChecker("x", "y", handler);

        var result = await checker.CheckForUpdateAsync("0.4.0");

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NetworkFailure_Throws()
    {
        var checker = new GitHubUpdateChecker("x", "y", new ThrowingHandler());

        await Assert.ThrowsAsync<HttpRequestException>(() => checker.CheckForUpdateAsync("0.4.0"));
    }

    [Fact]
    public async Task CheckForUpdateAsync_ServerError_Throws()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, null);
        var checker = new GitHubUpdateChecker("x", "y", handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => checker.CheckForUpdateAsync("0.4.0"));
    }

    [Fact]
    public async Task CheckForUpdateAsync_SetupExeAssetPresent_PopulatesInstallerAssetUrlAndDigest()
    {
        var assets = AssetsJson(
            ("LogiBuddy-v0.5.0-win-x64.zip", "https://github.com/x/y/releases/download/v0.5.0/LogiBuddy-v0.5.0-win-x64.zip", "sha256:zip"),
            ("LogiBuddy-v0.5.0-win-x64-setup.exe", "https://github.com/x/y/releases/download/v0.5.0/LogiBuddy-v0.5.0-win-x64-setup.exe", "sha256:abc123"));
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson("v0.5.0", assetsJson: assets));
        var checker = new GitHubUpdateChecker("x", "y", handler);

        var result = await checker.CheckForUpdateAsync("0.4.0");

        Assert.NotNull(result);
        Assert.Equal("https://github.com/x/y/releases/download/v0.5.0/LogiBuddy-v0.5.0-win-x64-setup.exe", result!.InstallerAssetUrl);
        Assert.Equal("abc123", result.InstallerAssetSha256);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoSetupExeAsset_InstallerAssetUrlIsNull()
    {
        var assets = AssetsJson(("LogiBuddy-v0.5.0-win-x64.zip", "https://github.com/x/y/releases/download/v0.5.0/LogiBuddy-v0.5.0-win-x64.zip", null));
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson("v0.5.0", assetsJson: assets));
        var checker = new GitHubUpdateChecker("x", "y", handler);

        var result = await checker.CheckForUpdateAsync("0.4.0");

        Assert.NotNull(result);
        Assert.Null(result!.InstallerAssetUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoAssetsField_InstallerAssetUrlIsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson("v0.5.0"));
        var checker = new GitHubUpdateChecker("x", "y", handler);

        var result = await checker.CheckForUpdateAsync("0.4.0");

        Assert.NotNull(result);
        Assert.Null(result!.InstallerAssetUrl);
    }

    [Theory]
    [InlineData("http://github.com/x/y/releases/download/v0.5.0/setup.exe")] // not https
    [InlineData("https://evil.example.com/x/y/releases/download/v0.5.0/setup.exe")] // not github.com
    [InlineData("https://github.com.evil.com/setup.exe")] // lookalike host, not an actual github.com subdomain match
    [InlineData("not a url at all")]
    public async Task CheckForUpdateAsync_AssetUrlNotHttpsGithub_IsRejected(string spoofedUrl)
    {
        var assets = AssetsJson(("LogiBuddy-v0.5.0-win-x64-setup.exe", spoofedUrl, "sha256:abc123"));
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson("v0.5.0", assetsJson: assets));
        var checker = new GitHubUpdateChecker("x", "y", handler);

        var result = await checker.CheckForUpdateAsync("0.4.0");

        Assert.NotNull(result);
        Assert.Null(result!.InstallerAssetUrl);
        Assert.Null(result.InstallerAssetSha256);
    }

    [Fact]
    public async Task CheckForUpdateAsync_DigestNotSha256Prefixed_IsIgnored()
    {
        var assets = AssetsJson(("LogiBuddy-v0.5.0-win-x64-setup.exe",
            "https://github.com/x/y/releases/download/v0.5.0/setup.exe", "md5:abc123"));
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson("v0.5.0", assetsJson: assets));
        var checker = new GitHubUpdateChecker("x", "y", handler);

        var result = await checker.CheckForUpdateAsync("0.4.0");

        Assert.NotNull(result);
        Assert.NotNull(result!.InstallerAssetUrl); // URL is still valid and usable
        Assert.Null(result.InstallerAssetSha256); // but the unverifiable digest is dropped, not trusted blindly
    }

    [Fact]
    public async Task CheckForUpdateAsync_SendsUserAgentHeader()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson("v0.4.0"));
        var checker = new GitHubUpdateChecker("x", "y", handler);

        await checker.CheckForUpdateAsync("0.4.0");

        Assert.NotNull(handler.LastRequest);
        Assert.NotEmpty(handler.LastRequest!.Headers.UserAgent);
    }
}
