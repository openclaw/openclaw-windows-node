<#
.SYNOPSIS
    Runs one existing CI E2E shard and enforces its TRX proof contract.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("setup-connect", "revocation-recovery", "network-recovery")]
    [string]$Name,
    [Parameter(Mandatory)][string]$Filter,
    [ValidateRange(1, 3600)][int]$HangTimeoutSeconds = 600,
    # The calling workflow step's timeout-minutes. Defaults to OPENCLAW_CI_STEP_TIMEOUT_MINUTES; 0 skips the report.
    [ValidateRange(0, 360)][int]$StepTimeoutMinutes = 0
)

$stepBudget = [Diagnostics.Stopwatch]::StartNew()
if (-not $PSBoundParameters.ContainsKey('StepTimeoutMinutes')) {
    # CI passes the budget in the step environment so an older candidate's runner, run by alpha
    # promotion, never receives a parameter it does not declare.
    . (Join-Path $PSScriptRoot 'CiStepBudget.ps1')
    $StepTimeoutMinutes = Get-CiStepTimeoutMinutes
}
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resultsDirectory = "TestResults\E2E"
$trxPath = Join-Path $resultsDirectory "OpenClaw.E2ETests.$Name.trx"
# VSTest's hang timer resets on test events, not fixture progress. A progressing
# WSL install can exceed it before the first case starts. Bound output inactivity
# instead, and end the whole run before the workflow step limit (below).
$testArguments = @(
    'test', 'tests/OpenClaw.E2ETests', '--no-build', '-c', 'Debug', '-r', 'win-x64',
    '--verbosity', 'normal', '--results-directory', $resultsDirectory,
    '--logger', "trx;LogFileName=OpenClaw.E2ETests.$Name.trx",
    '--logger', 'console;verbosity=detailed', '--blame', '--filter', $Filter
)
# A step that reaches its timeout-minutes cannot report, and if it does not end, the job limit cancels
# it before the always() upload runs. With a step budget, VSTest aborts the session (keeping the TRX
# and console output) at the budget minus a reserve. The runner's wall deadline, at least 60 s
# before the step limit, then lists recent processes and ends the run if the abort did not.
# OPENCLAW_CI_E2E_STOP_RESERVE_SECONDS: seconds of the step budget kept for that (default 180,
# 120 to 1800). Older candidates ignore it and keep relying on the step timeout alone.
# Neither deadline helps when the hosted runner's worker itself freezes during WSL work
# (actions/runner-images#14938): then no step timeout fires and the job keeps no log.
$wallTimeoutSeconds = 0
$consoleLogPath = Join-Path $resultsDirectory "OpenClaw.E2ETests.$Name.console.log"
# Records which WSL distros were still running when a deadline ended the run. Bounded, because a
# wedged WSL service is one of the states this captures; diagnostics never change the result.
function Save-WslStateAtDeadline {
    $previousUtf8 = $env:WSL_UTF8
    try {
        $wslState = Join-Path $resultsDirectory "wsl-state-at-deadline.$Name.txt"
        $env:WSL_UTF8 = '1'
        $wsl = Start-Process -FilePath 'wsl.exe' -ArgumentList '--list', '--verbose' -NoNewWindow -PassThru `
            -RedirectStandardOutput $wslState -RedirectStandardError "$wslState.stderr"
        if (-not $wsl.WaitForExit(15000)) {
            $wsl.Kill($true)
            Add-Content -LiteralPath $wslState -Value 'wsl.exe --list --verbose did not finish within 15 seconds.'
        }
    } catch {
        Write-Warning "WSL state capture failed: $($_.Exception.Message)"
    } finally {
        $env:WSL_UTF8 = $previousUtf8
    }
}
if ($StepTimeoutMinutes -gt 0) {
    $reserveSeconds = 180
    $configuredReserve = [Environment]::GetEnvironmentVariable('OPENCLAW_CI_E2E_STOP_RESERVE_SECONDS')
    if (-not [string]::IsNullOrWhiteSpace($configuredReserve)) {
        $parsedReserve = 0
        if ([int]::TryParse($configuredReserve.Trim(), [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$parsedReserve) -and
            $parsedReserve -ge 120 -and $parsedReserve -le 1800) {
            $reserveSeconds = $parsedReserve
        } else {
            Write-Warning 'OPENCLAW_CI_E2E_STOP_RESERVE_SECONDS must be a whole number of seconds from 120 to 1800; using 180.'
        }
    }
    $availableSeconds = [int]($StepTimeoutMinutes * 60 - $stepBudget.Elapsed.TotalSeconds)
    $sessionSeconds = $availableSeconds - $reserveSeconds
    if ($sessionSeconds -ge 60) {
        # Bounded cleanup after the wall deadline (snapshot, kill 5 s, readers 1 s, WSL state 15 s)
        # needs well under a minute.
        $wallTimeoutSeconds = $availableSeconds - [Math]::Max(60, [int][Math]::Ceiling($reserveSeconds / 2))
        $testArguments += @('--', "RunConfiguration.TestSessionTimeout=$($sessionSeconds * 1000)")
        Write-Host "E2E shard '$Name' run deadline: VSTest aborts after $sessionSeconds s; the runner stops after $wallTimeoutSeconds s ($StepTimeoutMinutes-minute step, $reserveSeconds s reserve)."
    } else {
        Write-Warning "E2E shard '$Name' step budget leaves under 60 s after its $reserveSeconds s reserve; relying on the step timeout alone."
    }
}
try {
    & (Join-Path $PSScriptRoot 'Invoke-CiProcess.ps1') -FilePath 'dotnet' `
        -ArgumentList $testArguments -IdleTimeoutSeconds $HangTimeoutSeconds `
        -WallTimeoutSeconds $wallTimeoutSeconds `
        -OutputPath $consoleLogPath
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0 -and (Test-Path -LiteralPath $consoleLogPath) -and
        (Select-String -LiteralPath $consoleLogPath -SimpleMatch 'Aborting test run: test run timeout' -Quiet)) {
        Save-WslStateAtDeadline
    }
} catch {
    Save-WslStateAtDeadline
    throw
} finally {
    # Also report when the idle deadline throws.
    if ($StepTimeoutMinutes -gt 0) {
        . (Join-Path $PSScriptRoot 'CiStepBudget.ps1')
        Write-CiStepBudget -Elapsed $stepBudget.Elapsed -StepTimeoutMinutes $StepTimeoutMinutes -Label "E2E shard '$Name'"
    }
}
if ($testExitCode -ne 0) {
    exit $testExitCode
}

[xml]$trx = Get-Content $trxPath
$executed = [int]$trx.TestRun.ResultSummary.Counters.executed
if ($executed -lt 1) {
    throw "E2E shard '$Name' executed zero tests. Check OPENCLAW_RUN_E2E gating/filter before merging."
}

if ($Name -ne "setup-connect") {
    return
}

$mxcProofNames = @(
    "RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox",
    "RealGateway_SystemRun_BlocksWritesToTrayDataDirectoryInMxcSandbox"
)
foreach ($mxcProofName in $mxcProofNames) {
    $mxcProof = @(
        $trx.TestRun.Results.UnitTestResult |
            Where-Object { $_.testName -like "*$mxcProofName*" }
    ) | Select-Object -First 1
    if ($null -eq $mxcProof) {
        throw "E2E shard '$Name' did not report the MXC proof test '$mxcProofName'. Check the setup-connect filter before merging."
    }

    $mxcOutcome = [string]$mxcProof.outcome
    if ($mxcOutcome -eq "Passed") {
        Write-Host "MXC E2E proof passed: $mxcProofName"
    } elseif ($mxcOutcome -eq "NotExecuted" -or $mxcOutcome -eq "Skipped") {
        $mxcSkipReason = @(
            $mxcProof.SelectSingleNode("Output/ErrorInfo/Message")
            $mxcProof.SelectSingleNode("Output/StdOut")
        ) |
            Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace($_.InnerText) } |
            ForEach-Object { $_.InnerText.Trim() } |
            Select-Object -First 1
        if ([string]::IsNullOrWhiteSpace($mxcSkipReason)) {
            $mxcSkipReason = "skip reason was not present in the trx output"
        }
        Write-Warning "MXC E2E proof skipped: $mxcProofName; $mxcSkipReason"
    } else {
        throw "MXC E2E proof '$mxcProofName' had unexpected outcome '$mxcOutcome'."
    }
}

$sshOwnershipProofNames = @(
    "UnownedListenerIsRejectedThenOwnedTunnelRecoversWithoutRepairing",
    "InitialHandshakeListenerReplacementWithholdsCredentialFrame",
    "InitialNodeHandshakeListenerReplacementWithholdsCredentialFrame"
)
foreach ($sshOwnershipProofName in $sshOwnershipProofNames) {
    $sshOwnershipProof = @(
        $trx.TestRun.Results.UnitTestResult |
            Where-Object { $_.testName -like "*$sshOwnershipProofName*" }
    ) | Select-Object -First 1
    if ($null -eq $sshOwnershipProof) {
        throw "E2E shard '$Name' did not report the SSH ownership proof test '$sshOwnershipProofName'. Check the setup-connect filter before merging."
    }

    $sshOwnershipOutcome = [string]$sshOwnershipProof.outcome
    if ($sshOwnershipOutcome -ne "Passed") {
        throw "SSH ownership E2E proof '$sshOwnershipProofName' had outcome '$sshOwnershipOutcome'; this proof must pass and may not skip."
    }
    Write-Host "SSH ownership E2E proof passed: $sshOwnershipProofName"
}
