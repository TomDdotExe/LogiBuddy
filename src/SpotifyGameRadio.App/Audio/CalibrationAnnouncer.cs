using System.IO;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpotifyGameRadio.App.Audio;

/// Speaks short calibration status phrases through the configured radio
/// output device (the game has focus during calibration, so spoken cues
/// beat any on-screen text). Fire-and-forget; any failure — no installed
/// voice, no device — is swallowed so it can never break calibration.
public sealed class CalibrationAnnouncer : IDisposable
{
    private readonly Func<string?> _deviceIdProvider;
    private readonly SpeechSynthesizer _synth = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <param name="outputDeviceIdProvider">
    /// Returns the MMDevice id the radio is playing to, or null/empty for
    /// the system default. Read fresh on every phrase.
    /// </param>
    public CalibrationAnnouncer(Func<string?> outputDeviceIdProvider)
    {
        _deviceIdProvider = outputDeviceIdProvider;
    }

    public void Say(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return;
        _ = Task.Run(() => SpeakToDevice(text));
    }

    private void SpeakToDevice(string text)
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(5))) return;
        try
        {
            if (_disposed) return;

            using var wav = new MemoryStream();
            _synth.SetOutputToWaveStream(wav);
            _synth.Speak(text);
            _synth.SetOutputToNull();
            wav.Position = 0;

            using var reader = new WaveFileReader(wav);
            using var player = CreatePlayer();
            using var done = new ManualResetEventSlim(false);
            player.PlaybackStopped += (_, _) => done.Set();
            player.Init(reader);
            player.Play();
            done.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // An unspoken cue must never break calibration.
        }
        finally
        {
            _gate.Release();
        }
    }

    private IWavePlayer CreatePlayer()
    {
        string? id = _deviceIdProvider();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                using var mm = new MMDeviceEnumerator();
                foreach (var device in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (device.ID == id)
                        return new WasapiOut(device, AudioClientShareMode.Shared, false, 120);
                    device.Dispose();
                }
            }
            catch
            {
                // Fall through to the default endpoint.
            }
        }
        return new WaveOutEvent();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _synth.Dispose(); } catch { }
    }
}
