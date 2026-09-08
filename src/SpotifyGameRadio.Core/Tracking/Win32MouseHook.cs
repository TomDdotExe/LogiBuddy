using System.Runtime.InteropServices;
using SpotifyGameRadio.Core.Config;

namespace SpotifyGameRadio.Core.Tracking;

/// Global low-level mouse hook + polled hotkey state. Does not read from or
/// inject into any other process — same risk class as AutoHotkey.
public class Win32MouseHook : IMouseInputSource, IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private readonly LowLevelMouseProc _proc;
    // Swapped live by SetHotkey; PollHotkeyState re-reads it every iteration.
    private volatile FreelookHotkey _hotkey;
    private readonly Thread _hookThread;
    private readonly Thread _hotkeyPollThread;
    private IntPtr _hookHandle = IntPtr.Zero;
    private volatile bool _running = true;
    private int _lastX, _lastY;
    private bool _havePreviousPoint;

    public bool IsHotkeyHeld { get; private set; }
    public event Action<int, int>? MouseMoved;

    /// Rebinds the freelook key without reinstalling the hook. The poll thread
    /// reads the new value on its next iteration (~8 ms).
    public void SetHotkey(FreelookHotkey hotkey) => _hotkey = hotkey;

    /// Thrown if the low-level hook could not be installed (e.g. blocked by policy/AV).
    public bool HookInstalled { get; private set; }

    public Win32MouseHook(FreelookHotkey hotkey)
    {
        _hotkey = hotkey;
        _proc = HookCallback;

        _hookThread = new Thread(RunHookMessageLoop) { IsBackground = true, Name = "SGR-MouseHook" };
        _hookThread.Start();

        _hotkeyPollThread = new Thread(PollHotkeyState) { IsBackground = true, Name = "SGR-HotkeyPoll" };
        _hotkeyPollThread.Start();
    }

    private void RunHookMessageLoop()
    {
        using var curModule = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(curModule?.ModuleName ?? ""), 0);
        HookInstalled = _hookHandle != IntPtr.Zero;

        // Pump a Win32 message loop so the hook callback is dispatched.
        while (_running)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(1);
        }

        if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);
    }

    private void PollHotkeyState()
    {
        while (_running)
        {
            // High bit set = key currently down.
            IsHotkeyHeld = (GetAsyncKeyState(_hotkey.VirtualKeyCode) & 0x8000) != 0;
            Thread.Sleep(8); // ~120Hz poll
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEMOVE)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (_havePreviousPoint)
            {
                int dx = hookStruct.pt.X - _lastX;
                int dy = hookStruct.pt.Y - _lastY;
                if (dx != 0 || dy != 0) MouseMoved?.Invoke(dx, dy);
            }
            _lastX = hookStruct.pt.X;
            _lastY = hookStruct.pt.Y;
            _havePreviousPoint = true;
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _running = false;
    }
}
