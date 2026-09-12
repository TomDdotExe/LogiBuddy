using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

public class MicrophoneCapture : IMicrophoneCapture
{
    private const int TargetSampleRate = 16000;

    private WasapiCapture? _capture;
    private readonly List<float> _buffer = new();
    private readonly object _bufferLock = new();
    private int _channels;

    public void Start(string? deviceId)
    {
        if (_capture is not null) return; // already capturing

        try
        {
            MMDevice device;
            using var enumerator = new MMDeviceEnumerator();
            device = string.IsNullOrEmpty(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : enumerator.GetDevice(deviceId);

            _capture = new WasapiCapture(device);
            _channels = _capture.WaveFormat.Channels;
            lock (_bufferLock) _buffer.Clear();

            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
        }
        catch (Exception)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int sampleCount = e.BytesRecorded / 4; // 32-bit float samples
        lock (_bufferLock)
        {
            for (int i = 0; i < sampleCount; i++)
                _buffer.Add(BitConverter.ToSingle(e.Buffer, i * 4));
        }
    }

    public float[] Stop()
    {
        if (_capture is null) return Array.Empty<float>();

        int inRate = _capture.WaveFormat.SampleRate;
        _capture.StopRecording();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _capture = null;

        float[] captured;
        lock (_bufferLock)
        {
            captured = _buffer.ToArray();
            _buffer.Clear();
        }
        if (captured.Length == 0) return captured;

        float[] mono = _channels <= 1 ? captured : MixDownToMono(captured, _channels);
        return inRate == TargetSampleRate ? mono : Resample(mono, inRate);
    }

    private static float[] MixDownToMono(float[] interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        return mono;
    }

    private static float[] Resample(float[] monoSamples, int inRate)
    {
        var resampler = new WdlResampler();
        resampler.SetMode(true, 2, false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(true);
        resampler.SetRates(inRate, TargetSampleRate);

        int inFrames = monoSamples.Length;
        int framesNeeded = resampler.ResamplePrepare(inFrames, 1, out float[] inBuffer, out int inOffset);
        int framesToCopy = Math.Min(framesNeeded, inFrames);
        Array.Copy(monoSamples, 0, inBuffer, inOffset, framesToCopy);

        int outCapacityFrames = (int)Math.Ceiling(inFrames * (double)TargetSampleRate / inRate) + 64;
        var outBuffer = new float[outCapacityFrames];
        int outFrames = resampler.ResampleOut(outBuffer, 0, framesToCopy, outCapacityFrames, 1);

        if (outFrames == outBuffer.Length) return outBuffer;
        var trimmed = new float[outFrames];
        Array.Copy(outBuffer, trimmed, outFrames);
        return trimmed;
    }
}
