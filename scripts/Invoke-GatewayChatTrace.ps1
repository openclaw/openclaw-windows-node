<#
.SYNOPSIS
Records a bounded, user-driven real chat interaction without reading chat content.
.DESCRIPTION
Run unelevated in PowerShell 7 with the already-running Companion, Gateway and optional
local-model PIDs. Only the recorder requests elevation. This script sends no prompts,
reads no chat logs or credentials, and does not launch, stop or reconfigure the app.
Use Write-GatewayChatTraceMarker.ps1 in a second console to mark send, first-response
and completed (or failed/cancelled). These are operator observations, not provider TTFT.
Private ETL and resource samples stay in the ACL-restricted run directory.
#>
param(
    [Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int[]]$ProcessId,
    [string[]]$PayloadAppRoot = @(),
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'OpenClawStartupTraces'),
    [string]$BuildIdentityPath,
    [ValidateRange(30, 3500)][int]$ChatTimeoutSeconds = 600,
    [ValidateRange(60, 3600)][int]$MaxRecordingSeconds = 900,
    [ValidateRange(10, 600)][int]$RecorderStartTimeoutSeconds = 120,
    [ValidateRange(90, 2400)][int]$RecorderStopTimeoutSeconds = 2000
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$toolDirectory = Join-Path $PSScriptRoot 'gateway-startup-trace'
Import-Module (Join-Path $toolDirectory 'GatewayStartupTrace.psm1') -Force
Import-Module (Join-Path $toolDirectory 'GatewayResourceTrace.psm1') -Force
if ($PSVersionTable.PSEdition -ne 'Core') { throw 'Run this script with PowerShell 7.' }
if (Test-IsElevated) { throw 'Run the chat controller unelevated. Only the recorder requests UAC approval.' }
if ($MaxRecordingSeconds -le $ChatTimeoutSeconds + 60) { throw 'MaxRecordingSeconds must exceed ChatTimeoutSeconds by at least 60 seconds.' }
$wprStopTimeout = [int][Math]::Min(1800, [Math]::Max(180, [Math]::Ceiling($MaxRecordingSeconds / 2)))
if ($RecorderStopTimeoutSeconds -lt $wprStopTimeout + 90) { throw 'RecorderStopTimeoutSeconds must exceed the WPR stop timeout by at least 90 seconds.' }
$PayloadAppRoot = @($PayloadAppRoot | ForEach-Object {
    $root = [IO.Path]::GetFullPath($_)
    if (-not (Test-Path -LiteralPath (Join-Path $root 'openclaw.mjs') -PathType Leaf)) { throw 'A payload app root is missing openclaw.mjs.' }
    $root
})
$runId = [guid]::NewGuid().ToString('N')
if (-not (Test-Path -LiteralPath $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory | Out-Null }
$run = New-PrivateDirectory (Join-Path $OutputDirectory ("{0:yyyyMMdd-HHmmss}-chat-{1}" -f [DateTime]::UtcNow, $runId.Substring(0, 8)))
$control = Join-Path $run 'control'
New-Item -ItemType Directory -Path $control | Out-Null
Write-ResourceTraceConfiguration -ControlDirectory $control -ProcessIds $ProcessId -BuildIdentityPath $BuildIdentityPath
$instance = "OpenClawStartup-$runId"
$tracePath = Join-Path $run 'chat-trace.etl'
$resultPath = Join-Path $run 'chat-result.json'
$paths = $null
$recorder = $null
$markers = @()
$reason = 'not-started'
$start = 0L
$end = 0L
Write-Host "Private chat run: $run"
try {
    $paths = Start-GatewayTraceRecorder -ToolDirectory $toolDirectory -InstanceName $instance -TracePath $tracePath `
        -ControlDirectory $control -MaxRecordingSeconds $MaxRecordingSeconds -StartTimeoutSeconds $RecorderStartTimeoutSeconds `
        -WprStartTimeoutSeconds 60 -WprStopTimeoutSeconds $wprStopTimeout -WprControlTimeoutSeconds 30
    $start = [DateTime]::UtcNow.ToFileTimeUtc()
    [IO.File]::WriteAllText((Join-Path $control 'chat-ready'), $instance)
    Write-Host 'Recording. Use your existing chat normally. Mark each observed event from a second console:'
    Write-Host ".\scripts\Write-GatewayChatTraceMarker.ps1 -RunDirectory '$run' -Event send"
    Write-Host 'Then mark first-response and completed, or failed/cancelled. No prompt or response text is accepted.'
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $reason = 'timeout'
    while ($deadline.Elapsed.TotalSeconds -lt $ChatTimeoutSeconds) {
        $markers = @(Get-ChatTraceMarkers -ControlDirectory $control -StartTicks $start -EndTicks ([DateTime]::UtcNow.ToFileTimeUtc()))
        if ($markers.Count -gt 0) {
            $timing = Get-ChatTimingSummary -Markers $markers
            if ($timing.outcome -ne 'incomplete') { $reason = $timing.outcome; break }
        }
        if (Test-Path -LiteralPath (Join-Path $control 'chat-stop')) { $reason = 'manual'; break }
        Start-Sleep -Milliseconds 250
    }
} catch {
    $reason = 'controller-error'
    Write-Warning $_.Exception.Message
} finally {
    $end = [DateTime]::UtcNow.ToFileTimeUtc()
    $ready = Join-Path $control 'chat-ready'
    if (Test-Path -LiteralPath $ready) { Remove-Item -LiteralPath $ready }
    if ($paths) {
        $recorder = Stop-GatewayTraceRecorder -InstanceName $instance -Paths $paths -StopTimeoutSeconds $RecorderStopTimeoutSeconds
    } else {
        [IO.File]::WriteAllText((Join-Path $control 'recorder-stop'), 'stop')
        $recorder = Read-GatewayTraceRecorderStatus -InstanceName $instance -StatusPath (Join-Path $control 'recorder-status.json')
    }
}
$result = [ordered]@{
    schema = 'openclaw-chat-trace-result/v1'
    stop_reason = $reason
    recording_start_filetime_utc = $start
    recording_stop_filetime_utc = $end
    markers = $markers
    recording_saved = [bool]($recorder -and $recorder.outcome -eq 'saved' -and
        $recorder.instance_recording_after -eq $false -and (Test-Path -LiteralPath $tracePath))
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8
if (-not $result.recording_saved) { throw 'Chat trace was not saved. Check the exact named recorder status and recovery command.' }
& (Join-Path $toolDirectory 'Get-GatewayChatTraceSummary.ps1') -TracePath $tracePath -ResultPath $resultPath `
    -PayloadAppRoot $PayloadAppRoot -OutputPath (Join-Path $run 'chat-summary.json') | Out-Null
Write-Host "Sanitized summary: $(Join-Path $run 'chat-summary.json')"
if ($reason -in @('timeout', 'manual', 'controller-error', 'not-started')) { exit 1 }
