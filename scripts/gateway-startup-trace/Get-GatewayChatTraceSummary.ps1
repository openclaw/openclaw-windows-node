param(
    [Parameter(Mandatory)][string]$TracePath,
    [Parameter(Mandatory)][string]$ResultPath,
    [string[]]$PayloadAppRoot = @(),
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'GatewayStartupTrace.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'GatewayResourceTrace.psm1') -Force
$result = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
if (-not $result.recording_saved) { throw 'Chat summary requires a saved owned recording.' }
$markers = @($result.markers)
$timing = if ($markers.Count -gt 0) { Get-ChatTimingSummary -Markers $markers } else {
    [ordered]@{ source = 'operator-observed'; outcome = 'not-observed'; observed_time_to_first_response_ms = $null; observed_send_to_terminal_ms = $null; server_time_to_first_token_ms = $null }
}
$start = [long]$result.recording_start_filetime_utc
$end = [long]$result.recording_stop_filetime_utc
$windows = @([pscustomobject]@{ Name = 'recording-overall'; StartTicks = $start; EndTicks = $end })
$previous = $start
$previousName = 'recording-start'
foreach ($marker in $markers) {
    if ([long]$marker.ticks -lt $start -or [long]$marker.ticks -gt $end) { throw 'Chat marker lies outside the recording window.' }
    $windows += [pscustomobject]@{ Name = "$previousName..$($marker.name)"; StartTicks = $previous; EndTicks = [long]$marker.ticks }
    $previous = [long]$marker.ticks
    $previousName = $marker.name
}
$windows += [pscustomobject]@{ Name = "$previousName..recording-stop"; StartTicks = $previous; EndTicks = $end }
$defender = [ordered]@{ status = 'not-attributed-no-payload-roots'; windows = @() }
if ($PayloadAppRoot.Count -gt 0) {
    $report = Get-CompleteMpPerformanceReport -Path $TracePath -TopFiles 100000 -TopScansPerFile 10000
    $roots = @($PayloadAppRoot | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') + '\' })
    $scans = @(foreach ($file in @($report.TopFiles)) {
        $normalized = $file.Path -replace '^\\\\\?\\', ''
        if (@($roots | Where-Object { $normalized.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) { continue }
        foreach ($scan in @($file.Scans)) {
            [pscustomobject]@{ Path = ''; Start = [long]$scan.StartTime; End = [long]$scan.EndTime }
        }
    })
    $defender = [ordered]@{
        status = if ($scans.Count -gt 0) { 'captured' } else { 'no-matching-scans-check-payload-roots' }
        scope = 'all-processes-touching-payload; not-selected-chat-processes-only'
        windows = @(foreach ($window in $windows) {
            $clipped = @(Get-ClippedIntervals -Intervals $scans -WindowStart $window.StartTicks -WindowEnd $window.EndTicks)
            [ordered]@{ phase = $window.Name; scan_count = $clipped.Count; scan_union_ms = (Get-IntervalUnionLength $clipped) / 10000.0 }
        })
    }
}
$summary = [ordered]@{
    schema = 'openclaw-chat-trace-summary/v1'
    stop_reason = if ($result.stop_reason -in @('completed', 'failed', 'cancelled', 'timeout', 'manual', 'controller-error')) { $result.stop_reason } else { 'invalid-result' }
    timing = $timing
    resources = Invoke-ResourceTraceAnalysis -TracePath $TracePath -Windows $windows -PayloadRoots $PayloadAppRoot
    defender = $defender
    provider_network_and_token_metrics = 'unavailable; no provider instrumentation or content captured'
    interpretation = 'This measures the observed real UI interaction, including operator delay. It is not server TTFT or tokens/second. CPU, disk, I/O and Defender activity overlap and do not partition response latency.'
}
$json = $summary | ConvertTo-Json -Depth 12
if ($OutputPath) { [IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false)) }
$json
