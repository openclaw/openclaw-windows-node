<#
.SYNOPSIS
Summarizes Defender scans of Gateway payload files during a traced Companion native setup.
.DESCRIPTION
Reads a private ETL and setup-result.json recorded by scripts\Invoke-GatewaySetupTrace.ps1.
It selects Defender scans of files under any -PayloadAppRoot (pass both a junction and its
target when the payload is reached through one), from all processes, then reports:
  - scan-interval union overall and per setup-log milestone window
  - per-process scan counts using anonymous indexes (no PIDs or process names)
  - top package groups by summed scan duration, as payload-relative names only

Setup milestones come from the Companion setup log, not from Gateway bootstrap diagnostics,
so they are not the synthetic probe's session-runtime-import or plugin-config phases. Scan
union correlates with, but does not prove, blocking. Summed scan time and windows must not
be added as exclusive contributions.
#>
param(
    [Parameter(Mandatory)][string]$TracePath,
    [Parameter(Mandatory)][string]$ResultPath,
    [Parameter(Mandatory)][string[]]$PayloadAppRoot,
    [string]$OutputPath,
    [ValidateRange(100, 1000000)][int]$TopFiles = 100000,
    [ValidateRange(1, 100000)][int]$TopScansPerFile = 10000,
    [ValidateRange(1, 100)][int]$TopGroups = 15
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'GatewayStartupTrace.psm1') -Force

$ticksPerMs = 10000.0
$roots = @($PayloadAppRoot | ForEach-Object {
    # pwsh -File passes "-PayloadAppRoot a,b" as one string; reject it instead of matching nothing.
    if ($_ -match ',') { throw 'Pass each payload app root as a separate array element, not a comma-joined string.' }
    $full = [IO.Path]::GetFullPath($_).TrimEnd('\') + '\'
    if (-not (Test-Path -LiteralPath (Join-Path $full 'openclaw.mjs') -PathType Leaf)) {
        throw 'A payload app root does not contain openclaw.mjs.'
    }
    $full
})
# Only fixed reason codes reach the shareable summary; raw error text stays in the private result.
$allowedStopReasons = @('not-started', 'timeout', 'manual', 'ai-setup-reached', 'ui-retry', 'failed', 'cancelled', 'controller-error')
$result = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json -DateKind String
$report = Get-CompleteMpPerformanceReport -Path $TracePath -TopFiles $TopFiles -TopScansPerFile $TopScansPerFile

function Get-RelativePayloadPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $normalized = $Path -replace '^\\\\\?\\', ''
    foreach ($root in $roots) {
        if ($normalized.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            return $normalized.Substring($root.Length)
        }
    }
    return $null
}

function Get-SummedTicks {
    param([object[]]$Intervals)
    [Int64]$total = 0
    foreach ($interval in $Intervals) { $total += $interval.End - $interval.Start }
    return $total
}

$recordingStart = [Int64]$result.recording_start_filetime_utc
$recordingStop = [Int64]$result.recording_stop_filetime_utc
$matchedReportFiles = 0
$scans = @(foreach ($file in @($report.TopFiles)) {
    $relative = Get-RelativePayloadPath $file.Path
    if ($null -eq $relative) { continue }
    $matchedReportFiles++
    foreach ($scan in @($file.Scans)) {
        $processId = if ($scan.PSObject.Properties['ProcessId']) { [int]$scan.ProcessId } else { -1 }
        [pscustomobject]@{ Path = $relative; ProcessId = $processId; Start = [Int64]$scan.StartTime; End = [Int64]$scan.EndTime }
    }
})
$scans = @(Get-ClippedIntervals -Intervals $scans -WindowStart $recordingStart -WindowEnd $recordingStop)
if ($matchedReportFiles -eq 0) {
    Write-Warning 'No Defender report file matched the payload app roots. Check the roots before reading zero scans as a measurement.'
}

$processIndex = @{}
foreach ($scan in $scans | Sort-Object Start) {
    if (-not $processIndex.ContainsKey($scan.ProcessId)) { $processIndex[$scan.ProcessId] = $processIndex.Count + 1 }
}
$processes = foreach ($group in $scans | Group-Object ProcessId) {
    $first = [Int64]($group.Group | Measure-Object Start -Minimum).Minimum
    $last = [Int64]($group.Group | Measure-Object End -Maximum).Maximum
    [ordered]@{
        process = 'P{0}' -f $processIndex[[int]$group.Name]
        attributed = ([int]$group.Name -ge 0)
        payload_scans = $group.Count
        payload_files = @($group.Group | Select-Object -ExpandProperty Path -Unique).Count
        first_scan_offset_ms = [Math]::Round(($first - $recordingStart) / $ticksPerMs, 1)
        last_scan_offset_ms = [Math]::Round(($last - $recordingStart) / $ticksPerMs, 1)
        scan_union_ms = [Math]::Round((Get-IntervalUnionLength $group.Group) / $ticksPerMs, 1)
        summed_scan_ms = [Math]::Round((Get-SummedTicks $group.Group) / $ticksPerMs, 1)
    }
}
$processes = @($processes | Sort-Object { $_.process })

$milestones = @($result.milestones | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Ticks = [Int64]$_.Ticks } })
$origin = if ($milestones.Count -gt 0) { ($milestones | Measure-Object Ticks -Minimum).Minimum } else { $recordingStart }
$windowList = @()
if ($milestones.Count -gt 0) {
    $windowList += [pscustomobject]@{ Phase = 'recording-start..attempt-started'; Start = $recordingStart; End = [Int64]$origin }
    $windowList += @(Get-NativeSetupWindows -Milestones $milestones -EndTicks $recordingStop)
}
$windows = foreach ($window in $windowList) {
    if ($window.End -le $window.Start) { continue }
    # Windows that began before recording are measured only from recording start.
    $recordedStart = [Math]::Max([Int64]$window.Start, $recordingStart)
    if ($window.End -le $recordedStart) { continue }
    $clipped = @(Get-ClippedIntervals -Intervals $scans -WindowStart $recordedStart -WindowEnd $window.End)
    $span = ($window.End - $recordedStart) / $ticksPerMs
    $union = (Get-IntervalUnionLength $clipped) / $ticksPerMs
    [ordered]@{
        phase = $window.Phase
        start_offset_from_attempt_ms = [Math]::Round(($window.Start - $origin) / $ticksPerMs, 1)
        unrecorded_prefix_ms = [Math]::Round(($recordedStart - $window.Start) / $ticksPerMs, 1)
        elapsed_ms = [Math]::Round($span, 1)
        scan_union_ms = [Math]::Round($union, 1)
        scan_union_fraction = if ($span -gt 0) { [Math]::Round($union / $span, 3) } else { 0 }
        scans = $clipped.Count
    }
}
$groups = $scans | Group-Object { Get-PayloadPackageGroup $_.Path } | ForEach-Object {
    [pscustomobject]@{
        group = $_.Name
        files = @($_.Group | Select-Object -ExpandProperty Path -Unique).Count
        scans = $_.Count
        summed_scan_ms = [Math]::Round((Get-SummedTicks $_.Group) / $ticksPerMs, 1)
    }
} | Sort-Object summed_scan_ms -Descending | Select-Object -First $TopGroups

$attemptStart = @($milestones | Where-Object Name -eq 'attempt-started' | Select-Object -First 1)
$terminal = @($milestones | Where-Object Name -in 'setup-failed', 'setup-cancelled', 'ai-setup-page', 'ui-retry-shown' |
    Sort-Object Ticks | Select-Object -First 1)
$attempt = if ($attemptStart.Count -eq 1 -and $terminal.Count -eq 1) {
    [ordered]@{
        terminal_event = $terminal[0].Name
        attempt_to_terminal_ms = [Math]::Round(($terminal[0].Ticks - $attemptStart[0].Ticks) / $ticksPerMs, 1)
    }
} else { $null }

$summary = [ordered]@{
    schema = 'openclaw-setup-trace-summary/v1'
    outcome = $result.outcome
    failed_step = $result.failed_step
    stop_reason = if ($result.stop_reason -in $allowedStopReasons) { $result.stop_reason } else { 'controller-error' }
    attempt = $attempt
    attempt_started_before_recording = ($milestones.Count -gt 0 -and $origin -lt $recordingStart)
    recording_ms = [Math]::Round(($recordingStop - $recordingStart) / $ticksPerMs, 1)
    report_files = @($report.TopFiles).Count
    report_files_matching_payload = $matchedReportFiles
    report_complete = $true
    payload_scans = $scans.Count
    payload_files_scanned = @($scans | Select-Object -ExpandProperty Path -Unique).Count
    scan_union_ms = [Math]::Round((Get-IntervalUnionLength $scans) / $ticksPerMs, 1)
    summed_scan_ms = [Math]::Round((Get-SummedTicks $scans) / $ticksPerMs, 1)
    processes = $processes
    windows = @($windows)
    top_groups = @($groups)
    interpretation = 'Windows come from Companion setup-log milestones plus the observed UI stop. listener-verified is not success; reached-ai-setup means Gateway preparation reached the AI-setup page, not full onboarding. Scan union correlates with, but does not prove, blocking. Summed scan time and windows must not be added as exclusive contributions.'
}

$json = $summary | ConvertTo-Json -Depth 8
if ($OutputPath) {
    [IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
}
$json
