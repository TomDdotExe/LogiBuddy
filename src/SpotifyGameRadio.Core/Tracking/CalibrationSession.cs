using System.Timers;
using Timer = System.Timers.Timer;

namespace SpotifyGameRadio.Core.Tracking;

public enum CalibrationStep { Idle, AwaitRightMark, AwaitLeftMark, Completed, Failed, Aborted }

/// SweepCounts is the mouse-count magnitude measured across a known 180°
/// yaw rotation (right mark to left mark).
public sealed record CalibrationResult(float SweepCounts);

/// Guided freelook calibration. Subscribes to a live mouse input source and
/// sums horizontal counts whenever a step is active — it does NOT require
/// the freelook key to be held, so it works with hold-to-look, toggle, and
/// always-on freelook alike. Two marks after Start():
///   right mark — the user turns 90° right of centre and marks
///   left mark  — the user turns 180° back the other way (through centre,
///                to 90° left of it) and marks
/// The count magnitude between the two marks corresponds to exactly 180° of
/// real rotation regardless of the game's actual view limits, so the view
/// model can derive MouseSensitivity directly (180 / SweepCounts) without
/// needing to find a hard stop.
public sealed class CalibrationSession : IDisposable
{
    private const long MinValidSweepCounts = 50;

    private readonly IMouseInputSource _input;
    private readonly Timer _idleTimer;
    private readonly object _gate = new();

    private long _accumX;
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
        or CalibrationStep.AwaitLeftMark;

    private void OnMouseMoved(int dx, int dy)
    {
        if (!IsActive) return;
        Interlocked.Add(ref _accumX, dx);
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
                    _idleTimer.Stop();
                    long sweep = Math.Abs(Interlocked.Read(ref _accumX));
                    if (sweep < MinValidSweepCounts)
                    {
                        Step = CalibrationStep.Failed;
                        return (Step,
                            "That didn't register as a full 180 degree turn. Try again.",
                            (CalibrationResult?)null);
                    }
                    Step = CalibrationStep.Completed;
                    return (Step, "Left mark set. Calibration complete.",
                        new CalibrationResult(sweep));

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
