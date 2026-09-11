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
    public void Pitch_UsesPitchSensitivity_IndependentlyFromYaw()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var profile = MakeProfile();
        profile.MouseSensitivity = 0.1f;
        profile.PitchSensitivity = 0.4f;
        var tracker = new FreelookTracker(input, profile);

        input.RaiseMove(dx: 50, dy: 20);

        Assert.Equal(5f, tracker.YawDegrees, precision: 3);   // 50 * 0.1 (MouseSensitivity)
        Assert.Equal(8f, tracker.PitchDegrees, precision: 3); // 20 * 0.4 (PitchSensitivity)
    }

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
    public void Update_OnHotkeyRelease_SnapsToForwardImmediately()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        input.RaiseMove(dx: 100, dy: 40); // yaw 10, pitch 4
        tracker.Update(0.05f);            // still held
        Assert.Equal(10f, tracker.YawDegrees, precision: 3);

        input.IsHotkeyHeld = false;
        tracker.Update(0.001f);

        Assert.Equal(0f, tracker.YawDegrees, precision: 3);
        Assert.Equal(0f, tracker.PitchDegrees, precision: 3);
    }

    [Fact]
    public void OffAxisClamp_LimitsCombinedAngle_PreservingRatio()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var profile = MakeProfile();
        profile.MaxYawDegrees = 90f;
        profile.MaxPitchDegrees = 90f;
        profile.MeasuredMaxOffAxisDegrees = 50f;
        var tracker = new FreelookTracker(input, profile);

        input.RaiseMove(dx: 400, dy: 400); // raw yaw 40, pitch 40, combined ~56.57

        float combined = MathF.Sqrt(
            tracker.YawDegrees * tracker.YawDegrees + tracker.PitchDegrees * tracker.PitchDegrees);
        Assert.Equal(50f, combined, precision: 1);
        Assert.Equal(tracker.YawDegrees, tracker.PitchDegrees, precision: 2); // ratio preserved
    }

    [Fact]
    public void OffAxisClamp_Disabled_WhenMeasuredMaxIsZero()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var profile = MakeProfile();
        profile.MaxYawDegrees = 90f;
        profile.MaxPitchDegrees = 90f;
        profile.MeasuredMaxOffAxisDegrees = 0f;
        var tracker = new FreelookTracker(input, profile);

        input.RaiseMove(dx: 400, dy: 400); // yaw 40, pitch 40

        Assert.Equal(40f, tracker.YawDegrees, precision: 3);
        Assert.Equal(40f, tracker.PitchDegrees, precision: 3);
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
        Assert.Equal(2f, tracker.PitchDegrees, precision: 3);
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
    public void Recenter_ZeroesYawAndPitch()
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
