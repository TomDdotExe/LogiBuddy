# Override mute toggle — design

Date: 2026-09-14
Status: approved for planning

## Goal

Give the user a single "kill switch" that instantly silences the whole
radio and brings it back, independent of everything else already
controlling audibility (Start/Stop, the Vehicle in/out toggle). Exposed
both as a button in the main window and as a rebindable global hotkey,
called **Override**.

## Non-goals

- No delay/hold-to-confirm semantics like the Vehicle toggle's
  `VehicleExitDelaySeconds` — Override is a plain instant tap-to-toggle.
- No interaction with `SetVehicleMuted` beyond both being independent
  multiplicative gain stages — Override does not read or change
  `_isInVehicle`, and the Vehicle toggle does not read or change Override
  state. Whichever fires last for its own axis wins on that axis; the
  audible result is silent if *either* axis is muted.
- No persistence of the muted/unmuted state itself — same precedent as
  `_vehicleGain` (see `RadioPipeline.cs:60-63`), which the code already
  documents as "never persisted; a new RadioPipeline instance always
  starts unmuted."
- No fade/ramp on the transition — snaps at a block boundary, same
  tradeoff already accepted for `_volume` and `_vehicleGain`.
- Override does not work while the pipeline is stopped, matching every
  other hotkey/control gated on `_pipeline is not null`.

## Profile changes (`RadioProfile`)

One new field, following the exact existing pattern (e.g.
`VehicleToggleHotkey`):

| Field | Type | Default | Notes |
|---|---|---|---|
| `OverrideHotkey` | `FreelookHotkey` | `{ VirtualKeyCode = 0 }` | unbound; user must set it |

No delay/mode fields are needed (unlike the Vehicle toggle's
`VehicleExitDelaySeconds`/`HoldToExitVehicle`) since Override has no
timed transition.

`ConfigStore` load of a pre-existing profile JSON without this key must
fall back to the default above (same tolerance pattern as prior
additions — verify with a test).

## `RadioPipeline` changes

Add a second independent runtime-only gain stage, mirroring
`_vehicleGain` exactly:

```csharp
private float _overrideGain = 1f; // not part of RadioProfile; runtime-only, toggled by the override mute feature.

/// Mutes/unmutes the processed output independent of Profile.Volume and
/// the vehicle in/out toggle — used by the override mute button/hotkey.
/// Never persisted; a new RadioPipeline instance always starts unmuted.
/// Safe to call from the UI thread.
public void SetOverrideMuted(bool muted) => _overrideGain = muted ? 0f : 1f;
```

`RadioPipeline.cs:226` changes from:

```csharp
float gain = _volume * _vehicleGain;
```

to:

```csharp
float gain = _volume * _vehicleGain * _overrideGain;
```

Both mute sources are plain multipliers into the same gain float, so
"either one silences the output" falls out for free — no new branching
or state machine needed here.

## `MainViewModel` changes

Follows the existing tap-hotkey wiring pattern used by
`_vehicleToggleHotkeyWatcher` (see `MainViewModel.cs:1221`), but the
handler is a direct toggle with no timer:

```csharp
private bool _isOverrideMuted;
private TapHotkeyWatcher? _overrideHotkeyWatcher;

public bool IsOverrideMuted
{
    get => _isOverrideMuted;
    private set => SetField(ref _isOverrideMuted, value);
}

public ICommand OverrideToggleCommand { get; }
// OverrideToggleCommand = new RelayCommand(_ => ToggleOverride(), _ => _pipeline is not null);

private void ToggleOverride()
{
    IsOverrideMuted = !IsOverrideMuted;
    _pipeline?.SetOverrideMuted(IsOverrideMuted);
    StatusMessage = IsOverrideMuted ? "Radio muted (override)" : "Radio unmuted (override)";
}
```

- `Start()` constructs `_overrideHotkeyWatcher` through `_keyStateGate`
  (the same `SuspendableKeyStateSource` every other feature hotkey uses —
  see `MainViewModel.cs:1221` for the equivalent Vehicle-toggle line),
  wires `Pressed += () => ToggleOverride()`, and resets `IsOverrideMuted`
  to `false` (matching `_isInVehicle`'s reset to `true`/unmuted on every
  Start).
- `OnProfilePropertyChanged` gets an `OverrideHotkey` branch calling
  `_overrideHotkeyWatcher?.SetHotkey(Profile.OverrideHotkey)`, mirroring
  the existing branches for the other five hotkeys.
- `Stop()` disposes `_overrideHotkeyWatcher` and sets it to `null`,
  mirroring `_vehicleToggleHotkeyWatcher`'s teardown.
- The button and the hotkey call the same `ToggleOverride()` method, so
  either input source stays in sync with `IsOverrideMuted` automatically.

## `MainWindow.xaml` changes

- One new `HotkeyCaptureControl` bound to `Profile.OverrideHotkey`,
  labeled "Override hotkey", following the existing hotkey-row pattern.
- One new toggle-style `Button` bound to `OverrideToggleCommand`, labeled
  "Override". Its content/style reflects `IsOverrideMuted` (e.g. a
  `DataTrigger` swapping label/background between "Override" (idle) and
  "Muted" (active) states), placed near the Vehicle-toggle controls.
- No new status control — the existing `StatusMessage` `TextBlock`
  already picks up the new transition text.

## Testing

### Automated (TDD, `LogiBuddy.Core.Tests`)

- `RadioPipelineTests`: `SetOverrideMuted(true)` silences output
  regardless of `Profile.Volume` and regardless of `SetVehicleMuted`'s
  current state; `SetOverrideMuted(false)` restores it; confirm the two
  mute sources are independent (muting one and not the other still
  silences output; un-muting one while the other is still muted stays
  silent).
- `ConfigStoreTests`: a profile JSON missing the new `OverrideHotkey` key
  loads with the documented default.

### Not unit tested (matches existing precedent)

- `MainViewModel`'s `ToggleOverride()` wiring (like the rest of
  `MainViewModel`, verified manually) — `TapHotkeyWatcher`'s edge
  detection itself is already covered by existing tests and isn't
  re-tested per feature.

### Manual (running app)

- Pressing the Override hotkey while the radio is playing silences it
  immediately, no delay; pressing again restores it immediately.
- Clicking the Override button has the identical effect and stays in
  sync with the hotkey (toggling via one updates the other's visual
  state).
- Combining with the Vehicle toggle: muting via Override while "in
  vehicle" stays silent; toggling "out of vehicle" while Override is
  *not* muted still silences via the vehicle path; the two controls
  don't interfere with or reset each other.
- Override does nothing before Start / after Stop (button disabled,
  hotkey inert).
- Unbound `OverrideHotkey` (default state) never fires.
- Volume slider setting is unaffected by Override mute/unmute, same
  invariant already verified for the Vehicle toggle.

## Risks

- **Two independent mute sources could confuse a user who forgets one is
  active** (e.g. radio stays silent after un-muting Override because
  Vehicle is still "out"). Mitigated by distinct `StatusMessage` text per
  control; no combined "why is it silent" indicator is planned, since the
  Vehicle toggle already has this same property today and hasn't needed
  one.
- **One more global-hotkey polling loop** on top of the five that already
  exist — negligible additional CPU, same note already on record from
  the prior in-game-controls design doc.
