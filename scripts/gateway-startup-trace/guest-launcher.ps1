$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$process = $null
$started = $false

try {
    $spec = $env:OPENCLAW_COMPARISON_LAUNCH | ConvertFrom-Json
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $spec.executable
    $startInfo.WorkingDirectory = $spec.working_directory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.EnvironmentVariables.Clear()
    foreach ($property in $spec.environment.PSObject.Properties) {
        $startInfo.EnvironmentVariables[$property.Name] = [string]$property.Value
    }
    $quoted = @()
    foreach ($argument in $spec.arguments) {
        if ([string]$argument -match '["\r\n]') {
            throw 'The comparison argument cannot be represented safely.'
        }
        $quoted += '"' + [string]$argument + '"'
    }
    $startInfo.Arguments = $quoted -join ' '
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'The owned comparison process did not start.'
    }
    $started = $true
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $finished = $process.WaitForExit([int]$spec.timeout_ms)
    if (-not $finished) {
        if (-not $process.HasExited) {
            $process.Kill()
        }
        if (-not $process.WaitForExit(10000)) {
            throw 'The owned comparison process did not stop.'
        }
    }
    $tasks = [Threading.Tasks.Task[]]@($stdoutTask, $stderrTask)
    if (-not [Threading.Tasks.Task]::WhenAll($tasks).Wait(10000)) {
        throw 'The comparison process output did not drain.'
    }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    [void]$stderrTask.GetAwaiter().GetResult()
    $markers = @($stdout -split "`r?`n" |
        Where-Object { $_.StartsWith('OPENCLAW_STARTUP_COMPARISON=', [StringComparison]::Ordinal) })
    if ($markers.Count -eq 1) {
        [Console]::Out.WriteLine($markers[0])
    }
    if (-not $finished) {
        exit 124
    }
    exit $process.ExitCode
} catch {
    $type = $_.Exception.GetType().Name
    if ($type -notmatch '^[A-Za-z0-9_]{1,80}$') { $type = 'Error' }
    $errorId = ($_.FullyQualifiedErrorId -split ',')[0]
    if ($errorId -notmatch '^[A-Za-z0-9_.-]{1,80}$') { $errorId = 'Error' }
    $result = @{
        status = 'guest-launcher-error'
        exception_type = $type
        error_id = $errorId
        line = $_.InvocationInfo.ScriptLineNumber
    } | ConvertTo-Json -Compress
    [Console]::Out.WriteLine('OPENCLAW_COMPARISON_LAUNCHER=' + $result)
    exit 125
} finally {
    if ($null -ne $process) {
        try {
            if ($started -and -not $process.HasExited) {
                $process.Kill()
                [void]$process.WaitForExit(10000)
            }
        } finally {
            $process.Dispose()
        }
    }
}
