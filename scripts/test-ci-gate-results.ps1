<#
.SYNOPSIS
    Exercises stable CI Gate pass, failure, cancellation, and skip contracts.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$gatePath = Join-Path $RepoRoot "scripts\Assert-CiGateResults.ps1"

function Invoke-Gate {
    param(
        [string]$ClassificationResult = "success",
        [string]$Classification = "full",
        [string]$FastValidationResult = "success",
        [string]$TestResult = "success",
        [string]$E2eResult = "success",
        [string]$BuildResult = "success"
    )

    & $gatePath `
        -ClassificationResult $ClassificationResult `
        -Classification $Classification `
        -FastValidationResult $FastValidationResult `
        -TestResult $TestResult `
        -E2eResult $E2eResult `
        -BuildResult $BuildResult
}

function Assert-GateFails {
    param(
        [Parameter(Mandatory)][hashtable]$Arguments,
        [Parameter(Mandatory)][string]$Scenario
    )

    try {
        $result = Invoke-Gate @Arguments
        throw "$Scenario unexpectedly passed with result '$result'."
    } catch {
        if ($_.Exception.Message.StartsWith(
                "$Scenario unexpectedly passed",
                [StringComparison]::Ordinal)) {
            throw
        }
    }
}

$docsOnly = Invoke-Gate `
    -Classification docs_only `
    -TestResult skipped `
    -E2eResult skipped `
    -BuildResult skipped
if ($docsOnly -ne "docs_only") {
    throw "Expected the docs-only gate to pass."
}

$full = Invoke-Gate
if ($full -ne "full") {
    throw "Expected the full gate to pass."
}

Assert-GateFails `
    -Arguments @{ ClassificationResult = "failure" } `
    -Scenario "Failed classification"
Assert-GateFails `
    -Arguments @{ FastValidationResult = "cancelled" } `
    -Scenario "Cancelled fast validation"
Assert-GateFails `
    -Arguments @{
        Classification = "docs_only"
        TestResult = "success"
        E2eResult = "skipped"
        BuildResult = "skipped"
    } `
    -Scenario "Unskipped docs-only test lane"
Assert-GateFails `
    -Arguments @{ Classification = "unexpected" } `
    -Scenario "Unknown classification"

foreach ($lane in @("TestResult", "E2eResult", "BuildResult")) {
    foreach ($result in @("failure", "cancelled", "skipped")) {
        Assert-GateFails `
            -Arguments @{ $lane = $result } `
            -Scenario "Full $lane result '$result'"
    }
}

Write-Host "CI Gate regressions passed: docs-only skips accepted, full successes accepted, and failures/cancellations/unexpected skips rejected." -ForegroundColor Green
