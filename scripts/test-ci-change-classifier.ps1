<#
.SYNOPSIS
    Exercises the fail-closed CI change classifier.
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
$classifierPath = Join-Path $repoRootPath "scripts\Get-CiChangeClassification.ps1"

function Assert-Classification {
    param(
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string[]]$Paths,
        [Parameter(Mandatory)][string]$Scenario
    )

    $actual = & $classifierPath `
        -EventName pull_request `
        -RepoRoot $repoRootPath `
        -ChangedPaths $Paths
    if ($actual -ne $Expected) {
        throw "$Scenario classified as '$actual' instead of '$Expected'."
    }
}

Assert-Classification `
    -Expected docs_only `
    -Paths @(".agents/skills/example/SKILL.md") `
    -Scenario "Skill-only documentation"
Assert-Classification `
    -Expected docs_only `
    -Paths @("README.md", "docs/TEST_COVERAGE.md", "docs/diagrams/ci.svg") `
    -Scenario "Maintained documentation"
Assert-Classification `
    -Expected full `
    -Paths @(".agents/skills/example/SKILL.md", "src/OpenClaw.Shared/Example.cs") `
    -Scenario "Mixed skill and source change"

$unsafePaths = @(
    ".github/workflows/ci.yml",
    ".github/dependabot.yml",
    "scripts/validate-docs.ps1",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "package.json",
    "src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj",
    "installer/OpenClaw.iss",
    "src/OpenClaw.Shared/Example.cs",
    "tests/OpenClaw.Shared.Tests/ExampleTests.cs",
    ".agents/skills/example/scripts/run.ps1",
    ".agents/skills/example/scripts/run",
    "unknown/location/file.txt"
)
foreach ($unsafePath in $unsafePaths) {
    Assert-Classification `
        -Expected full `
        -Paths @($unsafePath) `
        -Scenario "Unsafe path '$unsafePath'"
}

$emptyDecision = & $classifierPath `
    -EventName pull_request `
    -RepoRoot $repoRootPath `
    -ChangedPaths @()
if ($emptyDecision -ne "full") {
    throw "An empty explicit path list must classify as full."
}

$pushDecision = & $classifierPath `
    -EventName push `
    -RepoRoot $repoRootPath `
    -ChangedPaths @(".agents/skills/example/SKILL.md")
if ($pushDecision -ne "full") {
    throw "Push and tag workflow invocations must classify as full."
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "openclaw-change-classifier-" + [guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    & git -C $tempRoot init --quiet
    & git -C $tempRoot config user.email "ci-classifier@example.invalid"
    & git -C $tempRoot config user.name "CI Classifier"

    $skillPath = Join-Path $tempRoot ".agents\skills\example\SKILL.md"
    New-Item -ItemType Directory -Path (Split-Path -Parent $skillPath) -Force | Out-Null
    Set-Content -LiteralPath $skillPath -Value "baseline"
    & git -C $tempRoot add .
    & git -C $tempRoot commit --quiet -m "baseline"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not commit classifier test baseline."
    }
    $baseSha = (& git -C $tempRoot rev-parse HEAD).Trim()

    Add-Content -LiteralPath $skillPath -Value "changed"
    & git -C $tempRoot add .
    & git -C $tempRoot commit --quiet -m "skill docs"
    $headSha = (& git -C $tempRoot rev-parse HEAD).Trim()
    $gitDecision = & $classifierPath `
        -EventName pull_request `
        -BaseSha $baseSha `
        -HeadSha $headSha `
        -RepoRoot $tempRoot
    if ($gitDecision -ne "docs_only") {
        throw "A real skill-only git diff classified as '$gitDecision'."
    }

    $emptyGitDecision = & $classifierPath `
        -EventName pull_request `
        -BaseSha $headSha `
        -HeadSha $headSha `
        -RepoRoot $tempRoot
    if ($emptyGitDecision -ne "full") {
        throw "An empty git diff must classify as full."
    }

    foreach ($invalidBase in @("", "missing-base", ("f" * 40))) {
        $invalidDecision = & $classifierPath `
            -EventName pull_request `
            -BaseSha $invalidBase `
            -HeadSha $headSha `
            -RepoRoot $tempRoot
        if ($invalidDecision -ne "full") {
            throw "Invalid revision '$invalidBase' classified as '$invalidDecision'."
        }
    }
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "CI change classifier regressions passed: safe docs fast-path and fail-closed full validation cases." -ForegroundColor Green
