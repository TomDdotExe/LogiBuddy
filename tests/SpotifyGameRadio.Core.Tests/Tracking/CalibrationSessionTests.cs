using System;
using SpotifyGameRadio.Core.Tracking;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Tracking;

public class CalibrationSessionTests
{
    private static CalibrationSession NewSession(FakeMouseInputSource input) =>
        new(input, TimeSpan.FromMinutes(5));

    [Fact]
    public void Start_MovesToAwaitRightLimit_AndAnnounces()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationStep? announced = null;
        session.StepChanged += (s, _) => announced = s;

        session.Start();

        Assert.Equal(CalibrationStep.AwaitRightLimit, session.Step);
        Assert.Equal(CalibrationStep.AwaitRightLimit, announced);
    }

    [Fact]
    public void Start_ZeroesAccumulator_MovementBeforeStartIsIgnored()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        input.RaiseMove(-999, -999);   // before Start: Step is Idle, ignored
        session.Start();
        input.RaiseMove(6000, 0);
        session.Mark();

        Assert.Equal(CalibrationStep.Completed, session.Step);
        Assert.Equal(6000f, result!.SweepCounts, precision: 3);
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
        session.Mark();

        Assert.Equal(6000f, result!.SweepCounts, precision: 3);
    }

    [Fact]
    public void SweepCounts_IsAbsoluteValue_DirectionAgnostic()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(-6000, 0); // turned right using negative dx convention
        session.Mark();

        Assert.Equal(6000f, result!.SweepCounts, precision: 3);
    }

    [Fact]
    public void VerticalMovement_IsIgnored()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(6000, 9999); // incidental vertical drift during the sweep
        session.Mark();

        Assert.Equal(6000f, result!.SweepCounts, precision: 3);
    }

    [Fact]
    public void SweepTooSmall_Fails()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        var completed = false;
        string? ended = null;
        session.Completed += _ => completed = true;
        session.Ended += r => ended = r;

        session.Start();
        input.RaiseMove(20, 0);
        session.Mark();

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
        Assert.False(string.IsNullOrWhiteSpace(ended));
    }

    [Fact]
    public void Abort_WhileActive_EndsAndIgnoresLaterMarks()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        string? ended = null;
        session.Ended += r => ended = r;

        session.Start();
        session.Abort();

        Assert.Equal(CalibrationStep.Aborted, session.Step);
        Assert.False(string.IsNullOrWhiteSpace(ended));

        input.RaiseMove(9999, 0);
        session.Mark();
        Assert.Equal(CalibrationStep.Aborted, session.Step);
    }

    [Fact]
    public void Mark_AfterCompleted_IsNoOp()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        session.Start();
        input.RaiseMove(6000, 0);
        session.Mark();

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
        Assert.Equal(CalibrationStep.AwaitRightLimit, session.Step);
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

    [Fact]
    public void Start_Outside_MovesToAwaitFullSpin_AndAnnounces()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationStep? announced = null;
        session.StepChanged += (s, _) => announced = s;

        session.Start(CalibrationMode.Outside);

        Assert.Equal(CalibrationStep.AwaitFullSpin, session.Step);
        Assert.Equal(CalibrationStep.AwaitFullSpin, announced);
    }

    [Fact]
    public void Outside_Mark_CompletesWithSweepCounts()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start(CalibrationMode.Outside);
        input.RaiseMove(3600, 0); // full 360 spin
        session.Mark();

        Assert.Equal(CalibrationStep.Completed, session.Step);
        Assert.Equal(3600f, result!.SweepCounts, precision: 3);
    }

    [Fact]
    public void Outside_SweepCounts_IsAbsoluteValue_DirectionAgnostic()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start(CalibrationMode.Outside);
        input.RaiseMove(-3600, 0);
        session.Mark();

        Assert.Equal(3600f, result!.SweepCounts, precision: 3);
    }

    [Fact]
    public void Outside_IgnoresVerticalMovement()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start(CalibrationMode.Outside);
        input.RaiseMove(3600, 9999); // incidental vertical drift during the spin — pitch isn't calibrated at all
        session.Mark();

        Assert.Equal(3600f, result!.SweepCounts, precision: 3);
    }

    [Fact]
    public void Outside_SweepTooSmall_Fails()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        var completed = false;
        string? ended = null;
        session.Completed += _ => completed = true;
        session.Ended += r => ended = r;

        session.Start(CalibrationMode.Outside);
        input.RaiseMove(20, 0);
        session.Mark();

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
        Assert.False(string.IsNullOrWhiteSpace(ended));
    }

    [Fact]
    public void Cockpit_Mode_IsTheDefault_WhenStartCalledWithNoArguments()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);

        session.Start();

        Assert.Equal(CalibrationStep.AwaitRightLimit, session.Step);
    }
}
