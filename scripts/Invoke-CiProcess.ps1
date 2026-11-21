<# Runs a CI process with an output-idle deadline and an optional wall deadline. The wall deadline
   lets a caller end the run before its workflow step limit, so the step still reports and its
   always() upload still runs; the workflow keeps the hard step and job deadlines. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FilePath,
    [Parameter(Mandatory)][string[]]$ArgumentList,
    [Parameter(Mandatory)][ValidateRange(1, 3600)][int]$IdleTimeoutSeconds,
    [Parameter(Mandatory)][string]$OutputPath,
    # 0 disables the wall deadline.
    [ValidateRange(0, 86400)][int]$WallTimeoutSeconds = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Names, parents and start times of processes started since the root, to show what a hung run was
# still doing. Command lines are omitted because test processes can carry credentials in their
# arguments. Get-Process only, so a wedged management service cannot block it. On a CI runner,
# processes started after the root are almost all the root's own.
function Write-CiProcessSnapshot([datetime]$Since, [IO.StreamWriter]$Log) {
    try {
        $recent = @(Get-Process | Where-Object {
            try { $_.StartTime -ge $Since } catch { $false }
        } | Sort-Object StartTime)
        $Log.WriteLine("Processes started since the CI process began: $($recent.Count)")
        foreach ($entry in $recent) {
            $parent = try { $entry.Parent } catch { $null }
            $parentText = if ($null -eq $parent) { 'parent exited or unavailable' } else { "parent $($parent.Id)" }
            $startedText = $entry.StartTime.ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture)
            $Log.WriteLine("  PID $($entry.Id) $parentText $($entry.ProcessName) started $startedText")
        }
    } catch {
        $Log.WriteLine("Process snapshot unavailable ($($_.Exception.GetType().Name)).")
    }
}

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
    # A small margin keeps the root in the snapshot if the clock is corrected during the run.
    $launchedAt = (Get-Date).AddSeconds(-1)
    $started = $process.Start()
    if (-not $started) { throw "Could not start CI process: $FilePath" }
    $readers = @($process.StandardOutput, $process.StandardError)
    $buffers = @([char[]]::new(4096), [char[]]::new(4096))
    $reads = @(
        $readers[0].ReadAsync([Memory[char]]::new($buffers[0]), $cancellation.Token).AsTask(),
        $readers[1].ReadAsync([Memory[char]]::new($buffers[1]), $cancellation.Token).AsTask()
    )
    $idle = [Diagnostics.Stopwatch]::StartNew()
    $wall = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $idleRemaining = $IdleTimeoutSeconds * 1000L - $idle.ElapsedMilliseconds
        $wallRemaining = if ($WallTimeoutSeconds -gt 0) { $WallTimeoutSeconds * 1000L - $wall.ElapsedMilliseconds } else { [long]::MaxValue }
        $wallExpired = $wallRemaining -le 0
        if (-not $wallExpired -and $idleRemaining -gt 0) {
            $remaining = [int][Math]::Min($idleRemaining, $wallRemaining)
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
                }
            }
            # A wait can return slightly before either stopwatch reaches its deadline; check again.
            continue
        }
        $message = if ($wallExpired) {
            "CI process exceeded its wall deadline of $WallTimeoutSeconds seconds."
        } else {
            "CI process produced no output for $IdleTimeoutSeconds seconds."
        }
        if ($process.HasExited) { $message += ' The root exited but its output streams remained open.' }
        $log.WriteLine($message)
        Write-CiProcessSnapshot $launchedAt $log
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
