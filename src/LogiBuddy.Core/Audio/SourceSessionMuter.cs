using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace LogiBuddy.Core.Audio;

public interface ISourceSessionMuter
{
    /// Mutes every Windows audio session belonging to a process named
    /// processName. Best-effort: any failure (COM, a process exiting
    /// mid-scan, no default render device) is swallowed, never thrown —
    /// matches WindowsAppAudioRouter.TrySetRole's precedent.
    void Mute(string processName);

    /// Unmutes every session belonging to processName. Harmless no-op if
    /// nothing was muted (e.g. AutoMuteSource was off, or the process
    /// wasn't running). Best-effort, same as Mute.
    void Unmute(string processName);
}

/// Mutes/unmutes a process's own Windows audio session directly via
/// NAudio's AudioSessionControl.SimpleAudioVolume, instead of rerouting its
/// output device like WindowsAppAudioRouter — works on Windows 10 and 11,
/// no virtual cable required.
public sealed class NAudioSourceSessionMuter : ISourceSessionMuter
{
    public void Mute(string processName) => SetMuted(processName, true);
    public void Unmute(string processName) => SetMuted(processName, false);

    private static void SetMuted(string processName, bool muted)
    {
        if (string.IsNullOrEmpty(processName)) return;

        try
        {
            var pids = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
            if (pids.Count == 0) return;

            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var sessions = defaultDevice.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (!pids.Contains((int)session.GetProcessID)) continue;
                try { session.SimpleAudioVolume.Mute = muted; }
                catch (Exception) { /* best effort: session may have just ended */ }
            }
        }
        catch (Exception) { /* best effort: no default render device, COM failure, etc. */ }
    }
}
