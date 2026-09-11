using System;
using SpotifyGameRadio.Core.Tracking;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Tracking;

public class CalibrationSessionTests
{
    private static CalibrationSession NewSession(FakeMouseInputSource input) =>
        new(input, TimeSpan.FromMinutes(5));

    [Fact]
    public void Start_MovesToAwaitRightMark_AndAnnounces()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationStep? announced = null;
        session.StepChanged += (s, _) => announced = s;

        session.Start();

        Assert.Equal(CalibrationStep.AwaitRightMark, session.Step);
        Assert.Equal(CalibrationStep.AwaitRightMark, announced);
    }

    private static CalibrationResult RunFullFlow(
        FakeMouseInputSource input, CalibrationSession session,
        int rightDx, int leftDx, int upDy, int downDy)
    {
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(rightDx, 0);
        session.Mark();               // right mark
        input.RaiseMove(leftDx, 0);
        session.Mark();                // left mark (yaw complete)
        input.RaiseMove(0, upDy);
        session.Mark();                // up mark
        input.RaiseMove(0, downDy);
        session.Mark();                // down mark (pitch complete)

        return result!;
    }

    [Fact]
    public void Start_ZeroesAccumulator_MovementBeforeStartIsIgnored()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);

        input.RaiseMove(-999, -999);   // before Start: Step is Idle, ignored
        var result = RunFullFlow(input, session, rightDx: 6000, leftDx: -7000, upDy: -5000, downDy: 6000);

        Assert.Equal(CalibrationStep.Completed, session.Step);
        Assert.NotNull(result);
    }

    [Fact]
    public void RightMark_RezeroesAccumulator_OnlyMovementBetweenMarksCounts()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);

        var result = RunFullFlow(input, session, rightDx: 6000, leftDx: -7000, upDy: -5000, downDy: 6000);

        Assert.Equal(7000f, result.YawSweepCounts, precision: 3);
    }

    [Fact]
    public void UpMark_RezeroesAccumulator_OnlyMovementBetweenMarksCounts()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);

        var result = RunFullFlow(input, session, rightDx: 6000, leftDx: -7000, upDy: -5000, downDy: 6000);

        Assert.Equal(6000f, result.PitchSweepCounts, precision: 3);
    }

    [Fact]
    public void Movement_IsCounted_RegardlessOfFreelookKey()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false }; // toggle-style: key not held
        using var session = NewSession(input);

        var result = RunFullFlow(input, session, rightDx: 6000, leftDx: -7000, upDy: -5000, downDy: 6000);

        Assert.Equal(7000f, result.YawSweepCounts, precision: 3);
        Assert.Equal(6000f, result.PitchSweepCounts, precision: 3);
    }

    [Fact]
    public void SweepCounts_AreAbsoluteValues_DirectionAgnostic()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);

        // Turned right using negative dx convention, and up using positive dy.
        var result = RunFullFlow(input, session, rightDx: -6000, leftDx: 7000, upDy: 5000, downDy: -6000);

        Assert.Equal(7000f, result.YawSweepCounts, precision: 3);
        Assert.Equal(6000f, result.PitchSweepCounts, precision: 3);
    }

    [Fact]
    public void LeftSweepTooSmall_Fails()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        var completed = false;
        string? ended = null;
        session.Completed += _ => completed = true;
        session.Ended += r => ended = r;

        session.Start();
        input.RaiseMove(6000, 0);
        session.Mark();               // right mark
        input.RaiseMove(-20, 0);
        session.Mark();               // left, only 20 counts

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
        Assert.False(string.IsNullOrWhiteSpace(ended));
    }

    [Fact]
    public void DownSweepTooSmall_Fails()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        var completed = false;
        string? ended = null;
        session.Completed += _ => completed = true;
        session.Ended += r => ended = r;

        session.Start();
        input.RaiseMove(6000, 0);
        session.Mark();               // right
        input.RaiseMove(-7000, 0);
        session.Mark();               // left (yaw complete)
        input.RaiseMove(0, -6000);
        session.Mark();               // up
        input.RaiseMove(0, 10);
        session.Mark();               // down, only 10 counts

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
        Assert.False(string.IsNullOrWhiteSpace(ended));
    }

    [Fact]
    public void Abort_FromEachAwaitStep_EndsAndIgnoresLaterMarks()
    {
        foreach (var marksBeforeAbort in new[] { 0, 1, 2, 3 })
        {
            var input = new FakeMouseInputSource();
            using var session = NewSession(input);
            string? ended = null;
            session.Ended += r => ended = r;

            session.Start();
            for (int i = 0; i < marksBeforeAbort; i++) { input.RaiseMove(6000, -6000); session.Mark(); }
            session.Abort();

            Assert.Equal(CalibrationStep.Aborted, session.Step);
            Assert.False(string.IsNullOrWhiteSpace(ended));

            input.RaiseMove(9999, 9999);
            session.Mark();
            Assert.Equal(CalibrationStep.Aborted, session.Step);
        }
    }

    [Fact]
    public void Mark_AfterCompleted_IsNoOp()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        RunFullFlow(input, session, rightDx: 6000, leftDx: -7000, upDy: -5000, downDy: 6000);

        var step = session.Step;
        session.Mark();
        Assert.Equal(step, session.Step);
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var input = new FakeMouseInputSource();
        var session = NewSession(input);
        session.Start();
        session.Dispose();

        var ex = Record.Exception(() => input.RaiseMove(5000, 5000));
        Assert.Null(ex);
        Assert.Equal(CalibrationStep.AwaitRightMark, session.Step);
    }

    [Fact]
    public void IdleTimeout_Fails()
    {
        var input = new FakeMouseInputSource();
        using var session = new CalibrationSession(input, TimeSpan.FromMilliseconds(80));
        string? ended = null;
        session.Ended += r => ended = r;

        session.Start();
        System.Threading.Thread.Sleep(250);

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.Contains("timed out", ended, StringComparison.OrdinalIgnoreCase);
    }
}
