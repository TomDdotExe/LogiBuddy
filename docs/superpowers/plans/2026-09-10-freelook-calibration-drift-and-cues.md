# Calibration Drift Correction + Spoken Cues — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the freelook audio image drifting from the game view during 2D looking, and make calibration announce its progress in speech through the configured output device.

**Architecture:** `FreelookTracker` gains an instant snap-to-forward on freelook-key release, a combined-angle (cone) clamp from a calibrated max, and an idle ease for always-on mode. `CalibrationSession` grows a third "corner" mark and a Y accumulator. `CalibrationCuePlayer` is replaced by `CalibrationAnnouncer` (System.Speech → WAV → NAudio to `Profile.OutputDeviceId`).

**Tech Stack:** C# / .NET 8, WPF (MVVM), xUnit, NAudio 2.2.1, System.Speech 8.0.0.

**Spec:** `docs/superpowers/specs/2026-09-10-freelook-calibration-drift-and-cues-design.md`
(supersedes parts of `docs/superpowers/specs/2026-09-10-freelook-calibration-design.md`)

## Global Constraints

- **Snap, don't ease, on key release** (hold-to-look, i.e. `!FreelookAlwaysOn`): the held→released edge zeroes `YawDegrees` and `PitchDegrees` immediately.
- **Off-axis clamp:** when `Profile.MeasuredMaxOffAxisDegrees > 0`, after each mouse integration clamp `√(yaw²+pitch²)` to it by scaling both axes down proportionally. Per-axis `MaxYawDegrees` / `MaxPitchDegrees` clamps still apply first.
- **Always-on idle ease:** `FreelookAlwaysOn` only — after ≥ 1.0 s of no mouse movement, ease both axes toward 0 at `SpringBackRatePerSecond * deltaSeconds` (reusing `SpringTowardZero`, which never overshoots).
- **`SpringBackRatePerSecond`** keeps its field and serialisation; its only remaining use is the always-on idle ease; its window slider is removed.
- **Calibration marks are centre → right → corner.** Measurements are relative to the centre mark. `MinValidSweepCounts = 50` guards both the right sweep and the corner distance.
- **`CalibrationResult(float HalfSweepCounts, float CornerXCounts, float CornerYCounts)`** — counts, centre-relative; Y is +down as the hook reports it.
- **Announcements never throw to the caller** and play to `Profile.OutputDeviceId` when it names an active render device, else the default endpoint.
- Existing `CalibrationSession` threading/locking discipline (transitions under `_gate`, accumulators via `Interlocked`, events raised outside the lock) is preserved.

---

### Task 1: `RadioProfile.MeasuredMaxOffAxisDegrees`

**Files:**
- Modify: `src/LogiBuddy.Core/Config/RadioProfile.cs`
- Test: `tests/LogiBuddy.Core.Tests/Config/RadioProfileTests.cs`
- Test: `tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Produces: `RadioProfile.MeasuredMaxOffAxisDegrees` (`float`, default `0f`), `SetField`-backed.

- [ ] **Step 1: Add failing tests**

In `RadioProfileTests.cs`, extend `NewCalibrationFields_HaveExpectedDefaults`:

```csharp
        Assert.Equal(0f, profile.MeasuredMaxOffAxisDegrees);
```

and `AssigningCalibrateHotkey_RaisesPropertyChanged` (rename not required):

```csharp
        profile.MeasuredMaxOffAxisDegrees = 55f;
        Assert.Contains(nameof(RadioProfile.MeasuredMaxOffAxisDegrees), changed);
```

In `ConfigStoreTests.cs`: add `MeasuredMaxOffAxisDegrees = 47f,` to the
round-trip initialiser, `Assert.Equal(47f, loaded.MeasuredMaxOffAxisDegrees);`
to its assertions, and
`Assert.Equal(0f, loaded.MeasuredMaxOffAxisDegrees);` to
`Load_ProfileJsonMissingNewerFields_KeepsDefaults`.

- [ ] **Step 2: Run — expect compile failure**

`dotnet test tests/LogiBuddy.Core.Tests --filter FullyQualifiedName~RadioProfileTests`

- [ ] **Step 3: Add the field**

In `RadioProfile.cs`, near `_measuredYawSweepCounts`:

```csharp
private float _measuredMaxOffAxisDegrees = 0f;
```

and near `MeasuredYawSweepCounts`:

```csharp
/// Largest angle from "straight ahead" the game's freelook allows,
/// captured by the calibration "corner" mark. 0 means "no combined-angle
/// clamp" (per-axis clamps only). FreelookTracker clamps
/// sqrt(yaw^2 + pitch^2) to this when > 0.
public float MeasuredMaxOffAxisDegrees { get => _measuredMaxOffAxisDegrees; set => SetField(ref _measuredMaxOffAxisDegrees, value); }
```

- [ ] **Step 4: Run the Core suite — expect pass**

`dotnet test tests/LogiBuddy.Core.Tests --nologo`

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Config/RadioProfile.cs tests/LogiBuddy.Core.Tests/Config/
git commit -m "$(cat <<'EOF'
feat: add RadioProfile.MeasuredMaxOffAxisDegrees (calibrated cone limit)

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

### Task 2: `FreelookTracker` — snap on release, off-axis clamp, always-on idle ease

**Files:**
- Modify: `src/LogiBuddy.Core/Tracking/FreelookTracker.cs`
- Test: `tests/LogiBuddy.Core.Tests/Tracking/FreelookTrackerTests.cs`

**Interfaces:**
- Consumes: `RadioProfile.MeasuredMaxOffAxisDegrees` (Task 1), existing `MouseSensitivity` / `MaxYawDegrees` / `MaxPitchDegrees` / `SpringBackRatePerSecond` / `FreelookAlwaysOn`, `IMouseInputSource.IsHotkeyHeld`.
- Produces: same public surface (`YawDegrees`, `PitchDegrees`, `ApplyProfile`, `Recenter`, `Update`, ctor). No signature changes.

- [ ] **Step 1: Update / add tests**

In `FreelookTrackerTests.cs`:

Replace `Update_WhileHotkeyReleased_SpringsBackTowardZero` with:

```csharp
    [Fact]
    public void Update_OnHotkeyRelease_SnapsToForwardImmediately()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        input.RaiseMove(dx: 100, dy: 40); // yaw 10, pitch 4
        tracker.Update(0.05f);            // still held
        Assert.Equal(10f, tracker.YawDegrees, precision: 3);

        input.IsHotkeyHeld = false;
        tracker.Update(0.001f);

        Assert.Equal(0f, tracker.YawDegrees, precision: 3);
        Assert.Equal(0f, tracker.PitchDegrees, precision: 3);
    }
```

Replace `AlwaysOn_UpdateDoesNotSpringBack` with:

```csharp
    [Fact]
    public void AlwaysOn_Update_DoesNotEaseUntilMouseIdleOneSecond()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var profile = MakeProfile();
        profile.FreelookAlwaysOn = true;
        var tracker = new FreelookTracker(input, profile);
        input.RaiseMove(dx: 100, dy: 0); // yaw 10

        for (int i = 0; i < 9; i++) tracker.Update(0.1f); // 0.9s idle
        Assert.Equal(10f, tracker.YawDegrees, precision: 3);
    }

    [Fact]
    public void AlwaysOn_Update_EasesTowardZeroAfterOneSecondIdle()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var profile = MakeProfile();       // SpringBackRatePerSecond = 100
        profile.FreelookAlwaysOn = true;
        var tracker = new FreelookTracker(input, profile);
        input.RaiseMove(dx: 100, dy: 0);   // yaw 10

        for (int i = 0; i < 30; i++) tracker.Update(0.1f); // 3s: 1s wait + 2s ease @100/s

        Assert.Equal(0f, tracker.YawDegrees, precision: 3);
    }

    [Fact]
    public void AlwaysOn_MouseMovement_ResetsTheIdleTimer()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var profile = MakeProfile();
        profile.FreelookAlwaysOn = true;
        var tracker = new FreelookTracker(input, profile);
        input.RaiseMove(dx: 100, dy: 0); // yaw 10

        for (int i = 0; i < 9; i++) { tracker.Update(0.1f); input.RaiseMove(0, 0); }
        // Idle never reaches 1s because movement keeps resetting it.
        Assert.Equal(10f, tracker.YawDegrees, precision: 3);
    }
```

> Note: `RaiseMove(0, 0)` still invokes the handler; the handler must
> reset `_idleSeconds` on *every* call, not only on non-zero deltas.

Add off-axis clamp tests:

```csharp
    [Fact]
    public void OffAxisClamp_LimitsCombinedAngle_PreservingRatio()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var profile = MakeProfile();
        profile.MaxYawDegrees = 90f;
        profile.MaxPitchDegrees = 90f;
        profile.MeasuredMaxOffAxisDegrees = 50f;
        var tracker = new FreelookTracker(input, profile);

        input.RaiseMove(dx: 400, dy: 400); // raw yaw 40, pitch 40, combined ~56.57

        float combined = MathF.Sqrt(
            tracker.YawDegrees * tracker.YawDegrees + tracker.PitchDegrees * tracker.PitchDegrees);
        Assert.Equal(50f, combined, precision: 1);
        Assert.Equal(tracker.YawDegrees, tracker.PitchDegrees, precision: 2); // ratio preserved
    }

    [Fact]
    public void OffAxisClamp_Disabled_WhenMeasuredMaxIsZero()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var profile = MakeProfile();
        profile.MaxYawDegrees = 90f;
        profile.MaxPitchDegrees = 90f;
        profile.MeasuredMaxOffAxisDegrees = 0f;
        var tracker = new FreelookTracker(input, profile);

        input.RaiseMove(dx: 400, dy: 400); // yaw 40, pitch 40

        Assert.Equal(40f, tracker.YawDegrees, precision: 3);
        Assert.Equal(40f, tracker.PitchDegrees, precision: 3);
    }
```

`Update_SpringBack_NeverOvershootsZero` stays (release path now snaps;
`0 == 0` still holds). `MouseMove_*`, `Yaw_IsClampedToMaxYawDegrees`,
`AlwaysOn_TracksMovementWithHotkeyNotHeld`, `Recenter_ZeroesYawAndPitch`
stay unchanged.

- [ ] **Step 2: Run — expect the new/changed tests to fail**

`dotnet test tests/LogiBuddy.Core.Tests --filter FullyQualifiedName~FreelookTrackerTests`

- [ ] **Step 3: Implement**

`FreelookTracker.cs` — add fields:

```csharp
    private bool _wasHotkeyHeld;
    private float _idleSeconds;
```

`OnMouseMoved` — after the two existing `Clamp` assignments:

```csharp
        _idleSeconds = 0f;
        ApplyOffAxisClamp();
```

Add:

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

Replace `Update`:

```csharp
    /// Call once per audio block. Hold-to-look: snaps the view to forward
    /// the instant the freelook key is released, mirroring the game's own
    /// snap-back and resetting any integration error before the next hold.
    /// FreelookAlwaysOn: eases toward centre once the mouse has been still
    /// for ~1 s (there is no key release to reset on).
    public void Update(float deltaSeconds)
    {
        bool held = _inputSource.IsHotkeyHeld;

        if (!_profile.FreelookAlwaysOn)
        {
            if (_wasHotkeyHeld && !held)
            {
                YawDegrees = 0f;
                PitchDegrees = 0f;
            }
            _wasHotkeyHeld = held;
            return;
        }

        _idleSeconds += deltaSeconds;
        if (_idleSeconds < 1.0f) return;
        float step = _profile.SpringBackRatePerSecond * deltaSeconds;
        YawDegrees = SpringTowardZero(YawDegrees, step);
        PitchDegrees = SpringTowardZero(PitchDegrees, step);
    }
```

Keep `SpringTowardZero` and `Clamp` as-is. `OnMouseMoved` must reset
`_idleSeconds` unconditionally (it already returns early when neither
`FreelookAlwaysOn` nor `IsHotkeyHeld` — move the `_idleSeconds = 0f`
reset *above* that guard so an always-on move always counts):

```csharp
    private void OnMouseMoved(int deltaX, int deltaY)
    {
        _idleSeconds = 0f;
        if (!_profile.FreelookAlwaysOn && !_inputSource.IsHotkeyHeld) return;

        YawDegrees = Clamp(YawDegrees + deltaX * _profile.MouseSensitivity, _profile.MaxYawDegrees);
        PitchDegrees = Clamp(PitchDegrees + deltaY * _profile.MouseSensitivity, _profile.MaxPitchDegrees);
        ApplyOffAxisClamp();
    }
```

- [ ] **Step 4: Run FreelookTracker tests — expect pass**

`dotnet test tests/LogiBuddy.Core.Tests --filter FullyQualifiedName~FreelookTrackerTests`

- [ ] **Step 5: Run full Core suite — expect pass**

`dotnet test tests/LogiBuddy.Core.Tests --nologo`

- [ ] **Step 6: Commit**

```bash
git add src/LogiBuddy.Core/Tracking/FreelookTracker.cs tests/LogiBuddy.Core.Tests/Tracking/FreelookTrackerTests.cs
git commit -m "$(cat <<'EOF'
feat: FreelookTracker snap-on-release, off-axis clamp, always-on idle ease

Hold-to-look now snaps the view to forward the instant the freelook key
is released (was a gradual ease), so per-hold integration error can't
survive into the next hold. When MeasuredMaxOffAxisDegrees > 0 the
combined angle sqrt(yaw^2+pitch^2) is clamped to it. FreelookAlwaysOn
eases toward centre after ~1s of mouse stillness.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

### Task 3: `CalibrationSession` — centre / right / corner marks

**Files:**
- Modify: `src/LogiBuddy.Core/Tracking/CalibrationSession.cs`
- Rewrite: `tests/LogiBuddy.Core.Tests/Tracking/CalibrationSessionTests.cs`

**Interfaces:**
- Produces:
  - `enum CalibrationStep { Idle, AwaitCentre, AwaitRightLimit, AwaitCorner, Completed, Failed, Aborted }`
  - `sealed record CalibrationResult(float HalfSweepCounts, float CornerXCounts, float CornerYCounts)`
  - Same class surface: ctors `(IMouseInputSource)` / `(IMouseInputSource, TimeSpan)`, `Step`, `StepChanged`, `Completed`, `Ended`, `Start`, `Mark`, `Abort`, `Dispose`.

- [ ] **Step 1: Rewrite `CalibrationSessionTests.cs`**

```csharp
using System;
using LogiBuddy.Core.Tracking;
using Xunit;

namespace LogiBuddy.Core.Tests.Tracking;

public class CalibrationSessionTests
{
    private static CalibrationSession NewSession(FakeMouseInputSource input) =>
        new(input, TimeSpan.FromMinutes(5));

    [Fact]
    public void Start_MovesToAwaitCentre_AndAnnounces()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationStep? announced = null;
        session.StepChanged += (s, _) => announced = s;

        session.Start();

        Assert.Equal(CalibrationStep.AwaitCentre, session.Step);
        Assert.Equal(CalibrationStep.AwaitCentre, announced);
    }

    [Fact]
    public void CentreMark_ReZeroesAccumulators()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(-999, -999);   // pre-centre drift, must be discarded
        session.Mark();                 // centre

        input.RaiseMove(6000, 0);
        session.Mark();                 // right limit

        input.RaiseMove(-2000, -3000);  // net from centre: x +4000, y -3000
        session.Mark();                 // corner

        Assert.Equal(CalibrationStep.Completed, session.Step);
        Assert.NotNull(result);
        Assert.Equal(6000f, result!.HalfSweepCounts, precision: 3);
        Assert.Equal(4000f, result.CornerXCounts, precision: 3);
        Assert.Equal(-3000f, result.CornerYCounts, precision: 3);
    }

    [Fact]
    public void Movement_WhileHotkeyNotHeld_IsIgnored()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        session.Mark();                       // centre (nothing moved)
        input.RaiseMove(5000, 5000);          // ignored, key not held
        input.IsHotkeyHeld = true;
        input.RaiseMove(6000, 0);
        session.Mark();                       // right
        input.RaiseMove(0, -2000);
        session.Mark();                       // corner

        Assert.Equal(6000f, result!.HalfSweepCounts, precision: 3);
        Assert.Equal(6000f, result.CornerXCounts, precision: 3);
        Assert.Equal(-2000f, result.CornerYCounts, precision: 3);
    }

    [Fact]
    public void RightSweepTooSmall_Fails()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        var completed = false;
        string? ended = null;
        session.Completed += _ => completed = true;
        session.Ended += r => ended = r;

        session.Start();
        session.Mark();               // centre
        input.RaiseMove(20, 0);
        session.Mark();               // right, only 20 counts

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
        Assert.False(string.IsNullOrWhiteSpace(ended));
    }

    [Fact]
    public void CornerTooCloseToCentre_Fails()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        var completed = false;
        session.Completed += _ => completed = true;

        session.Start();
        session.Mark();               // centre
        input.RaiseMove(6000, 0);
        session.Mark();               // right
        input.RaiseMove(-6000, 10);   // back near centre: x 0, y 10 -> distance 10
        session.Mark();               // corner

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
    }

    [Fact]
    public void Abort_FromEachAwaitStep_EndsAndIgnoresLaterMarks()
    {
        foreach (var marksBeforeAbort in new[] { 0, 1, 2 })
        {
            var input = new FakeMouseInputSource { IsHotkeyHeld = true };
            using var session = NewSession(input);
            string? ended = null;
            session.Ended += r => ended = r;

            session.Start();
            for (int i = 0; i < marksBeforeAbort; i++) { input.RaiseMove(6000, 0); session.Mark(); }
            session.Abort();

            Assert.Equal(CalibrationStep.Aborted, session.Step);
            Assert.False(string.IsNullOrWhiteSpace(ended));

            input.RaiseMove(9999, 0);
            session.Mark();
            Assert.Equal(CalibrationStep.Aborted, session.Step);
        }
    }

    [Fact]
    public void Mark_AfterCompleted_IsNoOp()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        session.Start();
        session.Mark();
        input.RaiseMove(6000, 0); session.Mark();
        input.RaiseMove(0, -3000); session.Mark();

        var step = session.Step;
        session.Mark();
        Assert.Equal(step, session.Step);
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var session = NewSession(input);
        session.Start();
        session.Dispose();

        var ex = Record.Exception(() => input.RaiseMove(5000, 5000));
        Assert.Null(ex);
        Assert.Equal(CalibrationStep.AwaitCentre, session.Step);
    }

    [Fact]
    public void IdleTimeout_Fails()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = new CalibrationSession(input, TimeSpan.FromMilliseconds(80));
        string? ended = null;
        session.Ended += r => ended = r;

        session.Start();
        System.Threading.Thread.Sleep(250);

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.Contains("timed out", ended, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run — expect failures**

`dotnet test tests/LogiBuddy.Core.Tests --filter FullyQualifiedName~CalibrationSessionTests`

- [ ] **Step 3: Modify `CalibrationSession.cs`**

- Enum: replace `AwaitLeftLimit` with `AwaitCentre`; add `AwaitCorner`
  before `Completed`. Final:
  `Idle, AwaitCentre, AwaitRightLimit, AwaitCorner, Completed, Failed, Aborted`.
- Record: `public sealed record CalibrationResult(float HalfSweepCounts, float CornerXCounts, float CornerYCounts);`
- Fields: replace `_accum` with `private long _accumX; private long _accumY;` and add `private long _halfSweep;`
- `IsActive` → `Step is AwaitCentre or AwaitRightLimit or AwaitCorner`.
- `OnMouseMoved(dx, dy)`:

  ```csharp
      if (!IsActive || !_input.IsHotkeyHeld) return;
      Interlocked.Add(ref _accumX, dx);
      Interlocked.Add(ref _accumY, dy);
  ```

- `Start()` transition: `Step = CalibrationStep.AwaitCentre`, zero both
  accumulators, prompt `"Calibration started. Get into the game, face straight forward, hold freelook, then tap Mark."`
- `Mark()` switch:

  ```csharp
  case CalibrationStep.AwaitCentre:
      Interlocked.Exchange(ref _accumX, 0);
      Interlocked.Exchange(ref _accumY, 0);
      Step = CalibrationStep.AwaitRightLimit;
      RestartTimer();
      return (Step, "Centre set. Look fully RIGHT until the view stops, then tap Mark.", null);

  case CalibrationStep.AwaitRightLimit:
      long half = Math.Abs(Interlocked.Read(ref _accumX));
      if (half < MinValidSweepCounts)
      {
          _idleTimer.Stop();
          Step = CalibrationStep.Failed;
          return (Step, "That sweep was too small. Try again — hold freelook and turn all the way to the limit.", null);
      }
      _halfSweep = half;                       // keep; do NOT re-zero accumulators
      Step = CalibrationStep.AwaitCorner;
      RestartTimer();
      return (Step, "Right limit set. Now look to the far corner — as far up and to one side as the game allows — then tap Mark.", null);

  case CalibrationStep.AwaitCorner:
      _idleTimer.Stop();
      long cx = Interlocked.Read(ref _accumX);
      long cy = Interlocked.Read(ref _accumY);
      if (Math.Sqrt((double)cx * cx + (double)cy * cy) < MinValidSweepCounts)
      {
          Step = CalibrationStep.Failed;
          return (Step, "That corner was too close to centre. Try again.", null);
      }
      Step = CalibrationStep.Completed;
      return (Step, "Corner set. Calibration complete.",
              new CalibrationResult(_halfSweep, cx, cy));
  ```

- `Abort()` message unchanged (`"Calibration cancelled."`); idle-timeout
  message unchanged (`"Calibration timed out."`).
- Everything else (the `Raise` helper, lock discipline, `Dispose`)
  unchanged, other than the tuple now carrying `CalibrationResult?` in
  the third slot as it already does.

- [ ] **Step 4: Run CalibrationSession tests — expect pass**

- [ ] **Step 5: Run full Core suite — expect pass**

- [ ] **Step 6: Commit**

```bash
git add src/LogiBuddy.Core/Tracking/CalibrationSession.cs tests/LogiBuddy.Core.Tests/Tracking/CalibrationSessionTests.cs
git commit -m "$(cat <<'EOF'
feat: CalibrationSession — centre/right/corner marks, X+Y accumulators

Three marks now: centre establishes zero, right gives the horizontal
half-sweep, corner gives the far up-and-side offset. Result carries
HalfSweepCounts + CornerXCounts + CornerYCounts, all centre-relative.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

### Task 4: `CalibrationAnnouncer` (replaces `CalibrationCuePlayer`)

**Files:**
- Create: `src/LogiBuddy.App/Audio/CalibrationAnnouncer.cs`
- Delete: `src/LogiBuddy.App/Audio/CalibrationCuePlayer.cs`

**Interfaces:**
- Consumes: `System.Speech.Synthesis.SpeechSynthesizer`; NAudio `WaveFileReader`, `WasapiOut`, `WaveOutEvent`; `NAudio.CoreAudioApi.MMDeviceEnumerator`.
- Produces:
  ```csharp
  public sealed class CalibrationAnnouncer : IDisposable
  {
      public CalibrationAnnouncer(Func<string?> outputDeviceIdProvider);
      public void Say(string text);   // fire-and-forget; never throws
      public void Dispose();
  }
  ```

- [ ] **Step 1: Implement**

```csharp
using System.Speech.Synthesis;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LogiBuddy.App.Audio;

/// Speaks short calibration status phrases through the configured radio
/// output device. Fire-and-forget; any failure (no voice, no device) is
/// swallowed so it can never break calibration.
public sealed class CalibrationAnnouncer : IDisposable
{
    private readonly Func<string?> _deviceIdProvider;
    private readonly SpeechSynthesizer _synth = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public CalibrationAnnouncer(Func<string?> outputDeviceIdProvider)
    {
        _deviceIdProvider = outputDeviceIdProvider;
        try { _synth.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet); } catch { }
    }

    public void Say(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return;
        _ = Task.Run(() => SpeakToDevice(text));
    }

    private void SpeakToDevice(string text)
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(5))) return;
        try
        {
            using var wav = new MemoryStream();
            lock (_synth)
            {
                _synth.SetOutputToWaveStream(wav);
                _synth.Speak(text);
                _synth.SetOutputToNull();
            }
            wav.Position = 0;

            using var reader = new WaveFileReader(wav);
            using var player = CreatePlayer();
            using var done = new ManualResetEventSlim(false);
            player.PlaybackStopped += (_, _) => done.Set();
            player.Init(reader);
            player.Play();
            done.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // An unspoken cue must never break calibration.
        }
        finally
        {
            _gate.Release();
        }
    }

    private IWavePlayer CreatePlayer()
    {
        string? id = _deviceIdProvider();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                using var mm = new MMDeviceEnumerator();
                foreach (var d in mm.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (d.ID == id) return new WasapiOut(d, AudioClientShareMode.Shared, false, 120);
                    d.Dispose();
                }
            }
            catch { }
        }
        return new WaveOutEvent();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _synth.Dispose(); } catch { }
    }
}
```

> `WasapiOut` takes ownership of the `MMDevice` and disposes it — do not
> also dispose the matched device (note the loop only disposes the
> non-matches). If the analyzer flags the un-disposed `mm` enumerator,
> wrap it in `using` as shown; it is fine to dispose the enumerator once
> the `MMDevice` handed to `WasapiOut` has been retained.

- [ ] **Step 2: Delete `CalibrationCuePlayer.cs`**

```bash
git rm src/LogiBuddy.App/Audio/CalibrationCuePlayer.cs
```

(Task 5 removes its last references; the project will not build green
until then — that is expected between these two tasks. Do Task 4 + Task 5
before running the App build.)

- [ ] **Step 3: Commit (allowing the interim non-building App)**

```bash
git add src/LogiBuddy.App/Audio/CalibrationAnnouncer.cs
git commit -m "$(cat <<'EOF'
feat: add CalibrationAnnouncer (System.Speech TTS to the configured device)

Renders each phrase to an in-memory WAV via System.Speech, then plays it
through NAudio — WasapiOut on Profile.OutputDeviceId when it names an
active render endpoint, else the default WaveOutEvent. Replaces
CalibrationCuePlayer, whose sub-latency WaveOutEvent buffers never played.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

### Task 5: Wire the new flow into `MainViewModel` and the window

**Files:**
- Modify: `src/LogiBuddy.App/ViewModels/MainViewModel.cs`
- Modify: `src/LogiBuddy.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `CalibrationAnnouncer` (Task 4), `CalibrationStep` / `CalibrationResult` (Task 3), `RadioProfile.MeasuredMaxOffAxisDegrees` (Task 1).

- [ ] **Step 1: Swap the announcer in**

In `MainViewModel.cs`:

- Replace `private readonly CalibrationCuePlayer _cuePlayer = new();` with
  `private readonly CalibrationAnnouncer _announcer;`
- In the constructor (near the other command setup), add:
  `_announcer = new CalibrationAnnouncer(() => Profile.OutputDeviceId);`
- In `Cleanup()`, replace `_cuePlayer.Dispose();` with `_announcer.Dispose();`

- [ ] **Step 2: Update `StartOrAbortCalibration()` for three steps**

Replace the `session.StepChanged` / `session.Completed` / `session.Ended`
handlers with:

```csharp
        session.StepChanged += (step, message) => Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusMessage = message;
            _announcer.Say(StepPhrase(step, message));
        });
        session.Completed += result => Application.Current?.Dispatcher.Invoke(() =>
        {
            float sens = Profile.MaxYawDegrees / result.HalfSweepCounts;
            float cornerYawDeg = MathF.Abs(result.CornerXCounts) * sens;
            float cornerPitchDeg = MathF.Abs(result.CornerYCounts) * sens;

            Profile.MouseSensitivity = sens;
            Profile.MeasuredYawSweepCounts = result.HalfSweepCounts;
            if (cornerPitchDeg > 1f) Profile.MaxPitchDegrees = cornerPitchDeg;
            Profile.MeasuredMaxOffAxisDegrees =
                MathF.Sqrt(cornerYawDeg * cornerYawDeg + cornerPitchDeg * cornerPitchDeg);

            _announcer.Say("Corner set. Calibration complete.");
            StatusMessage =
                $"Calibration complete — sensitivity {sens:0.####}, max pitch {Profile.MaxPitchDegrees:0.#}°, " +
                $"off-axis limit {Profile.MeasuredMaxOffAxisDegrees:0.#}°. Click Save Profile to keep it.";
            TeardownCalibration();
        });
        session.Ended += reason => Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusMessage = reason;
            _announcer.Say(reason);
            TeardownCalibration();
        });
```

Add a small helper next to `TeardownCalibration()`:

```csharp
    private static string StepPhrase(CalibrationStep step, string fallbackMessage) => step switch
    {
        CalibrationStep.AwaitCentre => "Calibration started. Face forward and tap to set centre.",
        CalibrationStep.AwaitRightLimit => "Centre set. Now look fully right and tap.",
        CalibrationStep.AwaitCorner => "Right limit set. Now look to the far corner and tap.",
        _ => fallbackMessage,
    };
```

(`Completed` and `Ended` also fire `StepChanged`; speaking the fallback
message for those is fine, but `Completed`'s dedicated
`_announcer.Say("Corner set…")` will already have been queued — acceptable
double, or guard `StepPhrase` to return `""` for `Completed`/`Failed`/
`Aborted` and rely on the `Completed`/`Ended` handlers. Prefer the guard:
add `CalibrationStep.Completed or CalibrationStep.Failed or CalibrationStep.Aborted => ""` and have `Say` ignore empty — it already does.)

- [ ] **Step 3: Off-axis rescale on `MaxYawDegrees` change**

Replace the existing `MaxYawDegrees` re-derive block (added last round,
just before `if (LiveProfileProperties.Contains(...))`) with:

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

- [ ] **Step 4: Remove the Spring-back slider row from `MainWindow.xaml`**

Delete the `StackPanel Grid.Row="19"` block ("Spring-back (°/s):").
Remove one `<RowDefinition>` from the grid. Shift the rows after it: the
calibration row `20→19`, buttons `21→20`, canvas `22→21`, status
`23→22` — updating each `<!-- N ... -->` comment and `Grid.Row="N"`.
Set the window `Height="960"` (was 990).

- [ ] **Step 5: Build the solution — expect success**

`dotnet build LogiBuddy.sln -c Debug --nologo`
Expected: 0 errors, 0 warnings. `System.Speech.dll` present under
`src/LogiBuddy.App/bin/Debug/net8.0-windows/`.

- [ ] **Step 6: Run the full test suite**

`dotnet test LogiBuddy.sln --nologo` — expect PASS.

- [ ] **Step 7: Launch smoke test**

`dotnet run --project src/LogiBuddy.App/LogiBuddy.App.csproj`
Expected: window opens, no Spring-back row, status line not clipped, the
"Calibrate hotkey:" row still present.

- [ ] **Step 8: Manual verification (the testable stage)**

1. Start with a source playing; bind freelook + calibrate hotkeys.
2. Click "Calibrate freelook" → **hear** "Calibration started…". Tap →
   "Centre set…". Move the mouse right, tap → "Right limit set…". Move to
   a corner, tap → "Corner set. Calibration complete." Status shows the
   summary.
3. Set the output device to a non-default endpoint; recalibrate; confirm
   the speech comes from that device.
4. `Mouse sensitivity` and `Max pitch (°)` moved; Save Profile, restart,
   Start → persisted (including the off-axis limit — check the summary
   on a fresh calibrate, or a debug inspect).
5. Hold freelook, sweep around including past the limits, release → the
   audio image snaps to forward at once.
6. Turn while pitched up/down → the image no longer swings past where the
   game view can go.
7. Two marks with no movement between right and corner → "That corner was
   too close to centre…"; profile unchanged. Cancel via the button →
   "Calibration cancelled."
8. `Freelook always on` ticked: move, then hold the mouse still ~1 s →
   image eases back to centre.

- [ ] **Step 9: Commit**

```bash
git add src/LogiBuddy.App/ViewModels/MainViewModel.cs src/LogiBuddy.App/MainWindow.xaml
git commit -m "$(cat <<'EOF'
feat: wire 3-mark calibration, spoken cues, and cone limit into the VM/window

CalibrateFreelook now runs centre/right/corner, speaks each step via
CalibrationAnnouncer through the configured device, and on completion
writes MouseSensitivity + MaxPitchDegrees + MeasuredMaxOffAxisDegrees
(+ MeasuredYawSweepCounts). MaxYawDegrees changes rescale the off-axis
limit too. Spring-back slider removed from the window.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

## Self-Review

**Spec coverage:**

| Spec element | Task |
| --- | --- |
| `MeasuredMaxOffAxisDegrees` field, default 0, round-trip | Task 1 |
| Snap to forward on key release (hold-to-look) | Task 2 (`Update`, `Update_OnHotkeyRelease_SnapsToForwardImmediately`) |
| Off-axis clamp `√(yaw²+pitch²)` when measured > 0, ratio-preserving | Task 2 (`ApplyOffAxisClamp`, two clamp tests) |
| Per-axis clamps still apply | Task 2 (unchanged `Clamp` calls; `Yaw_IsClampedToMaxYawDegrees` retained) |
| Always-on idle ease after ~1 s, at `SpringBackRatePerSecond` | Task 2 (`Update` always-on branch, 3 always-on tests) |
| `SpringBackRatePerSecond` field kept, slider removed | Task 1 (field untouched) + Task 5 Step 4 |
| Calibration marks centre/right/corner | Task 3 (enum, `Mark` switch, `CentreMark_ReZeroesAccumulators`) |
| X + Y accumulators | Task 3 (`_accumX`/`_accumY`, `OnMouseMoved`) |
| `CalibrationResult(HalfSweepCounts, CornerXCounts, CornerYCounts)` | Task 3 |
| Sweep/corner `< 50` guards | Task 3 (`RightSweepTooSmall_Fails`, `CornerTooCloseToCentre_Fails`) |
| TTS → WAV → NAudio to `Profile.OutputDeviceId` else default | Task 4 (`SpeakToDevice`, `CreatePlayer`) |
| Announce every step incl. start; never throw | Task 4 + Task 5 Step 2 (`StepPhrase`, swallowing `catch`) |
| `System.Speech` package | committed with the spec; verified restoring |
| VM derives sensitivity + `MaxPitchDegrees` + off-axis from corner | Task 5 Step 2 |
| `MaxYawDegrees` change rescales off-axis limit | Task 5 Step 3 |
| Window: remove spring-back row, renumber, height | Task 5 Step 4 |
| Manual verification | Task 5 Step 8 |

No gaps.

**Placeholder scan:** none. Task 4 Step 2's "project won't build until
Task 5" is a stated, bounded interim, not deferred work.

**Type/name consistency:** `CalibrationStep` members
(`Idle, AwaitCentre, AwaitRightLimit, AwaitCorner, Completed, Failed, Aborted`)
identical across Task 3 code, Task 3 tests, and Task 5's `StepPhrase`.
`CalibrationResult(HalfSweepCounts, CornerXCounts, CornerYCounts)`
identical in Task 3 record, Task 3 tests, Task 5 consumption.
`CalibrationAnnouncer(Func<string?>)` / `Say` / `Dispose` identical in
Task 4 and Task 5. `_announcer` replaces `_cuePlayer` everywhere in
Task 5. `MeasuredMaxOffAxisDegrees` spelled the same in Task 1, Task 2,
Task 5.
