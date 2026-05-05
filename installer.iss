; Inno Setup script for Classic Radio
; Compile: ISCC.exe installer.iss
; Output: dist\ClassicRadio-Setup-win-x64.exe

#define AppName        "Classic Radio"
#define AppVersion     "1.0.2"
#define AppPublisher   "Sarta"
#define AppExeName     "ClassicRadio.exe"
#define PublishDir     "bin\Release\net8.0-windows\win-x64\publish"

[Setup]
; Stable AppId — DO NOT change between releases (uniquely identifies the app for upgrades).
AppId={{A6F2D8E7-3C4B-4F2A-9E1D-C7B5A8F6D2E1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir=dist
OutputBaseFilename=ClassicRadio-Setup-win-x64
SetupIconFile=radio-win-logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Main app + everything from publish folder (LibVLC native, .NET runtime, plugins, stations.json, etc.)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; Note: per-user data at %APPDATA%\ClassicRadio (custom stations, state.json) is
; intentionally preserved on uninstall. Users who want a clean wipe can delete
; that folder manually.
