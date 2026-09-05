using SpotifyGameRadio.Core.Audio;
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Dsp;
using SpotifyGameRadio.Core.Spatial;
using SpotifyGameRadio.Core.Tracking;

namespace SpotifyGameRadio.Core.Pipeline;

public class RadioPipeline
{
    private readonly IAudioCaptureService _capture;
    private readonly IAudioOutputService _output;
    private readonly ISpatializer _primarySpatializer;
    private readonly ISpatializer _fallbackSpatializer;
    private ISpatializer _activeSpatializer;
    private readonly RadioEffectChain _effectChain;
    private readonly FreelookTracker _tracker;
    private RadioProfile _profile = new();

    public int BufferUnderrunCount { get; private set; }
    public event EventHandler<string>? Warning;

    public RadioPipeline(
        IAudioCaptureService capture,
        IAudioOutputService output,
        ISpatializer primarySpatializer,
        ISpatializer fallbackSpatializer,
        RadioEffectChain effectChain,
        FreelookTracker tracker)
    {
        _capture = capture;
        _output = output;
        _primarySpatializer = primarySpatializer;
        _fallbackSpatializer = fallbackSpatializer;
        _activeSpatializer = primarySpatializer;
        _effectChain = effectChain;
        _tracker = tracker;

        _capture.DataAvailable += OnDataAvailable;
        _capture.StatusChanged += OnCaptureStatusChanged;
        _output.DeviceLost += OnOutputDeviceLost;
    }

    public void ApplyProfile(RadioProfile profile)
    {
        _profile = profile;
        _effectChain.ApplyProfile(profile);
        _tracker.ApplyProfile(profile);
        _primarySpatializer.SetSourcePosition(profile.SourceX, profile.SourceY, profile.SourceZ);
        _fallbackSpatializer.SetSourcePosition(profile.SourceX, profile.SourceY, profile.SourceZ);
    }

    public void Start()
    {
        _output.Start(_profile.OutputDeviceId);
        _capture.Start(_profile.SourceProcessName);
    }

    public void Stop()
    {
        _capture.Stop();
        _output.Stop();
    }

    private void OnDataAvailable(object? sender, float[] interleavedSamples)
    {
        try
        {
            // Capture services may report more than one channel (e.g. the
            // WasapiDeviceLoopbackCapture fallback reports the real device
            // format); downmix to mono before the effect chain/spatializer,
            // both of which operate on a single source channel.
            var mono = DownmixToMono(interleavedSamples, _capture.Format.Channels);

            _tracker.Update(deltaSeconds: mono.Length / (float)_capture.Format.SampleRate);
            _activeSpatializer.SetListenerOrientation(_tracker.YawDegrees, _tracker.PitchDegrees);

            _effectChain.Process(mono, mono.Length);

            var stereo = new float[mono.Length * 2];
            _activeSpatializer.Process(mono, mono.Length, stereo);

            _output.Write(stereo, stereo.Length);
        }
        catch (Exception)
        {
            BufferUnderrunCount++;
        }
    }

    private static float[] DownmixToMono(float[] interleaved, int channels)
    {
        if (channels <= 1) return (float[])interleaved.Clone();

        int frameCount = interleaved.Length / channels;
        var mono = new float[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
                sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    private void OnCaptureStatusChanged(object? sender, AudioCaptureStatus status)
    {
        if (status == AudioCaptureStatus.NoSource)
            Warning?.Invoke(this, $"No audio detected from '{_profile.SourceProcessName}'. Waiting for it to start playing...");
        else if (status == AudioCaptureStatus.Error)
            Warning?.Invoke(this, "Audio capture failed unexpectedly.");
    }

    private void OnOutputDeviceLost(object? sender, EventArgs e)
    {
        Warning?.Invoke(this, "Output device disconnected. Falling back to system default.");
    }

    /// Switches to the fallback spatializer (e.g. Steam Audio failed to load).
    public void UseFallbackSpatializer()
    {
        _activeSpatializer = _fallbackSpatializer;
        Warning?.Invoke(this, "HRTF spatialization unavailable — using simple stereo panning instead.");
    }
}
