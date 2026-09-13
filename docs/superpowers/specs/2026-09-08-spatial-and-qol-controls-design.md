# Spatial + QOL controls — design

Date: 2026-09-08
Status: approved for planning

## Goal

Add four user-facing controls to the running radio:

1. **Volume** — master output attenuation.
2. **Stereo width** — from a hard point source ("front console speaker") to a
   wide spread.
3. **Source height** — adjust the source's vertical position (`SourceY`) from the
   window, alongside the existing top-down X/Z canvas.
4. **Freelook always-on + Recenter** — support games whose free-look is always
   active (no hold-to-look gesture), plus a manual orientation reset.

All four apply live on a running pipeline — no Stop/Start.

## Non-goals

- No change to the capture, routing, or output-device machinery.
- Width does **not** touch the Steam Audio `spatialBlend` / HRTF directivity; it
  is a post-spatializer mid/side operation only, identical on the HRTF and
  stereo-pan paths.
- Height stays a plain slider; no new in-canvas interaction (wheel, modifier
  drag) and no elevation view.
- Recenter is a button now; binding it to its own hotkey is deferred.

## Profile changes (`RadioProfile`)

New fields, all raising `INotifyPropertyChanged` via the existing `SetField`:

| Field | Type | Default | Range (UI) |
|---|---|---|---|
| `Volume` | `float` | `1.0f` | 0 … 1 |
| `StereoWidth` | `float` | `1.0f` | 0 … 2 |
| `FreelookAlwaysOn` | `bool` | `false` | checkbox |

`SourceY` already exists (default `-0.1f`); the height slider binds straight to
it, range −2 … +2 metres (matches the canvas's ±2 m scale).

`ConfigStore` load of a pre-existing profile JSON without these keys must fall
back to the defaults above (same tolerance the store already has for other
fields — verify with a test).

## Audio path

### Pipeline block loop (`RadioPipeline.OnDataAvailable`)

Two new stages after spatialization, before `output.Write`:

```
effectChain.Process(monoBlock, frameSize)
activeSpatializer.Process(monoBlock, frameSize, stereoBlock)
StereoWidth.Apply(stereoBlock, frameSize, _stereoWidth)     // new
if (_volume != 1f) for i in 0..frameSize*2: stereoBlock[i] *= _volume   // new
output.Write(stereoBlock, stereoBlock.Length)
```

`_stereoWidth` and `_volume` are plain `float` fields on the pipeline, assigned
in `ApplyProfile` (same pattern as the effect-chain parameters), clamped
defensively:

```
_volume      = Math.Clamp(profile.Volume, 0f, 1f);
_stereoWidth = Math.Clamp(profile.StereoWidth, 0f, 4f);
```

The audio thread reads these fields once per block; individual `float` writes are
atomic, so a live change costs at most one block blended across the old and new
value — the concurrency note already in `ApplyProfile` covers this.

### `StereoWidth` (new — `Core/Dsp/StereoWidth.cs`)

```csharp
public static class StereoWidth
{
    /// Mid/side width. width 0 => mono (both channels = mid), 1 => unchanged,
    /// >1 => exaggerated ear-to-ear spread. Operates in place on interleaved
    /// stereo; frameCount is the number of L/R pairs.
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

No allocation, real-time safe. Volume is a trivial multiply kept inline in the
pipeline (no helper).

## Freelook (`FreelookTracker`)

- `OnMouseMoved` gate becomes:
  `if (!_profile.FreelookAlwaysOn && !_inputSource.IsHotkeyHeld) return;`
- `Update` (spring-back) returns early additionally when
  `_profile.FreelookAlwaysOn` — no auto-centering while always-on.
- New `public void Recenter() { YawDegrees = 0f; PitchDegrees = 0f; }`

`FreelookAlwaysOn` needs no new code path beyond adding it to
`LiveProfileProperties` (below): that routes its change through
`RadioPipeline.ApplyProfile` → `_tracker.ApplyProfile`, which swaps `_profile`,
and the tracker reads `_profile.FreelookAlwaysOn` live in the two guards above.

`RadioPipeline.RecenterListener()` → `_tracker.Recenter()`. Writes two floats from
the UI thread against the audio thread's per-block read — atomic per field, worst
case one block with yaw new / pitch old, inaudible.

## MainViewModel

- `LiveProfileProperties` gains `Volume`, `StereoWidth`, `FreelookAlwaysOn`
  (`SourceY` is already in the set).
- New `RecenterCommand` (`RelayCommand`): `_pipeline?.RecenterListener()`, then
  set `ListenerYaw = ListenerPitch = 0` so the marker snaps immediately rather
  than waiting for the next 30 Hz tick. Safe when stopped (null-guarded).

## MainWindow.xaml

Row list grows by two. New / changed rows:

- **Volume** — `Slider Minimum="0" Maximum="1"` bound `Profile.Volume`, after
  Wet/Dry.
- **Stereo width** — `Slider Minimum="0" Maximum="2"` bound `Profile.StereoWidth`,
  after Volume.
- **Freelook hotkey row** — add, right of `HotkeyCaptureControl`:
  `CheckBox Content="Freelook always on" IsChecked="{Binding Profile.FreelookAlwaysOn}"`
  and `Button Content="Recenter" Command="{Binding RecenterCommand}"`.
- **Canvas row** — wrap in a horizontal `StackPanel`:
  `[SourcePositionCanvas]` then a `TextBlock "Height (m)"` and
  `Slider Orientation="Vertical" Minimum="-2" Maximum="2" Height="200"
  Value="{Binding Profile.SourceY}"`.

Every `Grid.Row` index at or below the old freelook-hotkey row shifts by +2. The
window is `Height="940"`; bump to `1000` if the two extra rows overflow (confirm
visually).

`SourcePositionCanvas` itself is unchanged.

## Testing

### Automated (TDD, `LogiBuddy.Core.Tests`)

- `StereoWidthTests`:
  - `width == 1` leaves the buffer untouched
  - `width == 0` gives `L == R == (origL + origR) / 2` for every pair
  - `width == 2` doubles the side component (`L - R` doubles)
  - odd trailing sample / `frameCount` shorter than buffer is safe
- `RadioPipelineTests`:
  - `Volume = 0.5f` halves every output sample (dry passthrough, `FakeSpatializer`)
  - `StereoWidth = 0f` collapses a hard-panned spatializer's output to equal L/R
    — add `PanningFakeSpatializer` (writes `L = mono[i]`, `R = 0`)
  - `RecenterListener()` returns yaw and pitch to 0 after freelook input moved them
- `FreelookTrackerTests`:
  - `FreelookAlwaysOn = true`, hotkey **not** held, mouse move → yaw/pitch accumulate
  - `FreelookAlwaysOn = true`, `Update()` after movement → no spring-back
  - `Recenter()` zeroes both from a non-zero state
- `ConfigStoreTests`: a profile JSON missing the three new keys loads with
  `Volume == 1`, `StereoWidth == 1`, `FreelookAlwaysOn == false`

### Manual (running app)

- Volume slider attenuates to silence at 0, unity at 1.
- Width: 0 = mono/dead-centre point source; 1 = as today; 2 = noticeably wider.
- Height slider raises/lowers the source (audible elevation shift on the HRTF
  path; on the stereo-pan fallback it has no audible effect, as documented).
- Freelook always on: mouse moves the listener with no key held and the arrow
  does not spring back; Recenter snaps arrow + audio to forward.
- All four apply with the pipeline running, no Stop/Start; no "restart to apply".
- Layout: all rows visible, canvas + vertical slider aligned, nothing clipped.

## Risks

- **XAML row renumbering** — mechanical but error-prone; a wrong `Grid.Row`
  silently overlaps controls. Mitigate by building and eyeballing every row.
- **Width at extreme values** on already-wide HRTF output could push past ±1.0
  and into the (soft-clip-free) output path — clamp of 4 plus the 0–2 UI range
  keeps this mild; the manual pass should listen for clipping at width 2 on loud
  material.
- **Volume + width order**: width first, then volume, so the master fader also
  tames any width-induced level increase.
