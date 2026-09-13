using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LogiBuddy.App.Audio;

/// Resolves an IWavePlayer targeting the configured radio output device (the
/// game has focus during voice/calibration cues, so routing to the radio's
/// own device beats the system default). Shared by CalibrationAnnouncer and
/// VoiceCuePlayer so both fire-and-forget cue players resolve the
/// output device the same way.
internal static class DeviceOutputPlayer
{
    public static IWavePlayer Create(Func<string?> deviceIdProvider)
    {
        string? id = deviceIdProvider();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                using var mm = new MMDeviceEnumerator();
                foreach (var device in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (device.ID == id)
                        return new WasapiOut(device, AudioClientShareMode.Shared, false, 120);
                    device.Dispose();
                }
            }
            catch
            {
                // Fall through to the default endpoint.
            }
        }
        return new WaveOutEvent();
    }
}
