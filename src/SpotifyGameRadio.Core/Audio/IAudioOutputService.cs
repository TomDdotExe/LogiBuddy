namespace SpotifyGameRadio.Core.Audio;

public interface IAudioOutputService : IDisposable
{
    void Start(string deviceId);
    void Stop();
    void Write(float[] stereoInterleaved, int count);

    /// Raised if the output device disappears (e.g. headset unplugged).
    event EventHandler? DeviceLost;
}
