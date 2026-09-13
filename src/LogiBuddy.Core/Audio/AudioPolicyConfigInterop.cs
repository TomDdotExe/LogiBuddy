using System;
using System.Runtime.InteropServices;

namespace LogiBuddy.Core.Audio;

internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }

internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

/// Undocumented Windows 11 per-application audio endpoint routing.
///
/// The activation factory for "Windows.Media.Internal.AudioPolicyConfig" exposes
/// IAudioPolicyConfig, whose vtable no public header declares. Rather than a
/// [ComImport] interface with a fragile count of padding slots, this walks the
/// QI'd interface's vtable by absolute index (25/26/27) — the approach used by
/// SoundSwitch. HRESULT 0x80070057 (E_INVALIDARG) from Set/Get is the documented
/// "target process has no audio session" signal (PROCESS_NO_AUDIO), not an error.
internal static class AudioPolicyConfigInterop
{
    public const int E_INVALIDARG = unchecked((int)0x80070057); // == PROCESS_NO_AUDIO here

    private const string ClassId = "Windows.Media.Internal.AudioPolicyConfig";
    private const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string MmdevapiToken = @"\\?\SWD#MMDEVAPI#";

    // Absolute vtable slot indices on the QI'd IAudioPolicyConfig interface.
    private const int Slot_SetPersistedDefaultAudioEndpoint = 25;
    private const int Slot_GetPersistedDefaultAudioEndpoint = 26;
    private const int Slot_ClearAllPersistedApplicationDefaultEndpoints = 27;

    private static readonly Guid IID_IInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    // Known IAudioPolicyConfig IIDs across Windows builds, oldest -> newest.
    private static readonly Guid[] KnownIids =
    {
        new("2a59116d-6c4f-45e0-a74f-707e3fef9258"), // pre-21H2
        new("32aa8e18-6496-4e24-9f94-b800e7eccc45"), // 10.0.16299
        new("ab3d4648-e242-459f-b02f-541c70306324"), // 21H2 / Windows 11
    };

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetEndpointFn(IntPtr self, uint processId, EDataFlow flow, ERole role, IntPtr deviceIdHString);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetEndpointFn(IntPtr self, uint processId, EDataFlow flow, ERole role, ref IntPtr deviceIdHString);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearAllFn(IntPtr self);

    /// Activates the factory and QIs to the newest supported IAudioPolicyConfig
    /// IID. Returns a raw interface pointer the caller MUST Marshal.Release once.
    private static IntPtr CreateInterface()
    {
        IntPtr classIdHString = IntPtr.Zero;
        IntPtr factory = IntPtr.Zero;
        try
        {
            int hr = WindowsCreateString(ClassId, ClassId.Length, out classIdHString);
            Marshal.ThrowExceptionForHR(hr);

            Guid iinspectable = IID_IInspectable;
            hr = RoGetActivationFactory(classIdHString, ref iinspectable, out factory);
            Marshal.ThrowExceptionForHR(hr);

            for (int i = KnownIids.Length - 1; i >= 0; i--)
            {
                Guid iid = KnownIids[i];
                if (Marshal.QueryInterface(factory, ref iid, out IntPtr iface) == 0 && iface != IntPtr.Zero)
                    return iface;
            }
            throw new InvalidOperationException("Activation factory exposes no known IAudioPolicyConfig interface.");
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (classIdHString != IntPtr.Zero) WindowsDeleteString(classIdHString);
        }
    }

    private static T VtableMethod<T>(IntPtr iface, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(iface);
        IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static string PackDeviceId(string mmDeviceId) =>
        string.IsNullOrEmpty(mmDeviceId) ? "" : $"{MmdevapiToken}{mmDeviceId}{RenderInterfaceSuffix}";

    private static string UnpackDeviceId(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return "";
        if (endpointId.StartsWith(MmdevapiToken, StringComparison.OrdinalIgnoreCase))
            endpointId = endpointId[MmdevapiToken.Length..];
        if (endpointId.EndsWith(RenderInterfaceSuffix, StringComparison.OrdinalIgnoreCase))
            endpointId = endpointId[..^RenderInterfaceSuffix.Length];
        return endpointId;
    }

    /// True if the activation factory can be created and QI'd to a known IID.
    public static bool ProbeSupported()
    {
        try
        {
            IntPtr iface = CreateInterface();
            Marshal.Release(iface);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// Returns S_OK (0), E_INVALIDARG (target has no audio — no-op), or another
    /// failing HRESULT. mmDeviceId "" clears the override.
    public static int SetEndpoint(uint processId, ERole role, string mmDeviceId)
    {
        IntPtr iface = CreateInterface();
        IntPtr hstr = IntPtr.Zero;
        try
        {
            string endpointId = PackDeviceId(mmDeviceId);
            Marshal.ThrowExceptionForHR(WindowsCreateString(endpointId, endpointId.Length, out hstr));
            var set = VtableMethod<SetEndpointFn>(iface, Slot_SetPersistedDefaultAudioEndpoint);
            return set(iface, processId, EDataFlow.eRender, role, hstr);
        }
        finally
        {
            if (hstr != IntPtr.Zero) WindowsDeleteString(hstr);
            Marshal.Release(iface);
        }
    }

    /// (hr, mmDeviceId). Any non-zero hr (incl. E_INVALIDARG "no audio") yields "".
    public static (int hr, string mmDeviceId) GetEndpoint(uint processId, ERole role)
    {
        IntPtr iface = CreateInterface();
        IntPtr hstr = IntPtr.Zero;
        try
        {
            var get = VtableMethod<GetEndpointFn>(iface, Slot_GetPersistedDefaultAudioEndpoint);
            int hr = get(iface, processId, EDataFlow.eRender, role, ref hstr);
            if (hr != 0 || hstr == IntPtr.Zero) return (hr, "");
            IntPtr buf = WindowsGetStringRawBuffer(hstr, out uint len);
            string endpointId = buf == IntPtr.Zero ? "" : Marshal.PtrToStringUni(buf, (int)len) ?? "";
            return (hr, UnpackDeviceId(endpointId));
        }
        finally
        {
            if (hstr != IntPtr.Zero) WindowsDeleteString(hstr);
            Marshal.Release(iface);
        }
    }
}
