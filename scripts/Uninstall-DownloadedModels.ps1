<#
.SYNOPSIS
    Preview or explicitly remove only verified Kokoro downloads for Inno uninstall.
.DESCRIPTION
    No recursive removal, app startup, Gateway cleanup, or shared-cache access.
    The installer keeps its migration lease and rechecks preservation before removal.
    A nonzero selection requires -ConfirmDestructive. The C# owner re-verifies every
    selected file, pins its ancestors, and deletes using its verified file handle.
    Reports are local to this uninstall, never telemetry or an ownership receipt.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DataDirectory,
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [Parameter(Mandatory = $true)][string]$SelectionPath,
    [ValidateRange(0, 15)][int]$SelectionMask = 0,
    [string]$CancellationPath = '',
    [switch]$ConfirmDestructive,
    [switch]$Worker
)

$ErrorActionPreference = 'Stop'
$encoding = New-Object System.Text.UTF8Encoding $true
try {
    if ($SelectionMask -ne 0 -and -not $ConfirmDestructive) {
        throw 'Model removal requires explicit confirmation.'
    }
    if (-not $Worker) {
        # Bound compiler startup as well as scanning. On timeout, do not retry a
        # possibly partial deletion or claim the selected files were all removed.
        $seconds = 180
        $configured = 0
        if ([int]::TryParse($env:OPENCLAW_MODEL_UNINSTALL_PROCESS_TIMEOUT_SECONDS, [ref]$configured) -and
            $configured -ge 30 -and $configured -le 900) { $seconds = $configured }
        $cancellationPollMilliseconds = 100
        $terminationWaitMilliseconds = 10000
        if ($CancellationPath -and [IO.File]::Exists($CancellationPath)) {
            throw 'Model check was cancelled before starting. No model removal was started.'
        }
        $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $PSScriptRoot 'Uninstall-DownloadedModels.ps1'),
            '-DataDirectory', $DataDirectory, '-ReportPath', $ReportPath,
            '-SelectionPath', $SelectionPath, '-SelectionMask', [string]$SelectionMask, '-Worker')
        if ($ConfirmDestructive) { $arguments += '-ConfirmDestructive' }
        # Every value is quoted using Windows argv backslash rules.
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
        try {
            $clock = [Diagnostics.Stopwatch]::StartNew()
            while (-not $child.WaitForExit($cancellationPollMilliseconds)) {
                $cancelled = $CancellationPath -and [IO.File]::Exists($CancellationPath)
                if ($cancelled -or $clock.Elapsed.TotalSeconds -ge $seconds) {
                    $child.Kill()
                    if (-not $child.WaitForExit($terminationWaitMilliseconds)) {
                        throw 'The model helper has not confirmed exit. Its result is unknown; do not retry cleanup.'
                    }
                    if ($cancelled -and $SelectionMask -eq 0) {
                        throw 'Model check cancelled. No model removal was started.'
                    }
                    throw 'Model cleanup interrupted. Removal may be partial; inspect the model paths before retrying.'
                }
            }
            exit $child.ExitCode
        } finally { $child.Dispose() }
    }

    Add-Type -Path @((Join-Path $PSScriptRoot 'KokoroDownloadCatalog.cs'),
        (Join-Path $PSScriptRoot 'KokoroUninstallFiles.cs')) -ErrorAction Stop
    $result = [OpenClaw.Shared.Audio.KokoroUninstallFiles]::Run($DataDirectory, $SelectionMask)
    [IO.File]::WriteAllText($ReportPath, $result.Text, $encoding)
    [IO.File]::WriteAllText($SelectionPath, [string]$result.EligibleMask, $encoding)
    if (-not $result.Complete) { exit 1 }
    exit 0
} catch {
    [IO.File]::WriteAllText($SelectionPath, '0', $encoding)
    $message = @(
        'Model cleanup could not be completed or verified. Files may remain; no complete-cleanup claim is made.',
        "Model path: $DataDirectory\models\kokoro",
        'Other app data and shared caches are outside this choice. Sizes are unknown.',
        $_.Exception.Message
    ) -join [Environment]::NewLine
    [IO.File]::WriteAllText($ReportPath, $message, $encoding)
    exit 2
}
