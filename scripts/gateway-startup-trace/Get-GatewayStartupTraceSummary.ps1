<#
.SYNOPSIS
Summarizes Defender scan activity in a Gateway startup trace as sanitized aggregates.
.DESCRIPTION
Reads a private ETL recorded by scripts\Invoke-GatewayStartupTrace.ps1 and the comparison
result JSON. For each captured arm it selects Defender scans attributed to the probe PID
on files under the payload app directory, then reports:
  - scan-interval union (wall-clock time covered by at least one scan) overall and per
    startup window, including the unclassified interval before the first recorded phase
  - summed scan duration, which can exceed wall-clock time because scans overlap
  - top package groups by summed scan duration, as payload-relative names only

Correlation is not causation: scan coverage does not prove the probe was blocked on a
scan. FileIO, DiskIO and CPU sampling remain in the private ETL for WPA analysis.
Output contains no user paths, process names or file contents.
#>
param(
    [Parameter(Mandatory)][string]$TracePath,
    [Parameter(Mandatory)][string]$ResultPath,
    [Parameter(Mandatory)][string]$PayloadRoot,
    [string]$OutputPath,
    [ValidateRange(100, 1000000)][int]$TopFiles = 20000,
    [ValidateRange(1, 100000)][int]$TopScansPerFile = 10000,
    [ValidateRange(1, 100)][int]$TopGroups = 15
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'GatewayStartupTrace.psm1') -Force

$ticksPerMs = 10000.0
$appRoot = [IO.Path]::GetFullPath((Join-Path $PayloadRoot 'app')).TrimEnd('\') + '\'
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'openclaw.mjs') -PathType Leaf)) {
    throw 'The payload root does not contain app\openclaw.mjs.'
}
$result = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
$report = Get-CompleteMpPerformanceReport -Path $TracePath -TopFiles $TopFiles -TopScansPerFile $TopScansPerFile

function Get-RelativePayloadPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    $normalized = $Path -replace '^\\\\\?\\', ''
    if ($normalized.StartsWith($appRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $normalized.Substring($appRoot.Length)
    }
    return $null
}

$allScans = foreach ($file in @($report.TopFiles)) {
    $relative = Get-RelativePayloadPath $file.Path
    if ($null -eq $relative) { continue }
    foreach ($scan in @($file.Scans)) {
        if ($null -eq $scan.PSObject.Properties['ProcessId']) { continue }
        [pscustomobject]@{
            Path = $relative
            ProcessId = [int]$scan.ProcessId
            Start = [Int64]$scan.StartTime
            End = [Int64]$scan.EndTime
        }
    }
}
$allScans = @($allScans)

function Get-SummedTicks {
    param([object[]]$Intervals)
    [Int64]$total = 0
    foreach ($interval in $Intervals) { $total += $interval.End - $interval.Start }
    return $total
}

function Get-ArmSummary {
    param([string]$Name, $Arm)
    if ($null -eq $Arm -or $Arm.status -ne 'captured' -or $null -eq $Arm.trace) {
        return [ordered]@{ arm = $Name; status = if ($Arm) { $Arm.status } else { 'missing' } }
    }
    $trace = $Arm.trace
    $scans = @($allScans | Where-Object ProcessId -eq ([int]$trace.pid))
    $originTicks = ConvertTo-UtcFileTime $trace.started_utc
    $windows = foreach ($window in Get-StartupPhaseWindows $trace) {
        $start = $originTicks + [Int64]($window.StartMs * $ticksPerMs)
        $end = $originTicks + [Int64]($window.EndMs * $ticksPerMs)
        $clipped = @(Get-ClippedIntervals -Intervals $scans -WindowStart $start -WindowEnd $end)
        $union = (Get-IntervalUnionLength $clipped) / $ticksPerMs
        $span = $window.EndMs - $window.StartMs
        [ordered]@{
            phase = $window.Phase
            elapsed_ms = [Math]::Round($span, 1)
            scan_union_ms = [Math]::Round($union, 1)
            scan_union_fraction = if ($span -gt 0) { [Math]::Round($union / $span, 3) } else { 0 }
            scans = $clipped.Count
        }
    }
    $runEnd = $originTicks + [Int64]([double]$trace.elapsed_ms * $ticksPerMs)
    $inRun = @(Get-ClippedIntervals -Intervals $scans -WindowStart $originTicks -WindowEnd $runEnd)
    # Counts, durations and groups cover only the measured arm, matching scan_union_in_run_ms.
    $groups = $inRun | Group-Object { Get-PayloadPackageGroup $_.Path } | ForEach-Object {
        [pscustomobject]@{
            group = $_.Name
            files = @($_.Group | Select-Object -ExpandProperty Path -Unique).Count
            scans = $_.Count
            summed_scan_ms = [Math]::Round((Get-SummedTicks $_.Group) / $ticksPerMs, 1)
        }
    } | Sort-Object summed_scan_ms -Descending | Select-Object -First $TopGroups

    return [ordered]@{
        arm = $Name
        status = 'captured'
        elapsed_ms = $trace.elapsed_ms
        startup_to_first_phase_ms = $trace.startup_to_first_phase_ms
        cpu_user_ms = $trace.cpu_user_ms
        cpu_system_ms = $trace.cpu_system_ms
        payload_scans = $inRun.Count
        payload_files_scanned = @($inRun | Select-Object -ExpandProperty Path -Unique).Count
        summed_scan_ms = [Math]::Round((Get-SummedTicks $inRun) / $ticksPerMs, 1)
        scan_union_in_run_ms = [Math]::Round((Get-IntervalUnionLength $inRun) / $ticksPerMs, 1)
        windows = @($windows)
        top_groups = @($groups)
    }
}

$summary = [ordered]@{
    payload_version = $result.payload_version
    payload_commit = $result.payload_commit
    architecture = $result.architecture
    order = $result.order
    defender_payload_scans_in_report = $allScans.Count
    report_files = @($report.TopFiles).Count
    report_top_files_limit = $TopFiles
    report_complete = $true
    arms = @(
        Get-ArmSummary 'host' $result.host
        Get-ArmSummary 'isolated' $result.isolated
    )
    interpretation = 'Scan union is wall-clock coverage by at least one Defender scan; it correlates with, but does not prove, blocking. Summed scan time, CPU time and windows must not be added as exclusive contributions.'
}

$json = $summary | ConvertTo-Json -Depth 8
if ($OutputPath) {
    [IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
}
$json
