#requires -Version 5.1
<#
.SYNOPSIS
  Ensure src/LogiBuddy.App/runtimes/win-x64/native/phonon.dll is the
  pinned Steam Audio build, and fetch the license texts needed to
  redistribute it. Idempotent; performs no network I/O once everything is
  in place.

  Steam Audio 4.8.1 is licensed under Apache License 2.0. Its binary
  release archive ships no license file, so LICENSE.md is downloaded from
  the pinned git tag; THIRDPARTY.md (notices for libraries statically
  linked into phonon.dll) is extracted from the archive.
.PARAMETER Force
  Re-download the archive and LICENSE.md even if valid local copies exist.
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

# --- Pinned constants --------------------------------------------------------
$SteamAudioVersion = '4.8.1'
$ArchiveUrl        = 'https://github.com/ValveSoftware/steam-audio/releases/download/v4.8.1/steamaudio_4.8.1.zip'
$ArchiveSha256     = '4a0aa5ec1176f38f0b0993a37c2259d9e86f27e22d5e24f83ec4c3cb9a1d5449'
$PhononDllSha256   = 'ca3dbc01dbc24492717011e80f6a51404ca143ae344ca660971d2c983f1e058d'
$LicenseUrl        = 'https://raw.githubusercontent.com/ValveSoftware/steam-audio/v4.8.1/LICENSE.md'
$LicenseSha256     = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
# --------------------------------------------------------------------------

$repoRoot     = Split-Path -Parent $PSScriptRoot
$targetDll    = Join-Path $repoRoot 'src/LogiBuddy.App/runtimes/win-x64/native/phonon.dll'
$licenseOut   = Join-Path $repoRoot 'build/third-party/steam-audio-LICENSE.md'
$thirdPartyOut = Join-Path $repoRoot 'build/third-party/steam-audio-THIRDPARTY.md'
if (-not $CacheDir) { $CacheDir = Join-Path $repoRoot 'build/.cache' }
$archivePath = Join-Path $CacheDir 'steamaudio_4.8.1.zip'

function Get-Sha256([string] $Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}
function Test-Sha256([string] $Path, [string] $Expected) {
    (Test-Path -LiteralPath $Path) -and ((Get-Sha256 $Path) -eq $Expected.ToLowerInvariant())
}

# 1. Fast no-op: DLL + both license texts already in place and correct.
if (-not $Force -and
    (Test-Sha256 $targetDll $PhononDllSha256) -and
    (Test-Sha256 $licenseOut $LicenseSha256) -and
    (Test-Path -LiteralPath $thirdPartyOut)) {
    Write-Host "phonon.dll and license texts already present (Steam Audio $SteamAudioVersion)."
    return
}

New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $licenseOut) | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# 2. Resolve or download the release archive.
if ($Force -or -not (Test-Sha256 $archivePath $ArchiveSha256)) {
    Write-Host "Downloading Steam Audio $SteamAudioVersion archive ..."
    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    Invoke-WebRequest -Uri $ArchiveUrl -OutFile $archivePath -MaximumRedirection 5 -UseBasicParsing
}

# 3. Verify the archive hash. Hard-fail (and drop the bad file) on mismatch.
$actualArchiveHash = Get-Sha256 $archivePath
if ($actualArchiveHash -ne $ArchiveSha256.ToLowerInvariant()) {
    Remove-Item -LiteralPath $archivePath -Force
    throw "Archive SHA256 mismatch.`n  expected: $ArchiveSha256`n  actual:   $actualArchiveHash`nDeleted the bad download; re-run to retry."
}

# 4. Extract phonon.dll and THIRDPARTY.md from the archive.
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

    $tpEntry = $zip.Entries |
        Where-Object { $_.Name -ieq 'THIRDPARTY.md' } |
        Select-Object -First 1
    if (-not $tpEntry) {
        throw "No THIRDPARTY.md in the Steam Audio archive; refusing to proceed without the bundled-library notices."
    }
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($tpEntry, $thirdPartyOut, $true)
}
finally {
    $zip.Dispose()
}

# 5. Verify the extracted DLL.
$actualDllHash = Get-Sha256 $targetDll
if ($actualDllHash -ne $PhononDllSha256.ToLowerInvariant()) {
    throw "Extracted phonon.dll SHA256 mismatch.`n  expected: $PhononDllSha256`n  actual:   $actualDllHash"
}

# 6. Download and verify the Apache-2.0 LICENSE.md from the pinned tag.
if ($Force -or -not (Test-Sha256 $licenseOut $LicenseSha256)) {
    Write-Host "Downloading Steam Audio LICENSE.md (Apache-2.0) ..."
    Invoke-WebRequest -Uri $LicenseUrl -OutFile $licenseOut -MaximumRedirection 5 -UseBasicParsing
}
$actualLicenseHash = Get-Sha256 $licenseOut
if ($actualLicenseHash -ne $LicenseSha256.ToLowerInvariant()) {
    Remove-Item -LiteralPath $licenseOut -Force
    throw "LICENSE.md SHA256 mismatch.`n  expected: $LicenseSha256`n  actual:   $actualLicenseHash`nDeleted it; re-run to retry."
}

Write-Host "phonon.dll (Steam Audio $SteamAudioVersion) -> $targetDll"
Write-Host "LICENSE.md (Apache-2.0)                     -> $licenseOut"
Write-Host "THIRDPARTY.md                               -> $thirdPartyOut"
