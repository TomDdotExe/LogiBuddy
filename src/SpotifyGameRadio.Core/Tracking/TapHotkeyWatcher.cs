using SpotifyGameRadio.Core.Config;

namespace SpotifyGameRadio.Core.Tracking;

/// Polls a single rebindable key and raises Pressed once per press (rising
/// edge only) — unlike Win32MouseHook's hold-style freelook hotkey, this
/// never repeatedly fires while held. VirtualKeyCode == 0 means "unbound":
/// Poll() never fires.
public class TapHotkeyWatcher : IDisposable
{
    public event Action? Pressed;

    private readonly IKeyStateSource _keyState;
    private volatile FreelookHotkey _hotkey;
    private readonly Thread? _pollThread;
    private volatile bool _running = true;
    private bool _wasDown;

    /// Production use: starts a background poll thread at the same ~8ms
    /// cadence Win32MouseHook already uses for its hotkey poll.
    public TapHotkeyWatcher(FreelookHotkey hotkey, IKeyStateSource keyState)
        : this(hotkey, keyState, startPolling: true) { }

    /// Test use: pass startPolling: false and call Poll() directly to
    /// control timing deterministically, without a live background thread.
    public TapHotkeyWatcher(FreelookHotkey hotkey, IKeyStateSource keyState, bool startPolling)
    {
        _hotkey = hotkey;
        _keyState = keyState;
        if (startPolling)
        {
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "SGR-TapHotkey" };
            _pollThread.Start();
        }
    }

    /// Rebinds the key without reinstalling anything. The poll thread reads
    /// the new value on its next iteration (~8 ms).
    public void SetHotkey(FreelookHotkey hotkey) => _hotkey = hotkey;

    private void PollLoop()
    {
        while (_running)
        {
            Poll();
            Thread.Sleep(8);
        }
    }

    /// Runs exactly one edge-detection check. Public so tests can drive it
    /// deterministically without depending on thread timing.
    public void Poll()
    {
        var hotkey = _hotkey;
        bool isDown = hotkey.VirtualKeyCode != 0 && _keyState.IsKeyDown(hotkey.VirtualKeyCode);
        if (isDown && !_wasDown) Pressed?.Invoke();
        _wasDown = isDown;
    }

    public void Dispose() => _running = false;
}
