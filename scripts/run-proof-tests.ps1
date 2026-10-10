<#
.SYNOPSIS
    Runs a filtered proof test command and rejects zero-test success.

.DESCRIPTION
    Restores and builds through dotnet test, writes a deterministic TRX file,
    and requires at least one reported test so fresh proof hosts cannot turn
    --no-restore behavior or a stale filter into success-shaped evidence.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Project,

    [Parameter(Mandatory = $true)]
    [string]$Filter,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    [string]$ResultName,

    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier,

    [string]$ResultsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $scriptRoot))
$env:OPENCLAW_REPO_ROOT = $repoRoot
$projectPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Project))
$repoPrefix = $repoRoot.TrimEnd("\") + "\"
if (-not $projectPath.StartsWith(
        $repoPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Proof test project escapes the repository: $Project"
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Proof test project does not exist: $Project"
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repoRoot "TestResults\ProofPools\$ResultName"
} else {
    $ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
}

New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$trxPath = Join-Path $ResultsDirectory "$ResultName.trx"
Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue

$arguments = @(
    "test",
    $projectPath,
    "--filter", $Filter,
    "--results-directory", $ResultsDirectory,
    "--logger", "trx;LogFileName=$ResultName.trx",
    "--logger", "console;verbosity=normal"
)
if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $arguments += @("-r", $RuntimeIdentifier)
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Proof test command failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
    throw "Proof test command did not create its expected TRX: $trxPath"
}

[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
if ($results.Count -eq 0) {
    throw "Proof test command reported zero tests for filter: $Filter"
}
$passedResults = @($results | Where-Object {
    $_.GetAttribute("outcome") -ceq "Passed"
})
if ($passedResults.Count -eq 0) {
    throw "Proof test command reported $($results.Count) tests but none passed for filter: $Filter"
}

$outcomes = @($results | Group-Object { $_.GetAttribute("outcome") })
$summary = $outcomes | ForEach-Object { "$($_.Name)=$($_.Count)" }
Write-Host "Proof tests reported: total=$($results.Count); $($summary -join '; ')" -ForegroundColor Green
Write-Host "Proof TRX: $trxPath"
