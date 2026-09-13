#requires -Version 5.1
<#
.SYNOPSIS
  Build the LogiBuddy Windows installer (Inno Setup), alongside the
  portable zip from package.ps1.
.EXAMPLE
  ./build/make-installer.ps1 -Version 1.0.0
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

$repoRoot     = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build/dist' }
$staging      = Join-Path $repoRoot 'build/.staging-installer'
$appOut       = Join-Path $staging 'app'
$proj         = Join-Path $repoRoot 'src/LogiBuddy.App/LogiBuddy.App.csproj'
$fetch        = Join-Path $PSScriptRoot 'fetch-phonon.ps1'
$licenseIn    = Join-Path $repoRoot 'build/third-party/steam-audio-LICENSE.md'
$thirdPartyIn = Join-Path $repoRoot 'build/third-party/steam-audio-THIRDPARTY.md'
$readmeTpl    = Join-Path $PSScriptRoot 'templates/README.txt'
$issFile      = Join-Path $PSScriptRoot 'installer.iss'
$logoIn       = Join-Path $repoRoot 'Images/Logo.png'
$wizardSmallBmp = Join-Path $staging 'wizard-small.bmp'

# Keep in sync with $PhononDllSha256 in build/fetch-phonon.ps1 and package.ps1.
$PhononDllSha256 = 'ca3dbc01dbc24492717011e80f6a51404ca143ae344ca660971d2c983f1e058d'

function Get-Sha256([string] $Path) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Find-Iscc {
    $isccCmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $(if ($isccCmd) { $isccCmd.Source })
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    $candidates = @($candidates)
    if ($candidates.Count -gt 0) { return $candidates[0] }
    throw "ISCC.exe (Inno Setup compiler) not found. Install it: winget install JRSoftware.InnoSetup"
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

$publishedDll = Join-Path $appOut 'phonon.dll'
if (-not (Test-Path -LiteralPath $publishedDll)) {
    throw "phonon.dll is missing from the publish output ($publishedDll). The csproj <None> copy may be broken."
}
$dllHash = Get-Sha256 $publishedDll
if ($dllHash -ne $PhononDllSha256.ToLowerInvariant()) {
    throw "Published phonon.dll SHA256 mismatch.`n  expected: $PhononDllSha256`n  actual:   $dllHash"
}

Write-Host "== Assembling install tree extras =="
(Get-Content -LiteralPath $readmeTpl -Raw).Replace('{{VERSION}}', $Version) |
    Set-Content -LiteralPath (Join-Path $appOut 'README.txt') -Encoding UTF8

Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/SETUP.md') -Destination (Join-Path $appOut 'SETUP.md')

foreach ($p in @($licenseIn, $thirdPartyIn)) {
    if (-not (Test-Path -LiteralPath $p)) {
        throw "Missing $p. Run build/fetch-phonon.ps1 first (drop -SkipPhonon)."
    }
}
$notice = @"
THIRD-PARTY NOTICES
===================

This product bundles phonon.dll from Steam Audio
(https://valvesoftware.github.io/steam-audio/), which is licensed under
the Apache License, Version 2.0. The full license text is in section A
below. phonon.dll also statically links the libraries listed in section
B, each under its own license.

This product also bundles the Whisper.net managed library and the native
whisper.cpp/ggml runtime binaries it wraps, both licensed under the MIT
License. The full license text is in section C below.


================================================================================
A. Steam Audio - Apache License 2.0
================================================================================

"@ + (Get-Content -LiteralPath $licenseIn -Raw) + @"


================================================================================
B. Libraries linked into phonon.dll
================================================================================

"@ + (Get-Content -LiteralPath $thirdPartyIn -Raw) + @"


================================================================================
C. Whisper.net / whisper.cpp / ggml - MIT License
================================================================================

Whisper.net
https://github.com/sandrohanea/whisper.net

MIT License

Copyright (c) Whisper.net contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.


whisper.cpp / ggml
https://github.com/ggml-org/whisper.cpp

MIT License

Copyright (c) whisper.cpp/ggml and Whisper.net contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@
$notice | Set-Content -LiteralPath (Join-Path $appOut 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8

Write-Host "== Rendering wizard small image from Images/Logo.png =="
if (-not (Test-Path -LiteralPath $logoIn)) {
    throw "Missing $logoIn."
}
Add-Type -AssemblyName System.Drawing
# Inno Setup's WizardSmallImageFile requires a .bmp (no PNG/JPG support), and
# the recommended size for WizardStyle=modern is 100x100 — Logo.png is
# already square, so a straight high-quality resize needs no letterboxing.
$sourceImage = [System.Drawing.Image]::FromFile($logoIn)
try {
    $target = New-Object System.Drawing.Bitmap 100, 100
    $graphics = [System.Drawing.Graphics]::FromImage($target)
    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($sourceImage, 0, 0, 100, 100)
    } finally { $graphics.Dispose() }
    $target.Save($wizardSmallBmp, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $target.Dispose()
} finally { $sourceImage.Dispose() }

Write-Host "== Compiling installer (Inno Setup) =="
$iscc = Find-Iscc
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
& $iscc "/DAppVersion=$Version" "/DSourceDir=$appOut" "/DWizardSmallImage=$wizardSmallBmp" $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed (exit $LASTEXITCODE)." }

$setupPath = Join-Path $OutputDir "LogiBuddy-v$Version-win-x64-setup.exe"
$sizeMB = '{0:N1} MB' -f ((Get-Item -LiteralPath $setupPath).Length / 1MB)
Write-Host ""
Write-Host "Built:  $setupPath"
Write-Host "Size:   $sizeMB"
Write-Host "SHA256: $(Get-Sha256 $setupPath)"
