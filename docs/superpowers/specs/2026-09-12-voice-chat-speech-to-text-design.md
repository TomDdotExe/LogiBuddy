# Voice chat speech-to-text — design

Date: 2026-09-12
Status: Approved (brainstorming)

## Problem

Typing text chat while flying/driving in a milsim game is impractical —
both hands are on flight/vehicle controls. There's no way to send a short
tactical message (map callsigns, resupply requests, etc.) without letting
go of controls to type.

## Solution

A push-to-talk voice capture flow: hold a hotkey to record from the
microphone, release to transcribe locally (offline, no cloud dependency),
preview the transcript in an on-screen overlay, then either confirm
(copies to clipboard for the user to paste manually — see the anti-cheat
note below), discard, or re-record by holding the record hotkey again.

### Decisions locked in brainstorming

| Question | Decision |
| --- | --- |
| STT engine | Local/offline (Whisper via Whisper.net), not a cloud API — no internet dependency mid-game, no per-use cost, voice never leaves the machine |
| Text delivery | Clipboard-only, manual paste — **not** auto-paste via `SendInput`. See "Anti-cheat note" below |
| Hotkey scheme | Three separate hotkeys: hold-to-record, tap-to-confirm, tap-to-discard. Re-record = hold record again while a preview is showing |
| Vocabulary tuning | Bundle a mid-size model (`small.en`) + a user-editable custom vocabulary list, seeded with common milsim terms, applied as a Whisper "initial prompt" |
| Model distribution | Downloaded on first use, not bundled in the release zip — cached under `%LOCALAPPDATA%\SpotifyGameRadio\models\`, verified by pinned SHA256 (mirrors `fetch-phonon.ps1`'s pattern, just at runtime) |

### Anti-cheat note

Auto-pasting via `SendInput` was the original design but was dropped after
checking: `SendInput` sets the "injected" flag on synthetic input, which
EasyAntiCheat (used by e.g. Squad) is known to actively watch for as a
generic marker of third-party input injection, regardless of what the
injected input actually does. Clipboard-only sidesteps this entirely: the
Ctrl+V keypress that actually lands the text is the user's own hardware
input, indistinguishable from pasting anything else, so there is nothing
for anti-cheat to flag. **This app must never call
`SendInput`/`keybd_event`/`mouse_event` to inject input into another
process's window.**

## Components

### `IMicrophoneCapture` / `MicrophoneCapture` — `SpotifyGameRadio.Core/Audio/MicrophoneCapture.cs`

```csharp
public interface IMicrophoneCapture
{
    void Start(string? deviceId); // "" or null = default input device
    /// Stops capturing and returns everything captured since Start(), resampled
    /// to 16 kHz mono float32 (what Whisper.net expects). Empty array if Start()
    /// was never called or nothing was captured.
    float[] Stop();
}
```

NAudio `WasapiCapture` on the chosen `MMDevice` (`DataFlow.Capture`,
enumerated the same way `RenderDeviceEnumerator` does for render devices —
add a parallel `CaptureDeviceEnumerator.ListCaptureDevices()`). Buffers raw
samples in a growable list during `Start()..Stop()`; `Stop()` mixes down to
mono (average channels) and resamples to 16000 Hz using the same
`WdlResampler` pattern `ResamplingCaptureService` already uses, then
returns the flat float array. Not unit-tested (real hardware/NAudio, same
as `WasapiDeviceLoopbackCapture`).

### `ISpeechToText` / `WhisperSpeechToText` — `SpotifyGameRadio.Core/Speech/`

```csharp
public interface ISpeechToText
{
    Task<string> TranscribeAsync(float[] pcm16kMono, string? vocabularyHint, CancellationToken ct);
}
```

`WhisperSpeechToText` wraps Whisper.net's `WhisperFactory`/`WhisperProcessor`
pointed at the cached model file path (from `VoiceModelStore`). Runs
transcription on a background thread (`Task.Run`) so the UI thread never
blocks. `vocabularyHint` is passed as Whisper's initial-prompt parameter.

`VocabularyPromptBuilder.Build(IEnumerable<string> terms) -> string` (a
plain static method, pure, unit-tested): splits/dedupes/trims the user's
custom vocabulary list into the prompt string Whisper expects. There's a
practical token budget for the prompt — cap at roughly 200 characters
(the initial-prompt is only a soft bias, not a hard constraint, so it
doesn't need to hold much) and drop terms past that, keeping the earliest
ones in the user's list.

### `VoiceModelStore` — `SpotifyGameRadio.Core/Speech/VoiceModelStore.cs`

```csharp
public class VoiceModelStore
{
    public string ModelPath { get; } // %LOCALAPPDATA%\SpotifyGameRadio\models\ggml-small.en.bin
    public bool IsDownloaded { get; } // File.Exists + SHA256 matches the pinned hash
    public Task DownloadAsync(IProgress<double> progress, CancellationToken ct);
}
```

Pinned URL + SHA256 constants, analogous to `fetch-phonon.ps1`'s
`$PhononDllSha256`. `DownloadAsync` streams to a `.part` temp file first,
verifies the hash, then moves it into place — a failed/cancelled download
never leaves a corrupt file where `IsDownloaded` would wrongly report true.

### `VoiceChatSession` — `SpotifyGameRadio.Core/Speech/VoiceChatSession.cs`

Modelled directly on `CalibrationSession`: pure, no WPF, unit-tested with
fakes.

```csharp
public enum VoiceChatState { Idle, Recording, Transcribing, PreviewReady }

public sealed class VoiceChatSession : IDisposable
{
    public VoiceChatSession(IMicrophoneCapture mic, ISpeechToText stt, Func<string?> vocabularyHint);

    public VoiceChatState State { get; }
    public string? Transcript { get; } // set once State == PreviewReady, else null

    public event Action<VoiceChatState>? StateChanged;
    public event Action<string>? PreviewReady;   // transcript text
    public event Action<string>? Failed;         // reason (session returns to Idle immediately after)

    public void BeginRecording();  // Idle|PreviewReady -> Recording (re-record clears any pending preview)
    public void EndRecording();    // Recording -> Transcribing -> PreviewReady, or straight back to Idle
    public void Discard();         // PreviewReady -> Idle
    public void Dispose();
}
```

- `BeginRecording()` calls `mic.Start(deviceId)`; ignored if already
  `Recording` or `Transcribing`. If called during `PreviewReady`, clears
  `Transcript` and transitions to `Recording` immediately (the re-record
  case) — no separate confirmation needed.
- `EndRecording()` calls `mic.Stop()`. If the returned buffer is shorter
  than a minimum duration (~200 ms of 16 kHz mono, i.e. `Length < 3200`),
  skip transcription entirely and go straight back to `Idle` — an
  accidental brush of the key never shows a preview.
- Otherwise: `State = Transcribing`; calls
  `stt.TranscribeAsync(buffer, vocabularyHint(), ct)`. On completion
  (marshalled back through the same `Raise`-style pattern
  `CalibrationSession` uses, so events never race a concurrent
  `Dispose()`/`Discard()`):
  - Non-empty result → `Transcript = result`; `State = PreviewReady`;
    raise `PreviewReady`.
  - Empty/whitespace result → raise `Failed("No speech detected.")`,
    `State = Idle`.
  - Exception → raise `Failed(ex.Message)`, `State = Idle`.
- `Discard()` clears `Transcript`, `State = Idle`. No-op outside
  `PreviewReady`.
- `Dispose()` cancels any in-flight transcription and is safe to call
  twice.

### Overlay window — `SpotifyGameRadio.App/VoicePreviewOverlay.xaml(.cs)`

A small, borderless (`WindowStyle="None"`), `Topmost="True"`,
`ShowInTaskbar="False"` window, themed to match the rest of the app.
Positioned bottom-center of the primary screen, sized to content up to a
max width. Shows:
- The pending transcript text, or "Listening…" while `Recording`,
  "Transcribing…" while `Transcribing`.
- A muted hint line naming the bound confirm/discard hotkeys (falls back
  to "(unbound)" for either, matching `HotkeyCaptureControl`'s existing
  "(click to set)" convention elsewhere).

Purely a passive readout — no buttons, no focus, no click handling; all
interaction is via the global hotkeys, the same way calibration's audio
cues are the only feedback channel during that flow. `MainViewModel` owns
its lifetime: created and shown on the first `Recording` transition,
hidden on `Idle`.

### `RadioProfile` — five new fields

```csharp
private FreelookHotkey _voiceRecordHotkey = new() { VirtualKeyCode = 0 };
private FreelookHotkey _voiceConfirmHotkey = new() { VirtualKeyCode = 0 };
private FreelookHotkey _voiceDiscardHotkey = new() { VirtualKeyCode = 0 };
private string _voiceCustomVocabulary = VoiceVocabularyDefaults.Starter;
private string _voiceMicrophoneDeviceId = "";

public FreelookHotkey VoiceRecordHotkey { get => _voiceRecordHotkey; set => SetField(ref _voiceRecordHotkey, value); }
public FreelookHotkey VoiceConfirmHotkey { get => _voiceConfirmHotkey; set => SetField(ref _voiceConfirmHotkey, value); }
public FreelookHotkey VoiceDiscardHotkey { get => _voiceDiscardHotkey; set => SetField(ref _voiceDiscardHotkey, value); }
public string VoiceCustomVocabulary { get => _voiceCustomVocabulary; set => SetField(ref _voiceCustomVocabulary, value); }
public string VoiceMicrophoneDeviceId { get => _voiceMicrophoneDeviceId; set => SetField(ref _voiceMicrophoneDeviceId, value); }
```

`VoiceVocabularyDefaults.Starter` (new small static class next to
`RadioProfile`): a comma-separated starter list of common milsim/Squad-style
terms — FOB, RTB, LZ, HLZ, CASEVAC, MEDEVAC, WIA, KIA, RP, rally point,
danger close, contact front, resupply, rearm, AO, callsign, fire team,
overwatch, klick, grid, MGRS, azimuth, waypoint, bearing, tower. (Numbered
references like "tower 2" need no special-casing — "tower" in the
vocabulary plus Whisper's native number handling covers it.) The user
edits this as free text (comma- or newline-separated) in a new multi-line
`TextBox` in the UI; splitting logic lives in `VocabularyPromptBuilder`,
covered there by tests.

### `MainViewModel` wiring

New fields: `_voiceSession` (`VoiceChatSession?`), three new
`TapHotkeyWatcher`s (`_voiceRecordWatcher` using both `Pressed` and
`Released`, like the vehicle-toggle watcher; `_voiceConfirmWatcher` /
`_voiceDiscardWatcher` using `Pressed` only), `_voiceOverlay`
(`VoicePreviewOverlay?`), `_voiceModelStore` (`VoiceModelStore`,
constructed once in the view model's constructor).

Created in the constructor, not in `Start()`/`Stop()`: voice chat has no
dependency on the radio pipeline or mouse hook, so it must work whether or
not the radio effect is running.

- Record watcher `Pressed` → if `!_voiceModelStore.IsDownloaded`, set
  `StatusMessage` and do nothing further (no blocking modal — the Voice
  Chat card has its own download button/progress for this); else
  `_voiceSession.BeginRecording()`.
- Record watcher `Released` → `_voiceSession.EndRecording()`.
- Confirm watcher `Pressed` → no-op unless
  `_voiceSession.State == PreviewReady`; otherwise
  `Clipboard.SetText(_voiceSession.Transcript)`,
  `StatusMessage = "Copied to clipboard — paste it in."`,
  `_voiceSession.Discard()`.
- Discard watcher `Pressed` → `_voiceSession.Discard()`.
- `_voiceSession.StateChanged` → show/position the overlay and update its
  text for `Recording`/`Transcribing`/`PreviewReady`; hide it on `Idle`.
- `_voiceSession.Failed` → `StatusMessage = reason` (the session's own
  `State` has already gone back to `Idle` by the time this fires, so the
  overlay hides via the `StateChanged` handler as normal — `Failed` only
  ever needs to update the status line).
- `OnProfilePropertyChanged` additions: the three voice hotkeys each call
  `SetHotkey` on their matching watcher, exactly like the existing
  vehicle/recenter/calibrate hotkey wiring.

### Window — `MainWindow.xaml`

A new "Voice Chat" card (placement — its own card, likely alongside
Vehicle & Auto-mute or Freelook — decided at implementation time):
- Three `HotkeyCaptureControl` rows: Record, Confirm, Discard.
- A microphone `ComboBox` (mirrors the existing render-device combo,
  backed by a new `CaptureDeviceEnumerator`).
- A multi-line `TextBox` bound to `Profile.VoiceCustomVocabulary`. This
  needs a new themed `TextBox` style in `Theme.xaml` — nothing text-
  editable exists there yet (everything so far has been sliders,
  checkboxes, and hotkey-capture buttons).
- A "Download voice model" button + progress bar, visible only while
  `!VoiceModelStore.IsDownloaded`; replaced by a small "Model ready"
  muted note once downloaded.

## Data flow

```
Record hotkey held ──> VoiceChatSession.BeginRecording() ──> MicrophoneCapture.Start()
Record hotkey released ──> VoiceChatSession.EndRecording() ──> MicrophoneCapture.Stop()
                                │
                                ├─ too short ──────> State=Idle (no preview)
                                └─ otherwise ──> State=Transcribing
                                                     │
                                      WhisperSpeechToText.TranscribeAsync(pcm, vocabularyHint)
                                                     │
                              success, non-empty ────┼──── empty / exception
                                      │                          │
                              State=PreviewReady          Failed → StatusMessage
                            (overlay shows text)            → State=Idle

Confirm hotkey ──> Clipboard.SetText(Transcript) ──> Discard() ──> State=Idle
Discard hotkey ──> Discard() ──> State=Idle
Record hotkey held again (while PreviewReady) ──> BeginRecording() again (replaces pending transcript)
```

## Testing

### `VoiceChatSessionTests` (new)

Fake `IMicrophoneCapture` (settable `Stop()` return value) and fake
`ISpeechToText` (settable result/exception via `Task.FromResult`/
`Task.FromException` — no real async delay needed).

- `BeginRecording` → `EndRecording` with a "long enough" fake buffer →
  `Transcribing` then `PreviewReady` with the fake's transcript.
- `EndRecording` with a buffer under the minimum-duration guard → stays
  `Idle`, `PreviewReady` never raised, STT never called.
- Fake STT returns `""` → `Failed` then `Idle`.
- Fake STT throws → `Failed` with the exception message, then `Idle`.
- `Discard()` from `PreviewReady` → `Idle`, `Transcript` cleared.
- `BeginRecording()` again while `PreviewReady` → `Recording` immediately,
  old `Transcript` cleared before the new one arrives.
- `Dispose()` is safe to call twice; no events fire after.

### `VocabularyPromptBuilderTests` (new)

- Empty/whitespace input → empty prompt (no crash; Whisper just gets no
  hint).
- Comma-separated and newline-separated terms both split correctly;
  duplicate and blank entries are dropped.
- A list past the character budget is truncated, keeping the earliest
  terms (order-preserving), not silently dropping arbitrary ones.

### `VoiceModelStoreTests` (new)

- `IsDownloaded` is false when the file is missing.
- `IsDownloaded` is false when the file exists but its hash doesn't match
  (corrupt/partial download) — inject the expected hash via a test-only
  constructor overload so this doesn't depend on the real pinned model.
- `DownloadAsync`'s temp-file-then-verify-then-move sequencing is tested
  with a fake downloader delegate; the real network call itself is not
  unit-tested.

### `RadioProfileTests` / `ConfigStoreTests` (extend)

- Defaults: all three hotkeys unbound, `VoiceCustomVocabulary` equals the
  starter list, `VoiceMicrophoneDeviceId` empty.
- Legacy-JSON-missing-fields test extended the same way every prior field
  was.

### `MainViewModel` — no framework for it today

Same as calibration: wiring is thin and verified manually, keeping the
logic under test in `VoiceChatSession`.

### Manual verification

1. First run: Voice Chat card shows the download button; download it;
   confirm the progress bar and that it becomes usable without a restart.
2. Bind Record/Confirm/Discard to three different keys.
3. Hold Record, say a short milsim phrase, release — overlay shows
   "Transcribing…" then the text.
4. Tap Confirm — overlay hides, status says copied; Ctrl+V into a text
   field (Notepad, Discord, whatever) actually pastes it.
5. Repeat, but tap Discard instead — overlay hides, clipboard unchanged.
6. Repeat, but hold Record again while the preview is showing — old text
   disappears, new recording replaces it.
7. Tap Record and release immediately (a brush of the key) — no overlay,
   no transcription attempt.
8. Add a few custom vocabulary terms (real callsigns/terms used); say a
   phrase using them; check transcription picks them up more reliably
   than before (this one's inherently fuzzy — a smoke test, not a
   pass/fail assertion).
9. Unplug/disable the selected microphone mid-session (or pick a device
   that goes away) — recording fails gracefully with a status message, no
   crash.

## Out of scope

- Auto-paste or any form of synthetic input injection into another
  process — ruled out entirely, see the anti-cheat note above.
- Cloud STT or any network dependency at transcription time (only the
  one-time model download touches the network).
- Multi-language support (model is `.en`, English-only).
- In-game overlay rendering — the preview window is a normal always-on-
  top desktop window, not rendered inside the game.
- Editing the transcript text by hand before confirming (only
  discard-and-redo, no inline text editing).
- Voice activity detection / hands-free "always listening" mode —
  push-to-talk only.
- Choosing between multiple Whisper model sizes in the UI (fixed at
  `small.en` for this iteration).
- Validating/preventing hotkey collisions between the three new voice
  hotkeys, or against existing hotkeys elsewhere in the app — same as the
  rest of the app today (e.g. nothing stops Recenter and Vehicle-toggle
  sharing a key either); last-bound-wins if you reuse one.
