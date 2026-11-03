<#
.SYNOPSIS
Records Defender, file and CPU activity while a person runs the real Companion native Gateway setup.
.DESCRIPTION
Unlike Invoke-GatewayStartupTrace.ps1, which runs a synthetic pre-listener probe, this script
records the full product setup path. That path runs clawctl setup, openclaw config validate, the
isolated Gateway start and readiness. Run it unelevated from PowerShell 7:
  1. launch the Companion build under test with the intended profile before running this script
  2. run this script and approve the UAC prompt for the elevated recorder
  3. when prompted, start native Gateway setup in Companion
  4. the script watches -SetupLogDirectory for a native-setup-*.jsonl attempt that started after
     this script started (an attempt begun during the UAC prompt is accepted; the summary flags
     its unrecorded prefix). It stops when that attempt logs a failure or cancellation; when the
     task-owned Companion (-CompanionProcessId) shows the AI-setup page or the setup page's
     Retry state; when <run>\control\setup-stop is created; or at -SetupTimeoutSeconds.
  5. it stops the recording, copies the setup log into the private run directory and writes a
     sanitized summary

The setup log has no explicit success record. 'listener-verified' (the exact "Verifying the Gateway
endpoint before connecting..." record after the Gateway start acknowledgment) is a milestone, not success: handshake, pairing
and AI preparation can still fail afterwards. Success is 'reached-ai-setup', meaning Gateway
preparation reached the AI-setup page, not full onboarding. It is observed through UI Automation
IDs of the given process, or signalled by writing exactly 'ai-setup-reached' to the control file
after verifying that page. Some unexpected-cancellation paths show Retry without a log record;
those are reported as 'failed-ui-only'. Any other control-file content is a manual stop.

It changes no Defender, power or system settings and adds no exclusions. The ETL and copied log
stay in the private run directory; share only the sanitized summary. The recorder is not
crash-safe. If its window is closed or killed, run the printed
`wpr.exe -cancel -instancename <name>` command from an elevated prompt.
.EXAMPLE
.\scripts\Invoke-GatewaySetupTrace.ps1 -SetupLogDirectory C:\profiles\run1\settings\Logs\Setup `
    -PayloadAppRoot C:\payloads\openclaw-arm64\app
#>
param(
    [Parameter(Mandatory)][string]$SetupLogDirectory,
    [Parameter(Mandatory)][string[]]$PayloadAppRoot,
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'OpenClawStartupTraces'),
    [ValidateRange(60, 3600)][int]$MaxRecordingSeconds = 1800,
    [ValidateRange(30, 3500)][int]$SetupTimeoutSeconds = 1500,
    [int]$CompanionProcessId,
    [ValidateRange(10, 600)][int]$RecorderStartTimeoutSeconds = 120,
    [ValidateRange(10, 900)][int]$RecorderStopTimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolDirectory = Join-Path $PSScriptRoot 'gateway-startup-trace'
Import-Module (Join-Path $toolDirectory 'GatewayStartupTrace.psm1') -Force

function Read-SharedLines {
    # Companion keeps the setup log open for writing; open it with shared access.
    param([string]$Path)
    $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite, Delete')
    try {
        $reader = [IO.StreamReader]::new($stream)
        return $reader.ReadToEnd() -split "`r?`n"
    } finally {
        $stream.Dispose()
    }
}

function Get-CurrentSetupAttempt {
    # Returns the newest log whose last attempt started at or after $SinceTicks.
    param([string]$Directory, [Int64]$SinceTicks)
    if (-not (Test-Path -LiteralPath $Directory)) { return $null }
    $logs = @(Get-ChildItem -LiteralPath $Directory -Filter 'native-setup-*.jsonl' -File |
        Where-Object { $_.LastWriteTimeUtc.ToFileTimeUtc() -ge $SinceTicks } |
        Sort-Object LastWriteTimeUtc -Descending)
    foreach ($log in $logs) {
        $parsed = Get-NativeSetupMilestones -Lines (Read-SharedLines $log.FullName)
        $started = @($parsed.Milestones | Where-Object Name -eq 'attempt-started')
        if ($started.Count -eq 1 -and $started[0].Ticks -ge $SinceTicks) {
            return [pscustomobject]@{ Path = $log.FullName; Parsed = $parsed }
        }
    }
    return $null
}

if ($PSVersionTable.PSEdition -ne 'Core') { throw 'Run this script with PowerShell 7 (pwsh).' }
if (Test-IsElevated) {
    throw 'Run this script unelevated. Only the recorder is elevated, through a UAC prompt.'
}
$PayloadAppRoot = @($PayloadAppRoot | ForEach-Object {
    $full = [IO.Path]::GetFullPath($_)
    if (-not (Test-Path -LiteralPath (Join-Path $full 'openclaw.mjs') -PathType Leaf)) {
        throw "Payload app root $full is missing openclaw.mjs."
    }
    $full
})
$SetupLogDirectory = [IO.Path]::GetFullPath($SetupLogDirectory)
if ($CompanionProcessId -and $null -eq (Get-Process -Id $CompanionProcessId -ErrorAction SilentlyContinue)) {
    throw "Companion process $CompanionProcessId is not running."
}

$runId = [guid]::NewGuid().ToString('N')
# Setup may begin while UAC or WPR start-up is still pending; such an attempt is still the traced one.
$controllerStartTicks = [DateTime]::UtcNow.ToFileTimeUtc()
if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}
$runDirectory = New-PrivateDirectory (Join-Path $OutputDirectory ("{0:yyyyMMdd-HHmmss}-setup-{1}" -f [DateTime]::UtcNow, $runId.Substring(0, 8)))
$controlDirectory = Join-Path $runDirectory 'control'
New-Item -ItemType Directory -Path $controlDirectory | Out-Null
$tracePath = Join-Path $runDirectory 'setup-trace.etl'
$setupResultPath = Join-Path $runDirectory 'setup-result.json'
$summaryPath = Join-Path $runDirectory 'setup-summary.json'
$manualStopPath = Join-Path $controlDirectory 'setup-stop'
$instanceName = "OpenClawStartup-$runId"
$recorderPaths = $null
$attempt = $null
$stopReason = 'not-started'
$errorDetail = $null
$recordingStartTicks = 0L
$recordingStopTicks = 0L
$uiStopTicks = 0L

Write-Host "Run directory: $runDirectory"
try {
    $recorderPaths = Start-GatewayTraceRecorder -ToolDirectory $toolDirectory -InstanceName $instanceName `
        -TracePath $tracePath -ControlDirectory $controlDirectory -MaxRecordingSeconds $MaxRecordingSeconds `
        -StartTimeoutSeconds $RecorderStartTimeoutSeconds
    $recordingStartTicks = [DateTime]::UtcNow.ToFileTimeUtc()
    if (Test-IsElevated) { throw 'The setup controller unexpectedly became elevated.' }

    Write-Host 'Recording. Start native Gateway setup in Companion now.'
    Write-Host "To stop early, create: $manualStopPath (content 'ai-setup-reached' only after verifying the AI-setup page)"
    $deadline = [DateTime]::UtcNow.AddSeconds($SetupTimeoutSeconds)
    $stopReason = 'timeout'
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $manualStopPath) {
            $stopReason = Read-SetupStopSignal $manualStopPath
            $uiStopTicks = [DateTime]::UtcNow.ToFileTimeUtc()
            break
        }
        $attempt = Get-CurrentSetupAttempt -Directory $SetupLogDirectory -SinceTicks $controllerStartTicks
        if ($null -ne $attempt) {
            $outcome = $attempt.Parsed.Outcome
            if ($outcome -in @('failed', 'cancelled')) {
                Start-Sleep -Seconds 5
                $stopReason = $outcome
                break
            }
            if ($CompanionProcessId) {
                $uiState = try { Get-NativeSetupUiState -ProcessId $CompanionProcessId } catch { 'unknown' }
                if ($uiState -eq 'ai-setup') {
                    $stopReason = 'ai-setup-reached'
                    $uiStopTicks = [DateTime]::UtcNow.ToFileTimeUtc()
                    break
                }
                if ($uiState -eq 'retry') {
                    # Give a logged failure or cancellation time to land before classifying.
                    $uiStopTicks = [DateTime]::UtcNow.ToFileTimeUtc()
                    Start-Sleep -Seconds 5
                    $stopReason = 'ui-retry'
                    break
                }
            }
        }
        Start-Sleep -Seconds 2
    }
} catch {
    $stopReason = 'controller-error'
    $errorDetail = $_.Exception.Message
    Write-Warning $errorDetail
} finally {
    $recordingStopTicks = [DateTime]::UtcNow.ToFileTimeUtc()
    if ($null -ne $recorderPaths) {
        Stop-GatewayTraceRecorder -InstanceName $instanceName -Paths $recorderPaths -StopTimeoutSeconds $RecorderStopTimeoutSeconds
    } elseif (Test-Path -LiteralPath $controlDirectory) {
        # The recorder may have started after the wait timed out; ask it to stop as well.
        [IO.File]::WriteAllText((Join-Path $controlDirectory 'recorder-stop'), 'stop')
    }
}

$attempt = Get-CurrentSetupAttempt -Directory $SetupLogDirectory -SinceTicks $controllerStartTicks
$copiedLog = $null
if ($null -ne $attempt) {
    $copiedLog = Join-Path $runDirectory 'native-setup.jsonl'
    [IO.File]::WriteAllLines($copiedLog, [string[]](Read-SharedLines $attempt.Path))
}
$parsedOutcome = if ($attempt) { $attempt.Parsed.Outcome } else { 'not-observed' }
# Assign through @() so an empty result serializes as [] rather than null.
$milestones = @(if ($attempt) { $attempt.Parsed.Milestones })
$outcome = Resolve-NativeSetupOutcome -ParsedOutcome $parsedOutcome -StopReason $stopReason
if ($attempt -and $uiStopTicks -gt 0 -and $outcome -in @('reached-ai-setup', 'failed-ui-only')) {
    $name = if ($outcome -eq 'reached-ai-setup') { 'ai-setup-page' } else { 'ui-retry-shown' }
    $milestones += [pscustomobject]@{ Name = $name; Ticks = $uiStopTicks }
}
$result = [ordered]@{
    schema = 'openclaw-setup-trace-result/v2'
    stop_reason = $stopReason
    error_detail = $errorDetail
    recording_start_filetime_utc = $recordingStartTicks
    recording_stop_filetime_utc = $recordingStopTicks
    outcome = $outcome
    failed_step = if ($attempt) { $attempt.Parsed.FailedStep } else { $null }
    milestones = $milestones
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $setupResultPath -Encoding utf8
Write-Host "Setup outcome: $($result.outcome); stop reason: $stopReason"

if ($null -ne $attempt -and (Test-Path -LiteralPath $tracePath)) {
    & (Join-Path $toolDirectory 'Get-GatewaySetupTraceSummary.ps1') -TracePath $tracePath -ResultPath $setupResultPath `
        -PayloadAppRoot $PayloadAppRoot -OutputPath $summaryPath | Out-Null
    Write-Host "Sanitized summary: $summaryPath"
}
if ($stopReason -notin @('ai-setup-reached', 'ui-retry', 'failed', 'cancelled', 'manual')) { exit 1 }
