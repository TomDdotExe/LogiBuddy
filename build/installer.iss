; LogiBuddy installer. Built by build/make-installer.ps1, which stages a
; complete install tree (exe, native libs, README.txt, SETUP.md,
; THIRD-PARTY-NOTICES.txt) and passes it in via -DSourceDir, along with
; -DAppVersion and -DWizardSmallImage (a generated bmp from Images/Logo.png,
; since Inno's wizard image keys require .bmp, not .png). Per-user install
; (no admin/UAC needed) under %LocalAppData%\Programs.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\.staging-installer\app"
#endif
#ifndef WizardSmallImage
  #define WizardSmallImage "..\build\.staging-installer\wizard-small.bmp"
#endif

[Setup]
AppId={{124C6C65-C55D-40BD-A422-6ADC7D9B26BC}
AppName=LogiBuddy
AppVersion={#AppVersion}
AppPublisher=TomDdotExe
AppPublisherURL=https://github.com/TomDdotExe/LogiBuddy
AppSupportURL=https://github.com/TomDdotExe/LogiBuddy/issues
DefaultDirName={localappdata}\Programs\LogiBuddy
DefaultGroupName=LogiBuddy
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
OutputDir=..\build\dist
OutputBaseFilename=LogiBuddy-v{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Images\logibuddy-light.ico
WizardSmallImageFile={#WizardSmallImage}
UninstallDisplayIcon={app}\LogiBuddy.exe
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\LogiBuddy"; Filename: "{app}\LogiBuddy.exe"
Name: "{group}\Uninstall LogiBuddy"; Filename: "{uninstallexe}"
Name: "{userdesktop}\LogiBuddy"; Filename: "{app}\LogiBuddy.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LogiBuddy.exe"; Description: "Launch LogiBuddy"; Flags: nowait postinstall skipifsilent
