# Source Audio Routing — Design Spec

Date: 2026-09-06

## Problem

The app captures a source process (e.g. Spotify) with per-process
WASAPI loopback, runs it through the radio DSP + HRTF chain, and
renders the result to the user's headphones. But the source's
*original* audio is still going to those same headphones, so the user
hears both the raw source and the processed radio version overlapping.

Muting the source is not a workaround: per-process loopback capture
reflects the source *after* session volume/mute, so muting the source
silences the capture too.

The only way to remove the raw stream from the user's ears while
keeping capture intact is to send the source's output to a different
render endpoint that the user is not monitoring — in practice, a
virtual audio cable (the user has VB-Audio Virtual Cable installed).
A feasibility check confirmed per-process loopback is
endpoint-independent: a tone rendered to "CABLE Input" was still
captured per-process at full level.

Today this requires the user to hand-configure per-app output routing
in Windows Sound settings every session. This spec makes the app do it
automatically on Start and undo it on Stop.

## Non-goals

- No Windows 10 support. The routing interop targets the Windows 11
  `IAudioPolicyConfig` interface variant only. On Windows 10 or an
  unrecognised build the router reports unsupported and the feature is
  unavailable (capture still works; the user hears both streams or
  routes manually).
- No shipping/installing a virtual audio device. The user must already
  have one (virtual cable, VoiceMeeter, etc.); the app only routes to
  it.
- No "listen to the cable" passthrough or monitoring path. The app's
  processed output is the only intended monitor path.
- No routing of roles beyond `eConsole`, `eMultimedia`,
  `eCommunications`.
- No per-child-pid targeting. If the source name matches several
  processes, every matching pid is routed.

## Architecture

Three new units in `SpotifyGameRadio.Core/Audio/`, plus profile and
view-model wiring.

### AudioPolicyConfigInterop (new)

`internal static class` holding the raw COM interop for Windows 11
per-app audio routing. Sibling to `WasapiProcessLoopbackInterop.cs`.

- Activation: `RoGetActivationFactory` (from `combase.dll`) with the
  class name `Windows.Media.Internal.AudioPolicyConfig`, queried for
  the Windows 11 `IAudioPolicyConfig` interface variant.
- Interface methods used:
  - `SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow,
    ERole role, IntPtr deviceIdHString)`
  - `GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow,
    ERole role, out IntPtr deviceIdHString)`
- Endpoint id form: the MMDevice id (`{0.0.0.00000000}.{guid}`) is
  wrapped to the routing form
  `\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{e6327cad-dcec-4949-ae8a-991e976a79d2}`
  before being passed as an `HSTRING`. An empty string clears the
  override (reverts to system default).
- HSTRING lifetime is managed with `WindowsCreateString` /
  `WindowsDeleteString` (from `combase.dll`).

No automated test — undocumented COM against live OS state, same
rationale as `WasapiProcessLoopbackInterop`.

### ISourceAudioRouter / WindowsAppAudioRouter (new)

```
public interface ISourceAudioRouter
{
    bool IsSupported { get; }
    AppAudioRoute GetCurrentRoute(int processId);
    void RouteProcess(int processId, string renderDeviceId);
    void RestoreProcess(int processId, AppAudioRoute previous);
}
```

- `AppAudioRoute` — a record of the per-role endpoint ids
  (`Console`, `Multimedia`, `Communications`) read back from
  `GetPersistedDefaultAudioEndpoint`, so a pre-existing user override
  can be restored exactly. A role with no override is recorded as an
  empty string.
- `IsSupported` — true only when `Environment.OSVersion.Version.Build`
  is a Windows 11 build (>= 22000) **and** the activation factory
  query succeeds; false otherwise. Checked once, cached.
- `RouteProcess` — sets all three roles to `renderDeviceId` for the
  given pid. Throws `SourceRoutingException` (new) wrapping the
  HRESULT on failure.
- `RestoreProcess` — sets each role back to the value in `previous`
  (empty string ⇒ clear override).

No automated test for the class itself (live OS state); the exception
path and role loop are simple enough to leave to the manual checklist.

### RenderDeviceEnumerator (new)

```
public static class RenderDeviceEnumerator
{
    public static IReadOnlyList<RenderDeviceInfo> ListRenderDevices();
    public static bool LooksLikeVirtualCable(string friendlyName);
}
```

- `RenderDeviceInfo { string Id; string FriendlyName; }`.
- `ListRenderDevices` — active render endpoints via NAudio
  `MMDeviceEnumerator` (NAudio is already a dependency).
- `LooksLikeVirtualCable` — case-insensitive substring match on any
  of: `vb-audio`, `vb audio`, `cable`, `voicemeeter`, `virtual`.
  Unit-tested.

## Configuration

`RadioProfile` gains two fields (additive; existing JSON profiles
deserialise with the defaults):

- `bool AutoRouteSource` — default `true`.
- `string RouteSourceToDeviceId` — default `""`. Empty means
  "auto-detect": at Start, the first `ListRenderDevices()` entry for
  which `LooksLikeVirtualCable` is true is used. If the user picks a
  device in the UI, its id is stored here.

## Data flow

`MainViewModel` holds one `ISourceAudioRouter` (`new
WindowsAppAudioRouter()`), constructed once, in the same
new-it-up-directly style as `_configStore`. The held
`RouteRecoveryRecord` is a nullable field alongside `_pipeline`.

**Start (`MainViewModel.Start`), when `Profile.AutoRouteSource` is true,
before building the pipeline:**

1. `!router.IsSupported` → set status "Auto-routing needs Windows 11 —
   turn it off in settings to continue." and return without starting.
2. Resolve target device: `RouteSourceToDeviceId` if non-empty and
   still present in `ListRenderDevices()`, else the auto-detected
   virtual cable. None resolvable → status "No route-target device —
   pick one or turn off auto-routing." and return.
3. Resolve source pids: `Process.GetProcessesByName(
   Profile.SourceProcessName)`. Empty → status "Start the source app
   first, then Start." and return.
4. For each pid: `previous = router.GetCurrentRoute(pid)`; collect
   into a `RouteRecoveryRecord`. Persist the record to
   `%AppData%/SpotifyGameRadio/route-recovery.json` **before** the
   first `RouteProcess` call.
5. For each pid: `router.RouteProcess(pid, targetDeviceId)`. On
   `SourceRoutingException` → restore any pids already routed, delete
   the recovery record, set status "Couldn't route source audio:
   {hresult}. Fix the route device or turn off auto-routing." and
   return without starting.
6. Keep the `RouteRecoveryRecord` in a view-model field and build the
   pipeline as today.

**Stop (`MainViewModel.Stop`):**

1. If a recovery record is held: for each pid,
   `router.RestoreProcess(pid, previous)` (best-effort, swallow
   per-pid failures into the status line).
2. Delete `route-recovery.json`. Clear the held record.
3. Existing pipeline/hook/timer teardown.

**Startup (`MainViewModel` constructor):**

- If `route-recovery.json` exists (⇒ the previous session did not
  restore, e.g. crash/kill): for each recorded pid,
  `router.RestoreProcess`. Then delete the file. Failures are logged
  to the status line but do not block startup. Recorded pids that no
  longer exist are skipped.

**With `Profile.AutoRouteSource` false:** none of the above runs; Start
and Stop behave exactly as before this change.

## Crash safety

`route-recovery.json` schema:

```json
{
  "sourceProcessName": "Spotify",
  "routes": [
    { "processId": 12332,
      "console": "", "multimedia": "", "communications": "" }
  ]
}
```

Written before the first routing change, deleted after a clean
restore. Its presence at startup means "restore then delete". A stale
record whose pids are all gone is simply deleted. The empty-string
role values mean the source had no prior override and should be
cleared on restore.

A **"Reset source routing"** button in the UI runs the same restore
path on demand: if a recovery record exists, restore from it; else
clear all three roles for every current pid of
`Profile.SourceProcessName`. Then delete the record. This covers the
case where a record was lost or the user wants to force a revert.

## UI

`MainWindow.xaml`, a new row directly under the Source row:

- `CheckBox` "Auto-route source to a silent device", bound to
  `Profile.AutoRouteSource`.
- `ComboBox` of `RenderDeviceEnumerator.ListRenderDevices()`
  (`DisplayMemberPath` FriendlyName, `SelectedValuePath` Id), bound to
  `Profile.RouteSourceToDeviceId`, `IsEnabled` tied to the checkbox.
  When the bound id is empty the box shows a greyed "(auto-detect
  virtual cable)" placeholder item.
- `Button` "Reset source routing", always enabled.

A `RefreshRenderDevicesCommand` (or reuse of the existing Refresh
button's handler) repopulates the device list.

## Error handling

| Condition | Behaviour |
| --- | --- |
| Win10 / unrecognised build (`!IsSupported`) | Start blocked with a status message; feature effectively off. |
| No route-target device resolvable | Start blocked with a status message. |
| Source process not running at Start | Start blocked with a status message. |
| `SetPersistedDefaultAudioEndpoint` returns a failure HRESULT | Already-routed pids restored, recovery record deleted, Start blocked with the HRESULT in the status. |
| App killed mid-session | Recovery record restored on next launch, then deleted. |
| Recovery restore fails for a pid | Logged to status, does not block; "Reset source routing" available. |
| Route device removed between sessions | `RouteSourceToDeviceId` no longer in the list → falls back to auto-detect; if that also fails, Start blocked as "no route-target device". |
| `AutoRouteSource` off | No routing code runs; pre-change behaviour. |

## Testing

Automated (xUnit, `SpotifyGameRadio.Core.Tests`):

- `RenderDeviceEnumeratorTests` — `LooksLikeVirtualCable` true for
  representative virtual-cable names ("CABLE Input (VB-Audio Virtual
  Cable)", "VoiceMeeter Input", "Virtual Audio Device"), false for
  real devices ("Speakers (Focusrite USB Audio)", "LG ULTRAGEAR").
- `ConfigStoreTests` — extend the round-trip test to assert
  `AutoRouteSource` and `RouteSourceToDeviceId` persist, and that a
  JSON profile written without them loads with the defaults
  (`AutoRouteSource == true`, `RouteSourceToDeviceId == ""`).

Manual checklist (documented in `docs/SETUP.md`):

1. VB-Audio cable installed. Spotify playing. `AutoRouteSource` on,
   route device left on auto-detect.
2. Click Start. Windows Volume Mixer shows Spotify's output device is
   now "CABLE Input". Only the processed radio audio is audible.
3. Click Stop. Volume Mixer shows Spotify back on the default device.
4. Click Start again, then kill the app process (Task Manager).
   Relaunch. Volume Mixer shows Spotify reverted to default; no
   `route-recovery.json` left in `%AppData%/SpotifyGameRadio`.
5. Set `AutoRouteSource` off. Start behaves as before (both streams
   audible).
6. On a machine reporting a non-Win11 build (or with the build check
   forced false), Start is blocked with the Windows 11 notice.

## Open questions for implementation planning

- Exact Windows 11 `IAudioPolicyConfig` IID and vtable method order
  for build 26200 — to be pinned from the EarTrumpet / SoundSwitch
  implementations during planning, with a runtime guard that fails
  cleanly (→ `IsSupported = false`) if the factory query or first call
  returns `E_NOINTERFACE`.
- Whether `GetPersistedDefaultAudioEndpoint` returns `S_OK` with an
  empty string, or a specific failure HRESULT, when the app has no
  existing override — the "no prior override" detection must handle
  whichever it is.
