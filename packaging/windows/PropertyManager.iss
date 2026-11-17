; Inno Setup script for the Windows installer (Setup.exe). Built by .github/workflows/installers.yml:
;   iscc /DAppVersion=1.2.3 /DSourceDir=<published app folder> packaging\windows\PropertyManager.iss
; Installs for the current user only (no administrator password needed). The owner's records are kept in
; %LOCALAPPDATA%\Property Manager and are left alone by updates and by uninstalling.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\windows\app"
#endif

[Setup]
AppId={{6F1C2B7E-4E0D-4C59-9C1A-6B8E1D2F3A40}
AppName=Property Manager
AppVersion={#AppVersion}
AppPublisher=Property Manager
DefaultDirName={localappdata}\Programs\Property Manager
DefaultGroupName=Property Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\dist
OutputBaseFilename=PropertyManager-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Property Manager
UninstallDisplayIcon={app}\PropertyManager.Web.exe
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Put a Property Manager icon on the desktop"; GroupDescription: "Shortcuts:"
Name: "startatsignin"; Description: "Start Property Manager when I sign in to Windows (recommended)"; GroupDescription: "Starting:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Property Manager"; Filename: "{app}\PropertyManager.Web.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Property Manager"; Filename: "{app}\PropertyManager.Web.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; The same entry the app's own "Start when I sign in" switch uses (Settings → This computer).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Property Manager"; \
  ValueData: """{app}\PropertyManager.Web.exe"" --no-browser"; Tasks: startatsignin; Flags: uninsdeletevalue

[Run]
Filename: "{app}\PropertyManager.Web.exe"; Description: "Open Property Manager now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the app before its files are removed.
Filename: "{cmd}"; Parameters: "/C taskkill /IM PropertyManager.Web.exe /F"; Flags: runhidden; RunOnceId: "StopApp"

[Messages]
FinishedLabel=Property Manager is installed. It opens in your web browser; the first time, a short setup guide walks you through the rest.
