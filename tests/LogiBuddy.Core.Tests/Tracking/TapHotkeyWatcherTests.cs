using LogiBuddy.Core.Config;
using LogiBuddy.Core.Tracking;
using Xunit;

namespace LogiBuddy.Core.Tests.Tracking;

public class FakeKeyStateSource : IKeyStateSource
{
    private readonly HashSet<int> _down = new();
    public void SetDown(int virtualKeyCode, bool down)
    {
        if (down) _down.Add(virtualKeyCode); else _down.Remove(virtualKeyCode);
    }
    public bool IsKeyDown(int virtualKeyCode) => _down.Contains(virtualKeyCode);
}

public class TapHotkeyWatcherTests
{
    private const int Vk = 0x52; // 'R'

    [Fact]
    public void Poll_FiresOnceWhenKeyGoesDown()
    {
        var keyState = new FakeKeyStateSource();
        var watcher = new TapHotkeyWatcher(new FreelookHotkey { VirtualKeyCode = Vk }, keyState, startPolling: false);
        int fireCount = 0;
        watcher.Pressed += () => fireCount++;

        watcher.Poll(); // key up, no fire
        keyState.SetDown(Vk, true);
        watcher.Poll(); // rising edge, fires

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void Poll_DoesNotRefireWhileHeld()
    {
        var keyState = new FakeKeyStateSource();
        var watcher = new TapHotkeyWatcher(new FreelookHotkey { VirtualKeyCode = Vk }, keyState, startPolling: false);
        int fireCount = 0;
        watcher.Pressed += () => fireCount++;
        keyState.SetDown(Vk, true);

        watcher.Poll();
        watcher.Poll();
        watcher.Poll();

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void Poll_FiresAgainAfterReleaseThenPress()
    {
        var keyState = new FakeKeyStateSource();
        var watcher = new TapHotkeyWatcher(new FreelookHotkey { VirtualKeyCode = Vk }, keyState, startPolling: false);
        int fireCount = 0;
        watcher.Pressed += () => fireCount++;

        keyState.SetDown(Vk, true);
        watcher.Poll(); // fire 1
        keyState.SetDown(Vk, false);
        watcher.Poll(); // release, no fire
        keyState.SetDown(Vk, true);
        watcher.Poll(); // fire 2

        Assert.Equal(2, fireCount);
    }

    [Fact]
    public void Poll_UnboundHotkeyNeverFires()
    {
        var keyState = new FakeKeyStateSource();
        var watcher = new TapHotkeyWatcher(new FreelookHotkey { VirtualKeyCode = 0 }, keyState, startPolling: false);
        int fireCount = 0;
        watcher.Pressed += () => fireCount++;

        keyState.SetDown(0, true);
        watcher.Poll();

        Assert.Equal(0, fireCount);
    }

    [Fact]
    public void Poll_FiresReleasedOnceWhenKeyGoesUp()
    {
        var keyState = new FakeKeyStateSource();
        var watcher = new TapHotkeyWatcher(new FreelookHotkey { VirtualKeyCode = Vk }, keyState, startPolling: false);
        int releasedCount = 0;
        watcher.Released += () => releasedCount++;

        keyState.SetDown(Vk, true);
        watcher.Poll(); // rising edge, no release
        keyState.SetDown(Vk, false);
        watcher.Poll(); // falling edge, fires

        Assert.Equal(1, releasedCount);
    }

    [Fact]
    public void Poll_DoesNotRefireReleasedWhileUp()
    {
        var keyState = new FakeKeyStateSource();
        var watcher = new TapHotkeyWatcher(new FreelookHotkey { VirtualKeyCode = Vk }, keyState, startPolling: false);
        int releasedCount = 0;
        watcher.Released += () => releasedCount++;

        keyState.SetDown(Vk, true);
        watcher.Poll();
        keyState.SetDown(Vk, false);

        watcher.Poll();
        watcher.Poll();
        watcher.Poll();

        Assert.Equal(1, releasedCount);
    }

    [Fact]
    public void Poll_UnboundHotkeyNeverFiresReleased()
    {
        var keyState = new FakeKeyStateSource();
        var watcher = new TapHotkeyWatcher(new FreelookHotkey { VirtualKeyCode = 0 }, keyState, startPolling: false);
        int releasedCount = 0;
        watcher.Released += () => releasedCount++;

        keyState.SetDown(0, true);
        watcher.Poll();
        keyState.SetDown(0, false);
        watcher.Poll();

        Assert.Equal(0, releasedCount);
    }

    [Fact]
    public void SetHotkey_RebindsLive()
    {
        var keyState = new FakeKeyStateSource();
        var watcher = new TapHotkeyWatcher(new FreelookHotkey { VirtualKeyCode = Vk }, keyState, startPolling: false);
        int fireCount = 0;
        watcher.Pressed += () => fireCount++;

        watcher.SetHotkey(new FreelookHotkey { VirtualKeyCode = 0x56 }); // 'V'
        keyState.SetDown(Vk, true); // old key held - must not fire
        watcher.Poll();
        Assert.Equal(0, fireCount);

        keyState.SetDown(0x56, true); // new key held - must fire
        watcher.Poll();
        Assert.Equal(1, fireCount);
    }
}
