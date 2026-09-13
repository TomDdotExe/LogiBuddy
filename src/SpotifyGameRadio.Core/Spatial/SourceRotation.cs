namespace SpotifyGameRadio.Core.Spatial;

/// Rotates a source position fixed in vehicle/world space into listener
/// space, given the listener's yaw and pitch. Shared by every spatializer so
/// the rotation convention only has one implementation to get right — see
/// git history for a yaw sign bug found the hard way in an earlier version
/// of this code.
///
/// Convention (matches RadioProfile.SourceX/Y/Z): +X right, +Y up, +Z
/// forward. Yaw rotates X/Z (turning toward something makes a world-fixed
/// source swing away, e.g. turning right swings a forward source left).
/// Pitch is applied after yaw and rotates Y/Z the same way (looking up
/// makes a level, forward source swing down/away, mirroring the yaw case).
public static class SourceRotation
{
    public static (float X, float Y, float Z) Rotate(float sourceX, float sourceY, float sourceZ, float yawRadians, float pitchRadians)
    {
        float cosYaw = MathF.Cos(yawRadians);
        float sinYaw = MathF.Sin(yawRadians);
        float x1 = sourceX * cosYaw - sourceZ * sinYaw;
        float z1 = sourceX * sinYaw + sourceZ * cosYaw;
        float y1 = sourceY;

        float cosPitch = MathF.Cos(pitchRadians);
        float sinPitch = MathF.Sin(pitchRadians);
        float y2 = y1 * cosPitch - z1 * sinPitch;
        float z2 = y1 * sinPitch + z1 * cosPitch;

        return (x1, y2, z2);
    }
}
