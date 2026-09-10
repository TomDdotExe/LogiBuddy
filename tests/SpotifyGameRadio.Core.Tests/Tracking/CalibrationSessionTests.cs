using System;
using SpotifyGameRadio.Core.Tracking;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Tracking;

public class CalibrationSessionTests
{
    // Effectively no idle timeout for the deterministic tests.
    private static CalibrationSession NewSession(FakeMouseInputSource input) =>
        new(input, TimeSpan.FromMinutes(5));

    [Fact]
    public void Start_MovesToAwaitLeftLimit_AndAnnouncesStep()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationStep? announced = null;
        session.StepChanged += (s, _) => announced = s;

        session.Start();

        Assert.Equal(CalibrationStep.AwaitLeftLimit, session.Step);
        Assert.Equal(CalibrationStep.AwaitLeftLimit, announced);
    }

    [Fact]
    public void Movement_WhileHotkeyNotHeld_IsIgnored()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(-2000, 0);
        input.RaiseMove(-2000, 0);
        session.Mark(); // left limit captured as 0

        input.IsHotkeyHeld = true;
        input.RaiseMove(9000, 0);
        session.Mark();

        Assert.Equal(CalibrationStep.Completed, session.Step);
        Assert.NotNull(result);
        Assert.Equal(4500f, result!.MeasuredHalfSweepCounts, precision: 3);
    }

    [Fact]
    public void HappyPath_TwoMarks_CompletesWithHalfSweep()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(-1500, 0);
        input.RaiseMove(-2500, 0);   // accumulator now -4000
        session.Mark();              // -> AwaitRightLimit, accumulator re-zeroed
        Assert.Equal(CalibrationStep.AwaitRightLimit, session.Step);

        input.RaiseMove(3000, 0);
        input.RaiseMove(5000, 0);    // accumulator now +8000
        session.Mark();              // -> Completed

        Assert.Equal(CalibrationStep.Completed, session.Step);
        Assert.NotNull(result);
        Assert.Equal(4000f, result!.MeasuredHalfSweepCounts, precision: 3);
    }

    [Fact]
    public void SweepTooSmall_Fails_WithReason_AndNoResult()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        var completed = false;
        string? endedReason = null;
        session.Completed += _ => completed = true;
        session.Ended += r => endedReason = r;

        session.Start();
        input.RaiseMove(10, 0);
        session.Mark();
        input.RaiseMove(20, 0);      // full sweep only 20 counts
        session.Mark();

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
        Assert.False(string.IsNullOrWhiteSpace(endedReason));
    }

    [Fact]
    public void Abort_FromAwaitRightLimit_EndsAndIgnoresLaterMarks()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        string? endedReason = null;
        session.Ended += r => endedReason = r;

        session.Start();
        input.RaiseMove(-5000, 0);
        session.Mark();              // AwaitRightLimit
        session.Abort();

        Assert.Equal(CalibrationStep.Aborted, session.Step);
        Assert.False(string.IsNullOrWhiteSpace(endedReason));

        input.RaiseMove(9999, 0);
        session.Mark();              // no-op
        Assert.Equal(CalibrationStep.Aborted, session.Step);
    }

    [Fact]
    public void Mark_AfterCompleted_IsNoOp()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        session.Start();
        input.RaiseMove(-4000, 0); session.Mark();
        input.RaiseMove(8000, 0); session.Mark();

        var stepAfter = session.Step;
        session.Mark();
        Assert.Equal(stepAfter, session.Step);
    }

    [Fact]
    public void Dispose_Unsubscribes_FurtherMovementIsInert()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var session = NewSession(input);
        session.Start();
        session.Dispose();

        var ex = Record.Exception(() => input.RaiseMove(5000, 0));
        Assert.Null(ex);
        Assert.Equal(CalibrationStep.AwaitLeftLimit, session.Step); // unchanged by post-dispose movement
    }

    [Fact]
    public void IdleTimeout_Fails()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = new CalibrationSession(input, TimeSpan.FromMilliseconds(80));
        string? endedReason = null;
        session.Ended += r => endedReason = r;

        session.Start();
        System.Threading.Thread.Sleep(250); // the one place a sleep is unavoidable

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.Contains("timed out", endedReason, StringComparison.OrdinalIgnoreCase);
    }
}
