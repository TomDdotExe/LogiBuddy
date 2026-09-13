# Spatial + QOL Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add live volume, stereo-width, source-height, and freelook-always-on / recenter controls to the running radio.

**Architecture:** Two new post-spatializer stages in the `RadioPipeline` block loop (mid/side width, then a master gain). `FreelookTracker` gains an always-on gate and a `Recenter()`. Three new `RadioProfile` fields ride the existing live-apply path. UI is new sliders / a checkbox / a button in `MainWindow.xaml` plus a vertical height slider beside the unchanged `SourcePositionCanvas`.

**Tech Stack:** C# / .NET 8, WPF, xUnit, NAudio.

**Spec:** `docs/superpowers/specs/2026-09-08-spatial-and-qol-controls-design.md`

## Global Constraints

- All four controls apply live on a running pipeline — no Stop/Start, no "restart to apply".
- Real-time audio path: no heap allocation in `Process` / `OnDataAvailable` per-block code.
- Width is post-spatializer mid/side only — do **not** touch Steam Audio `spatialBlend` or HRTF settings.
- Live scalar reads on the audio thread rely on atomic single-`float` writes; no locks added.
- `Volume` range 0–1 (attenuate only). `StereoWidth` UI range 0–2, clamped in the pipeline to [0, 4]. `Volume` clamped to [0, 1].
- Pipeline stage order: width first, then volume.
- Follow existing patterns: profile fields via `SetField`; live props routed through `LiveProfileProperties`; tests in `LogiBuddy.Core.Tests`.

---

### Task 1: RadioProfile fields + legacy-load tolerance

**Files:**
- Modify: `src/LogiBuddy.Core/Config/RadioProfile.cs`
- Test: `tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Produces: `RadioProfile.Volume` (`float`, default `1.0f`), `RadioProfile.StereoWidth` (`float`, default `1.0f`), `RadioProfile.FreelookAlwaysOn` (`bool`, default `false`) — all raising `PropertyChanged` via `SetField`.

- [ ] **Step 1: Write the failing test**

In `ConfigStoreTests.cs`, add a test that a profile JSON written without the new keys loads with the defaults. Follow the existing legacy-load test in that file for the exact `IConfigStore` / temp-dir setup; assert:

```csharp
Assert.Equal(1.0f, loaded.Volume);
Assert.Equal(1.0f, loaded.StereoWidth);
Assert.False(loaded.FreelookAlwaysOn);
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: FAIL — `RadioProfile` has no `Volume` / `StereoWidth` / `FreelookAlwaysOn`.

- [ ] **Step 3: Add the fields**

In `RadioProfile.cs`, mirroring the existing backing-field + `SetField` pattern:

```csharp
private float _volume = 1.0f;
private float _stereoWidth = 1.0f;
private bool _freelookAlwaysOn;

/// Master output attenuation, 0..1. Applied after StereoWidth, before output.
public float Volume { get => _volume; set => SetField(ref _volume, value); }

/// Mid/side stereo width. 0 = mono point source, 1 = unchanged, >1 = wider.
public float StereoWidth { get => _stereoWidth; set => SetField(ref _stereoWidth, value); }

/// When true, freelook tracks the mouse continuously (no hold-to-look) and
/// does not spring back to centre.
public bool FreelookAlwaysOn { get => _freelookAlwaysOn; set => SetField(ref _freelookAlwaysOn, value); }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Config/RadioProfile.cs tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs
git commit -m "feat: add Volume, StereoWidth, FreelookAlwaysOn to RadioProfile"
```

---

### Task 2: StereoWidth DSP helper

**Files:**
- Create: `src/LogiBuddy.Core/Dsp/StereoWidth.cs`
- Test: `tests/LogiBuddy.Core.Tests/Dsp/StereoWidthTests.cs`

**Interfaces:**
- Produces: `static void LogiBuddy.Core.Dsp.StereoWidth.Apply(float[] interleaved, int frameCount, float width)` — in-place mid/side on interleaved stereo, `frameCount` = number of L/R pairs.

- [ ] **Step 1: Write the failing tests**

```csharp
using LogiBuddy.Core.Dsp;
using Xunit;

namespace LogiBuddy.Core.Tests.Dsp;

public class StereoWidthTests
{
    [Fact]
    public void WidthOne_LeavesBufferUntouched()
    {
        var buf = new[] { 0.4f, -0.2f, 0.9f, 0.1f };
        var copy = (float[])buf.Clone();
        StereoWidth.Apply(buf, 2, 1f);
        Assert.Equal(copy, buf);
    }

    [Fact]
    public void WidthZero_CollapsesEachPairToMid()
    {
        var buf = new[] { 1.0f, 0.0f, 0.4f, 0.2f };
        StereoWidth.Apply(buf, 2, 0f);
        Assert.Equal(0.5f, buf[0], precision: 5);
        Assert.Equal(0.5f, buf[1], precision: 5);
        Assert.Equal(0.3f, buf[2], precision: 5);
        Assert.Equal(0.3f, buf[3], precision: 5);
    }

    [Fact]
    public void WidthTwo_DoublesTheSideComponent()
    {
        var buf = new[] { 0.8f, 0.2f }; // mid 0.5, side 0.3
        StereoWidth.Apply(buf, 1, 2f);  // side -> 0.6
        Assert.Equal(1.1f, buf[0], precision: 5);   // mid + 2*side
        Assert.Equal(-0.1f, buf[1], precision: 5);  // mid - 2*side
    }

    [Fact]
    public void FrameCountShorterThanBuffer_LeavesTailUntouched()
    {
        var buf = new[] { 1.0f, 0.0f, 7.0f, 7.0f };
        StereoWidth.Apply(buf, 1, 0f);
        Assert.Equal(7.0f, buf[2]);
        Assert.Equal(7.0f, buf[3]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~StereoWidthTests"`
Expected: FAIL — `StereoWidth` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace LogiBuddy.Core.Dsp;

/// Mid/side stereo-width adjustment. Real-time safe: no allocation, in place.
public static class StereoWidth
{
    /// width 0 => both channels become the mid (mono point source),
    /// 1 => unchanged, >1 => exaggerated ear-to-ear spread.
    /// frameCount is the number of interleaved L/R pairs to process.
    public static void Apply(float[] interleaved, int frameCount, float width)
    {
        if (width == 1f) return;
        for (int i = 0; i < frameCount; i++)
        {
            float l = interleaved[i * 2];
            float r = interleaved[i * 2 + 1];
            float mid = (l + r) * 0.5f;
            float side = (l - r) * 0.5f * width;
            interleaved[i * 2] = mid + side;
            interleaved[i * 2 + 1] = mid - side;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~StereoWidthTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Dsp/StereoWidth.cs tests/LogiBuddy.Core.Tests/Dsp/StereoWidthTests.cs
git commit -m "feat: add StereoWidth mid/side DSP helper"
```

---

### Task 3: RadioPipeline width + volume stages

**Files:**
- Modify: `src/LogiBuddy.Core/Pipeline/RadioPipeline.cs`
- Test: `tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs`

**Interfaces:**
- Consumes: `StereoWidth.Apply` (Task 2); `RadioProfile.Volume`, `RadioProfile.StereoWidth` (Task 1).
- Produces: `RadioPipeline.ApplyProfile` now also sets private `float _volume` (clamped [0,1]) and `float _stereoWidth` (clamped [0,4]); the block loop applies width then volume to `_stereoBlock` before `_output.Write`.
- Produces (test double): `PanningFakeSpatializer` in `RadioPipelineTests.cs` — `Process` writes `stereoOutputInterleaved[i*2] = monoInput[i]; stereoOutputInterleaved[i*2+1] = 0f;`.

- [ ] **Step 1: Write the failing tests**

Add `PanningFakeSpatializer` next to `FakeSpatializer` in `RadioPipelineTests.cs`:

```csharp
public class PanningFakeSpatializer : ISpatializer
{
    public void SetSourcePosition(float x, float y, float z) { }
    public void SetListenerOrientation(float yawDegrees, float pitchDegrees) { }
    public void Process(float[] monoInput, int count, float[] stereoOutputInterleaved)
    {
        for (int i = 0; i < count; i++)
        {
            stereoOutputInterleaved[i * 2] = monoInput[i]; // hard left
            stereoOutputInterleaved[i * 2 + 1] = 0f;
        }
    }
}
```

Then the tests:

```csharp
[Fact]
public void Volume_ScalesEveryOutputSample()
{
    var capture = new FakeCaptureService();
    var output = new FakeOutputService();
    var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);
    pipeline.Start();

    var profile = new RadioProfile { WetDryMix = 0f, Volume = 0.5f };
    pipeline.ApplyProfile(profile);
    capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });

    Assert.All(output.WrittenBuffers[0], v => Assert.Equal(0.5f, v, precision: 5));
}

[Fact]
public void StereoWidthZero_CollapsesPannedOutputToEqualChannels()
{
    var capture = new FakeCaptureService();
    var output = new FakeOutputService();
    var profile = new RadioProfile { WetDryMix = 0f, StereoWidth = 0f };
    var fakeInput = new LogiBuddy.Core.Tests.Tracking.FakeMouseInputSource();
    var tracker = new FreelookTracker(fakeInput, profile);
    var effectChain = new RadioEffectChain(48000f);
    effectChain.ApplyProfile(profile);
    var spat = new PanningFakeSpatializer();
    var pipeline = new RadioPipeline(capture, output, spat, spat, effectChain, tracker, frameSize: 2);
    pipeline.ApplyProfile(profile);
    pipeline.Start();

    capture.PushSamples(new[] { 1.0f, 1.0f });

    var outBuf = output.WrittenBuffers[0];
    Assert.Equal(outBuf[0], outBuf[1], precision: 5); // L == R after width 0
    Assert.Equal(0.5f, outBuf[0], precision: 5);      // mid of (1, 0)
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RadioPipelineTests"`
Expected: FAIL — output not scaled / not collapsed (stages not implemented).

- [ ] **Step 3: Implement**

In `RadioPipeline.cs`:

Add fields near the other tunables:

```csharp
private float _volume = 1f;
private float _stereoWidth = 1f;
```

In `ApplyProfile`, after the existing body:

```csharp
_volume = Math.Clamp(profile.Volume, 0f, 1f);
_stereoWidth = Math.Clamp(profile.StereoWidth, 0f, 4f);
```

In `OnDataAvailable`, between `_activeSpatializer.Process(_monoBlock, _frameSize, _stereoBlock);` and `_output.Write(_stereoBlock, _stereoBlock.Length);`:

```csharp
Dsp.StereoWidth.Apply(_stereoBlock, _frameSize, _stereoWidth);
if (_volume != 1f)
    for (int s = 0; s < _frameSize * 2; s++)
        _stereoBlock[s] *= _volume;
```

Add `using LogiBuddy.Core.Dsp;` if not present (it already is).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RadioPipelineTests"`
Expected: PASS (all, including the 2 new).

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Pipeline/RadioPipeline.cs tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs
git commit -m "feat: apply stereo width and volume in the pipeline output stage"
```

---

### Task 4: FreelookTracker always-on gate + Recenter

**Files:**
- Modify: `src/LogiBuddy.Core/Tracking/FreelookTracker.cs`
- Test: `tests/LogiBuddy.Core.Tests/Tracking/FreelookTrackerTests.cs`

**Interfaces:**
- Consumes: `RadioProfile.FreelookAlwaysOn` (Task 1).
- Produces: `FreelookTracker.Recenter()` — sets `YawDegrees` and `PitchDegrees` to `0f`. `OnMouseMoved` and `Update` now honour `_profile.FreelookAlwaysOn`.

- [ ] **Step 1: Write the failing tests**

Add to `FreelookTrackerTests.cs`. Note `MakeProfile()` there returns a `RadioProfile`; set `FreelookAlwaysOn` on it per-test.

```csharp
[Fact]
public void AlwaysOn_TracksMovementWithHotkeyNotHeld()
{
    var input = new FakeMouseInputSource { IsHotkeyHeld = false };
    var profile = MakeProfile();
    profile.FreelookAlwaysOn = true;
    var tracker = new FreelookTracker(input, profile);

    input.RaiseMove(dx: 50, dy: 20);

    Assert.Equal(5f, tracker.YawDegrees, precision: 3);
    Assert.Equal(2f, tracker.PitchDegrees, precision: 3);
}

[Fact]
public void AlwaysOn_UpdateDoesNotSpringBack()
{
    var input = new FakeMouseInputSource { IsHotkeyHeld = false };
    var profile = MakeProfile();
    profile.FreelookAlwaysOn = true;
    var tracker = new FreelookTracker(input, profile);
    input.RaiseMove(dx: 100, dy: 0); // yaw = 10

    tracker.Update(deltaSeconds: 1f);

    Assert.Equal(10f, tracker.YawDegrees, precision: 3);
}

[Fact]
public void Recenter_ZeroesYawAndPitch()
{
    var input = new FakeMouseInputSource { IsHotkeyHeld = true };
    var tracker = new FreelookTracker(input, MakeProfile());
    input.RaiseMove(dx: 200, dy: 100);

    tracker.Recenter();

    Assert.Equal(0f, tracker.YawDegrees);
    Assert.Equal(0f, tracker.PitchDegrees);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FreelookTrackerTests"`
Expected: FAIL — `Recenter` missing; always-on tests fail because movement is gated by `IsHotkeyHeld`.

- [ ] **Step 3: Implement**

In `FreelookTracker.cs`:

`OnMouseMoved` first line:

```csharp
if (!_profile.FreelookAlwaysOn && !_inputSource.IsHotkeyHeld) return;
```

`Update` first line:

```csharp
if (_profile.FreelookAlwaysOn || _inputSource.IsHotkeyHeld) return;
```

Add:

```csharp
/// Snaps the listener orientation back to facing forward. Called from the UI
/// thread; the audio thread's per-block read of YawDegrees/PitchDegrees is a
/// pair of atomic float reads, so the worst case is one block with one axis
/// updated — inaudible.
public void Recenter()
{
    YawDegrees = 0f;
    PitchDegrees = 0f;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FreelookTrackerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Tracking/FreelookTracker.cs tests/LogiBuddy.Core.Tests/Tracking/FreelookTrackerTests.cs
git commit -m "feat: freelook always-on mode and Recenter"
```

---

### Task 5: RadioPipeline.RecenterListener

**Files:**
- Modify: `src/LogiBuddy.Core/Pipeline/RadioPipeline.cs`
- Test: `tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs`

**Interfaces:**
- Consumes: `FreelookTracker.Recenter()` (Task 4); `RadioPipeline.ListenerYawDegrees` / `ListenerPitchDegrees` (existing).
- Produces: `RadioPipeline.RecenterListener()` — forwards to the tracker.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void RecenterListener_ReturnsOrientationToZero()
{
    var capture = new FakeCaptureService();
    var output = new FakeOutputService();
    var profile = new RadioProfile { MouseSensitivity = 0.1f, MaxYawDegrees = 90f, MaxPitchDegrees = 60f };
    var input = new LogiBuddy.Core.Tests.Tracking.FakeMouseInputSource { IsHotkeyHeld = true };
    var tracker = new FreelookTracker(input, profile);
    var effectChain = new RadioEffectChain(48000f);
    var spat = new FakeSpatializer();
    var pipeline = new RadioPipeline(capture, output, spat, spat, effectChain, tracker, frameSize: 3);
    pipeline.ApplyProfile(profile);
    pipeline.Start();
    input.RaiseMove(dx: 300, dy: 100);

    pipeline.RecenterListener();

    Assert.Equal(0f, pipeline.ListenerYawDegrees);
    Assert.Equal(0f, pipeline.ListenerPitchDegrees);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RadioPipelineTests"`
Expected: FAIL — `RecenterListener` not defined.

- [ ] **Step 3: Implement**

In `RadioPipeline.cs`, next to the `ListenerYawDegrees` getters:

```csharp
/// Snaps the freelook listener orientation back to forward. Safe on the UI thread.
public void RecenterListener() => _tracker.Recenter();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RadioPipelineTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Pipeline/RadioPipeline.cs tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs
git commit -m "feat: add RadioPipeline.RecenterListener"
```

---

### Task 6: MainViewModel wiring

**Files:**
- Modify: `src/LogiBuddy.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `RadioProfile.Volume` / `StereoWidth` / `FreelookAlwaysOn` (Task 1); `RadioPipeline.RecenterListener()` (Task 5).
- Produces: `MainViewModel.RecenterCommand` (`ICommand`); `Volume`, `StereoWidth`, `FreelookAlwaysOn` added to `LiveProfileProperties`.

No unit test — `MainViewModel` constructs `ConfigStore` / `WindowsAppAudioRouter` / uses `Application.Current.Dispatcher`, so it is verified in the manual pass (Task 8). This task is a thin wiring layer over already-tested units.

- [ ] **Step 1: Extend `LiveProfileProperties`**

Add these three `nameof(RadioProfile.X)` entries to the `LiveProfileProperties` set initializer:

```csharp
nameof(RadioProfile.Volume), nameof(RadioProfile.StereoWidth),
nameof(RadioProfile.FreelookAlwaysOn),
```

(`SourceY` is already in the set — leave it.)

- [ ] **Step 2: Add `RecenterCommand`**

Declare alongside the other `public ICommand` properties:

```csharp
public ICommand RecenterCommand { get; }
```

In the constructor, next to the other command assignments:

```csharp
RecenterCommand = new RelayCommand(_ =>
{
    _pipeline?.RecenterListener();
    ListenerYaw = 0;
    ListenerPitch = 0;
});
```

- [ ] **Step 3: Build**

Run: `dotnet build src/LogiBuddy.App/LogiBuddy.App.csproj`
Expected: succeeds, 0 warnings. (Close the running app first if the exe is locked.)

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: all green (no MainViewModel tests, but confirm nothing else regressed).

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.App/ViewModels/MainViewModel.cs
git commit -m "feat: wire volume/width/always-on live + RecenterCommand in MainViewModel"
```

---

### Task 7: MainWindow.xaml + height slider

**Files:**
- Modify: `src/LogiBuddy.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `MainViewModel.RecenterCommand` (Task 6); `Profile.Volume` / `Profile.StereoWidth` / `Profile.FreelookAlwaysOn` / `Profile.SourceY`.

No automated test (declarative XAML) — verified in Task 8.

- [ ] **Step 1: Add two `RowDefinition`s**

The current `Grid.RowDefinitions` has rows 0–16. Insert two `<RowDefinition Height="Auto" />` after the Wet/Dry row (currently row 8), so what was row 9 (freelook hotkey) becomes row 11, and every later row shifts +2. Update the XML comments that number the rows.

- [ ] **Step 2: Add the Volume and Width slider rows**

After the Wet/Dry `StackPanel` (`Grid.Row="8"`), add:

```xml
<StackPanel Grid.Row="9" Orientation="Horizontal" Margin="0,0,0,8">
    <TextBlock Text="Volume:" VerticalAlignment="Center" Width="140" />
    <Slider Minimum="0" Maximum="1" Width="200" Value="{Binding Profile.Volume}" />
</StackPanel>

<StackPanel Grid.Row="10" Orientation="Horizontal" Margin="0,0,0,8">
    <TextBlock Text="Stereo width:" VerticalAlignment="Center" Width="140" />
    <Slider Minimum="0" Maximum="2" Width="200" Value="{Binding Profile.StereoWidth}" />
</StackPanel>
```

- [ ] **Step 3: Renumber the shifted rows**

Bump `Grid.Row` on every `StackPanel` / control from the old freelook-hotkey row onward by +2: freelook hotkey `9→11`, mouse sensitivity `10→12`, max yaw `11→13`, max pitch `12→14`, spring-back `13→15`, buttons+restart hint `14→16`, canvas `15→17`, status `16→18`.

- [ ] **Step 4: Add the always-on checkbox + Recenter button to the freelook-hotkey row**

In the (now `Grid.Row="11"`) freelook-hotkey `StackPanel`, after `HotkeyCaptureControl`:

```xml
<CheckBox Content="Freelook always on" IsChecked="{Binding Profile.FreelookAlwaysOn}"
          VerticalAlignment="Center" Margin="16,0,0,0" />
<Button Content="Recenter" Command="{Binding RecenterCommand}" Margin="8,0,0,0" />
```

- [ ] **Step 5: Wrap the canvas row with a vertical height slider**

Replace the `<controls:SourcePositionCanvas Grid.Row="17" ... />` element with:

```xml
<StackPanel Grid.Row="17" Orientation="Horizontal">
    <controls:SourcePositionCanvas
        SourceX="{Binding Profile.SourceX}"
        SourceZ="{Binding Profile.SourceZ}"
        ListenerYaw="{Binding ListenerYaw}"
        ListenerPitch="{Binding ListenerPitch}" />
    <TextBlock Text="Height (m)" VerticalAlignment="Top" Margin="12,0,4,0" />
    <Slider Orientation="Vertical" Minimum="-2" Maximum="2" Height="200"
            Value="{Binding Profile.SourceY}" />
</StackPanel>
```

- [ ] **Step 6: Build and eyeball**

Run: `dotnet build src/LogiBuddy.App/LogiBuddy.App.csproj`
Expected: succeeds. If the two extra rows push content past the window bottom, change the `Window` `Height="940"` to `Height="1010"`.

- [ ] **Step 7: Commit**

```bash
git add src/LogiBuddy.App/MainWindow.xaml
git commit -m "feat: add volume, width, height, always-on and recenter controls to the window"
```

---

### Task 8: Build, launch, manual verification

**Files:** none (verification only).

- [ ] **Step 1: Full build + test**

Run: `dotnet build` then `dotnet test`
Expected: build 0 warnings; all tests green.

- [ ] **Step 2: Launch**

Close any running instance, then launch `src/LogiBuddy.App/bin/Debug/net8.0-windows/LogiBuddy.App.exe`.

- [ ] **Step 3: Hand off the manual checklist**

Report to the human for checks (see the spec's "Manual" list):
- Volume 0 = silent, 1 = unity, live.
- Width 0 = mono/point, 1 = as before, 2 = wider; live; listen for clipping at 2 on loud material.
- Height slider raises/lowers the source (audible on HRTF; no effect on stereo-pan fallback, expected).
- Freelook "always on": mouse moves the listener arrow with no key held, no spring-back; Recenter snaps arrow + audio to forward.
- All four apply with the pipeline running; no "restart to apply".
- Layout: every row visible, canvas + vertical slider aligned, nothing clipped.

---

## Self-Review

**Spec coverage:**
- Profile fields (`Volume`, `StereoWidth`, `FreelookAlwaysOn`) + legacy load → Task 1. ✓
- `StereoWidth.Apply` mid/side → Task 2. ✓
- Pipeline width + volume stages, order, clamps, `ApplyProfile` copy → Task 3. ✓
- `FreelookTracker` always-on gate (both `OnMouseMoved` and `Update`) + `Recenter` → Task 4. ✓
- `RadioPipeline.RecenterListener` → Task 5. ✓
- `LiveProfileProperties` additions + `RecenterCommand` (+ immediate marker zero) → Task 6. ✓
- XAML: Volume/Width sliders, always-on checkbox, Recenter button, vertical height slider beside unchanged canvas, row renumber, window-height fallback → Task 7. ✓
- Automated + manual test lists → Tasks 1–5 (automated), Task 8 (manual). ✓
- Constraint "width before volume" → Task 3 Step 3 order. ✓
- Constraint "no HRTF/spatialBlend change" → not touched anywhere; Task 3 only edits the post-spatializer block. ✓

**Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N". Every code step has real code. `PanningFakeSpatializer` is fully written in Task 3. The window-height bump is a concrete conditional value (940 → 1010), not a placeholder.

**Type consistency:**
- `StereoWidth.Apply(float[], int, float)` — defined Task 2, called identically in Task 3 (`Dsp.StereoWidth.Apply(_stereoBlock, _frameSize, _stereoWidth)`). ✓
- `RadioProfile.Volume` / `StereoWidth` (`float`) / `FreelookAlwaysOn` (`bool`) — Task 1, consumed Tasks 3/4/6/7 consistently. ✓
- `FreelookTracker.Recenter()` — Task 4, called by `RadioPipeline.RecenterListener()` Task 5, which `MainViewModel.RecenterCommand` calls Task 6. ✓
- `RadioPipeline.RecenterListener()` — Task 5, bound via `RecenterCommand` Task 6 / XAML Task 7. ✓
- Pipeline private fields `_volume` / `_stereoWidth` — Task 3 only; not referenced across tasks. ✓
- `MainViewModel.ListenerYaw` / `ListenerPitch` — pre-existing (previous feature), reused in Task 6's `RecenterCommand`. ✓

No gaps found.
