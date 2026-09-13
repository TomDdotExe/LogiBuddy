using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LogiBuddy.Core.Config;

namespace LogiBuddy.App.Controls;

public partial class HotkeyCaptureControl : UserControl
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(FreelookHotkey), typeof(HotkeyCaptureControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyChanged));

    public FreelookHotkey? Hotkey
    {
        get => (FreelookHotkey?)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private bool _capturing;

    public HotkeyCaptureControl()
    {
        InitializeComponent();
        UpdateLabel();
    }

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HotkeyCaptureControl)d).UpdateLabel();

    private void UpdateLabel()
    {
        if (_capturing) { CaptureButton.Content = "Press a key or mouse button…"; return; }
        int vk = Hotkey?.VirtualKeyCode ?? 0;
        CaptureButton.Content = vk == 0 ? "(click to set)" : VirtualKeyName(vk);
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        UpdateLabel();
    }

    /// Clicking away from an armed control cancels capture, so it can't sit
    /// stuck on "Press a key or mouse button…" and then swallow the user's next
    /// click on it as "Mouse Left".
    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_capturing) return;
        _capturing = false;
        UpdateLabel();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        if (e.Key == Key.Escape) { _capturing = false; UpdateLabel(); return; }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return; // unmapped; keep waiting

        _capturing = false;
        Hotkey = new FreelookHotkey { VirtualKeyCode = vk };
        UpdateLabel();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing) return; // first click (to arm capture) falls through to OnCaptureClick
        e.Handled = true;

        int vk = e.ChangedButton switch
        {
            MouseButton.Left => 0x01,
            MouseButton.Right => 0x02,
            MouseButton.Middle => 0x04,
            MouseButton.XButton1 => 0x05,
            MouseButton.XButton2 => 0x06,
            _ => 0,
        };
        if (vk == 0) return;

        _capturing = false;
        Hotkey = new FreelookHotkey { VirtualKeyCode = vk };
        UpdateLabel();
    }

    internal static string VirtualKeyName(int vk) => vk switch
    {
        0x01 => "Mouse Left",
        0x02 => "Mouse Right",
        0x04 => "Mouse Middle",
        0x05 => "Mouse X1",
        0x06 => "Mouse X2",
        _ => KeyInterop.KeyFromVirtualKey(vk).ToString(),
    };
}
