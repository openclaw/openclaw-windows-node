<#
.SYNOPSIS
    Reports how much of its workflow step timeout a CI test runner used.
.DESCRIPTION
    GitHub does not expose a step's timeout-minutes to the step, so the workflow repeats the value
    in the step's OPENCLAW_CI_STEP_TIMEOUT_MINUTES environment variable;
    scripts\test-ci-workflow-contract.ps1 keeps them equal. It is an environment variable, not a
    runner parameter, because alpha promotion runs the current workflow against an older
    candidate's runner scripts, which ignore unknown variables but reject unknown parameters.
    At or above 80% of the budget the report is a GitHub warning annotation, so a passing step
    that is approaching its timeout is visible before it starts failing.
    Dot-sourcing this file does not change the caller's strict mode.
.EXAMPLE
    . (Join-Path $PSScriptRoot 'CiStepBudget.ps1')
    $budgetMinutes = Get-CiStepTimeoutMinutes  # 0 when the step declares no budget
    $budget = [Diagnostics.Stopwatch]::StartNew()
    # ... run tests ...
    if ($budgetMinutes -gt 0) { Write-CiStepBudget -Elapsed $budget.Elapsed -StepTimeoutMinutes $budgetMinutes -Label 'OpenClaw.Tray.UITests.trx' }
#>

$script:CiStepBudgetWarningPercent = 80
$script:CiStepTimeoutVariable = 'OPENCLAW_CI_STEP_TIMEOUT_MINUTES'

# The calling step's timeout-minutes from OPENCLAW_CI_STEP_TIMEOUT_MINUTES, or 0 when absent.
# A malformed value only warns: the budget report is diagnostics and must not fail a test run.
function Get-CiStepTimeoutMinutes {
    $configured = [Environment]::GetEnvironmentVariable($script:CiStepTimeoutVariable)
    if ([string]::IsNullOrWhiteSpace($configured)) { return 0 }
    $minutes = 0
    if ([int]::TryParse($configured.Trim(), [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$minutes) -and
        $minutes -ge 1 -and $minutes -le 360) { return $minutes }
    Write-Warning "$($script:CiStepTimeoutVariable) must be a whole number of minutes from 1 to 360; skipping the step budget report."
    return 0
}

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
