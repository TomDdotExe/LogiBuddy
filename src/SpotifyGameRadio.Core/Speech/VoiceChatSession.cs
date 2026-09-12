using SpotifyGameRadio.Core.Audio;

namespace SpotifyGameRadio.Core.Speech;

public enum VoiceChatState { Idle, Recording, Transcribing, PreviewReady }

/// Push-to-talk record -> transcribe -> preview -> confirm/discard/re-record
/// state machine. Pure aside from its two injected dependencies; unit-tested
/// with fakes. Modelled on CalibrationSession's event-driven shape.
///
/// Threading: this type has no internal locking. Callers must invoke
/// <see cref="BeginRecording"/>, <see cref="EndRecording"/>,
/// <see cref="Discard"/>, and <see cref="Dispose"/> from a single logical
/// thread, and must expect <see cref="StateChanged"/>, <see cref="PreviewReady"/>,
/// and <see cref="Failed"/> to fire on that same thread (e.g. all calls and
/// event handlers marshaled through the same UI dispatcher). The async
/// transcription continuation in particular is only safe to observe from
/// that same logical thread — interleaving calls across threads, or
/// receiving the transcription continuation on a different thread than the
/// one that calls the public methods, is not supported.
public sealed class VoiceChatSession : IDisposable
{
    // Below this many 16kHz mono samples (~200ms) a recording is treated as
    // an accidental brush of the key — no transcription attempt, no preview.
    private const int MinValidSampleCount = 3200;

    private readonly IMicrophoneCapture _mic;
    private readonly ISpeechToText _stt;
    private readonly Func<string> _vocabularyHint;
    private readonly Func<string?> _deviceId;
    private CancellationTokenSource? _transcribeCts;
    private bool _disposed;

    public VoiceChatState State { get; private set; } = VoiceChatState.Idle;
    public string? Transcript { get; private set; }

    public event Action<VoiceChatState>? StateChanged;
    public event Action<string>? PreviewReady;
    public event Action<string>? Failed;

    public VoiceChatSession(IMicrophoneCapture mic, ISpeechToText stt, Func<string> vocabularyHint, Func<string?> deviceId)
    {
        _mic = mic;
        _stt = stt;
        _vocabularyHint = vocabularyHint;
        _deviceId = deviceId;
    }

    public void BeginRecording()
    {
        if (State is VoiceChatState.Recording or VoiceChatState.Transcribing) return;

        Transcript = null;
        _mic.Start(_deviceId());
        SetState(VoiceChatState.Recording);
    }

    public void EndRecording()
    {
        if (State != VoiceChatState.Recording) return;

        float[] samples = _mic.Stop();
        if (samples.Length < MinValidSampleCount)
        {
            SetState(VoiceChatState.Idle);
            return;
        }

        SetState(VoiceChatState.Transcribing);
        _transcribeCts = new CancellationTokenSource();
        RunTranscription(samples, _transcribeCts.Token);
    }

    private async void RunTranscription(float[] samples, CancellationToken ct)
    {
        try
        {
            string result = await _stt.TranscribeAsync(samples, _vocabularyHint(), ct);
            if (ct.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(result))
            {
                SetState(VoiceChatState.Idle);
                Failed?.Invoke("No speech detected.");
                return;
            }

            Transcript = result;
            SetState(VoiceChatState.PreviewReady);
            PreviewReady?.Invoke(result);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetState(VoiceChatState.Idle);
            Failed?.Invoke(ex.Message);
        }
    }

    public void Discard()
    {
        if (State != VoiceChatState.PreviewReady) return;
        Transcript = null;
        SetState(VoiceChatState.Idle);
    }

    private void SetState(VoiceChatState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transcribeCts?.Cancel();
        _transcribeCts?.Dispose();
    }
}
