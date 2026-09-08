using NAudio.Dsp;
using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

/// Wraps a capture service and guarantees its output is 48 kHz, which is what
/// the rest of the pipeline (effect chain, spatializer frame size, output
/// renderer) assumes. When the inner service already reports 48 kHz — the
/// common case, since per-process loopback always does — blocks are forwarded
/// untouched. Only the whole-device fallback on a non-48 kHz device actually
/// resamples.
///
/// The inner's rate is re-checked on every block, so a FallbackAudioCaptureService
/// swapping a 48 kHz primary for a 44.1 kHz device-loopback fallback mid-session
/// is picked up without any restart.
public class ResamplingCaptureService : IAudioCaptureService
{
    private const int TargetSampleRate = 48000;

    private readonly IAudioCaptureService _inner;
    private readonly object _resamplerLock = new();

    private WdlResampler? _resampler;
    private int _resamplerInRate;
    private int _resamplerChannels;

    public WaveFormat Format =>
        WaveFormat.CreateIeeeFloatWaveFormat(TargetSampleRate, _inner.Format.Channels);

    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;

    public ResamplingCaptureService(IAudioCaptureService inner)
    {
        _inner = inner;
        _inner.DataAvailable += OnData;
        _inner.StatusChanged += (_, s) => StatusChanged?.Invoke(this, s);
    }

    private void OnData(object? sender, float[] samples)
    {
        int inRate = _inner.Format.SampleRate;
        int channels = _inner.Format.Channels;

        // Overwhelmingly common path (per-process loopback): nothing to do, so
        // forward the caller's own buffer with no copy or allocation.
        if (inRate == TargetSampleRate || channels < 1 || samples.Length == 0)
        {
            DataAvailable?.Invoke(this, samples);
            return;
        }

        float[] resampled = Resample(samples, inRate, channels);
        DataAvailable?.Invoke(this, resampled);
    }

    private float[] Resample(float[] interleaved, int inRate, int channels)
    {
        lock (_resamplerLock)
        {
            if (_resampler is null || _resamplerInRate != inRate || _resamplerChannels != channels)
            {
                _resampler = new WdlResampler();
                _resampler.SetMode(true, 2, false);
                _resampler.SetFilterParms();
                _resampler.SetFeedMode(true); // input-driven: feed what we have, take what comes out
                _resampler.SetRates(inRate, TargetSampleRate);
                _resamplerInRate = inRate;
                _resamplerChannels = channels;
            }

            int inFrames = interleaved.Length / channels;

            int framesNeeded = _resampler.ResamplePrepare(inFrames, channels, out float[] inBuffer, out int inOffset);
            int framesToCopy = Math.Min(framesNeeded, inFrames) * channels;
            Array.Copy(interleaved, 0, inBuffer, inOffset, framesToCopy);

            // Ceiling of the rate ratio plus headroom for the anti-alias filter's
            // warm-up so a whole block's worth of output always fits.
            int outCapacityFrames = (int)Math.Ceiling(inFrames * (double)TargetSampleRate / inRate) + 64;
            var outBuffer = new float[outCapacityFrames * channels];

            int outFrames = _resampler.ResampleOut(outBuffer, 0, framesToCopy / channels, outCapacityFrames, channels);

            int outSamples = outFrames * channels;
            if (outSamples == outBuffer.Length) return outBuffer;

            var trimmed = new float[outSamples];
            Array.Copy(outBuffer, trimmed, outSamples);
            return trimmed;
        }
    }

    public void Start(string processName) => _inner.Start(processName);

    public void Stop() => _inner.Stop();

    public void Dispose() => _inner.Dispose();
}
