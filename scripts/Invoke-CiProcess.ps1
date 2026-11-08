<# Runs a CI process with an output-idle deadline. The workflow owns the hard wall deadline. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FilePath,
    [Parameter(Mandatory)][string[]]$ArgumentList,
    [Parameter(Mandatory)][ValidateRange(1, 3600)][int]$IdleTimeoutSeconds,
    [Parameter(Mandatory)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$start = [Diagnostics.ProcessStartInfo]::new($FilePath)
$start.UseShellExecute = $false
$start.WorkingDirectory = (Get-Location).ProviderPath
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in $ArgumentList) { $start.ArgumentList.Add($argument) }
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath, $start.WorkingDirectory)
New-Item -ItemType Directory -Force (Split-Path $resolvedOutput -Parent) | Out-Null
$log = [IO.StreamWriter]::new($resolvedOutput)
$log.AutoFlush = $true
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$started = $false
$readers = @()
$reads = @()
$cancellation = [Threading.CancellationTokenSource]::new()
try {
    $started = $process.Start()
    if (-not $started) { throw "Could not start CI process: $FilePath" }
    $readers = @($process.StandardOutput, $process.StandardError)
    $buffers = @([char[]]::new(4096), [char[]]::new(4096))
    $reads = @(
        $readers[0].ReadAsync([Memory[char]]::new($buffers[0]), $cancellation.Token).AsTask(),
        $readers[1].ReadAsync([Memory[char]]::new($buffers[1]), $cancellation.Token).AsTask()
    )
    $idle = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $remaining = [int][Math]::Max(0, $IdleTimeoutSeconds * 1000 - $idle.ElapsedMilliseconds)
        $active = @($reads | Where-Object { $null -ne $_ })
        if ($active.Count -eq 0) {
            if ($process.WaitForExit($remaining)) { break }
        } else {
            $completed = [Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]$active, $remaining)
            if ($completed -ge 0) {
                $index = [Array]::IndexOf($reads, $active[$completed])
                $count = $reads[$index].GetAwaiter().GetResult()
                if ($count -eq 0) {
                    $reads[$index] = $null
                } else {
                    $idle.Restart()
                    $text = [string]::new($buffers[$index], 0, $count)
                    $log.Write($text)
                    [Console]::Write($text)
                    $reads[$index] = $readers[$index].ReadAsync([Memory[char]]::new($buffers[$index]), $cancellation.Token).AsTask()
                }
                continue
            }
        }
        $message = "CI process produced no output for $IdleTimeoutSeconds seconds."
        if ($process.HasExited) { $message += ' The root exited but its output streams remained open.' }
        $log.WriteLine($message)
        throw $message
    }
    $global:LASTEXITCODE = $process.ExitCode
} finally {
    if ($started -and -not $process.HasExited) {
        try {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
        } catch {
            Write-Warning "CI process cleanup failed: $($_.Exception.Message)"
        }
    }
    $cancellation.Cancel()
    foreach ($reader in $readers) { $reader.Dispose() }
    $pending = @($reads | Where-Object { $null -ne $_ })
    if ($pending.Count -gt 0) {
        try { $null = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$pending, 1000) }
        catch [AggregateException] { } # Cancellation and closed-pipe reads are expected during cleanup.
    }
    if (@($pending | Where-Object { -not $_.IsCompleted }).Count -gt 0) {
        $log.WriteLine('CI output readers did not settle after cancellation.')
        Write-Warning 'CI output readers did not settle after cancellation.'
    } else {
        $log.WriteLine('CI output readers settled after cancellation.')
    }
    $cancellation.Dispose()
    $process.Dispose()
    $log.Dispose()
}
