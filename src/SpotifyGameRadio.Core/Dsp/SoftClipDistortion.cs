namespace SpotifyGameRadio.Core.Dsp;

/// Drive in [0,1]: 0 = bypass, higher = more aggressive tanh-style soft clipping.
public class SoftClipDistortion
{
    public float Drive { get; set; }

    public SoftClipDistortion(float drive)
    {
        Drive = drive;
    }

    public float Process(float input)
    {
        if (Drive <= 0f) return input;
        float k = 1f + Drive * 19f; // maps [0,1] -> gentle..aggressive tanh slope
        float result = MathF.Tanh(k * input) / MathF.Tanh(k);
        // Clamp to ensure output stays strictly below ±1.0 for soft-clipping behavior
        return MathF.CopySign(Math.Min(MathF.Abs(result), 1.0f - 1e-6f), result);
    }
}
