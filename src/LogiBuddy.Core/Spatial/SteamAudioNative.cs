using System.Runtime.InteropServices;

namespace LogiBuddy.Core.Spatial;

/// P/Invoke surface for the subset of Steam Audio's C API (phonon.h) this
/// app needs: context creation, an HRTF instance, and a binaural effect
/// that renders a mono source at a given direction into stereo output.
internal static class SteamAudioNative
{
    private const string Lib = "phonon";

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLVector3 { public float x, y, z; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLContextSettings
    {
        public int version;
        public IntPtr logCallback;
        public IntPtr allocateCallback;
        public IntPtr freeCallback;
        public int simdLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAudioSettings
    {
        public int samplingRate;
        public int frameSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLHRTFSettings
    {
        public int type; // IPL_HRTFTYPE_DEFAULT = 0
        public IntPtr sofaFileName;
        public IntPtr sofaData;
        public int sofaDataSize;
        public float volume;
        public int normType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLBinauralEffectSettings
    {
        public IntPtr hrtf;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAudioBuffer
    {
        public int numChannels;
        public int numSamples;
        public IntPtr data; // float**
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLBinauralEffectParams
    {
        public IPLVector3 direction;
        public int interpolation; // IPL_HRTFINTERPOLATION_NEAREST = 0
        public float spatialBlend;
        public IntPtr hrtf;
        public int peakDelays;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int iplContextCreate(ref IPLContextSettings settings, out IntPtr context);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void iplContextRelease(ref IntPtr context);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int iplHRTFCreate(IntPtr context, ref IPLAudioSettings audioSettings, ref IPLHRTFSettings hrtfSettings, out IntPtr hrtf);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void iplHRTFRelease(ref IntPtr hrtf);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int iplBinauralEffectCreate(IntPtr context, ref IPLAudioSettings audioSettings, ref IPLBinauralEffectSettings effectSettings, out IntPtr effect);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void iplBinauralEffectRelease(ref IntPtr effect);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int iplBinauralEffectApply(IntPtr effect, ref IPLBinauralEffectParams parameters, ref IPLAudioBuffer inBuffer, ref IPLAudioBuffer outBuffer);
}
