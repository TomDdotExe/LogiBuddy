using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace SpotifyGameRadio.Core.Audio;

public record AudioSourceInfo(string ProcessName, int ProcessId, string DisplayName);

/// Lists running processes that currently have an active WASAPI audio
/// session on the default render device, so the user can pick a source
/// (Spotify, a browser, Discord, etc.) from a dropdown.
public static class AudioSessionEnumerator
{
    public static IReadOnlyList<AudioSourceInfo> ListActiveSources()
    {
        var results = new List<AudioSourceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        var sessions = defaultDevice.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            int pid = (int)session.GetProcessID;
            if (pid == 0) continue;

            string processName;
            try
            {
                using var process = Process.GetProcessById(pid);
                processName = process.ProcessName;
            }
            catch (ArgumentException)
            {
                continue; // process exited between enumeration and lookup
            }

            if (results.Any(r => r.ProcessId == pid)) continue;

            string displayName = string.IsNullOrWhiteSpace(session.DisplayName)
                ? processName
                : session.DisplayName;

            results.Add(new AudioSourceInfo(processName, pid, displayName));
        }

        return results;
    }
}
