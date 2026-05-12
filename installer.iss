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
; Round 2 (Scott #5): block install/uninstall while the tray is running.
; Mutex name matches App.xaml.cs (`new Mutex(true, "OpenClawTray", …)`).
; Tray and Inno run in the same user session, so the bare name resolves
; against Local\OpenClawTray — no Global\ prefix needed.
AppMutex=OpenClawTray
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
; Command Palette extension (all files from build output).
; skipifsourcedoesntexist: prevents ISCC compile error when the cmdpal publish
; dir is absent (e.g. developer builds that skip the cmdpalette task).
Source: "{#publish}\cmdpal\*"; DestDir: "{app}\CommandPalette"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist; Tasks: cmdpalette
; WSL gateway uninstall helper — invoked by [UninstallRun] to drive clean removal
Source: "scripts\Uninstall-LocalGateway.ps1"; DestDir: "{app}"; Flags: ignoreversion

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
; ORDERING NOTE: Inno Setup runs [UninstallRun] entries BEFORE deleting {app}
; directory contents.  This guarantees OpenClawTray.exe is still present when
; the script executes.  See Inno docs: "[UninstallRun] section".
; Fallback: if OpenClawTray.exe is missing for any reason, Uninstall-LocalGateway.ps1
; logs the error to {app}\uninstall-gateway-error.log and exits 0 so Inno continues.
; *** DO NOT COMMENT OUT OR REMOVE THE Flags LINE BELOW ***
; waituntilterminated is non-negotiable: without it Inno races ahead and deletes
; {app} while the PowerShell hook (and the CLI engine it invokes) is still running,
; leaving 279+ application files behind after unins000.exe reports exit 0.
; runhidden suppresses the console window that would otherwise flash briefly.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-LocalGateway.ps1"""; Flags: shellexec waituntilterminated runhidden; StatusMsg: "Removing local WSL gateway..."
; Unregister Command Palette extension on uninstall
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name '*OpenClaw*' | Remove-AppxPackage"""; Flags: runhidden
