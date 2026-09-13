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
  phonon.dll (Steam Audio, Apache License 2.0) is bundled next to the
  exe and provides true HRTF spatialisation. See THIRD-PARTY-NOTICES.txt.
