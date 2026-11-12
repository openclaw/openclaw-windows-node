$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$runner = Join-Path $PSScriptRoot 'Invoke-CiProcess.ps1'
$shell = (Get-Process -Id $PID).Path
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
        & $runner -FilePath $shell -ArgumentList @('-NoProfile', '-File', $child, 'space "quote"') -IdleTimeoutSeconds 2 -OutputPath 'output.log'
    } finally { Pop-Location }
    $text = [IO.File]::ReadAllText($output)
    if ($LASTEXITCODE -ne 7 -or $text -notmatch 'space "quote"' -or
        $text -notmatch 'stderr retained' -or $text -notmatch 'trailing output') {
        throw 'CI process lost exit status, argument boundaries, stderr or trailing output.'
    }

    [IO.File]::WriteAllText($child, @'
$elapsed = [Diagnostics.Stopwatch]::StartNew()
while ($elapsed.ElapsedMilliseconds -lt 5000) {
    [Console]::Out.Write('fixture progressing ')
    [Threading.Thread]::Sleep(100)
}
exit 0
'@)
    & $runner -FilePath $shell -ArgumentList @('-NoProfile', '-File', $child) -IdleTimeoutSeconds 2 -OutputPath $output
    if ($LASTEXITCODE -ne 0) { throw 'A progressing fixture exceeded its output-idle deadline.' }

    [IO.File]::WriteAllText($child, @'
param([string]$Receipt)
[IO.File]::WriteAllText($Receipt, [string]$PID)
[Console]::Out.WriteLine('fixture entered')
[Threading.Thread]::Sleep(30000)
'@)
    $receipt = Join-Path $root 'pid.txt'
    $timedOut = $false
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $runner -FilePath $shell -ArgumentList @('-NoProfile', '-File', $child, $receipt) -IdleTimeoutSeconds 2 -OutputPath $output
    } catch {
        if ($_.Exception.Message -notmatch 'produced no output') { throw }
        $timedOut = $true
    }
    if (-not $timedOut -or $elapsed.Elapsed.TotalSeconds -ge 15 -or -not (Test-Path $receipt)) {
        throw 'Silent fixture did not fail promptly after entering its body.'
    }
    $childId = [int][IO.File]::ReadAllText($receipt)
    if ($null -ne (Get-Process -Id $childId -ErrorAction SilentlyContinue)) {
        throw 'Timed-out CI child remained alive.'
    }
    if ([IO.File]::ReadAllText($output) -notmatch 'fixture entered[\s\S]*produced no output') {
        throw 'Timeout did not retain progress and the failure reason.'
    }

    [IO.File]::WriteAllText($child, @'
param([string]$Receipt, [string]$Mode)
$start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
$start.UseShellExecute = $false
foreach ($argument in @('-NoProfile', '-Command', "[Console]::Out.WriteLine('descendant entered'); [Threading.Thread]::Sleep(30000)")) {
    $start.ArgumentList.Add($argument)
}
$descendant = [Diagnostics.Process]::Start($start)
[IO.File]::WriteAllText($Receipt, [string]$descendant.Id)
if ($Mode -eq 'live-root') { [Threading.Thread]::Sleep(30000) }
exit 0
'@)
    foreach ($mode in @('live-root', 'exited-root')) {
        $descendantReceipt = Join-Path $root "$mode.pid"
        $timedOut = $false
        try {
            try {
                & $runner -FilePath $shell -ArgumentList @('-NoProfile', '-File', $child, $descendantReceipt, $mode) -IdleTimeoutSeconds 2 -OutputPath $output
            } catch {
                if ($_.Exception.Message -notmatch 'produced no output') { throw }
                $timedOut = $true
            }
            if (-not $timedOut -or [IO.File]::ReadAllText($output) -notmatch 'descendant entered') {
                throw "Descendant output lifecycle was not bounded: $mode."
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
Write-Host 'CI output-idle process tests passed: exit/output fidelity, progressing fixture, silent fixture termination, tree termination, inherited-pipe deadline.'
