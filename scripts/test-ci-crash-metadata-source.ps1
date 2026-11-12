<#
.SYNOPSIS
    Executes the CI diagnostics step against disposable Git history to verify
    pipeline-helper ownership without changing the selected product checkout.
    Usage: ./scripts/test-ci-crash-metadata-source.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$workflow = Get-Content (Join-Path $root '.github/workflows/ci.yml') -Raw
$step = [regex]::Match($workflow,
    '(?ms)^    - name: Capture non-memory Tray UI crash metadata\r?\n(?<body>.*?)(?=^    - |^  [a-z]|\z)').Groups['body'].Value
$run = [regex]::Match($step, '(?ms)^      run: (?<value>.*)').Groups['value'].Value.TrimEnd()
if ($run.StartsWith('|')) { $run = $run.Substring(1).TrimStart("`r", "`n") -replace '(?m)^        ', '' }
if ([string]::IsNullOrWhiteSpace($run)) { throw 'Missing diagnostics workflow script.' }
$script = [scriptblock]::Create($run)
$fixture = Join-Path ([IO.Path]::GetTempPath()) "ci-helper-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixture | Out-Null
$saved = @{}
foreach ($name in @('GITHUB_SHA', 'RUNNER_TEMP', 'TRAY_UI_TEST_STARTED_UTC')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
# Match the file inventory consumed by Invoke-LocalAiVisualProof in historical alphas.
function Get-ProofSourceHashes {
    @(Invoke-FixtureGit -c core.quotepath=false ls-files --cached --others --exclude-standard |
        Sort-Object -Unique | ForEach-Object {
            "${_}:$((Get-FileHash -LiteralPath (Join-Path $fixture $_) -Algorithm SHA256).Hash)"
        }) -join "`n"
}
function Invoke-FixtureGit {
    $result = & git @args
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($args[0])" }
    return $result
}
Push-Location $fixture
try {
    Invoke-FixtureGit init --quiet --initial-branch=main
    Invoke-FixtureGit config user.name 'CI helper fixture'
    Invoke-FixtureGit config user.email 'ci-helper@example.invalid'
    Invoke-FixtureGit config commit.gpgsign false
    Set-Content 'product.txt' 'The original alpha product source'
    Invoke-FixtureGit add product.txt
    Invoke-FixtureGit commit --quiet -m 'Alpha without diagnostic helper'
    $candidate = (Invoke-FixtureGit rev-parse HEAD).Trim()
    New-Item -ItemType Directory scripts | Out-Null
    $helper = 'scripts/Capture-CiTestHostCrashMetadata.ps1'
    Set-Content $helper 'param([string]$StartedUtc); Write-Output "pipeline:$StartedUtc"'
    Invoke-FixtureGit add $helper
    Invoke-FixtureGit commit --quiet -m 'Pipeline adds diagnostics'
    $env:GITHUB_SHA = (Invoke-FixtureGit rev-parse HEAD).Trim()
    Set-Content $helper "throw 'Moving main helper must not execute.'"
    Invoke-FixtureGit add $helper
    Invoke-FixtureGit commit --quiet -m 'Main moves after pipeline selection'
    Invoke-FixtureGit checkout --quiet --detach $candidate
    $env:RUNNER_TEMP = "$fixture-runner-temp"
    New-Item -ItemType Directory $env:RUNNER_TEMP | Out-Null
    $env:TRAY_UI_TEST_STARTED_UTC = '2026-11-07T02:00:00Z'
    foreach ($candidateHasHelper in @($false, $true)) {
        if ($candidateHasHelper) {
            New-Item -ItemType Directory scripts -Force | Out-Null
            Set-Content $helper "throw 'Candidate helper must not execute.'"
        }
        $sourceHashes = Get-ProofSourceHashes
        $before = @(Invoke-FixtureGit status --porcelain) -join "`n"
        $output = @(& $script) -join "`n"
        if ($output -cne 'pipeline:2026-11-07T02:00:00Z') { throw "Wrong helper or time boundary: $output" }
        if ((Get-ProofSourceHashes) -cne $sourceHashes) { throw 'Diagnostics contaminated the source manifest.' }
        if ((Invoke-FixtureGit rev-parse HEAD).Trim() -cne $candidate) { throw 'Product checkout moved.' }
        if ((@(Invoke-FixtureGit status --porcelain) -join "`n") -cne $before) { throw 'Product checkout files changed.' }
    }
    # A failed extraction must not execute a stale helper left in runner temp.
    $env:GITHUB_SHA = $candidate
    try {
        & $script | Out-Null
        throw 'Missing pipeline helper was accepted.'
    } catch {
        if (-not $_.Exception.Message.Contains('Could not load pipeline crash metadata helper')) { throw }
    }
    Write-Host 'Passed 3 pipeline crash-helper cases: absent/older candidate helper, pinned revision, unchanged hashable source manifest, and failed extraction.'
} finally {
    Pop-Location
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Remove-Item -LiteralPath $fixture, "$fixture-runner-temp" -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
