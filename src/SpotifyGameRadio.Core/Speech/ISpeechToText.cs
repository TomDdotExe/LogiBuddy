namespace SpotifyGameRadio.Core.Speech;

public interface ISpeechToText
{
    /// Transcribes 16 kHz mono float32 PCM. vocabularyHint (from
    /// VocabularyPromptBuilder.Build) biases recognition toward it; pass ""
    /// for no hint. Returns "" if no speech was detected.
    Task<string> TranscribeAsync(float[] pcm16kMono, string vocabularyHint, CancellationToken ct);
}
