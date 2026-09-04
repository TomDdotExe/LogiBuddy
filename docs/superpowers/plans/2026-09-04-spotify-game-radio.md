# Spotify Game Radio Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows desktop app that captures a user-selected process's audio, runs it through a radio-style DSP chain, and spatializes it via HRTF so it rotates correctly when the player holds a freelook hotkey and moves the mouse in-game.

**Architecture:** A .NET 8 solution with a UI-free `SpotifyGameRadio.Core` class library (DSP, tracking, spatialization, audio I/O, config) and a `SpotifyGameRadio.App` WPF project that composes those pieces and exposes them via a single window. Audio flows through one real-time callback thread: capture → mono downmix → `RadioEffectChain` → `ISpatializer` → output. `FreelookTracker` runs off a background hook thread and only ever writes one lock-free yaw/pitch value that the audio thread reads per block.

**Tech Stack:** .NET 8, WPF, NAudio (WASAPI device loopback + render + device enumeration), raw Win32/COM interop for per-process WASAPI loopback (not covered by NAudio), Steam Audio (native `phonon.dll`, MIT-licensed) via P/Invoke for HRTF, xUnit for tests.

**Spec:** `docs/superpowers/specs/2026-09-04-spotify-game-radio-design.md`

## Global Constraints

- No game memory reads, DLL injection, or process hooking into the target game. Only OS-level global input hooks (mouse/keyboard), same class as AutoHotkey.
- Per-process audio capture only — a browser process captures all of its tabs together; no per-tab isolation.
- Freelook angle is inferred from raw mouse deltas while a hotkey is held; it is an approximation, not a read of the game's real view angle.
- All real-time audio processing must run on a single dedicated thread, in fixed-size blocks, without blocking on the freelook-tracking thread (lock-free shared yaw/pitch value only).
- Every failure mode listed in the spec's Error Handling section must degrade gracefully (no crash): missing source process, output device loss, missing Steam Audio native library, failed global hook install, buffer underrun.
- Target platform: Windows 10 20H1+ (per-process loopback), with a whole-device loopback fallback for older Windows.

---

## File Structure

```
SpotifyGameRadio.sln
src/
  SpotifyGameRadio.Core/
    SpotifyGameRadio.Core.csproj
    Config/
      RadioProfile.cs
      FreelookHotkey.cs
      IConfigStore.cs
      ConfigStore.cs
    Dsp/
      BiquadFilter.cs
      SoftClipDistortion.cs
      Compressor.cs
      NoiseGenerator.cs
      RadioEffectChain.cs
    Tracking/
      IMouseInputSource.cs
      FreelookTracker.cs
      Win32MouseHook.cs
    Spatial/
      ISpatializer.cs
      StereoPanSpatializer.cs
      SteamAudioNative.cs
      SteamAudioSpatializer.cs
    Audio/
      AudioCaptureStatus.cs
      IAudioCaptureService.cs
      IAudioOutputService.cs
      AudioSessionEnumerator.cs
      WasapiProcessLoopbackInterop.cs
      WasapiProcessLoopbackCapture.cs
      WasapiDeviceLoopbackCapture.cs
      WasapiAudioOutput.cs
    Pipeline/
      RadioPipeline.cs
  SpotifyGameRadio.App/
    SpotifyGameRadio.App.csproj
    App.xaml / App.xaml.cs
    MainWindow.xaml / MainWindow.xaml.cs
    ViewModels/
      RelayCommand.cs
      MainViewModel.cs
    Controls/
      SourcePositionCanvas.xaml / SourcePositionCanvas.xaml.cs
tests/
  SpotifyGameRadio.Core.Tests/
    SpotifyGameRadio.Core.Tests.csproj
    Config/
      ConfigStoreTests.cs
    Dsp/
      BiquadFilterTests.cs
      RadioEffectChainTests.cs
    Tracking/
      FreelookTrackerTests.cs
    Spatial/
      StereoPanSpatializerTests.cs
    Pipeline/
      RadioPipelineTests.cs
docs/
  SETUP.md
```

Native binaries (`phonon.dll` and its dependencies) live in `src/SpotifyGameRadio.App/runtimes/win-x64/native/` and are copied to output on build.

---

### Task 1: Solution scaffolding, RadioProfile, and ConfigStore

**Files:**
- Create: `SpotifyGameRadio.sln`
- Create: `src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj`
- Create: `src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
- Create: `tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj`
- Create: `src/SpotifyGameRadio.Core/Config/FreelookHotkey.cs`
- Create: `src/SpotifyGameRadio.Core/Config/RadioProfile.cs`
- Create: `src/SpotifyGameRadio.Core/Config/IConfigStore.cs`
- Create: `src/SpotifyGameRadio.Core/Config/ConfigStore.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Produces: `RadioProfile` (POCO, all fields below), `FreelookHotkey { int VirtualKeyCode }`, `IConfigStore.ListProfiles() -> IReadOnlyList<string>`, `IConfigStore.Load(string name) -> RadioProfile`, `IConfigStore.Save(RadioProfile profile) -> void`, `IConfigStore.Delete(string name) -> void`, `ConfigStore(string directoryOverride = null)`.

- [ ] **Step 1: Create the solution and projects**

```bash
cd "D:/Freelance Work/SpotifyGameRadio"
dotnet new sln -n SpotifyGameRadio
dotnet new classlib -n SpotifyGameRadio.Core -o src/SpotifyGameRadio.Core -f net8.0
dotnet new wpf -n SpotifyGameRadio.App -o src/SpotifyGameRadio.App -f net8.0-windows
dotnet new xunit -n SpotifyGameRadio.Core.Tests -o tests/SpotifyGameRadio.Core.Tests -f net8.0
dotnet sln add src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj
dotnet sln add src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj
dotnet sln add tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj
dotnet add src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj reference src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj
dotnet add tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj reference src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj
dotnet add src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj package NAudio
rm src/SpotifyGameRadio.Core/Class1.cs
```

- [ ] **Step 2: Write `FreelookHotkey.cs`**

```csharp
namespace SpotifyGameRadio.Core.Config;

public class FreelookHotkey
{
    /// Win32 virtual-key code, e.g. 0x12 = VK_MENU (Alt).
    public int VirtualKeyCode { get; set; } = 0x12;
}
```

- [ ] **Step 3: Write `RadioProfile.cs`**

```csharp
namespace SpotifyGameRadio.Core.Config;

public class RadioProfile
{
    public string Name { get; set; } = "Default";
    public string SourceProcessName { get; set; } = "";
    public string OutputDeviceId { get; set; } = "";

    // Fixed source position in listener-relative meters: +X right, +Y up, +Z forward.
    public float SourceX { get; set; } = 0.3f;
    public float SourceY { get; set; } = -0.1f;
    public float SourceZ { get; set; } = 0.2f;

    public float HighPassHz { get; set; } = 400f;
    public float LowPassHz { get; set; } = 3400f;
    public float DistortionDrive { get; set; } = 0.2f;
    public float CompressorThresholdDb { get; set; } = -18f;
    public float CompressorRatio { get; set; } = 4f;
    public float NoiseLevel { get; set; } = 0.05f;
    public float WetDryMix { get; set; } = 1.0f;

    public FreelookHotkey Hotkey { get; set; } = new();
    public float MouseSensitivity { get; set; } = 0.15f; // degrees per mouse count
    public float MaxYawDegrees { get; set; } = 90f;
    public float MaxPitchDegrees { get; set; } = 60f;
    public float SpringBackRatePerSecond { get; set; } = 720f;
}
```

- [ ] **Step 4: Write the failing test for ConfigStore**

```csharp
using System.IO;
using SpotifyGameRadio.Core.Config;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Config;

public class ConfigStoreTests
{
    private static ConfigStore CreateStore(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "sgr-test-" + Path.GetRandomFileName());
        return new ConfigStore(tempDir);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var store = CreateStore(out var dir);
        try
        {
            var profile = new RadioProfile
            {
                Name = "Arma3-Truck",
                SourceProcessName = "Spotify",
                OutputDeviceId = "device-123",
                SourceX = 0.5f,
                SourceY = -0.2f,
                SourceZ = 0.4f,
                HighPassHz = 500f,
                LowPassHz = 3000f,
                DistortionDrive = 0.3f,
                CompressorThresholdDb = -20f,
                CompressorRatio = 6f,
                NoiseLevel = 0.1f,
                WetDryMix = 0.8f,
                Hotkey = new FreelookHotkey { VirtualKeyCode = 0x12 },
                MouseSensitivity = 0.2f,
                MaxYawDegrees = 80f,
                MaxPitchDegrees = 50f,
                SpringBackRatePerSecond = 500f
            };

            store.Save(profile);
            var loaded = store.Load("Arma3-Truck");

            Assert.Equal(profile.SourceProcessName, loaded.SourceProcessName);
            Assert.Equal(profile.SourceX, loaded.SourceX);
            Assert.Equal(profile.HighPassHz, loaded.HighPassHz);
            Assert.Equal(profile.Hotkey.VirtualKeyCode, loaded.Hotkey.VirtualKeyCode);
            Assert.Equal(profile.SpringBackRatePerSecond, loaded.SpringBackRatePerSecond);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ListProfiles_ReturnsAllSavedNames()
    {
        var store = CreateStore(out var dir);
        try
        {
            store.Save(new RadioProfile { Name = "One" });
            store.Save(new RadioProfile { Name = "Two" });

            var names = store.ListProfiles();

            Assert.Contains("One", names);
            Assert.Contains("Two", names);
            Assert.Equal(2, names.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Delete_RemovesProfile()
    {
        var store = CreateStore(out var dir);
        try
        {
            store.Save(new RadioProfile { Name = "Temp" });
            store.Delete("Temp");

            Assert.DoesNotContain("Temp", store.ListProfiles());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they fail**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj`
Expected: build FAILS — `IConfigStore`/`ConfigStore` don't exist yet.

- [ ] **Step 6: Write `IConfigStore.cs`**

```csharp
namespace SpotifyGameRadio.Core.Config;

public interface IConfigStore
{
    IReadOnlyList<string> ListProfiles();
    RadioProfile Load(string name);
    void Save(RadioProfile profile);
    void Delete(string name);
}
```

- [ ] **Step 7: Write `ConfigStore.cs`**

```csharp
using System.Text.Json;

namespace SpotifyGameRadio.Core.Config;

public class ConfigStore : IConfigStore
{
    private readonly string _directory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ConfigStore(string? directoryOverride = null)
    {
        _directory = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpotifyGameRadio", "Profiles");
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string name) => Path.Combine(_directory, $"{name}.json");

    public IReadOnlyList<string> ListProfiles()
    {
        if (!Directory.Exists(_directory)) return Array.Empty<string>();
        return Directory.GetFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();
    }

    public RadioProfile Load(string name)
    {
        var json = File.ReadAllText(PathFor(name));
        return JsonSerializer.Deserialize<RadioProfile>(json)
            ?? throw new InvalidDataException($"Profile '{name}' is invalid.");
    }

    public void Save(RadioProfile profile)
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(PathFor(profile.Name), json);
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }
}
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj`
Expected: PASS (3 tests).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: scaffold solution and add RadioProfile/ConfigStore"
```

---

### Task 2: BiquadFilter (high-pass / low-pass)

**Files:**
- Create: `src/SpotifyGameRadio.Core/Dsp/BiquadFilter.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Dsp/BiquadFilterTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `enum BiquadFilterType { HighPass, LowPass }`, `BiquadFilter(BiquadFilterType type, float cutoffHz, float sampleRate, float q = 0.7071f)`, `.SetCutoff(float cutoffHz)`, `.Process(float input) -> float`, `.Reset()`.

- [ ] **Step 1: Write the failing test**

```csharp
using SpotifyGameRadio.Core.Dsp;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Dsp;

public class BiquadFilterTests
{
    private const float SampleRate = 48000f;

    private static float RmsAtFrequency(BiquadFilter filter, float toneHz, int sampleCount)
    {
        filter.Reset();
        double sum = 0;
        // Discard the first 500 samples so filter transients settle before measuring.
        int warmup = 500;
        for (int i = 0; i < warmup + sampleCount; i++)
        {
            float sample = MathF.Sin(2f * MathF.PI * toneHz * i / SampleRate);
            float output = filter.Process(sample);
            if (i >= warmup) sum += output * output;
        }
        return (float)Math.Sqrt(sum / sampleCount);
    }

    [Fact]
    public void HighPass_AttenuatesLowFrequencyMoreThanHighFrequency()
    {
        var filter = new BiquadFilter(BiquadFilterType.HighPass, cutoffHz: 400f, sampleRate: SampleRate);

        float lowRms = RmsAtFrequency(filter, toneHz: 80f, sampleCount: 4000);
        float highRms = RmsAtFrequency(filter, toneHz: 4000f, sampleCount: 4000);

        Assert.True(lowRms < highRms * 0.3f,
            $"Expected 80Hz ({lowRms}) to be attenuated well below 4000Hz ({highRms}) through a 400Hz high-pass.");
    }

    [Fact]
    public void LowPass_AttenuatesHighFrequencyMoreThanLowFrequency()
    {
        var filter = new BiquadFilter(BiquadFilterType.LowPass, cutoffHz: 3400f, sampleRate: SampleRate);

        float lowRms = RmsAtFrequency(filter, toneHz: 200f, sampleCount: 4000);
        float highRms = RmsAtFrequency(filter, toneHz: 12000f, sampleCount: 4000);

        Assert.True(highRms < lowRms * 0.3f,
            $"Expected 12000Hz ({highRms}) to be attenuated well below 200Hz ({lowRms}) through a 3400Hz low-pass.");
    }

    [Fact]
    public void PassbandFrequency_IsNotStronglyAttenuated()
    {
        var filter = new BiquadFilter(BiquadFilterType.HighPass, cutoffHz: 400f, sampleRate: SampleRate);

        float passbandRms = RmsAtFrequency(filter, toneHz: 4000f, sampleCount: 4000);
        float inputRms = 1f / MathF.Sqrt(2f); // RMS of a unit sine wave

        Assert.True(passbandRms > inputRms * 0.8f,
            $"Expected passband frequency to pass through mostly unattenuated, got {passbandRms}.");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter BiquadFilterTests`
Expected: FAIL — `BiquadFilter` doesn't exist yet.

- [ ] **Step 3: Write `BiquadFilter.cs`**

```csharp
namespace SpotifyGameRadio.Core.Dsp;

public enum BiquadFilterType { HighPass, LowPass }

/// RBJ Audio EQ Cookbook biquad, Direct Form I.
public class BiquadFilter
{
    private readonly BiquadFilterType _type;
    private readonly float _sampleRate;
    private readonly float _q;
    private float _b0, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    public BiquadFilter(BiquadFilterType type, float cutoffHz, float sampleRate, float q = 0.7071f)
    {
        _type = type;
        _sampleRate = sampleRate;
        _q = q;
        SetCutoff(cutoffHz);
    }

    public void SetCutoff(float cutoffHz)
    {
        float w0 = 2f * MathF.PI * cutoffHz / _sampleRate;
        float cosW0 = MathF.Cos(w0);
        float alpha = MathF.Sin(w0) / (2f * _q);

        float b0, b1, b2, a0, a1, a2;
        if (_type == BiquadFilterType.HighPass)
        {
            b0 = (1f + cosW0) / 2f;
            b1 = -(1f + cosW0);
            b2 = (1f + cosW0) / 2f;
        }
        else
        {
            b0 = (1f - cosW0) / 2f;
            b1 = 1f - cosW0;
            b2 = (1f - cosW0) / 2f;
        }
        a0 = 1f + alpha;
        a1 = -2f * cosW0;
        a2 = 1f - alpha;

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public float Process(float input)
    {
        float output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = input;
        _y2 = _y1;
        _y1 = output;
        return output;
    }

    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0f;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter BiquadFilterTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add BiquadFilter high-pass/low-pass DSP"
```

---

### Task 3: SoftClipDistortion, Compressor, NoiseGenerator

**Files:**
- Create: `src/SpotifyGameRadio.Core/Dsp/SoftClipDistortion.cs`
- Create: `src/SpotifyGameRadio.Core/Dsp/Compressor.cs`
- Create: `src/SpotifyGameRadio.Core/Dsp/NoiseGenerator.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Dsp/SoftClipDistortionTests.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Dsp/CompressorTests.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Dsp/NoiseGeneratorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `SoftClipDistortion(float drive)` with `.Drive { get; set; }`, `.Process(float input) -> float`; `Compressor(float sampleRate, float thresholdDb, float ratio, float attackMs = 5f, float releaseMs = 80f)` with `.ThresholdDb { get; set; }`, `.Ratio { get; set; }`, `.Process(float input) -> float`; `NoiseGenerator(int seed = 0)` with `.Level { get; set; }`, `.NextSample() -> float`.

- [ ] **Step 1: Write the failing tests**

```csharp
using SpotifyGameRadio.Core.Dsp;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Dsp;

public class SoftClipDistortionTests
{
    [Fact]
    public void Process_WithZeroDrive_PassesSignalThroughUnchanged()
    {
        var distortion = new SoftClipDistortion(drive: 0f);

        float output = distortion.Process(0.5f);

        Assert.Equal(0.5f, output, precision: 3);
    }

    [Fact]
    public void Process_WithHighDrive_CompressesLoudSignalTowardUnity()
    {
        var distortion = new SoftClipDistortion(drive: 0.9f);

        float output = distortion.Process(1.0f);

        Assert.True(output < 1.0f && output > 0f,
            $"Expected soft-clipped output to stay bounded below 1.0, got {output}.");
    }

    [Fact]
    public void Process_IsOddSymmetric()
    {
        var distortion = new SoftClipDistortion(drive: 0.6f);

        float positive = distortion.Process(0.7f);
        float negative = distortion.Process(-0.7f);

        Assert.Equal(-positive, negative, precision: 4);
    }
}
```

```csharp
using SpotifyGameRadio.Core.Dsp;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Dsp;

public class CompressorTests
{
    [Fact]
    public void Process_SignalBelowThreshold_IsUnaffectedAtSteadyState()
    {
        var compressor = new Compressor(sampleRate: 48000f, thresholdDb: -18f, ratio: 4f);

        float output = 0f;
        // Feed a small constant signal long enough for the envelope to settle.
        for (int i = 0; i < 10000; i++)
            output = compressor.Process(0.05f); // well below -18dBFS (~0.126)

        Assert.InRange(output, 0.045f, 0.055f);
    }

    [Fact]
    public void Process_SignalAboveThreshold_IsGainReducedAtSteadyState()
    {
        var compressor = new Compressor(sampleRate: 48000f, thresholdDb: -18f, ratio: 4f);

        float output = 0f;
        for (int i = 0; i < 10000; i++)
            output = compressor.Process(0.9f); // well above threshold

        Assert.True(output < 0.9f, $"Expected gain reduction above threshold, got {output}.");
        Assert.True(output > 0.05f, $"Expected compressor not to silence the signal, got {output}.");
    }
}
```

```csharp
using SpotifyGameRadio.Core.Dsp;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Dsp;

public class NoiseGeneratorTests
{
    [Fact]
    public void NextSample_StaysWithinLevelBounds()
    {
        var noise = new NoiseGenerator(seed: 42) { Level = 0.1f };

        for (int i = 0; i < 1000; i++)
        {
            float sample = noise.NextSample();
            Assert.InRange(sample, -0.1f, 0.1f);
        }
    }

    [Fact]
    public void NextSample_WithZeroLevel_IsAlwaysZero()
    {
        var noise = new NoiseGenerator(seed: 1) { Level = 0f };

        for (int i = 0; i < 100; i++)
            Assert.Equal(0f, noise.NextSample());
    }

    [Fact]
    public void SameSeed_ProducesDeterministicSequence()
    {
        var a = new NoiseGenerator(seed: 7) { Level = 1f };
        var b = new NoiseGenerator(seed: 7) { Level = 1f };

        for (int i = 0; i < 20; i++)
            Assert.Equal(a.NextSample(), b.NextSample());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter "SoftClipDistortionTests|CompressorTests|NoiseGeneratorTests"`
Expected: FAIL — none of the three classes exist yet.

- [ ] **Step 3: Write `SoftClipDistortion.cs`**

```csharp
namespace SpotifyGameRadio.Core.Dsp;

/// Drive in [0,1]: 0 = bypass, higher = more aggressive tanh-style soft clipping.
public class SoftClipDistortion
{
    public float Drive { get; set; }

    public SoftClipDistortion(float drive)
    {
        Drive = drive;
    }

    public float Process(float input)
    {
        if (Drive <= 0f) return input;
        float k = 1f + Drive * 19f; // maps [0,1] -> gentle..aggressive tanh slope
        return MathF.Tanh(k * input) / MathF.Tanh(k);
    }
}
```

- [ ] **Step 4: Write `Compressor.cs`**

```csharp
namespace SpotifyGameRadio.Core.Dsp;

/// Simple feed-forward peak compressor with exponential attack/release envelopes.
public class Compressor
{
    private readonly float _sampleRate;
    private readonly float _attackCoeff;
    private readonly float _releaseCoeff;
    private float _envelope;

    public float ThresholdDb { get; set; }
    public float Ratio { get; set; }

    public Compressor(float sampleRate, float thresholdDb, float ratio, float attackMs = 5f, float releaseMs = 80f)
    {
        _sampleRate = sampleRate;
        ThresholdDb = thresholdDb;
        Ratio = ratio;
        _attackCoeff = MathF.Exp(-1f / (attackMs * 0.001f * _sampleRate));
        _releaseCoeff = MathF.Exp(-1f / (releaseMs * 0.001f * _sampleRate));
    }

    public float Process(float input)
    {
        float rectified = MathF.Abs(input);
        float coeff = rectified > _envelope ? _attackCoeff : _releaseCoeff;
        _envelope = coeff * _envelope + (1f - coeff) * rectified;

        float envelopeDb = 20f * MathF.Log10(MathF.Max(_envelope, 1e-6f));
        float gainReductionDb = 0f;
        if (envelopeDb > ThresholdDb)
            gainReductionDb = (ThresholdDb - envelopeDb) * (1f - 1f / Ratio);

        float gain = MathF.Pow(10f, gainReductionDb / 20f);
        return input * gain;
    }
}
```

- [ ] **Step 5: Write `NoiseGenerator.cs`**

```csharp
namespace SpotifyGameRadio.Core.Dsp;

/// Uniform white noise generator scaled by Level, seeded for deterministic tests.
public class NoiseGenerator
{
    private readonly Random _random;

    public float Level { get; set; }

    public NoiseGenerator(int seed = 0)
    {
        _random = new Random(seed);
    }

    public float NextSample()
    {
        if (Level <= 0f) return 0f;
        float uniform = (float)(_random.NextDouble() * 2.0 - 1.0); // [-1, 1]
        return uniform * Level;
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter "SoftClipDistortionTests|CompressorTests|NoiseGeneratorTests"`
Expected: PASS (8 tests).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add distortion, compressor, and noise DSP units"
```

---

### Task 4: RadioEffectChain

**Files:**
- Create: `src/SpotifyGameRadio.Core/Dsp/RadioEffectChain.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Dsp/RadioEffectChainTests.cs`

**Interfaces:**
- Consumes: `BiquadFilter`, `BiquadFilterType` (Task 2); `SoftClipDistortion`, `Compressor`, `NoiseGenerator` (Task 3); `RadioProfile` (Task 1).
- Produces: `RadioEffectChain(float sampleRate)` with settable `HighPassHz`, `LowPassHz`, `DistortionDrive`, `CompressorThresholdDb`, `CompressorRatio`, `NoiseLevel`, `WetDryMix` properties, `.ApplyProfile(RadioProfile profile)`, `.Process(float[] buffer, int count)` (in-place, mono).

- [ ] **Step 1: Write the failing test**

```csharp
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Dsp;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Dsp;

public class RadioEffectChainTests
{
    [Fact]
    public void Process_WithFullWetMix_ProducesBoundedNonZeroOutput()
    {
        var chain = new RadioEffectChain(48000f)
        {
            HighPassHz = 400f,
            LowPassHz = 3400f,
            DistortionDrive = 0.2f,
            CompressorThresholdDb = -18f,
            CompressorRatio = 4f,
            NoiseLevel = 0f, // deterministic for this test
            WetDryMix = 1f
        };

        var buffer = new float[512];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = MathF.Sin(2f * MathF.PI * 1000f * i / 48000f) * 0.5f;

        chain.Process(buffer, buffer.Length);

        Assert.Contains(buffer, s => MathF.Abs(s) > 0.001f);
        Assert.All(buffer, s => Assert.InRange(s, -1.5f, 1.5f));
    }

    [Fact]
    public void Process_WithZeroWetMix_LeavesSignalUnchanged()
    {
        var chain = new RadioEffectChain(48000f)
        {
            HighPassHz = 400f,
            LowPassHz = 3400f,
            DistortionDrive = 0.5f,
            NoiseLevel = 0.2f,
            WetDryMix = 0f
        };

        var buffer = new float[256];
        var original = new float[256];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = MathF.Sin(2f * MathF.PI * 1000f * i / 48000f) * 0.5f;
            original[i] = buffer[i];
        }

        chain.Process(buffer, buffer.Length);

        for (int i = 0; i < buffer.Length; i++)
            Assert.Equal(original[i], buffer[i], precision: 5);
    }

    [Fact]
    public void ApplyProfile_UpdatesAllChainParameters()
    {
        var chain = new RadioEffectChain(48000f);
        var profile = new RadioProfile
        {
            HighPassHz = 600f,
            LowPassHz = 2800f,
            DistortionDrive = 0.4f,
            CompressorThresholdDb = -12f,
            CompressorRatio = 8f,
            NoiseLevel = 0.15f,
            WetDryMix = 0.7f
        };

        chain.ApplyProfile(profile);

        Assert.Equal(600f, chain.HighPassHz);
        Assert.Equal(2800f, chain.LowPassHz);
        Assert.Equal(0.4f, chain.DistortionDrive);
        Assert.Equal(-12f, chain.CompressorThresholdDb);
        Assert.Equal(8f, chain.CompressorRatio);
        Assert.Equal(0.15f, chain.NoiseLevel);
        Assert.Equal(0.7f, chain.WetDryMix);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter RadioEffectChainTests`
Expected: FAIL — `RadioEffectChain` doesn't exist yet.

- [ ] **Step 3: Write `RadioEffectChain.cs`**

```csharp
using SpotifyGameRadio.Core.Config;

namespace SpotifyGameRadio.Core.Dsp;

public class RadioEffectChain
{
    private readonly float _sampleRate;
    private readonly BiquadFilter _highPass;
    private readonly BiquadFilter _lowPass;
    private readonly SoftClipDistortion _distortion;
    private readonly Compressor _compressor;
    private readonly NoiseGenerator _noise;

    private float _highPassHz = 400f;
    private float _lowPassHz = 3400f;

    public float HighPassHz
    {
        get => _highPassHz;
        set { _highPassHz = value; _highPass.SetCutoff(value); }
    }

    public float LowPassHz
    {
        get => _lowPassHz;
        set { _lowPassHz = value; _lowPass.SetCutoff(value); }
    }

    public float DistortionDrive
    {
        get => _distortion.Drive;
        set => _distortion.Drive = value;
    }

    public float CompressorThresholdDb
    {
        get => _compressor.ThresholdDb;
        set => _compressor.ThresholdDb = value;
    }

    public float CompressorRatio
    {
        get => _compressor.Ratio;
        set => _compressor.Ratio = value;
    }

    public float NoiseLevel
    {
        get => _noise.Level;
        set => _noise.Level = value;
    }

    public float WetDryMix { get; set; } = 1f;

    public RadioEffectChain(float sampleRate)
    {
        _sampleRate = sampleRate;
        _highPass = new BiquadFilter(BiquadFilterType.HighPass, _highPassHz, sampleRate);
        _lowPass = new BiquadFilter(BiquadFilterType.LowPass, _lowPassHz, sampleRate);
        _distortion = new SoftClipDistortion(drive: 0f);
        _compressor = new Compressor(sampleRate, thresholdDb: -18f, ratio: 4f);
        _noise = new NoiseGenerator();
    }

    public void ApplyProfile(RadioProfile profile)
    {
        HighPassHz = profile.HighPassHz;
        LowPassHz = profile.LowPassHz;
        DistortionDrive = profile.DistortionDrive;
        CompressorThresholdDb = profile.CompressorThresholdDb;
        CompressorRatio = profile.CompressorRatio;
        NoiseLevel = profile.NoiseLevel;
        WetDryMix = profile.WetDryMix;
    }

    public void Process(float[] buffer, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float dry = buffer[i];
            float wet = _highPass.Process(dry);
            wet = _lowPass.Process(wet);
            wet = _distortion.Process(wet);
            wet = _compressor.Process(wet);
            wet += _noise.NextSample();

            buffer[i] = dry * (1f - WetDryMix) + wet * WetDryMix;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter RadioEffectChainTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add RadioEffectChain combining filters, distortion, compressor, noise"
```

---

### Task 5: FreelookTracker

**Files:**
- Create: `src/SpotifyGameRadio.Core/Tracking/IMouseInputSource.cs`
- Create: `src/SpotifyGameRadio.Core/Tracking/FreelookTracker.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Tracking/FreelookTrackerTests.cs`

**Interfaces:**
- Consumes: `RadioProfile` (Task 1, for `MouseSensitivity`, `MaxYawDegrees`, `MaxPitchDegrees`, `SpringBackRatePerSecond`).
- Produces: `IMouseInputSource { bool IsHotkeyHeld { get; } event Action<int,int>? MouseMoved; }`, `FreelookTracker(IMouseInputSource inputSource, RadioProfile profile)` with `.YawDegrees { get; }`, `.PitchDegrees { get; }`, `.Update(float deltaSeconds)`, `.ApplyProfile(RadioProfile profile)`. Consumed later by Task 7 (`StereoPanSpatializer`), Task 8 (`SteamAudioSpatializer`), and Task 12 (`RadioPipeline`).

- [ ] **Step 1: Write the failing test**

```csharp
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Tracking;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Tracking;

public class FakeMouseInputSource : IMouseInputSource
{
    public bool IsHotkeyHeld { get; set; }
    public event Action<int, int>? MouseMoved;

    public void RaiseMove(int dx, int dy) => MouseMoved?.Invoke(dx, dy);
}

public class FreelookTrackerTests
{
    private static RadioProfile MakeProfile() => new()
    {
        MouseSensitivity = 0.1f, // degrees per mouse count
        MaxYawDegrees = 90f,
        MaxPitchDegrees = 60f,
        SpringBackRatePerSecond = 100f
    };

    [Fact]
    public void MouseMove_WhileHotkeyHeld_AccumulatesYawAndPitch()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());

        input.RaiseMove(dx: 50, dy: 20);

        Assert.Equal(5f, tracker.YawDegrees, precision: 3);   // 50 * 0.1
        Assert.Equal(2f, tracker.PitchDegrees, precision: 3); // 20 * 0.1
    }

    [Fact]
    public void MouseMove_WhileHotkeyNotHeld_IsIgnored()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = false };
        var tracker = new FreelookTracker(input, MakeProfile());

        input.RaiseMove(dx: 50, dy: 20);

        Assert.Equal(0f, tracker.YawDegrees);
        Assert.Equal(0f, tracker.PitchDegrees);
    }

    [Fact]
    public void Yaw_IsClampedToMaxYawDegrees()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());

        input.RaiseMove(dx: 5000, dy: 0);

        Assert.Equal(90f, tracker.YawDegrees, precision: 3);
    }

    [Fact]
    public void Update_WhileHotkeyReleased_SpringsBackTowardZero()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        input.RaiseMove(dx: 100, dy: 0); // yaw = 10 degrees
        input.IsHotkeyHeld = false;

        tracker.Update(deltaSeconds: 0.05f); // springs back 100deg/s * 0.05s = 5 degrees

        Assert.Equal(5f, tracker.YawDegrees, precision: 2);
    }

    [Fact]
    public void Update_SpringBack_NeverOvershootsZero()
    {
        var input = new FakeMouseInputSource { IsHotkeyHeld = true };
        var tracker = new FreelookTracker(input, MakeProfile());
        input.RaiseMove(dx: 20, dy: 0); // yaw = 2 degrees
        input.IsHotkeyHeld = false;

        tracker.Update(deltaSeconds: 1f); // spring rate would overshoot to -98 degrees

        Assert.Equal(0f, tracker.YawDegrees, precision: 3);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter FreelookTrackerTests`
Expected: FAIL — `IMouseInputSource`/`FreelookTracker` don't exist yet.

- [ ] **Step 3: Write `IMouseInputSource.cs`**

```csharp
namespace SpotifyGameRadio.Core.Tracking;

public interface IMouseInputSource
{
    bool IsHotkeyHeld { get; }

    /// Raised with raw (deltaX, deltaY) mouse counts, independent of hotkey state.
    event Action<int, int>? MouseMoved;
}
```

- [ ] **Step 4: Write `FreelookTracker.cs`**

```csharp
using SpotifyGameRadio.Core.Config;

namespace SpotifyGameRadio.Core.Tracking;

public class FreelookTracker
{
    private readonly IMouseInputSource _inputSource;
    private RadioProfile _profile;

    public float YawDegrees { get; private set; }
    public float PitchDegrees { get; private set; }

    public FreelookTracker(IMouseInputSource inputSource, RadioProfile profile)
    {
        _inputSource = inputSource;
        _profile = profile;
        _inputSource.MouseMoved += OnMouseMoved;
    }

    public void ApplyProfile(RadioProfile profile)
    {
        _profile = profile;
    }

    private void OnMouseMoved(int deltaX, int deltaY)
    {
        if (!_inputSource.IsHotkeyHeld) return;

        YawDegrees = Clamp(YawDegrees + deltaX * _profile.MouseSensitivity, _profile.MaxYawDegrees);
        PitchDegrees = Clamp(PitchDegrees + deltaY * _profile.MouseSensitivity, _profile.MaxPitchDegrees);
    }

    /// Call once per audio block (or on a timer) to ease the view back to
    /// center when the freelook hotkey is not held, mirroring in-game snap-back.
    public void Update(float deltaSeconds)
    {
        if (_inputSource.IsHotkeyHeld) return;

        float step = _profile.SpringBackRatePerSecond * deltaSeconds;
        YawDegrees = SpringTowardZero(YawDegrees, step);
        PitchDegrees = SpringTowardZero(PitchDegrees, step);
    }

    private static float Clamp(float value, float max) => Math.Clamp(value, -max, max);

    private static float SpringTowardZero(float value, float step)
    {
        if (value > 0f) return MathF.Max(0f, value - step);
        if (value < 0f) return MathF.Min(0f, value + step);
        return 0f;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter FreelookTrackerTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add FreelookTracker with clamp and spring-back angle math"
```

---

### Task 6: Win32MouseHook (real global input source)

**Files:**
- Create: `src/SpotifyGameRadio.Core/Tracking/Win32MouseHook.cs`

**Interfaces:**
- Consumes: `IMouseInputSource` (Task 5), `FreelookHotkey` (Task 1).
- Produces: `Win32MouseHook(FreelookHotkey hotkey) : IMouseInputSource, IDisposable` — real global implementation. No automated test (OS-level global hook, cannot run headless/unattended in CI); verified manually per Step 4 below.

- [ ] **Step 1: Write `Win32MouseHook.cs`**

```csharp
using System.Runtime.InteropServices;
using SpotifyGameRadio.Core.Config;

namespace SpotifyGameRadio.Core.Tracking;

/// Global low-level mouse hook + polled hotkey state. Does not read from or
/// inject into any other process — same risk class as AutoHotkey.
public class Win32MouseHook : IMouseInputSource, IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private readonly LowLevelMouseProc _proc;
    private readonly FreelookHotkey _hotkey;
    private readonly Thread _hookThread;
    private readonly Thread _hotkeyPollThread;
    private IntPtr _hookHandle = IntPtr.Zero;
    private volatile bool _running = true;
    private int _lastX, _lastY;
    private bool _havePreviousPoint;

    public bool IsHotkeyHeld { get; private set; }
    public event Action<int, int>? MouseMoved;

    /// Thrown if the low-level hook could not be installed (e.g. blocked by policy/AV).
    public bool HookInstalled { get; private set; }

    public Win32MouseHook(FreelookHotkey hotkey)
    {
        _hotkey = hotkey;
        _proc = HookCallback;

        _hookThread = new Thread(RunHookMessageLoop) { IsBackground = true, Name = "SGR-MouseHook" };
        _hookThread.Start();

        _hotkeyPollThread = new Thread(PollHotkeyState) { IsBackground = true, Name = "SGR-HotkeyPoll" };
        _hotkeyPollThread.Start();
    }

    private void RunHookMessageLoop()
    {
        using var curModule = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(curModule?.ModuleName ?? ""), 0);
        HookInstalled = _hookHandle != IntPtr.Zero;

        // Pump a Win32 message loop so the hook callback is dispatched.
        while (_running)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(1);
        }

        if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);
    }

    private void PollHotkeyState()
    {
        while (_running)
        {
            // High bit set = key currently down.
            IsHotkeyHeld = (GetAsyncKeyState(_hotkey.VirtualKeyCode) & 0x8000) != 0;
            Thread.Sleep(8); // ~120Hz poll
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEMOVE)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (_havePreviousPoint)
            {
                int dx = hookStruct.pt.X - _lastX;
                int dy = hookStruct.pt.Y - _lastY;
                if (dx != 0 || dy != 0) MouseMoved?.Invoke(dx, dy);
            }
            _lastX = hookStruct.pt.X;
            _lastY = hookStruct.pt.Y;
            _havePreviousPoint = true;
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _running = false;
    }
}
```

- [ ] **Step 2: Add project reference for WPF Dispatcher type**

`Win32MouseHook.cs` uses `System.Windows.Threading.Dispatcher`, which requires a Windows Desktop target framework. Edit `src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj` and change the `<TargetFramework>` element:

```xml
<TargetFramework>net8.0-windows</TargetFramework>
<UseWPF>true</UseWPF>
```

- [ ] **Step 3: Verify the solution still builds**

Run: `dotnet build SpotifyGameRadio.sln`
Expected: build succeeds with no errors.

- [ ] **Step 4: Manual verification (no automated test — real OS hook)**

This class cannot be unit tested (it depends on real Windows input and a live message loop). Verify manually before moving on:

1. Write a temporary `Program.cs` console snippet (or use the WPF app once Task 13 exists) that constructs `new Win32MouseHook(new FreelookHotkey { VirtualKeyCode = 0x12 })` (Alt key) and prints `MouseMoved` deltas and `IsHotkeyHeld` to the console.
2. Run it, hold Alt, move the mouse, confirm deltas print only while Alt is held.
3. Confirm `HookInstalled` is `true` after construction (allow ~50ms for the hook thread to start).
4. Delete the temporary snippet once verified — it was scaffolding, not part of the app.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add Win32MouseHook global freelook input source"
```

---

### Task 7: ISpatializer and StereoPanSpatializer (fallback)

**Files:**
- Create: `src/SpotifyGameRadio.Core/Spatial/ISpatializer.cs`
- Create: `src/SpotifyGameRadio.Core/Spatial/StereoPanSpatializer.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Spatial/StereoPanSpatializerTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks (pure math).
- Produces: `ISpatializer { void SetSourcePosition(float x, float y, float z); void SetListenerOrientation(float yawDegrees, float pitchDegrees); void Process(float[] monoInput, int count, float[] stereoOutputInterleaved); }`, `StereoPanSpatializer : ISpatializer`. Consumed later by Task 8 (`SteamAudioSpatializer`, same interface) and Task 12 (`RadioPipeline`, as the guaranteed-available fallback).

- [ ] **Step 1: Write the failing test**

```csharp
using SpotifyGameRadio.Core.Spatial;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Spatial;

public class StereoPanSpatializerTests
{
    [Fact]
    public void SourceDirectlyAhead_ListenerFacingForward_PansEqualLeftRight()
    {
        var spatializer = new StereoPanSpatializer();
        spatializer.SetSourcePosition(x: 0f, y: 0f, z: 1f); // straight ahead
        spatializer.SetListenerOrientation(yawDegrees: 0f, pitchDegrees: 0f);

        var mono = new float[] { 1f };
        var stereo = new float[2];
        spatializer.Process(mono, 1, stereo);

        Assert.Equal(stereo[0], stereo[1], precision: 3); // L == R
    }

    [Fact]
    public void ListenerTurnedRight_SourceAppearsOnLeft()
    {
        var spatializer = new StereoPanSpatializer();
        spatializer.SetSourcePosition(x: 0f, y: 0f, z: 1f); // fixed straight ahead of vehicle
        spatializer.SetListenerOrientation(yawDegrees: 90f, pitchDegrees: 0f); // player looks right

        var mono = new float[] { 1f };
        var stereo = new float[2];
        spatializer.Process(mono, 1, stereo);

        // Source that was ahead is now off the listener's left ear.
        Assert.True(stereo[0] > stereo[1], $"Expected left ({stereo[0]}) > right ({stereo[1]}) after turning right.");
    }

    [Fact]
    public void ListenerTurnedLeft_SourceAppearsOnRight()
    {
        var spatializer = new StereoPanSpatializer();
        spatializer.SetSourcePosition(x: 0f, y: 0f, z: 1f);
        spatializer.SetListenerOrientation(yawDegrees: -90f, pitchDegrees: 0f); // player looks left

        var mono = new float[] { 1f };
        var stereo = new float[2];
        spatializer.Process(mono, 1, stereo);

        Assert.True(stereo[1] > stereo[0], $"Expected right ({stereo[1]}) > left ({stereo[0]}) after turning left.");
    }

    [Fact]
    public void Process_HandlesMultiSampleBuffers()
    {
        var spatializer = new StereoPanSpatializer();
        spatializer.SetSourcePosition(x: 0f, y: 0f, z: 1f);
        spatializer.SetListenerOrientation(yawDegrees: 0f, pitchDegrees: 0f);

        var mono = new float[] { 0.5f, -0.5f, 0.25f };
        var stereo = new float[6];
        spatializer.Process(mono, 3, stereo);

        Assert.Equal(stereo[0], stereo[1], precision: 3);
        Assert.Equal(stereo[2], stereo[3], precision: 3);
        Assert.Equal(stereo[4], stereo[5], precision: 3);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter StereoPanSpatializerTests`
Expected: FAIL — `ISpatializer`/`StereoPanSpatializer` don't exist yet.

- [ ] **Step 3: Write `ISpatializer.cs`**

```csharp
namespace SpotifyGameRadio.Core.Spatial;

public interface ISpatializer
{
    /// Fixed position of the source in vehicle/world space, meters: +X right, +Y up, +Z forward.
    void SetSourcePosition(float x, float y, float z);

    /// Current listener (player head) orientation relative to vehicle forward.
    void SetListenerOrientation(float yawDegrees, float pitchDegrees);

    /// Renders count mono input samples into count*2 interleaved stereo output samples.
    void Process(float[] monoInput, int count, float[] stereoOutputInterleaved);
}
```

- [ ] **Step 4: Write `StereoPanSpatializer.cs`**

```csharp
namespace SpotifyGameRadio.Core.Spatial;

/// Simple equal-power pan law based on the source's azimuth relative to the
/// listener, used as a guaranteed-available fallback when true HRTF
/// rendering (Steam Audio) is unavailable. Not a substitute for HRTF —
/// no elevation or distance cues, just left/right balance.
public class StereoPanSpatializer : ISpatializer
{
    private float _sourceX, _sourceY, _sourceZ;
    private float _yawRadians, _pitchRadians;

    public void SetSourcePosition(float x, float y, float z)
    {
        _sourceX = x;
        _sourceY = y;
        _sourceZ = z;
    }

    public void SetListenerOrientation(float yawDegrees, float pitchDegrees)
    {
        _yawRadians = yawDegrees * MathF.PI / 180f;
        _pitchRadians = pitchDegrees * MathF.PI / 180f;
    }

    public void Process(float[] monoInput, int count, float[] stereoOutputInterleaved)
    {
        // Rotate the fixed source position into listener space by the
        // inverse of the listener's yaw (listener turning right makes a
        // world-fixed source appear to swing left).
        float cosYaw = MathF.Cos(-_yawRadians);
        float sinYaw = MathF.Sin(-_yawRadians);
        float relativeX = _sourceX * cosYaw - _sourceZ * sinYaw;
        float relativeZ = _sourceX * sinYaw + _sourceZ * cosYaw;

        float azimuth = MathF.Atan2(relativeX, MathF.Max(relativeZ, 0.0001f)); // 0 = ahead, +pi/2 = right
        float pan = MathF.Clamp(azimuth / (MathF.PI / 2f), -1f, 1f); // -1 = full left, +1 = full right

        // Equal-power pan law.
        float angle = (pan + 1f) * MathF.PI / 4f; // maps [-1,1] -> [0, pi/2]
        float leftGain = MathF.Cos(angle);
        float rightGain = MathF.Sin(angle);

        for (int i = 0; i < count; i++)
        {
            float sample = monoInput[i];
            stereoOutputInterleaved[i * 2] = sample * leftGain;
            stereoOutputInterleaved[i * 2 + 1] = sample * rightGain;
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter StereoPanSpatializerTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add ISpatializer and StereoPanSpatializer fallback"
```

---

### Task 8: Steam Audio native integration (SteamAudioSpatializer)

**Files:**
- Create: `src/SpotifyGameRadio.Core/Spatial/SteamAudioNative.cs`
- Create: `src/SpotifyGameRadio.Core/Spatial/SteamAudioSpatializer.cs`
- Create: `src/SpotifyGameRadio.App/runtimes/win-x64/native/` (native binaries directory)
- Modify: `src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj` (copy native DLLs to output)

**Interfaces:**
- Consumes: `ISpatializer` (Task 7, implements the same interface).
- Produces: `SteamAudioSpatializer : ISpatializer, IDisposable`, plus `SteamAudioSpatializer.TryCreate(int sampleRate, int frameSize, out SteamAudioSpatializer? spatializer) -> bool` (returns `false` if the native library fails to load, so `RadioPipeline` in Task 12 can fall back to `StereoPanSpatializer` per the spec's error-handling requirement).

- [ ] **Step 1: Download and place the Steam Audio native library**

Steam Audio ships prebuilt binaries; there is no NuGet package, so it must be downloaded manually:

1. Download the Steam Audio SDK release (`steamaudio_<version>.zip`) from Valve's Steam Audio releases.
2. Copy `lib/windows-x64/phonon.dll` into `src/SpotifyGameRadio.App/runtimes/win-x64/native/phonon.dll`.
3. Do not commit large binaries directly if the repo will be shared publicly without Git LFS — for this project, commit it directly since the repo is private and the file is a few MB.

- [ ] **Step 2: Add `phonon.dll` to the App project's output**

Edit `src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`, add inside the existing `<Project>` element:

```xml
<ItemGroup>
  <None Include="runtimes\win-x64\native\phonon.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 3: Write `SteamAudioNative.cs` (P/Invoke declarations)**

```csharp
using System.Runtime.InteropServices;

namespace SpotifyGameRadio.Core.Spatial;

/// P/Invoke surface for the subset of Steam Audio's C API (phonon.h) this
/// app needs: context creation, an HRTF instance, and a binaural effect
/// that renders a mono source at a given direction into stereo output.
internal static class SteamAudioNative
{
    private const string Lib = "phonon";

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLVector3 { public float x, y, z; }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLContextSettings
    {
        public int version;
        public IntPtr logCallback;
        public IntPtr allocateCallback;
        public IntPtr freeCallback;
        public int simdLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAudioSettings
    {
        public int samplingRate;
        public int frameSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLHRTFSettings
    {
        public int type; // IPL_HRTFTYPE_DEFAULT = 0
        public IntPtr sofaFileName;
        public IntPtr sofaData;
        public int sofaDataSize;
        public float volume;
        public int normType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLBinauralEffectSettings
    {
        public IntPtr hrtf;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLAudioBuffer
    {
        public int numChannels;
        public int numSamples;
        public IntPtr data; // float**
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IPLBinauralEffectParams
    {
        public IPLVector3 direction;
        public int interpolation; // IPL_HRTFINTERPOLATION_NEAREST = 0
        public float spatialBlend;
        public IntPtr hrtf;
        public int peakDelays;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int iplContextCreate(ref IPLContextSettings settings, out IntPtr context);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void iplContextRelease(ref IntPtr context);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int iplHRTFCreate(IntPtr context, ref IPLAudioSettings audioSettings, ref IPLHRTFSettings hrtfSettings, out IntPtr hrtf);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void iplHRTFRelease(ref IntPtr hrtf);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int iplBinauralEffectCreate(IntPtr context, ref IPLAudioSettings audioSettings, ref IPLBinauralEffectSettings effectSettings, out IntPtr effect);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void iplBinauralEffectRelease(ref IntPtr effect);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int iplBinauralEffectApply(IntPtr effect, ref IPLBinauralEffectParams parameters, ref IPLAudioBuffer inBuffer, ref IPLAudioBuffer outBuffer);
}
```

*(Exact struct layouts must be cross-checked against the installed Steam Audio version's `phonon.h` during implementation — Steam Audio's API has changed across major versions. Treat this file as the first thing to fix if `SteamAudioSpatializer` throws `EntryPointNotFoundException` or produces garbage output during manual verification in Step 5.)*

- [ ] **Step 4: Write `SteamAudioSpatializer.cs`**

```csharp
using static SpotifyGameRadio.Core.Spatial.SteamAudioNative;

namespace SpotifyGameRadio.Core.Spatial;

public class SteamAudioSpatializer : ISpatializer, IDisposable
{
    private IntPtr _context;
    private IntPtr _hrtf;
    private IntPtr _effect;
    private readonly int _frameSize;

    private float _sourceX, _sourceY, _sourceZ;
    private float _yawRadians, _pitchRadians;

    private SteamAudioSpatializer(IntPtr context, IntPtr hrtf, IntPtr effect, int frameSize)
    {
        _context = context;
        _hrtf = hrtf;
        _effect = effect;
        _frameSize = frameSize;
    }

    /// Attempts to load the native library and create the HRTF pipeline.
    /// Returns false (never throws) if the native library is missing or
    /// fails to initialize, so callers can fall back to StereoPanSpatializer.
    public static bool TryCreate(int sampleRate, int frameSize, out SteamAudioSpatializer? spatializer)
    {
        spatializer = null;
        try
        {
            var contextSettings = new IPLContextSettings { version = 4 << 16 | 5 << 8 };
            if (iplContextCreate(ref contextSettings, out var context) != 0) return false;

            var audioSettings = new IPLAudioSettings { samplingRate = sampleRate, frameSize = frameSize };
            var hrtfSettings = new IPLHRTFSettings { type = 0, volume = 1f, normType = 0 };
            if (iplHRTFCreate(context, ref audioSettings, ref hrtfSettings, out var hrtf) != 0)
            {
                iplContextRelease(ref context);
                return false;
            }

            var effectSettings = new IPLBinauralEffectSettings { hrtf = hrtf };
            if (iplBinauralEffectCreate(context, ref audioSettings, ref effectSettings, out var effect) != 0)
            {
                iplHRTFRelease(ref hrtf);
                iplContextRelease(ref context);
                return false;
            }

            spatializer = new SteamAudioSpatializer(context, hrtf, effect, frameSize);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void SetSourcePosition(float x, float y, float z)
    {
        _sourceX = x;
        _sourceY = y;
        _sourceZ = z;
    }

    public void SetListenerOrientation(float yawDegrees, float pitchDegrees)
    {
        _yawRadians = yawDegrees * MathF.PI / 180f;
        _pitchRadians = pitchDegrees * MathF.PI / 180f;
    }

    public unsafe void Process(float[] monoInput, int count, float[] stereoOutputInterleaved)
    {
        // Rotate the fixed source into listener space (same convention as
        // StereoPanSpatializer) before handing it to Steam Audio as a direction vector.
        float cosYaw = MathF.Cos(-_yawRadians);
        float sinYaw = MathF.Sin(-_yawRadians);
        float relativeX = _sourceX * cosYaw - _sourceZ * sinYaw;
        float relativeZ = _sourceX * sinYaw + _sourceZ * cosYaw;
        float relativeY = _sourceY; // pitch cross-coupling ignored for a car-mounted source

        var direction = new IPLVector3 { x = relativeX, y = relativeY, z = -relativeZ };

        fixed (float* inPtr = monoInput)
        fixed (float* outLeft = new float[count])
        fixed (float* outRight = new float[count])
        {
            var inChannels = stackalloc IntPtr[1] { (IntPtr)inPtr };
            var outChannels = stackalloc IntPtr[2] { (IntPtr)outLeft, (IntPtr)outRight };

            var inBuffer = new IPLAudioBuffer { numChannels = 1, numSamples = count, data = (IntPtr)inChannels };
            var outBuffer = new IPLAudioBuffer { numChannels = 2, numSamples = count, data = (IntPtr)outChannels };

            var effectParams = new IPLBinauralEffectParams
            {
                direction = direction,
                interpolation = 0,
                spatialBlend = 1f,
                hrtf = _hrtf
            };

            iplBinauralEffectApply(_effect, ref effectParams, ref inBuffer, ref outBuffer);

            for (int i = 0; i < count; i++)
            {
                stereoOutputInterleaved[i * 2] = outLeft[i];
                stereoOutputInterleaved[i * 2 + 1] = outRight[i];
            }
        }
    }

    public void Dispose()
    {
        if (_effect != IntPtr.Zero) iplBinauralEffectRelease(ref _effect);
        if (_hrtf != IntPtr.Zero) iplHRTFRelease(ref _hrtf);
        if (_context != IntPtr.Zero) iplContextRelease(ref _context);
    }
}
```

- [ ] **Step 5: Enable unsafe code and verify build**

Edit `src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj`, add inside `<PropertyGroup>`:

```xml
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
```

Run: `dotnet build SpotifyGameRadio.sln`
Expected: build succeeds.

- [ ] **Step 6: Manual verification (no automated test — requires native library + real listening)**

1. Confirm `phonon.dll` is present at `src/SpotifyGameRadio.App/bin/Debug/net8.0-windows/runtimes/win-x64/native/phonon.dll` after building the App project.
2. Write a temporary console snippet that calls `SteamAudioSpatializer.TryCreate(48000, 1024, out var spatializer)`, feeds it a 1kHz test tone with the source at `(1, 0, 0)` (hard right) and listener yaw `0`, writes the resulting stereo buffer to a `.wav` file (use NAudio's `WaveFileWriter`), and listens to confirm the tone is audibly panned/filtered toward the right ear with HRTF coloration (not just a flat pan).
3. Repeat with `SetListenerOrientation(90, 0)` (listener turned right) and confirm the source now sounds like it's behind/left, matching a world-fixed source.
4. If `TryCreate` returns `false`, check that `phonon.dll` is a matching architecture (x64) and that struct layouts in `SteamAudioNative.cs` match the downloaded SDK version's `phonon.h`; fix and retest before proceeding.
5. Delete the temporary snippet once verified.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add Steam Audio HRTF spatializer with native library integration"
```

---

### Task 9: AudioSessionEnumerator

**Files:**
- Create: `src/SpotifyGameRadio.Core/Audio/AudioSessionEnumerator.cs`

**Interfaces:**
- Consumes: NAudio's `MMDeviceEnumerator`/`AudioSessionManager` APIs.
- Produces: `AudioSessionEnumerator.ListActiveSources() -> IReadOnlyList<AudioSourceInfo>` where `AudioSourceInfo { string ProcessName; int ProcessId; string DisplayName; }`. Consumed later by Task 13 (`MainViewModel`, to populate the source dropdown) and Task 10 (`WasapiProcessLoopbackCapture`, which takes a process name/id resolved via this enumerator).

- [ ] **Step 1: Write `AudioSessionEnumerator.cs`**

```csharp
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace SpotifyGameRadio.Core.Audio;

public record AudioSourceInfo(string ProcessName, int ProcessId, string DisplayName);

/// Lists running processes that currently have an active WASAPI audio
/// session on the default render device, so the user can pick a source
/// (Spotify, a browser, Discord, etc.) from a dropdown.
public static class AudioSessionEnumerator
{
    public static IReadOnlyList<AudioSourceInfo> ListActiveSources()
    {
        var results = new List<AudioSourceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        var sessions = defaultDevice.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            int pid = (int)session.GetProcessID;
            if (pid == 0) continue;

            string processName;
            try
            {
                using var process = Process.GetProcessById(pid);
                processName = process.ProcessName;
            }
            catch (ArgumentException)
            {
                continue; // process exited between enumeration and lookup
            }

            if (results.Any(r => r.ProcessId == pid)) continue;

            string displayName = string.IsNullOrWhiteSpace(session.DisplayName)
                ? processName
                : session.DisplayName;

            results.Add(new AudioSourceInfo(processName, pid, displayName));
        }

        return results;
    }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build SpotifyGameRadio.sln`
Expected: build succeeds.

- [ ] **Step 3: Manual verification (no automated test — requires real running processes)**

1. Write a temporary console snippet calling `AudioSessionEnumerator.ListActiveSources()` and printing the results.
2. Start Spotify and play a track, run the snippet, confirm `spotify` appears in the list.
3. Open a browser tab playing audio, confirm the browser process appears.
4. Delete the temporary snippet once verified.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: add AudioSessionEnumerator for source process discovery"
```

---

### Task 10: Audio capture (process-loopback with device-loopback fallback)

**Files:**
- Create: `src/SpotifyGameRadio.Core/Audio/AudioCaptureStatus.cs`
- Create: `src/SpotifyGameRadio.Core/Audio/IAudioCaptureService.cs`
- Create: `src/SpotifyGameRadio.Core/Audio/WasapiProcessLoopbackInterop.cs`
- Create: `src/SpotifyGameRadio.Core/Audio/WasapiProcessLoopbackCapture.cs`
- Create: `src/SpotifyGameRadio.Core/Audio/WasapiDeviceLoopbackCapture.cs`

**Interfaces:**
- Consumes: `AudioSourceInfo` (Task 9).
- Produces: `enum AudioCaptureStatus { NoSource, Capturing, Error }`, `IAudioCaptureService : IDisposable { NAudio.Wave.WaveFormat Format { get; } event EventHandler<AudioCaptureStatus>? StatusChanged; event EventHandler<float[]>? DataAvailable; void Start(string processName); void Stop(); }`, `WasapiProcessLoopbackCapture : IAudioCaptureService`, `WasapiDeviceLoopbackCapture : IAudioCaptureService`. Consumed later by Task 12 (`RadioPipeline`).

- [ ] **Step 1: Write `AudioCaptureStatus.cs`**

```csharp
namespace SpotifyGameRadio.Core.Audio;

public enum AudioCaptureStatus { NoSource, Capturing, Error }
```

- [ ] **Step 2: Write `IAudioCaptureService.cs`**

```csharp
using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

public interface IAudioCaptureService : IDisposable
{
    WaveFormat Format { get; }
    event EventHandler<AudioCaptureStatus>? StatusChanged;

    /// Raised per captured block with interleaved float samples at Format's channel count.
    event EventHandler<float[]>? DataAvailable;

    void Start(string processName);
    void Stop();
}
```

- [ ] **Step 3: Write `WasapiProcessLoopbackInterop.cs`**

```csharp
using System.Runtime.InteropServices;

namespace SpotifyGameRadio.Core.Audio;

/// Raw COM interop for Windows 10 20H1+ per-process WASAPI loopback capture
/// (ActivateAudioInterfaceAsync with AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK).
/// This is not exposed by NAudio, so IAudioClient/IAudioCaptureClient are
/// declared directly against their (stable, decades-old) WASAPI COM GUIDs
/// rather than depending on NAudio's higher-level wrapper.
internal static class WasapiProcessLoopbackInterop
{
    public const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    public const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    public const ushort VT_BLOB = 0x41;
    public const string VirtualAudioDeviceProcessLoopback = "VAD://Process_Loopback";
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    [StructLayout(LayoutKind.Sequential)]
    public struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public int ProcessLoopbackMode;
        public uint TargetProcessId;
    }

    /// Minimal PROPVARIANT laid out for the VT_BLOB case only (what
    /// ActivateAudioInterfaceAsync needs to receive AUDIOCLIENT_ACTIVATION_PARAMS):
    /// 8-byte header (vt + 3 reserved WORDs) then, 8-byte aligned, {cbSize:uint, pBlobData:IntPtr}.
    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT_BLOB
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint blobCbSize;
        [FieldOffset(16)] public IntPtr blobPBlobData;
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}

internal class ActivationCompletionHandler : WasapiProcessLoopbackInterop.IActivateAudioInterfaceCompletionHandler
{
    private readonly ManualResetEvent _completedEvent = new(initialState: false);
    public object? ActivatedInterface { get; private set; }
    public int ActivateResult { get; private set; }

    public void ActivateCompleted(WasapiProcessLoopbackInterop.IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        activateOperation.GetActivateResult(out int result, out object iface);
        ActivateResult = result;
        ActivatedInterface = iface;
        _completedEvent.Set();
    }

    public void Wait() => _completedEvent.WaitOne(TimeSpan.FromSeconds(5));
}
```

*(The `PROPVARIANT_BLOB` field offsets follow the documented Windows `PROPVARIANT`/`BLOB` layout on x64. This is the single piece of the plan built from struct-layout knowledge rather than a runnable reference, so treat Step 5's manual verification as mandatory before relying on this path — if activation fails, the first thing to check is these offsets against `propidl.h`/`Windows-classic-samples`'s `ApplicationLoopback` sample.)*

- [ ] **Step 4: Write `WasapiProcessLoopbackCapture.cs`**

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

public class WasapiProcessLoopbackCapture : IAudioCaptureService
{
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private WasapiProcessLoopbackInterop.IAudioClient? _rawAudioClient;
    private WasapiProcessLoopbackInterop.IAudioCaptureClient? _rawCaptureClient;
    private AutoResetEvent? _eventHandle;
    private Thread? _captureThread;
    private volatile bool _running;
    private System.Timers.Timer? _retryTimer;
    private string? _processName;

    public WaveFormat Format { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;

    public void Start(string processName)
    {
        _processName = processName;
        TryStart();

        _retryTimer = new System.Timers.Timer(2000);
        _retryTimer.Elapsed += (_, _) => { if (!_running) TryStart(); };
        _retryTimer.Start();
    }

    private void TryStart()
    {
        var process = Process.GetProcessesByName(_processName).FirstOrDefault();
        if (process is null)
        {
            StatusChanged?.Invoke(this, AudioCaptureStatus.NoSource);
            return;
        }

        IntPtr formatPtr = IntPtr.Zero;
        try
        {
            _rawAudioClient = ActivateProcessLoopbackAudioClient((uint)process.Id);

            const int sampleRate = 48000, channels = 2, bitsPerSample = 32;
            formatPtr = AllocIeeeFloatWaveFormat(sampleRate, channels, bitsPerSample);

            int hr = _rawAudioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                2_000_000, // 200ms buffer, 100ns units
                0,
                formatPtr,
                IntPtr.Zero);
            if (hr != 0) throw new InvalidOperationException($"IAudioClient.Initialize failed, hresult=0x{hr:X}");

            Format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

            _eventHandle = new AutoResetEvent(false);
            _rawAudioClient.SetEventHandle(_eventHandle.SafeWaitHandle.DangerousGetHandle());

            var captureClientGuid = typeof(WasapiProcessLoopbackInterop.IAudioCaptureClient).GUID;
            _rawAudioClient.GetService(ref captureClientGuid, out var serviceObj);
            _rawCaptureClient = (WasapiProcessLoopbackInterop.IAudioCaptureClient)serviceObj;

            _rawAudioClient.Start();
            _running = true;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "SGR-ProcessLoopback" };
            _captureThread.Start();

            StatusChanged?.Invoke(this, AudioCaptureStatus.Capturing);
        }
        catch (Exception)
        {
            StatusChanged?.Invoke(this, AudioCaptureStatus.Error);
        }
        finally
        {
            if (formatPtr != IntPtr.Zero) Marshal.FreeHGlobal(formatPtr);
        }
    }

    private static WasapiProcessLoopbackInterop.IAudioClient ActivateProcessLoopbackAudioClient(uint processId)
    {
        var activationParams = new WasapiProcessLoopbackInterop.AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = WasapiProcessLoopbackInterop.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackMode = WasapiProcessLoopbackInterop.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
            TargetProcessId = processId
        };

        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WasapiProcessLoopbackInterop.AUDIOCLIENT_ACTIVATION_PARAMS>());
        IntPtr propvariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WasapiProcessLoopbackInterop.PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var propvariant = new WasapiProcessLoopbackInterop.PROPVARIANT_BLOB
            {
                vt = WasapiProcessLoopbackInterop.VT_BLOB,
                blobCbSize = (uint)Marshal.SizeOf<WasapiProcessLoopbackInterop.AUDIOCLIENT_ACTIVATION_PARAMS>(),
                blobPBlobData = paramsPtr
            };
            Marshal.StructureToPtr(propvariant, propvariantPtr, false);

            var handler = new ActivationCompletionHandler();
            WasapiProcessLoopbackInterop.ActivateAudioInterfaceAsync(
                WasapiProcessLoopbackInterop.VirtualAudioDeviceProcessLoopback,
                WasapiProcessLoopbackInterop.IID_IAudioClient,
                propvariantPtr,
                handler,
                out _);

            handler.Wait();
            if (handler.ActivateResult != 0 || handler.ActivatedInterface is null)
                throw new InvalidOperationException($"ActivateAudioInterfaceAsync failed, hresult=0x{handler.ActivateResult:X}");

            return (WasapiProcessLoopbackInterop.IAudioClient)handler.ActivatedInterface;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
            Marshal.FreeHGlobal(propvariantPtr);
        }
    }

    private static IntPtr AllocIeeeFloatWaveFormat(int sampleRate, int channels, int bitsPerSample)
    {
        IntPtr ptr = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(ptr, 0, 3); // wFormatTag = WAVE_FORMAT_IEEE_FLOAT
        Marshal.WriteInt16(ptr, 2, (short)channels);
        Marshal.WriteInt32(ptr, 4, sampleRate);
        int blockAlign = channels * bitsPerSample / 8;
        Marshal.WriteInt32(ptr, 8, sampleRate * blockAlign); // nAvgBytesPerSec
        Marshal.WriteInt16(ptr, 12, (short)blockAlign);
        Marshal.WriteInt16(ptr, 14, (short)bitsPerSample);
        Marshal.WriteInt16(ptr, 16, 0); // cbSize
        return ptr;
    }

    private void CaptureLoop()
    {
        while (_running)
        {
            _eventHandle!.WaitOne(200);
            if (!_running) break;

            _rawCaptureClient!.GetNextPacketSize(out uint packetFrames);
            while (packetFrames > 0)
            {
                _rawCaptureClient.GetBuffer(out IntPtr dataPtr, out uint framesAvailable, out uint flags, out _, out _);

                int sampleCount = (int)framesAvailable * 2; // stereo
                var samples = new float[sampleCount];
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                    Marshal.Copy(dataPtr, samples, 0, sampleCount);

                DataAvailable?.Invoke(this, samples);

                _rawCaptureClient.ReleaseBuffer(framesAvailable);
                _rawCaptureClient.GetNextPacketSize(out packetFrames);
            }
        }
    }

    public void Stop()
    {
        _running = false;
        _retryTimer?.Stop();
        _captureThread?.Join(500);
        _rawAudioClient?.Stop();
        _rawCaptureClient = null;
        _rawAudioClient = null;
        _eventHandle?.Dispose();
        _eventHandle = null;
    }

    public void Dispose() => Stop();
}
```

- [ ] **Step 5: Manual verification (real Windows COM activation — the interop in Steps 3-4 needs empirical confirmation)**

1. Play audio in the target process (e.g. Spotify), start capture, confirm `DataAvailable` fires with non-silent samples (log RMS of each buffer to confirm).
2. Confirm audio from *other* processes (e.g. a game or another app) is NOT captured — play a second, distinct audio source simultaneously and verify only the target process's audio appears in the captured buffers.
3. If `ActivateAudioInterfaceAsync` returns a failure HRESULT or `IAudioClient.Initialize` fails, cross-check `PROPVARIANT_BLOB`'s field offsets and the activation sequence against Microsoft's `ApplicationLoopback` sample (`Windows-classic-samples` repo, `Audio/ApplicationLoopback`) — this is the one piece of the plan built from struct-layout knowledge rather than a runnable reference.
4. Test the fallback path: on a machine/VM without per-process loopback support (or by forcing the code path), confirm `WasapiDeviceLoopbackCapture` (Step 6) is used instead and still produces audio.

- [ ] **Step 6: Write `WasapiDeviceLoopbackCapture.cs` (whole-device fallback)**

```csharp
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

/// Fallback for Windows versions without per-process loopback support:
/// captures everything playing on the chosen output device, not just one
/// process. Used automatically when WasapiProcessLoopbackCapture fails.
public class WasapiDeviceLoopbackCapture : IAudioCaptureService
{
    private WasapiLoopbackCapture? _capture;

    public WaveFormat Format { get; private set; } = new WaveFormat(48000, 32, 1);

    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;

    public void Start(string processName)
    {
        // processName is accepted for interface parity with
        // WasapiProcessLoopbackCapture but not used here — this fallback
        // captures the whole default render device.
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        _capture = new WasapiLoopbackCapture(device);
        Format = _capture.WaveFormat;

        _capture.DataAvailable += (_, e) =>
        {
            int sampleCount = e.BytesRecorded / 4; // 32-bit float samples
            var samples = new float[sampleCount];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
            DataAvailable?.Invoke(this, samples);
        };
        _capture.RecordingStopped += (_, _) => StatusChanged?.Invoke(this, AudioCaptureStatus.NoSource);

        _capture.StartRecording();
        StatusChanged?.Invoke(this, AudioCaptureStatus.Capturing);
    }

    public void Stop()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();
}
```

- [ ] **Step 7: Verify build**

Run: `dotnet build SpotifyGameRadio.sln`
Expected: build succeeds (the `NotSupportedException` throw in `WasapiProcessLoopbackCapture.TryStart` is expected to remain until Step 5's manual completion is done on a Windows dev machine).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add WASAPI process-loopback and device-loopback capture services"
```

---

### Task 11: WasapiAudioOutput

**Files:**
- Create: `src/SpotifyGameRadio.Core/Audio/IAudioOutputService.cs`
- Create: `src/SpotifyGameRadio.Core/Audio/WasapiAudioOutput.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks (standalone WASAPI wrapper).
- Produces: `IAudioOutputService : IDisposable { void Start(string deviceId); void Stop(); void Write(float[] stereoInterleaved, int count); event EventHandler? DeviceLost; }`, `WasapiAudioOutput : IAudioOutputService`. Consumed later by Task 12 (`RadioPipeline`).

- [ ] **Step 1: Write `IAudioOutputService.cs`**

```csharp
namespace SpotifyGameRadio.Core.Audio;

public interface IAudioOutputService : IDisposable
{
    void Start(string deviceId);
    void Stop();
    void Write(float[] stereoInterleaved, int count);

    /// Raised if the output device disappears (e.g. headset unplugged).
    event EventHandler? DeviceLost;
}
```

- [ ] **Step 2: Write `WasapiAudioOutput.cs`**

```csharp
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

public class WasapiAudioOutput : IAudioOutputService
{
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;

    public event EventHandler? DeviceLost;

    public void Start(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice device;
        try
        {
            device = enumerator.GetDevice(deviceId);
        }
        catch (Exception)
        {
            device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        _buffer = new BufferedWaveProvider(format)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(200)
        };

        _output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 20);
        _output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) DeviceLost?.Invoke(this, EventArgs.Empty);
        };
        _output.Init(_buffer);
        _output.Play();
    }

    public void Write(float[] stereoInterleaved, int count)
    {
        if (_buffer is null) return;
        var bytes = new byte[count * 4];
        Buffer.BlockCopy(stereoInterleaved, 0, bytes, 0, bytes.Length);
        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Stop()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }

    public void Dispose() => Stop();
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build SpotifyGameRadio.sln`
Expected: build succeeds.

- [ ] **Step 4: Manual verification (no automated test — requires real audio hardware)**

1. Write a temporary console snippet that starts `WasapiAudioOutput` on the default device and writes a generated sine-wave buffer via `Write`.
2. Confirm audible tone plays.
3. Unplug/disable the output device mid-playback (or switch default device) and confirm `DeviceLost` fires without crashing the process.
4. Delete the temporary snippet once verified.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add WasapiAudioOutput render service"
```

---

### Task 12: RadioPipeline

**Files:**
- Create: `src/SpotifyGameRadio.Core/Pipeline/RadioPipeline.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Pipeline/RadioPipelineTests.cs`

**Interfaces:**
- Consumes: `IAudioCaptureService` (Task 10), `IAudioOutputService` (Task 11), `ISpatializer` (Task 7/8), `RadioEffectChain` (Task 4), `FreelookTracker` (Task 5), `RadioProfile`/`IConfigStore` (Task 1).
- Produces: `RadioPipeline(IAudioCaptureService capture, IAudioOutputService output, ISpatializer primarySpatializer, ISpatializer fallbackSpatializer, RadioEffectChain effectChain, FreelookTracker tracker)` with `.ApplyProfile(RadioProfile profile)`, `.Start()`, `.Stop()`, `.BufferUnderrunCount { get; }`, `event EventHandler<string>? Warning`. Consumed later by Task 13 (`MainViewModel`).

- [ ] **Step 1: Write the failing test**

```csharp
using SpotifyGameRadio.Core.Audio;
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Dsp;
using SpotifyGameRadio.Core.Pipeline;
using SpotifyGameRadio.Core.Spatial;
using SpotifyGameRadio.Core.Tracking;
using NAudio.Wave;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Pipeline;

public class FakeCaptureService : IAudioCaptureService
{
    public WaveFormat Format { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
    public event EventHandler<AudioCaptureStatus>? StatusChanged;
    public event EventHandler<float[]>? DataAvailable;
    public bool Started { get; private set; }

    public void Start(string processName) { Started = true; StatusChanged?.Invoke(this, AudioCaptureStatus.Capturing); }
    public void Stop() { Started = false; }
    public void Dispose() { }

    public void PushSamples(float[] samples) => DataAvailable?.Invoke(this, samples);
    public void RaiseNoSource() => StatusChanged?.Invoke(this, AudioCaptureStatus.NoSource);
}

public class FakeOutputService : IAudioOutputService
{
    public event EventHandler? DeviceLost;
    public List<float[]> WrittenBuffers { get; } = new();

    public void Start(string deviceId) { }
    public void Write(float[] stereoInterleaved, int count) => WrittenBuffers.Add(stereoInterleaved[..(count)]);
    public void Stop() { }
    public void Dispose() { }
    public void RaiseDeviceLost() => DeviceLost?.Invoke(this, EventArgs.Empty);
}

public class FakeSpatializer : ISpatializer
{
    public void SetSourcePosition(float x, float y, float z) { }
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

public class RadioPipelineTests
{
    private static RadioPipeline BuildPipeline(FakeCaptureService capture, FakeOutputService output, out FreelookTracker tracker)
    {
        var profile = new RadioProfile { WetDryMix = 0f }; // dry passthrough for deterministic assertions
        var fakeInput = new SpotifyGameRadio.Core.Tests.Tracking.FakeMouseInputSource();
        tracker = new FreelookTracker(fakeInput, profile);
        var effectChain = new RadioEffectChain(48000f);
        effectChain.ApplyProfile(profile);
        var spatializer = new FakeSpatializer();

        var pipeline = new RadioPipeline(capture, output, spatializer, spatializer, effectChain, tracker);
        pipeline.ApplyProfile(profile);
        return pipeline;
    }

    [Fact]
    public void CapturedAudio_FlowsThroughToOutput()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _);

        pipeline.Start();
        capture.PushSamples(new float[] { 0.5f, -0.5f, 0.25f });

        Assert.Single(output.WrittenBuffers);
        Assert.Equal(new float[] { 0.5f, 0.5f, -0.5f, -0.5f, 0.25f, 0.25f }, output.WrittenBuffers[0]);
    }

    [Fact]
    public void NoSourceStatus_RaisesWarning()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _);
        string? warning = null;
        pipeline.Warning += (_, msg) => warning = msg;

        pipeline.Start();
        capture.RaiseNoSource();

        Assert.NotNull(warning);
    }

    [Fact]
    public void OutputDeviceLost_RaisesWarning()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _);
        string? warning = null;
        pipeline.Warning += (_, msg) => warning = msg;

        pipeline.Start();
        output.RaiseDeviceLost();

        Assert.NotNull(warning);
    }

    [Fact]
    public void Start_StartsCaptureService()
    {
        var capture = new FakeCaptureService();
        var output = new FakeOutputService();
        var pipeline = BuildPipeline(capture, output, out _);

        pipeline.Start();

        Assert.True(capture.Started);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter RadioPipelineTests`
Expected: FAIL — `RadioPipeline` doesn't exist yet.

- [ ] **Step 3: Write `RadioPipeline.cs`**

```csharp
using SpotifyGameRadio.Core.Audio;
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Dsp;
using SpotifyGameRadio.Core.Spatial;
using SpotifyGameRadio.Core.Tracking;

namespace SpotifyGameRadio.Core.Pipeline;

public class RadioPipeline
{
    private readonly IAudioCaptureService _capture;
    private readonly IAudioOutputService _output;
    private readonly ISpatializer _primarySpatializer;
    private readonly ISpatializer _fallbackSpatializer;
    private ISpatializer _activeSpatializer;
    private readonly RadioEffectChain _effectChain;
    private readonly FreelookTracker _tracker;
    private RadioProfile _profile = new();

    public int BufferUnderrunCount { get; private set; }
    public event EventHandler<string>? Warning;

    public RadioPipeline(
        IAudioCaptureService capture,
        IAudioOutputService output,
        ISpatializer primarySpatializer,
        ISpatializer fallbackSpatializer,
        RadioEffectChain effectChain,
        FreelookTracker tracker)
    {
        _capture = capture;
        _output = output;
        _primarySpatializer = primarySpatializer;
        _fallbackSpatializer = fallbackSpatializer;
        _activeSpatializer = primarySpatializer;
        _effectChain = effectChain;
        _tracker = tracker;

        _capture.DataAvailable += OnDataAvailable;
        _capture.StatusChanged += OnCaptureStatusChanged;
        _output.DeviceLost += OnOutputDeviceLost;
    }

    public void ApplyProfile(RadioProfile profile)
    {
        _profile = profile;
        _effectChain.ApplyProfile(profile);
        _tracker.ApplyProfile(profile);
        _primarySpatializer.SetSourcePosition(profile.SourceX, profile.SourceY, profile.SourceZ);
        _fallbackSpatializer.SetSourcePosition(profile.SourceX, profile.SourceY, profile.SourceZ);
    }

    public void Start()
    {
        _output.Start(_profile.OutputDeviceId);
        _capture.Start(_profile.SourceProcessName);
    }

    public void Stop()
    {
        _capture.Stop();
        _output.Stop();
    }

    private void OnDataAvailable(object? sender, float[] monoSamples)
    {
        try
        {
            _tracker.Update(deltaSeconds: monoSamples.Length / (float)_capture.Format.SampleRate);
            _activeSpatializer.SetListenerOrientation(_tracker.YawDegrees, _tracker.PitchDegrees);

            var processed = (float[])monoSamples.Clone();
            _effectChain.Process(processed, processed.Length);

            var stereo = new float[processed.Length * 2];
            _activeSpatializer.Process(processed, processed.Length, stereo);

            _output.Write(stereo, stereo.Length);
        }
        catch (Exception)
        {
            BufferUnderrunCount++;
        }
    }

    private void OnCaptureStatusChanged(object? sender, AudioCaptureStatus status)
    {
        if (status == AudioCaptureStatus.NoSource)
            Warning?.Invoke(this, $"No audio detected from '{_profile.SourceProcessName}'. Waiting for it to start playing...");
        else if (status == AudioCaptureStatus.Error)
            Warning?.Invoke(this, "Audio capture failed unexpectedly.");
    }

    private void OnOutputDeviceLost(object? sender, EventArgs e)
    {
        Warning?.Invoke(this, "Output device disconnected. Falling back to system default.");
    }

    /// Switches to the fallback spatializer (e.g. Steam Audio failed to load).
    public void UseFallbackSpatializer()
    {
        _activeSpatializer = _fallbackSpatializer;
        Warning?.Invoke(this, "HRTF spatialization unavailable — using simple stereo panning instead.");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter RadioPipelineTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full test suite to confirm nothing else broke**

Run: `dotnet test SpotifyGameRadio.sln`
Expected: PASS (all tests across all previous tasks).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add RadioPipeline orchestrating capture, effects, spatialization, output"
```

---

### Task 13: WPF MainViewModel and MainWindow shell

**Files:**
- Create: `src/SpotifyGameRadio.App/ViewModels/RelayCommand.cs`
- Create: `src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs`
- Modify: `src/SpotifyGameRadio.App/MainWindow.xaml`
- Modify: `src/SpotifyGameRadio.App/MainWindow.xaml.cs`
- Modify: `src/SpotifyGameRadio.App/App.xaml.cs`

**Interfaces:**
- Consumes: `AudioSessionEnumerator` (Task 9), `RadioPipeline` (Task 12), `IConfigStore`/`RadioProfile` (Task 1), `Win32MouseHook` (Task 6), `WasapiProcessLoopbackCapture`/`WasapiDeviceLoopbackCapture` (Task 10), `WasapiAudioOutput` (Task 11), `StereoPanSpatializer`/`SteamAudioSpatializer` (Task 7/8).
- Produces: `MainViewModel` bindable properties and commands consumed by `MainWindow.xaml` bindings and by Task 14 (`SourcePositionCanvas`, which binds to `MainViewModel.SourceX`/`SourceZ`).

- [ ] **Step 1: Write `RelayCommand.cs`**

```csharp
using System.Windows.Input;

namespace SpotifyGameRadio.App.ViewModels;

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
```

- [ ] **Step 2: Write `MainViewModel.cs`**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using SpotifyGameRadio.Core.Audio;
using SpotifyGameRadio.Core.Config;
using SpotifyGameRadio.Core.Dsp;
using SpotifyGameRadio.Core.Pipeline;
using SpotifyGameRadio.Core.Spatial;
using SpotifyGameRadio.Core.Tracking;

namespace SpotifyGameRadio.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IConfigStore _configStore = new ConfigStore();
    private RadioPipeline? _pipeline;
    private Win32MouseHook? _mouseHook;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<AudioSourceInfo> AvailableSources { get; private set; } = Array.Empty<AudioSourceInfo>();
    public IReadOnlyList<string> AvailableProfiles => _configStore.ListProfiles();

    private string _statusMessage = "Idle";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private int _bufferUnderrunCount;
    public int BufferUnderrunCount
    {
        get => _bufferUnderrunCount;
        set { _bufferUnderrunCount = value; OnPropertyChanged(); }
    }

    public RadioProfile Profile { get; private set; } = new();

    public ICommand RefreshSourcesCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SaveProfileCommand { get; }
    public ICommand LoadProfileCommand { get; }

    public MainViewModel()
    {
        RefreshSourcesCommand = new RelayCommand(_ => RefreshSources());
        StartCommand = new RelayCommand(_ => Start());
        StopCommand = new RelayCommand(_ => Stop());
        SaveProfileCommand = new RelayCommand(_ => _configStore.Save(Profile));
        LoadProfileCommand = new RelayCommand(name => LoadProfile((string)name!));

        RefreshSources();
    }

    private void RefreshSources()
    {
        AvailableSources = AudioSessionEnumerator.ListActiveSources();
        OnPropertyChanged(nameof(AvailableSources));
    }

    private void LoadProfile(string name)
    {
        Profile = _configStore.Load(name);
        OnPropertyChanged(nameof(Profile));
    }

    private void Start()
    {
        var capture = new WasapiProcessLoopbackCapture();
        IAudioCaptureService activeCapture = capture;

        var output = new WasapiAudioOutput();
        var effectChain = new RadioEffectChain(48000f);
        var mouseHook = new Win32MouseHook(Profile.Hotkey);
        _mouseHook = mouseHook;
        var tracker = new FreelookTracker(mouseHook, Profile);

        ISpatializer fallback = new StereoPanSpatializer();
        ISpatializer primary = fallback;
        if (SteamAudioSpatializer.TryCreate(48000, 1024, out var steamAudio) && steamAudio is not null)
            primary = steamAudio;

        _pipeline = new RadioPipeline(activeCapture, output, primary, fallback, effectChain, tracker);
        _pipeline.ApplyProfile(Profile);
        _pipeline.Warning += (_, message) => Application.Current.Dispatcher.Invoke(() => StatusMessage = message);

        _pipeline.Start();
        StatusMessage = "Running";

        // The hook installs on a background thread; give it a moment, then
        // warn if it failed (spec requires freelook-disabled to be visible).
        var hookCheckTimer = new System.Timers.Timer(500) { AutoReset = false };
        hookCheckTimer.Elapsed += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            if (!mouseHook.HookInstalled)
                StatusMessage = "Freelook hook failed to install — radio will play fixed at center.";
        });
        hookCheckTimer.Start();
    }

    private void Stop()
    {
        _pipeline?.Stop();
        _mouseHook?.Dispose();
        StatusMessage = "Stopped";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 3: Write `MainWindow.xaml`**

```xml
<Window x:Class="SpotifyGameRadio.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:SpotifyGameRadio.App.ViewModels"
        Title="Spotify Game Radio" Height="760" Width="640">
    <Window.DataContext>
        <local:MainViewModel />
    </Window.DataContext>
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Source:" VerticalAlignment="Center" Margin="0,0,8,0" />
            <ComboBox Width="200" ItemsSource="{Binding AvailableSources}" DisplayMemberPath="DisplayName"
                      SelectedValuePath="ProcessName" SelectedValue="{Binding Profile.SourceProcessName}" />
            <Button Content="Refresh" Command="{Binding RefreshSourcesCommand}" Margin="8,0,0,0" />
        </StackPanel>

        <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="High-pass (Hz):" VerticalAlignment="Center" Width="120" />
            <Slider Minimum="50" Maximum="2000" Width="200" Value="{Binding Profile.HighPassHz}" />
        </StackPanel>

        <StackPanel Grid.Row="2" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Low-pass (Hz):" VerticalAlignment="Center" Width="120" />
            <Slider Minimum="1000" Maximum="8000" Width="200" Value="{Binding Profile.LowPassHz}" />
        </StackPanel>

        <StackPanel Grid.Row="3" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Distortion:" VerticalAlignment="Center" Width="120" />
            <Slider Minimum="0" Maximum="1" Width="200" Value="{Binding Profile.DistortionDrive}" />
        </StackPanel>

        <StackPanel Grid.Row="4" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Compressor Thresh (dB):" VerticalAlignment="Center" Width="120" />
            <Slider Minimum="-40" Maximum="0" Width="200" Value="{Binding Profile.CompressorThresholdDb}" />
        </StackPanel>

        <StackPanel Grid.Row="5" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Compressor Ratio:" VerticalAlignment="Center" Width="120" />
            <Slider Minimum="1" Maximum="20" Width="200" Value="{Binding Profile.CompressorRatio}" />
        </StackPanel>

        <StackPanel Grid.Row="6" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Static/Noise:" VerticalAlignment="Center" Width="120" />
            <Slider Minimum="0" Maximum="0.3" Width="200" Value="{Binding Profile.NoiseLevel}" />
        </StackPanel>

        <StackPanel Grid.Row="7" Orientation="Horizontal" Margin="0,0,0,8">
            <TextBlock Text="Wet/Dry Mix:" VerticalAlignment="Center" Width="120" />
            <Slider Minimum="0" Maximum="1" Width="200" Value="{Binding Profile.WetDryMix}" />
        </StackPanel>

        <StackPanel Grid.Row="8" Orientation="Horizontal" Margin="0,0,0,8">
            <Button Content="Start" Command="{Binding StartCommand}" Width="80" Margin="0,0,8,0" />
            <Button Content="Stop" Command="{Binding StopCommand}" Width="80" Margin="0,0,8,0" />
            <Button Content="Save Profile" Command="{Binding SaveProfileCommand}" Width="100" />
        </StackPanel>

        <StackPanel Grid.Row="10" Orientation="Horizontal" Margin="0,0,0,8" VerticalAlignment="Top">
            <TextBlock Text="{Binding StatusMessage}" FontWeight="Bold" TextWrapping="Wrap" MaxWidth="400" />
            <TextBlock Text="{Binding BufferUnderrunCount, StringFormat=' (underruns: {0})'}" Margin="8,0,0,0" />
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 4: Verify `MainWindow.xaml.cs` needs no changes**

The default WPF template's `MainWindow.xaml.cs` (constructor calling `InitializeComponent()`) is sufficient since all logic lives in `MainViewModel` via bindings. Confirm it still contains just that.

- [ ] **Step 5: Verify build and run**

Run: `dotnet build SpotifyGameRadio.sln`
Expected: build succeeds.

Run: `dotnet run --project src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: window opens, source dropdown populates with running audio sessions, sliders are interactive.

- [ ] **Step 6: Manual verification**

1. Start Spotify, play a track.
2. Launch the app, click Refresh, select Spotify from the dropdown.
3. Click Start, confirm `StatusMessage` shows "Running" (or a warning if Task 10's process-loopback completion from Step 5 isn't finished yet — acceptable at this stage, full audio verification happens in Task 15).
4. Click Stop, confirm no crash.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add WPF MainViewModel and main window shell"
```

---

### Task 14: SourcePositionCanvas (2D top-down position picker)

**Files:**
- Create: `src/SpotifyGameRadio.App/Controls/SourcePositionCanvas.xaml`
- Create: `src/SpotifyGameRadio.App/Controls/SourcePositionCanvas.xaml.cs`
- Modify: `src/SpotifyGameRadio.App/MainWindow.xaml` (add the control to the layout)

**Interfaces:**
- Consumes: `MainViewModel.Profile.SourceX` / `.SourceZ` (Task 13).
- Produces: `SourcePositionCanvas` user control with dependency properties `SourceX` (double) and `SourceZ` (double), two-way bindable.

- [ ] **Step 1: Write `SourcePositionCanvas.xaml`**

```xml
<UserControl x:Class="SpotifyGameRadio.App.Controls.SourcePositionCanvas"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="200" Height="200">
    <Border BorderBrush="Gray" BorderThickness="1" Background="#111">
        <Canvas x:Name="PlacementCanvas" MouseLeftButtonDown="OnCanvasMouseDown" MouseMove="OnCanvasMouseMove">
            <!-- Listener marker fixed at center, facing up (forward = -Y on screen). -->
            <Ellipse Width="10" Height="10" Fill="White" Canvas.Left="95" Canvas.Top="95" IsHitTestVisible="False" />
            <Ellipse x:Name="SourceMarker" Width="14" Height="14" Fill="Orange"
                     Canvas.Left="93" Canvas.Top="93" IsHitTestVisible="False" />
        </Canvas>
    </Border>
</UserControl>
```

- [ ] **Step 2: Write `SourcePositionCanvas.xaml.cs`**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SpotifyGameRadio.App.Controls;

public partial class SourcePositionCanvas : UserControl
{
    public static readonly DependencyProperty SourceXProperty = DependencyProperty.Register(
        nameof(SourceX), typeof(double), typeof(SourcePositionCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged));

    public static readonly DependencyProperty SourceZProperty = DependencyProperty.Register(
        nameof(SourceZ), typeof(double), typeof(SourcePositionCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPositionChanged));

    public double SourceX
    {
        get => (double)GetValue(SourceXProperty);
        set => SetValue(SourceXProperty, value);
    }

    public double SourceZ
    {
        get => (double)GetValue(SourceZProperty);
        set => SetValue(SourceZProperty, value);
    }

    private const double MetersPerPixel = 0.02; // 100px = 2 meters from center each way
    private const double CenterPixel = 100;
    private bool _dragging;

    public SourcePositionCanvas()
    {
        InitializeComponent();
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SourcePositionCanvas)d).UpdateMarkerFromProperties();
    }

    private void UpdateMarkerFromProperties()
    {
        double pixelX = CenterPixel + SourceX / MetersPerPixel - 7;
        double pixelY = CenterPixel - SourceZ / MetersPerPixel - 7; // +Z (forward) is up on screen
        Canvas.SetLeft(SourceMarker, pixelX);
        Canvas.SetTop(SourceMarker, pixelY);
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        UpdateFromMouse(e.GetPosition(PlacementCanvas));
        PlacementCanvas.CaptureMouse();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragging = false;
            PlacementCanvas.ReleaseMouseCapture();
            return;
        }
        UpdateFromMouse(e.GetPosition(PlacementCanvas));
    }

    private void UpdateFromMouse(Point point)
    {
        SourceX = (point.X - CenterPixel) * MetersPerPixel;
        SourceZ = (CenterPixel - point.Y) * MetersPerPixel;
    }
}
```

- [ ] **Step 3: Add the control to `MainWindow.xaml`**

Edit `src/SpotifyGameRadio.App/MainWindow.xaml`: add the namespace import to the `<Window>` tag and place the control in row 5.

```xml
xmlns:controls="clr-namespace:SpotifyGameRadio.App.Controls"
```

```xml
<controls:SourcePositionCanvas Grid.Row="9"
    SourceX="{Binding Profile.SourceX}"
    SourceZ="{Binding Profile.SourceZ}"
    HorizontalAlignment="Left" />
```

*(This lands in the `Auto` row added between the DSP sliders/buttons and the status row in Task 13's `MainWindow.xaml` — row 9 of the 11-row grid, immediately above the status row at row 10.)*

*(Note: `Profile.SourceX`/`SourceZ` are `float` on `RadioProfile` but the control's dependency properties are `double`; WPF's default binding converter handles the numeric conversion automatically, so no explicit converter is needed.)*

- [ ] **Step 4: Verify build and run**

Run: `dotnet build SpotifyGameRadio.sln`
Expected: build succeeds.

Run: `dotnet run --project src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: window shows the placement canvas with a white center dot (listener) and an orange marker (source).

- [ ] **Step 5: Manual verification**

1. Click and drag within the canvas, confirm the orange marker follows the mouse.
2. Confirm dragging up increases `Profile.SourceZ` (forward) and dragging right increases `Profile.SourceX`.
3. Release the mouse outside the canvas bounds while still holding the button, confirm no crash (drag should stop cleanly per `OnCanvasMouseMove`'s button-state check).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add 2D top-down source position picker control"
```

---

### Task 15: End-to-end wiring, setup docs, and full verification

**Files:**
- Modify: `src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs` (wire `SteamAudioSpatializer` fallback notification, `BufferUnderrunCount` polling)
- Create: `docs/SETUP.md`

**Interfaces:**
- Consumes: everything from Tasks 1-14.
- Produces: a working end-to-end app plus setup documentation. No new public interfaces.

- [ ] **Step 1: Wire fallback and underrun polling into `MainViewModel`**

Edit `src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs`, in `Start()`, after `_pipeline.Start();`:

```csharp
if (primary == fallback)
    StatusMessage = "HRTF unavailable — using simple stereo panning.";

var underrunTimer = new System.Timers.Timer(1000);
underrunTimer.Elapsed += (_, _) => Application.Current.Dispatcher.Invoke(() =>
{
    if (_pipeline is not null) BufferUnderrunCount = _pipeline.BufferUnderrunCount;
});
underrunTimer.Start();
```

- [ ] **Step 2: Write `docs/SETUP.md`**

```markdown
# Setup

## Requirements
- Windows 10 20H1 (build 19041) or later for per-process audio capture.
  Older Windows falls back to whole-device capture automatically.
- .NET 8 SDK.
- `phonon.dll` (Steam Audio native library) placed at
  `src/SpotifyGameRadio.App/runtimes/win-x64/native/phonon.dll` — see
  Task 8 of the implementation plan for download instructions. Without
  it, the app still runs but falls back to simple stereo panning
  instead of true HRTF.

## Running

    dotnet run --project src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj

## First-time use

1. Start your audio source (Spotify, a browser tab, etc.) and begin playback.
2. Launch the app, click Refresh, select your source from the dropdown.
3. Drag the orange marker on the position canvas to where you want the
   radio to sound like it's coming from (e.g. slightly right and ahead
   for a dashboard-mounted radio).
4. Set your freelook hotkey to match your game's freelook key, and set
   Mouse Sensitivity / Max Yaw / Max Pitch to roughly match your
   in-game sensitivity and freelook angle limits — this is an
   approximation, not a memory read, so expect to tune it by ear.
5. Click Start, then hold your freelook key and look around in-game —
   the radio audio should shift as if it were mounted in the vehicle.
6. Click Save Profile to keep these settings for next time.
```

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test SpotifyGameRadio.sln`
Expected: PASS (all unit tests from Tasks 1-12).

- [ ] **Step 4: Full manual end-to-end verification**

1. Start Spotify (or another source), play music.
2. Launch the app, select the source, place the radio position slightly off-center on the canvas.
3. Click Start.
4. Put on headphones. Confirm the radio audio is audibly filtered (thinner/radio-like, not full-fidelity).
5. Hold the configured freelook hotkey and move the mouse left/right; confirm the audio image shifts (using Steam Audio HRTF if available, or the stereo pan fallback otherwise).
6. Release the hotkey; confirm the audio image eases back toward its original position rather than snapping or staying stuck.
7. Stop the source process (e.g. pause/quit Spotify); confirm the status message reports no source without the app crashing.
8. Unplug/switch the output device while running; confirm it falls back to default output without crashing.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: wire fallback/underrun reporting and add setup docs"
```
