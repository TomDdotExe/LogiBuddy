using SpotifyGameRadio.Core.Audio;
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Dsp;
using SpotifyGameRadio.Core.Spatial;
using SpotifyGameRadio.Core.Tracking;

namespace SpotifyGameRadio.Core.Pipeline;

public class RadioPipeline : IDisposable
{
    private readonly IAudioCaptureService _capture;
    private readonly IAudioOutputService _output;
    private readonly ISpatializer _primarySpatializer;
    private readonly ISpatializer _fallbackSpatializer;
    private ISpatializer _activeSpatializer;
    private readonly RadioEffectChain _effectChain;
    private readonly FreelookTracker _tracker;
    private RadioProfile _profile = new();
    private bool _disposed;

    // Fixed processing block size. Spatializers (notably SteamAudioSpatializer) are
    // created for one fixed frame size and their native effect always processes
    // exactly that many samples, so the pipeline must hand them exactly _frameSize
    // samples — never a short buffer (native out-of-bounds read) and never a long
    // one (silently truncated tail). WASAPI capture packets are smaller than this
    // and vary in size callback to callback, hence the accumulation buffer below.
    private readonly int _frameSize;

    // Downmixed-mono samples not yet consumed by a full block. Grown on demand;
    // in steady state it settles at (frameSize + one capture packet) and stops
    // reallocating.
    private float[] _accumulator;
    private int _accumulatedCount;

    // Pre-allocated once and reused on every block so the real-time audio
    // callback never allocates on the heap.
    private readonly float[] _monoBlock;
    private readonly float[] _stereoBlock;

    // Output-stage tunables, copied from the profile by ApplyProfile (same
    // pattern as the effect-chain parameters). Read once per block on the audio
    // thread; single-float writes are atomic so a live change is safe.
    private float _volume = 1f;
    private float _stereoWidth = 1f;

    public int BufferUnderrunCount { get; private set; }
    public event EventHandler<string>? Warning;

    /// Current freelook listener orientation, updated by the mouse hook as the
    /// user looks around. Exposed for a live UI readout; the audio thread reads
    /// the tracker directly.
    public float ListenerYawDegrees => _tracker.YawDegrees;
    public float ListenerPitchDegrees => _tracker.PitchDegrees;

    public RadioPipeline(
        IAudioCaptureService capture,
        IAudioOutputService output,
        ISpatializer primarySpatializer,
        ISpatializer fallbackSpatializer,
        RadioEffectChain effectChain,
        FreelookTracker tracker,
        int frameSize)
    {
        if (frameSize < 1) throw new ArgumentOutOfRangeException(nameof(frameSize));

        _capture = capture;
        _output = output;
        _primarySpatializer = primarySpatializer;
        _fallbackSpatializer = fallbackSpatializer;
        _activeSpatializer = primarySpatializer;
        _effectChain = effectChain;
        _tracker = tracker;
        _frameSize = frameSize;

        _accumulator = new float[frameSize * 2];
        _monoBlock = new float[frameSize];
        _stereoBlock = new float[frameSize * 2];

        _capture.DataAvailable += OnDataAvailable;
        _capture.StatusChanged += OnCaptureStatusChanged;
        _output.DeviceLost += OnOutputDeviceLost;
    }

    /// Re-applies every live-tunable value (DSP parameters, freelook tuning,
    /// source position) to the running components. Safe to call on a running
    /// pipeline from the UI thread: each target is a plain scalar the audio
    /// thread reads once per block and individual float writes are atomic, so
    /// the worst case is one block blended across an old and new value. It
    /// deliberately touches nothing in capture, output, or the input hook.
    public void ApplyProfile(RadioProfile profile)
    {
        _profile = profile;
        _effectChain.ApplyProfile(profile);
        _tracker.ApplyProfile(profile);
        _primarySpatializer.SetSourcePosition(profile.SourceX, profile.SourceY, profile.SourceZ);
        _fallbackSpatializer.SetSourcePosition(profile.SourceX, profile.SourceY, profile.SourceZ);
        _volume = Math.Clamp(profile.Volume, 0f, 1f);
        _stereoWidth = Math.Clamp(profile.StereoWidth, 0f, 4f);
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

    /// Switches playback to a different render device without disturbing capture,
    /// the effect chain, or the input hook. WasapiAudioOutput.Start tears down
    /// and rebuilds internally, the same restart the DeviceLost path performs,
    /// so a brief gap in audio is expected. Safe to call on a running pipeline
    /// from the UI thread — Write() null-checks its buffer.
    public void SetOutputDevice(string deviceId)
    {
        _output.Start(deviceId);
    }

    private void OnDataAvailable(object? sender, float[] interleavedSamples)
    {
        try
        {
            // Capture services may report more than one channel (e.g. the
            // WasapiDeviceLoopbackCapture fallback reports the real device
            // format); downmix to mono before the effect chain/spatializer,
            // both of which operate on a single source channel.
            AppendDownmixedToMono(interleavedSamples, _capture.Format.Channels);

            // Drain the accumulator in exact fixed-size blocks. Leftover samples
            // stay buffered for the next callback — nothing is dropped, nothing
            // is duplicated.
            while (_accumulatedCount >= _frameSize)
            {
                Array.Copy(_accumulator, 0, _monoBlock, 0, _frameSize);
                _accumulatedCount -= _frameSize;
                if (_accumulatedCount > 0)
                    Array.Copy(_accumulator, _frameSize, _accumulator, 0, _accumulatedCount);

                _tracker.Update(deltaSeconds: _frameSize / (float)_capture.Format.SampleRate);
                _activeSpatializer.SetListenerOrientation(_tracker.YawDegrees, _tracker.PitchDegrees);

                _effectChain.Process(_monoBlock, _frameSize);

                _activeSpatializer.Process(_monoBlock, _frameSize, _stereoBlock);

                // Post-spatializer output stage: width first, then master gain,
                // so the volume fader also tames any width-induced level rise.
                StereoWidth.Apply(_stereoBlock, _frameSize, _stereoWidth);
                if (_volume != 1f)
                    for (int s = 0; s < _frameSize * 2; s++)
                        _stereoBlock[s] *= _volume;

                _output.Write(_stereoBlock, _stereoBlock.Length);
            }
        }
        catch (Exception)
        {
            BufferUnderrunCount++;
        }
    }

    /// Downmixes an interleaved capture packet to mono and appends it to the
    /// accumulation buffer. Channel averaging is identical to the previous
    /// standalone DownmixToMono; only the destination changed.
    private void AppendDownmixedToMono(float[] interleaved, int channels)
    {
        int frameCount = channels <= 1 ? interleaved.Length : interleaved.Length / channels;
        EnsureAccumulatorCapacity(_accumulatedCount + frameCount);

        if (channels <= 1)
        {
            Array.Copy(interleaved, 0, _accumulator, _accumulatedCount, frameCount);
        }
        else
        {
            for (int i = 0; i < frameCount; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                    sum += interleaved[i * channels + c];
                _accumulator[_accumulatedCount + i] = sum / channels;
            }
        }

        _accumulatedCount += frameCount;
    }

    private void EnsureAccumulatorCapacity(int required)
    {
        if (_accumulator.Length >= required) return;

        // Doubling growth so this converges after the first few callbacks
        // rather than reallocating on every packet.
        int newSize = Math.Max(required, _accumulator.Length * 2);
        var grown = new float[newSize];
        Array.Copy(_accumulator, grown, _accumulatedCount);
        _accumulator = grown;
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

        // Actually perform the fallback the message promises: WasapiAudioOutput
        // resolves an unknown/empty device id to the current default render
        // endpoint (its GetDevice call throws and is caught). If that fails too
        // (e.g. no render device at all), report it rather than letting the
        // exception escape onto the device-notification/playback thread.
        try
        {
            _output.Start("");
        }
        catch (Exception)
        {
            Warning?.Invoke(this, "Output device disconnected and no default device is available. Click Stop, then Start again once a device is connected.");
        }
    }

    /// Switches to the fallback spatializer (e.g. Steam Audio failed to load).
    public void UseFallbackSpatializer()
    {
        _activeSpatializer = _fallbackSpatializer;
        Warning?.Invoke(this, "HRTF spatialization unavailable — using simple stereo panning instead.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _capture.DataAvailable -= OnDataAvailable;
        _capture.StatusChanged -= OnCaptureStatusChanged;
        _output.DeviceLost -= OnOutputDeviceLost;

        // Spatializers can own native resources (SteamAudioSpatializer holds an
        // HRTF context/effect). MainViewModel passes the same instance as both
        // primary and fallback when Steam Audio is unavailable, so guard against
        // disposing one object twice.
        (_primarySpatializer as IDisposable)?.Dispose();
        if (!ReferenceEquals(_primarySpatializer, _fallbackSpatializer))
            (_fallbackSpatializer as IDisposable)?.Dispose();

        GC.SuppressFinalize(this);
    }
}
