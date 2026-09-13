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

    public async Task<TranscriptionResult> TranscribeAsync(float[] pcm16kMono, IReadOnlyList<string> vocabularyTerms, CancellationToken ct)
    {
        // Beam search was tried here and reverted: A/B testing against the
        // real model (clean audio, silence-padded, and noise-overlaid at
        // three levels) never once produced a better transcript than the
        // default greedy decoding, so there's no evidenced reason to pay its
        // extra CPU cost.
        //
        // WithPrompt is marked [EXPERIMENTAL] by Whisper.net as of 1.9.1 but
        // is the documented way to bias recognition toward a term list; an
        // empty string is a harmless no-op hint. The temperature/entropy/
        // no-speech thresholds below are whisper.cpp's own CLI defaults —
        // set explicitly rather than trusting Whisper.net's unset default,
        // since they're what triggers a retry-at-higher-temperature when a
        // decode looks degenerate (e.g. stuck repeating a word), which is
        // exactly the failure mode a strong vocabulary prompt risks causing.
        using var processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt(VocabularyPromptBuilder.BuildFromTerms(vocabularyTerms))
            .WithTemperatureInc(0.2f)
            .WithLogProbThreshold(-1.0f)
            .WithEntropyThreshold(2.4f)
            .WithNoSpeechThreshold(0.6f)
            .WithProbabilities()
            .Build();

        var segments = new List<VocabularyCorrector.Segment>();
        await foreach (var segment in processor.ProcessAsync(pcm16kMono, ct))
            segments.Add(new VocabularyCorrector.Segment(segment.Text, segment.Probability));

        string text = TranscriptSanitizer.TrimLeadingSymbols(VocabularyCorrector.Correct(segments, vocabularyTerms).Trim());
        float confidence = segments.Count == 0 ? 1f : segments.Average(s => s.Probability);
        return new TranscriptionResult(text, confidence);
    }

    public void Dispose() => _factory.Dispose();
}
