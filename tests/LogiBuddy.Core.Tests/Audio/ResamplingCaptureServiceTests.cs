using NAudio.Wave;
using LogiBuddy.Core.Audio;
using Xunit;

namespace LogiBuddy.Core.Tests.Audio;

public class ResamplingCaptureServiceTests
{
    private static ControllableCapture InnerAt(int sampleRate, int channels = 2) =>
        new() { Format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels) };

    [Fact]
    public void InnerAlreadyAt48k_ForwardsBufferUntouched()
    {
        var inner = InnerAt(48000);
        var svc = new ResamplingCaptureService(inner);

        float[]? forwarded = null;
        svc.DataAvailable += (_, s) => forwarded = s;

        var block = new[] { 0.1f, -0.1f, 0.2f, -0.2f };
        inner.PushData(block);

        Assert.Same(block, forwarded);
    }

    [Fact]
    public void Format_AlwaysReports48k_WithInnerChannelCount()
    {
        var svc = new ResamplingCaptureService(InnerAt(44100, channels: 6));

        Assert.Equal(48000, svc.Format.SampleRate);
        Assert.Equal(6, svc.Format.Channels);
    }

    [Fact]
    public void InnerBelow48k_ResamplesUpToRoughly48k_PreservingLevel()
    {
        var inner = InnerAt(24000); // clean 2x ratio
        var svc = new ResamplingCaptureService(inner);

        var outFrames = 0;
        var samples = new List<float>();
        svc.DataAvailable += (_, s) => { outFrames += s.Length / 2; samples.AddRange(s); };

        // 1000 stereo frames of a constant 0.5 — a DC signal resamples to itself,
        // so steady-state output samples should still sit near 0.5.
        var block = new float[1000 * 2];
        Array.Fill(block, 0.5f);
        inner.PushData(block);

        // ~2x the frames, allowing for the resampler's filter warm-up latency.
        Assert.InRange(outFrames, 1800, 2000);
        // Look at the steady-state middle, past any start-up transient.
        var mid = samples.GetRange(samples.Count / 2 - 50, 100);
        Assert.All(mid, v => Assert.InRange(v, 0.45f, 0.55f));
    }

    [Fact]
    public void InnerRateChangesBetweenBlocks_SecondBlockIsResampled()
    {
        var inner = InnerAt(48000);
        var svc = new ResamplingCaptureService(inner);

        var lastForwarded = new List<float[]>();
        svc.DataAvailable += (_, s) => lastForwarded.Add(s);

        var first = new float[100 * 2];
        inner.PushData(first);
        Assert.Same(first, lastForwarded[0]); // 48k -> passthrough

        inner.Format = WaveFormat.CreateIeeeFloatWaveFormat(24000, 2);
        var second = new float[100 * 2];
        inner.PushData(second);

        Assert.NotSame(second, lastForwarded[1]);
        Assert.True(lastForwarded[1].Length > second.Length); // upsampled
    }

    [Fact]
    public void Start_Stop_Status_DelegateToInner()
    {
        var inner = InnerAt(48000);
        var svc = new ResamplingCaptureService(inner);

        var statuses = new List<AudioCaptureStatus>();
        svc.StatusChanged += (_, s) => statuses.Add(s);

        svc.Start("Spotify");
        inner.RaiseCapturing();
        svc.Stop();

        Assert.Equal("Spotify", inner.StartedWith);
        Assert.True(inner.Stopped);
        Assert.Equal(new[] { AudioCaptureStatus.Capturing }, statuses);
    }
}
