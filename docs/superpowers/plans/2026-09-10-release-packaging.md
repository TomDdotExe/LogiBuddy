# Release Packaging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a versioned, portable Windows ZIP of the app with `phonon.dll` already bundled, via two committed PowerShell build scripts.

**Architecture:** A new `build/` folder holds `fetch-phonon.ps1` (downloads the pinned Steam Audio release, verifies SHA256, extracts `phonon.dll` + its license) and `package.ps1` (calls fetch, runs `dotnet publish` framework-dependent, guards that the DLL landed, assembles a ZIP tree, compresses it). No installer, no CI, no code signing this pass.

**Tech Stack:** Windows PowerShell 5.1, `dotnet publish` (.NET 8, `net8.0-windows` WPF), `System.IO.Compression`, `Compress-Archive`.

**Spec:** `docs/superpowers/specs/2026-09-10-release-packaging-design.md`

**Testing note:** These are build scripts; the repo has no PowerShell test framework and adding one is out of scope (see spec "Out of scope"). Each task's verification is a documented sequence of commands with expected output — run it, observe it, then commit. There are no unit tests to write.

## Global Constraints

- **Framework-dependent publish only:** `dotnet publish` with **no `-r`/RID** and `--self-contained` left at its default (false). End users must install the .NET 8 Desktop Runtime separately.
- **Assembly version floor:** `<Version>0.1.0</Version>` in the App csproj; `package.ps1` overrides it per release via `-p:Version`.
- **Steam Audio pinned to 4.8.1.** Archive URL exactly: `https://github.com/ValveSoftware/steam-audio/releases/download/v4.8.1/steamaudio_4.8.1.zip`
- **Version argument regex:** `^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$` — reject anything else before doing work.
- **ZIP filename:** `LogiBuddy-v<Version>-win-x64.zip`, written to `build/dist/`.
- **ZIP root contents:** a `LogiBuddy/` folder (the full publish output, with `phonon.dll` next to the exe), `README.txt`, `SETUP.md`, `THIRD-PARTY-NOTICES.txt`.
- **License compliance:** the Steam Audio `LICENSE.md` text MUST be shipped in `THIRD-PARTY-NOTICES.txt`. Fetch hard-fails if it cannot extract the license.
- **Every hash comparison is lowercase-normalised and any mismatch is a hard `throw`** (no silent fallback).
- Scripts set `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`.

---

### Task 1: Repo prep — gitignore, version floor, SETUP.md

**Files:**
- Modify: `.gitignore` (append)
- Modify: `src/LogiBuddy.App/LogiBuddy.App.csproj` (add `<Version>` to the existing `<PropertyGroup>`)
- Modify: `docs/SETUP.md` (replace the manual `phonon.dll` requirement bullet)

**Interfaces:**
- Consumes: nothing.
- Produces: a `<Version>0.1.0</Version>` property later overridden by `package.ps1`; gitignore entries that keep `build/` transients and the fetched binary out of source control.

- [ ] **Step 1: Append packaging entries to `.gitignore`**

Add these lines at the end of `.gitignore`:

```gitignore

# Release packaging
build/.cache/
build/.staging/
build/dist/
build/third-party/

# Fetched native binary (see build/fetch-phonon.ps1)
src/LogiBuddy.App/runtimes/

# Claude Code local state
.claude/
```

- [ ] **Step 2: Add the version floor to the csproj**

In `src/LogiBuddy.App/LogiBuddy.App.csproj`, add a `<Version>` line to the existing `<PropertyGroup>` that already contains `<OutputType>WinExe</OutputType>`:

```xml
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <Version>0.1.0</Version>
  </PropertyGroup>
```

Leave the conditional `<None Include="runtimes\win-x64\native\phonon.dll" ...>` item exactly as it is.

- [ ] **Step 3: Rewrite the `phonon.dll` bullet in `docs/SETUP.md`**

In `docs/SETUP.md`, under `## Requirements`, replace the entire bullet that begins `- `phonon.dll` (Steam Audio native library) placed at` (it runs through `...instead of true HRTF.`) with:

```markdown
- `phonon.dll` (Steam Audio's native HRTF library, MIT licensed):
  - **End users:** it is already bundled in the release ZIP, next to the
    `.exe` — nothing to do.
  - **Developers:** run `./build/fetch-phonon.ps1` once. It downloads the
    pinned Steam Audio 4.8.1 release, verifies its checksum, and places
    the DLL at
    `src/LogiBuddy.App/runtimes/win-x64/native/phonon.dll`.
  Without the DLL the app still runs but falls back to simple stereo
  panning instead of true HRTF. The 4.x API is required — the project's
  native struct layouts were written against it.
```

- [ ] **Step 4: Verify the build still works and git is quiet**

Run:
```
dotnet build src/LogiBuddy.App/LogiBuddy.App.csproj -c Debug --nologo
```
Expected: `Build succeeded`, 0 errors. (A missing-`phonon.dll` warning is fine — the `<None>` item is conditional.)

Run:
```
git status --porcelain
```
Expected: shows `.gitignore`, the `.csproj`, and `docs/SETUP.md` as modified. Does **not** list `.claude/` or `src/LogiBuddy.App/runtimes/` any more.

- [ ] **Step 5: Commit**

```bash
git add .gitignore src/LogiBuddy.App/LogiBuddy.App.csproj docs/SETUP.md
git commit -m "$(cat <<'EOF'
build: repo prep for release packaging (gitignore, version floor, SETUP.md)

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

### Task 2: `build/fetch-phonon.ps1`

**Files:**
- Create: `build/fetch-phonon.ps1`

**Interfaces:**
- Consumes: the `.gitignore` entries from Task 1 (so its downloads/outputs stay untracked).
- Produces:
  - `src/LogiBuddy.App/runtimes/win-x64/native/phonon.dll` (the pinned build)
  - `build/third-party/steam-audio-LICENSE.md` (verbatim Steam Audio license)
  - Two pinned constants that `package.ps1` (Task 3) relies on by name: `$SteamAudioVersion` and `$PhononDllSha256`.
- Behaviour contract: exit 0 and print `phonon.dll already present ...` when the target DLL already matches `$PhononDllSha256`; otherwise download → verify archive hash → extract DLL + license → verify DLL hash; any mismatch or missing entry is a hard `throw` (non-zero exit).

- [ ] **Step 1: Write the script with placeholder hashes**

Create `build/fetch-phonon.ps1`:

```powershell
#requires -Version 5.1
<#
.SYNOPSIS
  Ensure src/LogiBuddy.App/runtimes/win-x64/native/phonon.dll is the
  pinned Steam Audio build. Idempotent; performs no network I/O once the DLL
  is in place. Also extracts Steam Audio's LICENSE.md for THIRD-PARTY-NOTICES.
.PARAMETER Force
  Re-download the archive even if a valid cached copy exists.
.PARAMETER CacheDir
  Where to cache the downloaded archive. Default: build/.cache
#>
[CmdletBinding()]
param(
    [switch] $Force,
    [string] $CacheDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Pinned constants ----------------------------------------------------
$SteamAudioVersion = '4.8.1'
$ArchiveUrl        = 'https://github.com/ValveSoftware/steam-audio/releases/download/v4.8.1/steamaudio_4.8.1.zip'
$ArchiveSha256     = 'REPLACE_ARCHIVE_SHA256'
$PhononDllSha256   = 'REPLACE_DLL_SHA256'
# ----------------------------------------------------------------------

$repoRoot   = Split-Path -Parent $PSScriptRoot
$targetDll  = Join-Path $repoRoot 'src/LogiBuddy.App/runtimes/win-x64/native/phonon.dll'
$licenseOut = Join-Path $repoRoot 'build/third-party/steam-audio-LICENSE.md'
if (-not $CacheDir) { $CacheDir = Join-Path $repoRoot 'build/.cache' }
$archivePath = Join-Path $CacheDir 'steamaudio_4.8.1.zip'

function Get-Sha256([string] $Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

# 1. Already-present no-op.
if ((Test-Path -LiteralPath $targetDll) -and
    ((Get-Sha256 $targetDll) -eq $PhononDllSha256.ToLowerInvariant())) {
    Write-Host "phonon.dll already present (Steam Audio $SteamAudioVersion)."
    return
}

# 2. Resolve or download the archive.
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
$haveValidArchive = (Test-Path -LiteralPath $archivePath) -and (-not $Force) -and
    ((Get-Sha256 $archivePath) -eq $ArchiveSha256.ToLowerInvariant())

if (-not $haveValidArchive) {
    Write-Host "Downloading Steam Audio $SteamAudioVersion ..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    Invoke-WebRequest -Uri $ArchiveUrl -OutFile $archivePath -MaximumRedirection 5 -UseBasicParsing
}

# 3. Verify the archive hash. Hard-fail (and drop the bad file) on mismatch.
$actualArchiveHash = Get-Sha256 $archivePath
if ($actualArchiveHash -ne $ArchiveSha256.ToLowerInvariant()) {
    Remove-Item -LiteralPath $archivePath -Force
    throw "Archive SHA256 mismatch.`n  expected: $ArchiveSha256`n  actual:   $actualArchiveHash`nDeleted the bad download; re-run to retry."
}

# 4-6. Extract phonon.dll and the license.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $dllEntry = $zip.Entries |
        Where-Object { $_.FullName -ilike '*/lib/windows-x64/phonon.dll' } |
        Select-Object -First 1
    if (-not $dllEntry) {
        $names = ($zip.Entries | ForEach-Object FullName) -join "`n  "
        throw "Could not find '*/lib/windows-x64/phonon.dll' in the archive. Entries:`n  $names"
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $targetDll) | Out-Null
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($dllEntry, $targetDll, $true)

    $licEntry = $zip.Entries |
        Where-Object { $_.Name -ieq 'LICENSE.md' -or $_.Name -ieq 'LICENSE' } |
        Select-Object -First 1
    if (-not $licEntry) {
        throw "No LICENSE file in the Steam Audio archive; refusing to proceed without redistributable license text."
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $licenseOut) | Out-Null
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($licEntry, $licenseOut, $true)
}
finally {
    $zip.Dispose()
}

# 7. Verify the extracted DLL.
$actualDllHash = Get-Sha256 $targetDll
if ($actualDllHash -ne $PhononDllSha256.ToLowerInvariant()) {
    throw "Extracted phonon.dll SHA256 mismatch.`n  expected: $PhononDllSha256`n  actual:   $actualDllHash"
}

Write-Host "phonon.dll (Steam Audio $SteamAudioVersion) -> $targetDll"
Write-Host "license -> $licenseOut"
```

- [ ] **Step 2: Bootstrap the real archive hash**

Run:
```
pwsh -File build/fetch-phonon.ps1
```
(or `powershell -File build/fetch-phonon.ps1`)

Expected: it downloads the archive, then throws `Archive SHA256 mismatch` printing the real `actual:` hash. Copy that hash into `$ArchiveSha256`, replacing `REPLACE_ARCHIVE_SHA256`.

- [ ] **Step 3: Bootstrap the real DLL hash**

Run the script again:
```
pwsh -File build/fetch-phonon.ps1
```
Expected: the cached archive now verifies, the DLL and license extract, then it throws `Extracted phonon.dll SHA256 mismatch` printing the real `actual:` hash. Copy that into `$PhononDllSha256`, replacing `REPLACE_DLL_SHA256`.

- [ ] **Step 4: Verify the happy path and idempotency**

Run a third time:
```
pwsh -File build/fetch-phonon.ps1
```
Expected: prints `phonon.dll (Steam Audio 4.8.1) -> ...` and `license -> ...`, exit 0.

Run a fourth time:
```
pwsh -File build/fetch-phonon.ps1
```
Expected: prints only `phonon.dll already present (Steam Audio 4.8.1).` and exits 0 — no "Downloading" line (no network I/O).

Confirm the artifacts exist:
```
Get-FileHash -Algorithm SHA256 src/LogiBuddy.App/runtimes/win-x64/native/phonon.dll
Test-Path build/third-party/steam-audio-LICENSE.md
```
Expected: hash equals `$PhononDllSha256`; `Test-Path` prints `True`.

- [ ] **Step 5: Verify corrupt-cache handling**

Run:
```
Set-Content -LiteralPath build/.cache/steamaudio_4.8.1.zip -Value 'corrupt' -NoNewline
Remove-Item src/LogiBuddy.App/runtimes/win-x64/native/phonon.dll
pwsh -File build/fetch-phonon.ps1
```
Expected: it re-downloads (cache hash no longer matches), then succeeds — OR if you also block the network, it throws `Archive SHA256 mismatch` and deletes the bad file. Either way it never extracts from a bad archive. Re-run once more to leave the DLL back in place:
```
pwsh -File build/fetch-phonon.ps1
```
Expected: `phonon.dll already present`.

- [ ] **Step 6: Commit**

```bash
git add build/fetch-phonon.ps1
git commit -m "$(cat <<'EOF'
build: add fetch-phonon.ps1 (pinned Steam Audio 4.8.1 download + verify)

Downloads steamaudio_4.8.1.zip, verifies SHA256, extracts phonon.dll to
runtimes/win-x64/native/ and LICENSE.md to build/third-party/. Idempotent.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

### Task 3: `build/package.ps1`, README template, and `build/README.md`

**Files:**
- Create: `build/package.ps1`
- Create: `build/templates/README.txt`
- Create: `build/README.md`

**Interfaces:**
- Consumes: `build/fetch-phonon.ps1` (invoked as `& $fetch`), its `$PhononDllSha256` value (duplicated here with a sync comment), `build/third-party/steam-audio-LICENSE.md`, `docs/SETUP.md`, the App csproj.
- Produces: `build/dist/LogiBuddy-v<Version>-win-x64.zip` and prints its path, size, and SHA256.

- [ ] **Step 1: Write `build/templates/README.txt`**

Create `build/templates/README.txt` (the `{{VERSION}}` token is substituted by `package.ps1`):

```
LogiBuddy v{{VERSION}}
==============================

WHAT IT IS
  Captures audio from Spotify (or any app) and plays it back spatialised,
  as if it were a radio mounted in your vehicle in-game.

REQUIREMENTS
  - Windows 10 version 20H1 (build 19041) or later.
  - .NET 8 Desktop Runtime (x64). If it is missing, Windows shows a
    download prompt on first launch. Direct link:
      https://dotnet.microsoft.com/download/dotnet/8.0
    Choose "Desktop Runtime", x64.

RUNNING
  1. Unzip this folder anywhere (for example, your Desktop).
  2. Run LogiBuddy.exe inside the LogiBuddy folder.
  3. See SETUP.md for first-time configuration.

OPTIONAL: SOURCE ROUTING
  The "auto-route source to a silent device" feature needs a virtual
  audio cable (VB-Audio Virtual Cable or VoiceMeeter) and Windows 11.
  It is only needed to stop hearing the raw source alongside the
  processed radio. See SETUP.md.

SPATIAL AUDIO
  phonon.dll (Steam Audio, MIT licensed) is bundled next to the exe and
  provides true HRTF spatialisation. See THIRD-PARTY-NOTICES.txt.
```

- [ ] **Step 2: Write `build/package.ps1`**

Create `build/package.ps1`:

```powershell
#requires -Version 5.1
<#
.SYNOPSIS
  Build a portable, framework-dependent release ZIP with phonon.dll bundled.
.EXAMPLE
  ./build/package.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutputDir,
    [string] $Configuration = 'Release',
    [switch] $SkipPhonon
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid -Version '$Version'. Expected semver, e.g. 1.0.0 or 1.0.0-rc1."
}

$repoRoot  = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build/dist' }
$staging   = Join-Path $repoRoot 'build/.staging'
$appOut    = Join-Path $staging 'app'
$zipRoot   = Join-Path $staging 'zip'
$proj      = Join-Path $repoRoot 'src/LogiBuddy.App/LogiBuddy.App.csproj'
$fetch     = Join-Path $PSScriptRoot 'fetch-phonon.ps1'
$licenseIn = Join-Path $repoRoot 'build/third-party/steam-audio-LICENSE.md'
$readmeTpl = Join-Path $PSScriptRoot 'templates/README.txt'

# Keep in sync with $PhononDllSha256 in build/fetch-phonon.ps1.
$PhononDllSha256 = 'REPLACE_DLL_SHA256'

function Get-Sha256([string] $Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

if (-not $SkipPhonon) {
    Write-Host "== Fetching phonon.dll =="
    & $fetch
}

Write-Host "== Publishing ($Configuration, v$Version) =="
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $appOut | Out-Null

dotnet publish $proj -c $Configuration -p:Version=$Version -p:InformationalVersion=$Version --nologo -o $appOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# Guard: phonon.dll present and correct next to the exe.
$publishedDll = Join-Path $appOut 'phonon.dll'
if (-not (Test-Path -LiteralPath $publishedDll)) {
    throw "phonon.dll is missing from the publish output ($publishedDll). The csproj <None> copy may be broken."
}
$dllHash = Get-Sha256 $publishedDll
if ($dllHash -ne $PhononDllSha256.ToLowerInvariant()) {
    throw "Published phonon.dll SHA256 mismatch.`n  expected: $PhononDllSha256`n  actual:   $dllHash"
}

Write-Host "== Assembling ZIP tree =="
New-Item -ItemType Directory -Force -Path (Join-Path $zipRoot 'LogiBuddy') | Out-Null
Copy-Item -Path (Join-Path $appOut '*') -Destination (Join-Path $zipRoot 'LogiBuddy') -Recurse

(Get-Content -LiteralPath $readmeTpl -Raw).Replace('{{VERSION}}', $Version) |
    Set-Content -LiteralPath (Join-Path $zipRoot 'README.txt') -Encoding UTF8

Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/SETUP.md') -Destination (Join-Path $zipRoot 'SETUP.md')

if (-not (Test-Path -LiteralPath $licenseIn)) {
    throw "Missing $licenseIn. Run build/fetch-phonon.ps1 first (drop -SkipPhonon)."
}
$notice = @"
This product bundles phonon.dll from Steam Audio
(https://valvesoftware.github.io/steam-audio/), licensed under the MIT
License. The full license text follows.

"@ + (Get-Content -LiteralPath $licenseIn -Raw)
$notice | Set-Content -LiteralPath (Join-Path $zipRoot 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8

Write-Host "== Compressing =="
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$zipPath = Join-Path $OutputDir "LogiBuddy-v$Version-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $zipRoot '*') -DestinationPath $zipPath

$sizeMB = '{0:N1} MB' -f ((Get-Item -LiteralPath $zipPath).Length / 1MB)
Write-Host ""
Write-Host "Built:  $zipPath"
Write-Host "Size:   $sizeMB"
Write-Host "SHA256: $(Get-Sha256 $zipPath)"
```

- [ ] **Step 3: Fill in the duplicated hash**

Copy the final `$PhononDllSha256` value from `build/fetch-phonon.ps1` into the `$PhononDllSha256` line in `build/package.ps1`, replacing `REPLACE_DLL_SHA256`.

- [ ] **Step 4: Write `build/README.md`**

Create `build/README.md`:

```markdown
# build/

Release packaging for LogiBuddy.

## Cutting a release

    ./build/package.ps1 -Version 1.0.0

Produces `build/dist/LogiBuddy-v1.0.0-win-x64.zip` (portable,
framework-dependent) and prints the zip's size and SHA256.

## Scripts

| Script | Does |
| --- | --- |
| `fetch-phonon.ps1` | Downloads the pinned Steam Audio release, verifies its SHA256, extracts `phonon.dll` to `src/LogiBuddy.App/runtimes/win-x64/native/` and `LICENSE.md` to `build/third-party/`. Idempotent. |
| `package.ps1` | Runs `fetch-phonon.ps1`, `dotnet publish` (framework-dependent, no RID), asserts `phonon.dll` is in the output, assembles the ZIP tree (`LogiBuddy/`, `README.txt`, `SETUP.md`, `THIRD-PARTY-NOTICES.txt`), compresses it. |

`package.ps1 -SkipPhonon` skips the fetch when the DLL is already in place.

## Bumping the Steam Audio version

Edit `$SteamAudioVersion`, `$ArchiveUrl`, `$ArchiveSha256`, and
`$PhononDllSha256` at the top of `fetch-phonon.ps1`, plus the duplicated
`$PhononDllSha256` in `package.ps1`. To get the new hashes, set them to a
dummy value and run `fetch-phonon.ps1` twice — it prints the real
`actual:` hash each time it fails to match.

## Ignored paths

`build/.cache/`, `build/.staging/`, `build/dist/`, `build/third-party/`,
and `src/LogiBuddy.App/runtimes/` are all gitignored — the fetched
binary and build transients are never committed.
```

- [ ] **Step 5: Verify version rejection**

Run:
```
pwsh -File build/package.ps1 -Version bogus
```
Expected: throws `Invalid -Version 'bogus'` immediately, no publish, no download.

- [ ] **Step 6: Verify a full package run**

Run:
```
pwsh -File build/package.ps1 -Version 0.1.0-test
```
Expected: prints `== Fetching phonon.dll ==` (→ `already present`), `== Publishing ==`, `== Assembling ZIP tree ==`, `== Compressing ==`, then `Built:`, `Size:`, `SHA256:` lines. Exit 0. A `build/dist/LogiBuddy-v0.1.0-test-win-x64.zip` file exists.

- [ ] **Step 7: Inspect the ZIP contents**

Run:
```
Expand-Archive -Path build/dist/LogiBuddy-v0.1.0-test-win-x64.zip -DestinationPath build/.staging/verify -Force
Get-ChildItem build/.staging/verify
Get-ChildItem build/.staging/verify/LogiBuddy/phonon.dll
Select-String -Path build/.staging/verify/README.txt -Pattern 'v0.1.0-test' -SimpleMatch
Select-String -Path build/.staging/verify/THIRD-PARTY-NOTICES.txt -Pattern 'MIT' -SimpleMatch
```
Expected: the verify folder holds `LogiBuddy/`, `README.txt`, `SETUP.md`, `THIRD-PARTY-NOTICES.txt`. `phonon.dll` exists inside `LogiBuddy/`. `README.txt` contains `v0.1.0-test`. `THIRD-PARTY-NOTICES.txt` contains the word `MIT` (license text present).

- [ ] **Step 8: Smoke-test the unzipped app**

Run:
```
build/.staging/verify/LogiBuddy/LogiBuddy.exe
```
(Have Spotify or a browser playing audio first.) Expected: the window opens; after selecting a source and clicking Start, `%TEMP%\sgr-capture-debug.log` shows a `capture started` line and spatialisation uses HRTF — the log does **not** report the `StereoPanSpatializer` panning fallback. Close the app.

If .NET 8 Desktop Runtime is not installed on the test machine, expect the Windows "download .NET" dialog instead — that is the documented framework-dependent behaviour, not a packaging bug.

- [ ] **Step 9: Clean the throwaway output and commit**

```bash
rm -rf "build/.staging" "build/dist"
git add build/package.ps1 build/templates/README.txt build/README.md
git commit -m "$(cat <<'EOF'
build: add package.ps1 — portable release zip with bundled phonon.dll

Runs fetch-phonon.ps1, publishes framework-dependent, guards the phonon.dll
copy, assembles README.txt/SETUP.md/THIRD-PARTY-NOTICES.txt, and zips to
build/dist/LogiBuddy-v<Version>-win-x64.zip.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019E1KP9g2hiXfAZMVNTrXUN
EOF
)"
```

---

## Self-Review

**Spec coverage:**

| Spec element | Task |
| --- | --- |
| Portable ZIP, unzip-and-run | Task 3 (Compress-Archive, zip tree) |
| Framework-dependent publish | Task 3 Step 2 (`dotnet publish`, no RID); Global Constraints |
| Build-time download of phonon.dll w/ pinned URL + SHA256 | Task 2 |
| Version as script argument + regex validation | Task 3 Step 2 |
| `fetch-phonon.ps1` pinned constants, params, 8-step flow | Task 2 Step 1 |
| Already-present no-op | Task 2 Step 1 (branch 1), verified Task 2 Step 4 |
| Archive hash hard-fail + delete bad file | Task 2 Step 1 (branch 3), verified Task 2 Step 5 |
| Search for `*/lib/windows-x64/phonon.dll` (no hard-coded top folder) | Task 2 Step 1 (`-ilike '*/lib/windows-x64/phonon.dll'`) |
| Extract + ship Steam Audio LICENSE | Task 2 Step 1 (licEntry, hard-fail if absent); Task 3 Step 2 (THIRD-PARTY-NOTICES.txt) |
| `package.ps1` signature incl. `-SkipPhonon` | Task 3 Step 2 |
| Publish guard: phonon.dll present + hash match | Task 3 Step 2 |
| ZIP tree: `LogiBuddy/`, README.txt, SETUP.md, THIRD-PARTY-NOTICES.txt | Task 3 Step 2, verified Step 7 |
| README.txt `{{VERSION}}` substitution + content | Task 3 Steps 1-2 |
| Print zip path/size/SHA256 | Task 3 Step 2 |
| csproj `<Version>0.1.0</Version>` floor | Task 1 Step 2 |
| csproj `<None>` left unchanged | Task 1 Step 2 |
| `.gitignore` additions incl. `.claude/` and `runtimes/` | Task 1 Step 1 |
| `docs/SETUP.md` rewrite | Task 1 Step 3 |
| `build/README.md` | Task 3 Step 4 |
| Manual verification checklist (6 items) | Task 2 Steps 4-5, Task 3 Steps 5-8 |
| Out of scope: CI, signing, auto-update, app LICENSE, installer | Not implemented (correct) |

No gaps.

**Placeholder scan:** `REPLACE_ARCHIVE_SHA256` / `REPLACE_DLL_SHA256` are intentional — Task 2 Steps 2-3 and Task 3 Step 3 are explicit bootstrap steps that replace them with values captured from a real download. Not plan placeholders.

**Type/name consistency:** `$SteamAudioVersion`, `$ArchiveUrl`, `$ArchiveSha256`, `$PhononDllSha256`, `Get-Sha256`, `$targetDll`, `$licenseOut` consistent across both scripts. `$PhononDllSha256` is deliberately duplicated in `package.ps1` with a "keep in sync" comment (Task 3 interface block notes this). ZIP name `LogiBuddy-v<Version>-win-x64.zip` identical in Global Constraints, Task 3 Step 2, and verification steps. Zip-root folder name `LogiBuddy/` consistent.
