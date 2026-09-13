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
  - A VIRTUAL AUDIO CABLE (VB-Audio Virtual Cable or VoiceMeeter) --
    REQUIRED, not optional. See below.

RUNNING
  1. Unzip this folder anywhere (for example, your Desktop).
  2. Run LogiBuddy.exe inside the LogiBuddy folder.
  3. See SETUP.md for first-time configuration.

IMPORTANT: SOURCE ROUTING (VIRTUAL AUDIO CABLE)
  Without this set up, you will hear the raw source AND the processed
  radio playing at once, overlapping -- muting the source instead does
  NOT work, since LogiBuddy captures that same audio to build the radio,
  so muting it silences the radio too. Routing the source's output to a
  virtual audio cable is the only way to hear just the processed radio.
  LogiBuddy's own "auto-route source to a silent device" automates
  finding and using the cable, but that automation needs Windows 11; on
  Windows 10, route the source manually in Windows Settings > System >
  Sound > "App volume and device preferences" instead. See SETUP.md.

SPATIAL AUDIO
  phonon.dll (Steam Audio, Apache License 2.0) is bundled next to the
  exe and provides true HRTF spatialisation. See THIRD-PARTY-NOTICES.txt.
