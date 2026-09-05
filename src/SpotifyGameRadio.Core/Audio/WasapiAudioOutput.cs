using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

public class WasapiAudioOutput : IAudioOutputService
{
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;

    public event EventHandler? DeviceLost;

    public void Start(string deviceId)
    {
        Stop();

        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = enumerator.GetDevice(deviceId);
        }
        catch (Exception)
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        _buffer = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(200)
        };

        _output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
        _output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) DeviceLost?.Invoke(this, EventArgs.Empty);
        };
        _output.Init(_buffer);
        _output.Play();
    }

    public void Write(float[] stereoInterleaved, int count)
    {
        if (_buffer is null) return;
        var bytes = new byte[count * 4];
        Buffer.BlockCopy(stereoInterleaved, 0, bytes, 0, bytes.Length);
        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Stop()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }

    public void Dispose() => Stop();
}
