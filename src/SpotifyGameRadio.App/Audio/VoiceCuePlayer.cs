using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpotifyGameRadio.App.Audio;

/// Plays short beeps through the configured radio output device to mark
/// voice-chat transitions the push-to-talk key gives no other feedback for:
/// recording starting (easy to start talking before noticing it hasn't) and
/// recording ending / transcription starting (so a held-too-long key press
/// or a slow release isn't mistaken for still-recording). Fire-and-forget;
/// any failure (no device, playback error) is swallowed so it can never
/// break recording.
public sealed class VoiceCuePlayer : IDisposable
{
    private readonly Func<string?> _deviceIdProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <param name="outputDeviceIdProvider">
    /// Returns the MMDevice id the radio is playing to, or null/empty for
    /// the system default. Read fresh on every beep.
    /// </param>
    public VoiceCuePlayer(Func<string?> outputDeviceIdProvider)
    {
        _deviceIdProvider = outputDeviceIdProvider;
    }

    /// Recording has started.
    public void PlayRecordStartBeep() => PlayBeep(880);

    /// Recording has ended and transcription is starting.
    public void PlayTranscribeStartBeep() => PlayBeep(660);

    private void PlayBeep(float frequencyHz)
    {
        if (_disposed) return;
        _ = Task.Run(() => PlayToDevice(frequencyHz));
    }

    private void PlayToDevice(float frequencyHz)
    {
        if (!_gate.Wait(0)) return; // a beep already in flight is fine to skip, never queue
        try
        {
            if (_disposed) return;

            var signal = new SignalGenerator(48000, 1)
            {
                Type = SignalGeneratorType.Sin,
                Frequency = frequencyHz,
                Gain = 0.25,
            };
            IWaveProvider clip = signal.Take(TimeSpan.FromMilliseconds(120)).ToWaveProvider();

            using var player = DeviceOutputPlayer.Create(_deviceIdProvider);
            using var done = new ManualResetEventSlim(false);
            player.PlaybackStopped += (_, _) => done.Set();
            player.Init(clip);
            player.Play();
            done.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // An unheard cue must never break recording.
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
