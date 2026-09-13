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
        MouseSensitivity = 0.1f, // degrees per mouse count, yaw (cockpit)
        PitchSensitivity = 0.1f, // degrees per mouse count, pitch (outside)
        MaxYawDegrees = 90f,
        OutsideYawSensitivity = 0.2f, // degrees per mouse count, yaw (outside) — deliberately distinct from MouseSensitivity
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

    [Fact]
    public void OutsideView_YawIsNotClampedToMaxYawDegrees()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile()); // MaxYawDegrees = 90
        tracker.SetOutsideView(true);

        input.RaiseMove(dx: 650, dy: 0); // raw magnitude 130 (650 * OutsideYawSensitivity 0.2) — well past the 90° cockpit limit

        Assert.Equal(-130f, tracker.YawDegrees, precision: 1); // sign flipped — see the orbit-convention tests below
    }

    [Fact]
    public void OutsideView_YawWrapsInsteadOfPinningAtTheBoundary()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        tracker.SetOutsideView(true);

        input.RaiseMove(dx: 1000, dy: 0); // raw magnitude 200 (1000 * OutsideYawSensitivity 0.2), sign-flipped to -200 -> wraps to -200+360=160

        Assert.Equal(160f, tracker.YawDegrees, precision: 1);
    }

    [Fact]
    public void OutsideView_IgnoresTheCalibratedOffAxisClamp()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var profile = MakeProfile();
        profile.MeasuredMaxOffAxisDegrees = 50f; // would clamp yaw to 50 inside the cockpit
        var tracker = new FreelookTracker(input, profile);
        tracker.SetOutsideView(true);

        input.RaiseMove(dx: 300, dy: 0); // raw magnitude 60 (300 * OutsideYawSensitivity 0.2) — past the cockpit's off-axis cone

        Assert.Equal(-60f, tracker.YawDegrees, precision: 1);
    }

    /// Outside view models an orbit camera — the listener's position moves
    /// around a fixed source rather than the listener rotating in place —
    /// which inverts the sign relative to the cockpit's head-turn model:
    /// panning right should sweep the sound to the right (as if walking
    /// around the source), not swing it left the way turning your own head
    /// right would. See SourceRotationTests for the underlying rotation
    /// math; these two confirm the tracker feeds it the inverted angle.
    [Fact]
    public void OutsideView_PanningRight_YawGoesNegative_SoTheSourceSweepsRightNotLeft()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        tracker.SetOutsideView(true);

        input.RaiseMove(dx: 100, dy: 0); // mouse right

        Assert.True(tracker.YawDegrees < 0f, $"Expected negative (orbit-inverted) yaw, got {tracker.YawDegrees}");
    }

    [Fact]
    public void OutsideView_PanningLeft_YawGoesPositive()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        tracker.SetOutsideView(true);

        input.RaiseMove(dx: -100, dy: 0); // mouse left

        Assert.True(tracker.YawDegrees > 0f, $"Expected positive (orbit-inverted) yaw, got {tracker.YawDegrees}");
    }

    [Fact]
    public void SetOutsideView_False_RestoresTheCockpitYawClamp()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile()); // MaxYawDegrees = 90
        tracker.SetOutsideView(true);
        tracker.SetOutsideView(false);

        input.RaiseMove(dx: 5000, dy: 0);

        Assert.Equal(90f, tracker.YawDegrees, precision: 3);
    }

    [Fact]
    public void InsideView_MouseMoveNeverProducesPitch()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());

        input.RaiseMove(dx: 0, dy: 500);

        Assert.Equal(0f, tracker.PitchDegrees);
    }

    /// Same orbit-inversion as yaw: the listener orbits vertically around
    /// the source rather than tilting their own head, so panning up should
    /// sweep the source up-and-over rather than down.
    [Fact]
    public void OutsideView_MouseUp_PitchGoesNegative()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile()); // PitchSensitivity = 0.1
        tracker.SetOutsideView(true);

        input.RaiseMove(dx: 0, dy: -100); // Raw Input Y grows downward, so "up" is negative

        Assert.Equal(-10f, tracker.PitchDegrees, precision: 3); // 100 * 0.1, sign-flipped
    }

    [Fact]
    public void OutsideView_MouseDown_PitchGoesPositive()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        tracker.SetOutsideView(true);

        input.RaiseMove(dx: 0, dy: 100);

        Assert.Equal(10f, tracker.PitchDegrees, precision: 3);
    }

    [Fact]
    public void OutsideView_PitchIsClampedToFixedNinetyDegrees()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        tracker.SetOutsideView(true);

        input.RaiseMove(dx: 0, dy: 5000);

        Assert.Equal(90f, tracker.PitchDegrees, precision: 3);
    }

    [Fact]
    public void SetOutsideView_False_SnapsPitchBackToLevel()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        tracker.SetOutsideView(true);
        input.RaiseMove(dx: 0, dy: 200); // pitch = 20

        tracker.SetOutsideView(false);

        Assert.Equal(0f, tracker.PitchDegrees);
    }

    [Fact]
    public void OutsideView_Update_OnHotkeyRelease_SnapsPitchToLevelImmediately()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        tracker.SetOutsideView(true);
        input.RaiseMove(dx: 0, dy: 200); // pitch = 20
        Assert.Equal(20f, tracker.PitchDegrees, precision: 3);

        input.IsHotkeyHeld = false;
        tracker.Update(0.001f);

        Assert.Equal(0f, tracker.PitchDegrees, precision: 3);
    }

    [Fact]
    public void OutsideView_AlwaysOn_Update_EasesPitchTowardLevelAfterOneSecondIdle()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var profile = MakeProfile(); // SpringBackRatePerSecond = 100
        profile.FreelookAlwaysOn = true;
        var tracker = new FreelookTracker(input, profile);
        tracker.SetOutsideView(true);
        input.RaiseMove(dx: 0, dy: 100); // pitch = 10

        for (int i = 0; i < 30; i++) tracker.Update(0.1f); // 3s: 1s wait + ample ease

        Assert.Equal(0f, tracker.PitchDegrees, precision: 3);
    }

    [Fact]
    public void Recenter_ZeroesPitchToo()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        tracker.SetOutsideView(true);
        input.RaiseMove(dx: 0, dy: 200);

        tracker.Recenter();

        Assert.Equal(0f, tracker.PitchDegrees);
    }
}
