namespace LogiBuddy.Core.Dsp;

/// Mid/side stereo-width adjustment. Real-time safe: no allocation, in place.
public static class StereoWidth
{
    /// width 0 => both channels become the mid (mono point source),
    /// 1 => unchanged, >1 => exaggerated ear-to-ear spread.
    /// frameCount is the number of interleaved L/R pairs to process.
    public static void Apply(float[] interleaved, int frameCount, float width)
    {
        if (width == 1f) return;
        for (int i = 0; i < frameCount; i++)
        {
            float l = interleaved[i * 2];
            float r = interleaved[i * 2 + 1];
            float mid = (l + r) * 0.5f;
            float side = (l - r) * 0.5f * width;
            interleaved[i * 2] = mid + side;
            interleaved[i * 2 + 1] = mid - side;
        }
    }
}
