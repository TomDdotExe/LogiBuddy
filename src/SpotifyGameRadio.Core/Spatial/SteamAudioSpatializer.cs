using static SpotifyGameRadio.Core.Spatial.SteamAudioNative;

namespace SpotifyGameRadio.Core.Spatial;

public class SteamAudioSpatializer : ISpatializer, IDisposable
{
    private IntPtr _context;
    private IntPtr _hrtf;
    private IntPtr _effect;
    private readonly int _frameSize;
    private bool _disposed;

    // Pre-allocated once (frame size is fixed for the lifetime of this instance) so
    // Process() never allocates on the real-time audio thread.
    private readonly float[] _outLeftBuffer;
    private readonly float[] _outRightBuffer;

    private float _sourceX, _sourceY, _sourceZ;
    private float _yawRadians, _pitchRadians;

    private SteamAudioSpatializer(IntPtr context, IntPtr hrtf, IntPtr effect, int frameSize)
    {
        _context = context;
        _hrtf = hrtf;
        _effect = effect;
        _frameSize = frameSize;
        _outLeftBuffer = new float[frameSize];
        _outRightBuffer = new float[frameSize];
    }

    /// Attempts to load the native library and create the HRTF pipeline.
    /// Returns false (never throws) if the native library is missing or
    /// fails to initialize, so callers can fall back to StereoPanSpatializer.
    public static bool TryCreate(int sampleRate, int frameSize, out SteamAudioSpatializer? spatializer)
    {
        spatializer = null;
        try
        {
            var contextSettings = new IPLContextSettings { version = 4 << 16 | 5 << 8 };
            if (iplContextCreate(ref contextSettings, out var context) != 0) return false;

            var audioSettings = new IPLAudioSettings { samplingRate = sampleRate, frameSize = frameSize };
            var hrtfSettings = new IPLHRTFSettings { type = 0, volume = 1f, normType = 0 };
            if (iplHRTFCreate(context, ref audioSettings, ref hrtfSettings, out var hrtf) != 0)
            {
                iplContextRelease(ref context);
                return false;
            }

            var effectSettings = new IPLBinauralEffectSettings { hrtf = hrtf };
            if (iplBinauralEffectCreate(context, ref audioSettings, ref effectSettings, out var effect) != 0)
            {
                iplHRTFRelease(ref hrtf);
                iplContextRelease(ref context);
                return false;
            }

            spatializer = new SteamAudioSpatializer(context, hrtf, effect, frameSize);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            // A phonon.dll of the wrong architecture (e.g. a 32-bit build dropped
            // next to a 64-bit app) — still a "not available", never a throw.
            return false;
        }
    }

    public void SetSourcePosition(float x, float y, float z)
    {
        _sourceX = x;
        _sourceY = y;
        _sourceZ = z;
    }

    public void SetListenerOrientation(float yawDegrees, float pitchDegrees)
    {
        _yawRadians = yawDegrees * MathF.PI / 180f;
        _pitchRadians = pitchDegrees * MathF.PI / 180f;
    }

    public unsafe void Process(float[] monoInput, int count, float[] stereoOutputInterleaved)
    {
        // Rotate the fixed source into listener space (see SourceRotation for
        // the yaw/pitch convention — same one StereoPanSpatializer uses).
        // NOTE: an earlier version of the yaw-only formula here used
        // cos(-yaw)/sin(-yaw) and was found to have a sign bug — see Task 7's
        // StereoPanSpatializer fix — so this deliberately matches the
        // verified-correct, non-negated convention in SourceRotation.
        var (relativeX, relativeY, relativeZ) = SourceRotation.Rotate(_sourceX, _sourceY, _sourceZ, _yawRadians, _pitchRadians);

        // A source position at (or extremely close to) the listener — reachable
        // via the outside-view pivot collapse, or simply a source position
        // manually set to (0,0,0) — has no defined direction. iplBinauralEffectApply
        // likely normalizes this vector internally; a zero-length one risks a
        // divide-by-zero (NaN output) rather than a crash, so nudge it forward
        // instead of trusting that to be handled gracefully.
        if (relativeX * relativeX + relativeY * relativeY + relativeZ * relativeZ < 1e-8f)
            relativeZ = 0.01f;

        var direction = new IPLVector3 { x = relativeX, y = relativeY, z = -relativeZ };

        // Defensive clamp: _outLeftBuffer/_outRightBuffer are fixed-size (allocated once at
        // _frameSize). Real WASAPI capture callbacks aren't strictly guaranteed to always
        // deliver exactly the configured block size (buffer catch-up after underrun, stream
        // start/end can hand back a short or occasionally larger packet), so clamp rather than
        // trust count blindly — this must never crash the real-time audio thread. Any samples
        // beyond n are silently dropped, not processed.
        int n = Math.Min(count, _frameSize);

        fixed (float* inPtr = monoInput)
        fixed (float* outLeft = _outLeftBuffer)
        fixed (float* outRight = _outRightBuffer)
        {
            var inChannels = stackalloc IntPtr[1] { (IntPtr)inPtr };
            var outChannels = stackalloc IntPtr[2] { (IntPtr)outLeft, (IntPtr)outRight };

            var inBuffer = new IPLAudioBuffer { numChannels = 1, numSamples = n, data = (IntPtr)inChannels };
            var outBuffer = new IPLAudioBuffer { numChannels = 2, numSamples = n, data = (IntPtr)outChannels };

            var effectParams = new IPLBinauralEffectParams
            {
                direction = direction,
                interpolation = 0,
                spatialBlend = 1f,
                hrtf = _hrtf
            };

            iplBinauralEffectApply(_effect, ref effectParams, ref inBuffer, ref outBuffer);

            for (int i = 0; i < n; i++)
            {
                stereoOutputInterleaved[i * 2] = outLeft[i];
                stereoOutputInterleaved[i * 2 + 1] = outRight[i];
            }
        }
    }

    public void Dispose()
    {
        // Releasing the same native handle twice would corrupt Steam Audio's
        // refcounts, so make a second Dispose() a no-op.
        if (_disposed) return;
        _disposed = true;

        if (_effect != IntPtr.Zero) iplBinauralEffectRelease(ref _effect);
        if (_hrtf != IntPtr.Zero) iplHRTFRelease(ref _hrtf);
        if (_context != IntPtr.Zero) iplContextRelease(ref _context);
    }
}
