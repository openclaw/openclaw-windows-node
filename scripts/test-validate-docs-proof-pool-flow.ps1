<#
.SYNOPSIS
    Proves the documentation gate returns from every proof-pool validation step.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot,
    [switch]$SkipPowerShell7
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
$validateDocsPath = Join-Path $repoRootPath "scripts\validate-docs.ps1"
$childPowerShell = if ($SkipPowerShell7) {
    $null
} else {
    Get-Command pwsh.exe -ErrorAction SilentlyContinue
}
if ($null -eq $childPowerShell) {
    $childPowerShell = Get-Command powershell.exe -ErrorAction Stop
    Write-Warning "PowerShell 7 is unavailable; running the documentation flow probe with Windows PowerShell 5.1."
}

$previousErrorActionPreference = $ErrorActionPreference
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $ErrorActionPreference = "Continue"
    $output = (& $childPowerShell.Source `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $validateDocsPath `
        -RepoRoot $repoRootPath `
        -SkipProofPoolFlowRegression 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
} finally {
    $stopwatch.Stop()
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
if ($output -match "Proof-pool validator regressions passed:") {
    throw "Nested documentation flow duplicated the malformed-contract matrix."
}
if ($output -notmatch "Documentation validation passed:") {
    throw "Documentation flow did not reach the final Markdown summary."
}
Write-Host "Documentation flow regression passed in $([math]::Round($stopwatch.Elapsed.TotalSeconds, 2))s: both schema paths and final summary reached without duplicating the validator matrix." -ForegroundColor Green
