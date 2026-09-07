namespace SpotifyGameRadio.Core.Audio;

public sealed record AppAudioRoute(string Console, string Multimedia, string Communications)
{
    public static AppAudioRoute None { get; } = new("", "", "");
}

public sealed class SourceRoutingException : Exception
{
    public SourceRoutingException(string message, int hresult) : base(message) => HResult = hresult;
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
        var (_, console) = AudioPolicyConfigInterop.GetEndpoint((uint)processId, ERole.eConsole);
        var (_, multimedia) = AudioPolicyConfigInterop.GetEndpoint((uint)processId, ERole.eMultimedia);
        var (_, comms) = AudioPolicyConfigInterop.GetEndpoint((uint)processId, ERole.eCommunications);
        return new AppAudioRoute(console, multimedia, comms);
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

    private static void SetRole(int processId, ERole role, string mmDeviceId)
    {
        int hr = AudioPolicyConfigInterop.SetEndpoint((uint)processId, role, mmDeviceId ?? "");
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
