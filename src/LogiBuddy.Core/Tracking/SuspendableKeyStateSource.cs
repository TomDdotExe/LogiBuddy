namespace LogiBuddy.Core.Tracking;

/// Wraps a real IKeyStateSource so every hotkey watcher sharing this one
/// instance can be suspended together — used to stop typing in an in-game
/// chat box from firing bound hotkeys ("phantom presses"). While Suspended,
/// IsKeyDown reports "not pressed" regardless of actual physical key state.
public class SuspendableKeyStateSource : IKeyStateSource
{
    private readonly IKeyStateSource _inner;
    private volatile bool _suspended;

    public SuspendableKeyStateSource(IKeyStateSource inner) => _inner = inner;

    public bool Suspended { get => _suspended; set => _suspended = value; }

    public bool IsKeyDown(int virtualKeyCode) => !_suspended && _inner.IsKeyDown(virtualKeyCode);
}
