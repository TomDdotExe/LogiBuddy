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
- Set your default Windows output device's sample rate to 48000 Hz
  (Sound Settings → your device → Device properties → Additional device
  properties → Advanced). The DSP chain, HRTF spatializer, and output
  renderer all assume 48kHz; only the `WasapiDeviceLoopbackCapture`
  fallback (used on pre-20H1 Windows or if per-process capture fails)
  reports the device's actual negotiated rate, so a mismatch there
  won't crash anything but can make the radio filter's cutoffs sound
  slightly off. Most modern default devices are already 48kHz.

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
