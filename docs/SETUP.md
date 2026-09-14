# Setup

## Requirements
- Windows 10 20H1 (build 19041) or later for per-process audio capture.
  Older Windows falls back to whole-device capture automatically, and so
  does a newer machine where per-process activation fails at runtime
  (driver quirks) — the first failed Start silently switches to
  whole-device capture and shows a one-line notice.
- .NET 8 SDK.
- `phonon.dll` (Steam Audio's native HRTF library, Apache-2.0 licensed):
  - **End users:** it is already bundled in the release ZIP, next to the
    `.exe` — nothing to do. Its licence terms are in the ZIP's
    `THIRD-PARTY-NOTICES.txt`.
  - **Developers:** run `./build/fetch-phonon.ps1` once. It downloads the
    pinned Steam Audio 4.8.1 release, verifies its checksum, places the
    DLL at
    `src/LogiBuddy.App/runtimes/win-x64/native/phonon.dll`, and
    fetches the licence texts into `build/third-party/`.
  Without the DLL the app still runs but falls back to simple stereo
  panning instead of true HRTF. The 4.x API is required — the project's
  native struct layouts were written against it.
- No sample-rate setup is needed. The DSP chain, HRTF spatializer, and
  output renderer all run at 48kHz; per-process capture always delivers
  that, and the `WasapiDeviceLoopbackCapture` fallback (used on pre-20H1
  Windows or when per-process capture fails) reports the device's actual
  rate, which `ResamplingCaptureService` converts up to 48kHz before it
  reaches the pipeline. It is a zero-copy passthrough when capture is
  already 48kHz, so there is no cost on the common path.
- **A virtual audio cable (VB-Audio Virtual Cable, VoiceMeeter, etc.) —
  required for the app to be usable as intended, not optional.** Without
  one there is no way to stop the raw source and the processed radio
  from both playing at once, overlapping — see
  [Source audio routing](#source-audio-routing) below for why muting the
  source is not a substitute.

## Running

    dotnet run --project src/LogiBuddy.App/LogiBuddy.App.csproj

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
5. Optionally set a Recenter hotkey, a Vehicle toggle hotkey, and an Override
   hotkey (all unbound by default) in the window — Recenter snaps the
   listener back to forward without alt-tabbing out of the game; the vehicle
   toggle simulates getting in/out of a vehicle, muting the radio (not the
   source) after a configurable delay so it matches games with an
   exit-vehicle animation (e.g. Squad); the Override hotkey (or its button)
   instantly mutes/un-mutes the whole radio with no delay, independent of
   the vehicle toggle. All three work while the game has keyboard focus.
6. Click Start, then hold your freelook key and look around in-game —
   the radio audio should shift as if it were mounted in the vehicle.
7. Click Save Profile to keep these settings for next time.

## Known Limitations

- A few settings still have no UI controls: output device selection and
  the profile name (so, profile switching too). To change them, click
  Save Profile once to create the file, then edit
  `%APPDATA%\LogiBuddy\Profiles\<name>.json` directly and
  restart the app.
- Vertical look (pitch) has no audible effect on the spatialized audio
  — only left/right (yaw) does.
- On pre-Windows-10-20H1 systems, which use the whole-device capture
  fallback, choosing an audio source on the same device you're playing
  back to can create a feedback loop. Use headphones and/or route the
  source to a different device if you hit this.

## Real-time editing

While the pipeline is running, these apply instantly (no Stop/Start):
DSP sliders (high/low-pass, distortion, compressor, static, wet/dry),
the source-position marker, and the freelook tuning sliders (mouse
sensitivity, max yaw/pitch, spring-back).

These need a Stop then Start — the window shows an orange "restart to
apply" hint when you change one while running: Source process, Output
device, the freelook hotkey, the auto-routing settings, and the
source-mute setting.

## Source audio routing

Without this, LogiBuddy is not really usable as intended: you'd hear the
raw source (e.g. Spotify) and the processed radio playing at once,
overlapping — muting the source instead does not work (see
[Source audio muting](#source-audio-muting) below for why). Routing the
source app's output to a silent render endpoint while running is the
only way to hear just the processed radio.

- "Auto-route source to a silent device" **automates** this, but the
  automation itself requires **Windows 11** and a virtual audio device
  (VB-Audio Virtual Cable, VoiceMeeter, etc.) installed. On Windows 10
  the option is unavailable and Start is blocked while "Auto-route" is
  ticked — untick it and route the source manually instead: Windows
  Settings → System → Sound → "App volume and device preferences" → set
  the source app's output to the virtual cable. This is a one-time
  setup per source app; Windows remembers it across launches.
- Leave the device dropdown on "(auto-detect virtual cable)" to have one
  found at Start, or pick a device explicitly. The choice is saved when
  you click Save Profile.
- On Stop, the source's previous output device is restored. If the app
  is killed while running, the next launch restores it and shows
  "Restored source audio routing from a previous session."
- "Reset routing" forces the restore if anything is left pointing at
  the cable.

## Source audio muting

"Auto-mute source audio on Start" is **not** an alternative to routing,
despite sounding like one — it mutes the source app's own Windows audio
session, and since this app captures that same session's audio to build
the radio, muting it also silences the radio itself. The same is true of
muting the source manually in the Windows Volume Mixer: it's the same
underlying session mute, so it kills LogiBuddy's own capture too. There
is no mute-based workaround — routing (via a virtual cable) is the only
way to avoid hearing the raw source. This toggle is off by default and
is only useful in the edge case where you don't need this app's capture
of that source at all; it is automatically skipped whenever "Auto-route
source to a silent device" is enabled.
