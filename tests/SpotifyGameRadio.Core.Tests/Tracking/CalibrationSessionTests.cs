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

    [Fact]
    public void Start_ZeroesAccumulator_MovementBeforeStartIsIgnored()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);

        input.RaiseMove(-999, -999);   // before Start: Step is Idle, ignored
        session.Start();
        input.RaiseMove(6000, 0);
        session.Mark();                 // right mark

        input.RaiseMove(-7000, 0);
        session.Mark();                 // left mark

        Assert.Equal(CalibrationStep.Completed, session.Step);
    }

    [Fact]
    public void RightMark_RezeroesAccumulator_OnlyMovementBetweenMarksCounts()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(6000, 0);       // the "turn 90 right" sweep, discarded at the mark
        session.Mark();                 // right mark rezeroes

        input.RaiseMove(-7000, 0);      // the 180-left sweep
        session.Mark();                 // left mark

        Assert.Equal(7000f, result!.SweepCounts, precision: 3);
    }

    [Fact]
    public void Movement_IsCounted_RegardlessOfFreelookKey()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false }; // toggle-style: key not held
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(6000, 0);
        session.Mark();                 // right
        input.RaiseMove(-7000, 0);
        session.Mark();                 // left

        Assert.Equal(7000f, result!.SweepCounts, precision: 3);
    }

    [Fact]
    public void SweepCounts_IsAbsoluteValue_DirectionAgnostic()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(-6000, 0);      // turned right using negative dx convention
        session.Mark();
        input.RaiseMove(7000, 0);
        session.Mark();

        Assert.Equal(7000f, result!.SweepCounts, precision: 3);
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
    public void Abort_FromEachAwaitStep_EndsAndIgnoresLaterMarks()
    {
        foreach (var marksBeforeAbort in new[] { 0, 1 })
        {
            var input = new FakeMouseInputSource();
            using var session = NewSession(input);
            string? ended = null;
            session.Ended += r => ended = r;

            session.Start();
            for (int i = 0; i < marksBeforeAbort; i++) { input.RaiseMove(6000, 0); session.Mark(); }
            session.Abort();

            Assert.Equal(CalibrationStep.Aborted, session.Step);
            Assert.False(string.IsNullOrWhiteSpace(ended));

            input.RaiseMove(9999, 0);
            session.Mark();
            Assert.Equal(CalibrationStep.Aborted, session.Step);
        }
    }

    [Fact]
    public void Mark_AfterCompleted_IsNoOp()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        session.Start();
        input.RaiseMove(6000, 0); session.Mark();
        input.RaiseMove(-7000, 0); session.Mark();

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
