<#
.SYNOPSIS
Elevated half of scripts\Invoke-GatewayStartupTrace.ps1. Records one named WPR instance.
.DESCRIPTION
Starts Microsoft Defender's Scans.Light profile together with FileIO.Light, DiskIO.Light
and CPU.Light in one file-mode WPR instance, then waits for the controller's stop file.
It stops and saves only that instance on request and cancels it at the deadline or on
any failure in this process.

The deadline is enforced by this process. It is not crash-safe: if this window is closed
or the process is killed, the named instance keeps recording. Recover with
`wpr.exe -cancel -instancename <InstanceName>` from an elevated prompt.
#>
param(
    [Parameter(Mandatory)][string]$InstanceName,
    [Parameter(Mandatory)][string]$TracePath,
    [Parameter(Mandatory)][string]$ControlDirectory,
    [ValidateRange(60, 3600)][int]$MaxRecordingSeconds = 600
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$wpr = Join-Path $env:SystemRoot 'System32\wpr.exe'
$readyPath = Join-Path $ControlDirectory 'recorder-ready.json'
$stopPath = Join-Path $ControlDirectory 'recorder-stop'
$statusPath = Join-Path $ControlDirectory 'recorder-status.json'
$started = $false
$startAttempted = $false
$stopSaved = $false
$instanceRecordingAfter = $false
$outcome = 'not-started'

function Write-ControlFile {
    param([string]$Path, $Value)
    $json = $Value | ConvertTo-Json -Compress
    $temporaryPath = "$Path.$PID.tmp"
    [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryPath, $Path, $true)
}

# WPR save can take minutes on large traces; status and cancel should return quickly.
$wprStartTimeoutSeconds = 60
$wprStopTimeoutSeconds = 180
$wprControlTimeoutSeconds = 30
if ($env:OPENCLAW_TRACE_WPR_START_TIMEOUT_SECONDS) { $wprStartTimeoutSeconds = [int]$env:OPENCLAW_TRACE_WPR_START_TIMEOUT_SECONDS }
if ($env:OPENCLAW_TRACE_WPR_STOP_TIMEOUT_SECONDS) { $wprStopTimeoutSeconds = [int]$env:OPENCLAW_TRACE_WPR_STOP_TIMEOUT_SECONDS }
if ($env:OPENCLAW_TRACE_WPR_CONTROL_TIMEOUT_SECONDS) { $wprControlTimeoutSeconds = [int]$env:OPENCLAW_TRACE_WPR_CONTROL_TIMEOUT_SECONDS }

function Invoke-BoundedWpr {
    <# Runs wpr.exe with a deadline. Kills only that child on timeout and returns its exit code, or $null on timeout. #>
    param([string]$Arguments, [int]$TimeoutSeconds, [switch]$CaptureOutput)
    $info = [Diagnostics.ProcessStartInfo]::new($wpr, $Arguments)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = [bool]$CaptureOutput
    $info.RedirectStandardError = [bool]$CaptureOutput
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = if ($CaptureOutput) { $process.StandardOutput.ReadToEndAsync() } else { $null }
        $stderr = if ($CaptureOutput) { $process.StandardError.ReadToEndAsync() } else { $null }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            return [pscustomobject]@{ ExitCode = $null; Output = '' }
        }
        $process.WaitForExit()
        $text = if ($CaptureOutput) { $stdout.Result + $stderr.Result } else { '' }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $text }
    } finally {
        $process.Dispose()
    }
}

function Test-InstanceRecording {
    # wpr requires -instancename to be the last argument.
    $result = Invoke-BoundedWpr "-status -instancename $InstanceName" $wprControlTimeoutSeconds -CaptureOutput
    if ($null -eq $result.ExitCode) { throw 'WPR status timed out.' }
    return ($result.Output -notmatch 'not recording')
}

try {
    if ($InstanceName -notmatch '^OpenClawStartup-[0-9a-f]{32}$') {
        throw 'The recorder instance name is not a task-owned OpenClawStartup name.'
    }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The recorder must run elevated.'
    }
    if (Test-Path -LiteralPath $TracePath) {
        throw 'The trace output already exists.'
    }
    if (Test-InstanceRecording) {
        $instanceRecordingAfter = $true
        throw 'The named instance is already recording, so this recorder does not own it.'
    }
    $module = Get-Module -ListAvailable ConfigDefenderPerformance | Select-Object -First 1
    if ($null -eq $module) {
        throw 'The ConfigDefenderPerformance module is unavailable.'
    }
    $defenderProfile = Join-Path $module.ModuleBase 'MSFT_MpPerformanceRecording.wprp'
    if (-not (Test-Path -LiteralPath $defenderProfile -PathType Leaf)) {
        throw 'The Defender performance profile is unavailable.'
    }

    $startAttempted = $true
    $start = Invoke-BoundedWpr "-start `"$defenderProfile!Scans.Light`" -start FileIO.Light -start DiskIO.Light -start CPU.Light -filemode -instancename $InstanceName" $wprStartTimeoutSeconds
    if ($null -eq $start.ExitCode) {
        throw "WPR start did not finish within $wprStartTimeoutSeconds seconds."
    }
    if ($start.ExitCode -ne 0) {
        throw "WPR start failed with exit code $($start.ExitCode)."
    }
    $started = $true
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($MaxRecordingSeconds)
    Write-ControlFile $readyPath ([ordered]@{
        instance = $InstanceName
        started_utc = [DateTimeOffset]::UtcNow.ToString('O')
        deadline_utc = $deadline.ToString('O')
    })
    Write-Host "Recording instance $InstanceName until the controller finishes (deadline $($deadline.ToLocalTime()))."
    Write-Host 'Keep this window open. Closing it leaves the instance recording.'

    while (-not (Test-Path -LiteralPath $stopPath)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw 'The recording deadline elapsed before the controller finished.'
        }
        Start-Sleep -Milliseconds 500
    }

    $stop = Invoke-BoundedWpr "-stop `"$TracePath`" -instancename $InstanceName" $wprStopTimeoutSeconds
    if ($null -eq $stop.ExitCode) {
        throw "WPR stop did not finish within $wprStopTimeoutSeconds seconds."
    }
    if ($stop.ExitCode -ne 0) {
        throw "WPR stop failed with exit code $($stop.ExitCode)."
    }
    $started = $false
    $stopSaved = $true
    $outcome = 'saved'
} catch {
    $outcome = 'failed: ' + $_.Exception.Message
    Write-Warning $outcome
} finally {
    $cancel = 'not-needed'
    if ($started -or ($startAttempted -and -not $stopSaved)) {
        $cancelResult = Invoke-BoundedWpr "-cancel -instancename $InstanceName" $wprControlTimeoutSeconds
        $cancel = if ($null -eq $cancelResult.ExitCode) { 'cancel-timed-out' }
            elseif ($cancelResult.ExitCode -eq 0) { 'canceled' }
            else { "cancel-exit-$($cancelResult.ExitCode)" }
    }
    if ($started -or $startAttempted) {
        try { $instanceRecordingAfter = Test-InstanceRecording } catch { $instanceRecordingAfter = $true }
    }
    Write-ControlFile $statusPath ([ordered]@{
        instance = $InstanceName
        outcome = $outcome
        cancel = $cancel
        instance_recording_after = $instanceRecordingAfter
        finished_utc = [DateTimeOffset]::UtcNow.ToString('O')
    })
    if ($instanceRecordingAfter) {
        Write-Warning "Instance $InstanceName may still be recording. Run: wpr.exe -cancel -instancename $InstanceName"
        Read-Host 'Press Enter to close this window after cancelling'
    }
}
