using System;
using SpotifyGameRadio.Core.Tracking;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Tracking;

public class CalibrationSessionTests
{
    private static CalibrationSession NewSession(FakeMouseInputSource input) =>
        new(input, TimeSpan.FromMinutes(5));

    [Fact]
    public void Start_MovesToAwaitCentre_AndAnnounces()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationStep? announced = null;
        session.StepChanged += (s, _) => announced = s;

        session.Start();

        Assert.Equal(CalibrationStep.AwaitCentre, session.Step);
        Assert.Equal(CalibrationStep.AwaitCentre, announced);
    }

    [Fact]
    public void CentreMark_ReZeroesAccumulators()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(-999, -999);   // pre-centre drift, must be discarded
        session.Mark();                 // centre

        input.RaiseMove(6000, 0);
        session.Mark();                 // right limit

        input.RaiseMove(-2000, -3000);  // net from centre: x +4000, y -3000
        session.Mark();                 // corner

        Assert.Equal(CalibrationStep.Completed, session.Step);
        Assert.NotNull(result);
        Assert.Equal(6000f, result!.HalfSweepCounts, precision: 3);
        Assert.Equal(4000f, result.CornerXCounts, precision: 3);
        Assert.Equal(-3000f, result.CornerYCounts, precision: 3);
    }

    [Fact]
    public void Movement_WhileHotkeyNotHeld_IsIgnored()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        session.Mark();                       // centre (nothing moved)
        input.RaiseMove(5000, 5000);          // ignored, key not held
        input.IsHotkeyHeld = true;
        input.RaiseMove(6000, 0);
        session.Mark();                       // right
        input.RaiseMove(0, -2000);
        session.Mark();                       // corner

        Assert.Equal(6000f, result!.HalfSweepCounts, precision: 3);
        Assert.Equal(6000f, result.CornerXCounts, precision: 3);
        Assert.Equal(-2000f, result.CornerYCounts, precision: 3);
    }

    [Fact]
    public void RightSweepTooSmall_Fails()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        var completed = false;
        string? ended = null;
        session.Completed += _ => completed = true;
        session.Ended += r => ended = r;

        session.Start();
        session.Mark();               // centre
        input.RaiseMove(20, 0);
        session.Mark();               // right, only 20 counts

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
        Assert.False(string.IsNullOrWhiteSpace(ended));
    }

    [Fact]
    public void CornerTooCloseToCentre_Fails()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        var completed = false;
        session.Completed += _ => completed = true;

        session.Start();
        session.Mark();               // centre
        input.RaiseMove(6000, 0);
        session.Mark();               // right
        input.RaiseMove(-6000, 10);   // back near centre: x 0, y 10 -> distance 10
        session.Mark();               // corner

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
    }

    [Fact]
    public void Abort_FromEachAwaitStep_EndsAndIgnoresLaterMarks()
    {
        foreach (var marksBeforeAbort in new[] { 0, 1, 2 })
        {
            var input = new FakeMouseInputSource { IsHotkeyHeld = true };
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
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        session.Start();
        session.Mark();
        input.RaiseMove(6000, 0); session.Mark();
        input.RaiseMove(0, -3000); session.Mark();

        var step = session.Step;
        session.Mark();
        Assert.Equal(step, session.Step);
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var session = NewSession(input);
        session.Start();
        session.Dispose();

        var ex = Record.Exception(() => input.RaiseMove(5000, 5000));
        Assert.Null(ex);
        Assert.Equal(CalibrationStep.AwaitCentre, session.Step);
    }

    [Fact]
    public void IdleTimeout_Fails()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = new CalibrationSession(input, TimeSpan.FromMilliseconds(80));
        string? ended = null;
        session.Ended += r => ended = r;

        session.Start();
        System.Threading.Thread.Sleep(250);

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.Contains("timed out", ended, StringComparison.OrdinalIgnoreCase);
    }
}
