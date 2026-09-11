using System.Timers;
using Timer = System.Timers.Timer;

namespace SpotifyGameRadio.Core.Tracking;

public enum CalibrationStep { Idle, AwaitRightMark, AwaitLeftMark, AwaitUpMark, AwaitDownMark, Completed, Failed, Aborted }

/// YawSweepCounts / PitchSweepCounts are mouse-count magnitudes each measured
/// across a known 180° rotation (right mark to left mark; up mark to down
/// mark), so MouseSensitivity/PitchSensitivity = 180 / SweepCounts.
public sealed record CalibrationResult(float YawSweepCounts, float PitchSweepCounts);

/// Guided freelook calibration. Subscribes to a live mouse input source and
/// sums counts on the relevant axis whenever a step is active — it does NOT
/// require the freelook key to be held, so it works with hold-to-look,
/// toggle, and always-on freelook alike. Four marks after Start(), two
/// independent phases:
///   yaw:   right mark (turn 90° right of centre) then left mark (turn 180°
///          back through centre, to 90° left of it)
///   pitch: up mark (look straight up until the view stops) then down mark
///          (look straight down until the view stops) — unlike yaw, most
///          games do hard-clamp vertical look, so the view's actual limit is
///          a reliable, easy-to-find reference here.
/// Each phase's count magnitude corresponds to exactly 180° of real rotation
/// regardless of the game's actual yaw limit, so the view model can derive
/// MouseSensitivity / PitchSensitivity directly (180 / SweepCounts) without
/// needing to find a yaw hard stop, and without mixing the two axes into one
/// ambiguous "corner" measurement.
public sealed class CalibrationSession : IDisposable
{
    private const long MinValidSweepCounts = 50;

    private readonly IMouseInputSource _input;
    private readonly Timer _idleTimer;
    private readonly object _gate = new();

    private long _accumX;
    private long _accumY;
    private float _yawSweep;
    private bool _disposed;

    public CalibrationStep Step { get; private set; } = CalibrationStep.Idle;

    /// Fires on every Step change with a one-line user-facing prompt.
    public event Action<CalibrationStep, string>? StepChanged;
    /// Fires once when Step becomes Completed.
    public event Action<CalibrationResult>? Completed;
    /// Fires once when Step becomes Failed or Aborted, with the reason.
    public event Action<string>? Ended;

    public CalibrationSession(IMouseInputSource input)
        : this(input, TimeSpan.FromSeconds(60)) { }

    public CalibrationSession(IMouseInputSource input, TimeSpan idleTimeout)
    {
        _input = input;
        _input.MouseMoved += OnMouseMoved;
        _idleTimer = new Timer(idleTimeout.TotalMilliseconds) { AutoReset = false };
        _idleTimer.Elapsed += OnIdleTimeout;
    }

    private bool IsActive => Step is CalibrationStep.AwaitRightMark
        or CalibrationStep.AwaitLeftMark
        or CalibrationStep.AwaitUpMark
        or CalibrationStep.AwaitDownMark;

    private void OnMouseMoved(int dx, int dy)
    {
        if (!IsActive) return;
        Interlocked.Add(ref _accumX, dx);
        Interlocked.Add(ref _accumY, dy);
    }

    public void Start() => Raise(() =>
    {
        lock (_gate)
        {
            if (Step != CalibrationStep.Idle) return null;
            Interlocked.Exchange(ref _accumX, 0);
            Step = CalibrationStep.AwaitRightMark;
            RestartTimer();
            return (Step,
                "Calibration started. Face forward, then turn 90 degrees right and tap Mark.",
                (CalibrationResult?)null);
        }
    });

    public void Mark() => Raise(() =>
    {
        lock (_gate)
        {
            switch (Step)
            {
                case CalibrationStep.AwaitRightMark:
                    Interlocked.Exchange(ref _accumX, 0); // this instant is the reference for the 180-turn
                    Step = CalibrationStep.AwaitLeftMark;
                    RestartTimer();
                    return (Step,
                        "Now turn 180 degrees to the left — back past where you started, to 90 degrees left of it — then tap Mark.",
                        (CalibrationResult?)null);

                case CalibrationStep.AwaitLeftMark:
                    long yawSweep = Math.Abs(Interlocked.Read(ref _accumX));
                    if (yawSweep < MinValidSweepCounts)
                    {
                        _idleTimer.Stop();
                        Step = CalibrationStep.Failed;
                        return (Step,
                            "That didn't register as a full 180 degree turn. Try again.",
                            (CalibrationResult?)null);
                    }
                    _yawSweep = yawSweep;
                    Interlocked.Exchange(ref _accumY, 0); // reference for the vertical 180-turn
                    Step = CalibrationStep.AwaitUpMark;
                    RestartTimer();
                    return (Step,
                        "Yaw set. Now look straight up until the view stops, then tap Mark.",
                        (CalibrationResult?)null);

                case CalibrationStep.AwaitUpMark:
                    Interlocked.Exchange(ref _accumY, 0); // this instant is the reference for the vertical 180-turn
                    Step = CalibrationStep.AwaitDownMark;
                    RestartTimer();
                    return (Step,
                        "Now look straight down until the view stops, then tap Mark.",
                        (CalibrationResult?)null);

                case CalibrationStep.AwaitDownMark:
                    _idleTimer.Stop();
                    long pitchSweep = Math.Abs(Interlocked.Read(ref _accumY));
                    if (pitchSweep < MinValidSweepCounts)
                    {
                        Step = CalibrationStep.Failed;
                        return (Step,
                            "That didn't register as a full up-to-down turn. Try again.",
                            (CalibrationResult?)null);
                    }
                    Step = CalibrationStep.Completed;
                    return (Step, "Down mark set. Calibration complete.",
                        new CalibrationResult(_yawSweep, pitchSweep));

                default:
                    return null;
            }
        }
    });

    public void Abort() => Raise(() =>
    {
        lock (_gate)
        {
            if (!IsActive) return null;
            _idleTimer.Stop();
            Step = CalibrationStep.Aborted;
            return (Step, "Calibration cancelled.", (CalibrationResult?)null);
        }
    });

    private void OnIdleTimeout(object? sender, ElapsedEventArgs e) => Raise(() =>
    {
        lock (_gate)
        {
            if (!IsActive) return null;
            Step = CalibrationStep.Failed;
            return (Step, "Calibration timed out.", (CalibrationResult?)null);
        }
    });

    private void RestartTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    /// Runs the transition delegate (which takes the lock and mutates Step),
    /// then raises the resulting events OUTSIDE any lock — handlers may
    /// dispose the session or write back to the profile.
    private void Raise(Func<(CalibrationStep step, string message, CalibrationResult? result)?> transition)
    {
        var outcome = transition();
        if (outcome is null) return;

        var (step, message, result) = outcome.Value;
        StepChanged?.Invoke(step, message);

        switch (step)
        {
            case CalibrationStep.Completed when result is not null:
                Completed?.Invoke(result);
                break;
            case CalibrationStep.Failed:
            case CalibrationStep.Aborted:
                Ended?.Invoke(message);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _input.MouseMoved -= OnMouseMoved;
        _idleTimer.Elapsed -= OnIdleTimeout;
        _idleTimer.Dispose();
    }
}
