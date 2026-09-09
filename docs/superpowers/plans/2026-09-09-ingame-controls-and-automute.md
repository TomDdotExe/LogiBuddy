# In-game Calibration, Vehicle Toggle, and Source Auto-mute Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a global Recenter hotkey, a global vehicle in/out toggle with a
configurable exit delay, and automatic muting of the source app's own
Windows audio session on Start.

**Architecture:** A new poll-based `TapHotkeyWatcher` (edge-triggered, unlike
the existing hold-style freelook hotkey) drives Recenter and the vehicle
toggle without needing window focus. `RadioPipeline` gets an independent,
non-persisted mute gain for the vehicle toggle so it never fights the user's
Volume slider. A new `NAudioSourceSessionMuter` mutes the source process's
audio session directly via NAudio, replacing the manual-mixer-mute step.

**Tech Stack:** C# / .NET 8, WPF, xUnit, NAudio.

**Spec:** `docs/superpowers/specs/2026-09-09-ingame-controls-and-automute-design.md`

## Global Constraints

- Recenter and vehicle-toggle hotkeys are **tap** (fire once on press), not
  **hold** — never repeat-fire while held.
- Both new hotkeys default to **unbound** (`VirtualKeyCode = 0`) — never
  fire until the user explicitly binds them.
- Vehicle mute must **never** write to `Profile.Volume` — it's an
  independent, non-persisted gain on `RadioPipeline`.
- Vehicle exit delay applies only when toggling to "out"; entering "in" is
  always immediate.
- `AutoMuteSource` defaults to `true` and is a restart-required setting,
  like `AutoRouteSource`.
- Session muting uses NAudio's existing `AudioSessionControl.SimpleAudioVolume`
  — no new native/COM interop.
- Real-time audio path (`RadioPipeline.OnDataAvailable`): no heap allocation.

---

### Task 1: RadioProfile fields + legacy-load tolerance

**Files:**
- Modify: `src/SpotifyGameRadio.Core/Config/RadioProfile.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Produces: `RadioProfile.RecenterHotkey` (`FreelookHotkey`, default
  `VirtualKeyCode = 0`), `RadioProfile.VehicleToggleHotkey` (`FreelookHotkey`,
  default `VirtualKeyCode = 0`), `RadioProfile.VehicleExitDelaySeconds`
  (`float`, default `3.0f`), `RadioProfile.AutoMuteSource` (`bool`, default
  `true`) — all raising `PropertyChanged` via `SetField`.

- [ ] **Step 1: Write the failing test**

Open `tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs` and find
`Load_ProfileJsonMissingNewerFields_KeepsDefaults`. Add these four lines
right after the existing `Assert.False(loaded.FreelookAlwaysOn);` line:

```csharp
Assert.Equal(0, loaded.RecenterHotkey.VirtualKeyCode);      // default retained (unbound)
Assert.Equal(0, loaded.VehicleToggleHotkey.VirtualKeyCode); // default retained (unbound)
Assert.Equal(3.0f, loaded.VehicleExitDelaySeconds);         // default retained
Assert.True(loaded.AutoMuteSource);                         // default retained
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: FAIL — `RadioProfile` has no `RecenterHotkey` / `VehicleToggleHotkey`
/ `VehicleExitDelaySeconds` / `AutoMuteSource`.

- [ ] **Step 3: Add the fields**

In `RadioProfile.cs`, add to the private-field block (after `_freelookAlwaysOn`):

```csharp
private FreelookHotkey _recenterHotkey = new() { VirtualKeyCode = 0 };
private FreelookHotkey _vehicleToggleHotkey = new() { VirtualKeyCode = 0 };
private float _vehicleExitDelaySeconds = 3.0f;
private bool _autoMuteSource = true;
```

Add to the public-property block (after `FreelookAlwaysOn`):

```csharp
/// Global hotkey that fires Recenter without alt-tabbing to the window.
/// VirtualKeyCode 0 means unbound (never fires).
public FreelookHotkey RecenterHotkey { get => _recenterHotkey; set => SetField(ref _recenterHotkey, value); }

/// Global hotkey that toggles the simulated vehicle in/out state (mutes or
/// unmutes the processed output independent of Volume). VirtualKeyCode 0
/// means unbound (never fires).
public FreelookHotkey VehicleToggleHotkey { get => _vehicleToggleHotkey; set => SetField(ref _vehicleToggleHotkey, value); }

/// Seconds to wait after toggling "out of vehicle" before the output
/// actually mutes, matching games with a multi-second exit-vehicle
/// animation. 0 = mute (almost) immediately. Entering back "in" is always
/// immediate, no delay.
public float VehicleExitDelaySeconds { get => _vehicleExitDelaySeconds; set => SetField(ref _vehicleExitDelaySeconds, value); }

/// When true, MainViewModel mutes the source process's own Windows audio
/// session on Start and unmutes it on Stop, so the raw source is never
/// audible alongside the processed radio without a manual mixer step.
public bool AutoMuteSource { get => _autoMuteSource; set => SetField(ref _autoMuteSource, value); }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SpotifyGameRadio.Core/Config/RadioProfile.cs tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs
git commit -m "feat: add RecenterHotkey, VehicleToggleHotkey, VehicleExitDelaySeconds, AutoMuteSource to RadioProfile"
```

---

### Task 2: TapHotkeyWatcher (global tap-hotkey polling)

**Files:**
- Create: `src/SpotifyGameRadio.Core/Tracking/Win32KeyStateSource.cs`
- Create: `src/SpotifyGameRadio.Core/Tracking/TapHotkeyWatcher.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Tracking/TapHotkeyWatcherTests.cs`

**Interfaces:**
- Consumes: `RadioProfile.RecenterHotkey` / `VehicleToggleHotkey` (Task 1,
  via `FreelookHotkey`).
- Produces: `IKeyStateSource` (interface, `bool IsKeyDown(int virtualKeyCode)`),
  `Win32KeyStateSource` (production impl, `GetAsyncKeyState`), `TapHotkeyWatcher`
  — `event Action? Pressed`, `void SetHotkey(FreelookHotkey)`, `void Poll()`
  (public so tests can drive edge-detection deterministically), `void Dispose()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SpotifyGameRadio.Core.Tests/Tracking/TapHotkeyWatcherTests.cs`:

```csharp
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Tracking;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Tracking;

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TapHotkeyWatcherTests"`
Expected: FAIL — `TapHotkeyWatcher`/`IKeyStateSource` do not exist.

- [ ] **Step 3: Implement `IKeyStateSource` + `Win32KeyStateSource`**

Create `src/SpotifyGameRadio.Core/Tracking/Win32KeyStateSource.cs`:

```csharp
using System.Runtime.InteropServices;

namespace SpotifyGameRadio.Core.Tracking;

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
```

- [ ] **Step 4: Implement `TapHotkeyWatcher`**

Create `src/SpotifyGameRadio.Core/Tracking/TapHotkeyWatcher.cs`:

```csharp
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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TapHotkeyWatcherTests"`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/SpotifyGameRadio.Core/Tracking/Win32KeyStateSource.cs src/SpotifyGameRadio.Core/Tracking/TapHotkeyWatcher.cs tests/SpotifyGameRadio.Core.Tests/Tracking/TapHotkeyWatcherTests.cs
git commit -m "feat: add TapHotkeyWatcher for edge-triggered global hotkeys"
```

---

### Task 3: RadioPipeline vehicle-mute stage

**Files:**
- Modify: `src/SpotifyGameRadio.Core/Pipeline/RadioPipeline.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Pipeline/RadioPipelineTests.cs`

**Interfaces:**
- Produces: `RadioPipeline.SetVehicleMuted(bool muted)` — sets an internal,
  non-persisted `_vehicleGain` (0 or 1) that multiplies with `_volume` in the
  output stage. Never touches `RadioProfile`.

- [ ] **Step 1: Write the failing tests**

In `tests/SpotifyGameRadio.Core.Tests/Pipeline/RadioPipelineTests.cs`, add
after `StereoWidthZero_CollapsesPannedOutputToEqualChannels`:

```csharp
[Fact]
public void SetVehicleMuted_True_SilencesOutputRegardlessOfVolume()
{
    var capture = new FakeCaptureService();
    var output = new FakeOutputService();
    var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);
    pipeline.Start();

    pipeline.SetVehicleMuted(true);
    capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });

    Assert.All(output.WrittenBuffers[0], v => Assert.Equal(0f, v, precision: 5));
}

[Fact]
public void SetVehicleMuted_ComposesWithVolume_NotReplacesIt()
{
    var capture = new FakeCaptureService();
    var output = new FakeOutputService();
    var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);
    pipeline.Start();
    var profile = new RadioProfile { WetDryMix = 0f, Volume = 0.5f };
    pipeline.ApplyProfile(profile);

    pipeline.SetVehicleMuted(true);
    pipeline.SetVehicleMuted(false); // back "in vehicle"
    capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });

    // Volume (0.5) must still apply after a mute/unmute cycle — vehicle
    // gain must multiply with _volume, not overwrite it.
    Assert.All(output.WrittenBuffers[0], v => Assert.Equal(0.5f, v, precision: 5));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RadioPipelineTests"`
Expected: FAIL — `SetVehicleMuted` not defined.

- [ ] **Step 3: Implement**

In `RadioPipeline.cs`, add a field next to `_stereoWidth`:

```csharp
private float _volume = 1f;
private float _stereoWidth = 1f;
private float _vehicleGain = 1f; // not part of RadioProfile; runtime-only, toggled by the vehicle in/out feature.
```

Add a method next to `RecenterListener()`:

```csharp
/// Mutes/unmutes the processed output independent of Profile.Volume — used
/// by the vehicle in/out toggle. Never persisted; a new RadioPipeline
/// instance always starts unmuted (1f). Safe to call from the UI thread.
public void SetVehicleMuted(bool muted) => _vehicleGain = muted ? 0f : 1f;
```

In `OnDataAvailable`, replace:

```csharp
                StereoWidth.Apply(_stereoBlock, _frameSize, _stereoWidth);
                if (_volume != 1f)
                    for (int s = 0; s < _frameSize * 2; s++)
                        _stereoBlock[s] *= _volume;
```

with:

```csharp
                StereoWidth.Apply(_stereoBlock, _frameSize, _stereoWidth);
                float gain = _volume * _vehicleGain;
                if (gain != 1f)
                    for (int s = 0; s < _frameSize * 2; s++)
                        _stereoBlock[s] *= gain;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RadioPipelineTests"`
Expected: PASS (all, including the 2 new).

- [ ] **Step 5: Commit**

```bash
git add src/SpotifyGameRadio.Core/Pipeline/RadioPipeline.cs tests/SpotifyGameRadio.Core.Tests/Pipeline/RadioPipelineTests.cs
git commit -m "feat: add RadioPipeline.SetVehicleMuted, independent of Volume"
```

---

### Task 4: Source session auto-mute (NAudio)

**Files:**
- Create: `src/SpotifyGameRadio.Core/Audio/SourceSessionMuter.cs`

**Interfaces:**
- Produces: `ISourceSessionMuter` (`void Mute(string processName)`,
  `void Unmute(string processName)`), `NAudioSourceSessionMuter` (impl).

No unit test — real WASAPI/NAudio session interop, same untested-interop
precedent as `WindowsAppAudioRouter` and `AudioSessionEnumerator`. Verified
in Task 7's manual pass.

- [ ] **Step 1: Implement**

Create `src/SpotifyGameRadio.Core/Audio/SourceSessionMuter.cs`:

```csharp
using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace SpotifyGameRadio.Core.Audio;

public interface ISourceSessionMuter
{
    /// Mutes every Windows audio session belonging to a process named
    /// processName. Best-effort: any failure (COM, a process exiting
    /// mid-scan, no default render device) is swallowed, never thrown —
    /// matches WindowsAppAudioRouter.TrySetRole's precedent.
    void Mute(string processName);

    /// Unmutes every session belonging to processName. Harmless no-op if
    /// nothing was muted (e.g. AutoMuteSource was off, or the process
    /// wasn't running). Best-effort, same as Mute.
    void Unmute(string processName);
}

/// Mutes/unmutes a process's own Windows audio session directly via
/// NAudio's AudioSessionControl.SimpleAudioVolume, instead of rerouting its
/// output device like WindowsAppAudioRouter — works on Windows 10 and 11,
/// no virtual cable required.
public sealed class NAudioSourceSessionMuter : ISourceSessionMuter
{
    public void Mute(string processName) => SetMuted(processName, true);
    public void Unmute(string processName) => SetMuted(processName, false);

    private static void SetMuted(string processName, bool muted)
    {
        if (string.IsNullOrEmpty(processName)) return;

        try
        {
            var pids = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
            if (pids.Count == 0) return;

            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var sessions = defaultDevice.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (!pids.Contains((int)session.GetProcessID)) continue;
                try { session.SimpleAudioVolume.Mute = muted; }
                catch (Exception) { /* best effort: session may have just ended */ }
            }
        }
        catch (Exception) { /* best effort: no default render device, COM failure, etc. */ }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj`
Expected: succeeds, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/SpotifyGameRadio.Core/Audio/SourceSessionMuter.cs
git commit -m "feat: add NAudioSourceSessionMuter for automatic source-session muting"
```

---

### Task 5: MainViewModel wiring

**Files:**
- Modify: `src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `RadioProfile.RecenterHotkey` / `VehicleToggleHotkey` /
  `VehicleExitDelaySeconds` / `AutoMuteSource` (Task 1); `TapHotkeyWatcher`,
  `Win32KeyStateSource` (Task 2); `RadioPipeline.SetVehicleMuted` (Task 3);
  `ISourceSessionMuter`, `NAudioSourceSessionMuter` (Task 4).
- Produces: `MainViewModel` mutes/unmutes the source on Start/Stop, fires
  Recenter and the vehicle toggle from global hotkeys, and runs the vehicle
  exit-delay state machine.

No unit test — `MainViewModel` constructs real Win32/WASAPI objects and
uses `Application.Current.Dispatcher`, same as the rest of the class.
Verified in Task 7's manual pass.

- [ ] **Step 1: Add new fields**

In `MainViewModel.cs`, add to the field block (after `_testTone`):

```csharp
private readonly ISourceSessionMuter _sessionMuter = new NAudioSourceSessionMuter();
private TapHotkeyWatcher? _recenterHotkeyWatcher;
private TapHotkeyWatcher? _vehicleToggleHotkeyWatcher;
private bool _isInVehicle = true;
private System.Windows.Threading.DispatcherTimer? _vehicleExitTimer;
```

- [ ] **Step 2: Add `AutoMuteSource` to the restart-required set**

In the `RestartRequiredProfileProperties` initializer, add a line:

```csharp
private static readonly HashSet<string> RestartRequiredProfileProperties = new()
{
    nameof(RadioProfile.SourceProcessName),
    nameof(RadioProfile.AutoRouteSource), nameof(RadioProfile.RouteSourceToDeviceId),
    nameof(RadioProfile.AutoMuteSource),
};
```

- [ ] **Step 3: Rebind the two new hotkeys live**

In `OnProfilePropertyChanged`, add two branches right after the existing
`else if (e.PropertyName == nameof(RadioProfile.Hotkey))` block:

```csharp
        else if (e.PropertyName == nameof(RadioProfile.RecenterHotkey))
        {
            _recenterHotkeyWatcher?.SetHotkey(Profile.RecenterHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.VehicleToggleHotkey))
        {
            _vehicleToggleHotkeyWatcher?.SetHotkey(Profile.VehicleToggleHotkey);
        }
```

- [ ] **Step 4: Rebind on profile load**

In `LoadProfile`, add two lines right after `_mouseHook?.SetHotkey(Profile.Hotkey);`:

```csharp
                _recenterHotkeyWatcher?.SetHotkey(Profile.RecenterHotkey);
                _vehicleToggleHotkeyWatcher?.SetHotkey(Profile.VehicleToggleHotkey);
```

- [ ] **Step 5: Add the vehicle-toggle state machine**

Add a new private method, e.g. right before `private void Start()`:

```csharp
    private void OnVehicleTogglePressed()
    {
        _isInVehicle = !_isInVehicle;
        _vehicleExitTimer?.Stop();
        _vehicleExitTimer = null;

        if (_isInVehicle)
        {
            _pipeline?.SetVehicleMuted(false);
            StatusMessage = "In vehicle";
        }
        else
        {
            StatusMessage = $"Exiting vehicle — muting in {Profile.VehicleExitDelaySeconds:0.#}s";
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(Math.Max(0, Profile.VehicleExitDelaySeconds))
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _pipeline?.SetVehicleMuted(true);
                StatusMessage = "Out of vehicle";
            };
            _vehicleExitTimer = timer;
            timer.Start();
        }
    }
```

- [ ] **Step 6: Create and wire the hotkey watchers in `Start()`**

In `Start()`, change the local-variable declarations above the `try` block
from:

```csharp
        Win32MouseHook? mouseHook = null;
        RadioPipeline? pipeline = null;
```

to:

```csharp
        Win32MouseHook? mouseHook = null;
        RadioPipeline? pipeline = null;
        TapHotkeyWatcher? recenterHotkeyWatcher = null;
        TapHotkeyWatcher? vehicleToggleHotkeyWatcher = null;
```

Inside the `try` block, right after `mouseHook = new Win32MouseHook(Profile.Hotkey);`,
add:

```csharp
            recenterHotkeyWatcher = new TapHotkeyWatcher(Profile.RecenterHotkey, new Win32KeyStateSource());
            recenterHotkeyWatcher.Pressed += () => Application.Current.Dispatcher.Invoke(() =>
            {
                _pipeline?.RecenterListener();
                ListenerYaw = 0;
                ListenerPitch = 0;
            });

            vehicleToggleHotkeyWatcher = new TapHotkeyWatcher(Profile.VehicleToggleHotkey, new Win32KeyStateSource());
            vehicleToggleHotkeyWatcher.Pressed += () => Application.Current.Dispatcher.Invoke(OnVehicleTogglePressed);
```

- [ ] **Step 7: Mute the source before starting capture**

Still in `Start()`'s `try` block, right before `pipeline.Start();`, add:

```csharp
            _isInVehicle = true;
            if (Profile.AutoMuteSource) _sessionMuter.Mute(Profile.SourceProcessName);
```

- [ ] **Step 8: Commit the watchers to instance fields on success**

Right after `_mouseHook = mouseHook;`, add:

```csharp
            _recenterHotkeyWatcher = recenterHotkeyWatcher;
            _vehicleToggleHotkeyWatcher = vehicleToggleHotkeyWatcher;
```

- [ ] **Step 9: Clean up on a failed Start()**

In the `catch (Exception ex)` block of `Start()`, change:

```csharp
        catch (Exception ex)
        {
            pipeline?.Stop();
            pipeline?.Dispose();
            mouseHook?.Dispose();
            _pipeline = null;
            _mouseHook = null;
            if (_activeRouteRecovery is not null) RestoreSourceRouting();
            StatusMessage = $"Failed to start: {ex.Message}";
        }
```

to:

```csharp
        catch (Exception ex)
        {
            pipeline?.Stop();
            pipeline?.Dispose();
            mouseHook?.Dispose();
            recenterHotkeyWatcher?.Dispose();
            vehicleToggleHotkeyWatcher?.Dispose();
            _pipeline = null;
            _mouseHook = null;
            _recenterHotkeyWatcher = null;
            _vehicleToggleHotkeyWatcher = null;
            _sessionMuter.Unmute(Profile.SourceProcessName);
            if (_activeRouteRecovery is not null) RestoreSourceRouting();
            StatusMessage = $"Failed to start: {ex.Message}";
        }
```

- [ ] **Step 10: Clean up in `Stop()`**

In `Stop()`, right after `_uiTimer?.Stop(); _uiTimer = null;`, add:

```csharp
        _vehicleExitTimer?.Stop();
        _vehicleExitTimer = null;
        _recenterHotkeyWatcher?.Dispose();
        _vehicleToggleHotkeyWatcher?.Dispose();
        _recenterHotkeyWatcher = null;
        _vehicleToggleHotkeyWatcher = null;
        _sessionMuter.Unmute(Profile.SourceProcessName);
```

- [ ] **Step 11: Build**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: succeeds, 0 warnings. (Close the running app first if the exe is locked.)

- [ ] **Step 12: Run the full test suite**

Run: `dotnet test`
Expected: all green (no MainViewModel tests, but confirm nothing else regressed).

- [ ] **Step 13: Commit**

```bash
git add src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs
git commit -m "feat: wire Recenter/vehicle-toggle hotkeys and source auto-mute in MainViewModel"
```

---

### Task 6: MainWindow.xaml — new controls

**Files:**
- Modify: `src/SpotifyGameRadio.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `Profile.RecenterHotkey` / `VehicleToggleHotkey` /
  `VehicleExitDelaySeconds` / `AutoMuteSource` (Task 1).

No automated test (declarative XAML) — verified in Task 7.

- [ ] **Step 1: Add four `RowDefinition`s**

The current `Grid.RowDefinitions` has rows 0–18, with row 11 the freelook
hotkey row. Insert four `<RowDefinition Height="Auto" />` rows right after
row 11, so what was row 12 (mouse sensitivity) becomes row 16, and every
later row shifts by +4. Update the XML comments accordingly:

```xml
            <RowDefinition Height="Auto" /> <!-- 11 freelook hotkey -->
            <RowDefinition Height="Auto" /> <!-- 12 recenter hotkey -->
            <RowDefinition Height="Auto" /> <!-- 13 vehicle toggle hotkey -->
            <RowDefinition Height="Auto" /> <!-- 14 vehicle exit delay -->
            <RowDefinition Height="Auto" /> <!-- 15 auto-mute source -->
            <RowDefinition Height="Auto" /> <!-- 16 mouse sensitivity -->
            <RowDefinition Height="Auto" /> <!-- 17 max yaw -->
            <RowDefinition Height="Auto" /> <!-- 18 max pitch -->
            <RowDefinition Height="Auto" /> <!-- 19 spring-back -->
            <RowDefinition Height="Auto" /> <!-- 20 buttons + restart hint -->
            <RowDefinition Height="Auto" /> <!-- 21 canvas + height -->
            <RowDefinition Height="*" />    <!-- 22 status -->
```

- [ ] **Step 2: Add the four new rows**

After the freelook-hotkey `StackPanel` (`Grid.Row="11"`), add:

```xml
        <StackPanel Grid.Row="12" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Recenter hotkey:" VerticalAlignment="Center" Width="140" />
            <controls:HotkeyCaptureControl Hotkey="{Binding Profile.RecenterHotkey, Mode=TwoWay}" />
            <TextBlock Text=" fires Recenter without alt-tabbing" VerticalAlignment="Center" Margin="8,0,0,0" Foreground="Gray" />
        </StackPanel>

        <StackPanel Grid.Row="13" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Vehicle toggle hotkey:" VerticalAlignment="Center" Width="140" />
            <controls:HotkeyCaptureControl Hotkey="{Binding Profile.VehicleToggleHotkey, Mode=TwoWay}" />
            <TextBlock Text=" simulates getting in/out of a vehicle" VerticalAlignment="Center" Margin="8,0,0,0" Foreground="Gray" />
        </StackPanel>

        <StackPanel Grid.Row="14" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Vehicle exit delay (s):" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="0" Maximum="15" Width="200" Value="{Binding Profile.VehicleExitDelaySeconds}"
                    ToolTip="Delay before the radio mutes after toggling 'out of vehicle', for games with an exit animation (e.g. Squad)." />
        </StackPanel>

        <StackPanel Grid.Row="15" Orientation="Horizontal" Margin="0,0,0,8">
            <CheckBox Content="Auto-mute source audio on Start" IsChecked="{Binding Profile.AutoMuteSource}"
                      VerticalAlignment="Center"
                      ToolTip="Mutes the source app's own Windows audio session while the radio is running, so you don't have to mute it manually in the mixer." />
        </StackPanel>
```

- [ ] **Step 3: Renumber the shifted rows**

Bump `Grid.Row` on every `StackPanel` from the old mouse-sensitivity row
onward by +4: mouse sensitivity `12→16`, max yaw `13→17`, max pitch
`14→18`, spring-back `15→19`, buttons+restart hint `16→20`, canvas
`17→21`, status `18→22`.

- [ ] **Step 4: Build and eyeball**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: succeeds. The window is currently `Height="800"`; if the four
extra rows push content past the window bottom, bump it to `950` and
rebuild.

- [ ] **Step 5: Commit**

```bash
git add src/SpotifyGameRadio.App/MainWindow.xaml
git commit -m "feat: add Recenter/vehicle-toggle hotkey controls, exit delay, and auto-mute checkbox to the window"
```

---

### Task 7: Build, launch, manual verification

**Files:** none (verification only).

- [ ] **Step 1: Full build + test**

Run: `dotnet build` then `dotnet test`
Expected: build 0 warnings; all tests green.

- [ ] **Step 2: Launch**

Close any running instance, then launch `src/SpotifyGameRadio.App/bin/Debug/net8.0-windows/SpotifyGameRadio.App.exe`.

- [ ] **Step 3: Hand off the manual checklist**

Report to the human for checks (see the spec's "Manual" list):
- Recenter hotkey fires Recenter while the game window has focus, without
  alt-tabbing.
- Vehicle toggle: pressing it while "in" starts the exit countdown (status
  message shows), radio mutes after the configured delay, not before.
  Toggling back "in" mid-countdown cancels the pending mute and unmutes
  immediately.
- Volume slider setting is unaffected by vehicle mute/unmute — confirm it
  isn't stomped to 0, and Save Profile doesn't persist a 0.
- With Auto-mute source on: the source app's own audio is silent as soon
  as Start is clicked, with no manual mixer step. Stop restores it.
- Unbound hotkeys (the default) never fire.
- Layout: all new rows visible, nothing clipped.

---

## Self-Review

**Spec coverage:**
- `RecenterHotkey`, `VehicleToggleHotkey`, `VehicleExitDelaySeconds`,
  `AutoMuteSource` profile fields + legacy load → Task 1. ✓
- Global tap-hotkey polling (edge-triggered, unbound-by-default, live
  rebind) → Task 2. ✓
- Independent, non-persisted vehicle mute gain, composes with Volume → Task 3. ✓
- NAudio session mute/unmute, best-effort, multi-pid → Task 4. ✓
- MainViewModel: hotkey watcher lifecycle (create/dispose on Start/Stop and
  on a failed Start), live rebind on profile change and profile load,
  vehicle exit-delay state machine, session mute on Start / unmute on Stop
  and on failure, `AutoMuteSource` in the restart-required set → Task 5. ✓
- XAML: two hotkey capture rows, delay slider, auto-mute checkbox, row
  renumber, window-height fallback → Task 6. ✓
- Automated + manual test lists → Tasks 1–3 (automated), Task 7 (manual). ✓
- Constraint "vehicle mute never touches Profile.Volume" → Task 3's
  `_vehicleGain` is a plain pipeline field, never read from or written to
  `RadioProfile`; Task 5's `OnVehicleTogglePressed` never touches
  `Profile.Volume` either. ✓
- Constraint "exit delay only on toggle-off" → `OnVehicleTogglePressed`'s
  `if (_isInVehicle)` branch unmutes immediately; only the `else` branch
  starts a timer. ✓

**Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N".
Every code step has real code, including the untested `Task 4`
(`NAudioSourceSessionMuter`) and `Task 5` (`MainViewModel`) — their steps
are "implement" + "build", consistent with how the prior QOL-controls plan
treated its own untested `MainViewModel` task.

**Type consistency:**
- `TapHotkeyWatcher(FreelookHotkey, IKeyStateSource)` and
  `TapHotkeyWatcher(FreelookHotkey, IKeyStateSource, bool)` — defined Task 2,
  the two-arg production overload used identically in Task 5 Step 6
  (`new TapHotkeyWatcher(Profile.RecenterHotkey, new Win32KeyStateSource())`),
  the three-arg overload used identically in Task 2's own tests. ✓
- `TapHotkeyWatcher.Pressed` (`event Action?`), `.SetHotkey(FreelookHotkey)`,
  `.Poll()`, `.Dispose()` — defined Task 2, consumed Task 5 (`Pressed +=`,
  `SetHotkey` in Steps 3/4, `Dispose()` in Steps 9/10) with matching
  signatures. ✓
- `RadioPipeline.SetVehicleMuted(bool)` — defined Task 3, called identically
  from `MainViewModel.OnVehicleTogglePressed` (Task 5 Step 5). ✓
- `ISourceSessionMuter.Mute(string)` / `.Unmute(string)` — defined Task 4,
  called identically as `_sessionMuter.Mute(Profile.SourceProcessName)` /
  `.Unmute(...)` in Task 5 Steps 7, 9, 10. ✓
- `RadioProfile.RecenterHotkey` / `VehicleToggleHotkey` (`FreelookHotkey`) /
  `VehicleExitDelaySeconds` (`float`) / `AutoMuteSource` (`bool`) — Task 1,
  consumed identically in Tasks 5 and 6. ✓

No gaps found.
