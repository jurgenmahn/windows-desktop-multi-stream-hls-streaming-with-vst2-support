; Inno Setup Script for Audio Processor And Streamer
; To build this installer, run: build-installer.bat
; Or manually:
;   1. dotnet publish -c Release -p:Platform=x64
;   2. Open this file with Inno Setup Compiler and press F9

#define MyAppName "Audio Processor And Streamer"
#define MyAppVersion "0.9.17"
#define MyAppPublisher "Jurgen Mahn - 9Yards"
#define MyAppExeName "AudioProcessorAndStreamer.exe"
#define MyAppURL "https://github.com/jurgenmahn/windows-desktop-multi-stream-hls-streaming-with-vst2-support"

; Path to the publish output (self-contained single-file)
#define BuildOutput "bin\x64\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=InstallerOutput
OutputBaseFilename=AudioProcessorAndStreamer-Setup-{#MyAppVersion}
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\Assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main application (single-file exe with .NET runtime bundled)
Source: "{#BuildOutput}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; Native DLLs that can't be bundled (if any exist)
Source: "{#BuildOutput}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; Config file - don't overwrite if user modified it
Source: "{#BuildOutput}\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist

; FFmpeg folder
Source: "{#BuildOutput}\FFmpeg\*"; DestDir: "{app}\FFmpeg"; Flags: ignoreversion recursesubdirs createallsubdirs

; Plugins folder (VST plugins)
Source: "{#BuildOutput}\Plugins\*"; DestDir: "{app}\Plugins"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

; Assets folder
Source: "{#BuildOutput}\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

; Presets folder
Source: "{#BuildOutput}\Presets\*"; DestDir: "{app}\Presets"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Dirs]
; Create folders even if empty (for user content)
Name: "{app}\Plugins"
Name: "{app}\Presets"
Name: "{app}\Assets"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up user data folder on uninstall (optional - contains config and HLS output)
Type: filesandordirs; Name: "{localappdata}\AudioProcessorAndStreamer"

