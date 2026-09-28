<# Runs the real shared Inno pipe client in a disposable GitHub-hosted fixture installer.
No production app is launched. This is not signed release, Chrome consent or WSL authority proof. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath,[Parameter(Mandatory)][string]$CompilerPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Requires a disposable GitHub-hosted Windows runner.' }
$exe=(Resolve-Path -LiteralPath $ExecutablePath).Path
$include=(Resolve-Path (Join-Path $PSScriptRoot 'BrowserBootstrapManagement.iss')).Path
$id=[Guid]::NewGuid().ToString('N')
$proof=Join-Path $env:RUNNER_TEMP ('browser-inno-proof-'+$id)
$install=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('OpenClaw-Inno-Proof-'+$id)
New-Item -ItemType Directory -Path $proof | Out-Null
$keys=@('Software\\Google\\Chrome\\NativeMessagingHosts\\ai.openclaw.browser_bootstrap','Software\\Chromium\\NativeMessagingHosts\\ai.openclaw.browser_bootstrap','Software\\Google\\Chrome\\Extensions\\kcdjddhmeafeomebliikmbpblkmkfoig')
$keys=$keys | ForEach-Object { $_.Replace('\\','\') }
foreach($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)) {
  foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)) {
    $root=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,$view)
    try { foreach($path in $keys) { $key=$root.OpenSubKey($path);if($null -ne $key){$key.Dispose();throw 'Proof refuses existing native/Store registration.'} } } finally {$root.Dispose()}
  }
}
$script=@"
[Setup]
AppName=OpenClaw Management Transport Proof
AppVersion=0.0.0
AppId=OpenClawManagementProof$id
DefaultDirName=$install
PrivilegesRequired=lowest
Uninstallable=yes
CreateAppDir=yes
DisableProgramGroupPage=yes
OutputDir=$proof
OutputBaseFilename=transport-proof
Compression=none
[Files]
Source: "$exe"; DestDir: "{app}\tools\browser-bootstrap"
[Code]
#include "$include"
procedure CurStepChanged(Step: TSetupStep);
begin
  if Step = ssPostInstall then begin
    if not RunBrowserManagement('install', 'request') then RaiseException('INSTALL_PIPE_FAILED');
    if not SaveStringToFile('$proof\install.ok', 'MANAGEMENT_INSTALL_OK', False) then RaiseException('Marker failed');
  end;
end;
procedure CurUninstallStepChanged(Step: TUninstallStep);
begin
  if Step = usUninstall then begin
    if not RunBrowserManagement('uninstall', 'remove') then RaiseException('UNINSTALL_PIPE_FAILED');
    if not SaveStringToFile('$proof\uninstall.ok', 'MANAGEMENT_UNINSTALL_OK', False) then RaiseException('Marker failed');
  end;
end;
"@
$iss=Join-Path $proof 'transport-proof.iss'
[IO.File]::WriteAllText($iss,$script,[Text.UTF8Encoding]::new($true))
& $CompilerPath $iss
if($LASTEXITCODE -ne 0){throw 'Inno transport harness did not compile.'}
function Run-Bounded([string]$Path,[string[]]$Arguments) {
  $p=Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru
  try {if(-not $p.WaitForExit(90000)){$p.Kill($true);throw 'Inno proof timed out.'};if($p.ExitCode -ne 0){throw "Inno proof exit $($p.ExitCode)."}}
  finally {$p.Dispose()}
}
Run-Bounded (Join-Path $proof 'transport-proof.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
if(-not (Test-Path (Join-Path $proof 'install.ok'))){throw 'Installer did not accept a clean management receipt.'}
foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)) {
  $root=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
  try {foreach($path in $keys){$key=$root.OpenSubKey($path);try{if($null -eq $key){throw 'Expected registration missing after install.'}}finally{if($null -ne $key){$key.Dispose()}}}}finally{$root.Dispose()}
}
Run-Bounded (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
if(-not (Test-Path (Join-Path $proof 'uninstall.ok'))){throw 'Uninstaller did not accept a clean management receipt.'}
foreach($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)) {
  $root=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
  try {foreach($path in $keys){$key=$root.OpenSubKey($path);if($null -ne $key){$key.Dispose();throw 'Owned registration remained after uninstall.'}}}finally{$root.Dispose()}
}
Write-Host 'INNO_MANAGEMENT_TRANSPORT_PROOF_OK: actual installer/uninstaller stdin, EOF, bounded receipt and owned registration cleanup.'
$global:LASTEXITCODE=0
