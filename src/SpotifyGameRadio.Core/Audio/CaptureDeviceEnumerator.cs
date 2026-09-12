using NAudio.CoreAudioApi;

namespace SpotifyGameRadio.Core.Audio;

public record CaptureDeviceInfo(string Id, string FriendlyName);

/// Active capture (microphone) endpoints for the Voice Chat device picker.
public static class CaptureDeviceEnumerator
{
    public static IReadOnlyList<CaptureDeviceInfo> ListCaptureDevices()
    {
        var results = new List<CaptureDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                    results.Add(new CaptureDeviceInfo(device.ID, device.FriendlyName));
            }
        }
        catch (Exception)
        {
            // No audio subsystem / no devices — return whatever was collected.
        }
        return results;
    }
}
