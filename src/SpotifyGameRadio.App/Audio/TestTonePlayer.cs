using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpotifyGameRadio.App.Audio;

/// Dev/test aid: plays a soft 440 Hz sine to the default render endpoint so
/// per-process loopback capture has something to grab without needing Spotify
/// or a separate process. Fades in and out so toggling it never produces a
/// startling click on headphones.
public sealed class TestTonePlayer : IDisposable
{
    private const int FadeMs = 200;

    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private FadeInOutSampleProvider? _fade;
    private System.Timers.Timer? _stopTimer;
    private bool _disposed;

    public bool IsPlaying { get; private set; }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || IsPlaying) return;

            // Cancel a pending post-fade-out teardown if we're restarting mid-fade.
            _stopTimer?.Stop();
            _stopTimer?.Dispose();
            _stopTimer = null;

            if (_output is null)
            {
                var sine = new SignalGenerator(48000, 2)
                {
                    Type = SignalGeneratorType.Sin,
                    Frequency = 440,
                    Gain = 0.12,
                };
                _fade = new FadeInOutSampleProvider(sine, initiallySilent: true);
                _output = new WaveOutEvent();
                _output.Init(_fade);
                _output.Play();
            }

            _fade!.BeginFadeIn(FadeMs);
            IsPlaying = true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsPlaying || _fade is null) return;

            _fade.BeginFadeOut(FadeMs);
            IsPlaying = false;

            _stopTimer?.Stop();
            _stopTimer?.Dispose();
            _stopTimer = new System.Timers.Timer(FadeMs + 100) { AutoReset = false };
            _stopTimer.Elapsed += (_, _) => TearDownOutput();
            _stopTimer.Start();
        }
    }

    /// Releases the WaveOut device once the fade-out has finished playing.
    private void TearDownOutput()
    {
        lock (_lock)
        {
            // A Start() may have re-fired during the fade-out window — leave the
            // (now fading back in) output alone.
            if (IsPlaying || _disposed) return;

            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _fade = null;

            _stopTimer?.Dispose();
            _stopTimer = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            IsPlaying = false;

            _stopTimer?.Stop();
            _stopTimer?.Dispose();
            _stopTimer = null;

            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _fade = null;
        }
    }
}
