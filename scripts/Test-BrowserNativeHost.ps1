<# Dedicated disposable Windows CI only. Refuses pre-existing native/Store registrations or product generations.
Proves real management/registry/framing/IPC, with a synthetic tray response. Not production Gateway/Chrome approval proof. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = (Resolve-Path -LiteralPath $ExecutablePath).Path
$origin = 'chrome-extension://kcdjddhmeafeomebliikmbpblkmkfoig/'
$nativeKeys = @('Software\Google\Chrome\NativeMessagingHosts\ai.openclaw.browser_bootstrap', 'Software\Chromium\NativeMessagingHosts\ai.openclaw.browser_bootstrap')
$storeKey = 'Software\Google\Chrome\Extensions\kcdjddhmeafeomebliikmbpblkmkfoig'
$rootPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OpenClawTray\browser-native\generations'
if (Test-Path -LiteralPath $rootPath) { throw 'Proof refuses an existing product-generation directory.' }
foreach ($hive in @([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryHive]::LocalMachine)) {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry32,[Microsoft.Win32.RegistryView]::Registry64)) {
        $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,$view)
        try { foreach ($path in @($nativeKeys)+@($storeKey)) {
            $existing=$root.OpenSubKey($path)
            if ($null -ne $existing) { $existing.Dispose(); throw 'Proof refuses an existing native or Store registration.' }
        }} finally { $root.Dispose() }
    }
}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
public static class OpenClawBoundedProofRead {
  public static async Task<byte[]> Read(Stream input,int limit,CancellationToken ct) {
    var data=new byte[limit+1]; int size=0;
    while(true) { int n=await input.ReadAsync(data,size,data.Length-size,ct); if(n==0) { var result=new byte[size];Array.Copy(data,result,size);return result; }
      size+=n;if(size>limit)throw new IOException("Proof transport bound exceeded."); }
  }
}
'@
function Start-Native([string]$File, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($start)
}
function Invoke-Management([string]$Json, [bool]$CloseInput=$true) {
    $p=Start-Native $script:exe @('--manage')
    $ct=[Threading.CancellationTokenSource]::new(60000)
    try {
        $output=[OpenClawBoundedProofRead]::Read($p.StandardOutput.BaseStream,32768,$ct.Token)
        $errors=[OpenClawBoundedProofRead]::Read($p.StandardError.BaseStream,0,$ct.Token)
        $bytes=[Text.Encoding]::UTF8.GetBytes($Json)
        $p.StandardInput.BaseStream.Write($bytes,0,$bytes.Length)
        $p.StandardInput.BaseStream.Flush()
        if ($CloseInput) { $p.StandardInput.Close() }
        [void][Threading.Tasks.Task]::WhenAll($output,$errors,$p.WaitForExitAsync($ct.Token)).GetAwaiter().GetResult()
        $data=$output.GetAwaiter().GetResult()
        if ($data.Length -lt 2 -or $data[-1] -ne 10 -or $data[-2] -ne 125) { throw 'Management response framing invalid.' }
        $text=[Text.UTF8Encoding]::new($false,$true).GetString($data)
        $result=$text | ConvertFrom-Json
        if ($result.v -ne 1 -or $p.ExitCode -ne [int](-not $result.ok)) { throw 'Management exit/result mismatch.' }
        return $result
    } finally {
        $ct.Cancel()
        if (-not $p.HasExited) { $p.Kill($true); [void]$p.WaitForExit(3000) }
        $p.Dispose();$ct.Dispose()
    }
}
function Management-Request([string]$Action,[string]$Store) {
    return (@{v=1;action=$Action;mode='companion-managed-wsl';context=$null;expectedOrigins=@($origin);store=$Store} | ConvertTo-Json -Compress)
}
function Write-Frame([IO.Stream]$Stream,[byte[]]$Bytes) {
    $header=[BitConverter]::GetBytes([uint32]$Bytes.Length)
    $Stream.Write($header,0,4);$Stream.Write($Bytes,0,$Bytes.Length);$Stream.Flush()
}
function Read-Frame([IO.Stream]$Stream) {
    $ct=[Threading.CancellationTokenSource]::new(10000)
    try {
        $header=[byte[]]::new(4)
        [void]$Stream.ReadExactlyAsync([Memory[byte]]$header,$ct.Token).AsTask().GetAwaiter().GetResult()
        $length=[BitConverter]::ToUInt32($header,0)
        if ($length -eq 0 -or $length -gt 4096) { throw 'Invalid native response frame length.' }
        $bytes=[byte[]]::new($length)
        [void]$Stream.ReadExactlyAsync([Memory[byte]]$bytes,$ct.Token).AsTask().GetAwaiter().GetResult()
        return [Text.UTF8Encoding]::new($false,$true).GetString($bytes) | ConvertFrom-Json
    } finally {$ct.Dispose()}
}
function Assert-Exit([Diagnostics.Process]$Process) {
    if (-not $Process.WaitForExit(10000)) { $Process.Kill($true);throw 'Native host did not exit.' }
    if ($Process.ExitCode -ne 0 -or $Process.StandardOutput.BaseStream.ReadByte() -ne -1 -or $Process.StandardError.BaseStream.ReadByte() -ne -1) { throw 'Native output or exit invalid.' }
}
# A published CI artifact is copied into a private install directory before source-admission proof.
$staging=Join-Path ([IO.Path]::GetTempPath()) ('OpenClawBootstrapProof-'+[Guid]::NewGuid().ToString('N'))
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner($sid)
foreach ($principal in @($sid.Value,'S-1-5-18','S-1-5-32-544')) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($principal),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
}
[IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($staging),$acl)
$exe=Join-Path $staging 'OpenClaw.BrowserBootstrap.exe'
if ((Get-Item -LiteralPath $source).Length -gt 268435456) { throw 'Published executable exceeds the proof bound.' }
$sourceBytes=[IO.File]::ReadAllBytes($source)
$copy=[IO.FileStream]::new($exe,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {$copy.Write($sourceBytes,0,$sourceBytes.Length);$copy.Flush($true)} finally {$copy.Dispose()}
if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $exe).Hash) { throw 'Staged executable differs from published artifact.' }
$installed=$false
try {
    & (Join-Path $PSScriptRoot 'test-browser-native-framing.ps1')
    $empty=Invoke-Management (Management-Request 'inspect' 'preserve')
    if (-not $empty.ok -or $empty.registration -ne 'missing' -or (Test-Path -LiteralPath $rootPath)) { throw 'Missing inspection mutated product state.' }
    $invalid=Invoke-Management ((Management-Request 'inspect' 'preserve').Replace('"v":1','"v":1.0'))
    if ($invalid.ok -or $invalid.code -ne 'invalid_request' -or $null -ne $invalid.registration) { throw 'Lexical version gate failed.' }
    $noEof=Invoke-Management (Management-Request 'install' 'request') $false
    if ($noEof.ok -or $noEof.code -ne 'invalid_request' -or (Test-Path -LiteralPath $rootPath)) { throw 'Missing management EOF was not rejected before mutation.' }
    $registration=Invoke-Management (Management-Request 'install' 'preserve')
    $installed=$true
    if (-not $registration.ok -or $registration.registration -ne 'owned' -or $registration.store -ne 'missing') { throw "Native generation installation failed ($($registration.code))." }
    $nativeExe=$registration.installation.launcherPath
    $again=Invoke-Management (Management-Request 'install' 'preserve')
    if (-not $again.ok -or $again.installation.generation -ne $registration.installation.generation) { throw 'Identical registration was not idempotent.' }
    $store=Invoke-Management (Management-Request 'install' 'request')
    if (-not $store.ok -or $store.store -ne 'requested') { throw 'Native-first Store request failed.' }
    $request=[Text.Encoding]::UTF8.GetBytes('{"v":1,"op":"bootstrap","nonce":"AAAAAAAAAAAAAAAAAAAAAA"}')
    $foreign=Start-Native $nativeExe @('chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/','--parent-window=0')
    try { Write-Frame $foreign.StandardInput.BaseStream $request; $reply=Read-Frame $foreign.StandardOutput.BaseStream
        if ($reply.ok -or $reply.code -ne 'origin_forbidden') { throw 'Foreign Chrome origin accepted.' };Assert-Exit $foreign
    } finally {if(-not $foreign.HasExited){$foreign.Kill($true)};$foreign.Dispose()}
    $pipe=[IO.Pipes.NamedPipeServerStream]::new('OpenClawTray.BrowserBootstrap.v1',[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,([IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly))
    try {
        $connected=$pipe.WaitForConnectionAsync()
        $native=Start-Native $nativeExe @($origin,'--parent-window=0')
        try {
            Write-Frame $native.StandardInput.BaseStream $request
            if (-not $connected.Wait(10000)) { throw 'Current-user IPC did not connect.' }
            $incoming=Read-Frame $pipe
            if ($incoming.nonce -ne 'AAAAAAAAAAAAAAAAAAAAAA') { throw 'Native request nonce changed.' }
            Write-Frame $pipe ([Text.Encoding]::UTF8.GetBytes('{"v":1,"ok":true,"nonce":"AAAAAAAAAAAAAAAAAAAAAA","pairingString":"synthetic-proof-only"}'))
            $reply=Read-Frame $native.StandardOutput.BaseStream
            if (-not $reply.ok -or $reply.pairingString -ne 'synthetic-proof-only') { throw 'Native pipe reply failed.' }
            Assert-Exit $native
        } finally {if(-not $native.HasExited){$native.Kill($true)};$native.Dispose()}
    } finally {$pipe.Dispose()}
    $removed=Invoke-Management (Management-Request 'uninstall' 'remove')
    if (-not $removed.ok -or $removed.registration -ne 'missing' -or $removed.store -ne 'missing') { throw 'Owned cleanup failed.' }
    $installed=$false
    $root=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry32)
    try {
        $foreign=$root.CreateSubKey($nativeKeys[0]);$foreign.SetValue('', 'C:\foreign-proof-host.json');$foreign.Dispose()
        $denied=Invoke-Management (Management-Request 'install' 'preserve')
        if ($denied.ok -or $denied.code -ne 'foreign_registration') { throw 'Foreign registration not preserved.' }
        $foreign=$root.OpenSubKey($nativeKeys[0]);try {if($foreign.GetValue('') -ne 'C:\foreign-proof-host.json'){throw 'Foreign entry changed.'}} finally {$foreign.Dispose()}
        $root.DeleteSubKey($nativeKeys[0],$false)
    } finally {$root.Dispose()}
    Write-Host 'NATIVE_BOOTSTRAP_PROOF_OK: actual executable, bounded management EOF, owned generation, both native products/views, framing/origin/current-user IPC, native-first Store request, cleanup and foreign preservation.'
} finally {
    if ($installed) { $cleanup=Invoke-Management (Management-Request 'uninstall' 'remove');if(-not $cleanup.ok){Write-Warning 'Proof cleanup remains incomplete.'} }
    # No recursive deletion of product generations, which may retain references or foreign files.
}
$global:LASTEXITCODE=0
