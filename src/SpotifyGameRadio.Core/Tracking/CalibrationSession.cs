using System.Timers;
using Timer = System.Timers.Timer;

namespace SpotifyGameRadio.Core.Tracking;

public enum CalibrationStep { Idle, AwaitRightLimit, Completed, Failed, Aborted }

/// SweepCounts is the mouse-count magnitude measured from centre to the
/// game's actual yaw limit.
public sealed record CalibrationResult(float SweepCounts);

/// Guided freelook calibration. Subscribes to a live mouse input source and
/// sums horizontal counts while active — it does NOT require the freelook
/// key to be held, so it works with hold-to-look, toggle, and always-on
/// freelook alike. One mark after Start(): turn right until the game's view
/// stops, then mark. Unlike pitch (where straight-up-to-straight-down is
/// always exactly 180° by geometry, regardless of the game), yaw's actual
/// limit is game-specific and often well short of 90° — so there's no
/// universal fixed angle to sweep to here. Instead the view model derives
/// MouseSensitivity from the profile's own MaxYawDegrees (the angle the user
/// has told it the game allows): MouseSensitivity = MaxYawDegrees /
/// SweepCounts. Vertical movement during the sweep is ignored, so it
/// doesn't matter that real human turns are never perfectly horizontal.
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

    private bool IsActive => Step is CalibrationStep.AwaitRightLimit;

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
            Step = CalibrationStep.AwaitRightLimit;
            RestartTimer();
            return (Step,
                "Calibration started. Face forward, then turn right until the view stops, then tap Mark.",
                (CalibrationResult?)null);
        }
    });

    public void Mark() => Raise(() =>
    {
        lock (_gate)
        {
            if (Step != CalibrationStep.AwaitRightLimit) return null;
            _idleTimer.Stop();
            long sweep = Math.Abs(Interlocked.Read(ref _accumX));
            if (sweep < MinValidSweepCounts)
            {
                Step = CalibrationStep.Failed;
                return (Step,
                    "That sweep was too small. Try again — turn all the way to the view's limit.",
                    (CalibrationResult?)null);
            }
            Step = CalibrationStep.Completed;
            return (Step, "Mark set. Calibration complete.", new CalibrationResult(sweep));
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
