using System.Timers;
using Timer = System.Timers.Timer;

namespace SpotifyGameRadio.Core.Tracking;

public enum CalibrationStep { Idle, AwaitLeftLimit, AwaitRightLimit, Completed, Failed, Aborted }

public sealed record CalibrationResult(float MeasuredHalfSweepCounts);

/// Guided freelook calibration. Subscribes to a live mouse input source,
/// sums horizontal counts while the freelook key is held, and advances on
/// Mark() calls (driven by a global tap-hotkey). Two marks — one at each
/// game view limit — yield the full sweep; half of it is the centre-to-limit
/// count the view model turns into MouseSensitivity.
public sealed class CalibrationSession : IDisposable
{
    private const long MinValidSweepCounts = 50;

    private readonly IMouseInputSource _input;
    private readonly Timer _idleTimer;
    private readonly object _gate = new();

    private long _accum;
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

    private bool IsActive => Step is CalibrationStep.AwaitLeftLimit or CalibrationStep.AwaitRightLimit;

    private void OnMouseMoved(int dx, int dy)
    {
        // Cheap unsynchronised gate; the Interlocked.Add is the real guard.
        if (!IsActive || !_input.IsHotkeyHeld) return;
        Interlocked.Add(ref _accum, dx);
    }

    public void Start() => Raise(() =>
    {
        lock (_gate)
        {
            if (Step != CalibrationStep.Idle) return null;
            Interlocked.Exchange(ref _accum, 0);
            Step = CalibrationStep.AwaitLeftLimit;
            RestartTimer();
            return (Step, "Hold freelook, look fully LEFT until the view stops, then tap Mark.", (CalibrationResult?)null);
        }
    });

    public void Mark() => Raise(() =>
    {
        lock (_gate)
        {
            switch (Step)
            {
                case CalibrationStep.AwaitLeftLimit:
                    Interlocked.Exchange(ref _accum, 0);
                    Step = CalibrationStep.AwaitRightLimit;
                    RestartTimer();
                    return (Step, "Now look fully RIGHT until the view stops, then tap Mark.", (CalibrationResult?)null);

                case CalibrationStep.AwaitRightLimit:
                    _idleTimer.Stop();
                    long sweep = Math.Abs(Interlocked.Read(ref _accum));
                    if (sweep < MinValidSweepCounts)
                    {
                        Step = CalibrationStep.Failed;
                        return (Step,
                            "Calibration failed: the sweep was too small. Hold the freelook key and turn all the way to each limit.",
                            (CalibrationResult?)null);
                    }
                    Step = CalibrationStep.Completed;
                    return (Step, "Calibration done — sensitivity updated.", new CalibrationResult(sweep / 2f));

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
