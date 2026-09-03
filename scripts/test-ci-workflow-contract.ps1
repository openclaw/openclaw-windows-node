<#
.SYNOPSIS
    Validates CI quick-win workflow and proof-regression routing contracts.
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
$workflowPath = Join-Path $repoRootPath ".github\workflows\ci.yml"
$selectorPath = Join-Path $repoRootPath "scripts\Get-CiProofPoolRegressionDecision.ps1"
$runnerPath = Join-Path $repoRootPath "scripts\Invoke-CiTest.ps1"
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$runner = Get-Content -LiteralPath $runnerPath -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Text.Contains($Expected, [StringComparison]::Ordinal)) {
        throw $Message
    }
}

Assert-Contains `
    -Text $workflow `
    -Expected "group: `${{ github.workflow }}-`${{ github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || format('{0}-{1}', github.ref, github.sha) }}" `
    -Message "CI concurrency must group PR runs by PR number and push/tag runs by ref and SHA."
Assert-Contains `
    -Text $workflow `
    -Expected "cancel-in-progress: `${{ github.event_name == 'pull_request' }}" `
    -Message "CI cancellation must apply only to pull_request runs."
Assert-Contains `
    -Text $workflow `
    -Expected "steps.proof_pool_regression.outcome != 'success' || steps.proof_pool_regression.outputs.run != 'false'" `
    -Message "Proof-pool regression routing must run when diff selection fails or does not explicitly return false."
Assert-Contains `
    -Text $workflow `
    -Expected "if: `${{ github.event_name != 'pull_request' }}" `
    -Message "dotnet-coverage installation must be limited to push and tag runs."
Assert-Contains `
    -Text $workflow `
    -Expected "OPENCLAW_CI_COLLECT_COVERAGE: `${{ github.event_name != 'pull_request' }}" `
    -Message "Test coverage must be disabled only for pull_request runs."

$runnerUses = [regex]::Matches(
    $workflow,
    "(?m)^\s+\./scripts/Invoke-CiTest\.ps1\s*$").Count
if ($runnerUses -ne 8) {
    throw "Expected 8 CI test steps to use Invoke-CiTest.ps1, found $runnerUses."
}
if ($workflow -match "(?m)^\s+dotnet-coverage collect\s*$") {
    throw "Workflow test steps must not invoke dotnet-coverage directly."
}

foreach ($requiredRunnerToken in @(
    '"--logger"',
    '"trx;LogFileName=$TrxFileName"',
    'dotnet-coverage collect',
    '--output-format cobertura',
    '& dotnet @testArguments'
)) {
    Assert-Contains `
        -Text $runner `
        -Expected $requiredRunnerToken `
        -Message "CI test runner is missing required token '$requiredRunnerToken'."
}

$buildJobStart = $workflow.IndexOf("  build:", [StringComparison]::Ordinal)
$buildMsixStart = $workflow.IndexOf("  build-msix:", [StringComparison]::Ordinal)
if ($buildJobStart -lt 0 -or $buildMsixStart -le $buildJobStart) {
    throw "Could not isolate the Release build job."
}
$buildJob = $workflow.Substring($buildJobStart, $buildMsixStart - $buildJobStart)
if ($buildJob.Contains("Build WinUI Tray App (Release)", [StringComparison]::Ordinal)) {
    throw "Release packaging must not compile once before dotnet publish compiles the payload."
}
foreach ($publishToken in @(
    "dotnet publish src/OpenClaw.Tray.WinUI",
    "--self-contained",
    "--no-restore",
    "-p:Version=`$env:OPENCLAW_BUILD_VERSION",
    "-p:InformationalVersion=`$env:OPENCLAW_BUILD_VERSION",
    "Test-ReleaseNativeDependencies.ps1 -PayloadPath publish"
)) {
    Assert-Contains `
        -Text $buildJob `
        -Expected $publishToken `
        -Message "Release publish contract is missing '$publishToken'."
}

$triggerPaths = @(
    ".github/proof-pools.json",
    ".github/proof-pools.schema.json",
    "scripts/validate-proof-pools.ps1",
    "scripts/test-proof-pool-validator.ps1",
    "scripts/test-validate-docs-proof-pool-flow.ps1",
    "scripts/validate-docs.ps1",
    "scripts/Get-CiProofPoolRegressionDecision.ps1",
    "scripts/test-ci-workflow-contract.ps1"
)

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "openclaw-ci-contract-" + [guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    & git -C $tempRoot init --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Could not initialize temporary git repository."
    }
    & git -C $tempRoot config user.email "ci-contract@example.invalid"
    & git -C $tempRoot config user.name "CI Contract"

    foreach ($triggerPath in $triggerPaths) {
        $fullPath = Join-Path $tempRoot ($triggerPath.Replace("/", "\"))
        New-Item -ItemType Directory -Path (Split-Path -Parent $fullPath) -Force | Out-Null
        Set-Content -LiteralPath $fullPath -Value "baseline"
    }
    $unrelatedPath = Join-Path $tempRoot "docs\unrelated.md"
    New-Item -ItemType Directory -Path (Split-Path -Parent $unrelatedPath) -Force | Out-Null
    Set-Content -LiteralPath $unrelatedPath -Value "baseline"

    & git -C $tempRoot add .
    & git -C $tempRoot commit --quiet -m "baseline"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not commit temporary baseline."
    }

    foreach ($triggerPath in $triggerPaths) {
        $baseSha = (& git -C $tempRoot rev-parse HEAD).Trim()
        Add-Content `
            -LiteralPath (Join-Path $tempRoot ($triggerPath.Replace("/", "\"))) `
            -Value "changed"
        & git -C $tempRoot add .
        & git -C $tempRoot commit --quiet -m "change $triggerPath"
        $headSha = (& git -C $tempRoot rev-parse HEAD).Trim()
        $decision = & $selectorPath `
            -EventName pull_request `
            -BaseSha $baseSha `
            -HeadSha $headSha `
            -RepoRoot $tempRoot
        if ($decision -ne "true") {
            throw "Proof-pool trigger '$triggerPath' produced decision '$decision'."
        }
    }

    $baseSha = (& git -C $tempRoot rev-parse HEAD).Trim()
    Add-Content -LiteralPath $unrelatedPath -Value "changed"
    & git -C $tempRoot add .
    & git -C $tempRoot commit --quiet -m "unrelated change"
    $headSha = (& git -C $tempRoot rev-parse HEAD).Trim()
    $unrelatedDecision = & $selectorPath `
        -EventName pull_request `
        -BaseSha $baseSha `
        -HeadSha $headSha `
        -RepoRoot $tempRoot
    if ($unrelatedDecision -ne "false") {
        throw "Unrelated PR change produced decision '$unrelatedDecision'."
    }

    $missingDiffDecision = & $selectorPath `
        -EventName pull_request `
        -BaseSha "missing-base" `
        -HeadSha $headSha `
        -RepoRoot $tempRoot
    if ($missingDiffDecision -ne "true") {
        throw "Undetermined PR diff must run the regression fail-closed."
    }

    $pushDecision = & $selectorPath `
        -EventName push `
        -RepoRoot $tempRoot
    if ($pushDecision -ne "true") {
        throw "Push and tag workflow runs must run the proof-pool regression."
    }
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "CI workflow contracts passed: PR cancellation, fail-closed proof routing, conditional coverage, TRX logging, and single-pass Release publish." -ForegroundColor Green
