using SpotifyGameRadio.Core.Dsp;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Dsp;

public class BiquadFilterTests
{
    private const float SampleRate = 48000f;

    private static float RmsAtFrequency(BiquadFilter filter, float toneHz, int sampleCount)
    {
        filter.Reset();
        double sum = 0;
        // Discard the first 500 samples so filter transients settle before measuring.
        int warmup = 500;
        for (int i = 0; i < warmup + sampleCount; i++)
        {
            float sample = MathF.Sin(2f * MathF.PI * toneHz * i / SampleRate);
            float output = filter.Process(sample);
            if (i >= warmup) sum += output * output;
        }
        return (float)Math.Sqrt(sum / sampleCount);
    }

    [Fact]
    public void HighPass_AttenuatesLowFrequencyMoreThanHighFrequency()
    {
        var filter = new BiquadFilter(BiquadFilterType.HighPass, cutoffHz: 400f, sampleRate: SampleRate);

        float lowRms = RmsAtFrequency(filter, toneHz: 80f, sampleCount: 4000);
        float highRms = RmsAtFrequency(filter, toneHz: 4000f, sampleCount: 4000);

        Assert.True(lowRms < highRms * 0.3f,
            $"Expected 80Hz ({lowRms}) to be attenuated well below 4000Hz ({highRms}) through a 400Hz high-pass.");
    }

    [Fact]
    public void LowPass_AttenuatesHighFrequencyMoreThanLowFrequency()
    {
        var filter = new BiquadFilter(BiquadFilterType.LowPass, cutoffHz: 3400f, sampleRate: SampleRate);

        float lowRms = RmsAtFrequency(filter, toneHz: 200f, sampleCount: 4000);
        float highRms = RmsAtFrequency(filter, toneHz: 12000f, sampleCount: 4000);

        Assert.True(highRms < lowRms * 0.3f,
            $"Expected 12000Hz ({highRms}) to be attenuated well below 200Hz ({lowRms}) through a 3400Hz low-pass.");
    }

    [Fact]
    public void PassbandFrequency_IsNotStronglyAttenuated()
    {
        var filter = new BiquadFilter(BiquadFilterType.HighPass, cutoffHz: 400f, sampleRate: SampleRate);

        float passbandRms = RmsAtFrequency(filter, toneHz: 4000f, sampleCount: 4000);
        float inputRms = 1f / MathF.Sqrt(2f); // RMS of a unit sine wave

        Assert.True(passbandRms > inputRms * 0.8f,
            $"Expected passband frequency to pass through mostly unattenuated, got {passbandRms}.");
    }
}
