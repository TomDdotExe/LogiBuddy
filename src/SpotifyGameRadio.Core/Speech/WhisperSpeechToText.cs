using System.Text;
using Whisper.net;

namespace SpotifyGameRadio.Core.Speech;

/// Wraps a Whisper.net model file for one-shot transcriptions. One instance
/// per model path; safe to reuse across many TranscribeAsync calls
/// (WhisperFactory itself is the expensive part to construct).
public sealed class WhisperSpeechToText : ISpeechToText, IDisposable
{
    private readonly WhisperFactory _factory;

    public WhisperSpeechToText(string modelPath)
    {
        _factory = WhisperFactory.FromPath(modelPath);
    }

    public async Task<string> TranscribeAsync(float[] pcm16kMono, string vocabularyHint, CancellationToken ct)
    {
        // WithPrompt is marked [EXPERIMENTAL] by Whisper.net as of 1.9.1 but is
        // the documented way to bias recognition toward a term list; an empty
        // string is a harmless no-op hint.
        using var processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt(vocabularyHint)
            .Build();

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(pcm16kMono, ct))
            sb.Append(segment.Text);

        return sb.ToString().Trim();
    }

    public void Dispose() => _factory.Dispose();
}
