# Override Mute Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an "Override" button + rebindable global hotkey that instantly mutes/un-mutes the whole radio, independent of the existing Vehicle in/out toggle.

**Architecture:** Add a second independent runtime-only gain multiplier (`_overrideGain`) to `RadioPipeline`'s existing output-stage gain calculation, exactly mirroring the already-shipped `_vehicleGain`/`SetVehicleMuted` mechanism. Wire it into `MainViewModel` with the same `TapHotkeyWatcher` pattern used for every other feature hotkey (Recenter, Vehicle toggle, Calibrate, Outside view), and expose it in `MainWindow.xaml` as a `HotkeyCaptureControl` + toggle `Button`, following the existing "Outside view hotkey" row layout.

**Tech Stack:** C#/.NET 8, WPF, xUnit (`LogiBuddy.Core.Tests`).

**Spec:** `docs/superpowers/specs/2026-09-14-override-mute-toggle-design.md`

## Global Constraints

- No delay/hold-to-confirm semantics — Override is a plain instant tap-to-toggle (unlike the Vehicle toggle's `VehicleExitDelaySeconds`).
- Override and the Vehicle toggle are fully independent gain stages — neither reads nor writes the other's state. The audible result is silent if *either* is muted.
- No persistence of the muted/unmuted state itself. Every `Start()` begins unmuted (`IsOverrideMuted = false`), matching `_isInVehicle`'s reset to `true` (unmuted) on every Start.
- No fade/ramp on the transition — snaps at a block boundary, same as `_volume`/`_vehicleGain`.
- Override does nothing while the pipeline is stopped (`_pipeline is null`) — button and hotkey both gate on this, matching every other feature hotkey/command.
- The hotkey routes through `_keyStateGate` (the shared `SuspendableKeyStateSource`), so it is correctly suspended during chat-mode typing like every other feature hotkey.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/LogiBuddy.Core/Config/RadioProfile.cs` | Modify: add `OverrideHotkey` profile field/property |
| `src/LogiBuddy.Core/Pipeline/RadioPipeline.cs` | Modify: add `_overrideGain` field + `SetOverrideMuted(bool)` + fold into the output gain calc |
| `src/LogiBuddy.App/ViewModels/MainViewModel.cs` | Modify: add `IsOverrideMuted` state, `OverrideToggleCommand`, `OnOverrideTogglePressed()`, hotkey watcher lifecycle (Start/Stop/OnProfilePropertyChanged) |
| `src/LogiBuddy.App/MainWindow.xaml` | Modify: add "Override hotkey" row + toggle button to the existing "Vehicle & Auto-mute" card |
| `tests/LogiBuddy.Core.Tests/Config/RadioProfileTests.cs` | Modify: add default-unbound + property-changed tests for `OverrideHotkey` |
| `tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs` | Modify: add default-retention assertion for `OverrideHotkey` |
| `tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs` | Modify: add `SetOverrideMuted` gain tests, including independence from `SetVehicleMuted` |

---

### Task 1: `RadioProfile.OverrideHotkey`

**Files:**
- Modify: `src/LogiBuddy.Core/Config/RadioProfile.cs`
- Test: `tests/LogiBuddy.Core.Tests/Config/RadioProfileTests.cs`
- Test: `tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Produces: `RadioProfile.OverrideHotkey` (type `FreelookHotkey`, default `{ VirtualKeyCode = 0 }`) — consumed by Task 3 (`MainViewModel`) and Task 4 (`MainWindow.xaml`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/LogiBuddy.Core.Tests/Config/RadioProfileTests.cs`, replacing its final `}` (the class-closing brace right after the `AssigningChatModeHotkey_RaisesPropertyChanged` test):

```csharp
    [Fact]
    public void OverrideHotkey_DefaultsUnbound()
    {
        var profile = new RadioProfile();

        Assert.Equal(0, profile.OverrideHotkey.VirtualKeyCode); // unbound
    }

    [Fact]
    public void AssigningOverrideHotkey_RaisesPropertyChanged()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.OverrideHotkey = new FreelookHotkey { VirtualKeyCode = 0x58 };

        Assert.Contains(nameof(RadioProfile.OverrideHotkey), changed);
    }
}
```

In `tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs`, inside `Load_ProfileJsonMissingNewerFields_KeepsDefaults`, add one line right after the existing `OutsideViewHotkey` assertion:

```csharp
            Assert.Equal(0, loaded.OutsideViewHotkey.VirtualKeyCode);      // default retained (unbound)
            Assert.Equal(0, loaded.OverrideHotkey.VirtualKeyCode);         // default retained (unbound)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test LogiBuddy.sln --filter "FullyQualifiedName~RadioProfileTests|FullyQualifiedName~ConfigStoreTests"`
Expected: FAIL — `RadioProfile` has no member `OverrideHotkey` (compile error).

- [ ] **Step 3: Implement `OverrideHotkey`**

In `src/LogiBuddy.Core/Config/RadioProfile.cs`, add a new backing field right after `_chatModeHotkey` (line 45):

```csharp
    private FreelookHotkey _chatModeHotkey = new() { VirtualKeyCode = 0 };
    private FreelookHotkey _overrideHotkey = new() { VirtualKeyCode = 0 };
```

Add the property right after the `ChatModeHotkey` property (currently line 190):

```csharp
    public FreelookHotkey ChatModeHotkey { get => _chatModeHotkey; set => SetField(ref _chatModeHotkey, value); }

    /// Global hotkey that instantly mutes/un-mutes the whole radio,
    /// independent of the Vehicle in/out toggle — both must be "on" (not
    /// muted / in vehicle) to hear anything. No delay, unlike the vehicle
    /// toggle's exit/enter delay. VirtualKeyCode 0 means unbound (never
    /// fires). Runtime-only mute state — always starts unmuted on the next
    /// Start(), same as the vehicle in/out toggle.
    public FreelookHotkey OverrideHotkey { get => _overrideHotkey; set => SetField(ref _overrideHotkey, value); }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test LogiBuddy.sln --filter "FullyQualifiedName~RadioProfileTests|FullyQualifiedName~ConfigStoreTests"`
Expected: PASS (all tests in both files, including the two new ones and the updated `Load_ProfileJsonMissingNewerFields_KeepsDefaults`).

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Config/RadioProfile.cs tests/LogiBuddy.Core.Tests/Config/RadioProfileTests.cs tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs
git commit -m "feat: add RadioProfile.OverrideHotkey"
```

---

### Task 2: `RadioPipeline` override gain

**Files:**
- Modify: `src/LogiBuddy.Core/Pipeline/RadioPipeline.cs`
- Test: `tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs`

**Interfaces:**
- Consumes: nothing new from Task 1.
- Produces: `RadioPipeline.SetOverrideMuted(bool muted)` — consumed by Task 3 (`MainViewModel.OnOverrideTogglePressed`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs`, right after the existing `SetVehicleMuted_ComposesWithVolume_NotReplacesIt` test (ends at line 284, just before the blank line and `ListenerYaw_ReflectsTheTrackerAfterFreelookInput_PitchStaysZero`):

```csharp
    [Fact]
    public void SetOverrideMuted_True_SilencesOutputRegardlessOfVolume()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);
        pipeline.Start();

        pipeline.SetOverrideMuted(true);
        capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });

        Assert.All(output.WrittenBuffers[0], v => Assert.Equal(0f, v, precision: 5));
    }

    [Fact]
    public void SetOverrideMuted_ComposesWithVolume_NotReplacesIt()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);
        pipeline.Start();
        var profile = new RadioProfile { WetDryMix = 0f, Volume = 0.5f };
        pipeline.ApplyProfile(profile);

        pipeline.SetOverrideMuted(true);
        pipeline.SetOverrideMuted(false);
        capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });

        Assert.All(output.WrittenBuffers[0], v => Assert.Equal(0.5f, v, precision: 5));
    }

    [Fact]
    public void SetOverrideMuted_IsIndependentOfSetVehicleMuted()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _, frameSize: 3);
        pipeline.Start();

        // Vehicle unmuted, Override muted -> still silent.
        pipeline.SetVehicleMuted(false);
        pipeline.SetOverrideMuted(true);
        capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });
        Assert.All(output.WrittenBuffers[^1], v => Assert.Equal(0f, v, precision: 5));

        // Un-muting Override while Vehicle is still muted -> still silent.
        pipeline.SetVehicleMuted(true);
        pipeline.SetOverrideMuted(false);
        capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });
        Assert.All(output.WrittenBuffers[^1], v => Assert.Equal(0f, v, precision: 5));

        // Both unmuted -> audible again.
        pipeline.SetVehicleMuted(false);
        capture.PushSamples(new[] { 1.0f, 1.0f, 1.0f });
        Assert.All(output.WrittenBuffers[^1], v => Assert.Equal(1f, v, precision: 5));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test LogiBuddy.sln --filter "FullyQualifiedName~RadioPipelineTests"`
Expected: FAIL — `RadioPipeline` has no member `SetOverrideMuted` (compile error).

- [ ] **Step 3: Implement `SetOverrideMuted` and fold it into the gain calc**

In `src/LogiBuddy.Core/Pipeline/RadioPipeline.cs`, add a field right after `_vehicleGain` (line 45):

```csharp
    private float _vehicleGain = 1f; // not part of RadioProfile; runtime-only, toggled by the vehicle in/out feature.
    private float _overrideGain = 1f; // not part of RadioProfile; runtime-only, toggled by the override mute feature.
```

Add the method right after `SetVehicleMuted` (currently lines 60-63):

```csharp
    /// Mutes/unmutes the processed output independent of Profile.Volume and
    /// SetVehicleMuted — used by the override mute button/hotkey. Never
    /// persisted; a new RadioPipeline instance always starts unmuted (1f).
    /// Safe to call from the UI thread.
    public void SetOverrideMuted(bool muted) => _overrideGain = muted ? 0f : 1f;
```

Change the gain calculation (currently line 226) from:

```csharp
                float gain = _volume * _vehicleGain;
```

to:

```csharp
                float gain = _volume * _vehicleGain * _overrideGain;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test LogiBuddy.sln --filter "FullyQualifiedName~RadioPipelineTests"`
Expected: PASS (all tests in the file, including the three new ones).

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test LogiBuddy.sln`
Expected: PASS (no regressions elsewhere — the gain-calc change only adds a `* 1f` no-op for every other existing test, since none of them call `SetOverrideMuted`).

- [ ] **Step 6: Commit**

```bash
git add src/LogiBuddy.Core/Pipeline/RadioPipeline.cs tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs
git commit -m "feat: add RadioPipeline.SetOverrideMuted, independent of vehicle mute"
```

---

### Task 3: `MainViewModel` wiring

Not unit tested (matches existing precedent for `MainViewModel`'s vehicle/outside-view state machines — verified manually in Task 5). This task is implementation + build verification only.

**Files:**
- Modify: `src/LogiBuddy.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `RadioProfile.OverrideHotkey` (Task 1), `RadioPipeline.SetOverrideMuted(bool)` (Task 2), `TapHotkeyWatcher` (existing, `LogiBuddy.Core.Tracking`), `RelayCommand` (existing).
- Produces: `MainViewModel.IsOverrideMuted` (bool, observable), `MainViewModel.OverrideToggleCommand` (`ICommand`) — consumed by Task 4 (`MainWindow.xaml`).

- [ ] **Step 1: Add backing field and observable property**

In `src/LogiBuddy.App/ViewModels/MainViewModel.cs`, add a field right after `_vehicleToggleHotkeyWatcher` (line 31):

```csharp
    private TapHotkeyWatcher? _vehicleToggleHotkeyWatcher;
    private TapHotkeyWatcher? _overrideHotkeyWatcher;
```

Add the observable property right after the existing `IsOutsideView` property block (lines 37-41):

```csharp
    public bool IsOutsideView
    {
        get => _isOutsideView;
        private set { _isOutsideView = value; OnPropertyChanged(); }
    }
    private bool _isOverrideMuted;
    /// Bound by the Override button's Style DataTrigger and drives
    /// RadioPipeline.SetOverrideMuted via OnOverrideTogglePressed.
    public bool IsOverrideMuted
    {
        get => _isOverrideMuted;
        private set { _isOverrideMuted = value; OnPropertyChanged(); }
    }
```

- [ ] **Step 2: Declare and construct `OverrideToggleCommand`**

Add the declaration right after `OutsideViewToggleCommand` (line 203):

```csharp
    public ICommand OutsideViewToggleCommand { get; }
    public ICommand OverrideToggleCommand { get; }
```

Construct it in the constructor right after `OutsideViewToggleCommand`'s assignment (line 233):

```csharp
        OutsideViewToggleCommand = new RelayCommand(_ => OnOutsideViewTogglePressed(), _ => _pipeline is not null);
        OverrideToggleCommand = new RelayCommand(_ => OnOverrideTogglePressed(), _ => _pipeline is not null);
```

- [ ] **Step 3: Add the toggle handler**

Add this method right after `OnOutsideViewTogglePressed` (currently ends at line 939, right before `OnVoiceRecordPressed`):

```csharp
    /// Fires on the override hotkey's tap (and the window button, via the
    /// same handler). Instantly mutes/un-mutes the whole radio, independent
    /// of the Vehicle in/out toggle — no delay, no in-vehicle gate.
    private void OnOverrideTogglePressed()
    {
        IsOverrideMuted = !IsOverrideMuted;
        _pipeline?.SetOverrideMuted(IsOverrideMuted);
        StatusMessage = IsOverrideMuted ? "Radio muted (override)" : "Radio unmuted (override)";
    }
```

- [ ] **Step 4: Wire the hotkey change into `OnProfilePropertyChanged`**

Add a branch right after the `OutsideViewHotkey` branch (lines 397-400):

```csharp
        else if (e.PropertyName == nameof(RadioProfile.OutsideViewHotkey))
        {
            _outsideViewHotkeyWatcher?.SetHotkey(Profile.OutsideViewHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.OverrideHotkey))
        {
            _overrideHotkeyWatcher?.SetHotkey(Profile.OverrideHotkey);
        }
```

- [ ] **Step 5: Construct the watcher in `Start()`**

Add a local-variable declaration right after `outsideViewHotkeyWatcher` (line 1166):

```csharp
        TapHotkeyWatcher? outsideViewHotkeyWatcher = null;
        TapHotkeyWatcher? overrideHotkeyWatcher = null;
```

Construct and wire it right after the `outsideViewHotkeyWatcher.Pressed` block (lines 1249-1257), before `var tracker = new FreelookTracker(mouseHook, Profile);`:

```csharp
            outsideViewHotkeyWatcher = new TapHotkeyWatcher(Profile.OutsideViewHotkey, _keyStateGate);
            outsideViewHotkeyWatcher.Pressed += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(OnOutsideViewTogglePressed);
                }
                catch (Exception) { /* app is shutting down; nothing to toggle */ }
            };

            overrideHotkeyWatcher = new TapHotkeyWatcher(Profile.OverrideHotkey, _keyStateGate);
            overrideHotkeyWatcher.Pressed += () =>
            {
                try
                {
                    Application.Current?.Dispatcher.Invoke(OnOverrideTogglePressed);
                }
                catch (Exception) { /* app is shutting down; nothing to toggle */ }
            };

            var tracker = new FreelookTracker(mouseHook, Profile);
```

Reset the muted state on every Start — add right after `IsOutsideView = true;` / `pipeline.SetOutsideView(true);` (lines 1285-1286):

```csharp
            IsOutsideView = true;
            pipeline.SetOutsideView(true);
            IsOverrideMuted = false;
```

Commit the watcher to instance state — add right after `_outsideViewHotkeyWatcher = outsideViewHotkeyWatcher;` (line 1310):

```csharp
            _outsideViewHotkeyWatcher = outsideViewHotkeyWatcher;
            _overrideHotkeyWatcher = overrideHotkeyWatcher;
```

Dispose it in the `catch` block on a failed Start — add right after `outsideViewHotkeyWatcher?.Dispose();` (line 1356):

```csharp
            outsideViewHotkeyWatcher?.Dispose();
            overrideHotkeyWatcher?.Dispose();
```

- [ ] **Step 6: Tear it down in `Stop()`**

Add right after `_outsideViewHotkeyWatcher?.Dispose();` (line 1394):

```csharp
        _outsideViewHotkeyWatcher?.Dispose();
        _overrideHotkeyWatcher?.Dispose();
```

Add right after `_outsideViewHotkeyWatcher = null;` (line 1398):

```csharp
        _outsideViewHotkeyWatcher = null;
        _overrideHotkeyWatcher = null;
```

Add right after `IsOutsideView = false;` (line 1399):

```csharp
        IsOutsideView = false;
        IsOverrideMuted = false;
```

- [ ] **Step 7: Build and run the full test suite**

Run: `dotnet build LogiBuddy.sln`
Expected: builds with no errors or new warnings.

Run: `dotnet test LogiBuddy.sln`
Expected: PASS (no regressions — this task adds no new automated tests per the spec's testing plan; `MainViewModel` state machines are verified manually in Task 5).

- [ ] **Step 8: Commit**

```bash
git add src/LogiBuddy.App/ViewModels/MainViewModel.cs
git commit -m "feat: wire Override toggle hotkey and command into MainViewModel"
```

---

### Task 4: `MainWindow.xaml` UI

Not unit tested (WPF UI, verified manually in Task 5). Implementation + build verification only.

**Files:**
- Modify: `src/LogiBuddy.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `Profile.OverrideHotkey` (Task 1), `OverrideToggleCommand` and `IsOverrideMuted` (Task 3).

- [ ] **Step 1: Add the Override row to the "Vehicle & Auto-mute" card**

In `src/LogiBuddy.App/MainWindow.xaml`, insert this block right after the existing `AutoMuteSource` `CheckBox` (lines 164-165), before the card's closing `</StackPanel></Border>` (line 166-167):

```xml
                            <CheckBox Content="Auto-mute source audio on Start" IsChecked="{Binding Profile.AutoMuteSource}"
                                      ToolTip="Mutes the source app's own Windows audio session. WARNING: this also silences this app's own capture of that source, so the radio goes silent too — only enable this if you don't need the radio to actually play that source. Off by default; prefer 'Auto-route source to a silent device' above instead." />

                            <Grid Margin="0,10,0,0">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="130" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="Override hotkey" Style="{StaticResource RowLabelText}" />
                                <StackPanel Grid.Column="1" Orientation="Horizontal">
                                    <controls:HotkeyCaptureControl Hotkey="{Binding Profile.OverrideHotkey, Mode=TwoWay}" />
                                    <Button Margin="8,0,0,0" Command="{Binding OverrideToggleCommand}">
                                        <Button.Style>
                                            <Style TargetType="Button">
                                                <Setter Property="Content" Value="Override" />
                                                <Style.Triggers>
                                                    <DataTrigger Binding="{Binding IsOverrideMuted}" Value="True">
                                                        <Setter Property="Content" Value="Muted" />
                                                    </DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </Button.Style>
                                    </Button>
                                </StackPanel>
                            </Grid>
                            <TextBlock Text="Instantly mutes/un-mutes the whole radio, independent of the Vehicle toggle above — both must be un-muted/in-vehicle to hear anything. No delay, and doesn't persist: always starts unmuted on Start." Style="{StaticResource MutedText}" TextWrapping="Wrap" Margin="0,4,0,0" />
                        </StackPanel>
                    </Border>
```

(This replaces the original closing `</StackPanel>` / `</Border>` pair with the same pair after the new content — no other lines in the card change.)

- [ ] **Step 2: Build**

Run: `dotnet build LogiBuddy.sln`
Expected: builds with no errors (XAML compiles, `DataTrigger`/`Style` syntax matches the existing pattern already used for `IsOutsideView` elsewhere in this file).

- [ ] **Step 3: Commit**

```bash
git add src/LogiBuddy.App/MainWindow.xaml
git commit -m "feat: add Override hotkey/button to the Vehicle & Auto-mute card"
```

---

### Task 5: Manual verification (running app)

No automated test — this exercises real Win32 hotkey polling and WPF data binding, matching the project's existing precedent of not unit-testing `TapHotkeyWatcher`'s interop or `MainViewModel`'s hotkey-driven state machines.

**Files:** none (verification only).

- [ ] **Step 1: Build and launch**

```powershell
dotnet build LogiBuddy.sln
dotnet run --project src/LogiBuddy.App/LogiBuddy.App.csproj --no-build
```

- [ ] **Step 2: Bind the Override hotkey and start the radio**

In the running app: pick a source, bind an unused key to "Override hotkey" in the Vehicle & Auto-mute card, click Start.

- [ ] **Step 3: Verify instant hotkey mute/unmute**

Press the Override hotkey while the radio is audible. Expected: silences immediately, no delay, status message reads "Radio muted (override)", the Override button now reads "Muted". Press it again. Expected: audible again immediately, status message reads "Radio unmuted (override)", button reads "Override" again.

- [ ] **Step 4: Verify the button matches the hotkey**

Click the Override button (instead of the hotkey). Expected: identical effect to Step 3, and the button's own label reflects the new state.

- [ ] **Step 5: Verify independence from the Vehicle toggle**

With the radio audible: press the Vehicle-toggle hotkey to go "out of vehicle" (wait for its configured delay), then press Override. Expected: still silent (both conditions must be satisfied). Press Override again to un-mute it, then press the Vehicle-toggle hotkey again to go back "in vehicle". Expected: audio does not resume until vehicle re-entry completes — Override alone doesn't override the vehicle state, and vice versa.

- [ ] **Step 6: Verify it's inert when stopped**

Click Stop. Expected: the Override button is disabled and the hotkey does nothing (no status message change) until Start is clicked again.

- [ ] **Step 7: Verify no persistence across Start**

While muted via Override, click Stop, then Start again. Expected: the radio starts unmuted (Override button reads "Override", not "Muted") — the mute state does not carry over.

- [ ] **Step 8: Verify chat-mode suspension**

If a Chat mode hotkey is bound, activate chat mode, then press the Override hotkey. Expected: no effect while chat mode is active (same as every other feature hotkey), then works again once chat mode is exited.

- [ ] **Step 9: Verify an unbound hotkey never fires**

Leave `OverrideHotkey` unbound (default). Expected: no key press toggles Override; only the button works.

- [ ] **Step 10: Verify the Volume slider is unaffected**

Set the Volume slider to a non-default value (e.g. 0.5), then mute/un-mute via Override. Expected: the slider's own value never changes and isn't reset to 0 or 1 by the toggle — same invariant already relied on for the Vehicle toggle, and already covered automatically by Task 2's `SetOverrideMuted_ComposesWithVolume_NotReplacesIt` test.
