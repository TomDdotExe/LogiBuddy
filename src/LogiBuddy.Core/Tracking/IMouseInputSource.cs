namespace LogiBuddy.Core.Tracking;

public interface IMouseInputSource
{
    bool IsHotkeyHeld { get; }

    /// Raised with raw (deltaX, deltaY) mouse counts, independent of hotkey state.
    event Action<int, int>? MouseMoved;
}
