<#
.SYNOPSIS
Measure bounded native Gateway startup inside a temporary MXC session and on the host.
.DESCRIPTION
Called by scripts\Invoke-GatewayStartupTrace.ps1. Runs the Gateway entry with a preload
guard that blocks networking and child processes and exits at the
`admission.config-recovery starting` boundary, before any listener. Uses disposable
synthetic profiles in RunDirectory and writes one sanitized result JSON.
Requires PowerShell 7 because the MXC SDK targets .NET 8 or later.
#>
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$NodePath,
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$ResultPath,
    [ValidateSet('isolated-then-host','host-then-isolated')][string]$Order = 'isolated-then-host',
    [string]$MxcSdkVersion = '1.0.1',
    [ValidateRange(10, 3600)][int]$BenchmarkTimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'Run this comparison with PowerShell 7; the MXC SDK cannot load in Windows PowerShell.'
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this comparison unelevated so the measured startup uses the normal user token.'
}
$scratch = [IO.Path]::GetFullPath($RunDirectory)
$payloadRoot = [IO.Path]::GetFullPath($PayloadRoot)
$payloadApp = Join-Path $payloadRoot 'app'
$entry = Join-Path $payloadApp 'openclaw.mjs'
$hostNode = [IO.Path]::GetFullPath($NodePath)
$hostGuard = Join-Path $PSScriptRoot 'startup-guard.cjs'
$hostSelfTest = Join-Path $PSScriptRoot 'startup-guard-selftest.cjs'
$guestLauncher = Join-Path $PSScriptRoot 'guest-launcher.ps1'
$recordPath = Join-Path $scratch 'owned-mxc-session.json'
$resultPath = [IO.Path]::GetFullPath($ResultPath)
$payloadMetadata = Get-Content -LiteralPath (Join-Path $payloadRoot 'payload-metadata.json') -Raw | ConvertFrom-Json
$expectedNodeVersion = [string]$payloadMetadata.nodeVersion
$expectedArchitecture = [string]$payloadMetadata.architecture
$expectedNodeHash = (Get-FileHash -LiteralPath $hostNode -Algorithm SHA256).Hash
$processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$benchmarkTimeoutMs = $BenchmarkTimeoutSeconds * 1000
$selfTestTimeoutMs = 60000
$marker = 'OPENCLAW_STARTUP_COMPARISON='

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class OcComparisonPowerRequest {
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ReasonContext {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public IntPtr SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr handle, int type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr handle, int type);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr handle;
    private OcComparisonPowerRequest(IntPtr handle) { this.handle = handle; }

    public static OcComparisonPowerRequest Start() {
        IntPtr reason = Marshal.StringToHGlobalUni("OpenClaw startup timing comparison");
        try {
            var context = new ReasonContext { Version = 0, Flags = 1, SimpleReasonString = reason };
            IntPtr handle = PowerCreateRequest(ref context);
            if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!PowerSetRequest(handle, 1)) {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(handle);
                throw new Win32Exception(error);
            }
            return new OcComparisonPowerRequest(handle);
        } finally {
            Marshal.FreeHGlobal(reason);
        }
    }

    public string Release() {
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Request already released");
        bool cleared = PowerClearRequest(handle, 1);
        int clearError = cleared ? 0 : Marshal.GetLastWin32Error();
        bool closed = CloseHandle(handle);
        int closeError = closed ? 0 : Marshal.GetLastWin32Error();
        handle = IntPtr.Zero;
        return "clear=" + (cleared ? "ok" : "error-" + clearError) +
            ";close=" + (closed ? "ok" : "error-" + closeError);
    }
}
'@

function Get-AwakeRequestVisibility {
    try {
        $requestOutput = & (Join-Path $env:SystemRoot 'System32\powercfg.exe') /requests 2>$null
        if ($LASTEXITCODE -ne 0) { return 'unverified' }
        if ($requestOutput -match 'OpenClaw startup timing comparison') { return 'visible' }
        return 'not-visible'
    } catch {
        return 'unverified'
    }
}

function Get-SafeExceptionType {
    param([System.Exception]$Exception)
    if ($Exception -and $Exception.GetType().Name -match '^[A-Za-z0-9_]{1,80}$') {
        return $Exception.GetType().Name
    }
    return 'Error'
}

function Assert-NoReparseDirectory {
    param([string]$Path)
    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'A comparison directory is missing or is a reparse point.'
    }
}

function New-ComparisonProfile {
    param([string]$Root)
    if (Test-Path -LiteralPath $Root) {
        throw 'A comparison profile already exists.'
    }
    [void][IO.Directory]::CreateDirectory($Root)
    Assert-NoReparseDirectory $Root
    $profileHome = Join-Path $Root 'home'
    $state = Join-Path $Root 'state'
    $roaming = Join-Path $Root 'roaming'
    $local = Join-Path $Root 'local'
    $temp = Join-Path $Root 'temp'
    foreach ($directory in @($profileHome, $state, $roaming, $local, $temp)) {
        [void][IO.Directory]::CreateDirectory($directory)
        Assert-NoReparseDirectory $directory
    }
    $config = Join-Path $state 'openclaw.json'
    [IO.File]::WriteAllText(
        $config,
        '{"gateway":{"mode":"local"}}',
        [Text.UTF8Encoding]::new($false))
    return [PSCustomObject]@{
        Root = $Root
        Home = $profileHome
        State = $state
        Roaming = $roaming
        Local = $local
        Temp = $temp
        Config = $config
    }
}

function New-ComparisonEnvironment {
    param($Profile, [string]$NodePath)
    $environment = [Collections.Generic.Dictionary[string,string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $systemRoot = $env:SystemRoot
    $nodeDirectory = [IO.Path]::GetDirectoryName($NodePath)
    $values = @{
        SystemRoot = $systemRoot
        windir = $systemRoot
        SystemDrive = [IO.Path]::GetPathRoot($systemRoot).TrimEnd('\')
        ComSpec = (Join-Path $systemRoot 'System32\cmd.exe')
        PATH = ($nodeDirectory + ';' + (Join-Path $systemRoot 'System32'))
        PATHEXT = '.COM;.EXE;.BAT;.CMD'
        HOME = $Profile.Home
        USERPROFILE = $Profile.Home
        APPDATA = $Profile.Roaming
        LOCALAPPDATA = $Profile.Local
        TEMP = $Profile.Temp
        TMP = $Profile.Temp
        XDG_CONFIG_HOME = $Profile.Roaming
        XDG_CACHE_HOME = $Profile.Local
        XDG_DATA_HOME = $Profile.Local
        OPENCLAW_HOME = $Profile.Home
        OPENCLAW_STATE_DIR = $Profile.State
        OPENCLAW_CONFIG_PATH = $Profile.Config
        OPENCLAW_GATEWAY_STARTUP_TRACE = '1'
        NODE_DISABLE_COMPILE_CACHE = '1'
        NO_COLOR = '1'
        FORCE_COLOR = '0'
    }
    foreach ($key in $values.Keys) {
        $environment.Add($key, [string]$values[$key])
    }
    return ,$environment
}

function Read-ComparisonResult {
    param([string]$StandardOutput, [int]$ExitCode, [bool]$TimedOut)
    if ($TimedOut -or $ExitCode -eq 124) {
        return [PSCustomObject]@{ status = 'timeout'; exit_code = $ExitCode }
    }
    $lines = @($StandardOutput -split "`r?`n" |
        Where-Object { $_.StartsWith($marker, [StringComparison]::Ordinal) })
    if ($lines.Count -ne 1) {
        $launcherLines = @($StandardOutput -split "`r?`n" |
            Where-Object { $_.StartsWith('OPENCLAW_COMPARISON_LAUNCHER=', [StringComparison]::Ordinal) })
        if ($launcherLines.Count -eq 1) {
            try {
                $launcher = $launcherLines[0].Substring('OPENCLAW_COMPARISON_LAUNCHER='.Length) | ConvertFrom-Json
                return [PSCustomObject]@{ status = 'guest-launcher-error'; exit_code = $ExitCode; launcher = $launcher }
            } catch {
            }
        }
        return [PSCustomObject]@{ status = 'missing-result'; exit_code = $ExitCode }
    }
    try {
        $result = $lines[0].Substring($marker.Length) | ConvertFrom-Json
    } catch {
        return [PSCustomObject]@{ status = 'invalid-result'; exit_code = $ExitCode }
    }
    if ($result.node_version -ne $expectedNodeVersion -or $result.architecture -ne $expectedArchitecture) {
        return [PSCustomObject]@{ status = 'unexpected-node'; exit_code = $ExitCode }
    }
    $status = if ($ExitCode -eq 0 -and $result.exit_code -eq 0 -and
        $result.outcome -eq 'captured' -and $result.session_confirmed -and
        $null -ne $result.spans.'session-runtime-import' -and
        $null -ne $result.spans.'plugin-config') {
        'captured'
    } else {
        'early-exit'
    }
    return [PSCustomObject]@{
        status = $status
        exit_code = $ExitCode
        trace = $result
    }
}

function Invoke-HostBenchmark {
    param($Profile)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $hostNode
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.WorkingDirectory = $Profile.Root
    $startInfo.Environment.Clear()
    $environment = New-ComparisonEnvironment $Profile $hostNode
    foreach ($key in $environment.Keys) {
        $startInfo.Environment[$key] = $environment[$key]
    }
    foreach ($argument in @('--require', $hostGuard, $entry, 'gateway', 'run', '--allow-unconfigured')) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $started = $false
    try {
        if (-not $process.Start()) {
            throw 'The host comparison process did not start.'
        }
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $finished = $process.WaitForExit($benchmarkTimeoutMs)
        if (-not $finished) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                }
            } catch [InvalidOperationException] {
            }
            if (-not $process.WaitForExit(10000)) {
                throw 'The owned host process did not exit after termination.'
            }
        }
        $timer.Stop()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        [void]$stderrTask.GetAwaiter().GetResult()
        $result = Read-ComparisonResult $stdout $process.ExitCode (-not $finished)
        $result | Add-Member -NotePropertyName wall_ms -NotePropertyValue ([Math]::Round($timer.Elapsed.TotalMilliseconds, 1))
        return $result
    } finally {
        try {
            if ($started -and -not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(10000)) {
                    throw 'The owned host process did not exit during cleanup.'
                }
            }
        } finally {
            $process.Dispose()
        }
    }
}

function Assert-SafeCmdPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.IndexOfAny([char[]]@('"', '%', "`r", "`n")) -ge 0) {
        throw 'A guest command path cannot be represented safely.'
    }
}

function Invoke-GuestNode {
    param(
        $SessionId,
        $Profile,
        [string]$NodePath,
        [string]$GuardPath,
        [string]$ScriptPath,
        [string[]]$Arguments,
        [int]$TimeoutMs,
        [bool]$SelfTest
    )
    foreach ($path in @($NodePath, $GuardPath, $ScriptPath)) {
        Assert-SafeCmdPath $path
    }
    $environment = New-ComparisonEnvironment $Profile $NodePath
    if ($SelfTest) {
        $environment.Add('OPENCLAW_COMPARISON_ENTRY', $entry)
        $environment.Add('OPENCLAW_COMPARISON_GUEST_USER', $script:guestUser)
        $environment.Add('OPENCLAW_COMPARISON_NODE_SHA256', $expectedNodeHash.ToLowerInvariant())
        $environment.Add('OPENCLAW_COMPARISON_ASSERT_SANITIZED', '1')
    }
    $spec = [ordered]@{
        executable = $NodePath
        working_directory = $Profile.Root
        arguments = @('--require', $GuardPath, $ScriptPath) + $Arguments
        environment = $environment
        timeout_ms = $TimeoutMs
    }
    $launcherCode = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(
        [IO.File]::ReadAllText($guestLauncher)))
    $bootstrap = '& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:OPENCLAW_COMPARISON_CODE))))'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
    $powershellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Assert-SafeCmdPath $powershellPath
    $command = 'cmd.exe /d /s /c ""{0}" -NoLogo -NoProfile -NonInteractive -EncodedCommand {1}"' -f
        $powershellPath, $encoded
    if ($command.Length -ge 8000) {
        throw 'The guest command exceeds the safe Windows command length.'
    }
    $request = [Microsoft.Mxc.Sdk.V1.ExecutionRequest]::new($command)
    $request.WorkingDirectory = $Profile.Root
    $request.Environment = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $request.Environment.Add('OPENCLAW_COMPARISON_LAUNCH', ($spec | ConvertTo-Json -Depth 10 -Compress))
    $request.Environment.Add('OPENCLAW_COMPARISON_CODE', $launcherCode)
    $request.Environment.Add('OPENCLAW_COMPARISON_PARENT_ONLY', 'do-not-inherit')
    $request.InheritDefaultEnvironment = $true
    $request.TimeoutMs = $TimeoutMs + 30000
    $process = $null
    $readerOut = $null
    $readerErr = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $process = [Microsoft.Mxc.Sdk.V1.MxcLifecycle]::SpawnInContainer($SessionId, $request, $null)
        if ($null -eq $process.StandardOutput -or $null -eq $process.StandardError) {
            throw 'The guest comparison did not expose both output streams.'
        }
        if ($null -ne $process.StandardInput) {
            $process.StandardInput.Dispose()
        }
        $readerOut = [IO.StreamReader]::new($process.StandardOutput, [Text.Encoding]::UTF8, $true, 4096, $true)
        $readerErr = [IO.StreamReader]::new($process.StandardError, [Text.Encoding]::UTF8, $true, 4096, $true)
        $stdoutTask = $readerOut.ReadToEndAsync()
        $stderrTask = $readerErr.ReadToEndAsync()
        $waitTask = $process.WaitAsync([Threading.CancellationToken]::None)
        $finished = $waitTask.Wait($TimeoutMs + 60000)
        if (-not $finished) {
            try {
                $process.Kill()
            } catch {
            }
            if ($null -ne $process.StandardOutputCloser) { $process.StandardOutputCloser.Close() }
            if ($null -ne $process.StandardErrorCloser) { $process.StandardErrorCloser.Close() }
            return [PSCustomObject]@{ status = 'timeout'; exit_code = -1 }
        }
        $waitResult = $waitTask.GetAwaiter().GetResult()
        $timer.Stop()
        if (-not [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask)).Wait(10000)) {
            if ($null -ne $process.StandardOutputCloser) { $process.StandardOutputCloser.Close() }
            if ($null -ne $process.StandardErrorCloser) { $process.StandardErrorCloser.Close() }
            return [PSCustomObject]@{ status = 'output-not-drained'; exit_code = $waitResult.ExitCode }
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        [void]$stderrTask.GetAwaiter().GetResult()
        $result = Read-ComparisonResult $stdout $waitResult.ExitCode $waitResult.TimedOut
        $result | Add-Member -NotePropertyName wall_ms -NotePropertyValue ([Math]::Round($timer.Elapsed.TotalMilliseconds, 1))
        $result | Add-Member -NotePropertyName warning_count -NotePropertyValue $process.Warnings.Count
        return $result
    } finally {
        if ($null -ne $readerOut) { $readerOut.Dispose() }
        if ($null -ne $readerErr) { $readerErr.Dispose() }
        if ($null -ne $process) { $process.Dispose() }
    }
}

$report = [ordered]@{
    comparison = 'gateway-startup-preflight'
    payload_version = [string]$payloadMetadata.packageVersion
    payload_commit = [string]$payloadMetadata.resolvedCommit
    architecture = $expectedArchitecture
    node_version = $expectedNodeVersion
    mxc_sdk_version = $MxcSdkVersion
    compile_cache = 'disabled-for-both'
    order = $Order
    cutoff = 'after-session-runtime-import-and-plugin-config-before-gateway-listener'
    package_identity = 'separate-unpackaged-sdk-caller'
    awake = [ordered]@{
        request = 'not-attempted'
        visibility_before = 'not-checked'
        visibility_after = 'not-checked'
        release = 'not-needed'
    }
    selftest = 'not-run'
    host = $null
    isolated = $null
    cleanup = [ordered]@{
        stop = 'not-needed'
        deprovision = 'not-needed'
        workspace = 'not-checked'
        ownership_record = 'not-created'
    }
    failure = $null
}
$phase = 'preflight'
$sessionId = $null
$workspace = $null
$script:guestUser = $null
$recordWritten = $false
$hadFailure = $false
$powerRequest = $null

try {
    Assert-NoReparseDirectory $scratch
    if ((Get-Acl -LiteralPath $scratch).AreAccessRulesProtected -ne $true) {
        throw 'The comparison scratch directory is not protected.'
    }
    if ((Test-Path -LiteralPath $recordPath) -or (Test-Path -LiteralPath $resultPath)) {
        throw 'A prior comparison ownership or result record already exists.'
    }
    if ($expectedArchitecture -ne $processArchitecture) {
        throw 'The payload architecture differs from this PowerShell process architecture.'
    }
    if (-not (Test-Path -LiteralPath $entry -PathType Leaf)) {
        throw 'The payload entry openclaw.mjs is missing.'
    }
    if ((Test-Path -LiteralPath (Join-Path $payloadApp '.openclaw-lifecycle-pending')) -or
        (Test-Path -LiteralPath (Join-Path $payloadApp 'dist\openclaw-install-guard'))) {
        throw 'The selected payload has pending package lifecycle work.'
    }
    $guardHash = (Get-FileHash -LiteralPath $hostGuard -Algorithm SHA256).Hash
    $selfTestHash = (Get-FileHash -LiteralPath $hostSelfTest -Algorithm SHA256).Hash

    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $sdk = Join-Path $nugetRoot "microsoft.mxc.sdk\$MxcSdkVersion"
    if (-not (Test-Path -LiteralPath (Join-Path $sdk 'lib\net8.0\Microsoft.Mxc.Sdk.dll') -PathType Leaf)) {
        throw 'The requested MXC SDK version is not in the NuGet package cache.'
    }
    Add-Type -Path (Join-Path $sdk 'lib\net8.0\Microsoft.Mxc.Sdk.dll')
    Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public static class OcMxcComparisonPath { [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool SetDllDirectory(string path); }'
    if (-not [OcMxcComparisonPath]::SetDllDirectory((Join-Path $sdk "runtimes\win-$processArchitecture\native"))) {
        throw 'The cached MXC native library could not be selected.'
    }
    if ([Microsoft.Mxc.Sdk.V1.MxcPlatform]::NativeVersion -ne $MxcSdkVersion) {
        throw 'The loaded MXC native version differs.'
    }

    $phase = 'request-awake'
    $powerRequest = [OcComparisonPowerRequest]::Start()
    $report.awake.request = 'set'
    $report.awake.start_utc = [DateTimeOffset]::UtcNow.ToString('O')
    $report.awake.visibility_before = Get-AwakeRequestVisibility

    $phase = 'provision'
    Write-Output 'comparison.phase=provision-temporary-session'
    $network = [Microsoft.Mxc.Sdk.V1.NetworkPolicy]::new()
    $network.Egress = [Microsoft.Mxc.Sdk.V1.NetworkEgressPolicy]::new()
    $network.Egress.Default = [Microsoft.Mxc.Sdk.V1.NetworkAction]::Allow
    $network.Ingress = [Microsoft.Mxc.Sdk.V1.NetworkIngressPolicy]::new()
    $network.Ingress.Default = [Microsoft.Mxc.Sdk.V1.NetworkAction]::Allow
    $network.Ingress.HostLoopback = [Microsoft.Mxc.Sdk.V1.NetworkAction]::Allow
    $provisionRequest = [Microsoft.Mxc.Sdk.V1.IsolationSessionProvisionRequest]::new($network)
    $provisionRequest.AppId = 'openclaw-startup-comparison-' + [Guid]::NewGuid().ToString('N')
    $provisioned = [Microsoft.Mxc.Sdk.V1.MxcLifecycle]::ProvisionContainer($provisionRequest, $null)
    $sessionId = $provisioned.ContainerId
    $record = [ordered]@{
        id = $sessionId.Value
        application = $provisionRequest.AppId
        created_utc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Compress
    $recordBytes = [Text.UTF8Encoding]::new($false).GetBytes($record)
    $recordStream = [IO.File]::Open($recordPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $recordStream.Write($recordBytes, 0, $recordBytes.Length)
        $recordStream.Flush($true)
        $recordWritten = $true
    } finally {
        $recordStream.Dispose()
    }
    $report.cleanup.ownership_record = 'retained-until-deprovision'
    if ($provisioned.Metadata -isnot [Microsoft.Mxc.Sdk.V1.IsolationSessionProvisionMetadata]) {
        throw 'MXC did not provide the temporary session workspace.'
    }
    if (-not $sessionId.Value.StartsWith('iso:', [StringComparison]::Ordinal) -or
        [string]::IsNullOrWhiteSpace($provisioned.Metadata.AgentUserName) -or
        [string]::IsNullOrWhiteSpace($provisioned.Metadata.AgentUserSid) -or
        $provisioned.Metadata.AgentUserSid -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) {
        throw 'MXC did not report a separate isolated agent identity.'
    }
    $script:guestUser = $provisioned.Metadata.AgentUserName
    $workspace = $provisioned.Metadata.EphemeralWorkspacePath
    [void][Microsoft.Mxc.Sdk.V1.MxcLifecycle]::StartContainer($sessionId, $null)
    Assert-NoReparseDirectory $workspace
    $guestRoot = Join-Path $workspace ('openclaw-startup-comparison-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    if (Test-Path -LiteralPath $guestRoot) {
        throw 'The temporary session comparison directory already exists.'
    }
    [void][IO.Directory]::CreateDirectory($guestRoot)
    Assert-NoReparseDirectory $guestRoot
    $guestNode = Join-Path $guestRoot 'node.exe'
    $guestGuard = Join-Path $guestRoot 'guard.cjs'
    $guestSelfTest = Join-Path $guestRoot 'selftest.cjs'
    [IO.File]::Copy($hostNode, $guestNode, $false)
    [IO.File]::Copy($hostGuard, $guestGuard, $false)
    [IO.File]::Copy($hostSelfTest, $guestSelfTest, $false)
    if ((Get-FileHash -LiteralPath $guestNode -Algorithm SHA256).Hash -ne $expectedNodeHash -or
        (Get-FileHash -LiteralPath $guestGuard -Algorithm SHA256).Hash -ne $guardHash -or
        (Get-FileHash -LiteralPath $guestSelfTest -Algorithm SHA256).Hash -ne $selfTestHash) {
        throw 'The temporary session comparison inputs differ.'
    }
    $hostProfile = New-ComparisonProfile (Join-Path $scratch 'host-profile')
    $guestProfile = New-ComparisonProfile (Join-Path $guestRoot 'isolated-profile')
    if ((Get-FileHash -LiteralPath $hostProfile.Config -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $guestProfile.Config -Algorithm SHA256).Hash) {
        throw 'The comparison profiles do not use identical synthetic configuration.'
    }

    $phase = 'guest-selftest'
    Write-Output 'comparison.phase=verify-isolated-session'
    $self = Invoke-GuestNode -SessionId $sessionId -Profile $guestProfile -NodePath $guestNode `
        -GuardPath $guestGuard -ScriptPath $guestSelfTest -Arguments @() `
        -TimeoutMs $selfTestTimeoutMs -SelfTest $true
    if ($self.status -ne 'captured' -or $self.trace.blocked.child -ne 1 -or
        $self.trace.blocked.network -ne 1 -or
        $self.trace.spans.'session-runtime-import'.elapsed_ms -ne 12.5 -or
        $self.trace.spans.'plugin-config'.elapsed_ms -ne 25) {
        $report.selftest = $self.status
        $report.selftest_details = $self
        throw 'The guest safety or payload-access self-test failed.'
    }
    $report.selftest = 'passed'

    if ($Order -eq 'host-then-isolated') {
        $phase = 'host-benchmark'
        Write-Output 'comparison.phase=measure-outside-mxc'
        $report.host = Invoke-HostBenchmark $hostProfile
        if ($report.host.status -ne 'captured' -or $report.host.trace.blocked.child -ne 0 -or
            $report.host.trace.blocked.network -ne 0) {
            throw 'The host benchmark did not reach the intended boundary unchanged.'
        }
    }

    $phase = 'isolated-benchmark'
    Write-Output 'comparison.phase=measure-inside-mxc'
    if ((Get-FileHash -LiteralPath $guestNode -Algorithm SHA256).Hash -ne $expectedNodeHash) {
        throw 'The guest Node executable changed before the benchmark.'
    }
    $report.isolated = Invoke-GuestNode -SessionId $sessionId -Profile $guestProfile -NodePath $guestNode `
        -GuardPath $guestGuard -ScriptPath $entry -Arguments @('gateway','run','--allow-unconfigured') `
        -TimeoutMs $benchmarkTimeoutMs -SelfTest $false
    if ($report.isolated.status -ne 'captured' -or $report.isolated.trace.blocked.child -ne 0 -or
        $report.isolated.trace.blocked.network -ne 0) {
        throw 'The isolated benchmark did not reach the intended boundary unchanged.'
    }
    if ($Order -eq 'isolated-then-host') {
        $phase = 'host-benchmark'
        Write-Output 'comparison.phase=measure-outside-mxc'
        $report.host = Invoke-HostBenchmark $hostProfile
        if ($report.host.status -ne 'captured' -or $report.host.trace.blocked.child -ne 0 -or
            $report.host.trace.blocked.network -ne 0) {
            throw 'The host benchmark did not reach the intended boundary unchanged.'
        }
    }
    if ((Get-FileHash -LiteralPath $guestNode -Algorithm SHA256).Hash -ne $expectedNodeHash) {
        throw 'The guest Node executable changed during the benchmark.'
    }
    $report.entry_sha256 = (Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash.ToLowerInvariant()
    $report.node_sha256 = $expectedNodeHash.ToLowerInvariant()
} catch {
    $hadFailure = $true
    $safeErrorId = ($_.FullyQualifiedErrorId -split ',')[0]
    if ($safeErrorId -notmatch '^[A-Za-z0-9_.-]{1,80}$') {
        $safeErrorId = 'Error'
    }
    $report.failure = [ordered]@{
        phase = $phase
        exception_type = Get-SafeExceptionType $_.Exception
        error_id = $safeErrorId
        line = $_.InvocationInfo.ScriptLineNumber
    }
    $inner = $_.Exception
    while ($null -ne $inner) {
        if ($inner.GetType().FullName -eq 'Microsoft.Mxc.Sdk.V1.MxcException') {
            $report.failure.sdk_code = $inner.Code.ToString()
            break
        }
        $inner = $inner.InnerException
    }
} finally {
    if ($null -ne $sessionId) {
        Write-Output 'comparison.phase=remove-temporary-session'
        try {
            [void][Microsoft.Mxc.Sdk.V1.MxcLifecycle]::StopContainer($sessionId, $null)
            $report.cleanup.stop = 'reported-success'
        } catch {
            $report.cleanup.stop = 'failed'
            $hadFailure = $true
        }
        try {
            [void][Microsoft.Mxc.Sdk.V1.MxcLifecycle]::DeprovisionContainer($sessionId, $null)
            $report.cleanup.deprovision = 'reported-success'
        } catch {
            $report.cleanup.deprovision = 'failed'
            $hadFailure = $true
        }
        if ($null -ne $workspace) {
            try {
                $report.cleanup.workspace = if (Test-Path -LiteralPath $workspace -ErrorAction Stop) {
                    'still-present'
                } else {
                    'not-found'
                }
                if ($report.cleanup.workspace -eq 'still-present') {
                    $hadFailure = $true
                }
            } catch {
                $report.cleanup.workspace = 'unverified'
            }
        }
        if ($report.cleanup.deprovision -eq 'reported-success' -and $recordWritten) {
            try {
                Remove-Item -LiteralPath $recordPath -Force
                $report.cleanup.ownership_record = 'removed-after-deprovision'
            } catch {
                $report.cleanup.ownership_record = 'retained-after-deprovision'
            }
        }
    }
    if ($null -ne $powerRequest) {
        $report.awake.visibility_after = Get-AwakeRequestVisibility
        try {
            $report.awake.release = $powerRequest.Release()
            $report.awake.release_utc = [DateTimeOffset]::UtcNow.ToString('O')
            if ($report.awake.release -ne 'clear=ok;close=ok') { $hadFailure = $true }
        } catch {
            $report.awake.release = 'failed'
            $hadFailure = $true
        }
    }
    try {
        $json = $report | ConvertTo-Json -Depth 12 -Compress
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
        $resultStream = [IO.File]::Open($resultPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $resultStream.Write($bytes, 0, $bytes.Length) } finally { $resultStream.Dispose() }
        Write-Output $json
    } catch {
        Write-Output '{"comparison":"gateway-startup-preflight","failure":{"phase":"result-write"},"cleanup":"inspect-session"}'
        $hadFailure = $true
    }
}

if ($hadFailure -or $null -eq $report.host -or $null -eq $report.isolated) {
    exit 1
}
