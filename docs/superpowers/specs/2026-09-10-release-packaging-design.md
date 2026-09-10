# Release packaging — portable ZIP with bundled phonon.dll

Date: 2026-09-10
Status: Approved (brainstorming)

## Problem

The app is feature-complete on `master` but has no distribution story. An
end user has no way to install it, and `phonon.dll` (Steam Audio's native
HRTF library) is a manual developer setup step documented in
`docs/SETUP.md`. When the DLL is absent the app silently falls back to
`StereoPanSpatializer` ("standard panning") instead of true HRTF — this
was hit for real on 2026-09-09 when the built app was first run without
the DLL present.

The user asked (2026-09-09) that `phonon.dll` be part of the
installation/setup package for the full release, not a step end users
perform themselves.

## Decisions (locked during brainstorming)

| Question | Decision |
| --- | --- |
| Distribution format | Portable ZIP — unzip and run, no installer, no uninstaller |
| .NET runtime | Framework-dependent — small ZIP; user needs the .NET 8 Desktop Runtime |
| `phonon.dll` sourcing | Build-time download script with pinned URL + SHA256 verification |
| Version source | Argument to the package script (`-Version`); no version state kept between releases |
| Script layout | Two scripts: `build/fetch-phonon.ps1` (independently useful) + `build/package.ps1` (calls it) |
| CI / signing / auto-update | Out of scope this pass (YAGNI); the script is the reusable unit for adding CI later |

## Deliverables

```
build/
  fetch-phonon.ps1          # ensure runtimes/win-x64/native/phonon.dll is the pinned Steam Audio build
  package.ps1               # fetch -> publish -> assemble -> zip
  README.md                 # how to cut a release
  templates/
    README.txt              # end-user readme template, {{VERSION}} placeholder
  .cache/                   # (gitignored) downloaded archive cache
  .staging/                 # (gitignored) transient publish + zip assembly
  dist/                     # (gitignored) output zips
  third-party/              # (gitignored) steam-audio-LICENSE.md extracted by fetch
```

Plus edits to `src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`,
`.gitignore`, and `docs/SETUP.md`.

## Component: `build/fetch-phonon.ps1`

**Purpose:** guarantee that
`src/SpotifyGameRadio.App/runtimes/win-x64/native/phonon.dll` exists and
is exactly the pinned Steam Audio build. Idempotent; no network I/O after
the first successful run.

**Pinned constants (top of script):**

- `$SteamAudioVersion = "4.8.1"`
- `$ArchiveUrl = "https://github.com/ValveSoftware/steam-audio/releases/download/v4.8.1/steamaudio_4.8.1.zip"`
- `$ArchiveSha256` — SHA256 of `steamaudio_4.8.1.zip`. Captured during
  implementation by downloading once and recording the real value, then
  committed. Enforced on every run.
- `$PhononDllSha256` — SHA256 of the extracted `phonon.dll`. Also pinned
  during implementation. Lets a corrupt or partial extraction be caught.

**Parameters:**

- `-Force` — re-download even if a valid cached archive exists.
- `-CacheDir <path>` — default `build/.cache`.

**Flow:**

1. If the target `phonon.dll` exists and its SHA256 equals
   `$PhononDllSha256` → write "phonon.dll already present (Steam Audio
   $SteamAudioVersion)" and exit 0.
2. Otherwise resolve the cached archive at
   `<CacheDir>/steamaudio_4.8.1.zip`. If it is missing, or `-Force` is
   set, or its SHA256 does not match `$ArchiveSha256`, download it from
   `$ArchiveUrl`. Use TLS 1.2, follow redirects, fail on non-200.
3. Verify the archive's SHA256 against `$ArchiveSha256`. On mismatch,
   **hard-fail** (`throw`) and do not extract. Delete the bad download.
4. Open the archive and find the entry whose full name matches
   `*/lib/windows-x64/phonon.dll` (case-insensitive, search — do not
   hard-code the top-level folder name; Steam Audio archive layouts have
   changed between releases). If not found, hard-fail with a message
   listing the archive's directory entries.
5. Extract that single entry to
   `src/SpotifyGameRadio.App/runtimes/win-x64/native/phonon.dll`,
   creating parent folders.
6. Find the archive entry matching `*/LICENSE.md` (or `*/LICENSE`) and
   extract it to `build/third-party/steam-audio-LICENSE.md`, creating
   folders. If not found, hard-fail — we must ship the license text.
7. Verify the extracted DLL's SHA256 against `$PhononDllSha256`;
   hard-fail on mismatch.
8. Print the resolved version and the target path.

**Exit codes:** 0 on success (including the no-op path), non-zero
(via `throw` / `$ErrorActionPreference = 'Stop'`) on any verification or
I/O failure.

## Component: `build/package.ps1`

**Signature:**

```
./build/package.ps1 -Version <semver> [-OutputDir build/dist] [-Configuration Release] [-SkipPhonon]
```

- `-Version` (required) — validated against
  `^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$`. Abort before any work on a
  mismatch.
- `-OutputDir` — default `build/dist`.
- `-Configuration` — default `Release`.
- `-SkipPhonon` — skip the `fetch-phonon.ps1` call (for iterating on
  packaging when the DLL is already in place).

**Flow:**

1. Validate `-Version`. Abort on failure.
2. Unless `-SkipPhonon`, invoke `build/fetch-phonon.ps1` (propagate a
   non-zero exit as a failure).
3. Remove and recreate `build/.staging`.
4. Publish:
   ```
   dotnet publish src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj `
     -c $Configuration `
     -p:Version=$Version -p:InformationalVersion=$Version `
     --nologo `
     -o build/.staging/app
   ```
   No RID; `--self-contained` left at its default (false) →
   framework-dependent output. `phonon.dll` is copied next to the exe by
   the existing `<None>` item in the csproj.
5. **Guard:** assert `build/.staging/app/phonon.dll` exists and its
   SHA256 matches `$PhononDllSha256` (read the constant from
   `fetch-phonon.ps1` or duplicate it with a comment linking the two).
   Hard-fail otherwise — this catches a broken csproj copy before a
   silent-fallback zip is ever produced.
6. Assemble the zip tree under `build/.staging/zip/`:
   - `SpotifyGameRadio/` ← full contents of `build/.staging/app`
   - `README.txt` ← `build/templates/README.txt` with `{{VERSION}}`
     replaced by `$Version`. Content covers:
     - Unzip anywhere and run `SpotifyGameRadio.exe`.
     - Prerequisite: **.NET 8 Desktop Runtime** (`https://dotnet.microsoft.com/download/dotnet/8.0`)
       — a framework-dependent app shows Windows' own "download .NET"
       prompt if it is missing, but the readme states it up front.
     - Optional: a virtual audio cable (VB-Audio Virtual Cable /
       VoiceMeeter) is only needed for the "auto-route source" feature
       (Windows 11); see `SETUP.md`.
   - `SETUP.md` ← copy of `docs/SETUP.md`
   - `THIRD-PARTY-NOTICES.txt` ← generated: a short intro paragraph
     naming Steam Audio and its MIT license, followed by the verbatim
     contents of `build/third-party/steam-audio-LICENSE.md`.
7. `Compress-Archive -Path build/.staging/zip/* -DestinationPath
   <OutputDir>/SpotifyGameRadio-v<Version>-win-x64.zip -Force`.
8. Print the final zip path, its size, and its SHA256.

**Exit codes:** 0 on success; non-zero on any step failure
(`$ErrorActionPreference = 'Stop'`).

## Component: repo changes outside `build/`

### `src/SpotifyGameRadio.App/SpotifyGameRadio.App.csproj`

- Add `<Version>0.1.0</Version>` to the existing `<PropertyGroup>` as a
  floor so plain dev builds are not `1.0.0.0`. `package.ps1` overrides it
  via `-p:Version`.
- Leave the conditional `<None Include="runtimes\win-x64\native\phonon.dll"
  Condition="Exists(...)">` item unchanged. Dev `dotnet build` still
  works without the DLL; `package.ps1` step 5 owns the "must be present"
  guarantee for releases.

### `.gitignore`

Add:

```
# Release packaging
build/.cache/
build/.staging/
build/dist/
build/third-party/

# Fetched native binary (see build/fetch-phonon.ps1)
src/SpotifyGameRadio.App/runtimes/

# Claude Code local state
.claude/
```

### `docs/SETUP.md`

Replace the manual `phonon.dll` bullet under "Requirements" with:

- **End users:** `phonon.dll` is already included in the release ZIP —
  nothing to do.
- **Developers:** run `./build/fetch-phonon.ps1` once; it downloads the
  pinned Steam Audio release, verifies it, and places the DLL. Without it
  the app runs but falls back to stereo panning instead of HRTF.

Keep the surrounding explanation of why the DLL matters and the version
requirement.

### `build/README.md`

Short doc: how to cut a release
(`./build/package.ps1 -Version 1.0.0`), one line per script, and where
the pinned hashes live / how to bump the Steam Audio version.

## Testing / verification

These are build scripts; there are no unit tests. Verification is a
manual checklist run on a clean working tree with the local
`phonon.dll` deleted:

1. `./build/fetch-phonon.ps1` → archive downloads, SHA256 verifies, DLL
   extracted to the runtimes path. A second run prints "already present"
   and performs no network I/O.
2. Truncate `build/.cache/steamaudio_4.8.1.zip` → next run hard-fails on
   the archive hash mismatch and does not extract.
3. `./build/package.ps1 -Version 0.1.0-test` → publish succeeds, the
   phonon guard passes, a zip is produced in `build/dist/`.
4. Unzip to a fresh folder and run `SpotifyGameRadio.exe` → the app
   launches and `%TEMP%\sgr-capture-debug.log` / runtime behaviour shows
   the HRTF path, not the `StereoPanSpatializer` fallback.
5. `./build/package.ps1 -Version bogus` → rejected before any build or
   download work.
6. Inspect the zip: contains `SpotifyGameRadio/` (with `phonon.dll` next
   to the exe), `README.txt` with the version substituted, `SETUP.md`,
   and `THIRD-PARTY-NOTICES.txt` containing the Steam Audio MIT license
   text.

An optional Pester test could cover the `-Version` regex only; not
planned.

## Out of scope

- GitHub Actions / CI workflow (the script is the reusable unit; add
  later).
- Authenticode code signing / SmartScreen reputation.
- Auto-update.
- An app-level `LICENSE` / EULA file — none exists in the repo today; the
  client can add one later. Only the Steam Audio notice is required for
  redistribution and is handled here.
- MSIX / Inno Setup installer.
