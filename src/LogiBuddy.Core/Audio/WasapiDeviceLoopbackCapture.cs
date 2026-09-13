using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LogiBuddy.Core.Audio;

/// Fallback for Windows versions without per-process loopback support:
/// captures everything playing on the chosen output device, not just one
/// process. Used automatically when WasapiProcessLoopbackCapture fails.
public class WasapiDeviceLoopbackCapture : IAudioCaptureService
{
    private WasapiLoopbackCapture? _capture;

    public WaveFormat Format { get; private set; } = new WaveFormat(48000, 32, 1);

    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;

    public void Start(string processName)
    {
        // processName is accepted for interface parity with
        // WasapiProcessLoopbackCapture but not used here — this fallback
        // captures the whole default render device.
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _capture = new WasapiLoopbackCapture(device);
            Format = _capture.WaveFormat;

            _capture.DataAvailable += (_, e) =>
            {
                int sampleCount = e.BytesRecorded / 4; // 32-bit float samples
                var samples = new float[sampleCount];
                Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
                DataAvailable?.Invoke(this, samples);
            };
            _capture.RecordingStopped += (_, _) => StatusChanged?.Invoke(this, AudioCaptureStatus.NoSource);

            _capture.StartRecording();
            StatusChanged?.Invoke(this, AudioCaptureStatus.Capturing);
        }
        catch (Exception)
        {
            _capture?.Dispose();
            _capture = null;
            StatusChanged?.Invoke(this, AudioCaptureStatus.Error);
        }
    }

    public void Stop()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();
}
