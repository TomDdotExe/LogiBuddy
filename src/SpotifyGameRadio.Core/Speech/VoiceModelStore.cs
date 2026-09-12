using System.Security.Cryptography;
using System.Net.Http;
using System.IO;

namespace SpotifyGameRadio.Core.Speech;

/// Manages the cached Whisper model file: knows where it lives, whether a
/// valid copy is present, and how to fetch one. Downloaded on first use
/// rather than bundled in the release zip — see the design spec's
/// "Model distribution" decision.
public class VoiceModelStore
{
    public const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin";
    public const string ModelFileName = "ggml-small.en.bin";

    // Verified 2026-09-12 against a fresh download of the pinned URL above
    // (487,614,201 bytes). Re-verify if ModelUrl ever changes.
    public const string ModelSha256 = "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d";

    private readonly string _expectedSha256;
    private readonly Func<string, CancellationToken, Task> _download;

    // IsDownloaded streams and SHA256-hashes the whole (~488 MB) cached model
    // file, which is far too slow to redo on every read (it's read on every
    // record-hotkey press, plus twice more at window construction via XAML
    // bindings). Cached after the first real check so at most one full hash
    // pass happens per process lifetime; DownloadAsync updates it directly on
    // success so a fresh download is reflected immediately.
    private bool? _isDownloadedCache;

    public string ModelPath { get; }

    /// Real pinned model/hash, real HTTP download.
    public VoiceModelStore()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyGameRadio", "models"),
            ModelSha256,
            DownloadWithHttpClient)
    {
    }

    /// Test/advanced constructor: inject the cache directory, expected hash,
    /// and download delegate so tests never touch the network or the pinned
    /// 488 MB file.
    public VoiceModelStore(string modelDirectory, string expectedSha256, Func<string, CancellationToken, Task> download)
    {
        Directory.CreateDirectory(modelDirectory);
        ModelPath = Path.Combine(modelDirectory, ModelFileName);
        _expectedSha256 = expectedSha256;
        _download = download;
    }

    public bool IsDownloaded
    {
        get
        {
            _isDownloadedCache ??= ComputeIsDownloaded();
            return _isDownloadedCache.Value;
        }
    }

    private bool ComputeIsDownloaded()
    {
        if (!File.Exists(ModelPath)) return false;
        using var stream = File.OpenRead(ModelPath);
        string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return actual == _expectedSha256.ToLowerInvariant();
    }

    public async Task DownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        string tempPath = ModelPath + ".part";
        try
        {
            await _download(tempPath, ct);

            using (var stream = File.OpenRead(tempPath))
            {
                string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (actual != _expectedSha256.ToLowerInvariant())
                    throw new InvalidOperationException(
                        $"Downloaded voice model hash mismatch (expected {_expectedSha256}, got {actual}).");
            }

            File.Move(tempPath, ModelPath, overwrite: true);
            _isDownloadedCache = true;
            progress.Report(1.0);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static async Task DownloadWithHttpClient(string destPath, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destPath);
        await source.CopyToAsync(dest, ct);
    }
}
