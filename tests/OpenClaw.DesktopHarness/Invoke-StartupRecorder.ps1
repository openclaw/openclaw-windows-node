[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Config,
    [Parameter(Mandatory)][string] $LeaseId,
    [Parameter(Mandatory)][string] $RunRoot,
    [Parameter(Mandatory)][ValidateSet('Disabled','Record')][string] $Mode,
    [ValidateSet('Capture','Watchdog')][string] $Role = 'Capture'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\StartupProof.ps1"
. "$PSScriptRoot\StartupProof.Native.ps1"
$c = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
if ($c.computer -cne $env:COMPUTERNAME -or $c.leaseId -cne $LeaseId) {
    throw 'Recorder requires the exact authorized guest elevated controller, never observer elevation.'
}
$actor = [Security.Principal.WindowsIdentity]::GetCurrent()
if (!([Security.Principal.WindowsPrincipal]::new($actor)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Recorder requires the exact authorized guest elevated controller, never observer elevation.'
}
if ($c.instanceName -notmatch '^OpenClaw1791-[0-9a-f]{32}$' -or
    !$c.installedProfileQualified -or $c.deadlineSeconds -ne 900 -or $c.toolDeadlineSeconds -ne 30) {
    throw 'Actual installed profile, unique named-instance grammar and unchanged budgets are required.'
}
$control = Join-Path $RunRoot 'control'
$private = Join-Path $control 'private'
$stoppedPath = Join-Path $control 'trace-stopped.json'
$intentPath = Join-Path $control 'adapter-started.json'
$ownerPath = Join-Path $control 'recorder-owner.json'
$mutex = [Threading.Mutex]::new($false,('Local\' + $c.instanceName + '-stop'))
$sequence = 0
function Invoke-RecorderWpr([ValidateSet('Status','Start','Stop','Cancel')][string] $Operation) {
    $script:sequence++
    $p = $null
    $psi = New-StartupRecorderStartInfo -Wpr $c.wpr -Operation $Operation `
        -InstanceName $c.instanceName -Profile $c.profile -TracePath (Join-Path $private 'startup.etl')
    try {
        $p = [Diagnostics.Process]::Start($psi)
        $stdout = $p.StandardOutput.ReadToEndAsync(); $stderr = $p.StandardError.ReadToEndAsync()
        if (!$p.WaitForExit($c.toolDeadlineSeconds * 1000)) {
            $p.Kill($true); $p.WaitForExit(); throw 'Exact owned WPR subprocess exceeded unchanged tool bound.'
        }
        $text = $stdout.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $private "$Role-$script:sequence.out"),$text)
        [IO.File]::WriteAllText((Join-Path $private "$Role-$script:sequence.err"),$stderr.GetAwaiter().GetResult())
        @{ exitCode = $p.ExitCode; text = $text }
    } finally { if ($p) { $p.Dispose() } }
}
function Test-RecorderAbsent {
    $status = Invoke-RecorderWpr -Operation Status
    Test-StartupRecorderStatusAbsent -ExitCode $status.exitCode -Text $status.text `
        -ExpectedExitCode $c.absentStatusExitCode -ExpectedPattern $c.absentStatusPattern
}
function Stop-StartupRecorder {
    $held = $false
    try {
        try { $held = $mutex.WaitOne(30000) } catch [Threading.AbandonedMutexException] { $held = $true }
        if (!$held) { throw 'Exact owned recorder stop mutex unavailable.' }
        if (Test-Path $stoppedPath) { return }
        $stopExit = $null; $cancelExit = $null
        if ($Mode -ceq 'Record' -and (Test-Path $intentPath) -and !(Test-RecorderAbsent)) {
            $stopExit = (Invoke-RecorderWpr -Operation Stop).exitCode
            if (!(Test-RecorderAbsent)) {
                $cancelExit = (Invoke-RecorderWpr -Operation Cancel).exitCode
            }
        }
        $absent = Test-RecorderAbsent
        $file = Get-Item (Join-Path $private 'startup.etl') -ErrorAction SilentlyContinue
        [ordered]@{
            stopped = $true; instanceAbsent = $absent; leaseId = $LeaseId
            instanceName = $c.instanceName; sourceHead = $c.sourceHead; computer = $env:COMPUTERNAME
            mode = $Mode; stoppedBy = $Role; stoppedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            stopExitCode = $stopExit; cancelExitCode = $cancelExit
            traceStartsCounter = [int](Test-Path -LiteralPath $c.traceStartsFile)
            etlPrivate = $true; etlBytes = if ($file) { $file.Length } else { 0 }
            etlSha256 = if ($file) { (Get-FileHash $file.FullName).Hash } else { $null }
            etlLastWriteUtc = if ($file) { $file.LastWriteTimeUtc.ToString('O') } else { $null }
            eventsLost = $null; buffersLost = $null; corruptionStatus = 'not-decoded'
        } | ConvertTo-Json | Set-Content "$stoppedPath.writing" -Encoding utf8NoBOM
        Move-Item "$stoppedPath.writing" $stoppedPath
        if (!$absent) { throw 'Owned recorder still present; cleanup blocker.' }
    } finally { if ($held) { $mutex.ReleaseMutex() } }
}
try {
    if ($Role -ceq 'Watchdog') {
        $owner = Get-Content $ownerPath -Raw | ConvertFrom-Json
        @{ pid = $PID; creationUtc = [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().ToString('O') } |
            ConvertTo-Json | Set-Content (Join-Path $control 'watchdog-ready.json')
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $watcher = [IO.FileSystemWatcher]::new($control)
        try {
            while (!(Test-Path $stoppedPath)) {
                $p = Get-Process -Id $owner.pid -ErrorAction SilentlyContinue
                if (!(Test-StartupProcessIdentity $owner.pid $owner.creationUtc $p) -or
                    $clock.Elapsed.TotalSeconds -ge $c.deadlineSeconds) { Stop-StartupRecorder; break }
                $null = $watcher.WaitForChanged([IO.WatcherChangeTypes]::All,100)
            }
        } finally { $watcher.Dispose() }
        return
    }
    $null = New-Item -ItemType Directory -Path $private
    $caller = Wait-StartupReceipt (Join-Path $RunRoot 'scenario\caller-ready.json') 60
    $caller | Add-Member -NotePropertyName actualProcess -NotePropertyValue (Get-Process -Id $caller.pid -ErrorAction Stop)
    $binding = Get-StartupBinding $c $caller
    if ($Mode -ceq 'Record') {
        $dry = Get-Content $c.dryRunReceipt -Raw | ConvertFrom-Json
        Assert-StartupDryRun $dry $binding.digest ([int](Test-Path $c.traceStartsFile))
        # Reserve this lease's only recording before any WPR start. Never overwrite it.
        $reservation = [IO.File]::Open($c.traceReservation,[IO.FileMode]::CreateNew)
        $reservation.Dispose()
    } elseif (Test-Path $c.traceStartsFile) { throw 'Dry-run after recording is prohibited.' }
    if (!(Test-RecorderAbsent)) { throw 'Unique owned WPR instance is not absent.' }
    $self = [Diagnostics.Process]::GetCurrentProcess()
    @{ pid = $PID; creationUtc = $self.StartTime.ToUniversalTime().ToString('O') } |
        ConvertTo-Json | Set-Content $ownerPath
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $c.powerShell; $psi.UseShellExecute = $false
    foreach ($arg in @('-NoProfile','-File',$PSCommandPath,'-Config',$Config,'-LeaseId',$LeaseId,
        '-RunRoot',$RunRoot,'-Mode',$Mode,'-Role','Watchdog')) { $psi.ArgumentList.Add($arg) }
    $watchdog = [Diagnostics.Process]::Start($psi)
    try {
        $null = Wait-StartupReceipt (Join-Path $control 'watchdog-ready.json') 10
        @{ prepared = $true; bindingHash = $binding.digest; leaseId = $LeaseId } |
            ConvertTo-Json | Set-Content (Join-Path $control 'recorder-prepared.json')
        $observed = Wait-StartupReceipt (Join-Path $RunRoot 'scenario\observer-qualified.json') 60
        if ($observed.ready -isnot [bool] -or !$observed.ready -or
            $observed.bindingHash -cne $binding.digest -or $observed.leaseId -cne $LeaseId) {
            throw 'Actual observer did not qualify for the prepared recorder.'
        }
        @{ entered = $true; mode = $Mode } | ConvertTo-Json | Set-Content $intentPath
        if ($Mode -ceq 'Record') {
            $attempt = [IO.File]::Open($c.traceStartsFile,[IO.FileMode]::CreateNew)
            try {
                $stamp = [Text.Encoding]::UTF8.GetBytes((@{
                    utc = [DateTimeOffset]::UtcNow.ToString('O'); qpc = [Diagnostics.Stopwatch]::GetTimestamp()
                    qpcFrequency = [Diagnostics.Stopwatch]::Frequency; tickCount64 = [Environment]::TickCount64
                    pointerBytes = [IntPtr]::Size; sourceHead = $c.sourceHead; bindingHash = $binding.digest
                } | ConvertTo-Json))
                $attempt.Write($stamp,0,$stamp.Length)
            } finally { $attempt.Dispose() }
            $start = Invoke-RecorderWpr -Operation Start
            if ($start.exitCode -ne 0) { throw 'Only trace start failed; no retry authorized.' }
        }
        @{ ready = $true; bindingHash = $binding.digest; leaseId = $LeaseId
            traceStartsCounter = [int](Test-Path $c.traceStartsFile) } |
            ConvertTo-Json | Set-Content (Join-Path $control 'trace-ready.json')
        $null = Wait-StartupReceipt (Join-Path $control 'scenario-finished.json') $c.deadlineSeconds
    } finally {
        Stop-StartupRecorder
        if (!$watchdog.WaitForExit(30000)) { throw 'Owned watchdog did not exit after recorder finalization.' }
        $watchdog.Dispose()
    }
} finally {
    if ($Role -ceq 'Capture' -and (Test-Path $private)) { Stop-StartupRecorder }
    $mutex.Dispose()
}
