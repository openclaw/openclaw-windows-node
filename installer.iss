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
; WSL gateway uninstall helper copied to {tmp} by [Code] during uninstall.
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

[Code]
var
  LocalGatewayCleanupChoiceInitialized: Boolean;
  LocalGatewayCleanupRequested: Boolean;
  LocalGatewayCleanupSucceeded: Boolean;

procedure EnsureLocalGatewayCleanupChoice;
begin
  if LocalGatewayCleanupChoiceInitialized then
    Exit;

  LocalGatewayCleanupChoiceInitialized := True;

  if UninstallSilent() then
  begin
    LocalGatewayCleanupRequested := True;
    Log('Silent uninstall: local gateway cleanup will run automatically.');
  end
  else
  begin
    LocalGatewayCleanupRequested :=
      MsgBox(
        'Do you also want to remove the OpenClaw local WSL gateway?' + #13#10#13#10 +
        'Choose Yes to unregister the OpenClawGateway WSL distro and remove generated local gateway state.' + #13#10 +
        'Choose No to leave the local gateway and generated local state on this computer.',
        mbConfirmation,
        MB_YESNO) = IDYES;

    if LocalGatewayCleanupRequested then
      Log('User chose to remove the local WSL gateway.')
    else
      Log('User chose to preserve the local WSL gateway and generated state.');
  end;
end;

function RunLocalGatewayCleanupOnce(var ResultCode: Integer): Boolean;
var
  SourceScriptPath: string;
  TempScriptPath: string;
  Params: string;
begin
  SourceScriptPath := ExpandConstant('{app}\Uninstall-LocalGateway.ps1');
  TempScriptPath := ExpandConstant('{tmp}\Uninstall-LocalGateway.ps1');

  if not FileExists(SourceScriptPath) then
  begin
    ResultCode := 2;
    Log('Local gateway cleanup script is missing: ' + SourceScriptPath);
    Result := False;
    Exit;
  end;

  if FileExists(TempScriptPath) then
    DeleteFile(TempScriptPath);

  if not CopyFile(SourceScriptPath, TempScriptPath, False) then
  begin
    ResultCode := 3;
    Log('Failed to copy local gateway cleanup script to: ' + TempScriptPath);
    Result := False;
    Exit;
  end;

  Params :=
    '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(TempScriptPath) +
    ' -AppRoot ' + AddQuotes(ExpandConstant('{app}'));

  Log('Running local gateway cleanup script from {tmp}.');
  Result :=
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      Params,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);

  if Result then
    Log('Local gateway cleanup script exited with code ' + IntToStr(ResultCode) + '.')
  else
    Log('Failed to start local gateway cleanup script. System error: ' + IntToStr(ResultCode) + '.');
end;

procedure RunLocalGatewayCleanup;
var
  ResultCode: Integer;
  Retry: Boolean;
  Started: Boolean;
begin
  if not LocalGatewayCleanupRequested then
    Exit;

  LocalGatewayCleanupSucceeded := False;

  repeat
    Retry := False;
    UninstallProgressForm.StatusLabel.Caption := 'Removing local WSL gateway...';
    Started := RunLocalGatewayCleanupOnce(ResultCode);

    if Started and (ResultCode = 0) then
    begin
      LocalGatewayCleanupSucceeded := True;
      Log('Local gateway cleanup completed successfully.');
      Exit;
    end;

    if UninstallSilent() then
    begin
      Log('Local gateway cleanup failed during silent uninstall; continuing without deleting generated state.');
      Exit;
    end;

    Retry :=
      MsgBox(
        'OpenClaw could not remove the local WSL gateway.' + #13#10#13#10 +
        'Exit code: ' + IntToStr(ResultCode) + #13#10#13#10 +
        'Select Retry to try again, or Cancel to continue uninstalling OpenClaw and leave local gateway state on disk.',
        mbError,
        MB_RETRYCANCEL) = IDRETRY;
  until not Retry;

  Log('User continued uninstall after local gateway cleanup failed; generated state will be preserved.');
end;

procedure DeleteGeneratedAppState;
begin
  if not LocalGatewayCleanupSucceeded then
    Exit;

  if DelTree(ExpandConstant('{app}'), True, True, True) then
    Log('Deleted generated app state from {app}.')
  else
    Log('Generated app state in {app} could not be fully deleted; continuing uninstall.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    EnsureLocalGatewayCleanupChoice;
    RunLocalGatewayCleanup;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DeleteGeneratedAppState;
  end;
end;
