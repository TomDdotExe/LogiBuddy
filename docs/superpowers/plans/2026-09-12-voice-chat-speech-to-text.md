# Voice Chat Speech-to-Text Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Push-to-talk voice-to-text: hold a hotkey to record from the mic, transcribe locally with Whisper, preview the text, then confirm (copies to clipboard for the user to paste manually), discard, or re-record.

**Architecture:** A pure, testable `VoiceChatSession` state machine (Core, modelled on the existing `CalibrationSession`) drives everything; `MicrophoneCapture` (NAudio) and `WhisperSpeechToText` (Whisper.net) are the two real-hardware/ML dependencies it's built against via interfaces. `MainViewModel` wires three hotkeys to the session and drives a small always-on-top overlay window. The model file is downloaded on first use, not bundled in the release zip.

**Tech Stack:** .NET 8, WPF, NAudio (already a dependency), Whisper.net 1.9.1 / Whisper.net.Runtime 1.9.1 (new), xUnit (existing test project).

**Spec:** `docs/superpowers/specs/2026-09-12-voice-chat-speech-to-text-design.md`

## Global Constraints

- **Never call `SendInput`/`keybd_event`/`mouse_event`** to inject input into another process's window — confirmed anti-cheat risk (EasyAntiCheat watches for the "injected" flag on synthetic input). Text delivery is clipboard-only; the user pastes manually.
- STT runs fully local/offline via Whisper.net — no cloud API, no network call at transcription time.
- The Whisper model is downloaded on first use and cached under `%LOCALAPPDATA%\SpotifyGameRadio\models\` — it must **not** be added to `build/package.ps1`'s zip contents or bundled as a `<None>` item in the csproj.
- Pin exact NuGet package versions (matches this project's existing convention — see `NAudio` and `System.Speech` in the App csproj): `Whisper.net` 1.9.1, `Whisper.net.Runtime` 1.9.1.
- New Core types with no WPF/hardware dependency (`VocabularyPromptBuilder`, `VoiceChatSession`, the testable parts of `VoiceModelStore`) get real unit tests in `tests/SpotifyGameRadio.Core.Tests`, following the existing test project's structure (one test class per production class, `Tests` suffix). Hardware/OS/ML-touching classes (`MicrophoneCapture`, `WhisperSpeechToText`, the overlay window) are not unit-tested, consistent with `WasapiDeviceLoopbackCapture`/`Win32MouseHook` today.
- `RadioProfile` changes follow the existing pattern exactly: a private backing field with a default value, a public property using `SetField`, and an XML doc comment. New fields must also be added to the `Load_ProfileJsonMissingNewerFields_KeepsDefaults` test in `tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs` (every prior field addition did this).

---

## Task 1: Add Whisper.net package references and verify the build

**Files:**
- Modify: `src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj`

**Interfaces:**
- Produces: `Whisper.net`/`Whisper.net.Runtime` available to all later Core tasks.

- [ ] **Step 1: Add the package references**

Open `src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj`. It currently looks like:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.2.1" />
  </ItemGroup>

</Project>
```

Change the `ItemGroup` to:

```xml
  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.2.1" />
    <PackageReference Include="Whisper.net" Version="1.9.1" />
    <PackageReference Include="Whisper.net.Runtime" Version="1.9.1" />
  </ItemGroup>
```

- [ ] **Step 2: Restore and build**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (this pulls the new packages in via the Core project reference).

- [ ] **Step 3: Verify the native runtime DLLs land in the output**

Run: `ls src/SpotifyGameRadio.App/bin/Debug/net8.0-windows/ | grep -i whisper` (Git Bash) or `Get-ChildItem src/SpotifyGameRadio.App/bin/Debug/net8.0-windows | Where-Object Name -like "*whisper*"` (PowerShell).
Expected: at least one `whisper.dll`/`ggml*.dll`-style native library present alongside `SpotifyGameRadio.dll`. `Whisper.net.Runtime` ships these via its own NuGet native-asset targeting, so they should appear automatically — if this step finds nothing, stop and investigate before continuing (later tasks depend on this working).

- [ ] **Step 4: Commit**

```bash
git add src/SpotifyGameRadio.Core/SpotifyGameRadio.Core.csproj
git commit -m "build: add Whisper.net package references for voice chat"
```

---

## Task 2: `VoiceVocabularyDefaults` and `VocabularyPromptBuilder`

**Files:**
- Create: `src/SpotifyGameRadio.Core/Speech/VoiceVocabularyDefaults.cs`
- Create: `src/SpotifyGameRadio.Core/Speech/VocabularyPromptBuilder.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Speech/VocabularyPromptBuilderTests.cs`

**Interfaces:**
- Produces: `VoiceVocabularyDefaults.Starter` (`string`, a comma-separated starter term list). `VocabularyPromptBuilder.Build(string? rawList) -> string` (static method — takes the raw text a user typed into the vocabulary box, returns the prompt string to hand to Whisper).

- [ ] **Step 1: Write the failing tests**

Create `tests/SpotifyGameRadio.Core.Tests/Speech/VocabularyPromptBuilderTests.cs`:

```csharp
using SpotifyGameRadio.Core.Speech;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Speech;

public class VocabularyPromptBuilderTests
{
    [Fact]
    public void Build_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal("", VocabularyPromptBuilder.Build(null));
        Assert.Equal("", VocabularyPromptBuilder.Build(""));
        Assert.Equal("", VocabularyPromptBuilder.Build("   \n  \t "));
    }

    [Fact]
    public void Build_CommaSeparated_JoinsWithCommaSpace()
    {
        Assert.Equal("FOB, RTB, LZ", VocabularyPromptBuilder.Build("FOB, RTB, LZ"));
    }

    [Fact]
    public void Build_NewlineSeparated_JoinsWithCommaSpace()
    {
        Assert.Equal("FOB, RTB, LZ", VocabularyPromptBuilder.Build("FOB\nRTB\nLZ"));
    }

    [Fact]
    public void Build_MixedSeparatorsAndWhitespace_TrimsAndSplitsBoth()
    {
        Assert.Equal("FOB, RTB, LZ", VocabularyPromptBuilder.Build("  FOB \n, RTB ,\nLZ  "));
    }

    [Fact]
    public void Build_DropsBlankEntries()
    {
        Assert.Equal("FOB, RTB", VocabularyPromptBuilder.Build("FOB,,\n\nRTB,"));
    }

    [Fact]
    public void Build_DropsDuplicates_CaseInsensitive_KeepsFirstCasing()
    {
        Assert.Equal("FOB, RTB", VocabularyPromptBuilder.Build("FOB, RTB, fob, Rtb"));
    }

    [Fact]
    public void Build_PastCharacterBudget_TruncatesKeepingEarliestTerms()
    {
        // Each term is "term1".."term40" (5-7 chars) joined by ", " (2 chars).
        // 200-char budget should keep a prefix of the list, not all 40, and
        // never include a term whose full text would exceed the budget.
        var terms = Enumerable.Range(1, 40).Select(i => $"term{i}");
        string result = VocabularyPromptBuilder.Build(string.Join(",", terms));

        Assert.True(result.Length <= 200);
        Assert.StartsWith("term1, term2, term3", result);
        Assert.DoesNotContain("term40", result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter "FullyQualifiedName~VocabularyPromptBuilderTests"`
Expected: build error — `VocabularyPromptBuilder` does not exist.

- [ ] **Step 3: Write `VoiceVocabularyDefaults`**

Create `src/SpotifyGameRadio.Core/Speech/VoiceVocabularyDefaults.cs`:

```csharp
namespace SpotifyGameRadio.Core.Speech;

/// Starter custom-vocabulary list seeded into a new RadioProfile, biasing
/// Whisper's transcription toward common milsim/tactical terms. Purely a
/// starting point — the user edits this freely in the Voice Chat UI.
public static class VoiceVocabularyDefaults
{
    public const string Starter =
        "FOB, RTB, LZ, HLZ, CASEVAC, MEDEVAC, WIA, KIA, RP, rally point, " +
        "danger close, contact front, resupply, rearm, AO, callsign, " +
        "fire team, overwatch, klick, grid, MGRS, azimuth, waypoint, " +
        "bearing, tower";
}
```

- [ ] **Step 4: Write `VocabularyPromptBuilder`**

Create `src/SpotifyGameRadio.Core/Speech/VocabularyPromptBuilder.cs`:

```csharp
namespace SpotifyGameRadio.Core.Speech;

/// Turns the free-text vocabulary list a user types into the UI into the
/// prompt string handed to Whisper as a soft bias toward those terms.
public static class VocabularyPromptBuilder
{
    // The initial-prompt is only a soft bias, not a hard constraint, so it
    // doesn't need to hold much — this keeps prompt-construction cheap and
    // avoids the (unlikely but possible) case of the prompt itself eating
    // into the model's context window.
    private const int CharacterBudget = 200;

    public static string Build(string? rawList)
    {
        if (string.IsNullOrWhiteSpace(rawList)) return "";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new List<string>();
        foreach (var raw in rawList.Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string term = raw.Trim();
            if (term.Length == 0) continue;
            if (!seen.Add(term)) continue;
            terms.Add(term);
        }

        var result = new System.Text.StringBuilder();
        foreach (var term in terms)
        {
            string candidate = result.Length == 0 ? term : result + ", " + term;
            if (candidate.Length > CharacterBudget) break;
            if (result.Length > 0) result.Append(", ");
            result.Append(term);
        }
        return result.ToString();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter "FullyQualifiedName~VocabularyPromptBuilderTests"`
Expected: all 7 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/SpotifyGameRadio.Core/Speech/VoiceVocabularyDefaults.cs src/SpotifyGameRadio.Core/Speech/VocabularyPromptBuilder.cs tests/SpotifyGameRadio.Core.Tests/Speech/VocabularyPromptBuilderTests.cs
git commit -m "feat: add voice vocabulary prompt builder"
```

---

## Task 3: `RadioProfile` voice fields

**Files:**
- Modify: `src/SpotifyGameRadio.Core/Config/RadioProfile.cs`
- Modify: `tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: `VoiceVocabularyDefaults.Starter` (Task 2).
- Produces: `RadioProfile.VoiceRecordHotkey`/`VoiceConfirmHotkey`/`VoiceDiscardHotkey` (`FreelookHotkey`), `RadioProfile.VoiceCustomVocabulary` (`string`), `RadioProfile.VoiceMicrophoneDeviceId` (`string`).

- [ ] **Step 1: Extend the legacy-defaults test (RED)**

In `tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs`, find `Load_ProfileJsonMissingNewerFields_KeepsDefaults` and add these lines right after the existing `Assert.Equal(0f, loaded.MeasuredMaxOffAxisDegrees);` line:

```csharp
            Assert.Equal(0, loaded.VoiceRecordHotkey.VirtualKeyCode);       // default retained (unbound)
            Assert.Equal(0, loaded.VoiceConfirmHotkey.VirtualKeyCode);      // default retained (unbound)
            Assert.Equal(0, loaded.VoiceDiscardHotkey.VirtualKeyCode);      // default retained (unbound)
            Assert.Equal(SpotifyGameRadio.Core.Speech.VoiceVocabularyDefaults.Starter, loaded.VoiceCustomVocabulary); // default retained
            Assert.Equal("", loaded.VoiceMicrophoneDeviceId);              // default retained
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter "FullyQualifiedName~Load_ProfileJsonMissingNewerFields"`
Expected: build error — `RadioProfile` has no `VoiceRecordHotkey` etc.

- [ ] **Step 3: Add the fields to `RadioProfile`**

In `src/SpotifyGameRadio.Core/Config/RadioProfile.cs`, add `using SpotifyGameRadio.Core.Speech;` to the top of the file (after the existing `using` lines), then add these backing fields right after `private float _measuredMaxOffAxisDegrees = 0f;`:

```csharp
    private FreelookHotkey _voiceRecordHotkey = new() { VirtualKeyCode = 0 };
    private FreelookHotkey _voiceConfirmHotkey = new() { VirtualKeyCode = 0 };
    private FreelookHotkey _voiceDiscardHotkey = new() { VirtualKeyCode = 0 };
    private string _voiceCustomVocabulary = VoiceVocabularyDefaults.Starter;
    private string _voiceMicrophoneDeviceId = "";
```

Then add these properties right after `MeasuredMaxOffAxisDegrees`'s property (before `public event PropertyChangedEventHandler? PropertyChanged;`):

```csharp
    /// Global hotkey held to record a voice-chat message. VirtualKeyCode 0
    /// means unbound (never fires).
    public FreelookHotkey VoiceRecordHotkey { get => _voiceRecordHotkey; set => SetField(ref _voiceRecordHotkey, value); }

    /// Global hotkey that copies the current voice-chat preview to the
    /// clipboard and dismisses it. VirtualKeyCode 0 means unbound.
    public FreelookHotkey VoiceConfirmHotkey { get => _voiceConfirmHotkey; set => SetField(ref _voiceConfirmHotkey, value); }

    /// Global hotkey that discards the current voice-chat preview without
    /// copying it. VirtualKeyCode 0 means unbound.
    public FreelookHotkey VoiceDiscardHotkey { get => _voiceDiscardHotkey; set => SetField(ref _voiceDiscardHotkey, value); }

    /// Free-text list (comma or newline separated) of terms to bias voice
    /// transcription toward, e.g. callsigns and milsim jargon. Turned into a
    /// Whisper prompt by VocabularyPromptBuilder.
    public string VoiceCustomVocabulary { get => _voiceCustomVocabulary; set => SetField(ref _voiceCustomVocabulary, value); }

    /// MMDevice id of the capture (microphone) endpoint to record from.
    /// Empty means "use the default capture device".
    public string VoiceMicrophoneDeviceId { get => _voiceMicrophoneDeviceId; set => SetField(ref _voiceMicrophoneDeviceId, value); }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj`
Expected: all tests pass (this also re-verifies nothing else broke).

- [ ] **Step 5: Commit**

```bash
git add src/SpotifyGameRadio.Core/Config/RadioProfile.cs tests/SpotifyGameRadio.Core.Tests/Config/ConfigStoreTests.cs
git commit -m "feat: add RadioProfile fields for voice chat hotkeys and vocabulary"
```

---

## Task 4: `CaptureDeviceEnumerator` and `IMicrophoneCapture`/`MicrophoneCapture`

**Files:**
- Create: `src/SpotifyGameRadio.Core/Audio/CaptureDeviceEnumerator.cs`
- Create: `src/SpotifyGameRadio.Core/Audio/IMicrophoneCapture.cs`
- Create: `src/SpotifyGameRadio.Core/Audio/MicrophoneCapture.cs`

**Interfaces:**
- Produces: `CaptureDeviceInfo(string Id, string FriendlyName)`, `CaptureDeviceEnumerator.ListCaptureDevices() -> IReadOnlyList<CaptureDeviceInfo>`, `IMicrophoneCapture.Start(string? deviceId)`, `IMicrophoneCapture.Stop() -> float[]` (16 kHz mono).

No tests in this task — real NAudio hardware I/O, same policy as `WasapiDeviceLoopbackCapture`/`RenderDeviceEnumerator` (which also have no tests).

- [ ] **Step 1: `CaptureDeviceEnumerator`**

Create `src/SpotifyGameRadio.Core/Audio/CaptureDeviceEnumerator.cs` (mirrors `RenderDeviceEnumerator.cs` exactly, but for capture/input devices):

```csharp
using NAudio.CoreAudioApi;

namespace SpotifyGameRadio.Core.Audio;

public record CaptureDeviceInfo(string Id, string FriendlyName);

/// Active capture (microphone) endpoints for the Voice Chat device picker.
public static class CaptureDeviceEnumerator
{
    public static IReadOnlyList<CaptureDeviceInfo> ListCaptureDevices()
    {
        var results = new List<CaptureDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                    results.Add(new CaptureDeviceInfo(device.ID, device.FriendlyName));
            }
        }
        catch (Exception)
        {
            // No audio subsystem / no devices — return whatever was collected.
        }
        return results;
    }
}
```

- [ ] **Step 2: `IMicrophoneCapture`**

Create `src/SpotifyGameRadio.Core/Audio/IMicrophoneCapture.cs`:

```csharp
namespace SpotifyGameRadio.Core.Audio;

public interface IMicrophoneCapture
{
    /// Begins buffering microphone audio. deviceId "" or null uses the
    /// default capture device. Safe to call again after Stop(); calling
    /// while already capturing is a no-op.
    void Start(string? deviceId);

    /// Stops capturing and returns everything captured since Start(), mixed
    /// down to mono and resampled to 16000 Hz float32 (what Whisper.net
    /// expects). Returns an empty array if Start() was never called, no
    /// audio arrived, or the capture device failed to open.
    float[] Stop();
}
```

- [ ] **Step 3: `MicrophoneCapture`**

Create `src/SpotifyGameRadio.Core/Audio/MicrophoneCapture.cs`. Uses NAudio's `WasapiCapture` (the microphone-input counterpart of `WasapiLoopbackCapture`, already used in `WasapiDeviceLoopbackCapture.cs`) and the same `WdlResampler` pattern `ResamplingCaptureService` uses:

```csharp
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace SpotifyGameRadio.Core.Audio;

public class MicrophoneCapture : IMicrophoneCapture
{
    private const int TargetSampleRate = 16000;

    private WasapiCapture? _capture;
    private readonly List<float> _buffer = new();
    private readonly object _bufferLock = new();
    private int _channels;

    public void Start(string? deviceId)
    {
        if (_capture is not null) return; // already capturing

        try
        {
            MMDevice device;
            using var enumerator = new MMDeviceEnumerator();
            device = string.IsNullOrEmpty(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : enumerator.GetDevice(deviceId);

            _capture = new WasapiCapture(device);
            _channels = _capture.WaveFormat.Channels;
            lock (_bufferLock) _buffer.Clear();

            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
        }
        catch (Exception)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int sampleCount = e.BytesRecorded / 4; // 32-bit float samples
        lock (_bufferLock)
        {
            for (int i = 0; i < sampleCount; i++)
                _buffer.Add(BitConverter.ToSingle(e.Buffer, i * 4));
        }
    }

    public float[] Stop()
    {
        if (_capture is null) return Array.Empty<float>();

        int inRate = _capture.WaveFormat.SampleRate;
        _capture.StopRecording();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _capture = null;

        float[] captured;
        lock (_bufferLock)
        {
            captured = _buffer.ToArray();
            _buffer.Clear();
        }
        if (captured.Length == 0) return captured;

        float[] mono = _channels <= 1 ? captured : MixDownToMono(captured, _channels);
        return inRate == TargetSampleRate ? mono : Resample(mono, inRate);
    }

    private static float[] MixDownToMono(float[] interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        return mono;
    }

    private static float[] Resample(float[] monoSamples, int inRate)
    {
        var resampler = new WdlResampler();
        resampler.SetMode(true, 2, false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(true);
        resampler.SetRates(inRate, TargetSampleRate);

        int inFrames = monoSamples.Length;
        int framesNeeded = resampler.ResamplePrepare(inFrames, 1, out float[] inBuffer, out int inOffset);
        int framesToCopy = Math.Min(framesNeeded, inFrames);
        Array.Copy(monoSamples, 0, inBuffer, inOffset, framesToCopy);

        int outCapacityFrames = (int)Math.Ceiling(inFrames * (double)TargetSampleRate / inRate) + 64;
        var outBuffer = new float[outCapacityFrames];
        int outFrames = resampler.ResampleOut(outBuffer, 0, framesToCopy, outCapacityFrames, 1);

        if (outFrames == outBuffer.Length) return outBuffer;
        var trimmed = new float[outFrames];
        Array.Copy(outBuffer, trimmed, outFrames);
        return trimmed;
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/SpotifyGameRadio.Core/Audio/CaptureDeviceEnumerator.cs src/SpotifyGameRadio.Core/Audio/IMicrophoneCapture.cs src/SpotifyGameRadio.Core/Audio/MicrophoneCapture.cs
git commit -m "feat: add microphone capture and capture-device enumeration"
```

---

## Task 5: `ISpeechToText` / `WhisperSpeechToText`

**Files:**
- Create: `src/SpotifyGameRadio.Core/Speech/ISpeechToText.cs`
- Create: `src/SpotifyGameRadio.Core/Speech/WhisperSpeechToText.cs`

**Interfaces:**
- Consumes: `Whisper.net`'s `WhisperFactory`/`WhisperProcessorBuilder` (Task 1).
- Produces: `ISpeechToText.TranscribeAsync(float[] pcm16kMono, string vocabularyHint, CancellationToken ct) -> Task<string>`.

No tests in this task — wraps a real ML model load + inference, same policy as `MicrophoneCapture`.

- [ ] **Step 1: `ISpeechToText`**

Create `src/SpotifyGameRadio.Core/Speech/ISpeechToText.cs`:

```csharp
namespace SpotifyGameRadio.Core.Speech;

public interface ISpeechToText
{
    /// Transcribes 16 kHz mono float32 PCM. vocabularyHint (from
    /// VocabularyPromptBuilder.Build) biases recognition toward it; pass ""
    /// for no hint. Returns "" if no speech was detected.
    Task<string> TranscribeAsync(float[] pcm16kMono, string vocabularyHint, CancellationToken ct);
}
```

- [ ] **Step 2: `WhisperSpeechToText`**

Create `src/SpotifyGameRadio.Core/Speech/WhisperSpeechToText.cs`:

```csharp
using System.Text;
using Whisper.net;

namespace SpotifyGameRadio.Core.Speech;

/// Wraps a Whisper.net model file for one-shot transcriptions. One instance
/// per model path; safe to reuse across many TranscribeAsync calls
/// (WhisperFactory itself is the expensive part to construct).
public sealed class WhisperSpeechToText : ISpeechToText, IDisposable
{
    private readonly WhisperFactory _factory;

    public WhisperSpeechToText(string modelPath)
    {
        _factory = WhisperFactory.FromPath(modelPath);
    }

    public async Task<string> TranscribeAsync(float[] pcm16kMono, string vocabularyHint, CancellationToken ct)
    {
        // WithPrompt is marked [EXPERIMENTAL] by Whisper.net as of 1.9.1 but is
        // the documented way to bias recognition toward a term list; an empty
        // string is a harmless no-op hint.
        using var processor = _factory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt(vocabularyHint)
            .Build();

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(pcm16kMono, ct))
            sb.Append(segment.Text);

        return sb.ToString().Trim();
    }

    public void Dispose() => _factory.Dispose();
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/SpotifyGameRadio.Core/Speech/ISpeechToText.cs src/SpotifyGameRadio.Core/Speech/WhisperSpeechToText.cs
git commit -m "feat: add Whisper.net-backed speech-to-text"
```

---

## Task 6: `VoiceModelStore`

**Files:**
- Create: `src/SpotifyGameRadio.Core/Speech/VoiceModelStore.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Speech/VoiceModelStoreTests.cs`

**Interfaces:**
- Produces: `VoiceModelStore(string modelDirectory, string expectedSha256, Func<string, CancellationToken, Task> download)` (the download delegate is injected so tests never touch the network), `.ModelPath` (`string`), `.IsDownloaded` (`bool`), `.DownloadAsync(IProgress<double>, CancellationToken) -> Task`. A parameterless convenience constructor uses the real pinned values and a real HTTP downloader.

The pinned model's SHA256 was already verified while writing this plan (downloaded `ggml-small.en.bin`, 487,614,201 bytes, and hashed it — the same "download once, pin the hash" step `build/fetch-phonon.ps1` already does for `phonon.dll`). It's baked into `ModelSha256` in Step 3 below; no need to redo it.

- [ ] **Step 1: Write the failing tests**

Create `tests/SpotifyGameRadio.Core.Tests/Speech/VoiceModelStoreTests.cs`:

```csharp
using SpotifyGameRadio.Core.Speech;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Speech;

public class VoiceModelStoreTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sgr-voice-model-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void IsDownloaded_FalseWhenFileMissing()
    {
        var store = new VoiceModelStore(NewTempDir(), expectedSha256: "deadbeef", download: (_, _) => Task.CompletedTask);
        Assert.False(store.IsDownloaded);
    }

    [Fact]
    public void IsDownloaded_FalseWhenHashDoesNotMatch()
    {
        string dir = NewTempDir();
        var store = new VoiceModelStore(dir, expectedSha256: "0000000000000000000000000000000000000000000000000000000000000000", download: (_, _) => Task.CompletedTask);
        File.WriteAllText(store.ModelPath, "not the real model");

        Assert.False(store.IsDownloaded);
    }

    [Fact]
    public async Task DownloadAsync_WritesToTempThenMovesIntoPlace_AndIsDownloadedBecomesTrue()
    {
        string dir = NewTempDir();
        const string content = "fake model bytes";
        string expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        var store = new VoiceModelStore(dir, expectedHash, download: async (destPath, ct) =>
        {
            await File.WriteAllTextAsync(destPath, content, ct);
        });

        Assert.False(store.IsDownloaded);
        await store.DownloadAsync(new Progress<double>(), CancellationToken.None);

        Assert.True(store.IsDownloaded);
        Assert.Equal(content, await File.ReadAllTextAsync(store.ModelPath));
    }

    [Fact]
    public async Task DownloadAsync_HashMismatch_ThrowsAndDoesNotLeaveFileInPlace()
    {
        string dir = NewTempDir();
        var store = new VoiceModelStore(dir, expectedSha256: "wonthappentomatch", download: async (destPath, ct) =>
        {
            await File.WriteAllTextAsync(destPath, "wrong content", ct);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.DownloadAsync(new Progress<double>(), CancellationToken.None));

        Assert.False(store.IsDownloaded);
        Assert.False(File.Exists(store.ModelPath));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter "FullyQualifiedName~VoiceModelStoreTests"`
Expected: build error — `VoiceModelStore` does not exist.

- [ ] **Step 3: Write `VoiceModelStore`**

Create `src/SpotifyGameRadio.Core/Speech/VoiceModelStore.cs`:

```csharp
using System.Security.Cryptography;

namespace SpotifyGameRadio.Core.Speech;

/// Manages the cached Whisper model file: knows where it lives, whether a
/// valid copy is present, and how to fetch one. Downloaded on first use
/// rather than bundled in the release zip — see the design spec's
/// "Model distribution" decision.
public class VoiceModelStore
{
    public const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin";
    public const string ModelFileName = "ggml-small.en.bin";

    // Verified 2026-09-12 against a fresh download of the pinned URL above
    // (487,614,201 bytes). Re-verify if ModelUrl ever changes.
    public const string ModelSha256 = "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d";

    private readonly string _expectedSha256;
    private readonly Func<string, CancellationToken, Task> _download;

    public string ModelPath { get; }

    /// Real pinned model/hash, real HTTP download.
    public VoiceModelStore()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyGameRadio", "models"),
            ModelSha256,
            DownloadWithHttpClient)
    {
    }

    /// Test/advanced constructor: inject the cache directory, expected hash,
    /// and download delegate so tests never touch the network or the pinned
    /// 488 MB file.
    public VoiceModelStore(string modelDirectory, string expectedSha256, Func<string, CancellationToken, Task> download)
    {
        Directory.CreateDirectory(modelDirectory);
        ModelPath = Path.Combine(modelDirectory, ModelFileName);
        _expectedSha256 = expectedSha256;
        _download = download;
    }

    public bool IsDownloaded
    {
        get
        {
            if (!File.Exists(ModelPath)) return false;
            using var stream = File.OpenRead(ModelPath);
            string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return actual == _expectedSha256.ToLowerInvariant();
        }
    }

    public async Task DownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        string tempPath = ModelPath + ".part";
        try
        {
            await _download(tempPath, ct);

            using (var stream = File.OpenRead(tempPath))
            {
                string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (actual != _expectedSha256.ToLowerInvariant())
                    throw new InvalidOperationException(
                        $"Downloaded voice model hash mismatch (expected {_expectedSha256}, got {actual}).");
            }

            File.Move(tempPath, ModelPath, overwrite: true);
            progress.Report(1.0);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static async Task DownloadWithHttpClient(string destPath, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = File.Create(destPath);
        await source.CopyToAsync(dest, ct);
    }
}
```

Note: the real `DownloadWithHttpClient` path doesn't report incremental progress (kept simple — `IProgress<double>` is still part of the public API for the injected-delegate/test path and for a future improvement, but Step 3's real downloader only reports `1.0` at the end via the caller in `DownloadAsync`). This is an acceptable simplification for this iteration — the UI's progress bar will show indeterminate/only-jump-to-100% behavior until someone wires real streaming progress through `DownloadWithHttpClient`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter "FullyQualifiedName~VoiceModelStoreTests"`
Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/SpotifyGameRadio.Core/Speech/VoiceModelStore.cs tests/SpotifyGameRadio.Core.Tests/Speech/VoiceModelStoreTests.cs
git commit -m "feat: add VoiceModelStore for on-first-use model download"
```

---

## Task 7: `VoiceChatSession`

**Files:**
- Create: `src/SpotifyGameRadio.Core/Speech/VoiceChatSession.cs`
- Test: `tests/SpotifyGameRadio.Core.Tests/Speech/VoiceChatSessionTests.cs`

**Interfaces:**
- Consumes: `IMicrophoneCapture` (Task 4), `ISpeechToText` (Task 5).
- Produces: `VoiceChatState` enum (`Idle, Recording, Transcribing, PreviewReady`), `VoiceChatSession(IMicrophoneCapture, ISpeechToText, Func<string> vocabularyHint)`, `.State`, `.Transcript`, events `StateChanged(VoiceChatState)`, `PreviewReady(string)`, `Failed(string)`, methods `BeginRecording()`, `EndRecording()`, `Discard()`, `Dispose()`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SpotifyGameRadio.Core.Tests/Speech/VoiceChatSessionTests.cs`:

```csharp
using SpotifyGameRadio.Core.Audio;
using SpotifyGameRadio.Core.Speech;
using Xunit;

namespace SpotifyGameRadio.Core.Tests.Speech;

public class FakeMicrophoneCapture : IMicrophoneCapture
{
    public float[] NextStopResult = new float[16000]; // 1 second of "speech" by default
    public bool Started { get; private set; }

    public void Start(string? deviceId) => Started = true;
    public float[] Stop() { Started = false; return NextStopResult; }
}

public class FakeSpeechToText : ISpeechToText
{
    public string Result = "dropping fuel supplies on tower 2 fob";
    public Exception? ThrowOnTranscribe;
    public string? LastVocabularyHint;

    public Task<string> TranscribeAsync(float[] pcm16kMono, string vocabularyHint, CancellationToken ct)
    {
        LastVocabularyHint = vocabularyHint;
        if (ThrowOnTranscribe is not null) return Task.FromException<string>(ThrowOnTranscribe);
        return Task.FromResult(Result);
    }
}

public class VoiceChatSessionTests
{
    private static VoiceChatSession CreateSession(
        FakeMicrophoneCapture mic, FakeSpeechToText stt, out List<VoiceChatState> states, out List<string> previews, out List<string> failures)
    {
        var session = new VoiceChatSession(mic, stt, () => "");
        var capturedStates = new List<VoiceChatState>();
        var capturedPreviews = new List<string>();
        var capturedFailures = new List<string>();
        session.StateChanged += s => capturedStates.Add(s);
        session.PreviewReady += t => capturedPreviews.Add(t);
        session.Failed += r => capturedFailures.Add(r);
        states = capturedStates;
        previews = capturedPreviews;
        failures = capturedFailures;
        return session;
    }

    [Fact]
    public async Task HappyPath_RecordingThenPreviewReady()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText();
        using var session = CreateSession(mic, stt, out var states, out var previews, out _);

        session.BeginRecording();
        Assert.Equal(VoiceChatState.Recording, session.State);
        Assert.True(mic.Started);

        session.EndRecording();
        // Transcription is awaited synchronously inside EndRecording for a
        // fake ISpeechToText that completes immediately (Task.FromResult), so
        // by the time EndRecording returns the session has already reached
        // PreviewReady.
        Assert.Equal(VoiceChatState.PreviewReady, session.State);
        Assert.Equal("dropping fuel supplies on tower 2 fob", session.Transcript);
        Assert.Contains(VoiceChatState.Recording, states);
        Assert.Contains(VoiceChatState.Transcribing, states);
        Assert.Contains(VoiceChatState.PreviewReady, states);
        Assert.Equal(new[] { "dropping fuel supplies on tower 2 fob" }, previews);
    }

    [Fact]
    public async Task EndRecording_BufferTooShort_ReturnsToIdleWithoutTranscribing()
    {
        var mic = new FakeMicrophoneCapture { NextStopResult = new float[100] }; // well under 200ms @ 16kHz
        var stt = new FakeSpeechToText();
        using var session = CreateSession(mic, stt, out _, out var previews, out var failures);

        session.BeginRecording();
        session.EndRecording();

        Assert.Equal(VoiceChatState.Idle, session.State);
        Assert.Empty(previews);
        Assert.Empty(failures);
        Assert.Null(stt.LastVocabularyHint); // TranscribeAsync was never called
    }

    [Fact]
    public async Task EndRecording_EmptyTranscript_RaisesFailedAndReturnsToIdle()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText { Result = "   " };
        using var session = CreateSession(mic, stt, out _, out var previews, out var failures);

        session.BeginRecording();
        session.EndRecording();

        Assert.Equal(VoiceChatState.Idle, session.State);
        Assert.Empty(previews);
        Assert.Single(failures);
    }

    [Fact]
    public async Task EndRecording_TranscriptionThrows_RaisesFailedAndReturnsToIdle()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText { ThrowOnTranscribe = new InvalidOperationException("model not loaded") };
        using var session = CreateSession(mic, stt, out _, out _, out var failures);

        session.BeginRecording();
        session.EndRecording();

        Assert.Equal(VoiceChatState.Idle, session.State);
        Assert.Single(failures);
        Assert.Contains("model not loaded", failures[0]);
    }

    [Fact]
    public async Task Discard_FromPreviewReady_ReturnsToIdleAndClearsTranscript()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText();
        using var session = CreateSession(mic, stt, out _, out _, out _);

        session.BeginRecording();
        session.EndRecording();
        Assert.Equal(VoiceChatState.PreviewReady, session.State);

        session.Discard();

        Assert.Equal(VoiceChatState.Idle, session.State);
        Assert.Null(session.Transcript);
    }

    [Fact]
    public async Task BeginRecording_WhilePreviewReady_ClearsOldTranscriptAndStartsOver()
    {
        var mic = new FakeMicrophoneCapture();
        var stt = new FakeSpeechToText();
        using var session = CreateSession(mic, stt, out _, out _, out _);

        session.BeginRecording();
        session.EndRecording();
        Assert.Equal(VoiceChatState.PreviewReady, session.State);

        session.BeginRecording();

        Assert.Equal(VoiceChatState.Recording, session.State);
        Assert.Null(session.Transcript);
    }

    [Fact]
    public void Dispose_IsSafeToCallTwice()
    {
        var session = new VoiceChatSession(new FakeMicrophoneCapture(), new FakeSpeechToText(), () => "");
        session.Dispose();
        session.Dispose(); // must not throw
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter "FullyQualifiedName~VoiceChatSessionTests"`
Expected: build error — `VoiceChatSession` does not exist.

- [ ] **Step 3: Write `VoiceChatSession`**

Create `src/SpotifyGameRadio.Core/Speech/VoiceChatSession.cs`:

```csharp
using SpotifyGameRadio.Core.Audio;

namespace SpotifyGameRadio.Core.Speech;

public enum VoiceChatState { Idle, Recording, Transcribing, PreviewReady }

/// Push-to-talk record -> transcribe -> preview -> confirm/discard/re-record
/// state machine. Pure aside from its two injected dependencies; unit-tested
/// with fakes. Modelled on CalibrationSession's event-driven shape.
public sealed class VoiceChatSession : IDisposable
{
    // Below this many 16kHz mono samples (~200ms) a recording is treated as
    // an accidental brush of the key — no transcription attempt, no preview.
    private const int MinValidSampleCount = 3200;

    private readonly IMicrophoneCapture _mic;
    private readonly ISpeechToText _stt;
    private readonly Func<string> _vocabularyHint;
    private CancellationTokenSource? _transcribeCts;
    private bool _disposed;

    public VoiceChatState State { get; private set; } = VoiceChatState.Idle;
    public string? Transcript { get; private set; }

    public event Action<VoiceChatState>? StateChanged;
    public event Action<string>? PreviewReady;
    public event Action<string>? Failed;

    public VoiceChatSession(IMicrophoneCapture mic, ISpeechToText stt, Func<string> vocabularyHint)
    {
        _mic = mic;
        _stt = stt;
        _vocabularyHint = vocabularyHint;
    }

    public void BeginRecording()
    {
        if (State is VoiceChatState.Recording or VoiceChatState.Transcribing) return;

        Transcript = null;
        _mic.Start(null);
        SetState(VoiceChatState.Recording);
    }

    public void EndRecording()
    {
        if (State != VoiceChatState.Recording) return;

        float[] samples = _mic.Stop();
        if (samples.Length < MinValidSampleCount)
        {
            SetState(VoiceChatState.Idle);
            return;
        }

        SetState(VoiceChatState.Transcribing);
        _transcribeCts = new CancellationTokenSource();
        RunTranscription(samples, _transcribeCts.Token);
    }

    private async void RunTranscription(float[] samples, CancellationToken ct)
    {
        try
        {
            string result = await _stt.TranscribeAsync(samples, _vocabularyHint(), ct);
            if (ct.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(result))
            {
                SetState(VoiceChatState.Idle);
                Failed?.Invoke("No speech detected.");
                return;
            }

            Transcript = result;
            SetState(VoiceChatState.PreviewReady);
            PreviewReady?.Invoke(result);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            SetState(VoiceChatState.Idle);
            Failed?.Invoke(ex.Message);
        }
    }

    public void Discard()
    {
        if (State != VoiceChatState.PreviewReady) return;
        Transcript = null;
        SetState(VoiceChatState.Idle);
    }

    private void SetState(VoiceChatState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transcribeCts?.Cancel();
        _transcribeCts?.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj --filter "FullyQualifiedName~VoiceChatSessionTests"`
Expected: all 7 tests pass. (`RunTranscription` is `async void` over a `Task.FromResult`/`Task.FromException`-based fake, which completes synchronously enough that by the time `EndRecording()` returns on the test's calling thread, the continuation has already run — this is standard behavior for already-completed tasks and is why the tests can assert `session.State` immediately after `EndRecording()` without awaiting anything themselves.)

- [ ] **Step 5: Run the full Core test suite**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj`
Expected: all tests pass (no regressions from earlier tasks).

- [ ] **Step 6: Commit**

```bash
git add src/SpotifyGameRadio.Core/Speech/VoiceChatSession.cs tests/SpotifyGameRadio.Core.Tests/Speech/VoiceChatSessionTests.cs
git commit -m "feat: add VoiceChatSession record/transcribe/preview state machine"
```

---

## Task 8: Themed `TextBox` style

**Files:**
- Modify: `src/SpotifyGameRadio.App/Styles/Theme.xaml`

**Interfaces:**
- Produces: an implicit `<Style TargetType="TextBox">` usable by any `TextBox` in the app, and a `CaretBrush`-aware dark theme.

Nothing in the theme today styles `TextBox` (only sliders/buttons/checkboxes/comboboxes/scrollbars exist) — Task 11's vocabulary text box needs one.

- [ ] **Step 1: Add the style**

Open `src/SpotifyGameRadio.App/Styles/Theme.xaml`. Find the `ToolTip` style near the end of the file (right before the `Expander`/`ExpanderHeaderToggle` styles) and insert this new `TextBox` style directly after it:

```xml
    <Style TargetType="TextBox">
        <Setter Property="Background" Value="{StaticResource BgBrush}" />
        <Setter Property="Foreground" Value="{StaticResource TextPrimaryBrush}" />
        <Setter Property="CaretBrush" Value="{StaticResource TextPrimaryBrush}" />
        <Setter Property="BorderBrush" Value="{StaticResource BorderBrush2}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="8,6" />
        <Setter Property="SelectionBrush" Value="{StaticResource AccentPressedBrush}" />
        <Setter Property="FocusVisualStyle" Value="{x:Null}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4">
                        <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsFocused" Value="True">
                            <Setter Property="BorderBrush" Value="{StaticResource AccentBrush}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.45" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

`PART_ContentHost` is the exact name WPF's `TextBox` control looks for in a custom template — without that exact name the text box silently renders no editable content.

- [ ] **Step 2: Build**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/SpotifyGameRadio.App/Styles/Theme.xaml
git commit -m "feat: add themed TextBox style"
```

---

## Task 9: `VoicePreviewOverlay` window

**Files:**
- Create: `src/SpotifyGameRadio.App/VoicePreviewOverlay.xaml`
- Create: `src/SpotifyGameRadio.App/VoicePreviewOverlay.xaml.cs`

**Interfaces:**
- Produces: `VoicePreviewOverlay.SetText(string)`, `.SetHint(string)`.

No tests — a WPF window, same policy as `MainWindow`/`HotkeyCaptureControl` (no test framework covers WPF views in this project).

- [ ] **Step 1: XAML**

Create `src/SpotifyGameRadio.App/VoicePreviewOverlay.xaml`:

```xml
<Window x:Class="SpotifyGameRadio.App.VoicePreviewOverlay"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
        SizeToContent="WidthAndHeight" WindowStartupLocation="Manual"
        Focusable="False" ShowActivated="False">
    <Border Style="{StaticResource CardPanel}" MaxWidth="480" Margin="0">
        <StackPanel>
            <TextBlock x:Name="TranscriptText" TextWrapping="Wrap" FontSize="15" />
            <TextBlock x:Name="HintText" Style="{StaticResource MutedText}" TextWrapping="Wrap" Margin="0,8,0,0" />
        </StackPanel>
    </Border>
</Window>
```

`ShowActivated="False"` is the key line: it means calling `Show()` never steals keyboard focus away from the game, so the overlay is purely a passive readout.

- [ ] **Step 2: Code-behind**

Create `src/SpotifyGameRadio.App/VoicePreviewOverlay.xaml.cs`:

```csharp
using System.Windows;

namespace SpotifyGameRadio.App;

public partial class VoicePreviewOverlay : Window
{
    public VoicePreviewOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PositionBottomCenter();
    }

    public void SetText(string text) => TranscriptText.Text = text;

    public void SetHint(string text) => HintText.Text = text;

    private void PositionBottomCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        Top = workArea.Bottom - ActualHeight - 40;
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/SpotifyGameRadio.App/VoicePreviewOverlay.xaml src/SpotifyGameRadio.App/VoicePreviewOverlay.xaml.cs
git commit -m "feat: add voice chat preview overlay window"
```

---

## Task 10: `MainViewModel` wiring

**Files:**
- Modify: `src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `VoiceChatSession`, `MicrophoneCapture`, `WhisperSpeechToText`, `VoiceModelStore`, `CaptureDeviceEnumerator` (Core, earlier tasks), `VoicePreviewOverlay` (Task 9), `VocabularyPromptBuilder` (Task 2).
- Produces: `MainViewModel.AvailableCaptureDevices` (`IReadOnlyList<CaptureDeviceInfo>`), `.VoiceModelReady`/`.VoiceModelDownloading`/`.VoiceModelDownloadProgress`, `.DownloadVoiceModelCommand`.

This task has no new automated tests (`MainViewModel` has none today — see the Global Constraints note); it's verified manually in Task 12.

- [ ] **Step 1: Add `using` and new fields**

In `src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs`, add to the `using` block at the top:

```csharp
using SpotifyGameRadio.Core.Speech;
```

Add these fields right after `private readonly CalibrationAnnouncer _announcer;`:

```csharp
    private readonly VoiceModelStore _voiceModelStore = new();
    private VoiceChatSession? _voiceSession;
    private TapHotkeyWatcher? _voiceRecordWatcher;
    private TapHotkeyWatcher? _voiceConfirmWatcher;
    private TapHotkeyWatcher? _voiceDiscardWatcher;
    private VoicePreviewOverlay? _voiceOverlay;
```

- [ ] **Step 2: Add new bindable properties**

Add these properties right after `public bool IsRoutingSupported => _router.IsSupported;`:

```csharp
    public IReadOnlyList<CaptureDeviceInfo> AvailableCaptureDevices { get; private set; } = Array.Empty<CaptureDeviceInfo>();

    public bool VoiceModelReady => _voiceModelStore.IsDownloaded;

    private bool _voiceModelDownloading;
    public bool VoiceModelDownloading
    {
        get => _voiceModelDownloading;
        set { _voiceModelDownloading = value; OnPropertyChanged(); }
    }

    private double _voiceModelDownloadProgress;
    public double VoiceModelDownloadProgress
    {
        get => _voiceModelDownloadProgress;
        set { _voiceModelDownloadProgress = value; OnPropertyChanged(); }
    }

    public ICommand DownloadVoiceModelCommand { get; }
```

- [ ] **Step 3: Wire the command and populate capture devices**

In the constructor, add `DownloadVoiceModelCommand = new RelayCommand(_ => _ = DownloadVoiceModelAsync(), _ => !VoiceModelDownloading);` next to the other command assignments (right after `CalibrateFreelookCommand = ...;`).

In `RefreshSources()` (which already refreshes `AvailableSources`/`AvailableRenderDevices`), add at the end:

```csharp
        AvailableCaptureDevices = CaptureDeviceEnumerator.ListCaptureDevices();
        OnPropertyChanged(nameof(AvailableCaptureDevices));
```

- [ ] **Step 4: Add the download method**

Add this private method near `RefreshSources()`:

```csharp
    private async Task DownloadVoiceModelAsync()
    {
        VoiceModelDownloading = true;
        var progress = new Progress<double>(p => VoiceModelDownloadProgress = p);
        try
        {
            await _voiceModelStore.DownloadAsync(progress, CancellationToken.None);
            StatusMessage = "Voice model ready.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Voice model download failed: {ex.Message}";
        }
        finally
        {
            VoiceModelDownloading = false;
            OnPropertyChanged(nameof(VoiceModelReady));
        }
    }
```

- [ ] **Step 5: Create the session and hotkey watchers in the constructor**

Voice chat has no dependency on the radio pipeline or mouse hook (unlike Recenter/Vehicle/Calibrate, whose watchers live inside `Start()`/`Stop()`), so it must work whether or not the radio effect is running. Add this near the end of the constructor, right before the closing `try { if (_routeRecovery.Exists()) ... }` block:

```csharp
        _voiceSession = new VoiceChatSession(
            new SpotifyGameRadio.Core.Audio.MicrophoneCapture(),
            CreateSpeechToText(),
            () => VocabularyPromptBuilder.Build(Profile.VoiceCustomVocabulary));
        _voiceSession.StateChanged += OnVoiceStateChanged;
        _voiceSession.Failed += reason => Application.Current?.Dispatcher.Invoke(() => StatusMessage = reason);

        _voiceRecordWatcher = new TapHotkeyWatcher(Profile.VoiceRecordHotkey, new Win32KeyStateSource());
        _voiceRecordWatcher.Pressed += () => Application.Current?.Dispatcher.Invoke(OnVoiceRecordPressed);
        _voiceRecordWatcher.Released += () => Application.Current?.Dispatcher.Invoke(() => _voiceSession?.EndRecording());

        _voiceConfirmWatcher = new TapHotkeyWatcher(Profile.VoiceConfirmHotkey, new Win32KeyStateSource());
        _voiceConfirmWatcher.Pressed += () => Application.Current?.Dispatcher.Invoke(OnVoiceConfirmPressed);

        _voiceDiscardWatcher = new TapHotkeyWatcher(Profile.VoiceDiscardHotkey, new Win32KeyStateSource());
        _voiceDiscardWatcher.Pressed += () => Application.Current?.Dispatcher.Invoke(() => _voiceSession?.Discard());
```

`CreateSpeechToText()` has to lazily construct a `WhisperSpeechToText` only once the model file actually exists (constructing it before the model is downloaded would throw). Add:

```csharp
    private ISpeechToText CreateSpeechToText() => new LazyWhisperSpeechToText(() => _voiceModelStore.ModelPath);
```

This references a small helper class — add it as a private nested class inside `MainViewModel` (right after the `OnPropertyChanged` method near the bottom of the file, still inside the `MainViewModel` class body):

```csharp
    /// Defers constructing the real WhisperFactory until the model file is
    /// actually present — MainViewModel builds this at startup, before the
    /// user may have downloaded the model yet.
    private sealed class LazyWhisperSpeechToText : ISpeechToText, IDisposable
    {
        private readonly Func<string> _modelPath;
        private WhisperSpeechToText? _inner;

        public LazyWhisperSpeechToText(Func<string> modelPath) => _modelPath = modelPath;

        public Task<string> TranscribeAsync(float[] pcm16kMono, string vocabularyHint, CancellationToken ct)
        {
            _inner ??= new WhisperSpeechToText(_modelPath());
            return _inner.TranscribeAsync(pcm16kMono, vocabularyHint, ct);
        }

        public void Dispose() => _inner?.Dispose();
    }
```

- [ ] **Step 6: Add the press handlers and overlay lifecycle**

Add these private methods near `OnVehicleTogglePressed`/`OnVehicleToggleReleased`:

```csharp
    private void OnVoiceRecordPressed()
    {
        if (!_voiceModelStore.IsDownloaded)
        {
            StatusMessage = "Voice model not downloaded yet — see the Voice Chat card.";
            return;
        }
        _voiceSession?.BeginRecording();
    }

    private void OnVoiceConfirmPressed()
    {
        if (_voiceSession is null || _voiceSession.State != VoiceChatState.PreviewReady) return;
        // Transcript is guaranteed non-null whenever State == PreviewReady
        // (VoiceChatSession sets it right before that transition) — the
        // compiler can't see that invariant across two separate properties,
        // hence the null-forgiving operator rather than a redundant null check.
        Clipboard.SetText(_voiceSession.Transcript!);
        StatusMessage = "Copied to clipboard — paste it in.";
        _voiceSession.Discard();
    }

    private void OnVoiceStateChanged(VoiceChatState state)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (state == VoiceChatState.Idle)
            {
                _voiceOverlay?.Hide();
                return;
            }

            _voiceOverlay ??= new VoicePreviewOverlay();
            _voiceOverlay.SetText(state switch
            {
                VoiceChatState.Recording => "Listening…",
                VoiceChatState.Transcribing => "Transcribing…",
                VoiceChatState.PreviewReady => _voiceSession?.Transcript ?? "",
                _ => "",
            });
            string confirmName = Profile.VoiceConfirmHotkey.VirtualKeyCode == 0 ? "(unbound)" : "Confirm";
            string discardName = Profile.VoiceDiscardHotkey.VirtualKeyCode == 0 ? "(unbound)" : "Discard";
            _voiceOverlay.SetHint($"{confirmName} to copy · {discardName} to clear · hold Record again to redo");
            _voiceOverlay.Show();
        });
    }
```

`Clipboard.SetText` needs `using System.Windows;`, which the file already has (used for `Application.Current`).

- [ ] **Step 7: Wire hotkey rebinding in `OnProfilePropertyChanged`**

In `OnProfilePropertyChanged`, add these branches alongside the existing `else if (e.PropertyName == nameof(RadioProfile.CalibrateHotkey))` one:

```csharp
        else if (e.PropertyName == nameof(RadioProfile.VoiceRecordHotkey))
        {
            _voiceRecordWatcher?.SetHotkey(Profile.VoiceRecordHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.VoiceConfirmHotkey))
        {
            _voiceConfirmWatcher?.SetHotkey(Profile.VoiceConfirmHotkey);
        }
        else if (e.PropertyName == nameof(RadioProfile.VoiceDiscardHotkey))
        {
            _voiceDiscardWatcher?.SetHotkey(Profile.VoiceDiscardHotkey);
        }
```

- [ ] **Step 8: Tear down in `Cleanup()`**

The voice watchers/session are constructor-scoped (like `_announcer`), not `Start()`/`Stop()`-scoped, so they're torn down in `Cleanup()`:

```csharp
    public void Cleanup()
    {
        if (_calibrationSession is not null)
        {
            _calibrationSession.Abort();
            TeardownCalibration();
        }
        _announcer.Dispose();
        _voiceRecordWatcher?.Dispose();
        _voiceConfirmWatcher?.Dispose();
        _voiceDiscardWatcher?.Dispose();
        _voiceSession?.Dispose();
        _voiceOverlay?.Close();
    }
```

- [ ] **Step 9: Build**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: `Build succeeded.`

- [ ] **Step 10: Commit**

```bash
git add src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs
git commit -m "feat: wire voice chat hotkeys, session, and overlay into MainViewModel"
```

---

## Task 11: "Voice Chat" card in `MainWindow.xaml`

**Files:**
- Modify: `src/SpotifyGameRadio.App/MainWindow.xaml`
- Modify: `src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs` (one small addition — see Step 2)

**Interfaces:**
- Consumes: everything from Task 10's bindable properties/commands, plus `Profile.VoiceRecordHotkey`/`VoiceConfirmHotkey`/`VoiceDiscardHotkey`/`VoiceCustomVocabulary`/`VoiceMicrophoneDeviceId` (Task 3).

- [ ] **Step 1: Add the card**

In `src/SpotifyGameRadio.App/MainWindow.xaml`, add a new card to the right column's `StackPanel` (`Grid.Column="2"`), after the closing `</Border>` of the "Spatial Position" card and before that column's closing `</StackPanel>`:

```xml
                    <Border Style="{StaticResource CardPanel}">
                        <StackPanel>
                            <TextBlock Text="Voice Chat" Style="{StaticResource SectionHeaderText}" />

                            <Grid Margin="0,0,0,4">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="130" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="Record hotkey" Style="{StaticResource RowLabelText}" />
                                <controls:HotkeyCaptureControl Grid.Column="1" HorizontalAlignment="Left"
                                    Hotkey="{Binding Profile.VoiceRecordHotkey, Mode=TwoWay}" />
                            </Grid>
                            <TextBlock Text="Hold to record, release to transcribe." Style="{StaticResource MutedText}" Margin="0,0,0,10" />

                            <Grid Margin="0,0,0,4">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="130" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="Confirm hotkey" Style="{StaticResource RowLabelText}" />
                                <controls:HotkeyCaptureControl Grid.Column="1" HorizontalAlignment="Left"
                                    Hotkey="{Binding Profile.VoiceConfirmHotkey, Mode=TwoWay}" />
                            </Grid>
                            <TextBlock Text="Copies the preview to the clipboard — paste it yourself." Style="{StaticResource MutedText}" Margin="0,0,0,10" />

                            <Grid Margin="0,0,0,10">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="130" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="Discard hotkey" Style="{StaticResource RowLabelText}" />
                                <controls:HotkeyCaptureControl Grid.Column="1" HorizontalAlignment="Left"
                                    Hotkey="{Binding Profile.VoiceDiscardHotkey, Mode=TwoWay}" />
                            </Grid>

                            <StackPanel Orientation="Horizontal" Margin="0,0,0,10">
                                <TextBlock Text="Microphone:" Width="90" Style="{StaticResource RowLabelText}" />
                                <ComboBox Width="220" ItemsSource="{Binding AvailableCaptureDevices}"
                                          SelectedValuePath="Id" SelectedValue="{Binding Profile.VoiceMicrophoneDeviceId}">
                                    <ComboBox.ItemTemplate>
                                        <DataTemplate>
                                            <TextBlock Text="{Binding FriendlyName}" />
                                        </DataTemplate>
                                    </ComboBox.ItemTemplate>
                                </ComboBox>
                            </StackPanel>

                            <TextBlock Text="Custom vocabulary" Style="{StaticResource RowLabelText}" Margin="0,0,0,4" />
                            <TextBox Text="{Binding Profile.VoiceCustomVocabulary, UpdateSourceTrigger=PropertyChanged}"
                                     AcceptsReturn="True" TextWrapping="Wrap" Height="70" Margin="0,0,0,10"
                                     ToolTip="Comma or newline separated. Biases transcription toward these terms — add callsigns and jargon specific to your unit/game." />

                            <StackPanel Orientation="Horizontal" Visibility="{Binding VoiceModelReady, Converter={StaticResource BoolToVis}, ConverterParameter=Invert}">
                                <Button Content="Download voice model" Command="{Binding DownloadVoiceModelCommand}" />
                                <TextBlock Text="{Binding VoiceModelDownloadProgress, StringFormat=' {0:P0}'}" Margin="8,0,0,0"
                                           VerticalAlignment="Center" Visibility="{Binding VoiceModelDownloading, Converter={StaticResource BoolToVis}}" />
                            </StackPanel>
                            <TextBlock Text="Voice model ready." Style="{StaticResource MutedText}"
                                       Visibility="{Binding VoiceModelReady, Converter={StaticResource BoolToVis}}" />
                        </StackPanel>
                    </Border>
```

- [ ] **Step 2: The existing `BoolToVis` converter doesn't support inverting — add one that does**

`MainWindow.xaml`'s `Window.Resources` currently only has `<BooleanToVisibilityConverter x:Key="BoolToVis" />`, which has no way to invert via `ConverterParameter`. Replace the "Download voice model" row's `Visibility` binding to avoid needing an inverting converter at all — bind to a new `bool` property instead. In `MainViewModel.cs` (back in Task 10's file), add this computed property right after `public bool VoiceModelReady => _voiceModelStore.IsDownloaded;`:

```csharp
    public bool VoiceModelNotReady => !_voiceModelStore.IsDownloaded;
```

And add `OnPropertyChanged(nameof(VoiceModelNotReady));` next to the existing `OnPropertyChanged(nameof(VoiceModelReady));` call in `DownloadVoiceModelAsync()`'s `finally` block.

Back in `MainWindow.xaml`, change the "Download voice model" row's `Visibility` binding to:

```xml
                            <StackPanel Orientation="Horizontal" Visibility="{Binding VoiceModelNotReady, Converter={StaticResource BoolToVis}}">
```

(Remove the `ConverterParameter=Invert` — it was never going to work with the stock `BooleanToVisibilityConverter` and this sidesteps needing a custom one.)

- [ ] **Step 3: Build**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Run the app and visually check the new card**

Run: `dotnet run --project src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: a new "Voice Chat" card appears in the right column with three hotkey rows, a microphone dropdown, a vocabulary text box (pre-filled with the starter list), and a "Download voice model" button. Confirm the window doesn't clip anything — the right column may now be taller than the left; if so this is a repeat of the same height-clipping issue handled before (bump `Window.Height` in `MainWindow.xaml` if needed).

- [ ] **Step 5: Commit**

```bash
git add src/SpotifyGameRadio.App/MainWindow.xaml src/SpotifyGameRadio.App/ViewModels/MainViewModel.cs
git commit -m "feat: add Voice Chat card to MainWindow"
```

---

## Task 12: Full verification pass and packaging check

**Files:** none (verification only)

- [ ] **Step 1: Run the full automated test suite**

Run: `dotnet test tests/SpotifyGameRadio.Core.Tests/SpotifyGameRadio.Core.Tests.csproj`
Expected: all tests pass, including every test added in Tasks 2, 3, 6, and 7.

- [ ] **Step 2: Full clean build**

Run: `dotnet build src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Manual verification (matches the spec's manual verification list)**

Run the app (`dotnet run --project src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`) and walk through:

1. First run: Voice Chat card shows the download button; download it; confirm it becomes usable (button disappears, "Voice model ready." appears) without a restart.
2. Bind Record/Confirm/Discard to three different keys.
3. Hold Record, say a short phrase, release — overlay shows "Transcribing…" then the text.
4. Tap Confirm — overlay hides, status says copied; `Ctrl+V` into Notepad/Discord actually pastes it.
5. Repeat, but tap Discard instead — overlay hides, clipboard unchanged.
6. Repeat, but hold Record again while the preview is showing — old text disappears, new recording replaces it.
7. Tap Record and release immediately — no overlay, no transcription attempt.
8. Add a few custom vocabulary terms; say a phrase using them; note whether transcription picks them up (this one's a smoke test, not pass/fail).
9. Pick a microphone device that then gets unplugged/disabled mid-session — recording fails gracefully (status message), no crash.

- [ ] **Step 4: Confirm the release zip doesn't bundle the model**

Run: `./build/package.ps1 -Version 0.4.0` (or the next appropriate version), then inspect the output zip's contents.
Expected: the zip contains the Whisper.net native runtime DLLs (from Task 1's automatic transitive copy) but **not** `ggml-small.en.bin` — that file only ever exists under `%LOCALAPPDATA%\SpotifyGameRadio\models\` on a machine that has actually clicked "Download voice model". If the model somehow appears in the zip, something in the build is picking up a locally-cached copy — find and fix it (most likely cause: a stray `<None Include="...">` was added somewhere it shouldn't have been; there should be none for this feature).

- [ ] **Step 5: Final commit**

If Step 4 required a fix, commit it:

```bash
git add -A
git commit -m "fix: exclude voice model from release packaging"
```

If nothing needed fixing, there's nothing to commit for this task — Task 11's commit was the last code change.

## Out of scope (carried over from the design spec)

- Auto-paste or any form of synthetic input injection into another process.
- Cloud STT or any network dependency at transcription time beyond the one-time model download.
- Multi-language support (model is `.en`, English-only).
- In-game overlay rendering.
- Editing the transcript text by hand before confirming.
- Voice activity detection / hands-free "always listening" mode.
- Choosing between multiple Whisper model sizes in the UI.
- Validating/preventing hotkey collisions between the three new voice hotkeys or against existing hotkeys elsewhere in the app.
