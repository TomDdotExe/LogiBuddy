using System.Runtime.InteropServices;

namespace SpotifyGameRadio.Core.Audio;

/// Raw COM interop for Windows 10 20H1+ per-process WASAPI loopback capture
/// (ActivateAudioInterfaceAsync with AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK).
/// This is not exposed by NAudio, so IAudioClient/IAudioCaptureClient are
/// declared directly against their (stable, decades-old) WASAPI COM GUIDs
/// rather than depending on NAudio's higher-level wrapper.
internal static class WasapiProcessLoopbackInterop
{
    public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    public const ushort VT_BLOB = 0x41;
    public const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// Minimal PROPVARIANT laid out for the VT_BLOB case only (what
    /// ActivateAudioInterfaceAsync needs to receive AUDIOCLIENT_ACTIVATION_PARAMS):
    /// 8-byte header (vt + 3 reserved WORDs) then, 8-byte aligned, {cbSize:uint, pBlobData:IntPtr}.
    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint blobCbSize;
        [FieldOffset(16)] public IntPtr blobPBlobData;
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}

internal class ActivationCompletionHandler : WasapiProcessLoopbackInterop.IActivateAudioInterfaceCompletionHandler
{
    private readonly ManualResetEvent _completedEvent = new(initialState: false);
    public object? ActivatedInterface { get; private set; }
    public int ActivateResult { get; private set; }

    public void ActivateCompleted(WasapiProcessLoopbackInterop.IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        activateOperation.GetActivateResult(out int result, out object iface);
        ActivateResult = result;
        ActivatedInterface = iface;
        _completedEvent.Set();
    }

    public void Wait() => _completedEvent.WaitOne(TimeSpan.FromSeconds(5));
}
