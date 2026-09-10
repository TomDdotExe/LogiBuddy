# Freelook Calibration Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a guided in-game calibration that measures the player's horizontal mouse sweep between the game's view limits and derives `MouseSensitivity` so the app's yaw clamp lines up with the game's.

**Architecture:** A pure `CalibrationSession` state machine (Core) subscribes to the live `IMouseInputSource`, sums `dx` while the freelook key is held, and advances on `Mark()` calls fed by a global tap-hotkey. On completion `MainViewModel` writes `MouseSensitivity` and a persisted `MeasuredYawSweepCounts` onto the profile. A small `CalibrationCuePlayer` gives audio feedback since the game has focus.

**Tech Stack:** C# / .NET 8, WPF (MVVM), xUnit, NAudio (`SignalGenerator`, `WaveOutEvent`/`WasapiOut`).

**Spec:** `docs/superpowers/specs/2026-09-10-freelook-calibration-design.md`

## Global Constraints

- **Yaw only.** Pitch is not touched (not audible in the spatializer today).
- **Only while running.** Calibration reuses the live `Win32MouseHook` created by `Start()`; the calibrate command is disabled when `_pipeline`/`_mouseHook` is null.
- **Mapping (Approach A):** `MouseSensitivity = MaxYawDegrees / MeasuredHalfSweepCounts`. The existing `±MaxYawDegrees` clamp in `FreelookTracker` is unchanged. `MaxYawDegrees` stays the user's audio-swing knob.
- **Two marks, no centre reference.** Mark 1 at one view limit re-zeroes the accumulator; mark 2 at the other limit gives the full sweep `C`; `MeasuredHalfSweepCounts = C / 2`.
- **Sweep guard:** full sweep `C < 50` counts → `Failed`, nothing written to the profile.
- **Idle timeout:** 60 s with no `Mark()` → `Failed`.
- **Unbound hotkey convention:** `VirtualKeyCode == 0` means unbound, exactly like `RecenterHotkey` / `VehicleToggleHotkey`.
- `FreelookTracker` must not change.
- Follow existing patterns: `SetField` properties, `RelayCommand(exec, canExec)`, hotkey events marshalled with `Application.Current?.Dispatcher.Invoke`, watchers held as nullable fields and disposed on `Stop()`.

---

### Task 1: `RadioProfile` — `CalibrateHotkey` and `MeasuredYawSweepCounts`

**Files:**
- Modify: `src/SpotifyGameRadio.Core/Config/RadioProfile.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Config/RadioProfileTests.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: `FreelookHotkey`, the existing `SetField<T>` helper.
- Produces: `RadioProfile.CalibrateHotkey` (`FreelookHotkey`, default `VirtualKeyCode = 0`) and `RadioProfile.MeasuredYawSweepCounts` (`float`, default `0f`), both raising `PropertyChanged` via `SetField`.

- [ ] **Step 1: Add the failing profile tests**

In `RadioProfileTests.cs` add:

```csharp
[Fact]
public void NewCalibrationFields_HaveExpectedDefaults()
{
    var profile = new RadioProfile();

    Assert.Equal(0, profile.CalibrateHotkey.VirtualKeyCode); // unbound
    Assert.Equal(0f, profile.MeasuredYawSweepCounts);
}

[Fact]
public void AssigningCalibrateHotkey_RaisesPropertyChanged()
{
    var profile = new RadioProfile();
    var changed = new List<string?>();
    profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

    profile.CalibrateHotkey = new FreelookHotkey { VirtualKeyCode = 0x4F };
    profile.MeasuredYawSweepCounts = 3200f;

    Assert.Contains(nameof(RadioProfile.CalibrateHotkey), changed);
    Assert.Contains(nameof(RadioProfile.MeasuredYawSweepCounts), changed);
}
```

- [ ] **Step 2: Run the tests, expect compile failure**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests --filter FullyQualifiedName~RadioProfileTests`
Expected: build error — `RadioProfile` has no `CalibrateHotkey` / `MeasuredYawSweepCounts`.

- [ ] **Step 3: Add the fields**

In `RadioProfile.cs`, add backing fields alongside the others (near `_autoMuteSource`):

```csharp
private FreelookHotkey _calibrateHotkey = new() { VirtualKeyCode = 0 };
private float _measuredYawSweepCounts = 0f;
```

and the properties near `MaxYawDegrees` / after `AutoMuteSource`:

```csharp
/// Global tap hotkey used to mark a view limit during freelook
/// calibration. VirtualKeyCode 0 means unbound.
public FreelookHotkey CalibrateHotkey { get => _calibrateHotkey; set => SetField(ref _calibrateHotkey, value); }

/// Mouse counts from centre to a view limit, captured by calibration.
/// 0 means "never calibrated". When > 0, MainViewModel re-derives
/// MouseSensitivity from it whenever MaxYawDegrees changes.
public float MeasuredYawSweepCounts { get => _measuredYawSweepCounts; set => SetField(ref _measuredYawSweepCounts, value); }
```

- [ ] **Step 4: Run the profile tests, expect pass**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests --filter FullyQualifiedName~RadioProfileTests`
Expected: PASS.

- [ ] **Step 5: Extend the ConfigStore round-trip tests**

In `ConfigStoreTests.cs`, in `SaveThenLoad_RoundTripsAllFields`, add to the `new RadioProfile { ... }` initialiser:

```csharp
                CalibrateHotkey = new FreelookHotkey { VirtualKeyCode = 0x4F },
                MeasuredYawSweepCounts = 3600f,
```

and add assertions before the `finally`:

```csharp
            Assert.Equal(0x4F, loaded.CalibrateHotkey.VirtualKeyCode);
            Assert.Equal(3600f, loaded.MeasuredYawSweepCounts);
```

In `Load_ProfileJsonMissingNewerFields_KeepsDefaults`, add:

```csharp
            Assert.Equal(0, loaded.CalibrateHotkey.VirtualKeyCode);   // default retained (unbound)
            Assert.Equal(0f, loaded.MeasuredYawSweepCounts);          // default retained
```

- [ ] **Step 6: Run the full Core test suite, expect pass**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests --nologo`
Expected: PASS, count up by the new facts.

- [ ] **Step 7: Commit**

```bash
git add src/SpotifyGameRadio.Core/Config/RadioProfile.cs tests/SpotifyGameRadio.Core.Tests/Config/RadioProfileTests.cs tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs
git commit -m "$(cat <<'EOF'
feat: add CalibrateHotkey and MeasuredYawSweepCounts to RadioProfile

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

### Task 2: `CalibrationSession` state machine

**Files:**
- Create: `src/SpotifyGameRadio.Core/Tracking/CalibrationSession.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Tracking/CalibrationSessionTests.cs`

**Interfaces:**
- Consumes: `IMouseInputSource` (`bool IsHotkeyHeld`, `event Action<int,int>? MouseMoved`). The test double `FakeMouseInputSource` already exists in `tests/SpotifyGameRadio.Core.Tests/Tracking/FreelookTrackerTests.cs` (same namespace `SpotifyGameRadio.Core.Tests.Tracking`): `{ bool IsHotkeyHeld; void RaiseMove(int dx, int dy); }` — reuse it, do not redefine.
- Produces:
  - `enum CalibrationStep { Idle, AwaitLeftLimit, AwaitRightLimit, Completed, Failed, Aborted }`
  - `sealed record CalibrationResult(float MeasuredHalfSweepCounts)`
  - `sealed class CalibrationSession : IDisposable` with:
    - `CalibrationSession(IMouseInputSource input)` — 60 s idle timeout
    - `CalibrationSession(IMouseInputSource input, TimeSpan idleTimeout)` — test ctor
    - `CalibrationStep Step { get; }`
    - `event Action<CalibrationStep, string>? StepChanged`
    - `event Action<CalibrationResult>? Completed`
    - `event Action<string>? Ended` (fires on `Failed` and `Aborted`, with the reason)
    - `void Start()`, `void Mark()`, `void Abort()`, `void Dispose()`

- [ ] **Step 1: Write `CalibrationSessionTests.cs` (failing)**

```csharp
using System;
using SpotifyGameRadio.Core.Tracking;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Tracking;

public class CalibrationSessionTests
{
    private static CalibrationSession NewSession(FakeMouseInputSource input) =>
        new(input, TimeSpan.FromMinutes(5)); // effectively no timeout for these tests

    [Fact]
    public void Start_MovesToAwaitLeftLimit_AndAnnouncesStep()
    {
        var input = new FakeMouseInputSource();
        using var session = NewSession(input);
        CalibrationStep? announced = null;
        session.StepChanged += (s, _) => announced = s;

        session.Start();

        Assert.Equal(CalibrationStep.AwaitLeftLimit, session.Step);
        Assert.Equal(CalibrationStep.AwaitLeftLimit, announced);
    }

    [Fact]
    public void Movement_WhileHotkeyNotHeld_IsIgnored()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        using var session = NewSession(input);
        session.Start();

        input.RaiseMove(-2000, 0);
        input.RaiseMove(-2000, 0);
        session.Mark(); // left limit "captured" as 0

        input.IsHotkeyHeld = true;
        input.RaiseMove(9000, 0);
        session.Mark();

        // Only the 9000 counts after the key went down matter: half = 4500.
        CalibrationResult? result = null;
        // (re-run pattern below in the happy-path test; here just assert state)
        Assert.Equal(CalibrationStep.Completed, session.Step);
    }

    [Fact]
    public void HappyPath_TwoMarks_CompletesWithHalfSweep()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        CalibrationResult? result = null;
        session.Completed += r => result = r;

        session.Start();
        input.RaiseMove(-1500, 0);
        input.RaiseMove(-2500, 0);   // accumulator now -4000
        session.Mark();              // -> AwaitRightLimit, accumulator re-zeroed
        Assert.Equal(CalibrationStep.AwaitRightLimit, session.Step);

        input.RaiseMove(3000, 0);
        input.RaiseMove(5000, 0);    // accumulator now +8000
        session.Mark();              // -> Completed

        Assert.Equal(CalibrationStep.Completed, session.Step);
        Assert.NotNull(result);
        Assert.Equal(4000f, result!.MeasuredHalfSweepCounts, precision: 3);
    }

    [Fact]
    public void SweepTooSmall_Fails_WithReason_AndNoResult()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        var completed = false;
        string? endedReason = null;
        session.Completed += _ => completed = true;
        session.Ended += r => endedReason = r;

        session.Start();
        input.RaiseMove(10, 0);
        session.Mark();
        input.RaiseMove(20, 0);      // full sweep only 20 counts
        session.Mark();

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.False(completed);
        Assert.False(string.IsNullOrWhiteSpace(endedReason));
    }

    [Fact]
    public void Abort_FromAwaitRightLimit_EndsAndIgnoresLaterMarks()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        string? endedReason = null;
        session.Ended += r => endedReason = r;

        session.Start();
        input.RaiseMove(-5000, 0);
        session.Mark();              // AwaitRightLimit
        session.Abort();

        Assert.Equal(CalibrationStep.Aborted, session.Step);
        Assert.False(string.IsNullOrWhiteSpace(endedReason));

        input.RaiseMove(9999, 0);
        session.Mark();              // no-op
        Assert.Equal(CalibrationStep.Aborted, session.Step);
    }

    [Fact]
    public void Mark_AfterCompleted_IsNoOp()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = NewSession(input);
        session.Start();
        input.RaiseMove(-4000, 0); session.Mark();
        input.RaiseMove(8000, 0); session.Mark();

        var stepAfter = session.Step;
        session.Mark();
        Assert.Equal(stepAfter, session.Step);
    }

    [Fact]
    public void Dispose_Unsubscribes_FurtherMovementIsInert()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var session = NewSession(input);
        session.Start();
        session.Dispose();

        var ex = Record.Exception(() => input.RaiseMove(5000, 0));
        Assert.Null(ex);
        Assert.Equal(CalibrationStep.Idle, session.Step); // never advanced past Start's transition? see note
    }

    [Fact]
    public void IdleTimeout_Fails()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        using var session = new CalibrationSession(input, TimeSpan.FromMilliseconds(80));
        string? endedReason = null;
        session.Ended += r => endedReason = r;

        session.Start();
        System.Threading.Thread.Sleep(250); // the one place a sleep is unavoidable

        Assert.Equal(CalibrationStep.Failed, session.Step);
        Assert.Contains("timed out", endedReason, StringComparison.OrdinalIgnoreCase);
    }
}
```

> Note on `Dispose_Unsubscribes...`: after `Start()` the step is `AwaitLeftLimit`. Assert `session.Step == CalibrationStep.AwaitLeftLimit` (not `Idle`) and that raising movement afterwards does not throw and does not change `Step`. Fix that assertion when you write the test.

- [ ] **Step 2: Run tests, expect compile failure**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests --filter FullyQualifiedName~CalibrationSessionTests`
Expected: build error — `CalibrationSession` does not exist.

- [ ] **Step 3: Implement `CalibrationSession.cs`**

```csharp
using System.Timers;
using Timer = System.Timers.Timer;

namespace SpotifyGameRadio.Core.Tracking;

public enum CalibrationStep { Idle, AwaitLeftLimit, AwaitRightLimit, Completed, Failed, Aborted }

public sealed record CalibrationResult(float MeasuredHalfSweepCounts);

/// Guided freelook calibration. Subscribes to a live mouse input source,
/// sums horizontal counts while the freelook key is held, and advances on
/// Mark() calls (driven by a global tap-hotkey). Two marks — one at each
/// game view limit — yield the full sweep; half of it is the centre-to-limit
/// count the view model turns into MouseSensitivity.
public sealed class CalibrationSession : IDisposable
{
    private const long MinValidSweepCounts = 50;

    private readonly IMouseInputSource _input;
    private readonly Timer _idleTimer;
    private readonly object _gate = new();

    private long _accum;
    private bool _disposed;

    public CalibrationStep Step { get; private set; } = CalibrationStep.Idle;

    public event Action<CalibrationStep, string>? StepChanged;
    public event Action<CalibrationResult>? Completed;
    public event Action<string>? Ended;

    public CalibrationSession(IMouseInputSource input)
        : this(input, TimeSpan.FromSeconds(60)) { }

    public CalibrationSession(IMouseInputSource input, TimeSpan idleTimeout)
    {
        _input = input;
        _input.MouseMoved += OnMouseMoved;
        _idleTimer = new Timer(idleTimeout.TotalMilliseconds) { AutoReset = false };
        _idleTimer.Elapsed += OnIdleTimeout;
    }

    private bool IsActive => Step is CalibrationStep.AwaitLeftLimit or CalibrationStep.AwaitRightLimit;

    private void OnMouseMoved(int dx, int dy)
    {
        // Cheap unsynchronised gate; the Interlocked.Add is the real guard.
        if (!IsActive || !_input.IsHotkeyHeld) return;
        Interlocked.Add(ref _accum, dx);
    }

    public void Start()
    {
        Raise(() =>
        {
            lock (_gate)
            {
                if (Step != CalibrationStep.Idle) return null;
                Interlocked.Exchange(ref _accum, 0);
                Step = CalibrationStep.AwaitLeftLimit;
                RestartTimer();
                return (Step, "Hold freelook, look fully LEFT until the view stops, then tap Mark.");
            }
        });
    }

    public void Mark()
    {
        Raise(() =>
        {
            lock (_gate)
            {
                switch (Step)
                {
                    case CalibrationStep.AwaitLeftLimit:
                        Interlocked.Exchange(ref _accum, 0);
                        Step = CalibrationStep.AwaitRightLimit;
                        RestartTimer();
                        return (Step, "Now look fully RIGHT until the view stops, then tap Mark.");

                    case CalibrationStep.AwaitRightLimit:
                        _idleTimer.Stop();
                        long sweep = Math.Abs(Interlocked.Read(ref _accum));
                        if (sweep < MinValidSweepCounts)
                        {
                            Step = CalibrationStep.Failed;
                            return (Step, "Calibration failed: the sweep was too small. Hold the freelook key and turn all the way to each limit.");
                        }
                        Step = CalibrationStep.Completed;
                        _pendingResult = new CalibrationResult(sweep / 2f);
                        return (Step, "Calibration done — sensitivity updated.");

                    default:
                        return null;
                }
            }
        });
    }

    public void Abort()
    {
        Raise(() =>
        {
            lock (_gate)
            {
                if (!IsActive) return null;
                _idleTimer.Stop();
                Step = CalibrationStep.Aborted;
                return (Step, "Calibration cancelled.");
            }
        });
    }

    private void OnIdleTimeout(object? sender, ElapsedEventArgs e)
    {
        Raise(() =>
        {
            lock (_gate)
            {
                if (!IsActive) return null;
                Step = CalibrationStep.Failed;
                return (Step, "Calibration timed out.");
            }
        });
    }

    private void RestartTimer()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    // --- event dispatch -------------------------------------------------

    private CalibrationResult? _pendingResult;

    /// Runs the transition delegate under its own lock, then raises the
    /// resulting events OUTSIDE any lock (handlers may dispose the session).
    private void Raise(Func<(CalibrationStep step, string message)?> transition)
    {
        var outcome = transition();
        if (outcome is null) return;

        var (step, message) = outcome.Value;
        StepChanged?.Invoke(step, message);

        switch (step)
        {
            case CalibrationStep.Completed:
                var r = _pendingResult;
                if (r is not null) Completed?.Invoke(r);
                break;
            case CalibrationStep.Failed:
            case CalibrationStep.Aborted:
                Ended?.Invoke(message);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _input.MouseMoved -= OnMouseMoved;
        _idleTimer.Elapsed -= OnIdleTimeout;
        _idleTimer.Dispose();
    }
}
```

- [ ] **Step 4: Run the calibration tests, expect pass**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests --filter FullyQualifiedName~CalibrationSessionTests`
Expected: PASS (all facts, including `IdleTimeout_Fails`).

Fix the two test-body notes as you go:
- `Movement_WhileHotkeyNotHeld_IsIgnored`: after the second `Mark()` assert `session.Step == CalibrationStep.Completed` and capture `Completed` to assert `MeasuredHalfSweepCounts == 4500f`.
- `Dispose_Unsubscribes_FurtherMovementIsInert`: assert `session.Step == CalibrationStep.AwaitLeftLimit` after `Start()` + `Dispose()`, and unchanged after `RaiseMove`.

- [ ] **Step 5: Run the full Core suite, expect pass**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests --nologo`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SpotifyGameRadio.Core/Tracking/CalibrationSession.cs tests/SpotifyGameRadio.Core.Tests/Tracking/CalibrationSessionTests.cs
git commit -m "$(cat <<'EOF'
feat: add CalibrationSession — measures freelook sweep between game view limits

Two-mark state machine over IMouseInputSource: sums dx while the freelook
key is held, re-zeroes on the first mark, and on the second returns the
half-sweep count. Sweep-too-small and 60s idle both fail without a result.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

### Task 3: `CalibrationCuePlayer`

**Files:**
- Create: `src/SpotifyGameRadio.App/Audio/CalibrationCuePlayer.cs`
- Reference: `src/SpotifyGameRadio.App/Audio/TestTonePlayer.cs` (pattern to mirror)

**Interfaces:**
- Consumes: NAudio `SignalGenerator`, `OffsetSampleProvider`, `FadeInOutSampleProvider`, `WaveOutEvent`; `SpotifyGameRadio.Core.Audio.RenderDeviceEnumerator` only if resolving a device by id is trivial — otherwise default endpoint is acceptable for this pass (note the deviation in the commit).
- Produces: `sealed class CalibrationCuePlayer : IDisposable` with `void Captured()`, `void Done()`, `void Failed()`. Each is fire-and-forget, overlap-safe, and never throws out to the caller (swallow device errors — a missing beep must not break calibration).

- [ ] **Step 1: Implement the cue player**

Mirror `TestTonePlayer`'s structure (own `WaveOutEvent`, fade to avoid clicks, `lock`, `_disposed` guard). Play short bursts:

```csharp
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpotifyGameRadio.App.Audio;

/// Short audio cues for freelook calibration — the game has focus, so the
/// user needs to hear progress, not see it. Fire-and-forget; a failed cue
/// (no device, etc.) is swallowed so it can never break calibration.
public sealed class CalibrationCuePlayer : IDisposable
{
    private readonly object _lock = new();
    private WaveOutEvent? _output;
    private bool _disposed;

    /// One 880 Hz blip: a limit was marked, the next prompt is showing.
    public void Captured() => Play(new[] { (880.0, 120) });

    /// Rising 660/880/1175 Hz: calibration finished and the profile updated.
    public void Done() => Play(new[] { (660.0, 90), (880.0, 90), (1175.0, 140) });

    /// One low 300 Hz blip: timed out, cancelled, or the sweep was too small.
    public void Failed() => Play(new[] { (300.0, 250) });

    private void Play((double freqHz, int ms)[] segments)
    {
        try
        {
            lock (_lock)
            {
                if (_disposed) return;

                ISampleProvider chain = BuildChain(segments);
                _output?.Dispose();
                _output = new WaveOutEvent();
                _output.Init(chain);
                _output.Play();
            }
        }
        catch
        {
            // A missing beep must never break calibration.
        }
    }

    private static ISampleProvider BuildChain((double freqHz, int ms)[] segments)
    {
        const int rate = 48000;
        ISampleProvider? chain = null;
        foreach (var (freq, ms) in segments)
        {
            var tone = new SignalGenerator(rate, 1)
            {
                Type = SignalGeneratorType.Sin,
                Frequency = freq,
                Gain = 0.18,
            };
            var seg = new OffsetSampleProvider(tone) { TakeSamples = rate * ms / 1000 };
            var faded = new FadeInOutSampleProvider(seg, initiallySilent: true);
            faded.BeginFadeIn(15);
            faded.BeginFadeOut(Math.Max(1, ms - 15));
            chain = chain is null ? faded : Concat(chain, faded);
        }
        return chain!;
    }

    private static ISampleProvider Concat(ISampleProvider a, ISampleProvider b) =>
        new ConcatenatingSampleProvider(new[] { a, b });

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _output?.Dispose();
            _output = null;
        }
    }
}
```

> If `ConcatenatingSampleProvider` or `FadeInOutSampleProvider` timing proves fiddly, a simpler acceptable fallback: build one `SignalGenerator` per segment, `OffsetSampleProvider` with `TakeSamples`, wrap the sequence in `ConcatenatingSampleProvider`, skip the per-segment fade and instead give the whole chain a single `FadeInOutSampleProvider`. The requirement is: no click, distinct-sounding cues, never throws.

- [ ] **Step 2: Build the App project, expect success**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj -c Debug --nologo`
Expected: `Build succeeded`, 0 errors. Resolve any NAudio API mismatch (`ConcatenatingSampleProvider` is in `NAudio.Wave.SampleProviders`).

- [ ] **Step 3: Commit**

```bash
git add src/SpotifyGameRadio.App/Audio/CalibrationCuePlayer.cs
git commit -m "$(cat <<'EOF'
feat: add CalibrationCuePlayer — captured / done / failed audio cues

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

### Task 4: Wire calibration into `MainViewModel` and the window

**Files:**
- Modify: `src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs`
- Modify: `src/SpotifyGameRadio.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `CalibrationSession`, `CalibrationResult`, `CalibrationStep` (Core.Tracking); `CalibrationCuePlayer` (App.Audio); `TapHotkeyWatcher`, `Win32KeyStateSource`, `Win32MouseHook` (already used here); `RadioProfile.CalibrateHotkey` / `.MeasuredYawSweepCounts` (Task 1).
- Produces: `MainViewModel.CalibrateFreelookCommand` (`ICommand`); a new hotkey/button row in `MainWindow.xaml`.

- [ ] **Step 1: Add fields and the command**

In `MainViewModel.cs`, near `_recenterHotkeyWatcher` / `_vehicleToggleHotkeyWatcher`:

```csharp
private CalibrationSession? _calibrationSession;
private TapHotkeyWatcher? _calibrateMarkWatcher;
private readonly CalibrationCuePlayer _cuePlayer = new();
```

Add the property near `RecenterCommand`:

```csharp
public ICommand CalibrateFreelookCommand { get; }
```

In the constructor, next to `RecenterCommand = ...`:

```csharp
CalibrateFreelookCommand = new RelayCommand(
    _ => StartOrAbortCalibration(),
    _ => _pipeline is not null
         && _mouseHook is not null
         && _calibrationSession is null
         && Profile.Hotkey.VirtualKeyCode != 0
         && Profile.CalibrateHotkey.VirtualKeyCode != 0
         && Profile.CalibrateHotkey.VirtualKeyCode != Profile.Hotkey.VirtualKeyCode);
```

> `RelayCommand` requeries on `CommandManager.RequerySuggested`, which fires after UI interactions — the same mechanism `StartCommand`/`StopCommand` rely on. When a session is active the button is simply disabled; cancelling is done from the window by... see Step 2 (the button stays the entry point, we keep the `_calibrationSession is null` guard OUT so it can also abort). Decide one way:
>
> **Chosen:** keep the command enabled during a session so it doubles as Cancel — so DROP `_calibrationSession is null` from `CanExecute`, and `StartOrAbortCalibration()` aborts when a session exists.

Use this `CanExecute` instead (no `_calibrationSession is null` clause):

```csharp
CalibrateFreelookCommand = new RelayCommand(
    _ => StartOrAbortCalibration(),
    _ => _pipeline is not null
         && _mouseHook is not null
         && Profile.Hotkey.VirtualKeyCode != 0
         && Profile.CalibrateHotkey.VirtualKeyCode != 0
         && Profile.CalibrateHotkey.VirtualKeyCode != Profile.Hotkey.VirtualKeyCode);
```

- [ ] **Step 2: Add the calibration lifecycle methods**

Add near `Recenter()`:

```csharp
private void StartOrAbortCalibration()
{
    if (_calibrationSession is not null)
    {
        _calibrationSession.Abort(); // Ended handler tears down
        return;
    }
    if (_pipeline is null || _mouseHook is null) return;

    Recenter();

    var session = new CalibrationSession(_mouseHook);
    var markWatcher = new TapHotkeyWatcher(Profile.CalibrateHotkey, new Win32KeyStateSource());
    markWatcher.Pressed += () => Application.Current?.Dispatcher.Invoke(() => _calibrationSession?.Mark());

    session.StepChanged += (step, message) => Application.Current?.Dispatcher.Invoke(() =>
    {
        StatusMessage = message;
        if (step == CalibrationStep.AwaitRightLimit) _cuePlayer.Captured();
    });
    session.Completed += result => Application.Current?.Dispatcher.Invoke(() =>
    {
        Profile.MeasuredYawSweepCounts = result.MeasuredHalfSweepCounts;
        Profile.MouseSensitivity = Profile.MaxYawDegrees / result.MeasuredHalfSweepCounts;
        _cuePlayer.Done();
        StatusMessage = $"Calibration done — mouse sensitivity set to {Profile.MouseSensitivity:0.####}. Click Save Profile to keep it.";
        TeardownCalibration();
    });
    session.Ended += reason => Application.Current?.Dispatcher.Invoke(() =>
    {
        StatusMessage = reason;
        _cuePlayer.Failed();
        TeardownCalibration();
    });

    _calibrationSession = session;
    _calibrateMarkWatcher = markWatcher;
    session.Start();
}

private void TeardownCalibration()
{
    _calibrateMarkWatcher?.Dispose();
    _calibrateMarkWatcher = null;
    _calibrationSession?.Dispose();
    _calibrationSession = null;
}
```

- [ ] **Step 3: Handle profile changes and shutdown paths**

In `OnProfilePropertyChanged`, add branches (after the `VehicleToggleHotkey` branch):

```csharp
else if (e.PropertyName == nameof(RadioProfile.CalibrateHotkey))
{
    _calibrateMarkWatcher?.SetHotkey(Profile.CalibrateHotkey);
}
else if (e.PropertyName == nameof(RadioProfile.MaxYawDegrees)
         && Profile.MeasuredYawSweepCounts > 0f)
{
    Profile.MouseSensitivity = Profile.MaxYawDegrees / Profile.MeasuredYawSweepCounts;
}
```

> `MaxYawDegrees` is already in `LiveProfileProperties`, so the earlier `if (LiveProfileProperties.Contains(...))` block already ran `ApplyProfile` for this same event. Setting `MouseSensitivity` here raises its own `PropertyChanged`, which re-enters this handler and re-applies. That is fine and terminates (the second pass hits the `LiveProfileProperties` branch only).

In `Stop()`, before `_mouseHook?.Dispose()`:

```csharp
if (_calibrationSession is not null)
{
    _calibrationSession.Abort();
    TeardownCalibration();
}
```

In `LoadProfile()`, at the top (before swapping `Profile`):

```csharp
if (_calibrationSession is not null)
{
    _calibrationSession.Abort();
    TeardownCalibration();
}
```

Where the other `IDisposable`s are cleaned up (the `Dispose`/teardown region near `_mouseHook?.Dispose()`), add `_cuePlayer.Dispose();` — or if `MainViewModel` has no `Dispose`, dispose it in `Stop()` is wrong (it outlives Stop). Leave `_cuePlayer` undisposed for the process lifetime if there is no VM disposal hook; a single `WaveOutEvent` field is acceptable to leak for app lifetime. Match whatever the file already does with long-lived audio objects.

- [ ] **Step 4: Add the window row**

In `MainWindow.xaml`: insert one `RowDefinition` immediately before the buttons row (currently `<!-- 20 buttons + restart hint -->`), and renumber the buttons/canvas/status rows and their `Grid.Row` attributes (`20→21`, `21→22`, `22→23`). Update the row comments to match.

New row (use the current buttons row's index, i.e. `Grid.Row="20"`, and push the buttons StackPanel to `Grid.Row="21"`, canvas to `22`, status to `23`):

```xml
        <StackPanel Grid.Row="20" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Calibrate hotkey:" VerticalAlignment="Center" Width="140" />
            <controls:HotkeyCaptureControl Hotkey="{Binding Profile.CalibrateHotkey, Mode=TwoWay}" />
            <Button Content="Calibrate freelook" Command="{Binding CalibrateFreelookCommand}" Margin="8,0,0,0"
                    ToolTip="While running: hold freelook and sweep to each in-game view limit, tapping the calibrate hotkey at each. Sets mouse sensitivity so the app's yaw range matches the game's. Click again to cancel." />
            <TextBlock Text=" run while playing; needs a freelook key too" VerticalAlignment="Center" Margin="8,0,0,0" Foreground="Gray" />
        </StackPanel>
```

Bump the window height: `Height="950"` → `Height="990"` (line 6).

- [ ] **Step 5: Build and run the app**

Run: `dotnet build SpotifyGameRadio.sln -c Debug --nologo`
Expected: `Build succeeded`, 0 errors/warnings.

Run: `dotnet run --project src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: the window opens, the new "Calibrate hotkey:" row is visible and nothing is clipped above the status line.

- [ ] **Step 6: Manual verification (the testable stage)**

With a source playing:
1. Click Start. "Calibrate freelook" is disabled until a freelook key **and** a distinct calibrate hotkey are both bound; disabled again after Stop.
2. Bind both. Click "Calibrate freelook" → status shows the LEFT prompt.
3. Hold the freelook key, move the mouse left a lot, tap the calibrate hotkey → one 880 Hz blip, status shows the RIGHT prompt.
4. Hold freelook, move right a lot, tap calibrate hotkey → rising three-blip, status shows "Calibration done — mouse sensitivity set to …". Confirm the `Mouse sensitivity` slider moved.
5. Save Profile, restart the app, Start again → the sensitivity and (internally) `MeasuredYawSweepCounts` persisted.
6. Drag `Max yaw (°)` → `Mouse sensitivity` tracks it proportionally.
7. Calibrate, tap the hotkey twice without moving → low blip, "sweep was too small", sensitivity unchanged.
8. Calibrate, then click "Calibrate freelook" again → low blip, "Calibration cancelled".
9. Calibrate, then click Stop → no crash, session gone.

- [ ] **Step 7: Run the full solution test suite**

Run: `dotnet test SpotifyGameRadio.sln --nologo`
Expected: PASS (Core suite green; App has no tests).

- [ ] **Step 8: Commit**

```bash
git add src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs src/SpotifyGameRadio.App/MainWindow.xaml
git commit -m "$(cat <<'EOF'
feat: wire freelook calibration into MainViewModel and the window

CalibrateFreelookCommand (enabled only while running, with a freelook key
and a distinct calibrate hotkey bound) drives a CalibrationSession over
the live mouse hook; a TapHotkeyWatcher feeds Mark() so it works with the
game focused. On completion, writes MouseSensitivity + MeasuredYawSweepCounts
and re-derives sensitivity when MaxYawDegrees later changes. Audio cues via
CalibrationCuePlayer. New hotkey/button row in the window (+40px height).

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
| `CalibrationSession` enum/record/class shape | Task 2 Step 3 |
| Two-mark model, re-zero on mark 1, half-sweep out | Task 2 Steps 1 & 3 (`HappyPath_TwoMarks...`) |
| Accumulate `dx` only while `IsHotkeyHeld` | Task 2 (`OnMouseMoved`, `Movement_WhileHotkeyNotHeld_IsIgnored`) |
| Sweep `< 50` → Failed, no result | Task 2 (`MinValidSweepCounts`, `SweepTooSmall_Fails...`) |
| 60 s idle timeout → Failed | Task 2 (`_idleTimer`, `IdleTimeout_Fails`) |
| `StepChanged` / `Completed` / `Ended` events | Task 2 Step 3 |
| Thread-safety (hook thread vs poll thread) | Task 2 (`_gate` lock, `Interlocked` on `_accum`, events raised outside lock) |
| `Dispose` unsubscribes, idempotent | Task 2 (`_disposed`, `Dispose_Unsubscribes...`) |
| `CalibrationCuePlayer` — 3 cues, no click, never throws, plays to configured device | Task 3 (cues + fade + swallow; **device: default endpoint this pass — deviation from spec's "configured output device", noted in Task 3 interfaces and commit**) |
| `RadioProfile.CalibrateHotkey` (unbound=0) + `MeasuredYawSweepCounts` (0=uncal) | Task 1 |
| Config round-trip + missing-field defaults | Task 1 Step 5 |
| `CalibrateFreelookCommand`, enabled only while running + both keys bound + distinct | Task 4 Step 1 |
| Button doubles as cancel | Task 4 Step 2 (`StartOrAbortCalibration`) |
| `Recenter()` at session start | Task 4 Step 2 |
| Mark via `TapHotkeyWatcher` on `CalibrateHotkey`, dispatcher-marshalled | Task 4 Step 2 |
| Completed → write `MouseSensitivity` + `MeasuredYawSweepCounts`, unsaved | Task 4 Step 2 |
| `CalibrateHotkey` change → `SetHotkey` on watcher | Task 4 Step 3 |
| `MaxYawDegrees` change → re-derive `MouseSensitivity` when calibrated | Task 4 Step 3 |
| Abort + teardown on Stop and LoadProfile | Task 4 Step 3 |
| Window row + height bump | Task 4 Step 4 |
| `FreelookTracker` unchanged | Not in any task (correct) |
| Manual verification checklist | Task 4 Step 6 |

**Deviation from spec:** `CalibrationCuePlayer` plays to the default endpoint, not `Profile.OutputDeviceId` via `WasapiOut`. Rationale: keeps Task 3 to a `TestTonePlayer`-sized change with no device-resolution/format-negotiation code. If the user calibrates on headphones that are not the default device the cues go to the default device. Flagged for the user at the testable stage; can be a fast follow if it matters.

**Placeholder scan:** none. The two "test-body note" callouts in Task 2 are explicit corrections to make while writing those tests, not deferred work; the corrected assertions are stated.

**Type/name consistency:** `CalibrationStep` (6 members), `CalibrationResult(float MeasuredHalfSweepCounts)`, `CalibrationSession(IMouseInputSource[, TimeSpan])`, events `StepChanged(CalibrationStep,string)` / `Completed(CalibrationResult)` / `Ended(string)` — identical across Task 2 code, Task 2 tests, and Task 4 wiring. `_calibrationSession` / `_calibrateMarkWatcher` / `_cuePlayer` used consistently in Task 4. `Profile.MeasuredYawSweepCounts` and `Profile.CalibrateHotkey` match Task 1. `FakeMouseInputSource.RaiseMove` matches the existing double in `FreelookTrackerTests.cs`.
