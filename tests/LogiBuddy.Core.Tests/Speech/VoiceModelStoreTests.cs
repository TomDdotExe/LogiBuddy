using LogiBuddy.Core.Speech;
using Xunit;

namespace LogiBuddy.Core.Tests.Speech;

public class VoiceModelStoreTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sgr-voice-model-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void IsDownloaded_FalseWhenFileMissing()
    {
        var store = new VoiceModelStore(NewTempDir(), expectedSha256: "deadbeef", download: (_, _) => Task.CompletedTask);
        Assert.False(store.IsDownloaded);
    }

    [Fact]
    public void IsDownloaded_FalseWhenHashDoesNotMatch()
    {
        string dir = NewTempDir();
        var store = new VoiceModelStore(dir, expectedSha256: "0000000000000000000000000000000000000000000000000000000000000000", download: (_, _) => Task.CompletedTask);
        File.WriteAllText(store.ModelPath, "not the real model");

        Assert.False(store.IsDownloaded);
    }

    [Fact]
    public async Task DownloadAsync_WritesToTempThenMovesIntoPlace_AndIsDownloadedBecomesTrue()
    {
        string dir = NewTempDir();
        const string content = "fake model bytes";
        string expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        var store = new VoiceModelStore(dir, expectedHash, download: async (destPath, ct) =>
        {
            await File.WriteAllTextAsync(destPath, content, ct);
        });

        Assert.False(store.IsDownloaded);
        await store.DownloadAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(store.IsDownloaded);
        Assert.Equal(content, await File.ReadAllTextAsync(store.ModelPath));
    }

    [Fact]
    public async Task DownloadAsync_HashMismatch_ThrowsAndDoesNotLeaveFileInPlace()
    {
        string dir = NewTempDir();
        var store = new VoiceModelStore(dir, expectedSha256: "wonthappentomatch", download: async (destPath, ct) =>
        {
            await File.WriteAllTextAsync(destPath, "wrong content", ct);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.DownloadAsync(new Progress<double>(), CancellationToken.None));

        Assert.False(store.IsDownloaded);
        Assert.False(File.Exists(store.ModelPath));
    }
}
