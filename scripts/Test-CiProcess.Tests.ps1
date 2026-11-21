$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$runner = Join-Path $PSScriptRoot 'Invoke-CiProcess.ps1'
$shell = (Get-Process -Id $PID).Path
# Output-idle budget for every fixture. Each fixture starts a cold pwsh child, which took up to
# 7 s to print anything on a contended host, so a 2 s budget measured startup, not idleness.
# Fixture durations derive from this budget so each case still proves the same behavior.
$idleSeconds = 10
$parsedIdle = 0
if ([int]::TryParse($env:OPENCLAW_CI_PROCESS_TEST_IDLE_SECONDS, [ref]$parsedIdle) -and $parsedIdle -gt 0) {
    $idleSeconds = $parsedIdle
}
# Silent fixtures must fail within this bound; held processes outlive it.
$promptSeconds = $idleSeconds + 13
$holdSeconds = $promptSeconds * 2
$root = Join-Path ([IO.Path]::GetTempPath()) "ci-process-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $root | Out-Null
try {
    $child = Join-Path $root 'child.ps1'
    $output = Join-Path $root 'output.log'
    [IO.File]::WriteAllText($child, @'
param([string]$Value)
[Console]::Out.WriteLine($Value)
[Console]::Error.WriteLine('stderr retained')
[Console]::Out.Write('trailing output')
exit 7
'@)
    Push-Location $root
    try {
        & $runner -FilePath $shell -ArgumentList @('-NoProfile', '-File', $child, 'space "quote"') -IdleTimeoutSeconds $idleSeconds -OutputPath 'output.log'
    } finally { Pop-Location }
    $text = [IO.File]::ReadAllText($output)
    if ($LASTEXITCODE -ne 7 -or $text -notmatch 'space "quote"' -or
        $text -notmatch 'stderr retained' -or $text -notmatch 'trailing output') {
        throw 'CI process lost exit status, argument boundaries, stderr or trailing output.'
    }

    # Progress for longer than the idle budget proves output resets the deadline.
    [IO.File]::WriteAllText($child, @'
param([int]$Seconds)
$elapsed = [Diagnostics.Stopwatch]::StartNew()
while ($elapsed.ElapsedMilliseconds -lt $Seconds * 1000) {
    [Console]::Out.Write('fixture progressing ')
    [Threading.Thread]::Sleep(100)
}
exit 0
'@)
    & $runner -FilePath $shell -ArgumentList @('-NoProfile', '-File', $child, [string]($idleSeconds + 3)) -IdleTimeoutSeconds $idleSeconds -OutputPath $output
    if ($LASTEXITCODE -ne 0) { throw 'A progressing fixture exceeded its output-idle deadline.' }

    # A fixture that keeps printing never trips the idle deadline; only the wall deadline ends it,
    # so the caller can still report before its workflow step limit.
    [IO.File]::WriteAllText($child, @'
param([string]$Receipt, [int]$Seconds)
[IO.File]::WriteAllText($Receipt, [string]$PID)
$elapsed = [Diagnostics.Stopwatch]::StartNew()
while ($elapsed.ElapsedMilliseconds -lt $Seconds * 1000) {
    [Console]::Out.Write('fixture still progressing ')
    [Threading.Thread]::Sleep(100)
}
exit 0
'@)
    $wallReceipt = Join-Path $root 'wall.pid'
    $wallStarted = [Diagnostics.Stopwatch]::StartNew()
    $wallEnded = $false
    try {
        & $runner -FilePath $shell -ArgumentList @('-NoProfile', '-File', $child, $wallReceipt, [string]$holdSeconds) `
            -IdleTimeoutSeconds $idleSeconds -WallTimeoutSeconds $idleSeconds -OutputPath $output
    } catch {
        if ($_.Exception.Message -notmatch 'exceeded its wall deadline') { throw }
        $wallEnded = $true
    }
    if (-not $wallEnded -or $wallStarted.Elapsed.TotalSeconds -ge $promptSeconds) {
        throw 'A continuously progressing fixture was not ended promptly by its wall deadline.'
    }
    $wallLog = [IO.File]::ReadAllText($output)
    if (-not (Test-Path $wallReceipt)) { throw 'Wall-deadline fixture never started.' }
    $wallChildId = [int][IO.File]::ReadAllText($wallReceipt)
    if ($wallLog -notmatch 'fixture still progressing[\s\S]*exceeded its wall deadline' -or
        $wallLog -notmatch "Processes started since the CI process began[\s\S]*PID $wallChildId ") {
        throw 'Wall deadline did not retain progress, the failure reason and a snapshot naming the running fixture.'
    }
    $wallChild = Get-Process -Id $wallChildId -ErrorAction SilentlyContinue
    if ($null -ne $wallChild -and -not $wallChild.WaitForExit(5000)) {
        throw 'Wall deadline left its CI child alive.'
    }

    [IO.File]::WriteAllText($child, @'
param([string]$Receipt, [int]$HoldSeconds)
[IO.File]::WriteAllText("$Receipt.entered", [string][Diagnostics.Stopwatch]::GetTimestamp())
[IO.File]::WriteAllText($Receipt, [string]$PID)
[Console]::Out.WriteLine('fixture entered')
[Threading.Thread]::Sleep($HoldSeconds * 1000)
'@)
    $receipt = Join-Path $root 'pid.txt'
    $timedOut = $false
    try {
        & $runner -FilePath $shell -ArgumentList @('-NoProfile', '-File', $child, $receipt, [string]$holdSeconds) -IdleTimeoutSeconds $idleSeconds -OutputPath $output
    } catch {
        if ($_.Exception.Message -notmatch 'produced no output') { throw }
        $timedOut = $true
    }
    # Measure from body entry on the system-wide monotonic clock, so a slow cold start or a
    # wall-clock adjustment cannot affect the bound.
    if (-not $timedOut -or -not (Test-Path $receipt) -or -not (Test-Path "$receipt.entered") -or
        ([Diagnostics.Stopwatch]::GetTimestamp() - [long][IO.File]::ReadAllText("$receipt.entered")) /
            [Diagnostics.Stopwatch]::Frequency -ge $promptSeconds) {
        throw 'Silent fixture did not fail promptly after entering its body.'
    }
    $childId = [int][IO.File]::ReadAllText($receipt)
    if ($null -ne (Get-Process -Id $childId -ErrorAction SilentlyContinue)) {
        throw 'Timed-out CI child remained alive.'
    }
    if ([IO.File]::ReadAllText($output) -notmatch 'fixture entered[\s\S]*produced no output') {
        throw 'Timeout did not retain progress and the failure reason.'
    }

    # The root reports progress until its descendant signals that it has printed, so the
    # descendant's cold start is outside the measured window. The descendant (and, in live-root,
    # the root) then holds the inherited pipe silently, so only the idle deadline can end the run.
    [IO.File]::WriteAllText($child, @'
param([string]$Receipt, [string]$Mode, [int]$HoldSeconds)
$ready = "$Receipt.ready"
$start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
$start.UseShellExecute = $false
# Pass the path as data so quotes in a temp path cannot break the command.
$start.Environment['OPENCLAW_TEST_DESCENDANT_READY'] = $ready
$descendantCommand = "[Console]::Out.WriteLine('descendant entered'); [IO.File]::WriteAllText(`$env:OPENCLAW_TEST_DESCENDANT_READY, 'ready'); [Threading.Thread]::Sleep($($HoldSeconds * 1000))"
foreach ($argument in @('-NoProfile', '-Command', $descendantCommand)) {
    $start.ArgumentList.Add($argument)
}
$descendant = [Diagnostics.Process]::Start($start)
[IO.File]::WriteAllText($Receipt, [string]$descendant.Id)
$waiting = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath $ready) -and $waiting.ElapsedMilliseconds -lt $HoldSeconds * 1000) {
    [Console]::Out.Write('awaiting descendant ')
    [Threading.Thread]::Sleep(100)
}
if ($Mode -eq 'live-root') { [Threading.Thread]::Sleep($HoldSeconds * 1000) }
exit 0
'@)
    foreach ($mode in @('live-root', 'exited-root')) {
        $descendantReceipt = Join-Path $root "$mode.pid"
        $timedOut = $false
        try {
            try {
                & $runner -FilePath $shell -ArgumentList @('-NoProfile', '-File', $child, $descendantReceipt, $mode, [string]$holdSeconds) -IdleTimeoutSeconds $idleSeconds -OutputPath $output
            } catch {
                if ($_.Exception.Message -notmatch 'produced no output') { throw }
                $timedOut = $true
            }
            if (-not $timedOut -or [IO.File]::ReadAllText($output) -notmatch 'descendant entered') {
                throw "Descendant output lifecycle was not bounded: $mode."
            }
            # Without readiness the descendant may have ended on its own, which would not prove
            # that the timeout bounded a live descendant.
            if (-not (Test-Path -LiteralPath "$descendantReceipt.ready")) {
                throw "Descendant never reported readiness: $mode."
            }
            $rootExitedNote = [IO.File]::ReadAllText($output) -match 'The root exited but its output streams remained open'
            if ($rootExitedNote -ne ($mode -eq 'exited-root')) {
                throw "Timeout misreported whether the root had exited: $mode."
            }
            if ([IO.File]::ReadAllText($output) -notmatch 'CI output readers settled after cancellation') {
                throw "Pending readers survived timeout before descendant cleanup: $mode."
            }
            $descendantId = [int][IO.File]::ReadAllText($descendantReceipt)
            $descendant = Get-Process -Id $descendantId -ErrorAction SilentlyContinue
            if ($mode -eq 'live-root' -and $null -ne $descendant -and -not $descendant.WaitForExit(5000)) {
                throw "Timeout failed to terminate the live root process tree (PID $descendantId)."
            }
        } finally {
            # An exited root cannot enumerate its former descendants. The test
            # owns this PID; CI retains its independent hard step/job deadlines.
            if (Test-Path $descendantReceipt) {
                $descendant = Get-Process -Id ([int][IO.File]::ReadAllText($descendantReceipt)) -ErrorAction SilentlyContinue
                if ($null -ne $descendant -and -not $descendant.HasExited) {
                    $descendant.Kill($true)
                    $null = $descendant.WaitForExit(5000)
                }
            }
        }
    }
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
$global:LASTEXITCODE = 0
Write-Host 'CI output-idle process tests passed: exit/output fidelity, progressing fixture, wall deadline, silent fixture termination, tree termination, inherited-pipe deadline.'
