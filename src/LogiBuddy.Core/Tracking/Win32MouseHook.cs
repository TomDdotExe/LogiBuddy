using System.Runtime.InteropServices;
using LogiBuddy.Core.Config;

namespace LogiBuddy.Core.Tracking;

/// Global relative mouse-delta capture via Windows Raw Input, plus polled
/// hotkey state. Raw Input reports true relative deltas straight from the
/// mouse driver, unaffected by the OS cursor's on-screen position — unlike
/// a low-level mouse hook diffing cursor coordinates, which silently drops
/// movement once the (invisible) system cursor pins against a screen edge,
/// exactly the case a large continuous freelook/calibration swipe hits.
/// Does not read from or inject into any other process — same risk class
/// as AutoHotkey.
public class Win32MouseHook : IMouseInputSource, IDisposable
{
    private const uint WM_INPUT = 0x00FF;
    private const uint WM_QUIT = 0x0012;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0;
    private const ushort MOUSE_MOVE_ABSOLUTE = 0x0001;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private const string WindowClassName = "SGR-RawInputWindow";

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    /// usButtonFlags/usButtonData (a nested struct in the real Win32 union)
    /// collapse into ulButtons here; only lLastX/lLastY are used.
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public ushort usFlags;
        public uint ulButtons;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE mouse;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly WndProc _wndProc; // kept alive: native code holds a raw pointer to this delegate
    private volatile FreelookHotkey _hotkey;
    private readonly IKeyStateSource _keyState;
    private readonly Thread _messageThread;
    private readonly Thread _hotkeyPollThread;
    private uint _messageThreadId;
    private volatile bool _pollRunning = true;

    public bool IsHotkeyHeld { get; private set; }
    public event Action<int, int>? MouseMoved;

    /// Rebinds the freelook key without reinstalling raw input registration.
    /// The poll thread reads the new value on its next iteration (~8 ms).
    public void SetHotkey(FreelookHotkey hotkey) => _hotkey = hotkey;

    /// Thrown if the raw input window/registration could not be created (e.g. blocked by policy/AV).
    public bool HookInstalled { get; private set; }

    /// keyState lets the hold-hotkey poll be gated the same way TapHotkeyWatcher's
    /// is (e.g. suspended while typing in an in-game chat box) — pass the same
    /// shared SuspendableKeyStateSource instance used for the app's other hotkeys.
    public Win32MouseHook(FreelookHotkey hotkey, IKeyStateSource keyState)
    {
        _hotkey = hotkey;
        _keyState = keyState;
        _wndProc = WndProcCallback;

        _messageThread = new Thread(RunMessageLoop) { IsBackground = true, Name = "SGR-RawInput" };
        _messageThread.Start();

        _hotkeyPollThread = new Thread(PollHotkeyState) { IsBackground = true, Name = "SGR-HotkeyPoll" };
        _hotkeyPollThread.Start();
    }

    private void RunMessageLoop()
    {
        _messageThreadId = GetCurrentThreadId();
        IntPtr hInstance = GetModuleHandle(null);

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        RegisterClassEx(ref wc);

        IntPtr hwnd = CreateWindowEx(0, WindowClassName, null, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

        HookInstalled = hwnd != IntPtr.Zero && RegisterForRawInput(hwnd);

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        UnregisterClass(WindowClassName, hInstance);
    }

    private static bool RegisterForRawInput(IntPtr hwnd)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = HID_USAGE_PAGE_GENERIC,
            usUsage = HID_USAGE_GENERIC_MOUSE,
            dwFlags = RIDEV_INPUTSINK, // receive input even while another window (the game) has focus
            hwndTarget = hwnd,
        };
        return RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private IntPtr WndProcCallback(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT) HandleRawInput(lParam);
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != size)
                return;

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
            if (raw.header.dwType != RIM_TYPEMOUSE) return;
            if ((raw.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0) return; // e.g. RDP/tablet — not a relative delta

            if (raw.mouse.lLastX != 0 || raw.mouse.lLastY != 0)
                MouseMoved?.Invoke(raw.mouse.lLastX, raw.mouse.lLastY);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void PollHotkeyState()
    {
        while (_pollRunning)
        {
            IsHotkeyHeld = _hotkey.VirtualKeyCode != 0 && _keyState.IsKeyDown(_hotkey.VirtualKeyCode);
            Thread.Sleep(8); // ~120Hz poll
        }
    }

    public void Dispose()
    {
        _pollRunning = false;
        if (_messageThreadId != 0) PostThreadMessage(_messageThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
