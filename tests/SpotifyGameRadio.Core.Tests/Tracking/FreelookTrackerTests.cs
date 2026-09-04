using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Tracking;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Tracking;

public class FakeMouseInputSource : IMouseInputSource
{
    public bool IsHotkeyHeld { get; set; }
    public event Action<int, int>? MouseMoved;

    public void RaiseMove(int dx, int dy) => MouseMoved?.Invoke(dx, dy);
}

public class FreelookTrackerTests
{
    private static RadioProfile MakeProfile() => new()
    {
        MouseSensitivity = 0.1f, // degrees per mouse count
        MaxYawDegrees = 90f,
        MaxPitchDegrees = 60f,
        SpringBackRatePerSecond = 100f
    };

    [Fact]
    public void MouseMove_WhileHotkeyHeld_AccumulatesYawAndPitch()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());

        input.RaiseMove(dx: 50, dy: 20);

        Assert.Equal(5f, tracker.YawDegrees, precision: 3);   // 50 * 0.1
        Assert.Equal(2f, tracker.PitchDegrees, precision: 3); // 20 * 0.1
    }

    [Fact]
    public void MouseMove_WhileHotkeyNotHeld_IsIgnored()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var tracker = new FreelookTracker(input, MakeProfile());

        input.RaiseMove(dx: 50, dy: 20);

        Assert.Equal(0f, tracker.YawDegrees);
        Assert.Equal(0f, tracker.PitchDegrees);
    }

    [Fact]
    public void Yaw_IsClampedToMaxYawDegrees()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());

        input.RaiseMove(dx: 5000, dy: 0);

        Assert.Equal(90f, tracker.YawDegrees, precision: 3);
    }

    [Fact]
    public void Update_WhileHotkeyReleased_SpringsBackTowardZero()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        input.RaiseMove(dx: 100, dy: 0); // yaw = 10 degrees
        input.IsHotkeyHeld = false;

        tracker.Update(deltaSeconds: 0.05f); // springs back 100deg/s * 0.05s = 5 degrees

        Assert.Equal(5f, tracker.YawDegrees, precision: 2);
    }

    [Fact]
    public void Update_SpringBack_NeverOvershootsZero()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        input.RaiseMove(dx: 20, dy: 0); // yaw = 2 degrees
        input.IsHotkeyHeld = false;

        tracker.Update(deltaSeconds: 1f); // spring rate would overshoot to -98 degrees

        Assert.Equal(0f, tracker.YawDegrees, precision: 3);
    }
}
