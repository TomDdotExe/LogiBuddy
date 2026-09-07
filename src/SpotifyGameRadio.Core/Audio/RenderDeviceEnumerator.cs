using NAudio.CoreAudioApi;

namespace SpotifyGameRadio.Core.Audio;

public record RenderDeviceInfo(string Id, string FriendlyName);

/// Active render (playback) endpoints for the routing UI, plus a name
/// heuristic used to pre-select a virtual-cable device.
public static class RenderDeviceEnumerator
{
    private static readonly string[] VirtualCableMarkers =
        { "vb-audio", "vb audio", "cable", "voicemeeter", "virtual" };

    public static IReadOnlyList<RenderDeviceInfo> ListRenderDevices()
    {
        var results = new List<RenderDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                    results.Add(new RenderDeviceInfo(device.ID, device.FriendlyName));
            }
        }
        catch (Exception)
        {
            // No audio subsystem / no devices — return whatever was collected.
        }
        return results;
    }

    public static bool LooksLikeVirtualCable(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;
        string lower = friendlyName.ToLowerInvariant();
        return VirtualCableMarkers.Any(m => lower.Contains(m));
    }
}
