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
        MouseSensitivity = 0.1f, // degrees per mouse count, yaw
        PitchSensitivity = 0.1f, // degrees per mouse count, pitch
        MaxYawDegrees = 90f,
        MaxPitchDegrees = 60f,
        SpringBackRatePerSecond = 100f
    };

    [Fact]
    public void MouseMove_WhileHotkeyHeld_AccumulatesYaw_IgnoresPitch()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());

        input.RaiseMove(dx: 50, dy: 20);

        Assert.Equal(5f, tracker.YawDegrees, precision: 3);   // 50 * 0.1
        Assert.Equal(0f, tracker.PitchDegrees);               // vertical panning disabled
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
    public void Update_OnHotkeyRelease_SnapsToForwardImmediately()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        input.RaiseMove(dx: 100, dy: 40); // yaw 10; dy ignored (pitch disabled)
        tracker.Update(0.05f);            // still held
        Assert.Equal(10f, tracker.YawDegrees, precision: 3);

        input.IsHotkeyHeld = false;
        tracker.Update(0.001f);

        Assert.Equal(0f, tracker.YawDegrees, precision: 3);
        Assert.Equal(0f, tracker.PitchDegrees, precision: 3);
    }

    [Fact]
    public void OffAxisClamp_LimitsYaw_WithPitchAlwaysZero()
    {
        // Pitch is disabled (always 0), so the combined-angle formula
        // degenerates to a plain yaw clamp here — still exercises the
        // (currently unreachable from the UI) off-axis clamp method.
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var profile = MakeProfile();
        profile.MaxYawDegrees = 90f;
        profile.MeasuredMaxOffAxisDegrees = 50f;
        var tracker = new FreelookTracker(input, profile);

        input.RaiseMove(dx: 600, dy: 400); // raw yaw 60; dy ignored

        Assert.Equal(50f, tracker.YawDegrees, precision: 1);
        Assert.Equal(0f, tracker.PitchDegrees);
    }

    [Fact]
    public void OffAxisClamp_Disabled_WhenMeasuredMaxIsZero()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var profile = MakeProfile();
        profile.MaxYawDegrees = 90f;
        profile.MeasuredMaxOffAxisDegrees = 0f;
        var tracker = new FreelookTracker(input, profile);

        input.RaiseMove(dx: 400, dy: 400); // yaw 40; dy ignored

        Assert.Equal(40f, tracker.YawDegrees, precision: 3);
        Assert.Equal(0f, tracker.PitchDegrees);
    }

    [Fact]
    public void AlwaysOn_TracksMovementWithHotkeyNotHeld()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var profile = MakeProfile();
        profile.FreelookAlwaysOn = true;
        var tracker = new FreelookTracker(input, profile);

        input.RaiseMove(dx: 50, dy: 20);

        Assert.Equal(5f, tracker.YawDegrees, precision: 3);
        Assert.Equal(0f, tracker.PitchDegrees);
    }

    [Fact]
    public void AlwaysOn_Update_DoesNotEaseUntilMouseIdleOneSecond()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var profile = MakeProfile();
        profile.FreelookAlwaysOn = true;
        var tracker = new FreelookTracker(input, profile);
        input.RaiseMove(dx: 100, dy: 0); // yaw 10

        for (int i = 0; i < 9; i++) tracker.Update(0.1f); // 0.9s idle

        Assert.Equal(10f, tracker.YawDegrees, precision: 3);
    }

    [Fact]
    public void AlwaysOn_Update_EasesTowardZeroAfterOneSecondIdle()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var profile = MakeProfile();       // SpringBackRatePerSecond = 100
        profile.FreelookAlwaysOn = true;
        var tracker = new FreelookTracker(input, profile);
        input.RaiseMove(dx: 100, dy: 0);   // yaw 10

        for (int i = 0; i < 30; i++) tracker.Update(0.1f); // 3s: 1s wait + ample ease

        Assert.Equal(0f, tracker.YawDegrees, precision: 3);
    }

    [Fact]
    public void AlwaysOn_MouseMovement_ResetsTheIdleTimer()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var profile = MakeProfile();
        profile.FreelookAlwaysOn = true;
        var tracker = new FreelookTracker(input, profile);
        input.RaiseMove(dx: 100, dy: 0); // yaw 10

        for (int i = 0; i < 9; i++) { tracker.Update(0.1f); input.RaiseMove(0, 0); }

        Assert.Equal(10f, tracker.YawDegrees, precision: 3); // idle never reached 1s
    }

    [Fact]
    public void Recenter_ZeroesYaw()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        input.RaiseMove(dx: 200, dy: 100);

        tracker.Recenter();

        Assert.Equal(0f, tracker.YawDegrees);
        Assert.Equal(0f, tracker.PitchDegrees);
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
