# Freelook calibration mode — design

Date: 2026-09-10
Status: Approved (brainstorming)

## Problem

The in-app freelook simulation integrates raw mouse-move counts into a yaw
angle (`YawDegrees += dx * MouseSensitivity`) and clamps at
`MaxYawDegrees`. In-game, the player's view is clamped by the game at some
angle the app doesn't know. The user currently sets `MouseSensitivity`
and `MaxYawDegrees` by eye. When they don't match the game:

- Past the game's view limit the game ignores further mouse movement, but
  the app keeps integrating it, so the audio image drifts further than
  the player actually turned ("phantom rotation").
- Small clamp mismatches compound at the extremes, so the radio's
  apparent direction and the on-screen view fall out of alignment.

Confirmed by the user against the packaged build 2026-09-10: HRTF works
well, but the alignment is off because of this clamp mismatch.

## Solution

A guided calibration mode. While the pipeline is running, the user holds
freelook and sweeps the mouse from one game view-limit to the other,
tapping a global hotkey at each limit. The app measures the horizontal
mouse-count sweep and derives `MouseSensitivity` so the app's existing
`±MaxYawDegrees` clamp lands exactly where the game's does. `MaxYawDegrees`
stays as the single knob the user tunes to taste — "how far the audio
image swings when you are at your game's look limit" (default 90°).

### Decisions locked in brainstorming

| Question | Decision |
| --- | --- |
| UX during calibration (game has focus) | Global hotkey to mark + short audio-cue beeps from the app; no overlay |
| Axes | Yaw only (pitch is not audible in the spatializer today) |
| Availability | Only while the pipeline is running (reuses the live mouse hook, tracker, output) |
| Mapping model | Derive `MouseSensitivity` from the measured sweep; keep `MaxYawDegrees` as the audio-swing knob (Approach A) |

### Measurement model (refined from brainstorming)

Two marks, no "center" reference — the extremes are well defined, a
neutral center under freelook is not:

1. `Start()` zeroes an accumulator and prompts: "Hold freelook, look
   fully **left** until the view stops, then tap Mark."
2. Mark #1 zeroes the accumulator and prompts: "Now look fully **right**
   until the view stops, then tap Mark."
3. Mark #2 takes `C = |accumulator|` — the full left-limit-to-right-limit
   sweep in mouse counts. The half-sweep `C / 2` is the count from centre
   to a limit.

Result carried out of the session: `MeasuredHalfSweepCounts = C / 2`.
The view model then sets, on `Profile`:

- `MouseSensitivity = MaxYawDegrees / MeasuredHalfSweepCounts`
- `MeasuredYawSweepCounts = MeasuredHalfSweepCounts`

Persisting the half-sweep lets `MouseSensitivity` re-derive whenever the
user later drags the `MaxYawDegrees` slider, instead of the two
desyncing.

Asymmetric per-side limits are **not** modelled — freelook is horizontally
symmetric in practice, and a single full-sweep measurement halved is more
robust than trying to find centre. (Considered and cut: a third "mark
centre" step for an asymmetry warning.)

**Guard:** if `C` is implausibly small (`< 50` counts — e.g. two marks
with no movement, or the hook delivered nothing), the session ends in
`Failed` with a message and writes nothing to `Profile`.

## Components

### `CalibrationSession` — `LogiBuddy.Core/Tracking/CalibrationSession.cs`

Pure, no WPF. Unit-tested with a fake `IMouseInputSource`.

```csharp
public enum CalibrationStep { Idle, AwaitLeftLimit, AwaitRightLimit, Completed, Failed, Aborted }

public sealed record CalibrationResult(float MeasuredHalfSweepCounts);

public sealed class CalibrationSession : IDisposable
{
    public CalibrationSession(IMouseInputSource input);

    public CalibrationStep Step { get; }

    /// Fires on every Step change with a one-line user-facing prompt for the new step.
    public event Action<CalibrationStep, string>? StepChanged;
    /// Fires once when Step becomes Completed.
    public event Action<CalibrationResult>? Completed;
    /// Fires once when Step becomes Failed or Aborted, with a reason.
    public event Action<string>? Ended;

    public void Start();     // Idle -> AwaitLeftLimit
    public void Mark();      // AwaitLeftLimit -> AwaitRightLimit -> Completed/Failed
    public void Abort();     // any active step -> Aborted
    public void Dispose();   // unsubscribes from input; safe to call twice
}
```

- Ctor subscribes to `input.MouseMoved`. The handler adds `dx` to a
  `long` accumulator via `Interlocked.Add` **only while**
  `input.IsHotkeyHeld` (the freelook key — the same gate `FreelookTracker`
  uses).
- `Mark()` and `Start()` are called from the tap-hotkey poll thread; the
  `MouseMoved` handler runs on the hook thread. State transitions happen
  under a private lock; `Interlocked.Read`/`Exchange` guard the
  accumulator; events are raised outside the lock.
- Idle-timeout: a `System.Timers.Timer` (60 s) reset on `Start()` and each
  `Mark()`. On elapse → `Failed("Calibration timed out.")`.
- After `Completed`/`Failed`/`Aborted` the session is inert; further
  `Mark()` calls are ignored.
- `Dispose()` unsubscribes from `input.MouseMoved` and stops the timer.

Step prompts (`StepChanged` payload):
- `AwaitLeftLimit`: "Hold freelook, look fully LEFT until the view stops, then tap Mark."
- `AwaitRightLimit`: "Now look fully RIGHT until the view stops, then tap Mark."
- `Completed`: "Calibration done — sensitivity updated."
- `Failed`: the reason string.
- `Aborted`: "Calibration cancelled."

### `CalibrationCuePlayer` — `LogiBuddy.App/Audio/CalibrationCuePlayer.cs`

Modelled on the existing `TestTonePlayer` (fading `SignalGenerator` sine
burst so there is no click). Plays to the profile's configured output
device: `WasapiOut` on the `MMDevice` for `Profile.OutputDeviceId` when
that is set and still present, else the default endpoint (`WaveOutEvent`,
exactly as `TestTonePlayer`). This matters because the user is wearing
headphones on a possibly-non-default device while calibrating. The device
is resolved once per cue call, so a mid-session device change is picked
up. Three cues, distinct so they are recognisable without looking:

- `Captured()` — one 880 Hz blip (~120 ms): a mark was recorded, next
  step is now prompted.
- `Done()` — three rising blips (660/880/1175 Hz): calibration completed
  and the profile was updated.
- `Failed()` — one low 300 Hz blip (~250 ms): timed out, cancelled, or
  the sweep was too small.

`IDisposable`; owns its output; each call is fire-and-forget and
overlap-safe (restart-on-call, like `TestTonePlayer`).

### `RadioProfile` — two new fields

```csharp
private FreelookHotkey _calibrateHotkey = new() { VirtualKeyCode = 0 };
private float _measuredYawSweepCounts = 0f;

/// Global tap hotkey used to mark a view limit during freelook calibration.
/// VirtualKeyCode 0 means unbound.
public FreelookHotkey CalibrateHotkey { get => _calibrateHotkey; set => SetField(ref _calibrateHotkey, value); }

/// Mouse counts from centre to a view limit, captured by calibration.
/// 0 means "never calibrated". When > 0, MouseSensitivity is re-derived
/// from it whenever MaxYawDegrees changes.
public float MeasuredYawSweepCounts { get => _measuredYawSweepCounts; set => SetField(ref _measuredYawSweepCounts, value); }
```

Both serialize automatically through `ConfigStore` (plain JSON of
`RadioProfile`). `CalibrateHotkey` follows the exact convention of
`RecenterHotkey`/`VehicleToggleHotkey` (unbound = code 0).

### `MainViewModel` wiring

New field: `private CalibrationSession? _calibrationSession;` and
`private TapHotkeyWatcher? _calibrateMarkWatcher;` and
`private readonly CalibrationCuePlayer _cuePlayer = new();`

New command:

```csharp
public ICommand CalibrateFreelookCommand { get; }
// = new RelayCommand(_ => StartOrAbortCalibration(),
//     _ => _pipeline is not null && _mouseHook is not null
//          && Profile.Hotkey.VirtualKeyCode != 0
//          && Profile.CalibrateHotkey.VirtualKeyCode != 0
//          && Profile.CalibrateHotkey.VirtualKeyCode != Profile.Hotkey.VirtualKeyCode);
```

`StartOrAbortCalibration()`:
- If a session is active → `_calibrationSession.Abort()` and return
  (button doubles as cancel).
- Else: `Recenter()`; create `_calibrationSession = new CalibrationSession(_mouseHook)`;
  create `_calibrateMarkWatcher = new TapHotkeyWatcher(Profile.CalibrateHotkey, new Win32KeyStateSource())`
  with `Pressed` → `_calibrationSession.Mark()`; subscribe to session
  events (marshalled to the UI thread with
  `Application.Current?.Dispatcher.Invoke`, matching the existing Recenter
  hotkey pattern):
  - `StepChanged` → `StatusMessage = prompt`; on `AwaitRightLimit` also
    `_cuePlayer.Captured()`.
  - `Completed(result)` →
    `Profile.MeasuredYawSweepCounts = result.MeasuredHalfSweepCounts;`
    `Profile.MouseSensitivity = Profile.MaxYawDegrees / result.MeasuredHalfSweepCounts;`
    `_cuePlayer.Done();` then tear down (below). Leaves the new values
    unsaved — the user clicks Save Profile as with every other setting.
  - `Ended(reason)` → `StatusMessage = reason`; `_cuePlayer.Failed();`
    tear down.
- Tear down = dispose `_calibrateMarkWatcher` and `_calibrationSession`,
  null both.

`OnProfilePropertyChanged` additions:
- `nameof(RadioProfile.CalibrateHotkey)` → `_calibrateMarkWatcher?.SetHotkey(Profile.CalibrateHotkey)`.
- `nameof(RadioProfile.MaxYawDegrees)` → if `Profile.MeasuredYawSweepCounts > 0`,
  set `Profile.MouseSensitivity = Profile.MaxYawDegrees / Profile.MeasuredYawSweepCounts`.
  (`MaxYawDegrees` stays in `LiveProfileProperties`, so the pipeline still
  gets `ApplyProfile` too; order doesn't matter — the setter fires its
  own change which re-applies.)

`Stop()` additions: if a calibration session is active, `Abort()` + tear
down before the hook is disposed. Dispose `_cuePlayer` where the other
disposables are handled.

`LoadProfile()`: if a session is active, abort + tear down (the hook and
profile are being swapped).

### Window — `MainWindow.xaml`

Near the existing Recenter / Vehicle hotkey rows:
- A `HotkeyCaptureControl` bound to `Profile.CalibrateHotkey`, labelled
  "Calibrate mark hotkey".
- A "Calibrate freelook" `Button` bound to `CalibrateFreelookCommand`.
  Its content does not need to change to "Cancel" — the status line
  carries the state — but may if trivial.
- Existing `StatusMessage` binding shows the step prompts; no new status
  surface.

Window height: the existing rows were sized exactly (commit `aef0bb7`
bumped height for four new rows). Adding one row will need a matching
height bump; verify nothing clips.

### `FreelookTracker`

Unchanged.

## Data flow

```
Win32MouseHook.MouseMoved(dx,dy) ──┬──> FreelookTracker (unchanged: yaw/pitch for audio)
                                   └──> CalibrationSession (sums dx while IsHotkeyHeld)

CalibrateHotkey tap ──> TapHotkeyWatcher.Pressed ──> CalibrationSession.Mark()
                                                          │
                        CalibrationSession.Completed ─────┼──> MainViewModel: write MouseSensitivity
                                                          │        + MeasuredYawSweepCounts to Profile
                                                          └──> CalibrationCuePlayer beeps
```

## Testing

### `CalibrationSessionTests` (new) — `tests/.../Tracking/CalibrationSessionTests.cs`

Fake `IMouseInputSource` with settable `IsHotkeyHeld` and a
`Raise(dx,dy)` helper. All deterministic, no threads (call `Mark()`
directly).

- `Start` moves `Idle → AwaitLeftLimit` and raises `StepChanged` with the
  left prompt.
- Movement while `IsHotkeyHeld == false` is ignored (accumulator stays 0).
- Full happy path: hold, raise `dx` totalling −4000, `Mark()`
  (→ `AwaitRightLimit`, accumulator re-zeroed), raise `dx` totalling
  +8000, `Mark()` → `Completed` with `MeasuredHalfSweepCounts == 4000`.
- Sweep-too-small: totals of 10 then 20 → `Failed`, `Ended` fired with a
  reason, no `Completed`.
- `Abort()` from each active step → `Aborted`, `Ended` fired; later
  `Mark()` is a no-op.
- After `Completed`, another `Mark()` does nothing.
- `Dispose()` unsubscribes: raising movement afterwards does not throw and
  changes nothing.
- Idle timeout: inject the timer interval (ctor overload
  `CalibrationSession(IMouseInputSource, TimeSpan idleTimeout)` with a
  tiny value, or an injectable clock) → `Failed("...timed out...")`.
  Prefer an injectable timeout arg over sleeping.

### `RadioProfileTests` / `ConfigStoreTests` (extend)

- Defaults: `CalibrateHotkey.VirtualKeyCode == 0`,
  `MeasuredYawSweepCounts == 0f`.
- `SetField` change notification fires for both new properties.
- `ConfigStore` round-trips both (save then load, values preserved).

### `MainViewModel` — no framework for it today

Wiring is verified manually (see below). Keep VM glue thin so the logic
under test lives in `CalibrationSession`.

### Manual verification (the testable stage)

1. Build, run the app, Start with a source playing.
2. Bind a freelook key and a distinct "Calibrate mark hotkey"; confirm
   "Calibrate freelook" enables only when both are bound and different,
   and only while running.
3. Click Calibrate → status shows the LEFT prompt. In a game (or just by
   moving the mouse with the freelook key held), sweep left, tap Mark →
   one blip, status shows the RIGHT prompt. Sweep right, tap Mark →
   rising three-blip, status shows "done".
4. Confirm `MouseSensitivity` changed and, after Save Profile + restart,
   both it and `MeasuredYawSweepCounts` persist.
5. Drag `MaxYawDegrees`; confirm `MouseSensitivity` tracks it.
6. Start calibration, tap Mark twice without moving → low blip, "sweep
   too small", profile unchanged.
7. Start calibration, click Calibrate again → low blip, "cancelled".
8. Start calibration, click Stop → session ends cleanly, no crash on hook
   teardown.

## Out of scope

- Pitch calibration / making pitch audible.
- Calibration without Start (standalone hook).
- Asymmetric per-side limits.
- Auto-detecting the game clamp without a user mark.
- An in-game overlay.
- Mouse-acceleration / non-linear response modelling (assumed linear).
