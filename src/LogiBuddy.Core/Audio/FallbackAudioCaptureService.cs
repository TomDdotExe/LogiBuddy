using NAudio.Wave;

namespace LogiBuddy.Core.Audio;

/// Wraps a primary capture service (per-process loopback) and swaps to a
/// fallback (whole-device loopback) when the primary proves non-functional on
/// this machine — i.e. it reports <see cref="AudioCaptureStatus.Error"/> before
/// it has ever reached <see cref="AudioCaptureStatus.Capturing"/>. Consumers see
/// a single <see cref="IAudioCaptureService"/> whose backing implementation may
/// change once, transparently.
///
/// An Error that arrives *after* a successful Capturing is a runtime loss, not a
/// "won't work here" — it is forwarded and left to the primary's own retry loop,
/// not treated as a reason to swap.
public class FallbackAudioCaptureService : IAudioCaptureService
{
    private readonly Func<IAudioCaptureService> _primaryFactory;
    private readonly Func<IAudioCaptureService> _fallbackFactory;
    private readonly Action<Action> _swapDispatcher;
    private readonly object _lock = new();

    private IAudioCaptureService? _active;
    private string _processName = "";
    private bool _sawCapturing;
    private bool _swapped;
    private bool _stopped;

    public WaveFormat Format => _active?.Format ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;

    /// Raised once, when the service gives up on the primary and switches to the
    /// fallback, with a user-facing explanation.
    public event Action<string>? Notice;

    /// <param name="swapDispatcher">
    /// Runs the primary→fallback swap. Defaults to <see cref="Task.Run(Action)"/>
    /// so the swap never executes on the primary's own worker thread (stopping
    /// it from there would deadlock its join). Tests pass a synchronous runner.
    /// </param>
    public FallbackAudioCaptureService(
        Func<IAudioCaptureService> primaryFactory,
        Func<IAudioCaptureService> fallbackFactory,
        Action<Action>? swapDispatcher = null)
    {
        _primaryFactory = primaryFactory;
        _fallbackFactory = fallbackFactory;
        _swapDispatcher = swapDispatcher ?? (a => Task.Run(a));
    }

    public void Start(string processName)
    {
        _processName = processName;
        var primary = _primaryFactory();
        lock (_lock) { _active = primary; }
        Subscribe(primary);
        primary.Start(processName);
    }

    private void Subscribe(IAudioCaptureService svc)
    {
        svc.DataAvailable += OnData;
        svc.StatusChanged += OnStatus;
    }

    private void Unsubscribe(IAudioCaptureService svc)
    {
        svc.DataAvailable -= OnData;
        svc.StatusChanged -= OnStatus;
    }

    private void OnData(object? sender, float[] samples) => DataAvailable?.Invoke(this, samples);

    private void OnStatus(object? sender, AudioCaptureStatus status)
    {
        bool swapNow = false;
        lock (_lock)
        {
            if (status == AudioCaptureStatus.Capturing) _sawCapturing = true;

            if (status == AudioCaptureStatus.Error && !_sawCapturing && !_swapped && !_stopped)
            {
                _swapped = true;
                swapNow = true;
            }
        }

        if (swapNow)
        {
            // Swallow the triggering Error — the fallback will report its own
            // status once it starts.
            _swapDispatcher(SwapToFallback);
            return;
        }

        StatusChanged?.Invoke(this, status);
    }

    private void SwapToFallback()
    {
        IAudioCaptureService? old;
        lock (_lock)
        {
            if (_stopped) return;
            old = _active;
        }

        if (old is not null)
        {
            Unsubscribe(old);
            try { old.Stop(); old.Dispose(); }
            catch { /* best effort — the primary is being abandoned anyway */ }
        }

        var fallback = _fallbackFactory();
        lock (_lock)
        {
            if (_stopped)
            {
                try { fallback.Dispose(); } catch { /* best effort */ }
                return;
            }
            _active = fallback;
        }

        Subscribe(fallback);
        Notice?.Invoke("Per-process capture unavailable — capturing the whole output device instead.");
        fallback.Start(_processName);
    }

    public void Stop()
    {
        IAudioCaptureService? active;
        lock (_lock)
        {
            _stopped = true;
            active = _active;
        }
        if (active is not null)
        {
            Unsubscribe(active);
            active.Stop();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_lock) { _active?.Dispose(); }
    }
}
