<#
.SYNOPSIS
Offline regressions for resource projection and content-free chat recording.
.DESCRIPTION
Does not start WPR, a Gateway, Companion, MXC or a chat request. The sampler smoke
check reads only the current test process plus host counters.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tools = Join-Path $PSScriptRoot 'gateway-startup-trace'
if (-not (Test-Path -LiteralPath (Join-Path $tools 'Analyzer\bin\Debug\net10.0\GatewayTraceAnalyzer.dll'))) {
    throw 'Build scripts\gateway-startup-trace\Analyzer\GatewayTraceAnalyzer.csproj before running the resource trace tests.'
}
Import-Module (Join-Path $tools 'GatewayResourceTrace.psm1') -Force
$checks = 0
function Assert-Equal($Actual, $Expected, [string]$Because) {
    if ($Actual -ne $Expected) { throw "$Because. Expected '$Expected', got '$Actual'." }
    $script:checks++
}
function Assert-Rejected([scriptblock]$Action, [string]$Because) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Equal $rejected $true $Because
}

$base = 130000000000000000L
function New-Sample([int]$Second, [long]$Creation = 100, [double]$Cpu = 0, [long]$Bytes = 0) {
    [pscustomobject]@{
        UtcTicks = $base + $Second * 10000000L
        MonotonicTicks = $Second * 1000L; MonotonicFrequency = 1000L
        Awake100ns = $Second * 10000000L
        Kernel100ns = $Second * 20000000L; User100ns = $Second * 10000000L; Idle100ns = $Second * 10000000L
        AvailableBytes = 1000000L - $Second; TotalBytes = 2000000L; UnavailableProcesses = 0
        Processes = @([pscustomobject]@{
            Id = 42; StartedUtcTicks = $Creation; ObservedUtcTicks = $base + $Second * 10000000L
            CpuUserMs = $Cpu; CpuKernelMs = $Cpu / 2
            WorkingSetBytes = 1000L + $Second; PeakWorkingSetBytes = 900000L; PrivateBytes = 2000L + $Second
            ReadBytes = $Bytes; WriteBytes = $Bytes / 2; ReadOperations = $Second; WriteOperations = $Second
        })
    }
}
$samples = @(New-Sample 0 100 0 0; New-Sample 1 100 100 1000; New-Sample 2 100 300 3000; New-Sample 3 200 1 10)
$window = Get-ResourceWindowSummary -Samples $samples -StartTicks ($base + 5000000L) -EndTicks ($base + 25000000L)
Assert-Equal $window.processes.Count 1 'One native process lifetime in the phase'
Assert-Equal $window.processes[0].cpu_user_ms 200 'Only counters whose endpoints lie inside the phase contribute'
Assert-Equal $window.processes[0].logical_read_bytes 2000 'Logical reads are counter deltas, not lifetime totals'
Assert-Equal $window.processes[0].sampled_peak_working_set_bytes 1002 'Do not use the pre-recording lifetime peak'
Assert-Equal $window.sampled_intervals 1 'Boundary-crossing sample intervals are excluded'
Assert-Equal $window.host_cpu_fraction 0.6667 'Kernel includes idle and must subtract it'
$all = Get-ResourceWindowSummary -Samples $samples -StartTicks $base -EndTicks ($base + 40000000L)
Assert-Equal $all.processes.Count 2 'PID reuse must split native creation identities'
Assert-Equal $all.processes[1].cpu_user_ms $null 'A single sample has no measured CPU delta'
Assert-Equal $all.processes[1].logical_read_bytes $null 'A single sample has no measured I/O delta'
Assert-Equal $all.processes[0].process $window.processes[0].process 'Process labels stay stable across phase windows'
$missing = Get-ResourceWindowSummary -Samples @() -StartTicks $base -EndTicks ($base + 10000000L)
Assert-Equal $missing.host_cpu_fraction $null 'Missing samples must not become zero CPU'
Assert-Equal $missing.host_minimum_available_bytes $null 'Missing memory must remain unavailable'
foreach ($unmeasured in @(
    $missing
    (Get-ResourceWindowSummary -Samples @($samples[1]) -StartTicks $base -EndTicks ($base + 20000000L))
    (Get-ResourceWindowSummary -Samples @($samples[0], $samples[3]) -StartTicks ($base + 5000000L) -EndTicks ($base + 25000000L))
)) {
    Assert-Equal $unmeasured.sampled_intervals 0 'Fixture has no complete interval'
    Assert-Equal $unmeasured.maximum_sample_gap_ms $null 'No interval cannot prove a zero sampling gap'
    Assert-Equal $unmeasured.non_awake_gap_ms $null 'No interval cannot prove uninterrupted awake time'
    Assert-Equal $unmeasured.clock_adjustment_magnitude_ms $null 'No interval cannot prove no clock adjustment'
}
$lateObservation = New-Sample 0
$lateObservation.Processes[0].ObservedUtcTicks = $base + 90000000L
$lateWindow = Get-ResourceWindowSummary -Samples @($lateObservation) -StartTicks $base -EndTicks ($base + 10000000L)
Assert-Equal $lateWindow.processes.Count 0 'Per-process observation time, not enumeration start, determines phase admission'
$suspend = @(New-Sample 0; New-Sample 3)
$suspend[1].Awake100ns = 10000000L
$sleepWindow = Get-ResourceWindowSummary -Samples $suspend -StartTicks $base -EndTicks ($base + 30000000L)
Assert-Equal $sleepWindow.non_awake_gap_ms 2000 'Awake counter distinguishes a non-awake interval from CPU work'
$suspend[1].UtcTicks += 50000000L
$clockWindow = Get-ResourceWindowSummary -Samples $suspend -StartTicks $base -EndTicks ($base + 90000000L)
Assert-Equal $clockWindow.clock_adjustment_magnitude_ms 5000 'Wall clock changes are not sleep evidence'

$chat = @(
    [pscustomobject]@{ name = 'send'; ticks = $base }
    [pscustomobject]@{ name = 'first-response'; ticks = $base + 25000000L }
    [pscustomobject]@{ name = 'completed'; ticks = $base + 90000000L }
)
$timing = Get-ChatTimingSummary $chat
Assert-Equal $timing.observed_time_to_first_response_ms 2500 'Observed first response uses independent marker timestamps'
Assert-Equal $timing.observed_send_to_terminal_ms 9000 'Observed end-to-end response duration'
Assert-Equal $timing.server_time_to_first_token_ms $null 'Operator markers cannot measure server TTFT'
Assert-Rejected { Get-ChatTimingSummary @($chat[1], $chat[2]) } 'Missing send is rejected'
Assert-Rejected { Get-ChatTimingSummary @($chat[0], $chat[0]) } 'Duplicate markers are rejected'
Assert-Rejected { Get-ChatTimingSummary @($chat[1], $chat[0]) } 'Out-of-order markers are rejected'
Assert-Rejected { Get-ChatTimingSummary @($chat[0], $chat[2], [pscustomobject]@{ name = 'failed'; ticks = $base + 100000000L }) } 'Conflicting terminal markers are rejected'
Assert-Rejected { Get-ChatTimingSummary @($chat[0], [pscustomobject]@{ name = 'secret-prompt'; ticks = $base + 1 }) } 'Only fixed event names are accepted'

Add-Type -Path (Join-Path $tools 'Analyzer\TraceProjection.cs')
$paths = [OpenClaw.Trace.PayloadPaths]::new(@('C:\payload\app'))
Assert-Equal ($paths.Group('C:\payload\app\node_modules\@scope\package\index.js')) 'node_modules\@scope\package' 'Scoped package grouping'
Assert-Equal ($paths.Relative('\\?\C:\payload\app\dist\index.js')) 'dist\index.js' 'Extended Windows path normalization'
Assert-Equal ($paths.Relative('\??\C:\payload\app\dist\index.js')) 'dist\index.js' 'NT DOS path normalization'
Assert-Equal ($paths.Relative('C:\payload\app-other\private.txt')) $null 'Root matching must respect directory boundary'
Assert-Equal ($paths.Relative('C:\payload\app\..\private.txt')) $null 'Relative traversal must not escape payload scope'
Assert-Equal ($paths.Group('C:\Users\private\secret.txt')) 'outside-payload' 'Unrelated paths do not reach shared output'
Assert-Equal ($paths.Group('')) 'unresolved' 'A missing filename is not a known external file'
$lifetimes = [Collections.Generic.List[OpenClaw.Trace.Lifetime]]::new()
$firstLife = [OpenClaw.Trace.Lifetime]::new(42, 100)
$firstLife.End = 200
$lifetimes.Add($firstLife)
$lifetimes.Add([OpenClaw.Trace.Lifetime]::new(42, 300))
$selections = [OpenClaw.Trace.ProcessSelection]::Match(@(
    [OpenClaw.Trace.ProcessIdentity]::new(42, 99, 150)
    [OpenClaw.Trace.ProcessIdentity]::new(42, 299, 350)
), $lifetimes)
Assert-Equal $selections[0].End 200 'ETL exit cuts off the selected PID lifetime'
Assert-Equal $selections[1].Start 300 'Later reused PID starts a separate selected interval'
$withoutEvents = [OpenClaw.Trace.ProcessSelection]::Match(@(
    [OpenClaw.Trace.ProcessIdentity]::new(42, 100, 150)
    [OpenClaw.Trace.ProcessIdentity]::new(42, 300, 350)
), [Collections.Generic.List[OpenClaw.Trace.Lifetime]]::new())
Assert-Equal $withoutEvents[0].End 151 'Missing lifetime events cannot extend selection beyond native observations'
$unselectedReplacement = [OpenClaw.Trace.ProcessSelection]::Match(@(
    [OpenClaw.Trace.ProcessIdentity]::new(42, 100, 150, 200)
), [Collections.Generic.List[OpenClaw.Trace.Lifetime]]::new())
Assert-Equal $unselectedReplacement[0].Start 150 'Missing lifetime start admits only the proven observation span'
Assert-Equal $unselectedReplacement[0].End 201 'An unselected replacement at tick 400 cannot inherit the selected identity'
$openLifetime = [Collections.Generic.List[OpenClaw.Trace.Lifetime]]::new()
$openLifetime.Add([OpenClaw.Trace.Lifetime]::new(42, 100))
$openSelection = [OpenClaw.Trace.ProcessSelection]::Match(@(
    [OpenClaw.Trace.ProcessIdentity]::new(42, 100, 150, 200)
), $openLifetime)
Assert-Equal $openSelection[0].Start 100 'Verified ETL start retains pre-observation attribution'
Assert-Equal $openSelection[0].End 201 'Missing stop events do not prove an indefinite lifetime'
$ioWindow = [OpenClaw.Trace.WindowResult]::new([OpenClaw.Trace.Window]::new('test', 100, 200))
Assert-Equal ($ioWindow.Contains(100)) $true 'Phase start is included'
Assert-Equal ($ioWindow.Contains(200)) $false 'Phase end is excluded to avoid double counting'
$ioWindow.AddFile('dist\other', 'dist\index.js', 4096, 'read')
$ioWindow.AddFile('dist\other', 'dist\index.js', 0, 'open')
$ioWindow.AddFile('outside-payload', $null, 1024, 'write')
Assert-Equal $ioWindow.PayloadFiles.Count 1 'Only relative payload files appear in top files'
Assert-Equal $ioWindow.PayloadFiles['dist\index.js'].ReadBytes 4096 'File I/O byte aggregation'
Assert-Equal $ioWindow.PayloadFiles['dist\index.js'].OpenRequests 1 'Open requests are separate from read operations'

# Execute the production finally block with failing output and bounded-WPR adapters.
# No recorder preflight, elevation or real WPR command runs in this fixture.
& {
    $tokens = $null
    $parseErrors = $null
    $recorderAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $tools 'Start-GatewayStartupRecorder.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw 'Recorder fixture could not parse the production script.' }
    $finally = @($recorderAst.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })[-1].Finally
    $body = $finally.Extent.Text.Substring(1, $finally.Extent.Text.Length - 2)
    $events = [Collections.Generic.List[string]]::new()
    $state = @{ status = $null }
    $resourceWriter = [pscustomobject]@{}
    $resourceWriter | Add-Member -MemberType ScriptMethod -Name Dispose -Value { $events.Add('dispose'); throw 'fixture flush failed' }
    $started = $true; $startAttempted = $true; $stopSaved = $false
    $instanceRecordingAfter = $true; $outcome = 'failed: fixture write'
    $resourceSamples = 1; $resourceMaximumCaptureMs = 2.0
    $InstanceName = 'OpenClawStartup-0123456789abcdef0123456789abcdef'
    $statusPath = 'fixture-status'; $wprControlTimeoutSeconds = 1
    function Invoke-BoundedWpr([string]$Arguments, [int]$TimeoutSeconds) {
        Assert-Equal $Arguments "-cancel -instancename $InstanceName" 'Cleanup cancels only the owned named WPR instance'
        $events.Add('cancel')
        [pscustomobject]@{ ExitCode = 0 }
    }
    function Test-InstanceRecording { $events.Add('inactive'); return $false }
    function Write-ControlFile($Path, $Value) { $events.Add('status'); $state.status = $Value }
    & ([scriptblock]::Create($body))
    Assert-Equal ($events -join ',') 'dispose,cancel,inactive,status' 'Flush failure cannot prevent cancellation and final inactive receipt'
    Assert-Equal $state.status.outcome 'failed: resource-sample-close' 'Flush failure is not reported as saved evidence'
    Assert-Equal $state.status.instance_recording_after $false 'Final receipt reflects the checked named instance state'
}

# Exercise the controller shutdown boundary without elevation or a recorder.
& {
    $tokens = $null
    $parseErrors = $null
    $chatAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'Invoke-GatewayChatTrace.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw 'Chat fixture could not parse the production script.' }
    $finally = @($chatAst.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })[-1].Finally
    $body = $finally.Extent.Text.Substring(1, $finally.Extent.Text.Length - 2)
    $events = [Collections.Generic.List[string]]::new()
    $control = 'fixture-control'; $paths = @{ status = 'fixture-status' }
    $instance = 'OpenClawStartup-0123456789abcdef0123456789abcdef'
    $RecorderStopTimeoutSeconds = 1; $start = 100L; $end = 0L; $reason = 'timeout'
    $markers = @(); $recorder = $null
    function Test-Path { return $true }
    function Remove-Item { $events.Add('remove'); throw 'fixture sharing violation' }
    function Stop-GatewayTraceRecorder { $events.Add('stop'); return [pscustomobject]@{ outcome = 'saved' } }
    function Get-ChatTraceMarkers {
        $events.Add('final-markers')
        return [pscustomobject]@{ name = 'send'; ticks = 150L }
    }
    . ([scriptblock]::Create($body))
    Assert-Equal ($events -join ',') 'remove,stop,final-markers' 'Readiness deletion failure cannot bypass stop or final marker collection'
    Assert-Equal $reason 'controller-error' 'Readiness cleanup errors stay explicit'
    Assert-Equal $recorder.outcome 'saved' 'Owned recorder stop receipt survives readiness cleanup failure'
    Assert-Equal $markers.Count 1 'Final marker collection includes observations after the last poll'
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) "openclaw-resource-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path (Join-Path $temporary 'control') -Force | Out-Null
try {
    $control = Join-Path $temporary 'control'
    [IO.File]::WriteAllText((Join-Path $control 'chat-ready'), 'OpenClawStartup-0123456789abcdef0123456789abcdef')
    & (Join-Path $PSScriptRoot 'Write-GatewayChatTraceMarker.ps1') -RunDirectory $temporary -Event send
    $readMarkers = @(Get-ChatTraceMarkers -ControlDirectory $control -StartTicks 0 -EndTicks ([DateTime]::UtcNow.ToFileTimeUtc()))
    Assert-Equal $readMarkers.Count 1 'Marker publisher creates a readable atomic observation'
    $publishedTicks = $readMarkers[0].ticks
    $pendingMarkers = @(Get-ChatTraceMarkers -ControlDirectory $control -StartTicks 0 -EndTicks ($publishedTicks - 1) -AllowPending)
    Assert-Equal $pendingMarkers.Count 0 'A marker published after the polling cutoff is deferred, not rejected'
    $nextPoll = @(Get-ChatTraceMarkers -ControlDirectory $control -StartTicks 0 -EndTicks $publishedTicks -AllowPending)
    Assert-Equal $nextPoll.Count 1 'The next poll admits the deferred marker'
    Assert-Rejected {
        Get-ChatTraceMarkers -ControlDirectory $control -StartTicks 0 -EndTicks ($publishedTicks - 1)
    } 'Closed recording boundaries remain strict'
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Write-GatewayChatTraceMarker.ps1') -RunDirectory $temporary -Event send } 'Duplicate publication fails without overwriting'
    [IO.File]::WriteAllText((Join-Path $control 'recorder-stop'), 'stop')
    Assert-Rejected { & (Join-Path $PSScriptRoot 'Write-GatewayChatTraceMarker.ps1') -RunDirectory $temporary -Event completed } 'Stopped runs reject new markers'
    $unrecorded = Invoke-ResourceTraceAnalysis -TracePath (Join-Path $temporary 'absent.etl') -Windows @() -PayloadRoots @()
    Assert-Equal $unrecorded.status 'not-recorded' 'Historical traces without sampler evidence are explicitly unavailable'
    Write-ResourceTraceConfiguration -ControlDirectory $control -ProcessIds @($PID)
    $config = Get-Content -LiteralPath (Join-Path $control 'resource-config.json') -Raw | ConvertFrom-Json
    Assert-Equal $config.processes.Count 1 'Explicit sampler scope records one native process identity'
    $buildIdentityPath = Join-Path $temporary 'build.json'
    [IO.File]::WriteAllText($buildIdentityPath, '{"gateway_commit":"0123456789abcdef0123456789abcdef01234567","gateway_version":"2026.10.9"}')
    Write-ResourceTraceConfiguration -ControlDirectory $control -BuildIdentityPath $buildIdentityPath
    $identityConfig = Get-Content -LiteralPath (Join-Path $control 'resource-config.json') -Raw | ConvertFrom-Json
    Assert-Equal $identityConfig.context.artifact_identity.gateway_version '2026.10.9' 'Build identity copies only validated fixed fields'
    [IO.File]::WriteAllText($buildIdentityPath, '{"prompt":"private text"}')
    Assert-Rejected { Write-ResourceTraceConfiguration -ControlDirectory $control -BuildIdentityPath $buildIdentityPath } 'Arbitrary content cannot be supplied through build metadata'
    [IO.File]::WriteAllText($buildIdentityPath, '{"gateway_commit":"not-a-commit"}')
    Assert-Rejected { Write-ResourceTraceConfiguration -ControlDirectory $control -BuildIdentityPath $buildIdentityPath } 'Invalid build identities fail before recording'
    $samples | ForEach-Object { $_ | ConvertTo-Json -Depth 5 -Compress } |
        Set-Content -LiteralPath (Join-Path $temporary 'resource-samples.jsonl') -Encoding utf8
    Assert-Rejected {
        Invoke-ResourceTraceAnalysis -TracePath (Join-Path $temporary 'absent.etl') -PayloadRoots @() -Windows @(
            [pscustomobject]@{ Name = 'test'; StartTicks = $base; EndTicks = $base + 40000000L }
        )
    } 'ETL read failure cannot produce a success-shaped resource report'
    $timelineJson = Get-Content -LiteralPath (Join-Path $temporary 'resource-timeline.json') -Raw
    $timeline = $timelineJson | ConvertFrom-Json
    $analysisRequest = Get-Content -LiteralPath (Join-Path $temporary 'resource-analysis-request.json') -Raw | ConvertFrom-Json
    Assert-Equal $analysisRequest.Processes[0].ObservedTicks $base 'ETL request retains first native observation'
    Assert-Equal $analysisRequest.Processes[0].LastObservedTicks ($base + 20000000L) 'ETL request retains final observation, not an indefinite PID'
    Assert-Equal $timeline.samples.Count 4 'Sanitized timeline retains every supplied sample'
    Assert-Equal $timeline.samples[0].processes[0].working_set_bytes 1000 'Timeline memory values come from the observed sample'
    Assert-Equal ($timelineJson -match 'StartedUtcTicks|"Id"|CpuUserMs|CommandLine|C:\\\\') $false 'Timeline excludes native identities, absolute counters and paths'
    $emptyProcesses = New-Sample 0
    $emptyProcesses.Processes = @()
    $emptyProcesses | ConvertTo-Json -Depth 5 -Compress |
        Set-Content -LiteralPath (Join-Path $temporary 'resource-samples.jsonl') -Encoding utf8
    $emptySummary = Invoke-ResourceTraceAnalysis -TracePath (Join-Path $temporary 'absent.etl') -PayloadRoots @() -Windows @()
    Assert-Equal $emptySummary.selected_process_lifetimes 0 'Missing selected processes do not crash timeline generation'
    $emptyTimeline = Get-Content -LiteralPath (Join-Path $temporary 'resource-timeline.json') -Raw | ConvertFrom-Json
    Assert-Equal $emptyTimeline.samples[0].processes.Count 0 'An empty process observation stays explicit in the timeline'
    Assert-Equal $emptySummary.status 'insufficient-samples' 'One host sample is not a measured resource interval'
    Initialize-ResourceSampler
    [OpenClaw.Trace.ResourceSampler]::SelectedProcesses.Clear()
    [OpenClaw.Trace.ResourceSampler]::SelectedProcesses.Add($PID, $config.processes[0].started_utc_ticks)
    $snapshot = [OpenClaw.Trace.ResourceSampler]::Capture()
    Assert-Equal $snapshot.Processes.Count 1 'Sampler only records configured process lifetimes'
    Assert-Equal $snapshot.Processes[0].Id $PID 'Sampler does not substitute another visible process'
    Assert-Equal $snapshot.UnavailableProcesses 0 'Explicit-only sampling does not count unrelated inaccessible processes'
    Assert-Equal ($null -ne $snapshot.Processes[0].ReadBytes) $true 'Logical I/O is readable through the limited-query handle'
    Assert-Equal ($snapshot.Processes[0].ObservedUtcTicks -ge $snapshot.Processes[0].StartedUtcTicks) $true 'A sampled process observation cannot predate its native creation'
    $json = $snapshot | ConvertTo-Json -Depth 5 -Compress
    Assert-Equal ($json -match 'CommandLine|FileName|ProcessName|C:\\\\Users') $false 'Sampler output contains no names, paths or command lines'
} finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
Write-Host "Gateway resource trace tests passed ($checks assertions)."
exit 0
