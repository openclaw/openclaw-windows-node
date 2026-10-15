<#
.SYNOPSIS
    Exercises the fail-closed CI impact classifier.
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
$laneNames = @(
    "core_tests",
    "tray_tests",
    "ui_tests",
    "setup_e2e",
    "revocation_e2e",
    "network_e2e",
    "x64_release",
    "arm64_release",
    "full"
)

function Get-Impact {
    param(
        [string]$EventName = "pull_request",
        [string[]]$Paths
    )

    $json = & $classifierPath `
        -EventName $EventName `
        -RepoRoot $repoRootPath `
        -ChangedPaths $Paths
    $json | ConvertFrom-Json
}

function Assert-Impact {
    param(
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][string[]]$Paths,
        [Parameter(Mandatory)][string]$Classification,
        [string[]]$Required = @(),
        [string]$EventName = "pull_request"
    )

    $actual = Get-Impact -EventName $EventName -Paths $Paths
    if ($actual.classification -ne $Classification) {
        throw "$Scenario classified as '$($actual.classification)' instead of '$Classification'."
    }
    $expectedProperties = @("classification") + $laneNames
    if (@(Compare-Object $expectedProperties @($actual.PSObject.Properties.Name)).Count -ne 0) {
        throw "$Scenario returned an unexpected classifier output shape."
    }

    foreach ($lane in $laneNames) {
        $expected = $Required -contains $lane
        if ($actual.$lane -isnot [bool] -or $actual.$lane -ne $expected) {
            throw "$Scenario expected $lane=$expected but received $($actual.$lane)."
        }
    }
}

$allLanes = @($laneNames)
$fullPrLanes = @($laneNames | Where-Object { $_ -ne "arm64_release" })
$allProductLanes = @(
    "core_tests",
    "tray_tests",
    "ui_tests",
    "setup_e2e",
    "revocation_e2e",
    "network_e2e"
)
$cases = @(
    @{
        Scenario = "MSIX allocation baseline"
        Paths = @(".github/msix-version-baseline.json")
        Classification = "full"
        Required = $fullPrLanes
    },
    @{
        Scenario = "Maintained documentation"
        Paths = @("README.md", "docs/TEST_COVERAGE.md", "docs/diagrams/ci.svg")
        Classification = "docs_only"
    },
    @{
        Scenario = "Skill-only documentation"
        Paths = @(".agents/skills/example/SKILL.md")
        Classification = "docs_only"
    },
    @{
        Scenario = "WinNode skill reference"
        Paths = @(".agents/skills/winnode/SKILL.md")
        Classification = "targeted"
        Required = @("core_tests")
    },
    @{
        Scenario = "CLI-only product change"
        Paths = @("src/OpenClaw.Cli/Program.cs")
        Classification = "targeted"
        Required = @("core_tests")
    },
    @{
        Scenario = "WinNode CLI-only product change"
        Paths = @("src/OpenClaw.WinNode.Cli/Program.cs")
        Classification = "targeted"
        Required = @("core_tests")
    },
    @{
        Scenario = "Chat UI change"
        Paths = @("src/OpenClaw.Chat/ChatModels.cs")
        Classification = "targeted"
        Required = @("tray_tests", "ui_tests")
    },
    @{
        Scenario = "Pure XAML change"
        Paths = @("src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml")
        Classification = "targeted"
        Required = @("tray_tests", "ui_tests")
    },
    @{
        Scenario = "Tray logic change"
        Paths = @("src/OpenClaw.Tray.WinUI/Services/TrayTooltipBuilder.cs")
        Classification = "targeted"
        Required = @("tray_tests", "ui_tests")
    },
    @{
        Scenario = "Setup engine change"
        Paths = @("src/OpenClaw.SetupEngine/SetupOrchestrator.cs")
        Classification = "targeted"
        Required = @("tray_tests", "setup_e2e")
    },
    @{
        Scenario = "Gateway install policy change"
        Paths = @("src/OpenClaw.SetupEngine/GatewayInstallPolicy.cs")
        Classification = "targeted"
        Required = @("tray_tests", "setup_e2e", "revocation_e2e", "network_e2e")
    },
    @{
        Scenario = "Connection change"
        Paths = @("src/OpenClaw.Connection/GatewayConnectionManager.cs")
        Classification = "targeted"
        Required = @("core_tests", "tray_tests", "setup_e2e", "revocation_e2e", "network_e2e")
    },
    @{
        Scenario = "Broad Shared protocol change"
        Paths = @("src/OpenClaw.Shared/Gateway/GatewayClient.cs")
        Classification = "targeted"
        Required = $allProductLanes
    },
    @{
        Scenario = "Tray MCP change"
        Paths = @("src/OpenClaw.Tray.WinUI/Services/McpRuntimeStatePolicy.cs")
        Classification = "targeted"
        Required = @("tray_tests", "ui_tests", "setup_e2e", "revocation_e2e", "network_e2e")
    },
    @{
        Scenario = "Core test-only change"
        Paths = @("tests/OpenClaw.WinNode.Cli.Tests/ProgramTests.cs")
        Classification = "targeted"
        Required = @("core_tests")
    },
    @{
        Scenario = "UI test-only change"
        Paths = @("tests/OpenClaw.Tray.UITests/ReactorTests.cs")
        Classification = "targeted"
        Required = @("ui_tests")
    },
    @{
        Scenario = "Setup E2E test-only change"
        Paths = @("tests/OpenClaw.E2ETests/Setup/SetupAndConnectTests.cs")
        Classification = "targeted"
        Required = @("setup_e2e")
    },
    @{
        Scenario = "Recognized mixed product change"
        Paths = @("src/OpenClaw.Cli/Program.cs", "src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml")
        Classification = "targeted"
        Required = @("core_tests", "tray_tests", "ui_tests")
    },
    @{
        Scenario = "Workflow infrastructure"
        Paths = @(".github/workflows/ci.yml")
        Classification = "full"
        Required = $fullPrLanes
    },
    @{
        Scenario = "Package and build infrastructure"
        Paths = @("Directory.Packages.props")
        Classification = "full"
        Required = $fullPrLanes
    },
    @{
        Scenario = "Installer infrastructure"
        Paths = @("installer.iss")
        Classification = "full"
        Required = $fullPrLanes
    },
    @{
        Scenario = "Classifier contract change"
        Paths = @("scripts/test-ci-change-classifier.ps1")
        Classification = "full"
        Required = $fullPrLanes
    },
    @{
        Scenario = "Unknown path"
        Paths = @("unknown/location/file.txt")
        Classification = "full"
        Required = $fullPrLanes
    },
    @{
        Scenario = "Mixed recognized and unknown paths"
        Paths = @("src/OpenClaw.Cli/Program.cs", "unknown/location/file.txt")
        Classification = "full"
        Required = $fullPrLanes
    },
    @{
        Scenario = "Main push"
        EventName = "push"
        Paths = @("src/OpenClaw.Cli/Program.cs")
        Classification = "full"
        Required = $allLanes
    },
    @{
        Scenario = "Tag push"
        EventName = "push"
        Paths = @("docs/TEST_COVERAGE.md")
        Classification = "full"
        Required = $allLanes
    }
)

foreach ($case in $cases) {
    $arguments = @{
        Scenario = $case.Scenario
        Paths = $case.Paths
        Classification = $case.Classification
    }
    if ($case.ContainsKey("Required")) {
        $arguments.Required = $case.Required
    }
    if ($case.ContainsKey("EventName")) {
        $arguments.EventName = $case.EventName
    }
    Assert-Impact @arguments
}

$fastOnlyPaths = @(
    ".github/scripts/repository-triage.cjs",
    ".github/scripts/repository-triage.test.cjs"
)
foreach ($path in $fastOnlyPaths) {
    Assert-Impact -Scenario "Exact tooling path $path" -Paths @($path) -Classification fast_only
    Assert-Impact -Scenario "Windows tooling path $path" -Paths @($path.Replace("/", "\")) -Classification fast_only
    Assert-Impact -Scenario "Tooling with docs $path" -Paths @($path, "docs/TEST_COVERAGE.md") -Classification fast_only

    # Adding tooling must never reduce any existing selection, in either order.
    foreach ($case in $cases) {
        $required = if ($case.ContainsKey("Required")) { $case.Required } else { @() }
        $eventName = if ($case.ContainsKey("EventName")) { $case.EventName } else { "pull_request" }
        $classification = if ($case.Classification -eq "docs_only" -and $eventName -eq "pull_request") {
            "fast_only"
        } else { $case.Classification }
        foreach ($toolingFirst in @($true, $false)) {
            $paths = if ($toolingFirst) { @($path) + $case.Paths } else { $case.Paths + @($path) }
            Assert-Impact -Scenario "Tooling union: $($case.Scenario), toolingFirst=$toolingFirst" `
                -Paths $paths -Classification $classification -Required $required -EventName $eventName
        }
    }
}
Assert-Impact -Scenario "Both tooling paths" -Paths $fastOnlyPaths -Classification fast_only
foreach ($eventName in @("push", "workflow_dispatch", "schedule", "pull_request_target", "")) {
    Assert-Impact -Scenario "Tooling on non-PR event '$eventName'" -Paths $fastOnlyPaths `
        -EventName $eventName -Classification full -Required $allLanes
}
foreach ($path in @(
        ".github/scripts/other.cjs",
        ".github/scripts/repository-triage.cjs.bak",
        ".github/scripts/repository-triage.test.mjs",
        ".github/scripts/nested/repository-triage.cjs",
        ".github/scripts/Repository-Triage.cjs",
        ".github/scripts/package.json",
        ".github/scripts/package-lock.json",
        ".github/actions/triage/action.yml",
        ".github/extensions/openclaw-triage-dashboard/triage-state.test.mjs",
        "scripts/repository-triage.cjs",
        "src/OpenClaw.Shared/OpenClaw.Shared.csproj",
        "src/Directory.Build.props",
        "NuGet.Config",
        "/.github/scripts/repository-triage.cjs",
        "../.github/scripts/repository-triage.cjs",
        ".github//scripts/repository-triage.cjs",
        ".github/scripts/../scripts/repository-triage.cjs",
        ".github/scripts/repository-triage.cjs$([char]0)",
        " ")) {
    Assert-Impact -Scenario "Tooling does not exempt '$path'" -Paths (@($path) + $fastOnlyPaths) `
        -Classification full -Required $fullPrLanes
}

$emptyImpact = Get-Impact -Paths @()
if ($emptyImpact.classification -ne "full" -or -not $emptyImpact.full) {
    throw "An empty explicit path list must select full validation."
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
    $gitImpact = (& $classifierPath `
        -EventName pull_request `
        -BaseSha $baseSha `
        -HeadSha $headSha `
        -RepoRoot $tempRoot) | ConvertFrom-Json
    if ($gitImpact.classification -ne "docs_only") {
        throw "A real skill-only git diff classified as '$($gitImpact.classification)'."
    }

    $toolingPath = Join-Path $tempRoot ".github\scripts\repository-triage.cjs"
    New-Item -ItemType Directory -Path (Split-Path -Parent $toolingPath) -Force | Out-Null
    foreach ($operation in @("add", "modify", "delete-allowlisted", "readd", "rename", "delete-unknown")) {
        $baseSha = (& git -C $tempRoot rev-parse HEAD).Trim()
        switch ($operation) {
            "add" { Set-Content -LiteralPath $toolingPath -Value "tooling baseline" }
            "modify" { Add-Content -LiteralPath $toolingPath -Value "tooling changed" }
            "delete-allowlisted" { Remove-Item -LiteralPath $toolingPath }
            "readd" { Set-Content -LiteralPath $toolingPath -Value "tooling baseline" }
            "rename" {
                $renamedPath = Join-Path $tempRoot ".github\scripts\unreviewed-tool.cjs"
                Move-Item -LiteralPath $toolingPath -Destination $renamedPath
            }
            "delete-unknown" { Remove-Item -LiteralPath $renamedPath }
        }
        & git -C $tempRoot add .
        & git -C $tempRoot commit --quiet -m "tooling $operation"
        if ($LASTEXITCODE -ne 0) { throw "Could not commit tooling $operation fixture." }
        $headSha = (& git -C $tempRoot rev-parse HEAD).Trim()
        $gitImpact = (& $classifierPath -EventName pull_request -BaseSha $baseSha `
            -HeadSha $headSha -RepoRoot $tempRoot) | ConvertFrom-Json
        $expected = if ($operation -in @("rename", "delete-unknown")) { "full" } else { "fast_only" }
        if ($gitImpact.classification -ne $expected) {
            throw "Real tooling $operation diff expected '$expected', got '$($gitImpact.classification)'."
        }
        foreach ($lane in $laneNames) {
            $expectedLane = $expected -eq "full" -and $fullPrLanes -contains $lane
            if ($gitImpact.$lane -ne $expectedLane) {
                throw "Real tooling $operation diff expected $lane=$expectedLane."
            }
        }
    }

    $emptyGitImpact = (& $classifierPath `
        -EventName pull_request `
        -BaseSha $headSha `
        -HeadSha $headSha `
        -RepoRoot $tempRoot) | ConvertFrom-Json
    if ($emptyGitImpact.classification -ne "full" -or -not $emptyGitImpact.full) {
        throw "An empty git diff must select full validation."
    }

    foreach ($invalidBase in @("", "missing-base", ("f" * 40))) {
        foreach ($invalidSide in @("base", "head")) {
            $base = if ($invalidSide -eq "base") { $invalidBase } else { $headSha }
            $head = if ($invalidSide -eq "head") { $invalidBase } else { $headSha }
            $invalidImpact = (& $classifierPath `
                -EventName pull_request `
                -BaseSha $base `
                -HeadSha $head `
                -RepoRoot $tempRoot) | ConvertFrom-Json
            if ($invalidImpact.classification -ne "full" -or -not $invalidImpact.full) {
                throw "Invalid revision '$invalidBase' did not select full validation."
            }
        }
    }
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "CI impact classifier regressions passed: exact fast-only tooling, monotonic mixed lanes, real git diffs, and fail-closed full validation." -ForegroundColor Green
