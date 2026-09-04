using SpotifyGameRadio.Core.Dsp;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Dsp;

public class SoftClipDistortionTests
{
    [Fact]
    public void Process_WithZeroDrive_PassesSignalThroughUnchanged()
    {
        var distortion = new SoftClipDistortion(drive: 0f);

        float output = distortion.Process(0.5f);

        Assert.Equal(0.5f, output, precision: 3);
    }

    [Fact]
    public void Process_WithHighDrive_CompressesLoudSignalTowardUnity()
    {
        var distortion = new SoftClipDistortion(drive: 0.9f);

        float output = distortion.Process(0.7f);

        Assert.True(output < 1.0f && output > 0f,
            $"Expected soft-clipped output to stay bounded below 1.0, got {output}.");
    }

    [Fact]
    public void Process_IsOddSymmetric()
    {
        var distortion = new SoftClipDistortion(drive: 0.6f);

        float positive = distortion.Process(0.7f);
        float negative = distortion.Process(-0.7f);

        Assert.Equal(-positive, negative, precision: 4);
    }
}
