<#
.SYNOPSIS
Run fixture profile checks and real-app UI smokes against an explicitly selected build.
.DESCRIPTION
Requires a Windows desktop and a built app with fixture isolation support. Missing
desktop/runtime support and skipped/zero-test runs are failures, not successful proof.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AppPath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$ArtifactsDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'openclaw-gateway-fixture-artifacts'),
    [switch]$Screenshots
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedApp = (Resolve-Path -LiteralPath $AppPath).Path
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$resultsDirectory = Join-Path $artifacts ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$arch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($arch -notin @('X64', 'Arm64')) { throw "Unsupported architecture: $arch" }
$rid = if ($arch -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
$platform = if ($arch -eq 'Arm64') { 'ARM64' } else { 'x64' }

function Invoke-FixtureTests {
    param([string]$Project, [string]$Filter, [string]$ResultName)
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $repoRoot
    foreach ($argument in @('test', (Join-Path $repoRoot $Project), '-c', $Configuration,
        '-r', $rid, "-p:Platform=$platform", '-p:DevBuild=false', '--filter', $Filter,
        '--results-directory', $resultsDirectory, '--logger', "trx;LogFileName=$ResultName.trx")) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['OPENCLAW_RUN_GATEWAY_FIXTURE_UI'] = '1'
    $start.Environment['OPENCLAW_GATEWAY_FIXTURE_APP'] = $resolvedApp
    $start.Environment['OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS'] = $artifacts
    $start.Environment['OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS'] = if ($Screenshots) { '1' } else { '0' }
    $start.Environment['OPENCLAW_REPO_ROOT'] = $repoRoot
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(900000)) {
            $process.Kill($true)
            throw "$ResultName exceeded 15 minutes. Stopped only the owned test process tree."
        }
        if ($process.ExitCode -ne 0) { throw "$ResultName failed (exit $($process.ExitCode)). Results: $resultsDirectory" }
    } finally {
        $process.Dispose()
    }
    $resultFile = Join-Path $resultsDirectory "$ResultName.trx"
    if (-not (Test-Path -LiteralPath $resultFile)) { throw "$ResultName produced no test report." }
    [xml]$trx = Get-Content -LiteralPath $resultFile -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.total -le 0 -or [int]$counters.passed -ne [int]$counters.total) {
        throw "$ResultName did not execute and pass every selected test. Skips are not fixture proof."
    }
}

Invoke-FixtureTests 'tests\OpenClaw.Tray.IntegrationTests\OpenClaw.Tray.IntegrationTests.csproj' 'FullyQualifiedName~GatewayFixtureProfileTests|FullyQualifiedName~GatewayFixtureRunTests|FullyQualifiedName~GatewayFixtureAppTests' 'fixture-profile-app'
Invoke-FixtureTests 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' 'FullyQualifiedName~GatewayFixtureUiTests' 'fixture-ui'
Write-Host "Fixture smoke passed. Results: $resultsDirectory"
exit 0
