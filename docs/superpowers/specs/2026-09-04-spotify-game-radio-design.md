# Spotify Game Radio — Design Spec

Date: 2026-09-04

## Problem

Milsim driving (e.g. Arma) plays much better with immersive "in-vehicle
radio" audio than with Spotify/YouTube played flat over the top. The
goal is a Windows desktop app that takes an arbitrary audio source
(Spotify, a browser tab, Discord, etc.), runs it through a filter
chain that makes it sound like a tinny in-cabin radio, and places it
at a fixed 3D point in space so that when the player free-looks
in-game (holding a hotkey and moving the mouse), the audio image
rotates the way a real fixed sound source would.

## Non-goals

- No game memory reads, DLL injection, or process hooking into the
  target game. Anything that touches the game process is out of
  scope — this is strictly an external, input-hook-based tool (same
  risk class as AutoHotkey/Voice Attack), not an anticheat risk.
- No per-browser-tab audio isolation. Source audio capture is
  per-process; a browser process captures all of its tabs' audio
  together. Acceptable limitation, not something this project solves.
- No true positional sync with the game's actual view angle — freelook
  angle is *inferred* from raw mouse deltas while a hotkey is held, not
  read from the game. This is an approximation, not pixel-perfect.

## Architecture

Single WPF desktop app (.NET), six components:

1. **AudioCaptureService** — WASAPI capture of a user-selected audio
   source. Enumerates running processes with active WASAPI audio
   sessions (via `IAudioSessionManager2`) and lets the user pick one
   from a dropdown (Spotify, a browser, Discord, etc.). Uses Windows
   10 20H1+ per-process loopback capture where available; falls back
   to whole-device loopback capture on older Windows or if
   per-process capture fails. Produces a stream of raw PCM frames.

2. **RadioEffectChain** — pure DSP applied to captured PCM before
   spatialization. Curated preset with tweakable knobs (not a full
   modular graph): high-pass filter, low-pass filter, soft-clip
   distortion, compressor, optional static/crackle noise layer,
   wet/dry mix.

3. **FreelookTracker** — global low-level mouse hook (`WH_MOUSE_LL`
   or raw input) + a user-configured hotkey (key or mouse button).
   While the hotkey is held, accumulates raw mouse deltas into an
   internal yaw/pitch value, clamped to a configurable max angle
   range. When released, eases back toward zero at a configurable
   spring rate, mimicking how freelook recenters in most games. Runs
   on its own thread, exposes the current yaw/pitch as a lock-free
   shared value for the audio thread to read — never blocks audio.

4. **SpatializerEngine** — wraps Steam Audio's HRTF binaural
   renderer (Valve, MIT-licensed, via P/Invoke). Takes filtered PCM +
   a fixed source position (set by the user, e.g. "on the dash") +
   the current listener yaw/pitch from FreelookTracker, and produces
   binaural stereo output. The source's apparent position shifts
   because the listener head rotates, not because the source moves.

5. **AudioOutputService** — WASAPI render of the binaural stream to
   a user-selected output device — normally the same physical
   headphones the game uses, so game audio and radio audio mix
   acoustically at the ear.

6. **ConfigStore** — JSON-persisted named profiles: source process,
   output device, fixed radio position, filter settings, freelook
   hotkey, mouse sensitivity multiplier, angle clamp, spring rate.
   Supports multiple profiles (e.g. one per game/vehicle).

## Data flow

```
selected source process ──▶ AudioCaptureService ──▶ RadioEffectChain ──▶ SpatializerEngine ──▶ AudioOutputService ──▶ output device
                                                                              ▲
                                                    FreelookTracker (yaw/pitch) ┘
                                                    (global mouse hook, gated by hotkey)

ConfigStore ──loads/saves──▶ all components (one JSON profile per game/vehicle)
```

All audio processing (capture → effects → spatialize → output) runs
on one real-time audio callback thread in small fixed-size blocks
(~10-20ms) for low, consistent latency. FreelookTracker runs
independently and only ever writes a single shared yaw/pitch value
the audio thread reads each block.

## Error handling

- **Selected source process not running / not found**: capture
  service reports "no source" status; UI shows a warning banner;
  retries periodically until the process appears.
- **Output device disappears** (e.g. headset unplugged): fall back to
  the system default output device, show a warning, let the user
  reselect.
- **Steam Audio native library fails to load**: app does not crash —
  falls back to simple stereo panning (no true HRTF), with a visible
  warning that spatialization is degraded.
- **Global mouse hook fails to install** (blocked by AV/permissions):
  freelook tracking is disabled; the radio plays fixed at center
  (same as an untracked source); warning shown. App stays otherwise
  functional.
- **Buffer underrun/glitch**: a diagnostic counter is shown in the UI;
  logged, does not crash.

## Testing

- Unit tests: RadioEffectChain (known input signals → verify filter
  frequency response and effect behavior), FreelookTracker angle math
  (synthetic mouse-delta sequences → verify clamping and spring-back),
  ConfigStore (round-trip serialization of profiles).
- SpatializerEngine: validate against Steam Audio's own reference
  behavior for known source positions and listener orientations.
- Manual/perceptual: run the app against Spotify (or another source)
  and a real game, hold the freelook hotkey, confirm the audio image
  shifts correctly. This cannot be automated — 3D audio perception is
  inherently subjective.

## Open questions for implementation planning

- Exact Steam Audio integration details (bundling native binaries,
  P/Invoke surface).
- UI layout for the 3D/2D source-position picker (likely a simple
  top-down 2D canvas: click-drag a point representing the radio's
  fixed position relative to "forward").
- Default radio DSP preset values (to be tuned by ear during
  implementation).
