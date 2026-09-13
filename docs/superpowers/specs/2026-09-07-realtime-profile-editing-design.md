# Real-time Profile Editing — Design Spec

Date: 2026-09-07

## Problem

Tuning the radio sound and freelook feel currently requires a
Stop → edit → Start cycle for every change: `MainViewModel.Start()`
calls `pipeline.ApplyProfile(Profile)` exactly once, and `RadioProfile`
is a plain POCO with no change notification, so slider edits sit in the
object until the next Start. Fast setup needs every safe-to-change
value to take effect on the running pipeline immediately.

Some settings cannot hot-swap — they rebuild capture, output, the
global hook, or the routing state. Those stay Stop → Start, but the UI
must say so instead of silently ignoring the edit.

## Non-goals

- No hot-swapping the source process, output device, freelook hotkey
  key, or route-target device while running. These show a "restart to
  apply" hint and take effect on the next Start.
- No parameter smoothing / click-suppression ramps. Filter coefficient
  and gain changes apply between audio blocks; small live tweaks are
  audibly fine, and this already happens once at Start. A lock-free
  coefficient swap is a possible later improvement, out of scope here.
- No undo/redo or per-parameter reset.

## Live vs. restart-required

**Live** (apply immediately to the running pipeline):

- `HighPassHz`, `LowPassHz`, `DistortionDrive`,
  `CompressorThresholdDb`, `CompressorRatio`, `NoiseLevel`,
  `WetDryMix`
- `SourceX`, `SourceY`, `SourceZ`
- `MouseSensitivity`, `MaxYawDegrees`, `MaxPitchDegrees`,
  `SpringBackRatePerSecond`

**Restart-required** (edit is stored, `RestartRequired` flag is set
while running, applied on next Start):

- `SourceProcessName`, `OutputDeviceId`
- `Hotkey` (the VK code)
- `AutoRouteSource`, `RouteSourceToDeviceId` (only present once the
  source-audio-routing feature lands; listed here so the handler
  covers them)

## Architecture

### RadioProfile → INotifyPropertyChanged (Core)

`RadioProfile` implements `System.ComponentModel.INotifyPropertyChanged`.
Every auto-property becomes a backing-field property that raises
`PropertyChanged` on change (with an equality guard so no-op sets are
silent). `System.ComponentModel` is a BCL namespace — no UI dependency
is added to Core.

`FreelookHotkey` stays a plain POCO. The hotkey control assigns a **new**
`FreelookHotkey` instance to `Profile.Hotkey`, so the change surfaces as
a `Hotkey` property change.

JSON serialisation is unchanged — `System.Text.Json` serialises public
properties identically whether auto- or backing-field, and does not
touch the event. Existing saved profiles load unchanged.

### RadioPipeline.ApplyProfile (Core)

No behaviour change. It already performs exactly the live updates
needed — re-applies the effect chain, tracker, and both spatializers'
source position, and touches nothing in capture / output / hook. A
comment is added noting it is safe to call on a running pipeline from
the UI thread.

**Concurrency:** the audio thread reads effect-chain / tracker /
spatializer scalars per block; the UI thread writes them via
`ApplyProfile`. Individual `float` reads and writes are atomic in .NET,
so there is no tearing of a single value. `BiquadFilter.SetCutoff`
recomputes five coefficients non-atomically, so one audio block during
an edit may mix old and new coefficients — a harmless transient, not a
crash. No locking is added (it would risk blocking the audio thread).

### MainViewModel (App)

- Subscribes to `Profile.PropertyChanged` in the constructor;
  re-subscribes when `LoadProfile` swaps the `Profile` instance
  (unsubscribe old, subscribe new).
- Handler:
  - changed property in the **live** set → `_pipeline?.ApplyProfile(Profile)`
  - changed property in the **restart-required** set and
    `_pipeline is not null` → `RestartRequired = true`
- New `bool RestartRequired` property (INPC), bound by the UI. Set only
  by the handler above; cleared in `Start()` and `Stop()`.
- `LoadProfile` while running: apply the live values immediately (one
  `ApplyProfile` call) and set `RestartRequired = true` so the
  non-live parts of the loaded profile prompt a restart.
- `Start()` clears `RestartRequired` after a successful start.

### UI (App)

`MainWindow.xaml` — new "Freelook" rows below the existing DSP sliders:

| Control | Binds | Range | Class |
| --- | --- | --- | --- |
| Hotkey capture button | `Profile.Hotkey` | — | restart |
| Mouse sensitivity slider | `Profile.MouseSensitivity` | 0.01–1.0 | live |
| Max yaw slider | `Profile.MaxYawDegrees` | 0–180 | live |
| Max pitch slider | `Profile.MaxPitchDegrees` | 0–90 | live |
| Spring-back slider | `Profile.SpringBackRatePerSecond` | 0–2000 | live |

- **Hotkey capture control** — new `HotkeyCaptureControl : UserControl`.
  Idle: shows the current key/button name (from the VK code). Clicked:
  shows "Press a key or mouse button…", captures the next
  `PreviewKeyDown` (`KeyInterop.VirtualKeyFromKey`) or
  `PreviewMouseDown` (L/R/M = `0x01/0x02/0x04`, XButton1/2 =
  `0x05/0x06`), writes a new `FreelookHotkey`, returns to idle.
  `Escape` cancels. Exposes a `Hotkey` dependency property for binding.
- **Restart hint** — a `TextBlock` near Start/Stop, visible when
  `RestartRequired`: "Restart to apply changed source / output /
  hotkey / routing."
- Inline "restart to apply" marker text next to the Source combo (and
  Output / route combos when they exist).

## Data flow

```
user drags a slider
  → TwoWay binding sets Profile.<Prop>
  → RadioProfile raises PropertyChanged("<Prop>")
  → MainViewModel handler:
       live prop      → _pipeline?.ApplyProfile(Profile)
                          → RadioEffectChain.ApplyProfile / tracker / spatializer
                          → audio thread picks up new values on the next block
       restart prop   → RestartRequired = true  (UI shows the hint)
```

When no pipeline is running, live props still write to `Profile`;
`ApplyProfile` is a no-op call on `null` and the values are used at the
next Start.

## Error handling

| Condition | Behaviour |
| --- | --- |
| `ApplyProfile` throws mid-edit (unexpected) | Caught in the handler, surfaced to `StatusMessage`; the pipeline keeps running with its previous values. |
| Slider bound to a value outside a component's sane range | Sliders are clamped by their `Minimum`/`Maximum`; `RadioEffectChain` / `FreelookTracker` already tolerate their full ranges. |
| Hotkey capture gets a key with no VK mapping | Ignored; control stays in capture mode until a valid key/button or `Escape`. |
| `LoadProfile` while running | Live values applied immediately; `RestartRequired` set for the rest. |

## Testing

Automated (xUnit, `LogiBuddy.Core.Tests`):

- `RadioProfileTests` (new) — setting each public property raises
  `PropertyChanged` once with the correct name; setting a property to
  its current value raises nothing.
- `ConfigStoreTests` — unchanged expectations still pass (JSON
  round-trip of the now-INPC profile), plus an assertion that a
  profile JSON written before this change loads with all values
  intact.
- `RadioPipelineTests` — a case that, after `Start()`, mutating the
  profile and calling `ApplyProfile` updates the spatializer source
  position and effect-chain parameters without throwing.

Manual checklist (add to `docs/SETUP.md`):

1. Start with a source playing. Drag High-pass, Low-pass, Distortion,
   Noise, Wet/Dry — each change is audible immediately, no dropouts or
   clicks beyond a faint transient.
2. Drag the position marker — the image moves live.
3. While running, hold the freelook hotkey and adjust Mouse
   sensitivity / Max yaw — the response changes without a restart.
4. Change the Source dropdown while running — the "restart to apply"
   hint appears; audio is unchanged until Stop → Start.
5. Rebind the freelook hotkey — hint appears; old key still active
   until restart, new key active after.

## Open questions for implementation planning

- Whether to convert `RadioProfile`'s ~18 properties by hand or with a
  small `SetField` helper (`SetField(ref _x, value)` +
  `[CallerMemberName]`). The helper keeps the file readable; decide at
  implementation time.
- Whether the existing `SourcePositionCanvas` already raises its bound
  `SourceX`/`SourceZ` changes through `Profile` (it binds TwoWay, so it
  should) — verify during implementation and add `SourceY` handling if
  a Y control is wanted later.
