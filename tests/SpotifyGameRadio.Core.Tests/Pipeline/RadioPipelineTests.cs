using SpotifyGameRadio.Core.Audio;
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Dsp;
using SpotifyGameRadio.Core.Pipeline;
using SpotifyGameRadio.Core.Spatial;
using SpotifyGameRadio.Core.Tracking;
using NAudio.Wave;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Pipeline;

public class FakeCaptureService : IAudioCaptureService
{
    public WaveFormat Format { get; }
    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;
    public bool Started { get; private set; }

    public FakeCaptureService(int channels = 1)
    {
        Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
    }

    public void Start(string processName) { Started = true; StatusChanged?.Invoke(this, AudioCaptureStatus.Capturing); }
    public void Stop() { Started = false; }
    public void Dispose() { }

    public void PushSamples(float[] samples) => DataAvailable?.Invoke(this, samples);
    public void RaiseNoSource() => StatusChanged?.Invoke(this, AudioCaptureStatus.NoSource);
}

public class FakeOutputService : IAudioOutputService
{
    public event EventHandler? DeviceLost;
    public List<float[]> WrittenBuffers { get; } = new();
    public List<string> StartedDeviceIds { get; } = new();
    public string? LastDeviceId => StartedDeviceIds.Count == 0 ? null : StartedDeviceIds[^1];

    public void Start(string deviceId) => StartedDeviceIds.Add(deviceId);
    public void Write(float[] stereoInterleaved, int count) => WrittenBuffers.Add(stereoInterleaved[..(count)]);
    public void Stop() { }
    public void Dispose() { }
    public void RaiseDeviceLost() => DeviceLost?.Invoke(this, EventArgs.Empty);
}

public class FakeSpatializer : ISpatializer
{
    public void SetSourcePosition(float x, float y, float z) { }
    public void SetListenerOrientation(float yawDegrees, float pitchDegrees) { }
    public void Process(float[] monoInput, int count, float[] stereoOutputInterleaved)
    {
        for (int i = 0; i < count; i++)
        {
            stereoOutputInterleaved[i * 2] = monoInput[i];
            stereoOutputInterleaved[i * 2 + 1] = monoInput[i];
        }
    }
}

public class PanningFakeSpatializer : ISpatializer
{
    public void SetSourcePosition(float x, float y, float z) { }
    public void SetListenerOrientation(float yawDegrees, float pitchDegrees) { }
    public void Process(float[] monoInput, int count, float[] stereoOutputInterleaved)
    {
        for (int i = 0; i < count; i++)
        {
            stereoOutputInterleaved[i * 2] = monoInput[i]; // hard left
            stereoOutputInterleaved[i * 2 + 1] = 0f;
        }
    }
}

public class RecordingSpatializer : ISpatializer
{
    public (float x, float y, float z) LastPosition { get; private set; }
    public void SetSourcePosition(float x, float y, float z) => LastPosition = (x, y, z);
    public void SetListenerOrientation(float yawDegrees, float pitchDegrees) { }
    public void Process(float[] monoInput, int count, float[] stereoOutputInterleaved)
    {
        for (int i = 0; i < count; i++)
        {
            stereoOutputInterleaved[i * 2] = monoInput[i];
            stereoOutputInterleaved[i * 2 + 1] = monoInput[i];
        }
    }
}

public class RadioPipelineTests
{
    // frameSize is explicit per test: the pipeline processes in fixed-size blocks,
    // so a test that expects one output buffer per push must use a frameSize equal
    // to the mono sample count of that push.
    private static RadioPipeline BuildPipeline(FakeCaptureService capture, FakeOutputService output, out FreelookTracker tracker, int frameSize = 3)
    {
        var profile = new RadioProfile { WetDryMix = 0f }; // dry passthrough for deterministic assertions
        var fakeInput = new SpotifyGameRadio.Core.Tests.Tracking.FakeMouseInputSource();
        tracker = new FreelookTracker(fakeInput, profile);
        var effectChain = new RadioEffectChain(48000f);
        effectChain.ApplyProfile(profile);
        var spatializer = new FakeSpatializer();

        var pipeline = new RadioPipeline(capture, output, spatializer, spatializer, effectChain, tracker, frameSize);
        pipeline.ApplyProfile(profile);
        return pipeline;
    }

    [Fact]
    public void CapturedAudio_FlowsThroughToOutput()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);

        pipeline.Start();
        capture.PushSamples(new float[] { 0.5f, -0.5f, 0.25f });

        Assert.Single(output.WrittenBuffers);
        Assert.Equal(new float[] { 0.5f, 0.5f, -0.5f, -0.5f, 0.25f, 0.25f }, output.WrittenBuffers[0]);
    }

    [Fact]
    public void NoSourceStatus_RaisesWarning()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _);
        string? warning = null;
        pipeline.Warning += (_, msg) => warning = msg;

        pipeline.Start();
        capture.RaiseNoSource();

        Assert.NotNull(warning);
    }

    [Fact]
    public void OutputDeviceLost_RaisesWarning()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _);
        string? warning = null;
        pipeline.Warning += (_, msg) => warning = msg;

        pipeline.Start();
        output.RaiseDeviceLost();

        Assert.NotNull(warning);
    }

    [Fact]
    public void Start_StartsCaptureService()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _);

        pipeline.Start();

        Assert.True(capture.Started);
    }

    [Fact]
    public void TwoChannelCapture_IsDownmixedToMonoBeforeProcessing()
    {
        var capture = new FakeCaptureService(channels: 2);
        var output = new FakeOutputService();
        // 4 interleaved stereo samples downmix to 2 mono samples -> frameSize 2.
        var pipeline = BuildPipeline(capture, output, out _, frameSize: 2);

        pipeline.Start();
        // Two stereo frames: (L=1.0, R=0.0) and (L=0.4, R=0.2) -> mono 0.5, 0.3
        capture.PushSamples(new float[] { 1.0f, 0.0f, 0.4f, 0.2f });

        Assert.Single(output.WrittenBuffers);
        Assert.Equal(new float[] { 0.5f, 0.5f, 0.3f, 0.3f }, output.WrittenBuffers[0]);
    }

    [Fact]
    public void ShortCaptureBuffers_AreAccumulatedIntoFixedSizeBlocks()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _, frameSize: 4);

        pipeline.Start();

        // Two samples is less than one block — nothing may be emitted yet, and
        // nothing may be dropped either.
        capture.PushSamples(new float[] { 0.1f, 0.2f });
        Assert.Empty(output.WrittenBuffers);

        // Two more completes exactly one block, spanning both callbacks in order.
        capture.PushSamples(new float[] { 0.3f, 0.4f });

        Assert.Single(output.WrittenBuffers);
        Assert.Equal(
            new float[] { 0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f, 0.4f, 0.4f },
            output.WrittenBuffers[0]);
    }

    [Fact]
    public void SetOutputDevice_RestartsOutputWithTheNewDeviceId()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _);
        pipeline.Start();

        pipeline.SetOutputDevice("{0.0.0.00000000}.{new-device}");

        Assert.Equal("{0.0.0.00000000}.{new-device}", output.LastDeviceId);
    }

    [Fact]
    public void Volume_ScalesEveryOutputSample()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);
        pipeline.Start();

        var profile = new RadioProfile { WetDryMix = 0f, Volume = 0.5f };
        pipeline.ApplyProfile(profile);
        capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });

        Assert.All(output.WrittenBuffers[0], v => Assert.Equal(0.5f, v, precision: 5));
    }

    [Fact]
    public void StereoWidthZero_CollapsesPannedOutputToEqualChannels()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var profile = new RadioProfile { WetDryMix = 0f, StereoWidth = 0f };
        var fakeInput = new SpotifyGameRadio.Core.Tests.Tracking.FakeMouseInputSource();
        var tracker = new FreelookTracker(fakeInput, profile);
        var effectChain = new RadioEffectChain(48000f);
        effectChain.ApplyProfile(profile);
        var spat = new PanningFakeSpatializer();
        var pipeline = new RadioPipeline(capture, output, spat, spat, effectChain, tracker, frameSize: 2);
        pipeline.ApplyProfile(profile);
        pipeline.Start();

        capture.PushSamples(new[] { 1.0f, 1.0f });

        var outBuf = output.WrittenBuffers[0];
        Assert.Equal(outBuf[0], outBuf[1], precision: 5); // L == R after width 0
        Assert.Equal(0.5f, outBuf[0], precision: 5);      // mid of (1, 0)
    }

    [Fact]
    public void SetVehicleMuted_True_SilencesOutputRegardlessOfVolume()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);
        pipeline.Start();

        pipeline.SetVehicleMuted(true);
        capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });

        Assert.All(output.WrittenBuffers[0], v => Assert.Equal(0f, v, precision: 5));
    }

    [Fact]
    public void SetVehicleMuted_ComposesWithVolume_NotReplacesIt()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);
        pipeline.Start();
        var profile = new RadioProfile { WetDryMix = 0f, Volume = 0.5f };
        pipeline.ApplyProfile(profile);

        pipeline.SetVehicleMuted(true);
        pipeline.SetVehicleMuted(false); // back "in vehicle"
        capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });

        // Volume (0.5) must still apply after a mute/unmute cycle — vehicle
        // gain must multiply with _volume, not overwrite it.
        Assert.All(output.WrittenBuffers[0], v => Assert.Equal(0.5f, v, precision: 5));
    }

    [Fact]
    public void ListenerYawAndPitch_ReflectTheTrackerAfterFreelookInput()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var profile = new RadioProfile { MouseSensitivity = 0.1f, MaxYawDegrees = 90f, MaxPitchDegrees = 60f };
        var input = new SpotifyGameRadio.Core.Tests.Tracking.FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, profile);
        var effectChain = new RadioEffectChain(48000f);
        var spatializer = new FakeSpatializer();
        var pipeline = new RadioPipeline(capture, output, spatializer, spatializer, effectChain, tracker, frameSize: 3);
        pipeline.ApplyProfile(profile);
        pipeline.Start();

        input.RaiseMove(dx: 300, dy: 100);

        Assert.Equal(30f, pipeline.ListenerYawDegrees, precision: 3);   // 300 * 0.1
        Assert.Equal(10f, pipeline.ListenerPitchDegrees, precision: 3); // 100 * 0.1
    }

    [Fact]
    public void RecenterListener_ReturnsOrientationToZero()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var profile = new RadioProfile { MouseSensitivity = 0.1f, MaxYawDegrees = 90f, MaxPitchDegrees = 60f };
        var input = new SpotifyGameRadio.Core.Tests.Tracking.FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, profile);
        var effectChain = new RadioEffectChain(48000f);
        var spat = new FakeSpatializer();
        var pipeline = new RadioPipeline(capture, output, spat, spat, effectChain, tracker, frameSize: 3);
        pipeline.ApplyProfile(profile);
        pipeline.Start();
        input.RaiseMove(dx: 300, dy: 100);

        pipeline.RecenterListener();

        Assert.Equal(0f, pipeline.ListenerYawDegrees);
        Assert.Equal(0f, pipeline.ListenerPitchDegrees);
    }

    [Fact]
    public void ApplyProfile_AfterStart_PushesUpdatedSourcePositionToSpatializer()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var profile = new RadioProfile { WetDryMix = 0f };
        var fakeInput = new SpotifyGameRadio.Core.Tests.Tracking.FakeMouseInputSource();
        var tracker = new FreelookTracker(fakeInput, profile);
        var effectChain = new RadioEffectChain(48000f);
        var spatializer = new RecordingSpatializer();
        var pipeline = new RadioPipeline(capture, output, spatializer, spatializer, effectChain, tracker, frameSize: 3);
        pipeline.ApplyProfile(profile);

        pipeline.Start();

        profile.SourceX = 0.9f;
        profile.SourceZ = -0.4f;
        pipeline.ApplyProfile(profile);

        Assert.Equal((0.9f, -0.1f, -0.4f), spatializer.LastPosition);
    }
}
