namespace SpotifyGameRadio.Core.Dsp;

/// Uniform white noise generator scaled by Level, seeded for deterministic tests.
public class NoiseGenerator
{
    private readonly Random _random;

    public float Level { get; set; }

    public NoiseGenerator(int seed = 0)
    {
        _random = new Random(seed);
    }

    public float NextSample()
    {
        if (Level <= 0f) return 0f;
        float uniform = (float)(_random.NextDouble() * 2.0 - 1.0); // [-1, 1]
        return uniform * Level;
    }
}
