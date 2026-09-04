using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

public interface IAudioCaptureService : IDisposable
{
    WaveFormat Format { get; }
    event EventHandler<AudioCaptureStatus>? StatusChanged;

    /// Raised per captured block with interleaved float samples at Format's channel count.
    event EventHandler<float[]>? DataAvailable;

    void Start(string processName);
    void Stop();
}
