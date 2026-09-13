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

$repoRoot   = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'build/dist' }
$staging    = Join-Path $repoRoot 'build/.staging'
$appOut     = Join-Path $staging 'app'
$zipRoot    = Join-Path $staging 'zip'
$proj       = Join-Path $repoRoot 'src/LogiBuddy.App/LogiBuddy.App.csproj'
$fetch      = Join-Path $PSScriptRoot 'fetch-phonon.ps1'
$licenseIn  = Join-Path $repoRoot 'build/third-party/steam-audio-LICENSE.md'
$thirdPartyIn = Join-Path $repoRoot 'build/third-party/steam-audio-THIRDPARTY.md'
$readmeTpl  = Join-Path $PSScriptRoot 'templates/README.txt'

# Keep in sync with $PhononDllSha256 in build/fetch-phonon.ps1.
$PhononDllSha256 = 'ca3dbc01dbc24492717011e80f6a51404ca143ae344ca660971d2c983f1e058d'

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
