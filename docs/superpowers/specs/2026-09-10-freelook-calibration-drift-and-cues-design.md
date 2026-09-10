# Freelook calibration — drift correction and spoken cues

Date: 2026-09-10
Status: Approved (brainstorming)
Supersedes parts of: `docs/superpowers/specs/2026-09-10-freelook-calibration-design.md`

## Why

User testing of the first calibration cut (commits `608367e`..`b07b5af`)
surfaced two problems:

1. **No audible feedback.** `CalibrationCuePlayer` used a bare
   `WaveOutEvent` with cue buffers (120–330 ms) shorter than NAudio's
   default 300 ms / 2-buffer latency, so playback underran before
   anything came out — and it played to the default endpoint, not the
   user's configured radio output.
2. **Drift.** After looking around in 2D (turning while also pitching),
   the app's idea of "forward" no longer matches the game's, and it does
   not recover even after the user re-centres the view in-game. Root
   cause: the app dead-reckons the camera by integrating raw mouse
   deltas with no ground-truth reference. Calibrating only the yaw
   sensitivity, as a rectangular per-axis clamp, cannot stop the app
   from losing mouse counts every time the player flicks past the game's
   view limit. The user's games "vary" between rectangular and
   conical limits.

## Decisions locked in brainstorming

| Question | Decision |
| --- | --- |
| Cue style | Spoken words via Windows in-box TTS (`System.Speech`) |
| Cue routing | Render TTS to an in-memory WAV, play via NAudio to `Profile.OutputDeviceId` (else default) |
| Between-hold drift | Already handled by spring-to-zero on key release; make it an **instant snap** and retire the gradual ease |
| Intra-hold drift | Calibrate a **combined off-forward angle limit** (3rd "corner" mark) and clamp `√(yaw²+pitch²)` to it, alongside the existing per-axis clamps |
| "Freelook always on" (no key) | Ease toward centre after ~1 s of no mouse movement, at the (repurposed) spring-back rate |
| Calibration marks | 3: centre → right limit → corner. Drops the separate "left" mark (left/right assumed symmetric) |

## Components

### `RadioProfile` — one new field, one repurposed

```csharp
private float _measuredMaxOffAxisDegrees = 0f;

/// Largest angle from "straight ahead" the game's freelook allows,
/// captured by the calibration "corner" mark. 0 means "no combined-angle
/// clamp" (per-axis clamps only). FreelookTracker clamps
/// sqrt(yaw^2 + pitch^2) to this when > 0.
public float MeasuredMaxOffAxisDegrees { get => _measuredMaxOffAxisDegrees; set => SetField(ref _measuredMaxOffAxisDegrees, value); }
```

`SpringBackRatePerSecond` keeps its name and its `SetField` property
(profile-compat, still serialised) but its **meaning narrows**: it is now
only the ease rate used in `FreelookAlwaysOn` idle re-centring. Its
window slider is removed (see Window).

### `FreelookTracker`

`src/SpotifyGameRadio.Core/Tracking/FreelookTracker.cs`.

State to add: `bool _wasHotkeyHeld;` and `float _idleSeconds;`.

**`OnMouseMoved(dx, dy)`** — unchanged accumulation and per-axis clamp,
then:

```csharp
_idleSeconds = 0f;
ApplyOffAxisClamp();
```

```csharp
private void ApplyOffAxisClamp()
{
    float max = _profile.MeasuredMaxOffAxisDegrees;
    if (max <= 0f) return;
    float combined = MathF.Sqrt(YawDegrees * YawDegrees + PitchDegrees * PitchDegrees);
    if (combined <= max || combined == 0f) return;
    float scale = max / combined;
    YawDegrees *= scale;
    PitchDegrees *= scale;
}
```

**`Update(float deltaSeconds)`** — replaces the current gradual
spring-back:

```csharp
public void Update(float deltaSeconds)
{
    bool held = _inputSource.IsHotkeyHeld;

    if (!_profile.FreelookAlwaysOn)
    {
        // Hold-to-look: the instant the key is released, snap to forward.
        // The game does the same, so per-hold integration error never
        // survives into the next hold.
        if (_wasHotkeyHeld && !held)
        {
            YawDegrees = 0f;
            PitchDegrees = 0f;
        }
        _wasHotkeyHeld = held;
        return;
    }

    // FreelookAlwaysOn: no key to release. Ease back toward centre once
    // the mouse has been still for ~1 s.
    _idleSeconds += deltaSeconds;
    if (_idleSeconds < 1.0f) return;
    float step = _profile.SpringBackRatePerSecond * deltaSeconds;
    YawDegrees = SpringTowardZero(YawDegrees, step);
    PitchDegrees = SpringTowardZero(PitchDegrees, step);
}
```

`Recenter()` unchanged. `SpringTowardZero` kept (now only used by the
always-on path). The `deltaX * MouseSensitivity` / `deltaY *
MouseSensitivity` integration and the `Clamp(..., MaxYawDegrees)` /
`Clamp(..., MaxPitchDegrees)` per-axis limits are unchanged.

**Note on the always-on idle timer:** `_idleSeconds` is reset in
`OnMouseMoved` (hook thread) and read/incremented in `Update` (audio
thread). Both are single `float` writes/reads; a torn increment at worst
delays the ease by one block. No lock needed — matches the existing
"two atomic float reads" comment on `Recenter`.

### `CalibrationSession`

`src/SpotifyGameRadio.Core/Tracking/CalibrationSession.cs`. Three marks
now; accumulate both axes.

```csharp
public enum CalibrationStep { Idle, AwaitCentre, AwaitRightLimit, AwaitCorner, Completed, Failed, Aborted }

public sealed record CalibrationResult(
    float HalfSweepCounts,   // |x| at the right-limit mark (centre-relative)
    float CornerXCounts,     // x at the corner mark (centre-relative)
    float CornerYCounts);    // y at the corner mark (centre-relative, +down as the hook reports it)
```

- Two `long` accumulators, `_accumX` and `_accumY`, both fed from
  `OnMouseMoved` while `IsActive && IsHotkeyHeld` (`IsActive` now = step
  is `AwaitCentre`, `AwaitRightLimit`, or `AwaitCorner`).
- `Start()` → `AwaitCentre`, zero both accumulators. Prompt: "Get into
  the game, face straight forward, hold freelook, then tap Mark."
- `Mark()`:
  - `AwaitCentre` → zero both accumulators (this is the true zero
    reference), → `AwaitRightLimit`. Prompt: "Look fully RIGHT until the
    view stops, then tap Mark."
  - `AwaitRightLimit` → `halfSweep = |Interlocked.Read(_accumX)|`; if
    `halfSweep < MinValidSweepCounts (50)` → `Failed` ("the sweep was
    too small…"). Else keep it, **do not** re-zero (the corner is
    measured from the same centre), → `AwaitCorner`. Prompt: "Now look
    to the far corner — as far up and to one side as the game allows —
    then tap Mark."
  - `AwaitCorner` → `cornerX = Interlocked.Read(_accumX)`,
    `cornerY = Interlocked.Read(_accumY)`; if
    `sqrt(cornerX² + cornerY²) < MinValidSweepCounts` → `Failed`. Else
    → `Completed` with `new CalibrationResult(halfSweep, cornerX, cornerY)`.
- `Abort()`, idle-timeout (60 s, reset each `Mark()`), `Dispose()`,
  event model (`StepChanged`, `Completed`, `Ended`), lock/`Interlocked`
  discipline, "raise outside the lock" — all exactly as the current
  implementation, extended for the extra step and accumulator.

### `CalibrationAnnouncer` (replaces `CalibrationCuePlayer`)

`src/SpotifyGameRadio.App/Audio/CalibrationAnnouncer.cs`. Delete
`CalibrationCuePlayer.cs`.

```csharp
public sealed class CalibrationAnnouncer : IDisposable
{
    public CalibrationAnnouncer(Func<string?> outputDeviceIdProvider);
    public void Say(string text);   // fire-and-forget; never throws to caller
    public void Dispose();
}
```

- Holds one `System.Speech.Synthesis.SpeechSynthesizer`.
- `Say(text)` runs on `Task.Run`:
  1. `synth.SetOutputToWaveStream(ms)` with a fresh `MemoryStream`.
  2. `synth.Speak(text)` (synchronous render into `ms`).
  3. Rewind `ms`; `new WaveFileReader(ms)` → `ISampleProvider`.
  4. Resolve the device: if `outputDeviceIdProvider()` returns a
     non-empty id that `MMDeviceEnumerator` still lists as active render,
     play via `new WasapiOut(mmDevice, AudioClientShareMode.Shared,
     false, 120)`; otherwise `new WaveOutEvent()` (default). Init, Play,
     and dispose the player on its `PlaybackStopped`.
  5. The whole body is wrapped in try/catch that swallows — a failed
     announcement must never break calibration.
- Serialise calls with a `SemaphoreSlim(1,1)` (or a queue) so two quick
  marks don't render over each other; a pending say is fine to drop if a
  newer one arrives (latest-wins is acceptable — keep it simple with the
  semaphore and just let them queue).
- `Dispose()` disposes the synth; in-flight tasks are best-effort.

Phrases (chosen by the view model, see below): `"Centre set."`,
`"Right limit set."`, `"Corner set. Calibration complete."`,
`"Calibration cancelled."`, `"That sweep was too small. Try again."`,
`"Calibration timed out."`, and on start `"Calibration started. Face
forward and tap to set centre."`

### `MainViewModel` wiring changes

- Field rename `_cuePlayer` → `_announcer`, type `CalibrationAnnouncer`,
  constructed as `new CalibrationAnnouncer(() => Profile.OutputDeviceId)`.
  `Cleanup()` disposes it.
- `StartOrAbortCalibration()`: unchanged structure. `session.StepChanged`
  maps step → phrase via a `switch` and calls `_announcer.Say(...)`
  (announce **every** step, including `AwaitCentre` at start, not just
  one).
- `session.Completed(result)` now computes:

  ```csharp
  float sens = Profile.MaxYawDegrees / result.HalfSweepCounts;
  float cornerYawDeg   = MathF.Abs(result.CornerXCounts) * sens;
  float cornerPitchDeg = MathF.Abs(result.CornerYCounts) * sens;

  Profile.MouseSensitivity        = sens;
  Profile.MeasuredYawSweepCounts  = result.HalfSweepCounts;
  Profile.MaxPitchDegrees         = cornerPitchDeg > 1f ? cornerPitchDeg : Profile.MaxPitchDegrees;
  Profile.MeasuredMaxOffAxisDegrees = MathF.Sqrt(cornerYawDeg * cornerYawDeg + cornerPitchDeg * cornerPitchDeg);
  ```

  Then `_announcer.Say("Corner set. Calibration complete.")`, set
  `StatusMessage` to a summary, `TeardownCalibration()`.
- `session.Ended(reason)` → `_announcer.Say(reason)` + status + teardown.
- The `MaxYawDegrees` re-derive branch (added last round, ahead of the
  `LiveProfileProperties` block) now also rescales the off-axis limit so
  it tracks the same proportional change:

  ```csharp
  if (e.PropertyName == nameof(RadioProfile.MaxYawDegrees)
      && Profile.MeasuredYawSweepCounts > 0f)
  {
      float oldSens = Profile.MouseSensitivity;
      float newSens = Profile.MaxYawDegrees / Profile.MeasuredYawSweepCounts;
      if (oldSens > 0f && Profile.MeasuredMaxOffAxisDegrees > 0f)
          Profile.MeasuredMaxOffAxisDegrees *= newSens / oldSens;
      Profile.MouseSensitivity = newSens;
  }
  ```

### Window — `MainWindow.xaml`

- **Remove** the "Spring-back (°/s)" row (currently `Grid.Row="19"`).
  Drop one `RowDefinition` from the grid and shift every row after it
  down by one: calibration `20→19`, buttons `21→20`, canvas `22→21`,
  status `23→22`, updating both the `<!-- N ... -->` comments and the
  `Grid.Row="N"` attributes. Set the window `Height="960"` (last round
  raised it to 990 for the added calibration row; removing a row of the
  same shape frees roughly that much) and confirm at run time that the
  status line is not clipped.
- The calibration row itself (hotkey capture + "Calibrate freelook"
  button + hint) is unchanged from last round apart from its row index.
- No new controls for `MeasuredMaxOffAxisDegrees` — it is calibration
  output, not a user knob.

## Packaging

- `System.Speech` 8.0.0 `PackageReference` added to the App project
  (verified: restores, "compatible with all the specified frameworks").
  It is a managed assembly and lands in the publish output
  automatically; `build/package.ps1` needs no change. Confirm
  `System.Speech.dll` appears in the built zip during verification.

## Testing

### `FreelookTrackerTests` (extend) — `tests/.../Tracking/FreelookTrackerTests.cs`

Reuse the existing `FakeMouseInputSource`. New facts:

- **Snap on release:** hotkey held, move to yaw 30 / pitch 10, `Update` →
  still 30/10; set `IsHotkeyHeld = false`, `Update` → yaw and pitch are
  exactly 0.
- **No gradual ease any more:** with a small `SpringBackRatePerSecond`,
  one `Update` after release is already 0 (not a partial decrement).
- **Off-axis clamp:** profile `MeasuredMaxOffAxisDegrees = 50`,
  `MaxYawDegrees = 90`, `MaxPitchDegrees = 90`; hold, move to raw yaw 40
  / pitch 40 (combined ≈ 56.6). After the move, `√(yaw²+pitch²) ≈ 50`
  (within 0.5) and the yaw:pitch ratio is preserved (≈ 1:1).
- **Off-axis clamp disabled at 0:** `MeasuredMaxOffAxisDegrees = 0` →
  combined angle may exceed any value up to the per-axis clamps.
- **Always-on idle ease:** `FreelookAlwaysOn = true`,
  `SpringBackRatePerSecond = 100`; move to yaw 20; call `Update(0.1)`
  ~11 times with no mouse movement → yaw eased below 20 and heading to 0
  (first ~1 s of `Update` calls do nothing; ease starts after).
- **Always-on ease resets on movement:** interleave a `RaiseMove` and
  confirm the idle timer restarts (ease does not progress).
- Existing spring-back tests that asserted a *gradual* decrement for the
  hold-to-look path are updated to expect the snap.

### `CalibrationSessionTests` (rewrite for 3 marks)

- `Start` → `AwaitCentre`, announced.
- Centre mark re-zeroes: move −999 before centre mark, then the
  right-limit and corner measurements are relative to the post-centre
  zero.
- Happy path: centre mark; move x +6000 → right mark (`HalfSweepCounts
  == 6000`, step `AwaitCorner`); move to x −2000 (net +4000) and y −3000
  → corner mark → `Completed` with `CornerXCounts == 4000`,
  `CornerYCounts == -3000`.
- Right-limit sweep `< 50` → `Failed`, no `Completed`.
- Corner within 50 counts of centre → `Failed`.
- `Abort()` from each of the three await steps → `Aborted` + `Ended`.
- `Mark()` after terminal is a no-op.
- `Dispose()` unsubscribes.
- Idle timeout (injected short timeout) → `Failed` "timed out".

### `RadioProfileTests` / `ConfigStoreTests` (extend)

- `MeasuredMaxOffAxisDegrees` default `0f`, raises `PropertyChanged`,
  round-trips through `ConfigStore`, and is a "missing field keeps
  default" case.

### Manual verification (the testable stage)

1. Build; `System.Speech.dll` present in `bin` and in a `package.ps1`
   zip.
2. Run, Start with a source playing, bind freelook + calibrate hotkeys.
3. Click "Calibrate freelook" → **hear** "Calibration started…". Tap
   Mark → "Centre set." Sweep right, Mark → "Right limit set." Sweep to
   a corner, Mark → "Corner set. Calibration complete."
4. Confirm the announcements come out of the **configured** output
   device (set a non-default device and re-check).
5. Confirm `Mouse sensitivity` and `Max pitch (°)` moved; save + restart
   → persisted.
6. Hold freelook, look around in 2D including to the limits, release →
   audio image snaps to forward immediately.
7. Turn while pitched: the radio no longer swings past where the game
   view can actually go (off-axis clamp).
8. Bad paths: two Marks with no movement between right and corner →
   "That sweep was too small. Try again.", profile unchanged. Cancel via
   the button → "Calibration cancelled."
9. `FreelookAlwaysOn` on: after moving, leaving the mouse still ~1 s
   eases the image back to centre.

## Out of scope

- Separate horizontal/vertical mouse sensitivity.
- Asymmetric left/right limits.
- Non-linear mouse response / acceleration.
- An in-game overlay.
- Choosing the TTS voice / rate from the UI (use the system default).
