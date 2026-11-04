<#
.SYNOPSIS
    Exercises the Gateway startup trace helpers without recording or launching a Gateway.
.DESCRIPTION
    Checks interval-union math, phase windows, package grouping, timestamp handling, the
    startup guard self-test when Node is on PATH, controller refusals, recorder instance
    safety, and that every trace script parses. Never starts WPR or MXC.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolDirectory = Join-Path $scriptRoot 'gateway-startup-trace'
Import-Module (Join-Path $toolDirectory 'GatewayStartupTrace.psm1') -Force
$failures = [Collections.Generic.List[string]]::new()

function Assert-Equal($Expected, $Actual, [string]$Name) {
    if ($Expected -ne $Actual) { $failures.Add("${Name}: expected '$Expected', got '$Actual'") }
}

function New-Interval([string]$Path, [Int64]$Start, [Int64]$End) {
    [pscustomobject]@{ Path = $Path; Start = $Start; End = $End }
}

# Overlapping scans count once; summed durations would double count.
$intervals = @(
    New-Interval 'a' 0 10
    New-Interval 'b' 5 20
    New-Interval 'c' 30 40
    New-Interval 'd' 35 35
)
Assert-Equal 30 (Get-IntervalUnionLength $intervals) 'union merges overlaps'
Assert-Equal 0 (Get-IntervalUnionLength @()) 'union of nothing'
$clipped = @(Get-ClippedIntervals -Intervals $intervals -WindowStart 8 -WindowEnd 35)
Assert-Equal 3 $clipped.Count 'clip keeps intersecting scans'
Assert-Equal 17 (Get-IntervalUnionLength $clipped) 'clipped union'

Assert-Equal 'node_modules\@openclaw\fs-safe' (Get-PayloadPackageGroup 'node_modules\@openclaw\fs-safe\dist\a.js') 'scoped package'
Assert-Equal 'node_modules\typebox' (Get-PayloadPackageGroup 'node_modules/typebox/build/index.mjs') 'unscoped package'
Assert-Equal 'dist\extensions\imap' (Get-PayloadPackageGroup 'dist\extensions\imap\index.js') 'extension'
Assert-Equal 'dist\other' (Get-PayloadPackageGroup 'dist\chunk-1.js') 'dist chunk'
Assert-Equal 'openclaw.mjs' (Get-PayloadPackageGroup 'openclaw.mjs') 'root file'

# The interval before the first recorded phase is its own window, not part of a phase.
$trace = [pscustomobject]@{
    elapsed_ms = 1000.0
    spans = [pscustomobject]@{
        'session-runtime-import' = [pscustomobject]@{ start_ms = 400.0; elapsed_ms = 300.0 }
        'plugin-config' = [pscustomobject]@{ start_ms = 750.0; elapsed_ms = 200.0 }
    }
}
$windows = @(Get-StartupPhaseWindows $trace)
Assert-Equal 'before-first-phase,session-runtime-import,between-phases,plugin-config,after-plugin-config' (($windows | ForEach-Object Phase) -join ',') 'phase windows'
Assert-Equal 400.0 $windows[0].EndMs 'pre-phase end'
Assert-Equal 1000.0 $windows[4].EndMs 'tail end'

$expectedTicks = [DateTime]::new(2026, 11, 3, 14, 23, 50, 328, [DateTimeKind]::Utc).ToFileTimeUtc()
Assert-Equal $expectedTicks (ConvertTo-UtcFileTime '2026-11-03T14:23:50.328Z') 'timestamp string'
$parsed = ('{"t":"2026-11-03T14:23:50.328Z"}' | ConvertFrom-Json).t
Assert-Equal $expectedTicks (ConvertTo-UtcFileTime $parsed) 'timestamp converted by ConvertFrom-Json'
$utc = [DateTime]::new(2026, 11, 3, 14, 23, 50, 328, [DateTimeKind]::Utc)
$fixedZone = [TimeZoneInfo]::CreateCustomTimeZone('OpenClawTest+0530', [TimeSpan]::FromMinutes(330), 'test', 'test')
$offsetValue = [DateTimeOffset]::new([TimeZoneInfo]::ConvertTimeFromUtc($utc, $fixedZone), [TimeSpan]::FromMinutes(330))
Assert-Equal $expectedTicks (ConvertTo-UtcFileTime $offsetValue) 'timestamp with non-UTC offset'
Assert-Equal $expectedTicks (ConvertTo-UtcFileTime '2026-11-03T19:53:50.328+05:30') 'timestamp string with non-UTC offset'
Assert-Equal $expectedTicks (ConvertTo-UtcFileTime $utc.ToLocalTime()) 'local DateTime'
$unspecifiedRejected = $false
try { [void](ConvertTo-UtcFileTime ([DateTime]::new(2026, 11, 3))) } catch { $unspecifiedRejected = $true }
Assert-Equal $true $unspecifiedRejected 'timestamp without time zone is rejected'

$clippedWithProcess = @(Get-ClippedIntervals -Intervals @([pscustomobject]@{ Path = 'x'; ProcessId = 42; Start = 0; End = 10 }) -WindowStart 2 -WindowEnd 5)
Assert-Equal 42 $clippedWithProcess[0].ProcessId 'clip keeps process id'

# Native setup log milestones. Fixture shape mirrors Companion native-setup JSONL; timestamps use
# a non-UTC offset so a local-time reinterpretation would shift every milestone.
function New-SetupLine([int]$Seconds, [string]$Level, [string]$Message) {
    $ts = [DateTimeOffset]::new(2026, 11, 3, 9, 0, 0, [TimeSpan]::FromHours(-5)).AddSeconds($Seconds).ToString('o')
    [ordered]@{ ts = $ts; run = 'r'; level = $Level; msg = $Message } | ConvertTo-Json -Compress
}
$failedAttempt = @(
    New-SetupLine 0 'info' 'Native Gateway setup attempt started.'
    New-SetupLine 1 'info' 'Native setup step: StepPrepare.'
    New-SetupLine 2 'warn' 'cmd.cancelled: caller cancellation observed'
    New-SetupLine 3 'error' 'Native Gateway setup failed during StepPrepare: TimeoutException: x'
)
$readyAttempt = @(
    New-SetupLine 100 'info' 'Native Gateway setup attempt started.'
    New-SetupLine 101 'info' 'Native setup step: StepPrepare.'
    New-SetupLine 110 'info' 'Preparing the isolated Gateway session...'
    New-SetupLine 130 'info' 'Validating native Gateway configuration...'
    New-SetupLine 131 'debug' 'cmd.start: openclaw config validate --json'
    New-SetupLine 200 'debug' 'cmd.exit: direct child exit observed'
    New-SetupLine 201 'debug' 'cmd.done: openclaw exit=0 (70000ms)'
    New-SetupLine 210 'info' 'Native setup step: StepVerify.'
    New-SetupLine 211 'info' 'Starting the packaged Gateway and verifying its listener...'
    New-SetupLine 212 'info' '[WS] Native Gateway start requested on port 1 (budget 180s).'
    New-SetupLine 250 'info' '[WS] Native Gateway start acknowledged after 38.0s.'
    New-SetupLine 255 'info' '[WS] Native Gateway startup status: gateway starting.'
    'not json'
    New-SetupLine 290 'info' 'Verifying the Gateway endpoint before connecting...'
    New-SetupLine 291 'info' 'Native setup step: StepVerify.'
    New-SetupLine 300 'info' 'Preparing local AI...'
)
$setup = Get-NativeSetupMilestones -Lines ($failedAttempt + $readyAttempt)
Assert-Equal 'incomplete' $setup.Outcome 'listener verification alone is not success'
Assert-Equal 'attempt-started,step-prepare,session-prepare,config-validation-start,config-validation-end,step-verify,gateway-start-requested,gateway-start-acknowledged,listener-verified' (($setup.Milestones | ForEach-Object Name) -join ',') 'setup milestones'
$attemptTicks = [DateTimeOffset]::new(2026, 11, 3, 14, 1, 40, [TimeSpan]::Zero).UtcDateTime.ToFileTimeUtc()
Assert-Equal $attemptTicks $setup.Milestones[0].Ticks 'attempt start is UTC'
Assert-Equal 710000000 ($setup.Milestones[4].Ticks - $setup.Milestones[3].Ticks) 'config validation span'
$selectedAttemptStartTicks = [DateTimeOffset]::new(2026, 11, 3, 14, 1, 40, [TimeSpan]::Zero).UtcDateTime.ToFileTimeUtc()
$cutoffTicks = [DateTimeOffset]::new(2026, 11, 3, 14, 5, 0, [TimeSpan]::Zero).UtcDateTime.ToFileTimeUtc()
$selectedAttempt = Get-NativeSetupMilestones -Lines ($failedAttempt + $readyAttempt + @(
    New-SetupLine 320 'info' 'Native Gateway setup attempt started.'
    New-SetupLine 321 'info' 'Native setup step: StepPrepare.'
)) -AttemptStartTicks $selectedAttemptStartTicks -EndTicks $cutoffTicks
Assert-Equal 'incomplete' $selectedAttempt.Outcome 'pinned setup attempt is not replaced by a retry'
Assert-Equal 'attempt-started,step-prepare,session-prepare,config-validation-start,config-validation-end,step-verify,gateway-start-requested,gateway-start-acknowledged,listener-verified' (($selectedAttempt.Milestones | ForEach-Object Name) -join ',') 'pinned attempt excludes later retry'
$cutoffBeforeListener = [DateTimeOffset]::new(2026, 11, 3, 14, 4, 15, [TimeSpan]::Zero).UtcDateTime.ToFileTimeUtc()
$truncatedAttempt = Get-NativeSetupMilestones -Lines ($failedAttempt + $readyAttempt) `
    -AttemptStartTicks $selectedAttemptStartTicks -EndTicks $cutoffBeforeListener
Assert-Equal $true (@($truncatedAttempt.Milestones | Where-Object Name -eq 'listener-verified').Count -eq 0) 'post-cutoff setup milestone excluded'
$setupWindows = @(Get-NativeSetupWindows -Milestones $setup.Milestones -EndTicks ($setup.Milestones[-1].Ticks + 10))
Assert-Equal 9 $setupWindows.Count 'setup windows include tail'
Assert-Equal 'listener-verified..recording-stop' $setupWindows[-1].Phase 'setup tail window'
$lateFailure = Get-NativeSetupMilestones -Lines ($readyAttempt + @(New-SetupLine 320 'error' 'Native Gateway setup failed during StepVerify: InvalidOperationException: pairing'))
Assert-Equal 'failed' $lateFailure.Outcome 'failure after listener verification'
Assert-Equal 'StepVerify' $lateFailure.FailedStep 'late failure step'
Assert-Equal 'listener-verified,setup-failed' (($lateFailure.Milestones | Select-Object -Last 2 | ForEach-Object Name) -join ',') 'late failure milestones'
Assert-Equal 'failed' (Resolve-NativeSetupOutcome -ParsedOutcome 'failed' -StopReason 'ai-setup-reached') 'logged failure wins over UI'
Assert-Equal 'reached-ai-setup' (Resolve-NativeSetupOutcome -ParsedOutcome $setup.Outcome -StopReason 'ai-setup-reached') 'UI success without a success log'
Assert-Equal 'failed-ui-only' (Resolve-NativeSetupOutcome -ParsedOutcome $setup.Outcome -StopReason 'ui-retry') 'Retry without a failure log'
Assert-Equal 'incomplete' (Resolve-NativeSetupOutcome -ParsedOutcome $setup.Outcome -StopReason 'manual') 'manual stop is not success'
Assert-Equal 'incomplete' (Resolve-NativeSetupOutcome -ParsedOutcome $setup.Outcome -StopReason 'timeout') 'timeout is not success'
$validArm = [pscustomobject]@{
    status = 'captured'
    trace = [pscustomobject]@{ blocked = [pscustomobject]@{ child = 0; network = 0 } }
}
$blockedArm = [pscustomobject]@{
    status = 'captured'
    trace = [pscustomobject]@{ blocked = [pscustomobject]@{ child = 0; network = 1 } }
}
Assert-Equal 'captured' (Get-StartupArmValidity $validArm).Status 'unblocked arm is valid'
Assert-Equal 'rejected' (Get-StartupArmValidity $blockedArm).Status 'blocked arm is rejected'
Assert-Equal 'blocked-network' (Get-StartupArmValidity $blockedArm).RejectionReason 'blocked arm rejection reason'
$signalPath = Join-Path ([IO.Path]::GetTempPath()) ('setup-stop-' + [guid]::NewGuid().ToString('N'))
try {
    [IO.File]::WriteAllText($signalPath, "ai-setup-reached`n")
    Assert-Equal 'ai-setup-reached' (Read-SetupStopSignal $signalPath) 'AI-setup signal'
    [IO.File]::WriteAllText($signalPath, 'stop')
    Assert-Equal 'manual' (Read-SetupStopSignal $signalPath) 'other signal is manual'
} finally {
    Remove-Item -LiteralPath $signalPath -ErrorAction SilentlyContinue
}
Assert-Equal 'ai-setup' (Get-NativeSetupUiStateFromIds @('OnboardingLocalAiCard', 'ChoicesScroller')) 'AI-setup page state'
Assert-Equal 'retry' (Get-NativeSetupUiStateFromIds @('NativeGatewaySetupStatus', 'RetryButton')) 'Retry state'
Assert-Equal 'busy' (Get-NativeSetupUiStateFromIds @('NativeGatewaySetupStatus', 'CancelButton')) 'busy state'
Assert-Equal 'unknown' (Get-NativeSetupUiStateFromIds @('NativeGatewaySetupStatus', 'ChoicesScroller')) 'mixed pages are not success'
Assert-Equal 'unknown' (Get-NativeSetupUiStateFromIds @('RetryButton')) 'Retry elsewhere is not setup failure'
$timedOutAttempt = @(
    New-SetupLine 400 'info' 'Native Gateway setup attempt started.'
    New-SetupLine 410 'info' 'Native setup step: StepVerify.'
    New-SetupLine 411 'info' 'Starting the packaged Gateway and verifying its listener...'
    New-SetupLine 412 'info' '[WS] Native Gateway start requested on port 1 (budget 180s).'
    New-SetupLine 450 'info' '[WS] Native Gateway start acknowledged after 38.0s.'
    New-SetupLine 451 'info' 'Native setup step: StepVerify.'
    New-SetupLine 452 'info' 'Some unrelated progress.'
    New-SetupLine 592 'warn' '[WS] The package-managed Gateway did not report a verified listener within 180s.'
    New-SetupLine 593 'error' 'Native Gateway setup failed during StepVerify: TimeoutException: x'
)
$timedOut = Get-NativeSetupMilestones -Lines $timedOutAttempt
Assert-Equal 'failed' $timedOut.Outcome 'readiness timeout fails'
Assert-Equal 'attempt-started,step-verify,gateway-start-requested,gateway-start-acknowledged,readiness-timeout,setup-failed' (($timedOut.Milestones | ForEach-Object Name) -join ',') 'unrelated info after acknowledgment is not listener evidence'
$failed = Get-NativeSetupMilestones -Lines $failedAttempt
Assert-Equal 'failed' $failed.Outcome 'failed outcome'
Assert-Equal 'StepPrepare' $failed.FailedStep 'failed step'
Assert-Equal 'not-started' (Get-NativeSetupMilestones -Lines @('{}')).Outcome 'no attempt'
$timedOut = Get-NativeSetupMilestones -Lines ($readyAttempt[0..11] + @(New-SetupLine 400 'warn' '[WS] The package-managed Gateway did not report a verified listener within 180 seconds of starting.'))
Assert-Equal 'incomplete' $timedOut.Outcome 'readiness timeout is not success'
Assert-Equal 'readiness-timeout' $timedOut.Milestones[-1].Name 'readiness timeout milestone'

$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    $output = & $node.Source --require (Join-Path $toolDirectory 'startup-guard.cjs') (Join-Path $toolDirectory 'startup-guard-selftest.cjs') 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { $failures.Add("guard self-test exited $LASTEXITCODE`: $output") }
    $line = ($output -split "`r?`n") | Where-Object { $_.StartsWith('OPENCLAW_STARTUP_COMPARISON=') } | Select-Object -Last 1
    if (-not $line) {
        $failures.Add('guard self-test emitted no comparison marker')
    } else {
        $selfTest = $line.Substring('OPENCLAW_STARTUP_COMPARISON='.Length) | ConvertFrom-Json
        Assert-Equal 'captured' $selfTest.outcome 'guard outcome'
        Assert-Equal 12.5 $selfTest.spans.'session-runtime-import'.elapsed_ms 'guard import span'
        Assert-Equal 25 $selfTest.spans.'plugin-config'.elapsed_ms 'guard plugin span'
        Assert-Equal 1 $selfTest.blocked.child 'guard blocked child process'
        Assert-Equal 3 $selfTest.blocked.network 'guard blocked network APIs'
    }
} else {
    Write-Warning 'node is not on PATH; skipped the startup guard self-test.'
}

$parseTargets = @(
    Join-Path $scriptRoot 'Invoke-GatewayStartupTrace.ps1'
    Join-Path $scriptRoot 'Invoke-GatewaySetupTrace.ps1'
    Get-ChildItem -LiteralPath $toolDirectory -Include '*.ps1', '*.psm1' -File -Recurse | ForEach-Object FullName
)
foreach ($path in $parseTargets) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors) { $failures.Add("parse error in $(Split-Path -Leaf $path): $($errors[0])") }
}

$pwsh = (Get-Process -Id $PID).Path
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-startup-trace-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    $archive = Join-Path $tempRoot 'node.zip'
    [IO.File]::WriteAllText($archive, 'not a zip')
    $output = & $pwsh -NoProfile -File (Join-Path $scriptRoot 'Invoke-GatewayStartupTrace.ps1') `
        -PayloadRoot $tempRoot -NodeArchive $archive -NodeArchiveSha256 ('0' * 64) `
        -OutputDirectory (Join-Path $tempRoot 'out') -SkipRecording 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'payload root is missing payload-metadata\.json|Run this script unelevated') {
        $failures.Add("controller accepted an invalid payload: $output")
    }
    if (Test-Path -LiteralPath (Join-Path $tempRoot 'out')) {
        $failures.Add('controller created a run directory before validating inputs')
    }

    $output = & $pwsh -NoProfile -File (Join-Path $scriptRoot 'Invoke-GatewaySetupTrace.ps1') `
        -SetupLogDirectory $tempRoot -PayloadAppRoot $tempRoot -OutputDirectory (Join-Path $tempRoot 'setup-out') 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'missing openclaw\.mjs|Run this script unelevated') {
        $failures.Add("setup controller accepted an invalid payload: $output")
    }
    if (Test-Path -LiteralPath (Join-Path $tempRoot 'setup-out')) {
        $failures.Add('setup controller created a run directory before validating inputs')
    }

    # Summaries must reject roots that cannot match any payload file instead of reporting zero scans.
    $payloadA = New-Item -ItemType Directory -Path (Join-Path $tempRoot 'payload-a')
    $payloadB = New-Item -ItemType Directory -Path (Join-Path $tempRoot 'payload-b')
    'x' | Set-Content (Join-Path $payloadA 'openclaw.mjs')
    'x' | Set-Content (Join-Path $payloadB 'openclaw.mjs')
    $setupSummary = Join-Path $toolDirectory 'Get-GatewaySetupTraceSummary.ps1'
    $output = & $pwsh -NoProfile -File $setupSummary -TracePath x.etl -ResultPath x.json `
        -PayloadAppRoot "$payloadA,$payloadB" 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'comma-joined') {
        $failures.Add("setup summary accepted a comma-joined payload root: $output")
    }
    $output = & $pwsh -NoProfile -File $setupSummary -TracePath x.etl -ResultPath x.json `
        -PayloadAppRoot $tempRoot 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'does not contain openclaw\.mjs') {
        $failures.Add("setup summary accepted a root without openclaw.mjs: $output")
    }
    $output = & $pwsh -NoProfile -File (Join-Path $toolDirectory 'Get-GatewayStartupTraceSummary.ps1') `
        -TracePath x.etl -ResultPath x.json -PayloadRoot $tempRoot 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -or $output -notmatch 'does not contain app\\openclaw\.mjs') {
        $failures.Add("startup summary accepted a payload root without app\openclaw.mjs: $output")
    }

    # The recorder must refuse names it does not own before touching WPR.
    $output = & $pwsh -NoProfile -File (Join-Path $toolDirectory 'Start-GatewayStartupRecorder.ps1') `
        -InstanceName 'SomeoneElse' -TracePath (Join-Path $tempRoot 'x.etl') -ControlDirectory $tempRoot 2>&1 | Out-String
    $status = Get-Content -LiteralPath (Join-Path $tempRoot 'recorder-status.json') -Raw | ConvertFrom-Json
    if ($status.outcome -notmatch 'not a task-owned' -or $status.cancel -ne 'not-needed' -or $status.instance_recording_after) {
        $failures.Add("recorder accepted an unowned instance name: $($status | ConvertTo-Json -Compress)")
    }

    $paths = [pscustomobject]@{
        Stop = Join-Path $tempRoot 'stop'
        Status = Join-Path $tempRoot 'status.json'
    }
    $instance = 'OpenClawStartup-0123456789abcdef0123456789abcdef'
    [ordered]@{
        instance = $instance
        outcome = 'failed: test'
        cancel = 'canceled'
        instance_recording_after = $false
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath $paths.Status
    $recording = Stop-GatewayTraceRecorder -InstanceName $instance -Paths $paths -StopTimeoutSeconds 1
    Assert-Equal 'failed: test' $recording.outcome 'recorder failure status is returned to controller'
    Assert-Equal $false $recording.instance_recording_after 'recorder stopped status is surfaced'
    $paths.Status = Join-Path $tempRoot 'missing-status.json'
    $missingRecording = Stop-GatewayTraceRecorder -InstanceName $instance -Paths $paths -StopTimeoutSeconds 0
    Assert-Equal 'missing-status' $missingRecording.outcome 'missing recorder status is a failure'
} finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}

$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (Test-Path -LiteralPath $windowsPowerShell) {
    $output = & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -Command "& '$(Join-Path $scriptRoot 'Invoke-GatewayStartupTrace.ps1')' -PayloadRoot x -NodeArchive x -NodeArchiveSha256 ('0' * 64)" 2>&1 | Out-String
    if ($output -notmatch 'PowerShell 7') { $failures.Add("controller ran under Windows PowerShell: $output") }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'Gateway startup trace tests passed.'
