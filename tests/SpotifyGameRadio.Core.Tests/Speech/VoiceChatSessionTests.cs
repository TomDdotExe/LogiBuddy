using SpotifyGameRadio.Core.Audio;
using SpotifyGameRadio.Core.Speech;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Speech;

public class FakeMicrophoneCapture : IMicrophoneCapture
{
    public float[] NextStopResult = new float[16000]; // 1 second of "speech" by default
    public bool Started { get; private set; }

    public void Start(string? deviceId) => Started = true;
    public float[] Stop() { Started = false; return NextStopResult; }
}

public class FakeSpeechToText : ISpeechToText
{
    public string Result = "dropping fuel supplies on tower 2 fob";
    public Exception? ThrowOnTranscribe;
    public string? LastVocabularyHint;

    // When set, TranscribeAsync returns this TCS's Task instead of an
    // already-completed one, letting a test control exactly when
    // transcription "completes" and observe genuinely in-flight state.
    public TaskCompletionSource<string>? PendingTranscription;

    public Task<string> TranscribeAsync(float[] pcm16kMono, string vocabularyHint, CancellationToken ct)
    {
        LastVocabularyHint = vocabularyHint;
        if (PendingTranscription is not null) return PendingTranscription.Task;
        if (ThrowOnTranscribe is not null) return Task.FromException<string>(ThrowOnTranscribe);
        return Task.FromResult(Result);
    }
}

public class VoiceChatSessionTests
{
    private static VoiceChatSession CreateSession(
        FakeMicrophoneCapture mic, FakeSpeechToText stt, out List<VoiceChatState> states, out List<string> previews, out List<string> failures)
    {
        var session = new VoiceChatSession(mic, stt, () => "");
        var capturedStates = new List<VoiceChatState>();
        var capturedPreviews = new List<string>();
        var capturedFailures = new List<string>();
        session.StateChanged += s => capturedStates.Add(s);
        session.PreviewReady += t => capturedPreviews.Add(t);
        session.Failed += r => capturedFailures.Add(r);
        states = capturedStates;
        previews = capturedPreviews;
        failures = capturedFailures;
        return session;
    }

    [Fact]
    public void HappyPath_RecordingThenPreviewReady()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText();
        using var session = CreateSession(mic, stt, out var states, out var previews, out _);

        session.BeginRecording();
        Assert.Equal(VoiceChatState.Recording, session.State);
        Assert.True(mic.Started);

        session.EndRecording();
        // Transcription is awaited synchronously inside EndRecording for a
        // fake ISpeechToText that completes immediately (Task.FromResult), so
        // by the time EndRecording returns the session has already reached
        // PreviewReady.
        Assert.Equal(VoiceChatState.PreviewReady, session.State);
        Assert.Equal("dropping fuel supplies on tower 2 fob", session.Transcript);
        Assert.Contains(VoiceChatState.Recording, states);
        Assert.Contains(VoiceChatState.Transcribing, states);
        Assert.Contains(VoiceChatState.PreviewReady, states);
        Assert.Equal(new[] { "dropping fuel supplies on tower 2 fob" }, previews);
    }

    [Fact]
    public void EndRecording_BufferTooShort_ReturnsToIdleWithoutTranscribing()
    {
        var mic = new FakeMicrophoneCapture { NextStopResult = new float[100] }; // well under 200ms @ 16kHz
        var stt = new FakeSpeechToText();
        using var session = CreateSession(mic, stt, out _, out var previews, out var failures);

        session.BeginRecording();
        session.EndRecording();

        Assert.Equal(VoiceChatState.Idle, session.State);
        Assert.Empty(previews);
        Assert.Empty(failures);
        Assert.Null(stt.LastVocabularyHint); // TranscribeAsync was never called
    }

    [Fact]
    public void EndRecording_EmptyTranscript_RaisesFailedAndReturnsToIdle()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText { Result = "   " };
        using var session = CreateSession(mic, stt, out _, out var previews, out var failures);

        session.BeginRecording();
        session.EndRecording();

        Assert.Equal(VoiceChatState.Idle, session.State);
        Assert.Empty(previews);
        Assert.Single(failures);
    }

    [Fact]
    public void EndRecording_TranscriptionThrows_RaisesFailedAndReturnsToIdle()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText { ThrowOnTranscribe = new InvalidOperationException("model not loaded") };
        using var session = CreateSession(mic, stt, out _, out _, out var failures);

        session.BeginRecording();
        session.EndRecording();

        Assert.Equal(VoiceChatState.Idle, session.State);
        Assert.Single(failures);
        Assert.Contains("model not loaded", failures[0]);
    }

    [Fact]
    public void Discard_FromPreviewReady_ReturnsToIdleAndClearsTranscript()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText();
        using var session = CreateSession(mic, stt, out _, out _, out _);

        session.BeginRecording();
        session.EndRecording();
        Assert.Equal(VoiceChatState.PreviewReady, session.State);

        session.Discard();

        Assert.Equal(VoiceChatState.Idle, session.State);
        Assert.Null(session.Transcript);
    }

    [Fact]
    public void BeginRecording_WhilePreviewReady_ClearsOldTranscriptAndStartsOver()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText();
        using var session = CreateSession(mic, stt, out _, out _, out _);

        session.BeginRecording();
        session.EndRecording();
        Assert.Equal(VoiceChatState.PreviewReady, session.State);

        session.BeginRecording();

        Assert.Equal(VoiceChatState.Recording, session.State);
        Assert.Null(session.Transcript);
    }

    [Fact]
    public async Task Dispose_DuringTranscription_SuppressesLaterEvents()
    {
        var mic = new FakeMicrophoneCapture();
        var tcs = new TaskCompletionSource<string>();
        var stt = new FakeSpeechToText { PendingTranscription = tcs };
        var session = CreateSession(mic, stt, out _, out var previews, out var failures);

        session.BeginRecording();
        session.EndRecording();

        // The fake's TranscribeAsync returns a not-yet-completed task, so the
        // transcription is genuinely in-flight at this point (unlike every
        // other test in this file, which uses an already-completed fake
        // task and observes the continuation having already run).
        Assert.Equal(VoiceChatState.Transcribing, session.State);

        session.Dispose();

        // Complete the in-flight transcription after Dispose(). If the
        // ct.IsCancellationRequested guard in RunTranscription works as
        // intended, this stale result must not produce a PreviewReady/Failed
        // event.
        tcs.SetResult("some transcript");

        // Give the "async void" continuation a chance to run in case it
        // isn't inlined synchronously by SetResult on this thread. A short
        // delay is the simplest deterministic-enough mechanism available
        // here: there's no test hook into the continuation, and the delay
        // only needs to be "long enough for a already-scheduled continuation
        // to run", not "long enough to wait for real work".
        await Task.Delay(50);

        Assert.Empty(previews);
        Assert.Empty(failures);
    }

    [Fact]
    public void Dispose_IsSafeToCallTwice()
    {
        var session = new VoiceChatSession(new FakeMicrophoneCapture(), new FakeSpeechToText(), () => "");
        session.Dispose();
        session.Dispose(); // must not throw
    }
}
