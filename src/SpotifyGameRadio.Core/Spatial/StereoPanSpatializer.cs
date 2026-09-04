namespace SpotifyGameRadio.Core.Spatial;

/// Simple equal-power pan law based on the source's azimuth relative to the
/// listener, used as a guaranteed-available fallback when true HRTF
/// rendering (Steam Audio) is unavailable. Not a substitute for HRTF —
/// no elevation or distance cues, just left/right balance.
public class StereoPanSpatializer : ISpatializer
{
    private float _sourceX, _sourceY, _sourceZ;
    private float _yawRadians, _pitchRadians;

    public void SetSourcePosition(float x, float y, float z)
    {
        _sourceX = x;
        _sourceY = y;
        _sourceZ = z;
    }

    public void SetListenerOrientation(float yawDegrees, float pitchDegrees)
    {
        _yawRadians = yawDegrees * MathF.PI / 180f;
        _pitchRadians = pitchDegrees * MathF.PI / 180f;
    }

    public void Process(float[] monoInput, int count, float[] stereoOutputInterleaved)
    {
        // Rotate the fixed source position into listener space by applying
        // the listener's yaw (listener turning right makes a world-fixed
        // source appear to swing left in listener space).
        float cosYaw = MathF.Cos(_yawRadians);
        float sinYaw = MathF.Sin(_yawRadians);
        float relativeX = _sourceX * cosYaw - _sourceZ * sinYaw;
        float relativeZ = _sourceX * sinYaw + _sourceZ * cosYaw;

        float azimuth = MathF.Atan2(relativeX, MathF.Max(relativeZ, 0.0001f)); // 0 = ahead, +pi/2 = right
        float panRaw = azimuth / (MathF.PI / 2f);
        float pan = panRaw < -1f ? -1f : panRaw > 1f ? 1f : panRaw; // -1 = full left, +1 = full right

        // Equal-power pan law.
        float angle = (pan + 1f) * MathF.PI / 4f; // maps [-1,1] -> [0, pi/2]
        float leftGain = MathF.Cos(angle);
        float rightGain = MathF.Sin(angle);

        for (int i = 0; i < count; i++)
        {
            float sample = monoInput[i];
            stereoOutputInterleaved[i * 2] = sample * leftGain;
            stereoOutputInterleaved[i * 2 + 1] = sample * rightGain;
        }
    }
}
