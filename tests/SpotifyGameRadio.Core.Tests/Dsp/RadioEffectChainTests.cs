using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Dsp;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Dsp;

public class RadioEffectChainTests
{
    [Fact]
    public void Process_WithFullWetMix_ProducesBoundedNonZeroOutput()
    {
        var chain = new RadioEffectChain(48000f)
        {
            HighPassHz = 400f,
            LowPassHz = 3400f,
            DistortionDrive = 0.2f,
            CompressorThresholdDb = -18f,
            CompressorRatio = 4f,
            NoiseLevel = 0f, // deterministic for this test
            WetDryMix = 1f
        };

        var buffer = new float[512];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = MathF.Sin(2f * MathF.PI * 1000f * i / 48000f) * 0.5f;

        chain.Process(buffer, buffer.Length);

        Assert.Contains(buffer, s => MathF.Abs(s) > 0.001f);
        Assert.All(buffer, s => Assert.InRange(s, -1.5f, 1.5f));
    }

    [Fact]
    public void Process_WithZeroWetMix_LeavesSignalUnchanged()
    {
        var chain = new RadioEffectChain(48000f)
        {
            HighPassHz = 400f,
            LowPassHz = 3400f,
            DistortionDrive = 0.5f,
            NoiseLevel = 0.2f,
            WetDryMix = 0f
        };

        var buffer = new float[256];
        var original = new float[256];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = MathF.Sin(2f * MathF.PI * 1000f * i / 48000f) * 0.5f;
            original[i] = buffer[i];
        }

        chain.Process(buffer, buffer.Length);

        for (int i = 0; i < buffer.Length; i++)
            Assert.Equal(original[i], buffer[i], precision: 5);
    }

    [Fact]
    public void ApplyProfile_UpdatesAllChainParameters()
    {
        var chain = new RadioEffectChain(48000f);
        var profile = new RadioProfile
        {
            HighPassHz = 600f,
            LowPassHz = 2800f,
            DistortionDrive = 0.4f,
            CompressorThresholdDb = -12f,
            CompressorRatio = 8f,
            NoiseLevel = 0.15f,
            WetDryMix = 0.7f
        };

        chain.ApplyProfile(profile);

        Assert.Equal(600f, chain.HighPassHz);
        Assert.Equal(2800f, chain.LowPassHz);
        Assert.Equal(0.4f, chain.DistortionDrive);
        Assert.Equal(-12f, chain.CompressorThresholdDb);
        Assert.Equal(8f, chain.CompressorRatio);
        Assert.Equal(0.15f, chain.NoiseLevel);
        Assert.Equal(0.7f, chain.WetDryMix);
    }
}
