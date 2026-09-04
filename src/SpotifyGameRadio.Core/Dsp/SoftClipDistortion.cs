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
        double k = 1.0 + Drive * 19.0; // maps [0,1] -> gentle..aggressive tanh slope
        double result = Math.Tanh(k * input) / Math.Tanh(k);
        // Clamp magnitude to just below 1.0 to ensure soft clipping never reaches hard limit
        double sign = input >= 0 ? 1.0 : -1.0;
        return (float)(sign * Math.Min(Math.Abs(result), 1.0 - 1e-6));
    }
}
