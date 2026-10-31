<#
.SYNOPSIS
    Captures method stacks from the calling testhost after its UI startup timeout.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$TesthostId,
    [Parameter(Mandatory)][long]$ExpectedStartTicks,
    [Parameter(Mandatory)][string]$Phase,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$StackTool,
    [ValidateRange(1, 60)][int]$CaptureTimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$directory = Join-Path $OutputDirectory "$TesthostId-$ExpectedStartTicks"
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$receipt = [ordered]@{
    testhostId = $TesthostId
    expectedStartTicks = $ExpectedStartTicks
    phase = $Phase
    observedUtc = [DateTime]::UtcNow.ToString('o')
    helperId = $PID
    status = 'checking-identity'
    toolExitCode = $null
}
$receiptPath = Join-Path $directory 'capture.json'
function Save-Receipt {
    $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8
}
Save-Receipt
$collector = $null
$caller = $null
$exitCode = 1
try {
    $caller = Get-Process -Id $TesthostId -ErrorAction Stop
    # Keep the process object alive so an exited target's PID cannot be reused.
    $null = $caller.SafeHandle
    $helper = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
    if ($helper.ParentProcessId -ne $TesthostId -or
        $caller.ProcessName -ne 'testhost' -or
        $caller.StartTime.ToUniversalTime().Ticks -ne $ExpectedStartTicks -or $caller.HasExited) {
        $receipt.status = 'identity-rejected'
        throw 'Only the exact calling testhost may be inspected.'
    }

    $start = [Diagnostics.ProcessStartInfo]::new($StackTool)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('report', '--process-id', "$TesthostId")) {
        $start.ArgumentList.Add($argument)
    }
    $collector = [Diagnostics.Process]::Start($start)
    $stdout = $collector.StandardOutput.ReadToEndAsync()
    $stderr = $collector.StandardError.ReadToEndAsync()
    $receipt.status = 'collecting'
    Save-Receipt
    if (-not $collector.WaitForExit($CaptureTimeoutSeconds * 1000)) {
        # Kill only this helper's stack tool, never the testhost or another process.
        $collector.Kill()
        $collector.WaitForExit()
        $receipt.status = 'capture-timeout'
    } else {
        $receipt.toolExitCode = $collector.ExitCode
        $receipt.status = if ($collector.ExitCode -eq 0) { 'captured' } else { 'tool-failed' }
        $exitCode = $collector.ExitCode
    }
    [IO.File]::WriteAllText((Join-Path $directory 'managed-stacks.txt'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $directory 'stack-tool.stderr.txt'), $stderr.GetAwaiter().GetResult())
} catch {
    $exitCode = 1
    if ($receipt.status -notin @('identity-rejected', 'capture-timeout')) {
        $receipt.status = 'capture-error'
    }
    $receipt.errorType = $_.Exception.GetType().Name
    Write-Error "UI startup stack capture failed: $($receipt.status) ($($receipt.errorType))" -ErrorAction Continue
} finally {
    if ($null -ne $collector) {
        if (-not $collector.HasExited) {
            $collector.Kill()
            $collector.WaitForExit()
        }
        $collector.Dispose()
    }
    if ($null -ne $caller) { $caller.Dispose() }
    Save-Receipt
}
exit $exitCode
