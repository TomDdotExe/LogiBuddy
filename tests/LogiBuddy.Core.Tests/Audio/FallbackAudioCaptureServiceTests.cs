using NAudio.Wave;
using LogiBuddy.Core.Audio;
using Xunit;

namespace LogiBuddy.Core.Tests.Audio;

/// A capture stand-in whose status/data events are driven by the test.
public class ControllableCapture : IAudioCaptureService
{
    public WaveFormat Format { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;

    public string? StartedWith { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }

    public void Start(string processName) => StartedWith = processName;
    public void Stop() => Stopped = true;
    public void Dispose() => Disposed = true;

    public void RaiseCapturing() => StatusChanged?.Invoke(this, AudioCaptureStatus.Capturing);
    public void RaiseError() => StatusChanged?.Invoke(this, AudioCaptureStatus.Error);
    public void RaiseNoSource() => StatusChanged?.Invoke(this, AudioCaptureStatus.NoSource);
    public void PushData(float[] samples) => DataAvailable?.Invoke(this, samples);
}

public class FallbackAudioCaptureServiceTests
{
    // Runs the swap inline so tests stay deterministic (production uses Task.Run
    // to get off the primary's worker thread).
    private static readonly Action<Action> RunInline = a => a();

    [Fact]
    public void PrimaryCapturingNormally_ForwardsDataAndNeverBuildsFallback()
    {
        var primary = new ControllableCapture();
        var fallbackBuilt = 0;
        var svc = new FallbackAudioCaptureService(
            () => primary,
            () => { fallbackBuilt++; return new ControllableCapture(); },
            RunInline);

        var received = new List<float[]>();
        svc.DataAvailable += (_, s) => received.Add(s);

        svc.Start("Spotify");
        primary.RaiseCapturing();
        primary.PushData(new[] { 0.1f, 0.2f });

        Assert.Equal("Spotify", primary.StartedWith);
        Assert.Single(received);
        Assert.Equal(0, fallbackBuilt);
    }

    [Fact]
    public void PrimaryErrorsBeforeEverCapturing_SwapsToFallback()
    {
        var primary = new ControllableCapture();
        var fallback = new ControllableCapture();
        var svc = new FallbackAudioCaptureService(() => primary, () => fallback, RunInline);

        var received = new List<float[]>();
        svc.DataAvailable += (_, s) => received.Add(s);
        string? notice = null;
        svc.Notice += m => notice = m;

        svc.Start("Spotify");
        primary.RaiseError();

        Assert.True(primary.Stopped);
        Assert.True(primary.Disposed);
        Assert.Equal("Spotify", fallback.StartedWith);
        Assert.NotNull(notice);

        // Data now flows from the fallback, not the (dead) primary.
        fallback.PushData(new[] { 0.3f, 0.4f });
        Assert.Single(received);
    }

    [Fact]
    public void PrimaryErrorBeforeCapturing_DoesNotForwardThatErrorToConsumers()
    {
        var primary = new ControllableCapture();
        var svc = new FallbackAudioCaptureService(
            () => primary, () => new ControllableCapture(), RunInline);

        var statuses = new List<AudioCaptureStatus>();
        svc.StatusChanged += (_, s) => statuses.Add(s);

        svc.Start("Spotify");
        primary.RaiseError();

        // The triggering Error is swallowed — the fallback reports its own status.
        Assert.DoesNotContain(AudioCaptureStatus.Error, statuses);
    }

    [Fact]
    public void PrimaryErrorsAfterCapturingSuccessfully_DoesNotSwap()
    {
        var primary = new ControllableCapture();
        var fallbackBuilt = 0;
        var svc = new FallbackAudioCaptureService(
            () => primary,
            () => { fallbackBuilt++; return new ControllableCapture(); },
            RunInline);

        var statuses = new List<AudioCaptureStatus>();
        svc.StatusChanged += (_, s) => statuses.Add(s);

        svc.Start("Spotify");
        primary.RaiseCapturing();
        primary.RaiseError();

        Assert.Equal(0, fallbackBuilt);
        Assert.False(primary.Stopped);
        // A post-success Error is a runtime loss — forward it, don't hide it.
        Assert.Contains(AudioCaptureStatus.Error, statuses);
    }

    [Fact]
    public void FallbackAlsoErrors_ForwardsItOnceAndDoesNotSwapAgain()
    {
        var primary = new ControllableCapture();
        var fallback = new ControllableCapture();
        var fallbackBuilt = 0;
        var svc = new FallbackAudioCaptureService(
            () => primary,
            () => { fallbackBuilt++; return fallback; },
            RunInline);

        var statuses = new List<AudioCaptureStatus>();
        svc.StatusChanged += (_, s) => statuses.Add(s);

        svc.Start("Spotify");
        primary.RaiseError();   // triggers the one swap
        fallback.RaiseError();  // fallback is broken too

        Assert.Equal(1, fallbackBuilt);
        Assert.Equal(new[] { AudioCaptureStatus.Error }, statuses);
    }

    [Fact]
    public void Stop_BeforeSwap_StopsPrimary_AfterSwap_StopsFallback()
    {
        var primary = new ControllableCapture();
        var fallback = new ControllableCapture();

        var beforeSwap = new FallbackAudioCaptureService(() => primary, () => fallback, RunInline);
        beforeSwap.Start("Spotify");
        beforeSwap.Stop();
        Assert.True(primary.Stopped);
        Assert.False(fallback.Stopped);

        var primary2 = new ControllableCapture();
        var fallback2 = new ControllableCapture();
        var afterSwap = new FallbackAudioCaptureService(() => primary2, () => fallback2, RunInline);
        afterSwap.Start("Spotify");
        primary2.RaiseError();
        afterSwap.Stop();
        Assert.True(fallback2.Stopped);
    }

    [Fact]
    public void Format_ReflectsTheActiveInnerBeforeAndAfterSwap()
    {
        var primary = new ControllableCapture { Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2) };
        var fallback = new ControllableCapture { Format = new WaveFormat(44100, 32, 6) };
        var svc = new FallbackAudioCaptureService(() => primary, () => fallback, RunInline);

        svc.Start("Spotify");
        Assert.Equal(2, svc.Format.Channels);
        Assert.Equal(48000, svc.Format.SampleRate);

        primary.RaiseError();
        Assert.Equal(6, svc.Format.Channels);
        Assert.Equal(44100, svc.Format.SampleRate);
    }
}
