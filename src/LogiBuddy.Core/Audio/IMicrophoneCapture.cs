namespace LogiBuddy.Core.Audio;

public interface IMicrophoneCapture
{
    /// Begins buffering microphone audio. deviceId "" or null uses the
    /// default capture device. Safe to call again after Stop(); calling
    /// while already capturing is a no-op.
    void Start(string? deviceId);

    /// Stops capturing and returns everything captured since Start(), mixed
    /// down to mono and resampled to 16000 Hz float32 (what Whisper.net
    /// expects). Returns an empty array if Start() was never called, no
    /// audio arrived, or the capture device failed to open.
    float[] Stop();
}
