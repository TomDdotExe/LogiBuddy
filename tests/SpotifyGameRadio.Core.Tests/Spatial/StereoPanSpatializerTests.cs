using SpotifyGameRadio.Core.Spatial;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Spatial;

public class StereoPanSpatializerTests
{
    [Fact]
    public void SourceDirectlyAhead_ListenerFacingForward_PansEqualLeftRight()
    {
        var spatializer = new StereoPanSpatializer();
        spatializer.SetSourcePosition(x: 0f, y: 0f, z: 1f); // straight ahead
        spatializer.SetListenerOrientation(yawDegrees: 0f, pitchDegrees: 0f);

        var mono = new float[] { 1f };
        var stereo = new float[2];
        spatializer.Process(mono, 1, stereo);

        Assert.Equal(stereo[0], stereo[1], precision: 3); // L == R
    }

    [Fact]
    public void ListenerTurnedRight_SourceAppearsOnLeft()
    {
        var spatializer = new StereoPanSpatializer();
        spatializer.SetSourcePosition(x: 0f, y: 0f, z: 1f); // fixed straight ahead of vehicle
        spatializer.SetListenerOrientation(yawDegrees: 90f, pitchDegrees: 0f); // player looks right

        var mono = new float[] { 1f };
        var stereo = new float[2];
        spatializer.Process(mono, 1, stereo);

        // Source that was ahead is now off the listener's left ear.
        Assert.True(stereo[0] > stereo[1], $"Expected left ({stereo[0]}) > right ({stereo[1]}) after turning right.");
    }

    [Fact]
    public void ListenerTurnedLeft_SourceAppearsOnRight()
    {
        var spatializer = new StereoPanSpatializer();
        spatializer.SetSourcePosition(x: 0f, y: 0f, z: 1f);
        spatializer.SetListenerOrientation(yawDegrees: -90f, pitchDegrees: 0f); // player looks left

        var mono = new float[] { 1f };
        var stereo = new float[2];
        spatializer.Process(mono, 1, stereo);

        Assert.True(stereo[1] > stereo[0], $"Expected right ({stereo[1]}) > left ({stereo[0]}) after turning left.");
    }

    [Fact]
    public void Process_HandlesMultiSampleBuffers()
    {
        var spatializer = new StereoPanSpatializer();
        spatializer.SetSourcePosition(x: 0f, y: 0f, z: 1f);
        spatializer.SetListenerOrientation(yawDegrees: 0f, pitchDegrees: 0f);

        var mono = new float[] { 0.5f, -0.5f, 0.25f };
        var stereo = new float[6];
        spatializer.Process(mono, 3, stereo);

        Assert.Equal(stereo[0], stereo[1], precision: 3);
        Assert.Equal(stereo[2], stereo[3], precision: 3);
        Assert.Equal(stereo[4], stereo[5], precision: 3);
    }
}
