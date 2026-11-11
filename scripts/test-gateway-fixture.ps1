<#
.SYNOPSIS
Run fixture profile checks and real-app UI smokes against an explicitly selected build.
.DESCRIPTION
Requires a Windows desktop and a built app with fixture isolation support. Missing
desktop/runtime support and skipped/zero-test runs are failures, not successful proof.
StableAppPath selects the released-profile compatibility case instead of the ordinary
application smoke. Both executables must advertise fixture isolation support.
NativeInputProof explicitly authorizes native wheel input in the ordinary fixture UI
child on an exclusive interactive desktop. It does not select the separate keyboard lane.
.PARAMETER NoBuild
Use test projects already built for the selected configuration and native runtime.
Both the IntegrationTests and UITests projects must have matching canonical outputs.
.PARAMETER TestTimeoutSeconds
Finite deadline for each test-project invocation, from 1 to 1800 seconds.
Defaults to 900 seconds. A timeout still terminates only the owned process tree.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AppPath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$ArtifactsDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'openclaw-gateway-fixture-artifacts'),
    [string]$StableAppPath,
    [switch]$NoBuild,
    [switch]$Screenshots,
    [switch]$NativeInputProof,
    [switch]$KeyboardProof,
    [ValidateRange(1, 1800)]
    [int]$TestTimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedApp = (Resolve-Path -LiteralPath $AppPath).Path
$resolvedStableApp = if ($StableAppPath) { (Resolve-Path -LiteralPath $StableAppPath).Path } else { $null }
$artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory)
$resultsDirectory = Join-Path $artifacts ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$arch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($arch -notin @('X64', 'Arm64')) { throw "Unsupported architecture: $arch" }
$rid = if ($arch -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }

function New-FixtureTestStartInfo {
    param([string]$Project, [string]$Filter, [string]$ResultName, [switch]$EnableKeyboardProof)
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $repoRoot
    $testArguments = @('test', (Join-Path $repoRoot $Project), '-c', $Configuration,
        '-r', $rid, '-p:DevBuild=false', '--filter', $Filter,
        '--results-directory', $resultsDirectory, '--logger', "trx;LogFileName=$ResultName.trx")
    if ($NoBuild) { $testArguments += @('--no-build', '--no-restore') }
    foreach ($argument in $testArguments) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['OPENCLAW_RUN_GATEWAY_FIXTURE_UI'] = '1'
    # Native input requires this child's explicit runner option, never an inherited opt-in.
    $start.Environment['OPENCLAW_RUN_KEYBOARD_PROOF'] = if ($EnableKeyboardProof) { '1' } else { '0' }
    $start.Environment['OPENCLAW_GATEWAY_FIXTURE_APP'] = $resolvedApp
    $start.Environment.Remove('OPENCLAW_GATEWAY_FIXTURE_STABLE_APP') | Out-Null
    if ($resolvedStableApp) {
        $start.Environment['OPENCLAW_GATEWAY_FIXTURE_STABLE_APP'] = $resolvedStableApp
    }
    $start.Environment['OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS'] = $artifacts
    $start.Environment['OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS'] = if ($Screenshots) { '1' } else { '0' }
    $start.Environment['OPENCLAW_REPO_ROOT'] = $repoRoot
    return $start
}

function Invoke-FixtureTests {
    param([string]$Project, [string]$Filter, [string]$ResultName, [switch]$EnableKeyboardProof)
    $start = New-FixtureTestStartInfo -Project $Project -Filter $Filter -ResultName $ResultName `
        -EnableKeyboardProof:$EnableKeyboardProof
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit($TestTimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw "$ResultName exceeded $TestTimeoutSeconds seconds. Stopped only the owned test process tree."
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

if ($resolvedStableApp) {
    Invoke-FixtureTests 'tests\OpenClaw.Tray.IntegrationTests\OpenClaw.Tray.IntegrationTests.csproj' 'FullyQualifiedName~GatewayFixtureUpgradeTests' 'fixture-released-profile'
} else {
    Invoke-FixtureTests 'tests\OpenClaw.Tray.IntegrationTests\OpenClaw.Tray.IntegrationTests.csproj' 'FullyQualifiedName~GatewayFixtureProfileTests|FullyQualifiedName~GatewayFixtureRunTests|FullyQualifiedName~GatewayFixtureAppTests' 'fixture-profile-app'
    Invoke-FixtureTests 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' 'Category=GatewayFixture&KeyboardProof!=Required' 'fixture-ui' -EnableKeyboardProof:$NativeInputProof
    if ($KeyboardProof) {
        Invoke-FixtureTests 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' 'Category=GatewayFixture&KeyboardProof=Required' 'fixture-keyboard' -EnableKeyboardProof
    }
}
Write-Host "Fixture checks passed. Results: $resultsDirectory"
exit 0
