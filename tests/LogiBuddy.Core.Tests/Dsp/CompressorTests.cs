using LogiBuddy.Core.Dsp;
using Xunit;

namespace LogiBuddy.Core.Tests.Dsp;

public class CompressorTests
{
    [Fact]
    public void Process_SignalBelowThreshold_IsUnaffectedAtSteadyState()
    {
        var compressor = new Compressor(sampleRate: 48000f, thresholdDb: -18f, ratio: 4f);

        float output = 0f;
        // Feed a small constant signal long enough for the envelope to settle.
        for (int i = 0; i < 10000; i++)
            output = compressor.Process(0.05f); // well below -18dBFS (~0.126)

        Assert.InRange(output, 0.045f, 0.055f);
    }

    [Fact]
    public void Process_SignalAboveThreshold_IsGainReducedAtSteadyState()
    {
        var compressor = new Compressor(sampleRate: 48000f, thresholdDb: -18f, ratio: 4f);

        float output = 0f;
        for (int i = 0; i < 10000; i++)
            output = compressor.Process(0.9f); // well above threshold

        Assert.True(output < 0.9f, $"Expected gain reduction above threshold, got {output}.");
        Assert.True(output > 0.05f, $"Expected compressor not to silence the signal, got {output}.");
    }
}
