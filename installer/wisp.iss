; Inno Setup script for Wisp — builds WispSetup.exe (a friendly Windows installer).
;
; Build the payload first, then compile:
;   dotnet publish Wisp/Wisp.csproj -c Release -r win-x64 --self-contained true ^
;     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
;     -p:EnableCompressionInSingleFile=true -o <payload>
;   ISCC /DSourceDir=<payload> /DIconFile=Wisp/wisp.ico /DOutDir=<out> installer/wisp.iss
;
; The payload is self-contained (bundles .NET 8), so end users need NO .NET install.

#ifndef SourceDir
  #define SourceDir "..\dist"
#endif
#ifndef IconFile
  #define IconFile "..\Wisp\wisp.ico"
#endif
#ifndef OutDir
  #define OutDir "."
#endif
#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif

#define MyAppName "Wisp"
#define MyAppPublisher "izoose"
#define MyAppURL "https://github.com/izoose/wisp-browser"
#define MyAppExeName "Wisp.exe"

[Setup]
AppId={{7B2D9E14-4C3A-4E9B-9A6E-0C7F1D2E8A55}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=WispSetup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Resources\*"; DestDir: "{app}\Resources"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Launch after install — including silent (auto-update) installs, so the app relaunches itself.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Wisp"; Flags: nowait postinstall
