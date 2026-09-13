# build/

Release packaging for LogiBuddy.

## Cutting a release

    ./build/package.ps1 -Version 1.0.0
    ./build/make-installer.ps1 -Version 1.0.0

Produces `build/dist/LogiBuddy-v1.0.0-win-x64.zip` (portable,
framework-dependent — the end user needs the .NET 8 Desktop Runtime) and
`build/dist/LogiBuddy-v1.0.0-win-x64-setup.exe` (installer, same
requirements), and prints each artifact's size and SHA256.

## Scripts

| Script | Does |
| --- | --- |
| `fetch-phonon.ps1` | Downloads the pinned Steam Audio 4.8.1 release, verifies its SHA256, extracts `phonon.dll` to `src/LogiBuddy.App/runtimes/win-x64/native/` and `THIRDPARTY.md` to `build/third-party/`, and downloads the Apache-2.0 `LICENSE.md` from the pinned git tag (the binary archive ships no license file). All three artifacts are hash-pinned. Idempotent. |
| `package.ps1` | Runs `fetch-phonon.ps1`, `dotnet publish` (framework-dependent, no RID), asserts `phonon.dll` landed in the output and matches the pinned hash, assembles the ZIP tree (`LogiBuddy/`, `README.txt`, `SETUP.md`, `THIRD-PARTY-NOTICES.txt`), compresses it. |
| `make-installer.ps1` | Runs `fetch-phonon.ps1` and its own `dotnet publish` (kept separate from `package.ps1`'s staging so the two can run independently), assembles the same `README.txt`/`SETUP.md`/`THIRD-PARTY-NOTICES.txt` alongside the publish output, then compiles `installer.iss` via Inno Setup's `ISCC.exe` into a single setup `.exe`. |
| `installer.iss` | Inno Setup script for the installer: per-user install under `%LocalAppData%\Programs\LogiBuddy` (no admin/UAC required), Start Menu shortcut, optional desktop shortcut, standard uninstaller. Takes `AppVersion`/`SourceDir` from `make-installer.ps1` via `/D` defines. |

Both `package.ps1` and `make-installer.ps1` accept `-SkipPhonon` to skip
the fetch when the DLL and license texts are already in place.
`make-installer.ps1` requires the Inno Setup compiler
(`winget install JRSoftware.InnoSetup`) — a dev-machine build tool, not
an end-user dependency.

## Licensing

Steam Audio 4.8.1 is under **Apache License 2.0**. The release zip's
`THIRD-PARTY-NOTICES.txt` carries the full Apache-2.0 text plus Steam
Audio's `THIRDPARTY.md` (notices for IPP, FFTS, and other libraries
statically linked into `phonon.dll`). Redistribution is permitted; no
code changes were made to the binary.

## Bumping the Steam Audio version

Edit the pinned constants at the top of `fetch-phonon.ps1`:
`$SteamAudioVersion`, `$ArchiveUrl`, `$ArchiveSha256`, `$PhononDllSha256`,
`$LicenseUrl`, `$LicenseSha256` — plus the duplicated `$PhononDllSha256`
in `package.ps1`. To get the new hashes, set each to a dummy value and
run `fetch-phonon.ps1`; it prints the real `actual:` hash each time a
check fails. Also re-check the license: a future Steam Audio release
could change it.

## Ignored paths

`build/.cache/`, `build/.staging/`, `build/.staging-installer/`,
`build/dist/`, `build/third-party/`, and `src/LogiBuddy.App/runtimes/`
are all gitignored — the fetched binary and build transients are never
committed.
