<#
.SYNOPSIS
    Preview or explicitly remove only verified Kokoro downloads for Inno uninstall.
.DESCRIPTION
    The installer owns consent and the migration lease. The worker re-verifies
    selected files and deletes through their pinned handles. No recursive cleanup.
    The supervisor atomically publishes one completion report AFTER worker exit:
    first line is 32..47 (preview mask = code - 32), 32 (removal complete),
    48 (partial removal), 49 (stopped, failed/cancelled), or 50 (exit unconfirmed).
    Remaining lines are the human-readable report. Launch alone is not completion.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [ValidateRange(0, 15)][int]$SelectionMask = 0,
    [string]$CancellationPath = '',
    [switch]$ConfirmDestructive,
    [switch]$Worker
)

$ErrorActionPreference = 'Stop'
$encoding = New-Object System.Text.UTF8Encoding $true
$failureText = "Model operation could not be completed. Sizes or removal may be incomplete.$([Environment]::NewLine)Model path: $DataDirectory\models\kokoro"

if ($Worker) {
    try {
        if ($SelectionMask -ne 0 -and -not $ConfirmDestructive) { throw 'Explicit removal confirmation is missing.' }
        Add-Type -Path @((Join-Path $PSScriptRoot 'KokoroDownloadCatalog.cs'),
            (Join-Path $PSScriptRoot 'KokoroUninstallFiles.cs')) -ErrorAction Stop
        $result = [OpenClaw.Shared.Audio.KokoroUninstallFiles]::Run($DataDirectory, $SelectionMask)
        [IO.File]::WriteAllText($ReportPath, $result.Text, $encoding)
        if ($SelectionMask -eq 0) { exit (32 + $result.EligibleMask) }
        if (-not $result.Complete) { exit 48 }
        exit 32
    } catch {
        [IO.File]::WriteAllText($ReportPath, $failureText + [Environment]::NewLine + $_.Exception.Message, $encoding)
        exit 49
    }
}

$child = $null
$stopped = $true
$code = 49
$report = $failureText
try {
    if ($SelectionMask -ne 0 -and -not $ConfirmDestructive) { throw 'Explicit removal confirmation is missing.' }
    $seconds = 180
    $configured = 0
    if ([int]::TryParse($env:OPENCLAW_MODEL_UNINSTALL_PROCESS_TIMEOUT_SECONDS, [ref]$configured) -and
        $configured -ge 30 -and $configured -le 900) { $seconds = $configured }
    $cancellationPollMilliseconds = 100
    $terminationWaitMilliseconds = 10000
    if ($CancellationPath -and [IO.File]::Exists($CancellationPath)) {
        throw 'Model check cancelled before starting. No model removal was started.'
    }
    $workerReport = $ReportPath + '.worker'
    $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
        $PSCommandPath, '-DataDirectory', $DataDirectory, '-ReportPath', $workerReport,
        '-SelectionMask', [string]$SelectionMask, '-Worker')
    if ($ConfirmDestructive) { $arguments += '-ConfirmDestructive' }
    $quoted = foreach ($argument in $arguments) {
        if ($argument.Contains('"')) { throw 'Unexpected quote in uninstall path.' }
        '"' + ($argument -replace '(\\+)$', '$1$1') + '"'
    }
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $PSHOME 'powershell.exe'
    $start.Arguments = $quoted -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $child = [Diagnostics.Process]::Start($start)
    $stopped = $false
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not $child.WaitForExit($cancellationPollMilliseconds)) {
        $cancelled = $CancellationPath -and [IO.File]::Exists($CancellationPath)
        if ($cancelled -or $clock.Elapsed.TotalSeconds -ge $seconds) {
            $child.Kill()
            $stopped = $child.WaitForExit($terminationWaitMilliseconds)
            if (-not $stopped) { throw 'Worker exit is unconfirmed. Uninstall must stop; do not retry cleanup.' }
            if ($cancelled -and $SelectionMask -eq 0) { throw 'Model check cancelled. No model removal was started.' }
            throw 'Model operation interrupted. Removal may be partial; inspect the model paths before retrying.'
        }
    }
    $stopped = $true
    $code = $child.ExitCode
    if ($code -lt 32 -or $code -gt 49) { throw "Unexpected model worker exit code: $code" }
    $report = [IO.File]::ReadAllText($workerReport)
} catch {
    if (-not $stopped -and $null -ne $child) {
        try { $stopped = $child.HasExited } catch { }
    }
    $code = if ($stopped) { 49 } else { 50 }
    $report = $failureText + [Environment]::NewLine + $_.Exception.Message
} finally {
    if ($null -ne $child) { $child.Dispose() }
}

# A unique per-invocation path is supplied by Inno. A partial write is never a receipt.
[IO.File]::WriteAllText($ReportPath + '.pending', [string]$code + [Environment]::NewLine + $report, $encoding)
[IO.File]::Move($ReportPath + '.pending', $ReportPath)
exit $code
