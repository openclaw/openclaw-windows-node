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
    [ValidateRange(0, 3600)][int]$HangTimeoutSeconds = 0,
    [switch]$CaptureHostExit
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
if ($CaptureHostExit) {
    . (Join-Path $PSScriptRoot "CiTestHostExit.ps1")
    if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP) -or -not [IO.Path]::IsPathFullyQualified($env:RUNNER_TEMP)) {
        throw 'Host-exit diagnostics require an absolute RUNNER_TEMP outside the checkout.'
    }
    $tempRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([char[]]"\/") + [IO.Path]::DirectorySeparatorChar
    $checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd([char[]]"\/") + [IO.Path]::DirectorySeparatorChar
    if ($tempRoot.StartsWith($checkout, [StringComparison]::OrdinalIgnoreCase)) { throw 'Raw diagnostics must stay outside the checkout and its artifacts.' }
    $rawRoot = Join-Path $tempRoot ("ci-testhost-exit-" + $invocation.id)
    $testArguments.Add('--diag')
    $testArguments.Add((Join-Path $rawRoot 'testplatform.log') + ';tracelevel=info')
    New-Item -ItemType Directory -Path $rawRoot -ErrorAction Stop | Out-Null
    $testExit = $null; $nativeErrorType = $null
    try {
        Invoke-CiNativeWithExit -ExitCode ([ref]$testExit) -Command {
            if ($collectCoverage) {
                $coverageOutput = Join-Path $ResultsDirectory "coverage.cobertura.xml"
                & dotnet-coverage collect --output $coverageOutput --output-format cobertura dotnet @testArguments
            } else {
                & dotnet @testArguments
            }
        }
    }
    catch { $nativeErrorType = $_.Exception.GetType().FullName }
    finally {
        $diagnostic = Complete-CiHostExitTrace -Root $rawRoot -NativeExitCode $testExit -NativeErrorType $nativeErrorType
        Write-Host ('CI host exit metadata: ' + ($diagnostic | ConvertTo-Json -Depth 5 -Compress))
    }
    if ($null -ne $testExit -and $testExit -ne 0) { exit $testExit }
    if ($null -eq $testExit -or $nativeErrorType) { throw 'Test command failed without a successful native exit.' }
    if ($diagnostic.diagnosticErrorType -or -not $diagnostic.rawTraceRemoved) { throw 'Host-exit diagnostic extraction or cleanup failed.' }
} elseif ($collectCoverage) {
    $coverageOutput = Join-Path $ResultsDirectory "coverage.cobertura.xml"
    & dotnet-coverage collect `
        --output $coverageOutput `
        --output-format cobertura `
        dotnet @testArguments
} else {
    & dotnet @testArguments
}

if (-not $CaptureHostExit -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
$result = Read-TestResultContract -Path $invocation.report -StartedUtc $invocation.startedUtc -ExitCode 0 -AllowSkipped
Write-Host ("CI test results: " + ($result | ConvertTo-Json -Compress))
Write-Host "CI TRX: $($invocation.report)"
# Ordinary CI retains environment-gated skips. This is not native/custom proof.
$global:LASTEXITCODE = 0
