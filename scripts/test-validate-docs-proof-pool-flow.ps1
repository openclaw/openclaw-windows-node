<#
.SYNOPSIS
    Proves the documentation gate returns from every proof-pool validation step.
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
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
$validateDocsPath = Join-Path $repoRootPath "scripts\validate-docs.ps1"
$pwshPath = (Get-Command pwsh.exe -ErrorAction Stop).Source

$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $output = (& $pwshPath `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $validateDocsPath `
        -RepoRoot $repoRootPath `
        -SkipProofPoolFlowRegression 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($exitCode -ne 0) {
    throw "Documentation flow regression child failed with exit code ${exitCode}: $output"
}

$proofValidationCount = [regex]::Matches(
    $output,
    "Proof-pool validation passed:").Count
if ($proofValidationCount -ne 2) {
    throw "Documentation flow ran $proofValidationCount proof schema paths instead of 2."
}
if ($output -notmatch "Proof-pool validator regressions passed:") {
    throw "Documentation flow did not reach proof-pool validator regressions."
}
if ($output -notmatch "Documentation validation passed:") {
    throw "Documentation flow did not reach the final Markdown summary."
}

Write-Host "Documentation flow regression passed: both schema paths and final summary reached." -ForegroundColor Green
