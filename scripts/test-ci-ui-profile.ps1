<#
.SYNOPSIS
    Exercises the UI lane's environment export without starting the app or using a real profile.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$exporter = Join-Path $RepoRoot "scripts\Set-CiUiTestEnvironment.ps1"
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-ui-profile-" + [guid]::NewGuid().ToString("N"))
$workspace = Join-Path $scratch "checkout with spaces"
$environmentFile = Join-Path $scratch "github-env"
$null = New-Item -ItemType Directory -Path $workspace

function Read-EnvironmentExport([string]$Path) {
    $bindings = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $pair = $line.Split("=", 2)
        if ($pair.Count -ne 2 -or $bindings.ContainsKey($pair[0])) {
            throw "The UI environment export contains a malformed or duplicate binding."
        }
        $bindings[$pair[0]] = $pair[1]
    }
    return $bindings
}

try {
    [IO.File]::WriteAllText($environmentFile, "UNRELATED_VARIABLE=preserved`n")
    & $exporter -RepoRoot $workspace -EnvironmentFile $environmentFile

    $actual = Read-EnvironmentExport $environmentFile
    $expected = [ordered]@{
        OPENCLAW_REPO_ROOT = $workspace
        OPENCLAW_TRAY_DATA_DIR = "$workspace\TestData\OpenClawTrayUITests"
        OPENCLAW_TRAY_APPDATA_DIR = "$workspace\TestData\OpenClawTrayUITests-Roaming"
        OPENCLAW_TRAY_LOCALAPPDATA_DIR = "$workspace\TestData\OpenClawTrayUITests-Local"
    }
    foreach ($entry in $expected.GetEnumerator()) {
        if (-not $actual.ContainsKey($entry.Key) -or $actual[$entry.Key] -cne $entry.Value) {
            throw "UI isolation contract failed for $($entry.Key): missing or misdirected root."
        }
        Write-Output "PASS $($entry.Key)"
    }

    if ($actual.Count -ne 5 -or $actual["UNRELATED_VARIABLE"] -cne "preserved") {
        throw "The UI environment export must preserve unrelated bindings without adding other overrides."
    }
    Write-Output "PASS unrelated environment preserved"

    $defaultEnvironmentFile = Join-Path $scratch "default-github-env"
    Push-Location $workspace
    try {
        & $exporter -EnvironmentFile $defaultEnvironmentFile
    } finally {
        Pop-Location
    }
    $default = Read-EnvironmentExport $defaultEnvironmentFile
    $expectedRepository = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath
    if ($default["OPENCLAW_REPO_ROOT"] -cne $expectedRepository) {
        throw "The default UI repository root must follow the exporter checkout, not the caller's directory."
    }
    Write-Output "PASS default repository follows checkout"
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}

# The UI jobs run this exporter from the pipeline revision, so a promoted alpha that predates it
# (or carries a different copy) still gets the pipeline's isolated roots bound to its own checkout.
$workflow = [IO.File]::ReadAllText((Join-Path $RepoRoot ".github\workflows\ci.yml"))
$steps = [regex]::Matches($workflow,
    '(?ms)^    - name: Configure isolated UI product roots\r?\n(?<body>.*?)(?=^    - |^  [a-z]|\z)')
if ($steps.Count -lt 1) { throw "Missing UI product-root workflow step." }
# Every job that binds UI roots (ui-tests and its tray-ui-shard) must run this exact, executed step.
foreach ($other in $steps) {
    if ($other.Groups["body"].Value -cne $steps[0].Groups["body"].Value) {
        throw "Every UI job must bind its isolated roots through the same pipeline step."
    }
}
Write-Output "PASS every UI job binds its roots through the same pipeline step ($($steps.Count))"
$step = $steps[0].Groups["body"].Value
$run = [regex]::Match($step, '(?ms)^      run: (?<value>.*)').Groups["value"].Value.TrimEnd()
if ($run.StartsWith("|")) { $run = $run.Substring(1).TrimStart("`r", "`n") -replace '(?m)^        ', '' }
if ([string]::IsNullOrWhiteSpace($run)) { throw "Missing UI product-root workflow script." }
$stepScript = [scriptblock]::Create($run)

$fixture = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-ui-profile-step-" + [guid]::NewGuid().ToString("N"))
$runnerTemp = "$fixture-runner-temp"
$saved = @{}
foreach ($name in @("GITHUB_SHA", "GITHUB_WORKSPACE", "GITHUB_ENV", "RUNNER_TEMP")) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
function Invoke-FixtureGit {
    $result = & git @args
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($args[0])" }
    return $result
}
$null = New-Item -ItemType Directory -Path $fixture, $runnerTemp
Push-Location $fixture
try {
    Invoke-FixtureGit init --quiet --initial-branch=main
    Invoke-FixtureGit config user.name "CI UI profile fixture"
    Invoke-FixtureGit config user.email "ci-ui-profile@example.invalid"
    Invoke-FixtureGit config commit.gpgsign false
    Set-Content "product.txt" "Alpha product source"
    Invoke-FixtureGit add product.txt
    Invoke-FixtureGit commit --quiet -m "Alpha without the UI environment helper"
    $candidate = (Invoke-FixtureGit rev-parse HEAD).Trim()
    $null = New-Item -ItemType Directory scripts
    $helper = "scripts/Set-CiUiTestEnvironment.ps1"
    Copy-Item -LiteralPath $exporter -Destination $helper
    Invoke-FixtureGit add $helper
    Invoke-FixtureGit commit --quiet -m "Pipeline adds the UI environment helper"
    $pipeline = (Invoke-FixtureGit rev-parse HEAD).Trim()
    Set-Content $helper "throw 'Moving main helper must not execute.'"
    Invoke-FixtureGit add $helper
    Invoke-FixtureGit commit --quiet -m "Main moves after pipeline selection"
    Invoke-FixtureGit checkout --quiet --detach $candidate

    $env:GITHUB_SHA = $pipeline
    $env:GITHUB_WORKSPACE = $fixture
    $env:RUNNER_TEMP = $runnerTemp
    $expectedWorkspace = (Resolve-Path -LiteralPath $fixture).ProviderPath
    foreach ($candidateHasHelper in @($false, $true)) {
        if ($candidateHasHelper) {
            $null = New-Item -ItemType Directory scripts -Force
            Set-Content $helper "throw 'Candidate helper must not execute.'"
        }
        $before = @(Invoke-FixtureGit status --porcelain) -join "`n"
        $env:GITHUB_ENV = Join-Path $runnerTemp "github-env-$candidateHasHelper"
        & $stepScript
        $bound = Read-EnvironmentExport $env:GITHUB_ENV
        if ($bound.Count -ne 4 -or
            $bound["OPENCLAW_REPO_ROOT"] -cne $expectedWorkspace -or
            $bound["OPENCLAW_TRAY_DATA_DIR"] -cne "$expectedWorkspace\TestData\OpenClawTrayUITests") {
            throw "The UI workflow step must bind the pipeline exporter's roots to the candidate checkout."
        }
        if ((Invoke-FixtureGit rev-parse HEAD).Trim() -cne $candidate) { throw "The UI workflow step moved the product checkout." }
        if ((@(Invoke-FixtureGit status --porcelain) -join "`n") -cne $before) { throw "The UI workflow step changed product checkout files." }
    }
    Write-Output "PASS workflow step uses the pipeline exporter for an older or divergent candidate"

    $env:GITHUB_SHA = $candidate
    $env:GITHUB_ENV = Join-Path $runnerTemp "github-env-missing"
    try {
        & $stepScript
        throw "A missing pipeline exporter was accepted."
    } catch {
        if (-not $_.Exception.Message.Contains("Could not load pipeline UI environment helper")) { throw }
    }
    if (Test-Path -LiteralPath $env:GITHUB_ENV) { throw "A failed exporter extraction still wrote UI roots." }
    Write-Output "PASS workflow step fails closed when the pipeline exporter is missing"
    Write-Output "UI profile isolation: 9 checks passed."
} finally {
    Pop-Location
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Remove-Item -LiteralPath $fixture, $runnerTemp -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
