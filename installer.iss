; OpenClaw Companion Inno Setup Script (WinUI version)
; Pass /DDevBuild=1 to produce a side-by-side dev installer.
#ifdef DevBuild
  #define MyAppName "OpenClaw Companion (Dev)"
  #define MyAppAumid "OpenClaw.Companion.Dev"
  #define MyAppId "{{M0LTB0T-TRAY-4PP1-DEV}"
  #define MyInstallDir "OpenClawTray-Dev"
  #define MyMutex "OpenClawTray-Dev"
  #define MyAutoStartName "OpenClawTray-Dev"
  #define MyStartupTaskName "OpenClaw Companion (Dev)"
  #define MyDistroName "OpenClawGateway-Dev"
  #define MyProtocol "openclaw-dev"
  #define MyOutputSuffix "-Dev"
#else
  #define MyAppName "OpenClaw Companion"
  #define MyAppAumid "OpenClaw.Companion"
  #define MyAppId "{{M0LTB0T-TRAY-4PP1-D3N7}"
  #define MyInstallDir "OpenClawTray"
  #define MyMutex "OpenClawTray"
  #define MyAutoStartName "OpenClawTray"
  #define MyStartupTaskName "OpenClaw Companion"
  #define MyDistroName "OpenClawGateway"
  #define MyProtocol "openclaw"
  #define MyOutputSuffix ""
#endif
#define MyAppPublisher "OpenClaw Foundation"
#define MyAppURL "https://github.com/openclaw/openclaw-windows-node"
#define MyAppExeName "OpenClaw.Tray.WinUI.exe"

; Must stay equal to MigrationRecordCodec.PackageName. The uninstaller reads the
; packaged-app registration under this identity to decide whether the Store app is
; present, and a drift here would silently restore the destructive advice.
; Pinned by InnoMigrationContractTests.Installer_PinsTheStorePackageIdentity.
#define MyStorePackageName "OpenClawFoundation.OpenClaw"

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
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://github.com/openclaw/openclaw-windows-node/issues
AppUpdatesURL=https://github.com/openclaw/openclaw-windows-node/releases
DefaultDirName={localappdata}\{#MyInstallDir}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=OpenClawCompanion{#MyOutputSuffix}-Setup-{#MyAppArch}
Compression={#MyCompression}
SolidCompression={#MySolidCompression}
WizardStyle=modern
PrivilegesRequired=lowest
SetupIconFile=src\OpenClaw.Tray.WinUI\Assets\openclaw.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
; Round 2 (Scott #5): block install/uninstall while the tray is running.
; Mutex name matches AppIdentity.MutexBaseName for this build variant.
; Tray and Inno run in the same user session, so no Global\ prefix is needed.
AppMutex={#MyMutex}
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

#if !FileExists(publish + "\OpenClaw.Tray.WinUI.exe")
  #error Tray payload missing. Publish OpenClaw.Tray.WinUI before compiling the installer.
#endif

#if FileExists(publish + "\SetupEngine\OpenClaw.SetupEngine.UI.exe")
  #error SetupEngine.UI.exe should not be shipped. Setup UI is hosted by OpenClaw.Tray.WinUI.exe.
#endif

; vcRedist should point at the architecture-matching Visual C++ Runtime
; redistributable in CI release builds.
#ifndef vcRedist
  #define vcRedist ""
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start {#MyAppName} when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; WinUI Tray app - include all files (WinUI needs DLLs, not single-file)
Source: "{#publish}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; WSL gateway uninstall helper copied to {tmp} by [Code] during uninstall.
; Uninstall reads these during usUninstall, which runs before Inno removes
; files, so they must not carry uninsneveruninstall. Retaining them would
; strand the helpers in {app} on every uninstall that keeps the local gateway.
Source: "scripts\Uninstall-LocalGateway.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "scripts\Test-InnoMigration.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "src\OpenClaw.Connection\Migration\MigrationRecordCodec.cs"; DestDir: "{app}"; Flags: ignoreversion
#if vcRedist != ""
Source: "{#vcRedist}"; DestDir: "{tmp}"; DestName: "vc_redist.exe"; Flags: deleteafterinstall; AfterInstall: InstallVCRuntime
#endif

[Registry]
Root: HKCU; Subkey: "Software\Classes\{#MyProtocol}"; ValueType: string; ValueName: ""; ValueData: "URL:OpenClaw Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#MyProtocol}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\{#MyProtocol}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\{#MyProtocol}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppAumid}"
Name: "{group}\OpenClaw Gateway Setup"; Filename: "{app}\{#MyAppExeName}"; Parameters: "{#MyProtocol}://setup"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppAumid}"
Name: "{group}\OpenClaw Companion Settings"; Filename: "{app}\{#MyAppExeName}"; Parameters: "{#MyProtocol}://commandcenter"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppAumid}"
Name: "{group}\OpenClaw Chat"; Filename: "{app}\{#MyAppExeName}"; Parameters: "{#MyProtocol}://chat"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppAumid}"
Name: "{group}\Check for Updates"; Filename: "{app}\{#MyAppExeName}"; Parameters: "{#MyProtocol}://check-updates"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppAumid}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; AppUserModelID: "{#MyAppAumid}"
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon; AppUserModelID: "{#MyAppAumid}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent; Check: ShouldLaunchTray

[Code]
var
  VCRuntimeInstallSucceeded: Boolean;
  LocalGatewayCleanupChoiceInitialized: Boolean;
  LocalGatewayCleanupRequested: Boolean;
  LocalGatewayCleanupSucceeded: Boolean;
  MigrationOperationHandle: THandle;
  MigrationOperationLocked: Boolean;
  MigrationOperationUnavailable: Boolean;

function OpenMigrationOperationFile(
  FileName: String; DesiredAccess, ShareMode: LongWord; SecurityAttributes: Integer;
  CreationDisposition, FlagsAndAttributes: LongWord; TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseMigrationOperationFile(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function MigrationPathAttributes(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function MigrationPathIsOrdinary(Path: String): Boolean;
var
  Attributes: LongWord;
  Parent: String;
begin
  Result := False;
  while Path <> '' do
  begin
    Attributes := MigrationPathAttributes(Path);
    if Attributes = $FFFFFFFF then
    begin
      if (DLLGetLastError <> 2) and (DLLGetLastError <> 3) then
        Exit;
    end
    else if (Attributes and $400) <> 0 then
      Exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then
      Break;
    Path := Parent;
  end;
  Result := True;
end;

function InitializeUninstall: Boolean;
var
  Directory: String;
  LockPath: String;
  LastError: LongWord;
begin
  Result := True;
#ifndef DevBuild
  MigrationOperationUnavailable := True;
  Directory := ExpandConstant('{userappdata}\{#MyInstallDir}\store-migration');
  LockPath := Directory + '\prepare.lock';
  // Kept as separate tests rather than one boolean expression: ForceDirectories must
  // never run when the path check rejected a reparse point, and Pascal Script
  // short-circuit behavior is not worth betting a redirected directory create on.
  if not MigrationPathIsOrdinary(LockPath) then
    Log('Migration state path is not an ordinary directory. Uninstall continues and preserves the local gateway.')
  else if ForceDirectories(Directory) then
  begin
    // Shared read handles allow our cleanup child to join, but exclude Store's
    // FileShare.None writer. Keep this handle through registry/payload removal.
    MigrationOperationHandle := OpenMigrationOperationFile(
      LockPath, $80000000, 1, 0, 4, $80, 0);
    if MigrationOperationHandle <> THandle(-1) then
    begin
      MigrationOperationLocked := True;
      MigrationOperationUnavailable := False;
    end
    else
    begin
      LastError := DLLGetLastError;
      // Only a migration actively holding the lock may stop an uninstall. Any other
      // failure means migration state is merely unreadable, which must never trap the
      // user in an app they cannot remove. Continue and suppress destructive cleanup.
      if (LastError = 32) or (LastError = 33) then
      begin
        Result := False;
        Log('Migration state is locked by an in-progress migration. Uninstall stopped before changing the installation.');
        if not UninstallSilent() then
          MsgBox('OpenClaw migration is currently running. Close the Store migration preview, then retry uninstall.', mbError, MB_OK);
      end;
    end;
  end;
  if Result and MigrationOperationUnavailable then
    Log('Migration state could not be locked. Uninstall continues and preserves the local gateway.');
#endif
end;

procedure DeinitializeUninstall;
begin
  if MigrationOperationLocked then
  begin
    CloseMigrationOperationFile(MigrationOperationHandle);
    MigrationOperationLocked := False;
  end;
end;

#if vcRedist != ""
procedure InstallVCRuntime;
var
  ResultCode: Integer;
  Started: Boolean;
begin
  VCRuntimeInstallSucceeded := False;
  Log('Running bundled Visual C++ Runtime redistributable.');
  Started :=
    Exec(
      ExpandConstant('{tmp}\vc_redist.exe'),
      '/install /quiet /norestart',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);

  if not Started then
  begin
    Log('Failed to start Visual C++ Runtime redistributable. System error: ' + IntToStr(ResultCode) + '.');
    Exit;
  end;

  VCRuntimeInstallSucceeded := (ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1641);
  if VCRuntimeInstallSucceeded then
    Log('Visual C++ Runtime redistributable exited with success code ' + IntToStr(ResultCode) + '.')
  else
    Log('Visual C++ Runtime redistributable failed with exit code ' + IntToStr(ResultCode) + '.');
end;
#endif

function ShouldLaunchTray: Boolean;
begin
#if vcRedist != ""
  Result := VCRuntimeInstallSucceeded;
  if not Result then
    Log('Skipping post-install tray launch because Visual C++ Runtime installation did not succeed.');
#else
  Result := True;
#endif
end;

function CheckCompletedStoreMigration: Integer;
var
  ResultCode: Integer;
  Started: Boolean;
begin
  Result := 2;
  if not FileExists(ExpandConstant('{app}\Test-InnoMigration.ps1')) then
  begin
    Log('Migration preservation checker is missing. Generated state will be preserved.');
    Exit;
  end;
  Started := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File ' +
    AddQuotes(ExpandConstant('{app}\Test-InnoMigration.ps1')) +
    ' -AppRoot ' + AddQuotes(ExpandConstant('{app}')) +
    ' -DataDirectoryName ' + AddQuotes('{#MyInstallDir}') +
    ' -Architecture ' + AddQuotes('{#MyAppArch}'),
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Started then
    Result := ResultCode;
  Log('Migration preservation check returned ' + IntToStr(Result) + '.');
end;

// Returns 'Present', 'Absent', or 'Indeterminate', matching the vocabulary
// Test-InnoMigration.ps1 uses for the same hive. A Boolean cannot carry this: "the Store
// app is installed" and "this hive could not be read" both preserve the gateway, but they
// are not the same claim and the user must not be told the first when we only know the
// second.
function StorePackagePresence: String;
var
  Names: TArrayOfString;
  I: Integer;
  Prefix: String;
begin
  // Read directly rather than relying on the checker, because several routes to a
  // preserve verdict happen when the checker could not run at all. This is the only
  // signal available in those cases, and it decides what we tell the user, never
  // whether we destroy anything.
  Prefix := Lowercase('{#MyStorePackageName}' + '_');
  if not RegGetSubkeyNames(HKEY_CURRENT_USER,
      'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages',
      Names) then
  begin
    // Unreadable, so the Store app can be neither confirmed nor ruled out.
    Result := 'Indeterminate';
    Exit;
  end;

  if GetArrayLength(Names) = 0 then
  begin
    // Every real profile has hundreds of registered packages. Zero means the hive was
    // tampered with or is unreadable, which is uncertainty, not absence.
    Result := 'Indeterminate';
    Exit;
  end;

  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    if Pos(Prefix, Lowercase(Names[I])) = 1 then
    begin
      Result := 'Present';
      Exit;
    end;
  end;

  Result := 'Absent';
end;

procedure ReportStoreAppOwnsGateway;
begin
  Log('Store app registration found without a migration receipt: preserving the local WSL gateway.');
  if not UninstallSilent() then
    MsgBox(
      'OpenClaw from the Microsoft Store is installed on this PC and is using the local WSL gateway.' + #13#10#13#10 +
      'The gateway and its generated state were left in place, so the Store app keeps working.' + #13#10#13#10 +
      'Do not run "wsl --unregister {#MyDistroName}". That would delete the gateway the Store app is ' +
      'still using. If you want to remove it later, open OpenClaw and choose ' +
      'Settings > Local Gateway > Remove Local Gateway.',
      mbInformation, MB_OK);
end;

procedure ReportStoreAppStateUnknown;
begin
  Log('Store app registration could not be read: preserving the local WSL gateway. ' +
      'The {#MyDistroName} WSL distro and ' +
      ExpandConstant('{localappdata}\{#MyInstallDir}\wsl\{#MyDistroName}') +
      ' were left in place.');
  if not UninstallSilent() then
    MsgBox(
      'Setup could not check whether OpenClaw from the Microsoft Store is installed on this PC.' + #13#10#13#10 +
      'The local WSL gateway and its generated state were left in place, so nothing is lost.' + #13#10#13#10 +
      'Do not remove the gateway by hand until you know the Store app is not using it. To remove it ' +
      'safely, open OpenClaw and choose Settings > Local Gateway > Remove Local Gateway.',
      mbInformation, MB_OK);
end;

procedure WarnMigrationCheckUnavailable;
var
  Presence: String;
begin
  // Most routes here mean the check could not run, and several of them are reachable on
  // a machine that has already migrated. Telling that user to run wsl --unregister would
  // destroy the gateway the installed Store app is using, so ask the registry directly
  // before saying anything destructive.
  Presence := StorePackagePresence;
  if Presence = 'Present' then
  begin
    ReportStoreAppOwnsGateway;
    Exit;
  end;

  if Presence <> 'Absent' then
  begin
    // Anything other than a positively observed absence is uncertainty. Preserve and say
    // so, rather than handing over the command that destroys what was just preserved.
    // Only a fully enumerated hive with no matching package may reach the advice below.
    ReportStoreAppStateUnknown;
    Exit;
  end;

  Log('Migration preservation check unavailable: skipping destructive gateway cleanup. ' +
      'The {#MyDistroName} WSL distro and ' +
      ExpandConstant('{localappdata}\{#MyInstallDir}\wsl\{#MyDistroName}') +
      ' were left in place.');
  if not UninstallSilent() then
    MsgBox(
      'Setup could not confirm whether your OpenClaw data was migrated to the Store app.' + #13#10#13#10 +
      'The local WSL gateway and its generated state were left in place so nothing is lost.' + #13#10#13#10 +
      'If you want to remove them, open OpenClaw and choose Settings > Local Gateway > ' +
      'Remove Local Gateway before uninstalling. If OpenClaw is already removed, run:' + #13#10#13#10 +
      'wsl --unregister {#MyDistroName}',
      mbInformation, MB_OK);
end;

procedure EnsureLocalGatewayCleanupChoice;
var
  MigrationResult: Integer;
begin
  if LocalGatewayCleanupChoiceInitialized then
    Exit;

  LocalGatewayCleanupChoiceInitialized := True;

  // Cleanup runs a child process that must join the migration lock. Without it the
  // uninstall cannot prove the gateway is unowned, so preservation is the only safe answer.
  if MigrationOperationUnavailable then
  begin
    LocalGatewayCleanupRequested := False;
    WarnMigrationCheckUnavailable;
    Exit;
  end;

  MigrationResult := CheckCompletedStoreMigration;
  if MigrationResult <> 0 then
  begin
    LocalGatewayCleanupRequested := False;
    if MigrationResult = 10 then
      Log('Completed Store migration: preserving generated state and local WSL gateway.')
    else if MigrationResult = 11 then
      // The checker positively identified an installed Store app, so the generic
      // "could not confirm" advice would be both untrue and destructive here.
      ReportStoreAppOwnsGateway
    else
      WarnMigrationCheckUnavailable;
    Exit;
  end;

  if UninstallSilent() then
  begin
    LocalGatewayCleanupRequested := True;
    Log('Silent uninstall: local gateway cleanup will run automatically.');
  end
  else
  begin
    // MB_DEFBUTTON2 makes "No" the default: removing the WSL gateway is destructive and
    // unrecoverable, and a user uninstalling in order to reinstall (for example when
    // moving to the Store package) must not lose their gateway by pressing Enter.
    LocalGatewayCleanupRequested :=
      MsgBox(
        'Do you also want to remove the OpenClaw local WSL gateway?' + #13#10#13#10 +
        'Choose Yes to unregister the {#MyDistroName} WSL distro and remove generated local gateway state.' + #13#10 +
        'Choose No to leave the local gateway and generated local state on this computer.',
        mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2) = IDYES;

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
    ResultCode := 102;
    Log('Local gateway cleanup script is missing: ' + SourceScriptPath);
    Result := False;
    Exit;
  end;

  if FileExists(TempScriptPath) then
    DeleteFile(TempScriptPath);

  if not CopyFile(SourceScriptPath, TempScriptPath, False) then
  begin
    ResultCode := 103;
    Log('Failed to copy local gateway cleanup script to: ' + TempScriptPath);
    Result := False;
    Exit;
  end;

  Params :=
    '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(TempScriptPath) +
    ' -AppRoot ' + AddQuotes(ExpandConstant('{app}')) +
    ' -DataDirectoryName ' + AddQuotes('{#MyInstallDir}') +
    ' -AutoStartName ' + AddQuotes('{#MyAutoStartName}') +
    ' -StartupTaskName ' + AddQuotes('{#MyStartupTaskName}') +
    ' -DistroName ' + AddQuotes('{#MyDistroName}') +
    ' -Architecture ' + AddQuotes('{#MyAppArch}');

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

    if Started and (ResultCode = 10) then
    begin
      Log('Completed Store migration detected before cleanup. Generated state will be preserved.');
      Exit;
    end;

    if Started and (ResultCode = 11) then
    begin
      // The cleanup script re-runs the check, so the Store app can be registered between
      // the initial decision and this point. That is a deliberate refusal, not a failure,
      // and must not surface a Retry dialog offering to destroy the gateway again.
      ReportStoreAppOwnsGateway;
      Exit;
    end;

    if Started and (ResultCode = 0) then
    begin
      LocalGatewayCleanupSucceeded := True;
      Log('Local gateway cleanup completed successfully.');
      Exit;
    end;

    if Started and (ResultCode = 2) then
    begin
      WarnMigrationCheckUnavailable;
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

procedure DeleteGeneratedChild(const ChildName: String);
var
  ChildPath: String;
begin
  ChildPath := AddBackslash(ExpandConstant('{app}')) + ChildName;
  if DirExists(ChildPath) then
  begin
    if not DelTree(ChildPath, True, True, True) then
      Log('Generated directory could not be deleted: ' + ChildName);
  end
  else if FileExists(ChildPath) then
  begin
    if not DeleteFile(ChildPath) then
      Log('Generated file could not be deleted: ' + ChildName);
  end;
end;

procedure DeleteGeneratedAppState;
var
  AppDir: String;
  FolderName: String;
begin
  if not LocalGatewayCleanupSucceeded then
    Exit;

  AppDir := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  FolderName := ExtractFileName(AppDir);
  if (CompareText(FolderName, 'OpenClawTray') <> 0) and
     (CompareText(FolderName, 'OpenClawTray-Dev') <> 0) then
  begin
    Log('Refusing to delete generated app state because the install folder is not OpenClawTray or OpenClawTray-Dev.');
    Exit;
  end;

  DeleteGeneratedChild('wsl');
  DeleteGeneratedChild('Logs');
  DeleteGeneratedChild('wsl-keepalive');
  DeleteGeneratedChild('WebView2');
  DeleteGeneratedChild('canvas');
  DeleteGeneratedChild('native-cli');
  DeleteGeneratedChild('setup-state.json');
  DeleteGeneratedChild('run.marker');
  DeleteGeneratedChild('exec-approvals.json');
  DeleteGeneratedChild('exec-policy.json');
  DeleteGeneratedChild('openclaw-tray.log');
  DeleteGeneratedChild('uninstall-gateway-result.json');
  DeleteGeneratedChild('uninstall-gateway-error.log');
  DeleteGeneratedChild('uninstall-gateway-wsl.log');
  Log('Deleted generated app state children from {app}.');
end;

procedure RemoveAppAutoStart;
var
  ResultCode: Integer;
  Started: Boolean;
begin
  if RegDeleteValue(
      HKCU,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
      '{#MyAutoStartName}') then
    Log('Removed {#MyAutoStartName} autostart registry value.')
  else
    Log('{#MyAutoStartName} autostart registry value already absent.');

  Started :=
    Exec(
      ExpandConstant('{sys}\schtasks.exe'),
      '/Delete /TN ' + AddQuotes('{#MyStartupTaskName}') + ' /F',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);
  if Started and (ResultCode = 0) then
    Log('Removed {#MyStartupTaskName} startup task.')
  else
    Log('{#MyStartupTaskName} startup task already absent or unavailable.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveAppAutoStart;
    EnsureLocalGatewayCleanupChoice;
    RunLocalGatewayCleanup;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DeleteGeneratedAppState;
  end;
end;
