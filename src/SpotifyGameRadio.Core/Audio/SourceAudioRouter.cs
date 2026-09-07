namespace SpotifyGameRadio.Core.Audio;

public sealed record AppAudioRoute(string Console, string Multimedia, string Communications)
{
    public static AppAudioRoute None { get; } = new("", "", "");
}

public sealed class SourceRoutingException : Exception
{
    // E_INVALIDARG. The undocumented IAudioPolicyConfig API returns this when the
    // target process has no audio session (PROCESS_NO_AUDIO) — normal, not a
    // failure. Hard-coded because AudioPolicyConfigInterop.E_INVALIDARG lives on
    // an internal class that public callers of this exception can't name.
    private const int ProcessNoAudioHresult = unchecked((int)0x80070057);

    public SourceRoutingException(string message, int hresult) : base(message)
    {
        HResult = hresult;
        NoActiveAudio = hresult == ProcessNoAudioHresult;
    }

    /// True when the failure was only "this process has no audio session yet".
    /// Callers routing a multi-process app should skip such pids rather than
    /// treating them as a routing failure.
    public bool NoActiveAudio { get; }
}

public interface ISourceAudioRouter
{
    bool IsSupported { get; }
    AppAudioRoute GetCurrentRoute(int processId);
    void RouteProcess(int processId, string renderDeviceId);
    void RestoreProcess(int processId, AppAudioRoute previous);
}

/// Routes a process's default render endpoint using the undocumented Windows 11
/// IAudioPolicyConfig API. IsSupported == false on Windows 10 or if the
/// activation factory cannot be reached.
public sealed class WindowsAppAudioRouter : ISourceAudioRouter
{
    private readonly bool _supported;

    public WindowsAppAudioRouter()
    {
        _supported = Environment.OSVersion.Version.Build >= 22000
                     && AudioPolicyConfigInterop.ProbeSupported();
    }

    public bool IsSupported => _supported;

    public AppAudioRoute GetCurrentRoute(int processId)
    {
        if (!_supported) return AppAudioRoute.None;
        return new AppAudioRoute(
            GetRole(processId, ERole.eConsole),
            GetRole(processId, ERole.eMultimedia),
            GetRole(processId, ERole.eCommunications));
    }

    public void RouteProcess(int processId, string renderDeviceId)
    {
        if (!_supported) throw new SourceRoutingException("Per-app routing is not supported on this Windows version.", 0);
        SetRole(processId, ERole.eConsole, renderDeviceId);
        SetRole(processId, ERole.eMultimedia, renderDeviceId);
        SetRole(processId, ERole.eCommunications, renderDeviceId);
    }

    public void RestoreProcess(int processId, AppAudioRoute previous)
    {
        if (!_supported) return;
        // Best-effort: the process may be gone or silent by restore time.
        TrySetRole(processId, ERole.eConsole, previous.Console);
        TrySetRole(processId, ERole.eMultimedia, previous.Multimedia);
        TrySetRole(processId, ERole.eCommunications, previous.Communications);
    }

    /// Reads one role's endpoint override. "" means no override. Throws
    /// SourceRoutingException for any real failure — a failed read must never be
    /// mistaken for "no override", or restoring it later would wipe the user's
    /// own per-app routing.
    private static string GetRole(int processId, ERole role)
    {
        int hr;
        string mmDeviceId;
        try
        {
            (hr, mmDeviceId) = AudioPolicyConfigInterop.GetEndpoint((uint)processId, role);
        }
        catch (Exception ex)
        {
            throw new SourceRoutingException($"Reading the {role} endpoint failed: {ex.Message}", ex.HResult);
        }

        if (hr == 0) return mmDeviceId;
        // Session-less process: genuinely has no override.
        if (hr == AudioPolicyConfigInterop.E_INVALIDARG) return "";
        throw new SourceRoutingException($"Reading the {role} endpoint failed (hresult 0x{hr:X}).", hr);
    }

    /// Throws SourceRoutingException with NoActiveAudio == true for a
    /// session-less process; callers routing a multi-pid app (Spotify runs
    /// several helper processes) should skip those pids rather than fail.
    private static void SetRole(int processId, ERole role, string mmDeviceId)
    {
        int hr;
        try
        {
            hr = AudioPolicyConfigInterop.SetEndpoint((uint)processId, role, mmDeviceId ?? "");
        }
        catch (Exception ex)
        {
            throw new SourceRoutingException($"Setting the {role} endpoint failed: {ex.Message}", ex.HResult);
        }

        if (hr == 0) return;
        if (hr == AudioPolicyConfigInterop.E_INVALIDARG)
            throw new SourceRoutingException("The source has no active audio yet — start playback in it, then Start.", hr);
        throw new SourceRoutingException($"Setting the {role} endpoint failed (hresult 0x{hr:X}).", hr);
    }

    private static void TrySetRole(int processId, ERole role, string mmDeviceId)
    {
        try { AudioPolicyConfigInterop.SetEndpoint((uint)processId, role, mmDeviceId ?? ""); }
        catch (Exception) { /* best effort */ }
    }
}
