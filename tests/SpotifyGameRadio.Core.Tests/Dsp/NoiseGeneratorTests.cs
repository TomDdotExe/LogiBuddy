using SpotifyGameRadio.Core.Dsp;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Dsp;

public class NoiseGeneratorTests
{
    [Fact]
    public void NextSample_StaysWithinLevelBounds()
    {
        var noise = new NoiseGenerator(seed: 42) { Level = 0.1f };

        for (int i = 0; i < 1000; i++)
        {
            float sample = noise.NextSample();
            Assert.InRange(sample, -0.1f, 0.1f);
        }
    }

    [Fact]
    public void NextSample_WithZeroLevel_IsAlwaysZero()
    {
        var noise = new NoiseGenerator(seed: 1) { Level = 0f };

        for (int i = 0; i < 100; i++)
            Assert.Equal(0f, noise.NextSample());
    }

    [Fact]
    public void SameSeed_ProducesDeterministicSequence()
    {
        var a = new NoiseGenerator(seed: 7) { Level = 1f };
        var b = new NoiseGenerator(seed: 7) { Level = 1f };

        for (int i = 0; i < 20; i++)
            Assert.Equal(a.NextSample(), b.NextSample());
    }
}
