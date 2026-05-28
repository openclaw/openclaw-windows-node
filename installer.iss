; OpenClaw Companion Inno Setup Script (WinUI version)
#define MyAppName "OpenClaw Companion"
#define MyAppPublisher "Scott Hanselman"
#define MyAppURL "https://github.com/openclaw/openclaw-windows-node"
#define MyAppExeName "OpenClaw.Tray.WinUI.exe"

; MyAppArch should be passed via /DMyAppArch=x64 or /DMyAppArch=arm64
#ifndef MyAppArch
  #define MyAppArch "x64"
#endif

#ifndef MyCompression
  #define MyCompression "lzma"
#endif

#ifndef MySolidCompression
  #define MySolidCompression "yes"
#endif

[Setup]
; Inno requires "{{" to emit a literal opening brace in AppId.
; Do not add a second closing brace here; that creates a malformed uninstall registry key.
AppId={{M0LTB0T-TRAY-4PP1-D3N7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://github.com/openclaw/openclaw-windows-node/issues
AppUpdatesURL=https://github.com/openclaw/openclaw-windows-node/releases
DefaultDirName={localappdata}\OpenClawTray
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=OpenClawCompanion-Setup-{#MyAppArch}
Compression={#MyCompression}
SolidCompression={#MySolidCompression}
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

#if !FileExists(publish + "\SetupEngine\OpenClaw.SetupEngine.UI.exe")
  #error SetupEngine.UI payload missing. Publish OpenClaw.SetupEngine.UI into {#publish}\SetupEngine before compiling the installer.
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start OpenClaw Companion when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; WinUI Tray app - include all files (WinUI needs DLLs, not single-file)
Source: "{#publish}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; WSL gateway uninstall helper — invoked by [UninstallRun] to drive clean removal
Source: "scripts\Uninstall-LocalGateway.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\Classes\openclaw"; ValueType: string; ValueName: ""; ValueData: "URL:OpenClaw Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\openclaw"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\openclaw\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\openclaw\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\OpenClaw Gateway Setup"; Filename: "{app}\SetupEngine\OpenClaw.SetupEngine.UI.exe"; IconFilename: "{app}\SetupEngine\OpenClaw.SetupEngine.UI.exe"
Name: "{group}\OpenClaw Companion Settings"; Filename: "{app}\{#MyAppExeName}"; Parameters: "openclaw://commandcenter"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\OpenClaw Chat"; Filename: "{app}\{#MyAppExeName}"; Parameters: "openclaw://chat"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Check for Updates"; Filename: "{app}\{#MyAppExeName}"; Parameters: "openclaw://check-updates"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

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
; The hook launches our self-contained tray executable from {app}. Windows can
; keep just-loaded .NET/WinUI DLL handles around briefly after the process exits;
; give those handles time to release before Inno deletes the app directory.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Start-Sleep -Seconds 3"""; Flags: waituntilterminated runhidden; StatusMsg: "Preparing installed files for removal..."

[UninstallDelete]
; The tray creates runtime state (logs, WSL files, JSON metadata) under the
; dedicated {app} directory. Remove the directory after the gateway cleanup hook
; has finished so uninstall leaves neither app files nor generated state behind.
Type: filesandordirs; Name: "{app}"
