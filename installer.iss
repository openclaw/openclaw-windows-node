; OpenClaw Tray Inno Setup Script (WinUI version)
#define MyAppName "OpenClaw Tray"
#define MyAppPublisher "Scott Hanselman"
#define MyAppURL "https://github.com/openclaw/openclaw-windows-node"
#define MyAppExeName "OpenClaw.Tray.WinUI.exe"

; MyAppArch should be passed via /DMyAppArch=x64 or /DMyAppArch=arm64
#ifndef MyAppArch
  #define MyAppArch "x64"
#endif

[Setup]
AppId={{M0LTB0T-TRAY-4PP1-D3N7}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://github.com/openclaw/openclaw-windows-node/issues
AppUpdatesURL=https://github.com/openclaw/openclaw-windows-node/releases
DefaultDirName={localappdata}\OpenClawTray
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=OpenClawTray-Setup-{#MyAppArch}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
SetupIconFile=src\OpenClaw.Tray.WinUI\Assets\openclaw.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
#if MyAppArch == "arm64"
ArchitecturesInstallIn64BitMode=arm64
ArchitecturesAllowed=arm64
#else
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; publish folder should be passed via /Dpublish=publish-x64 or /Dpublish=publish-arm64
#ifndef publish
  #define publish "publish"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start OpenClaw Tray when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked
Name: "cmdpalette"; Description: "Install PowerToys Command Palette extension"; GroupDescription: "Integrations:"; Flags: unchecked

[Files]
; WinUI Tray app - include all files (WinUI needs DLLs, not single-file)
Source: "{#publish}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; Command Palette extension (all files from build output)
Source: "{#publish}\cmdpal\*"; DestDir: "{app}\CommandPalette"; Flags: ignoreversion recursesubdirs; Tasks: cmdpalette

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Register Command Palette extension (silently, only if task selected)
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -Command ""Add-AppxPackage -Register '{app}\CommandPalette\AppxManifest.xml' -ForceApplicationShutdown"""; Flags: runhidden; Tasks: cmdpalette

[UninstallRun]
; Unregister Command Palette extension on uninstall
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name '*OpenClaw*' | Remove-AppxPackage"""; Flags: runhidden
