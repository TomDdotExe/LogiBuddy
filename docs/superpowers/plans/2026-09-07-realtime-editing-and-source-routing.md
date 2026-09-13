# Real-time Editing & Source Audio Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every safe-to-change profile value update the running audio pipeline instantly, add the missing freelook controls, and auto-route the captured source process's audio to a silent render endpoint (virtual cable) on Start so only the processed radio output is audible.

**Architecture:** `RadioProfile` becomes an `INotifyPropertyChanged` model; `MainViewModel` subscribes and re-pushes live values through the existing `RadioPipeline.ApplyProfile`, while flagging restart-only settings. Source routing is a new Core subsystem (`WindowsAppAudioRouter` over the undocumented Windows 11 `IAudioPolicyConfig` COM API) driven from `MainViewModel.Start`/`Stop`, with a crash-safe recovery file.

**Tech Stack:** .NET 8, WPF, NAudio (device enumeration), raw COM interop (`RoGetActivationFactory` + `IAudioPolicyConfig`, HSTRING marshalling), `System.Text.Json`, xUnit.

**Specs:**
- `docs/superpowers/specs/2026-09-07-realtime-profile-editing-design.md`
- `docs/superpowers/specs/2026-09-06-source-audio-routing-design.md`

## Global Constraints

- Live-editable values: `HighPassHz`, `LowPassHz`, `DistortionDrive`, `CompressorThresholdDb`, `CompressorRatio`, `NoiseLevel`, `WetDryMix`, `SourceX`, `SourceY`, `SourceZ`, `MouseSensitivity`, `MaxYawDegrees`, `MaxPitchDegrees`, `SpringBackRatePerSecond`.
- Restart-required values: `SourceProcessName`, `OutputDeviceId`, `Hotkey`, `AutoRouteSource`, `RouteSourceToDeviceId`.
- `RadioProfile` must stay serializable with `System.Text.Json` default options; profiles written by earlier builds must load unchanged (missing fields fall back to defaults).
- No locking on the audio path. Rely on atomic scalar reads/writes; a one-block coefficient blend during an edit is acceptable.
- Source routing requires Windows 11 (`Environment.OSVersion.Version.Build >= 22000`). On lower/unknown builds `WindowsAppAudioRouter.IsSupported` is `false`; capture still works.
- The `IAudioPolicyConfig` interop must degrade to `IsSupported = false` on activation failure or `E_NOINTERFACE` — never crash the app.
- Every routing change is persisted by Windows and must be paired with a restore. `route-recovery.json` in `%AppData%/LogiBuddy/` makes restore survive a crash.
- Follow existing patterns: dependencies are `new`-ed directly in `MainViewModel`; automated tests live only in `LogiBuddy.Core.Tests` (there is no WPF test project); WPF / COM / hardware code is verified by a manual checklist, not xUnit.
- End every commit message with:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01KZSuka8NcFDh2ckJXkViNS
  ```

---

## File Structure

**Create:**
- `src/LogiBuddy.App/Controls/HotkeyCaptureControl.xaml` / `.xaml.cs` — click-to-rebind control for the freelook key, exposes a two-way `Hotkey` dependency property.
- `src/LogiBuddy.Core/Audio/RenderDeviceEnumerator.cs` — active render-endpoint list + virtual-cable name heuristic.
- `src/LogiBuddy.Core/Audio/AudioPolicyConfigInterop.cs` — raw COM for Windows 11 per-app routing.
- `src/LogiBuddy.Core/Audio/SourceAudioRouter.cs` — `ISourceAudioRouter`, `WindowsAppAudioRouter`, `AppAudioRoute`, `SourceRoutingException`.
- `src/LogiBuddy.Core/Config/RouteRecoveryStore.cs` — `RouteRecoveryRecord` + read/write/delete of `route-recovery.json`.
- `tests/LogiBuddy.Core.Tests/Config/RadioProfileTests.cs`
- `tests/LogiBuddy.Core.Tests/Audio/RenderDeviceEnumeratorTests.cs`
- `tests/LogiBuddy.Core.Tests/Config/RouteRecoveryStoreTests.cs`

**Modify:**
- `src/LogiBuddy.Core/Config/RadioProfile.cs` — `INotifyPropertyChanged`; two new routing fields.
- `src/LogiBuddy.Core/Pipeline/RadioPipeline.cs` — doc comment only on `ApplyProfile`.
- `src/LogiBuddy.App/ViewModels/MainViewModel.cs` — observe `Profile`, live/restart routing, `RestartRequired`, routing in `Start`/`Stop`/ctor, `ResetSourceRoutingCommand`.
- `src/LogiBuddy.App/MainWindow.xaml` — freelook rows, restart hint, routing row, `BooleanToVisibilityConverter` resource.
- `tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs` — round-trip + legacy-load assertions for new fields.
- `tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs` — live `ApplyProfile` case + `RecordingSpatializer`.
- `docs/SETUP.md` — manual checklists for both features.

---

### Task 1: `RadioProfile` raises `INotifyPropertyChanged`

**Files:**
- Modify: `src/LogiBuddy.Core/Config/RadioProfile.cs`
- Create: `tests/LogiBuddy.Core.Tests/Config/RadioProfileTests.cs`
- Modify: `tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `RadioProfile : INotifyPropertyChanged` — every existing public property unchanged in name/type, now raising `PropertyChanged` on a real change. No new members beyond the event.

- [ ] **Step 1: Write the failing tests**

Create `tests/LogiBuddy.Core.Tests/Config/RadioProfileTests.cs`:

```csharp
using LogiBuddy.Core.Config;
using Xunit;

namespace LogiBuddy.Core.Tests.Config;

public class RadioProfileTests
{
    [Fact]
    public void SettingProperties_RaisesPropertyChangedInOrderWithNames()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.HighPassHz = 555f;
        profile.WetDryMix = 0.5f;
        profile.MaxYawDegrees = 42f;

        Assert.Equal(
            new[] { nameof(RadioProfile.HighPassHz), nameof(RadioProfile.WetDryMix), nameof(RadioProfile.MaxYawDegrees) },
            changed);
    }

    [Fact]
    public void SettingProperty_ToItsCurrentValue_RaisesNothing()
    {
        var profile = new RadioProfile { NoiseLevel = 0.1f };
        var raised = false;
        profile.PropertyChanged += (_, _) => raised = true;

        profile.NoiseLevel = 0.1f;

        Assert.False(raised);
    }

    [Fact]
    public void AssigningHotkey_RaisesHotkeyPropertyChanged()
    {
        var profile = new RadioProfile();
        var changed = new List<string?>();
        profile.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        profile.Hotkey = new FreelookHotkey { VirtualKeyCode = 0x20 };

        Assert.Contains(nameof(RadioProfile.Hotkey), changed);
    }
}
```

Add to `tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs` (new `[Fact]` inside the class):

```csharp
    [Fact]
    public void Load_ProfileJsonMissingNewerFields_KeepsDefaults()
    {
        var store = CreateStore(out var dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "Legacy.json"),
                "{ \"Name\": \"Legacy\", \"HighPassHz\": 600 }");

            var loaded = store.Load("Legacy");

            Assert.Equal("Legacy", loaded.Name);
            Assert.Equal(600f, loaded.HighPassHz);
            Assert.Equal(1.0f, loaded.WetDryMix);           // default retained
            Assert.Equal(0x12, loaded.Hotkey.VirtualKeyCode); // default retained
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LogiBuddy.Core.Tests/LogiBuddy.Core.Tests.csproj --filter "RadioProfileTests|ConfigStoreTests"`
Expected: `RadioProfileTests` FAIL to build/assert — `RadioProfile` has no `PropertyChanged`. `Load_ProfileJsonMissingNewerFields_KeepsDefaults` PASSES already (proves current serialization tolerance; keep it).

- [ ] **Step 3: Rewrite `RadioProfile.cs` with backing fields and change notification**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LogiBuddy.Core.Config;

public class RadioProfile : INotifyPropertyChanged
{
    private string _name = "Default";
    private string _sourceProcessName = "";
    private string _outputDeviceId = "";
    private float _sourceX = 0.3f;
    private float _sourceY = -0.1f;
    private float _sourceZ = 0.2f;
    private float _highPassHz = 400f;
    private float _lowPassHz = 3400f;
    private float _distortionDrive = 0.2f;
    private float _compressorThresholdDb = -18f;
    private float _compressorRatio = 4f;
    private float _noiseLevel = 0.05f;
    private float _wetDryMix = 1.0f;
    private FreelookHotkey _hotkey = new();
    private float _mouseSensitivity = 0.15f;
    private float _maxYawDegrees = 90f;
    private float _maxPitchDegrees = 60f;
    private float _springBackRatePerSecond = 720f;

    public string Name { get => _name; set => SetField(ref _name, value); }
    public string SourceProcessName { get => _sourceProcessName; set => SetField(ref _sourceProcessName, value); }
    public string OutputDeviceId { get => _outputDeviceId; set => SetField(ref _outputDeviceId, value); }

    // Fixed source position in listener-relative meters: +X right, +Y up, +Z forward.
    public float SourceX { get => _sourceX; set => SetField(ref _sourceX, value); }
    public float SourceY { get => _sourceY; set => SetField(ref _sourceY, value); }
    public float SourceZ { get => _sourceZ; set => SetField(ref _sourceZ, value); }

    public float HighPassHz { get => _highPassHz; set => SetField(ref _highPassHz, value); }
    public float LowPassHz { get => _lowPassHz; set => SetField(ref _lowPassHz, value); }
    public float DistortionDrive { get => _distortionDrive; set => SetField(ref _distortionDrive, value); }
    public float CompressorThresholdDb { get => _compressorThresholdDb; set => SetField(ref _compressorThresholdDb, value); }
    public float CompressorRatio { get => _compressorRatio; set => SetField(ref _compressorRatio, value); }
    public float NoiseLevel { get => _noiseLevel; set => SetField(ref _noiseLevel, value); }
    public float WetDryMix { get => _wetDryMix; set => SetField(ref _wetDryMix, value); }

    public FreelookHotkey Hotkey { get => _hotkey; set => SetField(ref _hotkey, value); }
    public float MouseSensitivity { get => _mouseSensitivity; set => SetField(ref _mouseSensitivity, value); }
    public float MaxYawDegrees { get => _maxYawDegrees; set => SetField(ref _maxYawDegrees, value); }
    public float MaxPitchDegrees { get => _maxPitchDegrees; set => SetField(ref _maxPitchDegrees, value); }
    public float SpringBackRatePerSecond { get => _springBackRatePerSecond; set => SetField(ref _springBackRatePerSecond, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LogiBuddy.Core.Tests/LogiBuddy.Core.Tests.csproj`
Expected: all pass (35 total — the 32 existing plus the 3 new `RadioProfileTests`; `ConfigStoreTests` now has 4).

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Config/RadioProfile.cs tests/LogiBuddy.Core.Tests/Config/RadioProfileTests.cs tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs
git commit -m "feat: RadioProfile raises INotifyPropertyChanged"
```

---

### Task 2: Document `RadioPipeline.ApplyProfile` as live-safe and cover it

**Files:**
- Modify: `src/LogiBuddy.Core/Pipeline/RadioPipeline.cs:72`
- Modify: `tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs`

**Interfaces:**
- Consumes: `RadioProfile` change notification (Task 1) — not directly used here, but this task proves `ApplyProfile` is callable after `Start()`.
- Produces: no API change. A `RecordingSpatializer` test double other tasks may reuse.

- [ ] **Step 1: Write the failing test**

Add to `tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs`:

```csharp
public class RecordingSpatializer : ISpatializer
{
    public (float x, float y, float z) LastPosition { get; private set; }
    public void SetSourcePosition(float x, float y, float z) => LastPosition = (x, y, z);
    public void SetListenerOrientation(float yawDegrees, float pitchDegrees) { }
    public void Process(float[] monoInput, int count, float[] stereoOutputInterleaved)
    {
        for (int i = 0; i < count; i++)
        {
            stereoOutputInterleaved[i * 2] = monoInput[i];
            stereoOutputInterleaved[i * 2 + 1] = monoInput[i];
        }
    }
}
```

```csharp
    [Fact]
    public void ApplyProfile_AfterStart_PushesUpdatedSourcePositionToSpatializer()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var profile = new RadioProfile { WetDryMix = 0f };
        var fakeInput = new LogiBuddy.Core.Tests.Tracking.FakeMouseInputSource();
        var tracker = new FreelookTracker(fakeInput, profile);
        var effectChain = new RadioEffectChain(48000f);
        var spatializer = new RecordingSpatializer();
        var pipeline = new RadioPipeline(capture, output, spatializer, spatializer, effectChain, tracker, frameSize: 3);
        pipeline.ApplyProfile(profile);

        pipeline.Start();

        profile.SourceX = 0.9f;
        profile.SourceZ = -0.4f;
        pipeline.ApplyProfile(profile);

        Assert.Equal((0.9f, -0.1f, -0.4f), spatializer.LastPosition);
    }
```

- [ ] **Step 2: Run test to verify it passes (behaviour already works) — then make it meaningful by asserting the pre-condition first**

Run: `dotnet test tests/LogiBuddy.Core.Tests/LogiBuddy.Core.Tests.csproj --filter RadioPipelineTests`
Expected: PASS. (This is a characterization test; `ApplyProfile` already works post-Start. The value is locking that in against regressions and giving the codebase a `RecordingSpatializer`.)

- [ ] **Step 3: Add the doc comment**

In `src/LogiBuddy.Core/Pipeline/RadioPipeline.cs`, replace the line `    public void ApplyProfile(RadioProfile profile)` and add the comment directly above it:

```csharp
    /// Re-applies every live-tunable value (DSP parameters, freelook tuning,
    /// source position) to the running components. Safe to call on a running
    /// pipeline from the UI thread: each target is a plain scalar the audio
    /// thread reads once per block and individual float writes are atomic, so
    /// the worst case is one block blended across an old and new value. It
    /// deliberately touches nothing in capture, output, or the input hook.
    public void ApplyProfile(RadioProfile profile)
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test LogiBuddy.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Pipeline/RadioPipeline.cs tests/LogiBuddy.Core.Tests/Pipeline/RadioPipelineTests.cs
git commit -m "test: characterize live ApplyProfile on a running pipeline"
```

---

### Task 3: `MainViewModel` applies live edits and flags restarts

**Files:**
- Modify: `src/LogiBuddy.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `RadioProfile.PropertyChanged` (Task 1); `RadioPipeline.ApplyProfile` (Task 2).
- Produces: `MainViewModel.RestartRequired` (`bool`, INPC) — bound by the window in Task 5. `MainViewModel` now keeps `Profile.PropertyChanged` wired for the lifetime of each `Profile` instance.

No automated test (no WPF test project — matches the codebase). Verified by the manual checklist in Step 4.

- [ ] **Step 1: Add the `RestartRequired` property and the live/restart name sets**

In `MainViewModel.cs`, add these members (place the `RestartRequired` property near `BufferUnderrunCount`, and the two `HashSet`s as `private static readonly` fields near the top of the class):

```csharp
    private bool _restartRequired;
    public bool RestartRequired
    {
        get => _restartRequired;
        set { _restartRequired = value; OnPropertyChanged(); }
    }

    private static readonly HashSet<string> LiveProfileProperties = new()
    {
        nameof(RadioProfile.HighPassHz), nameof(RadioProfile.LowPassHz),
        nameof(RadioProfile.DistortionDrive), nameof(RadioProfile.CompressorThresholdDb),
        nameof(RadioProfile.CompressorRatio), nameof(RadioProfile.NoiseLevel),
        nameof(RadioProfile.WetDryMix),
        nameof(RadioProfile.SourceX), nameof(RadioProfile.SourceY), nameof(RadioProfile.SourceZ),
        nameof(RadioProfile.MouseSensitivity), nameof(RadioProfile.MaxYawDegrees),
        nameof(RadioProfile.MaxPitchDegrees), nameof(RadioProfile.SpringBackRatePerSecond),
    };

    private static readonly HashSet<string> RestartRequiredProfileProperties = new()
    {
        nameof(RadioProfile.SourceProcessName), nameof(RadioProfile.OutputDeviceId),
        nameof(RadioProfile.Hotkey),
        // AutoRouteSource / RouteSourceToDeviceId are added to this set in Task 7.
    };
```

- [ ] **Step 2: Wire the handler**

Add the handler method:

```csharp
    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null) return;

        if (LiveProfileProperties.Contains(e.PropertyName))
        {
            if (_pipeline is null) return;
            try
            {
                _pipeline.ApplyProfile(Profile);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Couldn't apply change: {ex.Message}";
            }
        }
        else if (RestartRequiredProfileProperties.Contains(e.PropertyName) && _pipeline is not null)
        {
            RestartRequired = true;
        }
    }
```

In the constructor, after `RefreshSources();`, add:

```csharp
        Profile.PropertyChanged += OnProfilePropertyChanged;
```

Replace `LoadProfile` with:

```csharp
    private void LoadProfile(string name)
    {
        Profile.PropertyChanged -= OnProfilePropertyChanged;
        Profile = _configStore.Load(name);
        Profile.PropertyChanged += OnProfilePropertyChanged;
        OnPropertyChanged(nameof(Profile));

        if (_pipeline is not null)
        {
            try { _pipeline.ApplyProfile(Profile); }
            catch (Exception ex) { StatusMessage = $"Couldn't apply loaded profile: {ex.Message}"; }
            RestartRequired = true;
        }
    }
```

- [ ] **Step 3: Clear the flag on Start and Stop**

In `Start()`, as the final statement inside the `try` block (after `_underrunTimer.Start();`):

```csharp
            RestartRequired = false;
```

In `Stop()`, next to `StatusMessage = "Stopped";`:

```csharp
        RestartRequired = false;
```

- [ ] **Step 4: Build, then manually verify**

Run: `dotnet build LogiBuddy.sln`
Expected: builds clean.

Manual (run the app, tick **Test tone (440 Hz)**, Refresh, pick **LogiBuddy.App**, Start):
1. Drag **Distortion**, **Static/Noise**, **Wet/Dry Mix** — each change is audible immediately, no Stop/Start.
2. Drag the position marker — image shifts live.
3. Change the **Source** dropdown — audio does not change (the "restart" hint appears once Task 5 is in; for now confirm no crash and no audible change).
4. Click **Stop**.

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.App/ViewModels/MainViewModel.cs
git commit -m "feat: apply profile edits to the running pipeline in real time"
```

---

### Task 4: `HotkeyCaptureControl`

**Files:**
- Create: `src/LogiBuddy.App/Controls/HotkeyCaptureControl.xaml`
- Create: `src/LogiBuddy.App/Controls/HotkeyCaptureControl.xaml.cs`

**Interfaces:**
- Consumes: `FreelookHotkey` (`LogiBuddy.Core.Config`).
- Produces: `HotkeyCaptureControl` with a two-way `Hotkey` dependency property of type `FreelookHotkey`. Click → "Press a key or mouse button…" → next key (`PreviewKeyDown`) or mouse button (`PreviewMouseDown`) is captured as a VK code; `Escape` cancels.

No automated test (WPF control). Verified in Task 5's checklist.

- [ ] **Step 1: Create the XAML**

`src/LogiBuddy.App/Controls/HotkeyCaptureControl.xaml`:

```xml
<UserControl x:Class="LogiBuddy.App.Controls.HotkeyCaptureControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Button x:Name="CaptureButton" Width="160" Click="OnCaptureClick"
            PreviewKeyDown="OnPreviewKeyDown" PreviewMouseDown="OnPreviewMouseDown" />
</UserControl>
```

- [ ] **Step 2: Create the code-behind**

`src/LogiBuddy.App/Controls/HotkeyCaptureControl.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LogiBuddy.Core.Config;

namespace LogiBuddy.App.Controls;

public partial class HotkeyCaptureControl : UserControl
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(FreelookHotkey), typeof(HotkeyCaptureControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyChanged));

    public FreelookHotkey? Hotkey
    {
        get => (FreelookHotkey?)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private bool _capturing;

    public HotkeyCaptureControl()
    {
        InitializeComponent();
        UpdateLabel();
    }

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HotkeyCaptureControl)d).UpdateLabel();

    private void UpdateLabel()
    {
        if (_capturing) { CaptureButton.Content = "Press a key or mouse button…"; return; }
        int vk = Hotkey?.VirtualKeyCode ?? 0;
        CaptureButton.Content = vk == 0 ? "(click to set)" : VirtualKeyName(vk);
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        UpdateLabel();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        if (e.Key == Key.Escape) { _capturing = false; UpdateLabel(); return; }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return; // unmapped; keep waiting

        _capturing = false;
        Hotkey = new FreelookHotkey { VirtualKeyCode = vk };
        UpdateLabel();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing) return; // first click (to arm capture) falls through to OnCaptureClick
        e.Handled = true;

        int vk = e.ChangedButton switch
        {
            MouseButton.Left => 0x01,
            MouseButton.Right => 0x02,
            MouseButton.Middle => 0x04,
            MouseButton.XButton1 => 0x05,
            MouseButton.XButton2 => 0x06,
            _ => 0,
        };
        if (vk == 0) return;

        _capturing = false;
        Hotkey = new FreelookHotkey { VirtualKeyCode = vk };
        UpdateLabel();
    }

    private static string VirtualKeyName(int vk) => vk switch
    {
        0x01 => "Mouse Left",
        0x02 => "Mouse Right",
        0x04 => "Mouse Middle",
        0x05 => "Mouse X1",
        0x06 => "Mouse X2",
        _ => KeyInterop.KeyFromVirtualKey(vk).ToString(),
    };
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/LogiBuddy.App/LogiBuddy.App.csproj`
Expected: builds clean.

- [ ] **Step 4: Commit**

```bash
git add src/LogiBuddy.App/Controls/HotkeyCaptureControl.xaml src/LogiBuddy.App/Controls/HotkeyCaptureControl.xaml.cs
git commit -m "feat: add HotkeyCaptureControl for rebinding the freelook key"
```

---

### Task 5: Freelook controls and restart hint in `MainWindow`

**Files:**
- Modify: `src/LogiBuddy.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `MainViewModel.RestartRequired` (Task 3); `HotkeyCaptureControl` (Task 4); `Profile.MouseSensitivity` / `MaxYawDegrees` / `MaxPitchDegrees` / `SpringBackRatePerSecond` / `Hotkey` (Task 1).
- Produces: the finished window layout for both features' non-routing UI.

No automated test. Verified in Step 3.

- [ ] **Step 1: Replace `MainWindow.xaml` with the extended layout**

```xml
<Window x:Class="LogiBuddy.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:LogiBuddy.App.ViewModels"
        xmlns:controls="clr-namespace:LogiBuddy.App.Controls"
        Title="LogiBuddy" Height="900" Width="640">
    <Window.DataContext>
        <local:MainViewModel />
    </Window.DataContext>
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
    </Window.Resources>
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" /> <!-- 0 source -->
            <RowDefinition Height="Auto" /> <!-- 1 high-pass -->
            <RowDefinition Height="Auto" /> <!-- 2 low-pass -->
            <RowDefinition Height="Auto" /> <!-- 3 distortion -->
            <RowDefinition Height="Auto" /> <!-- 4 comp thresh -->
            <RowDefinition Height="Auto" /> <!-- 5 comp ratio -->
            <RowDefinition Height="Auto" /> <!-- 6 noise -->
            <RowDefinition Height="Auto" /> <!-- 7 wet/dry -->
            <RowDefinition Height="Auto" /> <!-- 8 freelook hotkey -->
            <RowDefinition Height="Auto" /> <!-- 9 mouse sensitivity -->
            <RowDefinition Height="Auto" /> <!-- 10 max yaw -->
            <RowDefinition Height="Auto" /> <!-- 11 max pitch -->
            <RowDefinition Height="Auto" /> <!-- 12 spring-back -->
            <RowDefinition Height="Auto" /> <!-- 13 buttons + restart hint -->
            <RowDefinition Height="Auto" /> <!-- 14 canvas -->
            <RowDefinition Height="*" />    <!-- 15 status -->
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Source:" VerticalAlignment="Center" Margin="0,0,8,0" />
            <ComboBox Width="200" ItemsSource="{Binding AvailableSources}" DisplayMemberPath="DisplayName"
                      SelectedValuePath="ProcessName" SelectedValue="{Binding Profile.SourceProcessName}" />
            <Button Content="Refresh" Command="{Binding RefreshSourcesCommand}" Margin="8,0,0,0" />
            <CheckBox Content="Test tone (440 Hz)" IsChecked="{Binding TestToneEnabled}"
                      VerticalAlignment="Center" Margin="16,0,0,0"
                      ToolTip="Plays a soft sine from this app so per-process loopback has something to capture. Pick &quot;LogiBuddy.App&quot; as the source to hear it through the radio pipeline." />
        </StackPanel>

        <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="High-pass (Hz):" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="50" Maximum="2000" Width="200" Value="{Binding Profile.HighPassHz}" />
        </StackPanel>

        <StackPanel Grid.Row="2" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Low-pass (Hz):" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="1000" Maximum="8000" Width="200" Value="{Binding Profile.LowPassHz}" />
        </StackPanel>

        <StackPanel Grid.Row="3" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Distortion:" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="0" Maximum="1" Width="200" Value="{Binding Profile.DistortionDrive}" />
        </StackPanel>

        <StackPanel Grid.Row="4" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Compressor Thresh (dB):" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="-40" Maximum="0" Width="200" Value="{Binding Profile.CompressorThresholdDb}" />
        </StackPanel>

        <StackPanel Grid.Row="5" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Compressor Ratio:" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="1" Maximum="20" Width="200" Value="{Binding Profile.CompressorRatio}" />
        </StackPanel>

        <StackPanel Grid.Row="6" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Static/Noise:" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="0" Maximum="0.3" Width="200" Value="{Binding Profile.NoiseLevel}" />
        </StackPanel>

        <StackPanel Grid.Row="7" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Wet/Dry Mix:" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="0" Maximum="1" Width="200" Value="{Binding Profile.WetDryMix}" />
        </StackPanel>

        <StackPanel Grid.Row="8" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Freelook hotkey:" VerticalAlignment="Center" Width="140" />
            <controls:HotkeyCaptureControl Hotkey="{Binding Profile.Hotkey, Mode=TwoWay}" />
            <TextBlock Text=" restart to apply" VerticalAlignment="Center" Foreground="DarkOrange" Margin="8,0,0,0" />
        </StackPanel>

        <StackPanel Grid.Row="9" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Mouse sensitivity:" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="0.01" Maximum="1.0" Width="200" Value="{Binding Profile.MouseSensitivity}" />
        </StackPanel>

        <StackPanel Grid.Row="10" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Max yaw (°):" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="0" Maximum="180" Width="200" Value="{Binding Profile.MaxYawDegrees}" />
        </StackPanel>

        <StackPanel Grid.Row="11" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Max pitch (°):" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="0" Maximum="90" Width="200" Value="{Binding Profile.MaxPitchDegrees}" />
        </StackPanel>

        <StackPanel Grid.Row="12" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Spring-back (°/s):" VerticalAlignment="Center" Width="140" />
            <Slider Minimum="0" Maximum="2000" Width="200" Value="{Binding Profile.SpringBackRatePerSecond}" />
        </StackPanel>

        <StackPanel Grid.Row="13" Orientation="Horizontal" Margin="0,0,0,8">
            <Button Content="Start" Command="{Binding StartCommand}" Width="80" Margin="0,0,8,0" />
            <Button Content="Stop" Command="{Binding StopCommand}" Width="80" Margin="0,0,8,0" />
            <Button Content="Save Profile" Command="{Binding SaveProfileCommand}" Width="100" />
            <TextBlock Margin="12,0,0,0" VerticalAlignment="Center" Foreground="DarkOrange"
                       Visibility="{Binding RestartRequired, Converter={StaticResource BoolToVis}}"
                       Text="Restart (Stop then Start) to apply source / output / hotkey changes." />
        </StackPanel>

        <controls:SourcePositionCanvas Grid.Row="14"
            SourceX="{Binding Profile.SourceX}"
            SourceZ="{Binding Profile.SourceZ}"
            HorizontalAlignment="Left" />

        <StackPanel Grid.Row="15" Orientation="Horizontal" Margin="0,8,0,0" VerticalAlignment="Top">
            <TextBlock Text="{Binding StatusMessage}" FontWeight="Bold" TextWrapping="Wrap" MaxWidth="400" />
            <TextBlock Text="{Binding BufferUnderrunCount, StringFormat=' (underruns: {0})'}" Margin="8,0,0,0" />
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Build**

Run: `dotnet build src/LogiBuddy.App/LogiBuddy.App.csproj`
Expected: builds clean.

- [ ] **Step 3: Manual verification**

Run the app. With **Test tone** on and source = `LogiBuddy.App`, click **Start**:
1. Freelook hotkey button shows the current key ("LeftAlt"). Click it → "Press a key or mouse button…" → press a key → it shows the new key. `Escape` mid-capture cancels.
2. Drag **Mouse sensitivity**, **Max yaw**, **Max pitch**, **Spring-back** while holding the freelook key and moving the mouse — response changes with no restart.
3. Change **Source** or rebind the hotkey → the orange "Restart …" line appears next to the buttons. Click **Stop** then **Start** → it clears.

- [ ] **Step 4: Commit**

```bash
git add src/LogiBuddy.App/MainWindow.xaml
git commit -m "feat: add freelook controls and a restart-required hint to the window"
```

---

### Task 6: `RenderDeviceEnumerator`

**Files:**
- Create: `src/LogiBuddy.Core/Audio/RenderDeviceEnumerator.cs`
- Create: `tests/LogiBuddy.Core.Tests/Audio/RenderDeviceEnumeratorTests.cs`

**Interfaces:**
- Consumes: NAudio `MMDeviceEnumerator` (already used by `AudioSessionEnumerator`).
- Produces: `record RenderDeviceInfo(string Id, string FriendlyName)`; `RenderDeviceEnumerator.ListRenderDevices() -> IReadOnlyList<RenderDeviceInfo>`; `RenderDeviceEnumerator.LooksLikeVirtualCable(string friendlyName) -> bool`.

- [ ] **Step 1: Write the failing tests**

`tests/LogiBuddy.Core.Tests/Audio/RenderDeviceEnumeratorTests.cs`:

```csharp
using LogiBuddy.Core.Audio;
using Xunit;

namespace LogiBuddy.Core.Tests.Audio;

public class RenderDeviceEnumeratorTests
{
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)")]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)")]
    [InlineData("Virtual Audio Device")]
    public void LooksLikeVirtualCable_TrueForVirtualDevices(string name)
        => Assert.True(RenderDeviceEnumerator.LooksLikeVirtualCable(name));

    [Theory]
    [InlineData("Speakers (Focusrite USB Audio)")]
    [InlineData("LG ULTRAGEAR (NVIDIA High Definition Audio)")]
    [InlineData("Headphones (Realtek(R) Audio)")]
    [InlineData("")]
    public void LooksLikeVirtualCable_FalseForRealDevices(string name)
        => Assert.False(RenderDeviceEnumerator.LooksLikeVirtualCable(name));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LogiBuddy.Core.Tests/LogiBuddy.Core.Tests.csproj --filter RenderDeviceEnumeratorTests`
Expected: FAIL — `RenderDeviceEnumerator` does not exist.

- [ ] **Step 3: Write `RenderDeviceEnumerator.cs`**

```csharp
using NAudio.CoreAudioApi;

namespace LogiBuddy.Core.Audio;

public record RenderDeviceInfo(string Id, string FriendlyName);

/// Active render (playback) endpoints for the routing UI, plus a name
/// heuristic used to pre-select a virtual-cable device.
public static class RenderDeviceEnumerator
{
    private static readonly string[] VirtualCableMarkers =
        { "vb-audio", "vb audio", "cable", "voicemeeter", "virtual" };

    public static IReadOnlyList<RenderDeviceInfo> ListRenderDevices()
    {
        var results = new List<RenderDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                    results.Add(new RenderDeviceInfo(device.ID, device.FriendlyName));
            }
        }
        catch (Exception)
        {
            // No audio subsystem / no devices — return whatever was collected.
        }
        return results;
    }

    public static bool LooksLikeVirtualCable(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;
        string lower = friendlyName.ToLowerInvariant();
        return VirtualCableMarkers.Any(m => lower.Contains(m));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LogiBuddy.Core.Tests/LogiBuddy.Core.Tests.csproj --filter RenderDeviceEnumeratorTests`
Expected: PASS (8 cases).

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Audio/RenderDeviceEnumerator.cs tests/LogiBuddy.Core.Tests/Audio/RenderDeviceEnumeratorTests.cs
git commit -m "feat: add RenderDeviceEnumerator with a virtual-cable name heuristic"
```

---

### Task 7: Routing fields on `RadioProfile`

**Files:**
- Modify: `src/LogiBuddy.Core/Config/RadioProfile.cs`
- Modify: `src/LogiBuddy.App/ViewModels/MainViewModel.cs` (add the two names to `RestartRequiredProfileProperties`)
- Modify: `tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: the `SetField` pattern from Task 1.
- Produces: `RadioProfile.AutoRouteSource` (`bool`, default `true`); `RadioProfile.RouteSourceToDeviceId` (`string`, default `""`). Both raise `PropertyChanged`.

- [ ] **Step 1: Extend the round-trip test and legacy-load test**

In `ConfigStoreTests.cs`, in `SaveThenLoad_RoundTripsAllFields`, add to the `new RadioProfile { ... }` initializer:

```csharp
                AutoRouteSource = false,
                RouteSourceToDeviceId = "device-cable-1",
```

and add assertions before the `finally`:

```csharp
            Assert.False(loaded.AutoRouteSource);
            Assert.Equal("device-cable-1", loaded.RouteSourceToDeviceId);
```

In `Load_ProfileJsonMissingNewerFields_KeepsDefaults`, add:

```csharp
            Assert.True(loaded.AutoRouteSource);            // default retained
            Assert.Equal("", loaded.RouteSourceToDeviceId); // default retained
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LogiBuddy.Core.Tests/LogiBuddy.Core.Tests.csproj --filter ConfigStoreTests`
Expected: FAIL to build — `AutoRouteSource` / `RouteSourceToDeviceId` do not exist.

- [ ] **Step 3: Add the fields to `RadioProfile.cs`**

Add backing fields next to the others:

```csharp
    private bool _autoRouteSource = true;
    private string _routeSourceToDeviceId = "";
```

Add properties after `SpringBackRatePerSecond`:

```csharp
    /// When true, MainViewModel routes the source process's audio to
    /// RouteSourceToDeviceId (or an auto-detected virtual cable) on Start and
    /// restores it on Stop, so only the processed radio output is audible.
    public bool AutoRouteSource { get => _autoRouteSource; set => SetField(ref _autoRouteSource, value); }

    /// MMDevice id of the render endpoint to route the source to. Empty means
    /// "auto-detect a virtual cable at Start".
    public string RouteSourceToDeviceId { get => _routeSourceToDeviceId; set => SetField(ref _routeSourceToDeviceId, value); }
```

- [ ] **Step 4: Add the names to the restart set in `MainViewModel.cs`**

Replace the comment line in `RestartRequiredProfileProperties` with the real entries:

```csharp
    private static readonly HashSet<string> RestartRequiredProfileProperties = new()
    {
        nameof(RadioProfile.SourceProcessName), nameof(RadioProfile.OutputDeviceId),
        nameof(RadioProfile.Hotkey),
        nameof(RadioProfile.AutoRouteSource), nameof(RadioProfile.RouteSourceToDeviceId),
    };
```

- [ ] **Step 5: Run tests + build**

Run: `dotnet test LogiBuddy.sln` and `dotnet build LogiBuddy.sln`
Expected: all pass, builds clean.

- [ ] **Step 6: Commit**

```bash
git add src/LogiBuddy.Core/Config/RadioProfile.cs src/LogiBuddy.App/ViewModels/MainViewModel.cs tests/LogiBuddy.Core.Tests/Config/ConfigStoreTests.cs
git commit -m "feat: add AutoRouteSource / RouteSourceToDeviceId to the profile"
```

---

### Task 8: `WindowsAppAudioRouter` over `IAudioPolicyConfig`

**Files:**
- Create: `src/LogiBuddy.Core/Audio/AudioPolicyConfigInterop.cs`
- Create: `src/LogiBuddy.Core/Audio/SourceAudioRouter.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `interface ISourceAudioRouter { bool IsSupported { get; } AppAudioRoute GetCurrentRoute(int processId); void RouteProcess(int processId, string renderDeviceId); void RestoreProcess(int processId, AppAudioRoute previous); }`
  - `sealed record AppAudioRoute(string Console, string Multimedia, string Communications)` with `static AppAudioRoute None { get; } = new("", "", "")`.
  - `sealed class WindowsAppAudioRouter : ISourceAudioRouter`.
  - `sealed class SourceRoutingException : Exception` (takes a message + int hresult; exposes `int HResult`).

No xUnit test — undocumented COM against live OS state (same call as `WasapiProcessLoopbackInterop`). Verified by a probe (Step 4) and Task 10/12 manual checklists.

> **Interop risk:** the Windows 11 `IAudioPolicyConfig` vtable has seven undocumented slots before `SetPersistedDefaultAudioEndpoint`. The declaration below matches EarTrumpet/SoundSwitch as of 2026. Step 4 is a mandatory probe: if it returns a clean HRESULT the layout is right; if it access-violates or returns garbage, re-count the padding slots against the current SoundSwitch `AudioSwitcher` interop source before proceeding.

- [ ] **Step 1: Write `AudioPolicyConfigInterop.cs`**

```csharp
using System.Runtime.InteropServices;

namespace LogiBuddy.Core.Audio;

internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }

internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

/// Undocumented Windows 11 per-application audio endpoint routing, reached via
/// the WinRT activation factory for "Windows.Media.Internal.AudioPolicyConfig".
/// Only the Windows 11 (21H2+) interface variant is declared.
internal static class AudioPolicyConfigInterop
{
    // DEVINTERFACE_AUDIO_RENDER — the endpoint-id form the API expects.
    private const string AudioRenderInterface = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        [In] ref Guid iid,
        [MarshalAs(UnmanagedType.IInspectable)] out object factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactoryWin11
    {
        // --- seven undocumented slots; never called, only vtable padding ---
        void _slot0(); void _slot1(); void _slot2(); void _slot3();
        void _slot4(); void _slot5(); void _slot6();

        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceIdHString);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceIdHString);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }

    private static IAudioPolicyConfigFactoryWin11 CreateFactory()
    {
        var iid = typeof(IAudioPolicyConfigFactoryWin11).GUID;
        RoGetActivationFactory("Windows.Media.Internal.AudioPolicyConfig", ref iid, out object factory);
        return (IAudioPolicyConfigFactoryWin11)factory;
    }

    /// Wraps an MMDevice id ("{0.0.0.00000000}.{guid}") into the
    /// "\\?\SWD#MMDEVAPI#...#{render-interface}" form. Empty in -> empty out
    /// (which clears the override).
    private static string ToEndpointId(string mmDeviceId) =>
        string.IsNullOrEmpty(mmDeviceId)
            ? ""
            : $@"\\?\SWD#MMDEVAPI#{mmDeviceId}#{AudioRenderInterface}";

    private static string FromEndpointId(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId)) return "";
        // "\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{iface}" -> "{0.0.0.00000000}.{guid}"
        int hashPrefix = endpointId.IndexOf("MMDEVAPI#", StringComparison.OrdinalIgnoreCase);
        if (hashPrefix < 0) return endpointId;
        string tail = endpointId[(hashPrefix + "MMDEVAPI#".Length)..];
        int lastHash = tail.LastIndexOf('#');
        return lastHash >= 0 ? tail[..lastHash] : tail;
    }

    /// True if the activation factory can be created and queried for the
    /// Windows 11 interface.
    public static bool ProbeSupported()
    {
        try { _ = CreateFactory(); return true; }
        catch (Exception) { return false; }
    }

    public static int SetEndpoint(uint processId, ERole role, string mmDeviceId)
    {
        var factory = CreateFactory();
        IntPtr hstr = IntPtr.Zero;
        try
        {
            string endpointId = ToEndpointId(mmDeviceId);
            WindowsCreateString(endpointId, endpointId.Length, out hstr);
            return factory.SetPersistedDefaultAudioEndpoint(processId, EDataFlow.eRender, role, hstr);
        }
        finally
        {
            if (hstr != IntPtr.Zero) WindowsDeleteString(hstr);
        }
    }

    public static (int hr, string mmDeviceId) GetEndpoint(uint processId, ERole role)
    {
        var factory = CreateFactory();
        int hr = factory.GetPersistedDefaultAudioEndpoint(processId, EDataFlow.eRender, role, out IntPtr hstr);
        if (hr != 0 || hstr == IntPtr.Zero) return (hr, "");
        try
        {
            IntPtr buf = WindowsGetStringRawBuffer(hstr, out uint len);
            string endpointId = buf == IntPtr.Zero ? "" : Marshal.PtrToStringUni(buf, (int)len) ?? "";
            return (hr, FromEndpointId(endpointId));
        }
        finally
        {
            WindowsDeleteString(hstr);
        }
    }
}
```

- [ ] **Step 2: Write `SourceAudioRouter.cs`**

```csharp
namespace LogiBuddy.Core.Audio;

public sealed record AppAudioRoute(string Console, string Multimedia, string Communications)
{
    public static AppAudioRoute None { get; } = new("", "", "");
}

public sealed class SourceRoutingException : Exception
{
    public SourceRoutingException(string message, int hresult) : base(message) => HResult = hresult;
}

public interface ISourceAudioRouter
{
    bool IsSupported { get; }
    AppAudioRoute GetCurrentRoute(int processId);
    void RouteProcess(int processId, string renderDeviceId);
    void RestoreProcess(int processId, AppAudioRoute previous);
}

/// Routes a process's default render endpoint using the undocumented Windows 11
/// IAudioPolicyConfig API. Unsupported (IsSupported == false) on Windows 10 or
/// if the activation factory cannot be reached.
public sealed class WindowsAppAudioRouter : ISourceAudioRouter
{
    private readonly bool _supported;

    public WindowsAppAudioRouter()
    {
        _supported = Environment.OSVersion.Version.Build >= 22000
                     && AudioPolicyConfigInterop.ProbeSupported();
    }

    public bool IsSupported => _supported;

    public AppAudioRoute GetCurrentRoute(int processId)
    {
        if (!_supported) return AppAudioRoute.None;
        var (_, console) = AudioPolicyConfigInterop.GetEndpoint((uint)processId, ERole.eConsole);
        var (_, multimedia) = AudioPolicyConfigInterop.GetEndpoint((uint)processId, ERole.eMultimedia);
        var (_, comms) = AudioPolicyConfigInterop.GetEndpoint((uint)processId, ERole.eCommunications);
        return new AppAudioRoute(console, multimedia, comms);
    }

    public void RouteProcess(int processId, string renderDeviceId)
    {
        if (!_supported) throw new SourceRoutingException("Per-app routing is not supported on this Windows version.", 0);
        SetRole(processId, ERole.eConsole, renderDeviceId);
        SetRole(processId, ERole.eMultimedia, renderDeviceId);
        SetRole(processId, ERole.eCommunications, renderDeviceId);
    }

    public void RestoreProcess(int processId, AppAudioRoute previous)
    {
        if (!_supported) return;
        SetRole(processId, ERole.eConsole, previous.Console);
        SetRole(processId, ERole.eMultimedia, previous.Multimedia);
        SetRole(processId, ERole.eCommunications, previous.Communications);
    }

    private static void SetRole(int processId, ERole role, string mmDeviceId)
    {
        int hr = AudioPolicyConfigInterop.SetEndpoint((uint)processId, role, mmDeviceId ?? "");
        if (hr != 0)
            throw new SourceRoutingException($"Setting the {role} endpoint failed (hresult 0x{hr:X}).", hr);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build LogiBuddy.sln`
Expected: builds clean.

- [ ] **Step 4: Probe the interop on this machine (mandatory)**

Create a throwaway console probe under the scratchpad directory that references `LogiBuddy.Core`, and run it against the app's own process:

```csharp
using LogiBuddy.Core.Audio;

var router = new WindowsAppAudioRouter();
Console.WriteLine($"IsSupported: {router.IsSupported}");
var route = router.GetCurrentRoute(Environment.ProcessId);
Console.WriteLine($"Current route for self: console='{route.Console}' mm='{route.Multimedia}' comms='{route.Communications}'");
Console.WriteLine("No access violation => vtable layout is correct.");
```

Expected: `IsSupported: True`, the three route strings print (empty is fine — this process has no override), and the process exits 0. If it access-violates or `IsSupported` is unexpectedly `False` on Windows 11, fix the `_slot0..6` padding count in `AudioPolicyConfigInterop` against the current SoundSwitch interop source before continuing. Delete the probe afterward.

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Audio/AudioPolicyConfigInterop.cs src/LogiBuddy.Core/Audio/SourceAudioRouter.cs
git commit -m "feat: add WindowsAppAudioRouter over the Win11 IAudioPolicyConfig API"
```

---

### Task 9: `RouteRecoveryStore`

**Files:**
- Create: `src/LogiBuddy.Core/Config/RouteRecoveryStore.cs`
- Create: `tests/LogiBuddy.Core.Tests/Config/RouteRecoveryStoreTests.cs`

**Interfaces:**
- Consumes: `AppAudioRoute` (Task 8).
- Produces:
  - `sealed record RouteRecoveryEntry(int ProcessId, string Console, string Multimedia, string Communications)`
  - `sealed record RouteRecoveryRecord(string SourceProcessName, IReadOnlyList<RouteRecoveryEntry> Routes)`
  - `sealed class RouteRecoveryStore(string? directoryOverride = null)` with `void Write(RouteRecoveryRecord record)`, `RouteRecoveryRecord? Read()`, `void Delete()`, `bool Exists()`.

- [ ] **Step 1: Write the failing tests**

`tests/LogiBuddy.Core.Tests/Config/RouteRecoveryStoreTests.cs`:

```csharp
using System.IO;
using LogiBuddy.Core.Config;
using Xunit;

namespace LogiBuddy.Core.Tests.Config;

public class RouteRecoveryStoreTests
{
    private static RouteRecoveryStore CreateStore(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "sgr-recov-" + Path.GetRandomFileName());
        return new RouteRecoveryStore(dir);
    }

    [Fact]
    public void WriteThenRead_RoundTripsRecord()
    {
        var store = CreateStore(out var dir);
        try
        {
            var record = new RouteRecoveryRecord("Spotify", new[]
            {
                new RouteRecoveryEntry(1234, "", "", ""),
                new RouteRecoveryEntry(5678, "dev-a", "dev-a", ""),
            });

            store.Write(record);
            var read = store.Read();

            Assert.NotNull(read);
            Assert.Equal("Spotify", read!.SourceProcessName);
            Assert.Equal(2, read.Routes.Count);
            Assert.Equal(5678, read.Routes[1].ProcessId);
            Assert.Equal("dev-a", read.Routes[1].Multimedia);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Read_WhenNoFile_ReturnsNull()
    {
        var store = CreateStore(out var dir);
        try
        {
            Assert.False(store.Exists());
            Assert.Null(store.Read());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var store = CreateStore(out var dir);
        try
        {
            store.Write(new RouteRecoveryRecord("X", Array.Empty<RouteRecoveryEntry>()));
            Assert.True(store.Exists());

            store.Delete();

            Assert.False(store.Exists());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LogiBuddy.Core.Tests/LogiBuddy.Core.Tests.csproj --filter RouteRecoveryStoreTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Write `RouteRecoveryStore.cs`**

```csharp
using System.IO;
using System.Text.Json;

namespace LogiBuddy.Core.Config;

public sealed record RouteRecoveryEntry(int ProcessId, string Console, string Multimedia, string Communications);

public sealed record RouteRecoveryRecord(string SourceProcessName, IReadOnlyList<RouteRecoveryEntry> Routes);

/// Persists what a source process's per-app audio routing was before the app
/// changed it, so a crash between Start and Stop can still be undone on the
/// next launch. One file, %AppData%/LogiBuddy/route-recovery.json.
public sealed class RouteRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public RouteRecoveryStore(string? directoryOverride = null)
    {
        string dir = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LogiBuddy");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "route-recovery.json");
    }

    public bool Exists() => File.Exists(_path);

    public void Write(RouteRecoveryRecord record)
        => File.WriteAllText(_path, JsonSerializer.Serialize(record, JsonOptions));

    public RouteRecoveryRecord? Read()
    {
        if (!File.Exists(_path)) return null;
        try { return JsonSerializer.Deserialize<RouteRecoveryRecord>(File.ReadAllText(_path)); }
        catch (JsonException) { return null; }
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LogiBuddy.Core.Tests/LogiBuddy.Core.Tests.csproj --filter RouteRecoveryStoreTests`
Expected: PASS (3).

- [ ] **Step 5: Commit**

```bash
git add src/LogiBuddy.Core/Config/RouteRecoveryStore.cs tests/LogiBuddy.Core.Tests/Config/RouteRecoveryStoreTests.cs
git commit -m "feat: add RouteRecoveryStore for crash-safe routing restore"
```

---

### Task 10: Routing in `MainViewModel` (Start / Stop / startup / reset)

**Files:**
- Modify: `src/LogiBuddy.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `ISourceAudioRouter` / `WindowsAppAudioRouter` / `AppAudioRoute` / `SourceRoutingException` (Task 8); `RouteRecoveryStore` / `RouteRecoveryRecord` / `RouteRecoveryEntry` (Task 9); `RenderDeviceEnumerator` (Task 6); `Profile.AutoRouteSource` / `RouteSourceToDeviceId` (Task 7).
- Produces: `MainViewModel.ResetSourceRoutingCommand` (`ICommand`); `MainViewModel.AvailableRenderDevices` (`IReadOnlyList<RenderDeviceInfo>`).

No automated test. Verified in Step 6 and Task 12's checklist.

- [ ] **Step 1: Add fields, the render-device list, and the reset command**

Add fields near `_configStore`:

```csharp
    private readonly ISourceAudioRouter _router = new WindowsAppAudioRouter();
    private readonly RouteRecoveryStore _routeRecovery = new();
    private RouteRecoveryRecord? _activeRouteRecovery;
```

Add a property near `AvailableSources`:

```csharp
    public IReadOnlyList<RenderDeviceInfo> AvailableRenderDevices { get; private set; } = Array.Empty<RenderDeviceInfo>();
```

In `RefreshSources()`, after the existing body, also refresh render devices:

```csharp
        AvailableRenderDevices = RenderDeviceEnumerator.ListRenderDevices();
        OnPropertyChanged(nameof(AvailableRenderDevices));
```

Add the command field + wire-up (near the other `ICommand`s and their assignment in the constructor):

```csharp
    public ICommand ResetSourceRoutingCommand { get; }
```

```csharp
        ResetSourceRoutingCommand = new RelayCommand(_ => ResetSourceRouting());
```

- [ ] **Step 2: Add the routing helpers**

```csharp
    /// Resolves the render-device id to route the source to: the profile's
    /// explicit choice if it still exists, else the first auto-detected virtual
    /// cable, else null.
    private string? ResolveRouteDeviceId()
    {
        var devices = RenderDeviceEnumerator.ListRenderDevices();
        if (!string.IsNullOrEmpty(Profile.RouteSourceToDeviceId) &&
            devices.Any(d => d.Id == Profile.RouteSourceToDeviceId))
            return Profile.RouteSourceToDeviceId;

        return devices.FirstOrDefault(d => RenderDeviceEnumerator.LooksLikeVirtualCable(d.FriendlyName))?.Id;
    }

    /// Applies routing for every pid of the source process. Returns null on
    /// success or a user-facing error string on failure (with any partial
    /// routing already rolled back).
    private string? TryRouteSource()
    {
        if (!_router.IsSupported)
            return "Auto-routing needs Windows 11 — turn it off in settings to continue.";

        string? deviceId = ResolveRouteDeviceId();
        if (deviceId is null)
            return "No route-target device — pick one or turn off auto-routing.";

        var pids = System.Diagnostics.Process
            .GetProcessesByName(Profile.SourceProcessName)
            .Select(p => p.Id)
            .ToList();
        if (pids.Count == 0)
            return "Start the source app first, then Start.";

        var entries = pids
            .Select(pid =>
            {
                var r = _router.GetCurrentRoute(pid);
                return new RouteRecoveryEntry(pid, r.Console, r.Multimedia, r.Communications);
            })
            .ToList();

        var record = new RouteRecoveryRecord(Profile.SourceProcessName, entries);
        _routeRecovery.Write(record); // persist BEFORE changing anything

        var routed = new List<int>();
        try
        {
            foreach (var pid in pids)
            {
                _router.RouteProcess(pid, deviceId);
                routed.Add(pid);
            }
        }
        catch (SourceRoutingException ex)
        {
            foreach (var pid in routed)
            {
                var prev = entries.First(e => e.ProcessId == pid);
                try { _router.RestoreProcess(pid, new AppAudioRoute(prev.Console, prev.Multimedia, prev.Communications)); }
                catch (SourceRoutingException) { /* best effort */ }
            }
            _routeRecovery.Delete();
            return $"Couldn't route source audio: {ex.Message} Fix the route device or turn off auto-routing.";
        }

        _activeRouteRecovery = record;
        return null;
    }

    private void RestoreSourceRouting()
    {
        var record = _activeRouteRecovery ?? _routeRecovery.Read();
        if (record is not null)
        {
            foreach (var e in record.Routes)
            {
                try { _router.RestoreProcess(e.ProcessId, new AppAudioRoute(e.Console, e.Multimedia, e.Communications)); }
                catch (SourceRoutingException) { /* best effort */ }
            }
        }
        _routeRecovery.Delete();
        _activeRouteRecovery = null;
    }

    private void ResetSourceRouting()
    {
        var record = _activeRouteRecovery ?? _routeRecovery.Read();
        if (record is not null)
        {
            RestoreSourceRouting();
        }
        else if (_router.IsSupported)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(Profile.SourceProcessName))
            {
                try { _router.RestoreProcess(p.Id, AppAudioRoute.None); }
                catch (SourceRoutingException) { /* best effort */ }
            }
        }
        StatusMessage = "Source routing reset.";
    }
```

- [ ] **Step 3: Call routing from `Start()`**

At the very top of `Start()`, before the `Win32MouseHook? mouseHook = null;` line:

```csharp
        if (Profile.AutoRouteSource)
        {
            string? routeError = TryRouteSource();
            if (routeError is not null)
            {
                StatusMessage = routeError;
                return;
            }
        }
```

In the `catch (Exception ex)` block of `Start()`, before `StatusMessage = $"Failed to start: {ex.Message}";`, undo routing so a later-stage failure doesn't leave the source routed:

```csharp
            if (_activeRouteRecovery is not null) RestoreSourceRouting();
```

- [ ] **Step 4: Call restore from `Stop()`**

In `Stop()`, before `StatusMessage = "Stopped";`:

```csharp
        RestoreSourceRouting();
```

- [ ] **Step 5: Crash recovery on startup**

At the end of the constructor (after `Profile.PropertyChanged += OnProfilePropertyChanged;`):

```csharp
        if (_routeRecovery.Exists())
        {
            RestoreSourceRouting();
            StatusMessage = "Restored source audio routing from a previous session.";
        }
```

- [ ] **Step 6: Build + manual verification**

Run: `dotnet build LogiBuddy.sln`
Expected: builds clean.

Manual (needs the VB-Audio cable + Spotify playing; `AutoRouteSource` on, device left on auto-detect — Task 12 adds the UI, so for now toggle via a saved profile or the default `true`):
1. Start Spotify, play a track. Launch the app, set Source to `Spotify`, click **Start**.
2. Windows **Volume Mixer** shows Spotify's output device is now the cable; only the processed radio is audible.
3. Click **Stop** → Volume Mixer shows Spotify back on the previous device.
4. **Start** again, then kill the app from Task Manager. Relaunch → status says "Restored…", Volume Mixer shows Spotify reverted, and `%AppData%/LogiBuddy/route-recovery.json` is gone.

- [ ] **Step 7: Commit**

```bash
git add src/LogiBuddy.App/ViewModels/MainViewModel.cs
git commit -m "feat: auto-route the source to a silent device on Start, restore on Stop"
```

---

### Task 11: Routing UI row in `MainWindow`

**Files:**
- Modify: `src/LogiBuddy.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `Profile.AutoRouteSource` / `RouteSourceToDeviceId` (Task 7); `MainViewModel.AvailableRenderDevices` / `ResetSourceRoutingCommand` (Task 10).
- Produces: final window layout.

- [ ] **Step 1: Add a routing row and one grid row**

In `MainWindow.xaml`, add one `<RowDefinition Height="Auto" />` after the source row's definition (it becomes row 1; renumber the comments — every subsequent `Grid.Row` increases by 1). Then insert this block immediately after the closing `</StackPanel>` of `Grid.Row="0"`:

```xml
        <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,0,0,8">
            <CheckBox x:Name="AutoRouteCheck" Content="Auto-route source to a silent device"
                      IsChecked="{Binding Profile.AutoRouteSource}" VerticalAlignment="Center" />
            <ComboBox Width="230" Margin="8,0,0,0"
                      ItemsSource="{Binding AvailableRenderDevices}" DisplayMemberPath="FriendlyName"
                      SelectedValuePath="Id" SelectedValue="{Binding Profile.RouteSourceToDeviceId}"
                      IsEnabled="{Binding IsChecked, ElementName=AutoRouteCheck}"
                      ToolTip="Leave unset to auto-detect a virtual cable at Start." />
            <Button Content="Reset routing" Command="{Binding ResetSourceRoutingCommand}" Margin="8,0,0,0" />
            <TextBlock Text=" restart to apply" VerticalAlignment="Center" Foreground="DarkOrange" Margin="8,0,0,0" />
        </StackPanel>
```

Every following row's `Grid.Row="N"` must become `Grid.Row="N+1"` (rows 1→2 through 15→16), and add one more `<RowDefinition Height="Auto" />` so the count matches (17 rows total, last one `Height="*"`).

- [ ] **Step 2: Build + verify**

Run: `dotnet build src/LogiBuddy.App/LogiBuddy.App.csproj`
Expected: builds clean.

Manual: launch the app. The routing row shows; the device dropdown lists render endpoints and disables when the checkbox is off; "Reset routing" is clickable; toggling the checkbox while running shows the restart hint.

- [ ] **Step 3: Commit**

```bash
git add src/LogiBuddy.App/MainWindow.xaml
git commit -m "feat: add the source-routing row to the window"
```

---

### Task 12: SETUP.md checklists and full verification

**Files:**
- Modify: `docs/SETUP.md`

- [ ] **Step 1: Add a "Real-time editing" section to `docs/SETUP.md`**

```markdown
## Real-time editing

While the pipeline is running, these apply instantly (no Stop/Start):
DSP sliders (high/low-pass, distortion, compressor, static, wet/dry),
the source-position marker, and the freelook tuning sliders (mouse
sensitivity, max yaw/pitch, spring-back).

These need a Stop then Start — the window shows an orange "restart to
apply" hint when you change one while running: Source process, Output
device, the freelook hotkey, and the auto-routing settings.
```

- [ ] **Step 2: Add a "Source audio routing" section to `docs/SETUP.md`**

```markdown
## Source audio routing

To stop hearing the raw source (e.g. Spotify) alongside the processed
radio, the app routes the source app's output to a silent render
endpoint while running.

- Requires Windows 11 and a virtual audio device (VB-Audio Virtual
  Cable, VoiceMeeter, etc.). On Windows 10 the option is unavailable
  and Start is blocked while "Auto-route" is ticked — untick it and
  route the source manually in Windows Sound settings instead.
- Leave the device dropdown unset to auto-detect a virtual cable, or
  pick one explicitly. The choice is saved in the profile.
- On Stop, the source's previous output device is restored. If the app
  is killed while running, the next launch restores it and shows
  "Restored source audio routing from a previous session."
- "Reset routing" forces the restore if anything is left pointing at
  the cable.
```

- [ ] **Step 3: Full regression run**

Run: `dotnet test LogiBuddy.sln`
Expected: all pass (Core tests: 32 original + 3 `RadioProfileTests` + 1 `ConfigStoreTests` + 8 `RenderDeviceEnumeratorTests` + 3 `RouteRecoveryStoreTests` + 1 `RadioPipelineTests` = 48).

Run: `dotnet build LogiBuddy.sln`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: End-to-end manual pass**

With the VB-Audio cable installed and Spotify playing:
1. Launch, Source = `Spotify`, `Auto-route` ticked, device = auto-detect. **Start**.
2. Only the processed radio is audible; Volume Mixer shows Spotify on the cable.
3. Drag DSP + freelook sliders and the position marker — all live, no dropouts.
4. Change Source in the dropdown → "restart to apply" hint; **Stop** → Spotify restored to its previous device; **Start** → re-routes.
5. **Start**, kill the app, relaunch → routing restored, no `route-recovery.json` left.
6. Untick `Auto-route`, **Start** → runs with both streams audible (pre-feature behaviour).

- [ ] **Step 5: Commit**

```bash
git add docs/SETUP.md
git commit -m "docs: document real-time editing and source audio routing"
```

---

## Self-Review

**Spec coverage — real-time editing spec:**
- `RadioProfile` INPC → Task 1. ✓
- `RadioPipeline.ApplyProfile` live-safe + comment → Task 2. ✓
- `MainViewModel` observe/live/restart + `RestartRequired` → Task 3. ✓
- `LoadProfile` while running → Task 3, Step 2. ✓
- `HotkeyCaptureControl` (keys + 5 mouse buttons, Escape cancels) → Task 4. ✓
- Freelook sliders + restart hint + `BooleanToVisibilityConverter` → Task 5. ✓
- Concurrency note (atomic float writes, no locks) → Task 2 comment + Global Constraints. ✓
- Tests: `RadioProfileTests`, `ConfigStoreTests` legacy-load, `RadioPipelineTests` live case → Tasks 1, 2. ✓
- Manual checklist → Task 12 §Real-time editing. ✓

**Spec coverage — source audio routing spec:**
- `AudioPolicyConfigInterop` (RoGetActivationFactory, HSTRING, endpoint-id transform, Win11 IID) → Task 8, Step 1. ✓
- `ISourceAudioRouter` / `WindowsAppAudioRouter` / `AppAudioRoute` / `SourceRoutingException` → Task 8, Step 2. ✓
- `IsSupported` = Win11 build ≥ 22000 AND factory query OK → Task 8 `WindowsAppAudioRouter` ctor. ✓
- `RenderDeviceEnumerator` + `LooksLikeVirtualCable` → Task 6. ✓
- Profile fields `AutoRouteSource` / `RouteSourceToDeviceId` → Task 7. ✓
- `route-recovery.json` written before change, restored + deleted → Task 9 + Task 10 Steps 2–5. ✓
- Start data flow steps 1–6 (unsupported / no device / no process / HRESULT fail / build pipeline) → Task 10 `TryRouteSource` + Step 3. ✓
- Stop restore → Task 10 Step 4. ✓
- Startup crash recovery → Task 10 Step 5. ✓
- "Reset source routing" button (record path or clear all pids) → Task 10 `ResetSourceRouting` + Task 11. ✓
- Every matching pid routed/restored → Task 10 `TryRouteSource` / `RestoreSourceRouting`. ✓
- Prior override saved and restored exactly (not clobbered) → Task 10 `entries` from `GetCurrentRoute`, `RestoreProcess(prev)`. ✓
- UI row (checkbox + device combo + reset button + restart marker) → Task 11. ✓
- Error-handling table rows → Task 10 `TryRouteSource` return strings + Global Constraints. ✓
- Testing: `RenderDeviceEnumeratorTests`, `ConfigStoreTests` extension, `RouteRecoveryStoreTests`, manual checklist → Tasks 6, 7, 9, 12. ✓
- `AudioPolicyConfigInterop` / router: no xUnit, probe instead → Task 8 Step 4. ✓ (matches spec "no automated test")

**Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N". Every code step has real code. The `_slot0..6` names in Task 8 are deliberate vtable padding, explained in the task's risk note and probed in Step 4. ✓

**Type consistency:**
- `AppAudioRoute(string Console, string Multimedia, string Communications)` + `AppAudioRoute.None` — defined Task 8, used Tasks 9-cross-ref/10 consistently. ✓
- `RouteRecoveryEntry(int ProcessId, string Console, string Multimedia, string Communications)` / `RouteRecoveryRecord(string SourceProcessName, IReadOnlyList<RouteRecoveryEntry> Routes)` — defined Task 9, used Task 10 consistently. ✓
- `ISourceAudioRouter` members (`IsSupported`, `GetCurrentRoute`, `RouteProcess`, `RestoreProcess`) — Task 8 signature matches all Task 10 call sites. ✓
- `SourceRoutingException(string message, int hresult)` — Task 8 ctor matches `throw` sites; caught by type in Task 10. ✓
- `RenderDeviceInfo(string Id, string FriendlyName)` — Task 6, bound in Task 11 (`DisplayMemberPath="FriendlyName"`, `SelectedValuePath="Id"`) consistently. ✓
- `RadioProfile.AutoRouteSource` / `RouteSourceToDeviceId` — Task 7 names match Task 3's set (updated in Task 7 Step 4) and Task 11 bindings. ✓
- `MainViewModel.RestartRequired` / `AvailableRenderDevices` / `ResetSourceRoutingCommand` — produced Tasks 3/10, bound Tasks 5/11 consistently. ✓
- `RadioProfile.SetField` — defined Task 1, reused by Task 7's new properties. ✓

No gaps found.
