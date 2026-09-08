# Setup

## Requirements
- Windows 10 20H1 (build 19041) or later for per-process audio capture.
  Older Windows falls back to whole-device capture automatically, and so
  does a newer machine where per-process activation fails at runtime
  (driver quirks) — the first failed Start silently switches to
  whole-device capture and shows a one-line notice.
- .NET 8 SDK.
- `phonon.dll` (Steam Audio native library) placed at
  `src/SpotifyGameRadio.App/runtimes/win-x64/native/phonon.dll`. Steam
  Audio is Valve's open-source spatial audio SDK (MIT licensed); grab a
  Windows release from
  <https://github.com/ValveSoftware/steam-audio/releases> — use version
  4.5.x or later, since this project's native struct layouts were
  written against the phonon 4.x API. Unzip it, find `phonon.dll` under
  `bin/windows-x64/` (older archives may name that folder slightly
  differently — take the 64-bit Windows one), and copy that single file
  to the path above, creating the folders if they don't exist. Without
  it, the app still runs but falls back to simple stereo panning
  instead of true HRTF.
- No sample-rate setup is needed. The DSP chain, HRTF spatializer, and
  output renderer all run at 48kHz; per-process capture always delivers
  that, and the `WasapiDeviceLoopbackCapture` fallback (used on pre-20H1
  Windows or when per-process capture fails) reports the device's actual
  rate, which `ResamplingCaptureService` converts up to 48kHz before it
  reaches the pipeline. It is a zero-copy passthrough when capture is
  already 48kHz, so there is no cost on the common path.

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

## Known Limitations

- A few settings still have no UI controls: output device selection and
  the profile name (so, profile switching too). To change them, click
  Save Profile once to create the file, then edit
  `%APPDATA%\SpotifyGameRadio\Profiles\<name>.json` directly and
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
device, the freelook hotkey, and the auto-routing settings.

## Source audio routing

To stop hearing the raw source (e.g. Spotify) alongside the processed
radio, the app routes the source app's output to a silent render
endpoint while running.

- Requires Windows 11 and a virtual audio device (VB-Audio Virtual
  Cable, VoiceMeeter, etc.). On Windows 10 the option is unavailable
  and Start is blocked while "Auto-route" is ticked — untick it and
  route the source manually in Windows Sound settings instead.
- Leave the device dropdown on "(auto-detect virtual cable)" to have one
  found at Start, or pick a device explicitly. The choice is saved when
  you click Save Profile.
- On Stop, the source's previous output device is restored. If the app
  is killed while running, the next launch restores it and shows
  "Restored source audio routing from a previous session."
- "Reset routing" forces the restore if anything is left pointing at
  the cable.
