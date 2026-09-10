using System.Timers;
using Timer = System.Timers.Timer;

namespace SpotifyGameRadio.Core.Tracking;

public enum CalibrationStep { Idle, AwaitCentre, AwaitRightLimit, AwaitCorner, Completed, Failed, Aborted }

/// All counts are centre-relative (measured from the centre mark).
/// CornerYCounts is +down, matching the raw mouse hook's dy sign.
public sealed record CalibrationResult(float HalfSweepCounts, float CornerXCounts, float CornerYCounts);

/// Guided freelook calibration. Subscribes to a live mouse input source,
/// sums horizontal and vertical counts while the freelook key is held, and
/// advances on Mark() calls (driven by a global tap-hotkey). Three marks:
///   centre  — establishes the zero reference
///   right   — horizontal half-sweep to the game's yaw limit
///   corner  — far up-and-to-one-side extreme
/// The view model turns these into MouseSensitivity, MaxPitchDegrees, and
/// the combined off-axis clamp.
public sealed class CalibrationSession : IDisposable
{
    private const long MinValidSweepCounts = 50;

    private readonly IMouseInputSource _input;
    private readonly Timer _idleTimer;
    private readonly object _gate = new();

    private long _accumX;
    private long _accumY;
    private long _halfSweep;
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

    private bool IsActive => Step is CalibrationStep.AwaitCentre
        or CalibrationStep.AwaitRightLimit
        or CalibrationStep.AwaitCorner;

    private void OnMouseMoved(int dx, int dy)
    {
        if (!IsActive || !_input.IsHotkeyHeld) return;
        Interlocked.Add(ref _accumX, dx);
        Interlocked.Add(ref _accumY, dy);
    }

    public void Start() => Raise(() =>
    {
        lock (_gate)
        {
            if (Step != CalibrationStep.Idle) return null;
            Interlocked.Exchange(ref _accumX, 0);
            Interlocked.Exchange(ref _accumY, 0);
            Step = CalibrationStep.AwaitCentre;
            RestartTimer();
            return (Step,
                "Calibration started. Get into the game, face straight forward, hold freelook, then tap Mark.",
                (CalibrationResult?)null);
        }
    });

    public void Mark() => Raise(() =>
    {
        lock (_gate)
        {
            switch (Step)
            {
                case CalibrationStep.AwaitCentre:
                    Interlocked.Exchange(ref _accumX, 0);
                    Interlocked.Exchange(ref _accumY, 0);
                    Step = CalibrationStep.AwaitRightLimit;
                    RestartTimer();
                    return (Step,
                        "Centre set. Look fully RIGHT until the view stops, then tap Mark.",
                        (CalibrationResult?)null);

                case CalibrationStep.AwaitRightLimit:
                    long half = Math.Abs(Interlocked.Read(ref _accumX));
                    if (half < MinValidSweepCounts)
                    {
                        _idleTimer.Stop();
                        Step = CalibrationStep.Failed;
                        return (Step,
                            "That sweep was too small. Try again — hold freelook and turn all the way to the limit.",
                            (CalibrationResult?)null);
                    }
                    _halfSweep = half; // keep; do NOT re-zero — the corner is measured from the same centre
                    Step = CalibrationStep.AwaitCorner;
                    RestartTimer();
                    return (Step,
                        "Right limit set. Now look to the far corner — as far up and to one side as the game allows — then tap Mark.",
                        (CalibrationResult?)null);

                case CalibrationStep.AwaitCorner:
                    _idleTimer.Stop();
                    long cx = Interlocked.Read(ref _accumX);
                    long cy = Interlocked.Read(ref _accumY);
                    if (Math.Sqrt((double)cx * cx + (double)cy * cy) < MinValidSweepCounts)
                    {
                        Step = CalibrationStep.Failed;
                        return (Step, "That corner was too close to centre. Try again.", (CalibrationResult?)null);
                    }
                    Step = CalibrationStep.Completed;
                    return (Step, "Corner set. Calibration complete.",
                        new CalibrationResult(_halfSweep, cx, cy));

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
