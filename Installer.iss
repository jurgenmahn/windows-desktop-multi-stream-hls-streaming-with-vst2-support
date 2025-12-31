; Inno Setup Script for Audio Processor And Streamer
; To build this installer:
; 1. Build the application in Release mode: dotnet publish -c Release -r win-x64 --self-contained
; 2. Open this file with Inno Setup Compiler
; 3. Click Build > Compile

#define MyAppName "Audio Processor And Streamer"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Audio Processor"
#define MyAppExeName "AudioProcessorAndStreamer.exe"
#define MyAppURL ""

; Path to the published output (adjust if using different publish location)
#define BuildOutput "..\bin\x64\Release\net8.0-windows\win-x64"

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
; Main application files
Source: "{#BuildOutput}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildOutput}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildOutput}\*.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist
Source: "{#BuildOutput}\*.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildOutput}\*.deps.json"; DestDir: "{app}"; Flags: ignoreversion

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
Name: "{app}\hls_output"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up generated HLS files on uninstall
Type: filesandordirs; Name: "{app}\hls_output"

