<#
.SYNOPSIS
    Reports how much of its workflow step timeout a CI test runner used.
.DESCRIPTION
    GitHub does not expose a step's timeout-minutes to the step, so the workflow passes the same
    value as -StepTimeoutMinutes; scripts\test-ci-workflow-contract.ps1 keeps them equal. At or
    above 80% of the budget the report is a GitHub warning annotation, so a passing step that is
    approaching its timeout is visible before it starts failing.
.EXAMPLE
    . (Join-Path $PSScriptRoot 'CiStepBudget.ps1')
    $budget = [Diagnostics.Stopwatch]::StartNew()
    # ... run tests ...
    Write-CiStepBudget -Elapsed $budget.Elapsed -StepTimeoutMinutes 25 -Label 'OpenClaw.Tray.UITests.trx'
#>

Set-StrictMode -Version Latest

$script:CiStepBudgetWarningPercent = 80

function Get-CiStepBudgetReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][TimeSpan]$Elapsed,
        [Parameter(Mandatory)][ValidateRange(1, 360)][int]$StepTimeoutMinutes,
        [Parameter(Mandatory)][string]$Label
    )

    $percent = [int][Math]::Floor(100 * $Elapsed.TotalMinutes / $StepTimeoutMinutes)
    $summary = [string]::Format([Globalization.CultureInfo]::InvariantCulture,
        '{0} used {1:0.0} of {2} minutes ({3}%) of its step timeout.',
        $Label, $Elapsed.TotalMinutes, $StepTimeoutMinutes, $percent)
    if ($percent -lt $script:CiStepBudgetWarningPercent) {
        return "CI step budget: $summary"
    }
    $message = "$summary Raise timeout-minutes with headroom or split the suite before passing runs time out."
    # Workflow command data must escape these characters.
    $escaped = $message.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    return "::warning title=CI step budget::$escaped"
}

function Write-CiStepBudget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][TimeSpan]$Elapsed,
        [Parameter(Mandatory)][ValidateRange(1, 360)][int]$StepTimeoutMinutes,
        [Parameter(Mandatory)][string]$Label
    )

    # Diagnostics only: a reporting failure must never change the runner's result.
    try { Write-Host (Get-CiStepBudgetReport -Elapsed $Elapsed -StepTimeoutMinutes $StepTimeoutMinutes -Label $Label) }
    catch { Write-Host "CI step budget: report unavailable ($($_.Exception.GetType().Name))." }
}
