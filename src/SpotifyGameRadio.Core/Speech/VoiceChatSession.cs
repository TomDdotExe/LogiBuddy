using SpotifyGameRadio.Core.Audio;

namespace SpotifyGameRadio.Core.Speech;

public enum VoiceChatState { Idle, Recording, Transcribing, PreviewReady }

/// Push-to-talk record -> transcribe -> preview -> confirm/discard/re-record
/// state machine. Pure aside from its two injected dependencies; unit-tested
/// with fakes. Modelled on CalibrationSession's event-driven shape.
public sealed class VoiceChatSession : IDisposable
{
    // Below this many 16kHz mono samples (~200ms) a recording is treated as
    // an accidental brush of the key — no transcription attempt, no preview.
    private const int MinValidSampleCount = 3200;

    private readonly IMicrophoneCapture _mic;
    private readonly ISpeechToText _stt;
    private readonly Func<string> _vocabularyHint;
    private CancellationTokenSource? _transcribeCts;
    private bool _disposed;

    public VoiceChatState State { get; private set; } = VoiceChatState.Idle;
    public string? Transcript { get; private set; }

    public event Action<VoiceChatState>? StateChanged;
    public event Action<string>? PreviewReady;
    public event Action<string>? Failed;

    public VoiceChatSession(IMicrophoneCapture mic, ISpeechToText stt, Func<string> vocabularyHint)
    {
        _mic = mic;
        _stt = stt;
        _vocabularyHint = vocabularyHint;
    }

    public void BeginRecording()
    {
        if (State is VoiceChatState.Recording or VoiceChatState.Transcribing) return;

        Transcript = null;
        _mic.Start(null);
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
