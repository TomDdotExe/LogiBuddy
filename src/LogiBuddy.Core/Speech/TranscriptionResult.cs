namespace LogiBuddy.Core.Speech;

/// Text is "" if no speech was detected. Confidence is the average
/// segment-level probability Whisper reports for the transcription — 1
/// when there were no segments (nothing to be unsure about), otherwise in
/// [0, 1]. Meant for a coarse "trust this?" signal to the user, not a
/// precise accuracy metric.
public sealed record TranscriptionResult(string Text, float Confidence);
