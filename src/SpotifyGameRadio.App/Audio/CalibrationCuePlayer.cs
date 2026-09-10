using NAudio.Wave;

namespace SpotifyGameRadio.App.Audio;

/// Short audio cues for freelook calibration — the game has focus, so the
/// user needs to hear progress, not see it. Fire-and-forget; a failed cue
/// (no device, etc.) is swallowed so it can never break calibration.
public sealed class CalibrationCuePlayer : IDisposable
{
    private const int SampleRate = 48000;
    private const double Gain = 0.18;
    private const int EdgeFadeMs = 8; // raised-cosine ends so there is no click

    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private bool _disposed;

    /// One 880 Hz blip: a limit was marked, the next prompt is showing.
    public void Captured() => Play((880.0, 120));

    /// Rising 660/880/1175 Hz: calibration finished and the profile updated.
    public void Done() => Play((660.0, 90), (880.0, 90), (1175.0, 150));

    /// One low 300 Hz blip: timed out, cancelled, or the sweep was too small.
    public void Failed() => Play((300.0, 260));

    private void Play(params (double freqHz, int ms)[] segments)
    {
        try
        {
            var samples = Render(segments);
            lock (_lock)
            {
                if (_disposed) return;
                _output?.Dispose();
                _output = new WaveOutEvent();
                _output.Init(new FloatBuffer(samples, SampleRate));
                _output.Play();
            }
        }
        catch
        {
            // A missing beep must never break calibration.
        }
    }

    private static float[] Render((double freqHz, int ms)[] segments)
    {
        int total = 0;
        foreach (var (_, ms) in segments) total += SampleRate * ms / 1000;
        var buffer = new float[total];

        int fade = SampleRate * EdgeFadeMs / 1000;
        int pos = 0;
        foreach (var (freq, ms) in segments)
        {
            int len = SampleRate * ms / 1000;
            for (int i = 0; i < len; i++)
            {
                double env = 1.0;
                if (i < fade) env = 0.5 * (1 - Math.Cos(Math.PI * i / fade));
                else if (i >= len - fade) env = 0.5 * (1 - Math.Cos(Math.PI * (len - 1 - i) / fade));
                buffer[pos + i] = (float)(Gain * env * Math.Sin(2 * Math.PI * freq * i / SampleRate));
            }
            pos += len;
        }
        return buffer;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _output?.Dispose();
            _output = null;
        }
    }

    /// Plays a pre-rendered mono float buffer once, then silence.
    private sealed class FloatBuffer : ISampleProvider
    {
        private readonly float[] _data;
        private int _pos;

        public FloatBuffer(float[] data, int sampleRate)
        {
            _data = data;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}
