using SpotifyGameRadio.Core.Spatial;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Spatial;

public class SourceRotationTests
{
    private const float Deg2Rad = MathF.PI / 180f;

    [Fact]
    public void NoRotation_ReturnsSourceUnchanged()
    {
        var (x, y, z) = SourceRotation.Rotate(0.3f, -0.1f, 0.2f, yawRadians: 0f, pitchRadians: 0f);

        Assert.Equal(0.3f, x, precision: 5);
        Assert.Equal(-0.1f, y, precision: 5);
        Assert.Equal(0.2f, z, precision: 5);
    }

    [Fact]
    public void YawOnly_TurningRight_SwingsAForwardSourceLeft()
    {
        var (x, y, z) = SourceRotation.Rotate(0f, 0f, 1f, yawRadians: 90f * Deg2Rad, pitchRadians: 0f);

        Assert.Equal(-1f, x, precision: 3); // left, per the +X=right convention
        Assert.Equal(0f, y, precision: 3);
        Assert.Equal(0f, z, precision: 3);
    }

    [Fact]
    public void PitchOnly_LookingUp_SwingsALevelForwardSourceDown()
    {
        var (x, y, z) = SourceRotation.Rotate(0f, 0f, 1f, yawRadians: 0f, pitchRadians: 90f * Deg2Rad);

        Assert.Equal(0f, x, precision: 3);
        Assert.Equal(-1f, y, precision: 3); // below, per the +Y=up convention
        Assert.Equal(0f, z, precision: 3);
    }

    [Fact]
    public void PitchOnly_LookingDown_SwingsALevelForwardSourceUp()
    {
        var (x, y, z) = SourceRotation.Rotate(0f, 0f, 1f, yawRadians: 0f, pitchRadians: -90f * Deg2Rad);

        Assert.Equal(0f, x, precision: 3);
        Assert.Equal(1f, y, precision: 3); // above
        Assert.Equal(0f, z, precision: 3);
    }

    [Fact]
    public void ZeroPitch_IsIdenticalToTheOriginalYawOnlyFormula()
    {
        // Any source, any yaw, pitch = 0 must reproduce the pre-pitch
        // behavior exactly — inside/cockpit view depends on this, since it
        // always passes pitch = 0.
        var (x, y, z) = SourceRotation.Rotate(0.9f, -0.4f, 0.2f, yawRadians: 37f * Deg2Rad, pitchRadians: 0f);

        float cosYaw = MathF.Cos(37f * Deg2Rad);
        float sinYaw = MathF.Sin(37f * Deg2Rad);
        float expectedX = 0.9f * cosYaw - 0.2f * sinYaw;
        float expectedZ = 0.9f * sinYaw + 0.2f * cosYaw;

        Assert.Equal(expectedX, x, precision: 5);
        Assert.Equal(-0.4f, y, precision: 5);
        Assert.Equal(expectedZ, z, precision: 5);
    }

    [Fact]
    public void ElevatedSource_NoYawOrPitch_StaysElevated()
    {
        var (x, y, z) = SourceRotation.Rotate(0f, 1f, 0f, yawRadians: 0f, pitchRadians: 0f);

        Assert.Equal(0f, x, precision: 5);
        Assert.Equal(1f, y, precision: 5);
        Assert.Equal(0f, z, precision: 5);
    }
}
