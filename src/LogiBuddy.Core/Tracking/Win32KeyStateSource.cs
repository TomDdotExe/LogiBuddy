using System.Runtime.InteropServices;

namespace LogiBuddy.Core.Tracking;

public interface IKeyStateSource
{
    bool IsKeyDown(int virtualKeyCode);
}

/// Real Win32 key-state polling (GetAsyncKeyState), same API
/// Win32MouseHook already uses for the hold-style freelook hotkey.
public class Win32KeyStateSource : IKeyStateSource
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public bool IsKeyDown(int virtualKeyCode) => (GetAsyncKeyState(virtualKeyCode) & 0x8000) != 0;
}
