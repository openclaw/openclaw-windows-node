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
; The model helper compiles the same pinned catalog and file operation as Shared.
; It does not load Companion or keep its installed binaries locked.
Source: "scripts\Uninstall-DownloadedModels.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "src\OpenClaw.Shared\Audio\KokoroDownloadCatalog.cs"; DestDir: "{app}"; Flags: ignoreversion
Source: "src\OpenClaw.Shared\Audio\KokoroUninstallFiles.cs"; DestDir: "{app}"; Flags: ignoreversion
#if vcRedist != ""
Source: "{#vcRedist}"; DestDir: "{tmp}"; DestName: "vc_redist.exe"; Flags: deleteafterinstall; AfterInstall: InstallVCRuntime
#endif

[InstallDelete]
; Exact obsolete binaries shipped by the retired npm bridge. Do not remove sibling DLLs or directories.
Type: files; Name: "{app}\tools\mxc\x64\wxc-exec.exe"; Check: LegacyMxcPathIsOrdinary(ExpandConstant('{app}\tools\mxc\x64\wxc-exec.exe'))
Type: files; Name: "{app}\tools\mxc\x64\wslcsdk.dll"; Check: LegacyMxcPathIsOrdinary(ExpandConstant('{app}\tools\mxc\x64\wslcsdk.dll'))
Type: files; Name: "{app}\tools\mxc\arm64\wxc-exec.exe"; Check: LegacyMxcPathIsOrdinary(ExpandConstant('{app}\tools\mxc\arm64\wxc-exec.exe'))
Type: files; Name: "{app}\tools\mxc\arm64\wslcsdk.dll"; Check: LegacyMxcPathIsOrdinary(ExpandConstant('{app}\tools\mxc\arm64\wslcsdk.dll'))
Type: dirifempty; Name: "{app}\tools\mxc\x64"; Check: LegacyMxcPathIsOrdinary(ExpandConstant('{app}\tools\mxc\x64'))
Type: dirifempty; Name: "{app}\tools\mxc\arm64"; Check: LegacyMxcPathIsOrdinary(ExpandConstant('{app}\tools\mxc\arm64'))
Type: dirifempty; Name: "{app}\tools\mxc"; Check: LegacyMxcPathIsOrdinary(ExpandConstant('{app}\tools\mxc'))

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
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--background"; Tasks: startupicon; AppUserModelID: "{#MyAppAumid}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent; Check: ShouldLaunchTray

[Code]
const
  ModelPreviewPollMilliseconds = 100;
  ModelStartupGraceSeconds = 30;
  ModelStopGraceSeconds = 10;
var
  VCRuntimeInstallSucceeded: Boolean;
  LocalGatewayCleanupChoiceInitialized: Boolean;
  LocalGatewayCleanupRequested: Boolean;
  LocalGatewayCleanupSucceeded: Boolean;
  MigrationOperationHandle: THandle;
  MigrationOperationLocked: Boolean;
  MigrationOperationUnavailable: Boolean;
  ModelCleanupSelection: Integer;
  ModelSummary: String;
  ModelScanRunning, ModelScanCancelled, ModelScanAllowed: Boolean;
  ModelPreviewEligible: Integer;
  ModelDialog: TSetupForm;
  ModelDetails: TNewMemo;
  ModelRemoveCheck: TNewCheckBox;
  ModelScanButton, ModelContinueButton: TNewButton;
  ModelPreviewTimer: THandle;
  ModelPreviewDeadline: Int64;
  ModelInvocation: Integer;
  ModelReportPath, ModelCancelPath: String;

function SetModelTimer(Window: THandle; Id: THandle; Interval: LongWord; Callback: THandle): THandle;
  external 'SetTimer@user32.dll stdcall';
function KillModelTimer(Window: THandle; Id: THandle): Boolean;
  external 'KillTimer@user32.dll stdcall';
function ModelTickCount: Int64;
  external 'GetTickCount64@kernel32.dll stdcall';

function ChooseModelCleanup: Boolean; forward;

function OpenMigrationOperationFile(
  FileName: String; DesiredAccess, ShareMode: LongWord; SecurityAttributes: Integer;
  CreationDisposition, FlagsAndAttributes: LongWord; TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseMigrationOperationFile(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function MigrationPathAttributes(FileName: String): Integer;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function MigrationPathIsUncRoot(Path: String): Boolean;
var
  Rest: String;
  Separator: Integer;
begin
  Result := False;
  if Copy(Path, 1, 2) <> '\\' then
    Exit;
  Rest := Copy(Path, 3, Length(Path) - 2);
  Separator := Pos('\', Rest);
  if Separator = 0 then
    // '\\server': no share component left to walk into.
    Result := True
  else
    // '\\server\share': a share root, with nothing addressable above it.
    Result := Pos('\', Copy(Rest, Separator + 1, Length(Rest) - Separator)) = 0;
end;

function MigrationPathIsOrdinary(Path: String): Boolean;
var
  Attributes: Integer;
  Parent: String;
begin
  Result := False;
  while Path <> '' do
  begin
    // GetFileAttributesW reports failure as INVALID_FILE_ATTRIBUTES. That value is
    // read as a signed -1 here rather than compared against an unsigned $FFFFFFFF
    // literal, whose type Pascal Script resolves inconsistently. The bit pattern is
    // identical, and the reparse-point test below is unaffected by the signedness.
    Attributes := MigrationPathAttributes(Path);
    if Attributes = -1 then
    begin
      // ERROR_FILE_NOT_FOUND / ERROR_PATH_NOT_FOUND: nothing is there to be a
      // reparse point. Any other failure means the component cannot be cleared.
      if (DLLGetLastError <> 2) and (DLLGetLastError <> 3) then
        Exit;
    end
    else if (Attributes and $400) <> 0 then
      Exit;
    // A local walk ends at 'C:\', where ExtractFileDir returns its own argument. A UNC
    // walk has no such fixed point: ExtractFileDir('\\server\share') yields '\\server',
    // which is not a filesystem object, so probing it fails with a code that is neither
    // 2 nor 3 and would refuse the uninstall outright. Stop at the share root instead;
    // every component below it has already been checked, exactly as on a local disk.
    if MigrationPathIsUncRoot(Path) then
      Break;
    Parent := ExtractFileDir(Path);
    if Parent = Path then
      Break;
    Path := Parent;
  end;
  Result := True;
end;

function LegacyMxcPathIsOrdinary(Path: String): Boolean;
begin
  Result := MigrationPathIsOrdinary(Path);
  if not Result then
    Log('Skipping legacy MXC cleanup: path is not ordinary or could not be checked.');
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
  if Result then
    Result := ChooseModelCleanup;
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

procedure MigrateLegacyBackgroundLaunch;
var
  ExecutablePath: string;
  RunCommand: string;
  ActionPath: string;
  ActionArguments: string;
  Scheduler: Variant;
  Task: Variant;
  Action: Variant;
  ResultCode: Integer;
begin
  ExecutablePath := ExpandConstant('{app}\{#MyAppExeName}');
  { Only migrate the exact old argument-free registration for this installation. }
  if RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
      '{#MyAutoStartName}', RunCommand) then
  begin
    if CompareText(RemoveQuotes(RunCommand), ExecutablePath) = 0 then
    begin
      if RegWriteStringValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
          '{#MyAutoStartName}', AddQuotes(ExecutablePath) + ' --background') then
        Log('Migrated existing Run registration to background launch.')
      else
        Log('Could not migrate Run registration. Re-enable Start with Windows in Settings.');
    end;
  end;

  try
    Scheduler := CreateOleObject('Schedule.Service');
    Scheduler.Connect;
    Task := Scheduler.GetFolder('\').GetTask('{#MyStartupTaskName}');
    if Task.Definition.Actions.Count <> 1 then
      Exit;
    Action := Task.Definition.Actions.Item(1);
    { Only executable actions expose Path and Arguments. Other action types
      fail the guarded lookup below without changing the task. }
    ActionPath := Action.Path;
    ActionArguments := Action.Arguments;
    if (CompareText(RemoveQuotes(ActionPath), ExecutablePath) <> 0) or
        (Trim(ActionArguments) <> '') then
      Exit;

    { /Change preserves the existing task's triggers, principal, and enabled state. }
    if Exec(ExpandConstant('{sys}\schtasks.exe'),
        '/Change /TN ' + AddQuotes('{#MyStartupTaskName}') +
        ' /TR ' + AddQuotes('\"' + ExecutablePath + '\" --background'),
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
      Log('Migrated existing scheduled task to background launch.')
    else
      Log('Could not migrate startup task. Re-enable Start with Windows in Settings.');
  except
    Log('No migratable startup task: ' + GetExceptionMessage);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MigrateLegacyBackgroundLaunch;
end;

function ReadModelCompletion(var Code: Integer): Boolean;
var
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := False;
  Code := -1;
  if not LoadStringsFromFile(ModelReportPath, Lines) then Exit;
  if GetArrayLength(Lines) < 2 then Exit;
  Code := StrToIntDef(Lines[0], -1);
  if (Code < 32) or (Code > 50) then Exit;
  ModelSummary := '';
  for I := 1 to GetArrayLength(Lines) - 1 do
  begin
    if I > 1 then ModelSummary := ModelSummary + #13#10;
    ModelSummary := ModelSummary + Lines[I];
  end;
  Result := True;
end;

function StageModelHelper: Boolean;
begin
  Result := CopyFile(ExpandConstant('{app}\Uninstall-DownloadedModels.ps1'),
    ExpandConstant('{tmp}\Uninstall-DownloadedModels.ps1'), False);
  if not Result then Exit;
  Result := CopyFile(ExpandConstant('{app}\KokoroDownloadCatalog.cs'),
    ExpandConstant('{tmp}\KokoroDownloadCatalog.cs'), False);
  if not Result then Exit;
  Result := CopyFile(ExpandConstant('{app}\KokoroUninstallFiles.cs'),
    ExpandConstant('{tmp}\KokoroUninstallFiles.cs'), False);
end;

function StartModelHelper(Selection: Integer; Wait: TExecWait; var Code: Integer): Boolean;
var
  Params: String;
begin
  Inc(ModelInvocation);
  ModelReportPath := ExpandConstant('{tmp}\model-result-') + IntToStr(ModelInvocation) + '.txt';
  ModelCancelPath := ExpandConstant('{tmp}\model-cancel-') + IntToStr(ModelInvocation) + '.txt';
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    AddQuotes(ExpandConstant('{tmp}\Uninstall-DownloadedModels.ps1')) +
    ' -DataDirectory ' + AddQuotes(ExpandConstant('{userappdata}\{#MyInstallDir}')) +
    ' -ReportPath ' + AddQuotes(ModelReportPath) +
    ' -CancellationPath ' + AddQuotes(ModelCancelPath) +
    ' -SelectionMask ' + IntToStr(Selection);
  if Selection <> 0 then Params := Params + ' -ConfirmDestructive';
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Params, '', SW_HIDE, Wait, Code);
end;

procedure StopModelPreviewTimer;
begin
  if ModelPreviewTimer <> 0 then
  begin
    KillModelTimer(0, ModelPreviewTimer);
    ModelPreviewTimer := 0;
  end;
end;

procedure FinishModelPreview;
begin
  StopModelPreviewTimer;
  ModelScanRunning := False;
  ModelDetails.Text := ModelSummary;
  ModelRemoveCheck.Enabled := ModelPreviewEligible <> 0;
  ModelScanButton.Enabled := ModelScanAllowed;
  ModelContinueButton.Enabled := True;
  if ModelScanCancelled then ModelDialog.Close;
end;

procedure CancelModelPreview(Sender: TObject);
begin
  if ModelScanRunning then
  begin
    ModelScanCancelled := True;
    SaveStringToFile(ModelCancelPath, 'cancel', False);
    ModelDetails.Text := 'Cancelling the model check. Waiting for the worker to stop; no model removal has started.';
  end
  else
    ModelDialog.Close;
end;

procedure ModelPreviewCloseQuery(Sender: TObject; var CanClose: Boolean);
begin
  CanClose := not ModelScanRunning;
  if not CanClose then CancelModelPreview(Sender);
end;

procedure ModelPreviewTick(Window: THandle; Message: LongWord; Timer: THandle; Time: LongWord);
var
  Code: Integer;
begin
  if (Timer <> ModelPreviewTimer) or not ModelScanRunning then Exit;
  if FileExists(ModelReportPath) then
  begin
    // This file is atomically published by the supervisor, never by the worker.
    // A late receipt from another invocation has a different filename.
    if not ReadModelCompletion(Code) then Code := 50;
    if Code = 50 then
    begin
      StopModelPreviewTimer;
      SaveStringToFile(ModelCancelPath, 'cancel', False);
      ModelScanCancelled := True;
      MsgBox('The model check could not confirm completion. Uninstall will stop without starting model removal.', mbError, MB_OK);
    end
    else if (Code >= 32) and (Code <= 47) then
      ModelPreviewEligible := Code - 32;
    FinishModelPreview;
  end
  else if ModelTickCount >= ModelPreviewDeadline then
  begin
    StopModelPreviewTimer;
    SaveStringToFile(ModelCancelPath, 'cancel', False);
    ModelScanCancelled := True;
    MsgBox('The model check did not report completion within its time limit. Uninstall will stop; worker exit is unconfirmed.', mbError, MB_OK);
    FinishModelPreview;
  end;
end;

procedure CheckModelPreview(Sender: TObject);
var
  Code, Seconds: Integer;
begin
  ModelPreviewEligible := 0;
  ModelRemoveCheck.Checked := False;
  ModelRemoveCheck.Enabled := False;
  if not StageModelHelper then
  begin
    ModelDetails.Text := 'The model helper is unavailable. Models are kept.';
    Exit;
  end;
  ModelScanRunning := True;
  ModelScanCancelled := False;
  ModelScanButton.Enabled := False;
  ModelContinueButton.Enabled := False;
  ModelDetails.Text := 'Checking downloaded model files. Cancel stops this read-only check.';
  Seconds := StrToIntDef(GetEnv('OPENCLAW_MODEL_UNINSTALL_PROCESS_TIMEOUT_SECONDS'), 180);
  if (Seconds < 30) or (Seconds > 900) then Seconds := 180;
  ModelPreviewDeadline := ModelTickCount + (Seconds + ModelStartupGraceSeconds + ModelStopGraceSeconds) * 1000;
  ModelPreviewTimer := SetModelTimer(0, 0, ModelPreviewPollMilliseconds, CreateCallback(@ModelPreviewTick));
  if ModelPreviewTimer = 0 then
  begin
    ModelSummary := 'The model check could not start. Models are kept.';
    FinishModelPreview;
    Exit;
  end;
  // No synchronous wait: Inno must not disable the custom form while scanning.
  // Preview is read-only. Store ownership is rechecked at the deletion boundary.
  if not StartModelHelper(0, ewNoWait, Code) then
  begin
    ModelSummary := 'The model helper could not start. Models are kept.';
    FinishModelPreview;
  end;
end;

function ShowModelReport(Choosing: Boolean): Boolean;
var
  CancelButton: TNewButton;
begin
  ModelDialog := CreateCustomForm(ScaleX(640), ScaleY(440), True, True);
  try
    ModelDialog.Caption := 'Downloaded voice models';
    ModelDialog.OnCloseQuery := @ModelPreviewCloseQuery;
    ModelRemoveCheck := TNewCheckBox.Create(ModelDialog);
    ModelRemoveCheck.Parent := ModelDialog;
    ModelRemoveCheck.SetBounds(ScaleX(16), ScaleY(16), ScaleX(608), ScaleY(32));
    ModelRemoveCheck.Caption := 'Remove the verified Kokoro downloads listed below';
    ModelRemoveCheck.Checked := False;
    ModelRemoveCheck.Enabled := False;
    ModelRemoveCheck.Visible := Choosing;
    ModelDetails := TNewMemo.Create(ModelDialog);
    ModelDetails.Parent := ModelDialog;
    ModelDetails.SetBounds(ScaleX(16), ScaleY(56), ScaleX(608), ScaleY(320));
    ModelDetails.ReadOnly := True;
    ModelDetails.ScrollBars := ssVertical;
    ModelDetails.WordWrap := True;
    ModelDetails.Text := ModelSummary;
    ModelScanButton := TNewButton.Create(ModelDialog);
    ModelScanButton.Parent := ModelDialog;
    ModelScanButton.SetBounds(ScaleX(16), ScaleY(392), ScaleX(240), ScaleY(32));
    ModelScanButton.Caption := 'Check downloaded models';
    ModelScanButton.Enabled := ModelScanAllowed;
    ModelScanButton.Visible := Choosing;
    ModelScanButton.OnClick := @CheckModelPreview;
    ModelContinueButton := TNewButton.Create(ModelDialog);
    ModelContinueButton.Parent := ModelDialog;
    ModelContinueButton.SetBounds(ScaleX(352), ScaleY(392), ScaleX(152), ScaleY(32));
    ModelContinueButton.ModalResult := mrOk;
    ModelContinueButton.Default := True;
    if Choosing then ModelContinueButton.Caption := 'Continue uninstall'
    else ModelContinueButton.Caption := 'Close';
    CancelButton := TNewButton.Create(ModelDialog);
    CancelButton.Parent := ModelDialog;
    CancelButton.SetBounds(ScaleX(512), ScaleY(392), ScaleX(112), ScaleY(32));
    CancelButton.Caption := 'Cancel';
    CancelButton.OnClick := @CancelModelPreview;
    CancelButton.Cancel := True;
    CancelButton.Visible := Choosing;
    Result := ModelDialog.ShowModal = mrOk;
    if Choosing and Result and ModelRemoveCheck.Checked then
      ModelCleanupSelection := ModelPreviewEligible;
  finally
    StopModelPreviewTimer;
    ModelDialog.Free;
  end;
end;

function ChooseModelCleanup: Boolean;
begin
  Result := True;
  ModelCleanupSelection := 0;
  ModelPreviewEligible := 0;
  ModelScanRunning := False;
  ModelScanAllowed := False;
  ModelSummary := 'Downloaded models and other app data will be kept at ' +
    ExpandConstant('{userappdata}\{#MyInstallDir}') + '.' + #13#10 +
    'Sizes have not been measured. Gateway removal is a separate choice.';
  if UninstallSilent() then Exit;
  if GetEnv('OPENCLAW_TRAY_DATA_DIR') <> '' then
    ModelSummary := 'A custom app-data directory is configured. Models are kept; sizes were not scanned.' + #13#10 +
      'Configured path: ' + GetEnv('OPENCLAW_TRAY_DATA_DIR') + #13#10 + ModelSummary
  else if MigrationOperationUnavailable then
    ModelSummary := 'Migration ownership could not be checked. Models are kept.' + #13#10 + ModelSummary
  else
  begin
    ModelScanAllowed := True;
    ModelSummary := ModelSummary + #13#10#13#10 +
      'Continue to keep models, or check downloaded models to preview files eligible for optional removal.';
  end;
  // Keeping models needs no scan. The visible dialog owns the optional check.
  Result := ShowModelReport(True);
end;

procedure RunSelectedModelCleanup;
var
  Code, ReportCode: Integer;
begin
  if ModelCleanupSelection = 0 then Exit;
  // Preserve on changed Store ownership, independently of the Gateway choice.
  if MigrationOperationUnavailable then
  begin
    ModelSummary := 'Models were kept: migration ownership is unavailable.' + #13#10 + ModelSummary;
    Exit;
  end;
  if CheckCompletedStoreMigration <> 0 then
  begin
    ModelSummary := 'Models were kept: Store migration or ownership changed.' + #13#10 + ModelSummary;
    Exit;
  end;
  UninstallProgressForm.StatusLabel.Caption := 'Checking and removing selected voice models...';
  if StartModelHelper(ModelCleanupSelection, ewWaitUntilTerminated, Code) then
  begin
    if ReadModelCompletion(ReportCode) then
      if (Code = 32) and (ReportCode = Code) then Exit;
  end;
  ModelSummary := 'Model cleanup could not be completed or verified. Some selected files may remain.' + #13#10 +
    ModelSummary;
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

procedure DeleteGeneratedAppState;
begin
  if not LocalGatewayCleanupSucceeded then
    Exit;

  if DelTree(ExpandConstant('{app}'), True, True, True) then
    Log('Deleted generated app state from {app}.')
  else
    Log('Generated app state in {app} could not be fully deleted; continuing uninstall.');
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
    RunSelectedModelCleanup;
    RemoveAppAutoStart;
    EnsureLocalGatewayCleanupChoice;
    RunLocalGatewayCleanup;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DeleteGeneratedAppState;
    if not UninstallSilent() then
    begin
      if ModelCleanupSelection = 0 then
        ModelSummary := 'No model removal was requested. Measurements below are from the preview.' + #13#10 + ModelSummary;
      ShowModelReport(False);
    end;
  end;
end;
