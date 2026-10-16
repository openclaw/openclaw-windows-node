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
    [switch]$Screenshots,
    [ValidateSet('Smoke', 'Journeys')]
    [string]$Suite = 'Smoke',
    [string]$LvtPath,
    [string]$TestAssemblyPath
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
if ($Suite -eq 'Journeys') {
    $driver = & (Join-Path $PSScriptRoot 'Get-ContractLvtRelease.ps1') -Architecture $arch
    if ([string]::IsNullOrWhiteSpace($LvtPath)) { throw '-Suite Journeys requires an explicit -LvtPath.' }
    $LvtPath = (Resolve-Path -LiteralPath $LvtPath).Path
    if ((Get-FileHash -LiteralPath $LvtPath -Algorithm SHA256).Hash -ne
        $driver.ExecutableSha256) {
        throw "lvt.exe does not match the pinned v$($driver.Version) $arch release."
    }
    if ([string]::IsNullOrWhiteSpace($TestAssemblyPath)) {
        [xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj') -Raw
        $framework = ($project.Project.PropertyGroup | Where-Object { $_.TargetFramework } | Select-Object -First 1).TargetFramework
        if ([string]::IsNullOrWhiteSpace($framework)) { throw 'Cannot determine the UI test target framework.' }
        $TestAssemblyPath = Join-Path $repoRoot "tests\OpenClaw.Tray.UITests\bin\$platform\$Configuration\$framework\$rid\OpenClaw.Tray.UITests.dll"
    }
    $TestAssemblyPath = (Resolve-Path -LiteralPath $TestAssemblyPath).Path
}

function Invoke-FixtureTests {
    param([string]$Project, [string]$Filter, [string]$ResultName)
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $repoRoot
    $arguments = if ($Suite -eq 'Journeys') { @('test', $TestAssemblyPath) }
        else { @('test', (Join-Path $repoRoot $Project), '-c', $Configuration,
            '-r', $rid, "-p:Platform=$platform", '-p:DevBuild=false') }
    $arguments += @('--filter', $Filter, '--results-directory', $resultsDirectory,
        '--logger', "trx;LogFileName=$ResultName.trx")
    foreach ($argument in $arguments) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['OPENCLAW_RUN_GATEWAY_FIXTURE_UI'] = '1'
    $start.Environment['OPENCLAW_GATEWAY_FIXTURE_APP'] = $resolvedApp
    $start.Environment['OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS'] = $artifacts
    $start.Environment['OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS'] = if ($Screenshots) { '1' } else { '0' }
    $start.Environment['OPENCLAW_REPO_ROOT'] = $repoRoot
    if ($Suite -eq 'Journeys') {
        $start.Environment['OPENCLAW_RUN_CONTRACT_JOURNEYS'] = '1'
        $start.Environment['OPENCLAW_LVT_PATH'] = $LvtPath
        $start.Environment['OPENCLAW_CONTRACT_REPORTS_DIR'] = $resultsDirectory
    } else {
        $start.Environment.Remove('OPENCLAW_RUN_CONTRACT_JOURNEYS')
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(900000)) {
            $process.Kill($true)
            throw "$ResultName exceeded 15 minutes. Stopped only the owned test process tree."
        }
        $exitCode = $process.ExitCode
    } finally {
        $process.Dispose()
    }
    $resultFile = Join-Path $resultsDirectory "$ResultName.trx"
    if ($exitCode -ne 0) { throw "$ResultName failed (exit $exitCode). Results: $resultsDirectory" }
    if (-not (Test-Path -LiteralPath $resultFile)) { throw "$ResultName produced no test report." }
    [xml]$trx = Get-Content -LiteralPath $resultFile -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    if ([int]$counters.total -le 0 -or [int]$counters.passed -ne [int]$counters.total) {
        throw "$ResultName did not execute and pass every selected test. Skips are not fixture proof."
    }
}

if ($Suite -eq 'Journeys') {
    $snapshot = Join-Path $PSScriptRoot 'Get-ContractProofSnapshot.ps1'
    $beforePath = Join-Path $resultsDirectory 'journey-build.json'
    $afterPath = Join-Path $resultsDirectory 'journey-build-after.json'
    $runnerFailure = $null
    try {
        & $snapshot -AppPath $resolvedApp -TestAssemblyPath $TestAssemblyPath -LvtPath $LvtPath |
            ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $beforePath -Encoding utf8
        Invoke-FixtureTests 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' 'Category=ContractPreflight' 'contract-preflight'
        Invoke-FixtureTests 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' 'Category=ContractJourney' 'contract-journeys'
    } catch {
        $runnerFailure = $_.Exception.Message
    } finally {
        try {
            & $snapshot -AppPath $resolvedApp -TestAssemblyPath $TestAssemblyPath -LvtPath $LvtPath |
                ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $afterPath -Encoding utf8
        } catch {
            $runnerFailure = "$runnerFailure After-run identity failed: $($_.Exception.Message)"
        }
        & (Join-Path $PSScriptRoot 'Complete-ContractJourneyRun.ps1') -ReportsDirectory $resultsDirectory `
            -ResultPath (Join-Path $resultsDirectory 'contract-journeys.trx') `
            -BeforeIdentityPath $beforePath -AfterIdentityPath $afterPath -ArtifactsDirectory $artifacts `
            -RunnerFailure $runnerFailure
    }
} else {
    Invoke-FixtureTests 'tests\OpenClaw.Tray.IntegrationTests\OpenClaw.Tray.IntegrationTests.csproj' 'FullyQualifiedName~GatewayFixtureProfileTests|FullyQualifiedName~GatewayFixtureRunTests|FullyQualifiedName~GatewayFixtureAppTests' 'fixture-profile-app'
    Invoke-FixtureTests 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' 'FullyQualifiedName~GatewayFixtureUiTests' 'fixture-ui'
}
Write-Host "Fixture smoke passed. Results: $resultsDirectory"
exit 0
