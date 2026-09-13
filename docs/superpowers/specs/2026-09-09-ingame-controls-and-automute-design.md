# In-game calibration, vehicle toggle, and source auto-mute — design

Date: 2026-09-09
Status: approved for planning

## Goal

Fix three issues found in manual testing of the app:

1. **Recenter requires alt-tab.** The Recenter button only lives in the app
   window, breaking calibration flow mid-game. Add a configurable global
   hotkey that fires Recenter without leaving the game.
2. **No way to simulate getting in/out of a vehicle.** Add a configurable
   global hotkey that toggles the radio's audibility, with a configurable
   delay on exit (some games, e.g. Squad, have a multi-second exit-vehicle
   animation before the character is actually "out").
3. **Native source audio isn't muted automatically.** Today the user must
   manually mute the source app (e.g. Spotify) in the Windows volume mixer
   before first use, or set up the separate Auto-route (virtual cable)
   feature. Add automatic session muting on Start so this is no longer a
   manual step by default.

## Non-goals

- No change to the existing `AutoRouteSource` (virtual-cable) feature — it
  stays available as an alternative for users who want a full device
  reroute. The new session-mute is independent and on by default.
- No crash-recovery persistence for the new session mute (unlike
  `RouteRecoveryStore` for routing) — see Risks.
- No continuous re-scan for source sessions that appear after Start (matches
  the existing routing feature's same limitation) — see Risks.
- Not renaming `FreelookHotkey` even though it's reused for two unrelated
  hotkeys now; a rename is unrelated churn.
- No audio fade/ramp on the vehicle mute transition — it snaps at a block
  boundary, same as the existing Volume slider.

## Profile changes (`RadioProfile`)

New fields, all raising `INotifyPropertyChanged` via the existing `SetField`:

| Field | Type | Default | Notes |
|---|---|---|---|
| `RecenterHotkey` | `FreelookHotkey` | `{ VirtualKeyCode = 0 }` | unbound; user must set it |
| `VehicleToggleHotkey` | `FreelookHotkey` | `{ VirtualKeyCode = 0 }` | unbound; user must set it |
| `VehicleExitDelaySeconds` | `float` | `3.0f` | 0 = instant mute on toggle-off |
| `AutoMuteSource` | `bool` | `true` | restart-required, like `AutoRouteSource` |

`ConfigStore` load of a pre-existing profile JSON without these keys must
fall back to the defaults above (same tolerance pattern as prior additions —
verify with a test).

## Global tap-hotkeys (`Core/Tracking/TapHotkeyWatcher.cs`, new)

`Win32MouseHook`'s existing hotkey poll thread treats the freelook key as a
**hold**: `IsHotkeyHeld` is read every block/frame, true for the whole press.
Recenter and the vehicle toggle need **tap** semantics: fire once per press,
not once per poll tick while held.

```csharp
namespace LogiBuddy.Core.Tracking;

/// Polls a single rebindable key at the same ~8ms cadence as Win32MouseHook's
/// hotkey poll, and raises Pressed once per press (rising edge only) — never
/// repeatedly while held. A VirtualKeyCode of 0 means "unbound": never fires.
public class TapHotkeyWatcher : IDisposable
{
    public event Action? Pressed;
    public void SetHotkey(FreelookHotkey hotkey);
    // internal: Thread polling GetAsyncKeyState(vk), tracks previous-down
    // state, fires Pressed on false->true transition. vk == 0 => no-op poll.
}
```

The edge-detection (previous-state → new-state → fire?) is factored into a
small pure function/class so it can be unit tested against a fake key-state
sequence, instead of `GetAsyncKeyState` directly — the Win32 polling wrapper
around it is untested interop, consistent with `Win32MouseHook` today.

`MainViewModel.Start()` creates two instances (alongside the existing
`Win32MouseHook`), wires `SetHotkey` the same way `Profile.Hotkey` changes
already reach `_mouseHook.SetHotkey` in `OnProfilePropertyChanged`, and
disposes both in `Stop()`.

- Recenter watcher → `pipeline.RecenterListener()` (existing, from the prior
  QOL-controls feature) marshaled onto the UI thread via
  `Application.Current.Dispatcher.Invoke`, same as the `pipeline.Warning`
  handler already does.
- Vehicle-toggle watcher → the vehicle state machine below.

## Vehicle in/out toggle

**Must not reuse `Profile.Volume`** — that value is user-set and persisted
on Save Profile; driving it to 0 for "out of vehicle" risks saving a silent
profile. Instead `RadioPipeline` gets an independent, non-persisted gain
stage.

### `RadioPipeline`

```csharp
private float _vehicleGain = 1f; // not part of RadioProfile; runtime-only

public void SetVehicleMuted(bool muted) => _vehicleGain = muted ? 0f : 1f;
```

Applied in `OnDataAvailable` alongside the existing `_volume` multiply
(combine: `stereoBlock[s] *= _volume * _vehicleGain`, or two passes —
implementation detail for the plan). Same atomic-float, block-boundary
semantics as `_volume`/`_stereoWidth` already have.

### `MainViewModel` state machine

```csharp
private bool _isInVehicle = true;
private DispatcherTimer? _vehicleExitTimer;

private void OnVehicleTogglePressed()
{
    _isInVehicle = !_isInVehicle;
    _vehicleExitTimer?.Stop();
    _vehicleExitTimer = null;

    if (_isInVehicle)
    {
        pipeline?.SetVehicleMuted(false);
        StatusMessage = "In vehicle";
    }
    else
    {
        StatusMessage = $"Exiting vehicle — muting in {Profile.VehicleExitDelaySeconds:0.#}s";
        _vehicleExitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Profile.VehicleExitDelaySeconds) };
        _vehicleExitTimer.Tick += (_, _) =>
        {
            _vehicleExitTimer!.Stop();
            pipeline?.SetVehicleMuted(true);
            StatusMessage = "Out of vehicle";
        };
        _vehicleExitTimer.Start();
    }
}
```

- Starts "in vehicle" (unmuted) every time Start is clicked — no persisted
  vehicle state across runs.
- `Stop()` cancels any pending `_vehicleExitTimer` so it can't fire into a
  torn-down pipeline.
- `VehicleExitDelaySeconds = 0` means the timer fires on (almost) the next
  tick — effectively instant, no special-casing needed.

## Source session auto-mute (`Core/Audio/ISourceSessionMuter.cs`, new)

NAudio's `AudioSessionControl` already exposes `SimpleAudioVolume.Mute` —
no new native interop needed (unlike the undocumented API
`WindowsAppAudioRouter` uses for routing).

```csharp
public interface ISourceSessionMuter
{
    void Mute(string processName);
    void Unmute(string processName);
}

public sealed class NAudioSourceSessionMuter : ISourceSessionMuter
{
    // For each pid in Process.GetProcessesByName(processName) (same
    // multi-process pattern as WindowsAppAudioRouter/TryRouteSource),
    // find matching sessions on the default render device
    // (MMDeviceEnumerator, same device AudioSessionEnumerator already
    // queries) and set session.SimpleAudioVolume.Mute = true/false.
    // Best-effort per pid/session: catch and swallow COM/process
    // exceptions, mirroring WindowsAppAudioRouter.TrySetRole.
}
```

`MainViewModel.Start()` calls `_sessionMuter.Mute(Profile.SourceProcessName)`
right after the pipeline is built, only if `Profile.AutoMuteSource`. `Stop()`
(and the existing crash-cleanup path) calls `Unmute(...)` unconditionally —
harmless no-op if nothing was muted.

`AutoMuteSource` goes in `RestartRequiredProfileProperties`, same treatment
as `AutoRouteSource` — toggling it while running has no effect until the
next Start.

## MainWindow.xaml

New rows (exact `Grid.Row` placement/renumbering is a plan detail):

- Two more `HotkeyCaptureControl`s: "Recenter hotkey" and "Vehicle toggle
  hotkey", each next to a label, following the existing freelook-hotkey row
  pattern.
- A `Slider` or numeric input for `VehicleExitDelaySeconds` (0–15s range).
- A `CheckBox` "Auto-mute source on Start" bound to `Profile.AutoMuteSource`.
- The existing `StatusMessage` `TextBlock` picks up the new vehicle-state
  transition text — no new status control needed.

## Testing

### Automated (TDD, `LogiBuddy.Core.Tests`)

- `TapHotkeyWatcher` edge-detection: fires once on a press, does not
  re-fire while held, fires again after a release-then-press, does not fire
  when unbound (`VirtualKeyCode == 0`).
- `RadioPipelineTests`: `SetVehicleMuted(true)` silences output regardless
  of `Profile.Volume`; `SetVehicleMuted(false)` restores it; independent of
  the `Volume`/`StereoWidth` stages already tested.
- `ConfigStoreTests`: a profile JSON missing the four new keys loads with
  the documented defaults.

### Not unit tested (matches existing precedent)

- `NAudioSourceSessionMuter` (real WASAPI interop, like
  `WindowsAppAudioRouter`/`AudioSessionEnumerator`).
- `TapHotkeyWatcher`'s `GetAsyncKeyState` polling wrapper (real Win32
  interop, like `Win32MouseHook`).
- `MainViewModel`'s vehicle state machine (like the rest of
  `MainViewModel`, verified manually).

### Manual (running app)

- Recenter hotkey fires Recenter while the game window has focus, without
  alt-tabbing.
- Vehicle toggle: pressing it while "in" starts the exit countdown (status
  message shows), radio mutes after the configured delay, not before.
  Toggling back "in" mid-countdown cancels the mute and unmutes immediately.
- Volume slider setting is unaffected by vehicle mute/unmute (confirm it
  doesn't get stomped to 0 and isn't saved as 0 on Save Profile).
- With `AutoMuteSource` on: the source app's own audio is silent as soon as
  Start is clicked, with no manual mixer step. Stop restores it.
- Unbound hotkeys (default state) never fire.

## Risks

- **Session mute is Start-time-only.** A source session that appears after
  Start (e.g., a player recreating its session on track change) won't be
  auto-muted until the next Start. `AutoRouteSource` has the identical
  limitation today, so this isn't a new class of bug, but it's worth calling
  out since auto-mute is now the default.
- **No crash-recovery for mute state.** If the app is killed while running,
  the source stays muted until the user unmutes it manually (Windows mixer)
  or relaunches SGR and clicks Stop... which won't happen since it's not
  running. This is a real gap but low-severity — unlike a stuck device
  route, a stuck mute is a one-click manual fix and doesn't silently
  misroute future audio.
- **Vehicle mute has no fade.** An abrupt full-scale gain change at a block
  boundary could be audible as a small click on loud material, same
  tradeoff the Volume slider already accepted. If this proves annoying in
  manual testing, a short linear ramp is a cheap follow-up, not building it
  preemptively.
- **Two more global-hotkey polling loops** (on top of the existing freelook
  hold-hotkey and the low-level mouse hook) add a small amount of constant
  CPU polling at ~120Hz each — negligible, but worth noting if a future
  hotkey is added and this pattern doesn't scale further without
  consolidating into one poll loop watching N keys.
