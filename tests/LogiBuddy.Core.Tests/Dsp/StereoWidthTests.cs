using LogiBuddy.Core.Dsp;
using Xunit;

namespace LogiBuddy.Core.Tests.Dsp;

public class StereoWidthTests
{
    [Fact]
    public void WidthOne_LeavesBufferUntouched()
    {
        var buf = new[] { 0.4f, -0.2f, 0.9f, 0.1f };
        var copy = (float[])buf.Clone();
        StereoWidth.Apply(buf, 2, 1f);
        Assert.Equal(copy, buf);
    }

    [Fact]
    public void WidthZero_CollapsesEachPairToMid()
    {
        var buf = new[] { 1.0f, 0.0f, 0.4f, 0.2f };
        StereoWidth.Apply(buf, 2, 0f);
        Assert.Equal(0.5f, buf[0], precision: 5);
        Assert.Equal(0.5f, buf[1], precision: 5);
        Assert.Equal(0.3f, buf[2], precision: 5);
        Assert.Equal(0.3f, buf[3], precision: 5);
    }

    [Fact]
    public void WidthTwo_DoublesTheSideComponent()
    {
        var buf = new[] { 0.8f, 0.2f }; // mid 0.5, side 0.3
        StereoWidth.Apply(buf, 1, 2f);  // side -> 0.6
        Assert.Equal(1.1f, buf[0], precision: 5);   // mid + 2*side
        Assert.Equal(-0.1f, buf[1], precision: 5);  // mid - 2*side
    }

    [Fact]
    public void FrameCountShorterThanBuffer_LeavesTailUntouched()
    {
        var buf = new[] { 1.0f, 0.0f, 7.0f, 7.0f };
        StereoWidth.Apply(buf, 1, 0f);
        Assert.Equal(7.0f, buf[2]);
        Assert.Equal(7.0f, buf[3]);
    }
}
