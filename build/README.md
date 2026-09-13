# build/

Release packaging for LogiBuddy.

## Cutting a release

    ./build/package.ps1 -Version 1.0.0

Produces `build/dist/LogiBuddy-v1.0.0-win-x64.zip` (portable,
framework-dependent — the end user needs the .NET 8 Desktop Runtime) and
prints the zip's size and SHA256.

## Scripts

| Script | Does |
| --- | --- |
| `fetch-phonon.ps1` | Downloads the pinned Steam Audio 4.8.1 release, verifies its SHA256, extracts `phonon.dll` to `src/LogiBuddy.App/runtimes/win-x64/native/` and `THIRDPARTY.md` to `build/third-party/`, and downloads the Apache-2.0 `LICENSE.md` from the pinned git tag (the binary archive ships no license file). All three artifacts are hash-pinned. Idempotent. |
| `package.ps1` | Runs `fetch-phonon.ps1`, `dotnet publish` (framework-dependent, no RID), asserts `phonon.dll` landed in the output and matches the pinned hash, assembles the ZIP tree (`LogiBuddy/`, `README.txt`, `SETUP.md`, `THIRD-PARTY-NOTICES.txt`), compresses it. |

`package.ps1 -SkipPhonon` skips the fetch when the DLL and license texts
are already in place.

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

`build/.cache/`, `build/.staging/`, `build/dist/`, `build/third-party/`,
and `src/LogiBuddy.App/runtimes/` are all gitignored — the fetched
binary and build transients are never committed.
