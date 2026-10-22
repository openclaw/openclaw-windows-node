<#
.SYNOPSIS
    Runs a CI test project with optional push/tag coverage collection.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [Parameter(Mandatory)][string]$TrxFileName,
    [string]$Runtime,
    [string]$Filter,
    [ValidateRange(0, 3600)][int]$HangTimeoutSeconds = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "TestResultContract.ps1")
$invocation = New-TestInvocation -ResultsDirectory $ResultsDirectory -TrxFileName $TrxFileName
$ResultsDirectory = $invocation.directory

$testArguments = [System.Collections.Generic.List[string]]::new()
@(
    "test",
    $Project,
    "--no-build",
    "-c",
    "Debug"
) | ForEach-Object { $testArguments.Add($_) }

if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
    $testArguments.Add("-r")
    $testArguments.Add($Runtime)
}

@(
    "--verbosity",
    "normal",
    "--results-directory",
    $ResultsDirectory,
    "--logger",
    "trx;LogFileName=$TrxFileName"
) | ForEach-Object { $testArguments.Add($_) }

if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $testArguments.Add("--filter")
    $testArguments.Add($Filter)
}

if ($HangTimeoutSeconds -gt 0) {
    # Keep the interrupted test sequence, not a potentially secret-bearing memory dump.
    @(
        "--blame-hang",
        "--blame-hang-timeout",
        "${HangTimeoutSeconds}s",
        "--blame-hang-dump-type",
        "none"
    ) | ForEach-Object { $testArguments.Add($_) }
}

$collectCoverage = $env:OPENCLAW_CI_COLLECT_COVERAGE -eq "true"
if ($collectCoverage) {
    $coverageOutput = Join-Path $ResultsDirectory "coverage.cobertura.xml"
    & dotnet-coverage collect `
        --output $coverageOutput `
        --output-format cobertura `
        dotnet @testArguments
} else {
    & dotnet @testArguments
}

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
$result = Read-TestResultContract -Path $invocation.report -StartedUtc $invocation.startedUtc -ExitCode 0 -AllowSkipped
Write-Host ("CI test results: " + ($result | ConvertTo-Json -Compress))
Write-Host "CI TRX: $($invocation.report)"
# Ordinary CI retains environment-gated skips. This is not native/custom proof.
$global:LASTEXITCODE = 0
