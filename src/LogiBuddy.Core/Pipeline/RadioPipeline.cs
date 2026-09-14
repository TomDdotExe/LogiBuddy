using LogiBuddy.Core.Audio;
using LogiBuddy.Core.Config;
using LogiBuddy.Core.Dsp;
using LogiBuddy.Core.Spatial;
using LogiBuddy.Core.Tracking;

namespace LogiBuddy.Core.Pipeline;

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
    private float _vehicleGain = 1f; // not part of RadioProfile; runtime-only, toggled by the vehicle in/out feature.
    private float _overrideGain = 1f; // not part of RadioProfile; runtime-only, toggled by the override mute feature.
    private bool _outsideView; // not part of RadioProfile; runtime-only, toggled by the inside/outside view feature.

    public int BufferUnderrunCount { get; private set; }
    public event EventHandler<string>? Warning;

    /// Current freelook listener orientation, updated by the mouse hook as the
    /// user looks around. Exposed for a live UI readout; the audio thread reads
    /// the tracker directly.
    public float ListenerYawDegrees => _tracker.YawDegrees;
    public float ListenerPitchDegrees => _tracker.PitchDegrees;

    /// Snaps the freelook listener orientation back to forward. Safe on the UI thread.
    public void RecenterListener() => _tracker.Recenter();

    /// Mutes/unmutes the processed output independent of Profile.Volume — used
    /// by the vehicle in/out toggle. Never persisted; a new RadioPipeline
    /// instance always starts unmuted (1f). Safe to call from the UI thread.
    public void SetVehicleMuted(bool muted) => _vehicleGain = muted ? 0f : 1f;

    /// Mutes/unmutes the processed output independent of Profile.Volume and
    /// SetVehicleMuted — used by the override mute button/hotkey. Never
    /// persisted; a new RadioPipeline instance always starts unmuted (1f).
    /// Safe to call from the UI thread.
    public void SetOverrideMuted(bool muted) => _overrideGain = muted ? 0f : 1f;

    /// Switches between the profile's normal (inside-cockpit) tone, source
    /// position, and yaw range and their outside-view equivalents (muffled/
    /// quieter/narrower tone, source collapsed to the pivot point, free 360°
    /// pan instead of the cockpit's clamped range) — used by the inside/
    /// outside view toggle. Never persisted; a new RadioPipeline instance
    /// always starts inside (false). Safe to call from the UI thread.
    public void SetOutsideView(bool outside)
    {
        _outsideView = outside;
        _tracker.SetOutsideView(outside);
        ApplyEffectiveTone();
    }

    /// Re-derives the low-pass/volume/stereo-width/source-position the audio
    /// thread actually uses from the current profile and _outsideView.
    /// Called after ApplyProfile's own (inside-only) assignment so a live
    /// profile edit while outside doesn't accidentally snap back to the
    /// inside values, and after SetOutsideView so a toggle takes effect
    /// immediately.
    // Reference distance (metres) at which OutsideSourceDistance applies no
    // gain change — chosen to match RadioProfile's own default for that
    // field, so an untouched profile's outside volume doesn't shift just
    // from this feature existing. Neither spatializer models loudness
    // falloff over distance on its own: azimuth is derived from the
    // direction vector's angle alone (scale-invariant — see
    // StereoPanSpatializer's atan2), and Steam Audio's binaural effect only
    // uses the vector's magnitude for near-field cues within roughly a
    // metre, not for level. Without this, the distance slider audibly does
    // nothing across most of its range.
    private const float OutsideDistanceReferenceMeters = 5f;
    private const float MinOutsideDistanceGain = 0.15f;
    private const float MaxOutsideDistanceGain = 1.5f;

    private void ApplyEffectiveTone()
    {
        _effectChain.LowPassHz = _outsideView ? _profile.OutsideLowPassHz : _profile.LowPassHz;
        _stereoWidth = Math.Clamp(_outsideView ? _profile.OutsideStereoWidth : _profile.StereoWidth, 0f, 4f);

        if (_outsideView)
        {
            // Floor guards against the user dragging the slider to (or near)
            // 0 — a near-zero distance is the degenerate direction-vector
            // case SteamAudioSpatializer's own epsilon guard exists for, but
            // it also (per real-world testing) collapses real HRTF rendering
            // toward centred/mono well before hitting that exact edge case,
            // since near-field handling reduces directional cues as distance
            // shrinks. 0.5 m keeps a clear sweep even at the slider's floor.
            float distance = Math.Max(_profile.OutsideSourceDistance, 0.5f);
            _primarySpatializer.SetSourcePosition(0f, 0f, distance);
            _fallbackSpatializer.SetSourcePosition(0f, 0f, distance);

            float distanceGain = Math.Clamp(OutsideDistanceReferenceMeters / distance, MinOutsideDistanceGain, MaxOutsideDistanceGain);
            _volume = Math.Clamp(_profile.OutsideVolume, 0f, 1f) * distanceGain;
        }
        else
        {
            _primarySpatializer.SetSourcePosition(_profile.SourceX, _profile.SourceY, _profile.SourceZ);
            _fallbackSpatializer.SetSourcePosition(_profile.SourceX, _profile.SourceY, _profile.SourceZ);
            _volume = Math.Clamp(_profile.Volume, 0f, 1f);
        }
    }

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
        // _effectChain.ApplyProfile above just set LowPassHz to the inside
        // value; this re-derives LowPassHz/Volume/StereoWidth/source-position
        // from the current _outsideView so a live profile edit while outside
        // doesn't snap back to the inside tone/position.
        ApplyEffectiveTone();
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
                float gain = _volume * _vehicleGain * _overrideGain;
                if (gain != 1f)
                    for (int s = 0; s < _frameSize * 2; s++)
                        _stereoBlock[s] *= gain;

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
