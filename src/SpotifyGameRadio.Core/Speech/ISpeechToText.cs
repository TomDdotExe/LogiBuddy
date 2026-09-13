namespace SpotifyGameRadio.Core.Speech;

public interface ISpeechToText
{
    /// Transcribes 16 kHz mono float32 PCM. vocabularyTerms (from
    /// VocabularyPromptBuilder.ParseTerms) both biases decoding toward those
    /// terms (as a Whisper prompt) and is used to fuzzy-correct low-confidence
    /// words/phrases in the result against them; pass an empty list for
    /// neither. TranscriptionResult.Text is "" if no speech was detected.
    Task<TranscriptionResult> TranscribeAsync(float[] pcm16kMono, IReadOnlyList<string> vocabularyTerms, CancellationToken ct);
}
