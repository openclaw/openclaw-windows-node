<#
.SYNOPSIS
    Enforces the stable CI Gate result contract.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ClassificationResult,
    [Parameter(Mandatory)][string]$Classification,
    [Parameter(Mandatory)][string]$FastValidationResult,
    [Parameter(Mandatory)][string]$TestResult,
    [Parameter(Mandatory)][string]$E2eResult,
    [Parameter(Mandatory)][string]$BuildResult
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($ClassificationResult -ne "success") {
    throw "Change classification did not succeed: $ClassificationResult"
}
if ($FastValidationResult -ne "success") {
    throw "Fast validation did not succeed: $FastValidationResult"
}

$heavyResults = [ordered]@{
    test = $TestResult
    e2e = $E2eResult
    build = $BuildResult
}

if ($Classification -eq "docs_only") {
    foreach ($lane in $heavyResults.GetEnumerator()) {
        if ($lane.Value -ne "skipped") {
            throw "Docs-only CI expected $($lane.Key) to be skipped, but it was '$($lane.Value)'."
        }
    }
    "docs_only"
    return
}

if ($Classification -ne "full") {
    throw "Unknown or missing change classification '$Classification'."
}
foreach ($lane in $heavyResults.GetEnumerator()) {
    if ($lane.Value -ne "success") {
        throw "Full CI requires $($lane.Key) to succeed, but it was '$($lane.Value)'."
    }
}
"full"
