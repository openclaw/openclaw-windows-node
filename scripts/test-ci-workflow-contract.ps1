<#
.SYNOPSIS
    Validates CI lane routing, stable gate, release, and proof contracts.
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
$codeQlWorkflowPath = Join-Path $repoRootPath ".github\workflows\codeql.yml"
$selectorPath = Join-Path $repoRootPath "scripts\Get-CiProofPoolRegressionDecision.ps1"
$runnerPath = Join-Path $repoRootPath "scripts\Invoke-CiTest.ps1"
$e2eRunnerPath = Join-Path $repoRootPath "scripts\Invoke-CiE2e.ps1"
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$codeQlWorkflow = Get-Content -LiteralPath $codeQlWorkflowPath -Raw
$runner = Get-Content -LiteralPath $runnerPath -Raw
$e2eRunner = Get-Content -LiteralPath $e2eRunnerPath -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Text.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw $Message
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Unexpected,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Text.IndexOf($Unexpected, [StringComparison]::Ordinal) -ge 0) {
        throw $Message
    }
}

function Get-JobBlock {
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Text = $workflow
    )

    $escapedName = [regex]::Escape($Name)
    $startMatch = [regex]::Match($Text, "(?m)^  ${escapedName}:\r?$")
    if (-not $startMatch.Success) {
        throw "Could not find CI job '$Name'."
    }
    $jobHeadingPattern = [regex]::new("(?m)^  [a-zA-Z0-9_-]+:\r?$")
    $nextMatch = $jobHeadingPattern.Match(
        $Text,
        $startMatch.Index + $startMatch.Length)
    if (-not $nextMatch.Success) {
        return $Text.Substring($startMatch.Index)
    }
    return $Text.Substring(
        $startMatch.Index,
        $nextMatch.Index - $startMatch.Index)
}

function Get-StepBlock {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )

    $escapedName = [regex]::Escape($Name)
    $startMatch = [regex]::Match($Text, "(?m)^    - name: ${escapedName}\r?$")
    if (-not $startMatch.Success) {
        throw "Could not find CI step '$Name'."
    }
    $stepHeadingPattern = [regex]::new("(?m)^    - (?:name:|uses:)")
    $nextMatch = $stepHeadingPattern.Match(
        $Text,
        $startMatch.Index + $startMatch.Length)
    if (-not $nextMatch.Success) {
        return $Text.Substring($startMatch.Index)
    }
    return $Text.Substring(
        $startMatch.Index,
        $nextMatch.Index - $startMatch.Index)
}

function Get-WorkflowTriggerBlock {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )

    $escapedName = [regex]::Escape($Name)
    $startMatch = [regex]::Match($Text, "(?m)^  ${escapedName}:\r?$")
    if (-not $startMatch.Success) {
        throw "Could not find workflow trigger '$Name'."
    }

    $nextMatch = [regex]::Match(
        $Text.Substring($startMatch.Index + $startMatch.Length),
        "(?m)^(?:  [a-zA-Z0-9_-]+:|[a-zA-Z0-9_-]+:)\r?$")
    if (-not $nextMatch.Success) {
        return $Text.Substring($startMatch.Index)
    }

    return $Text.Substring(
        $startMatch.Index,
        $startMatch.Length + $nextMatch.Index)
}

function Get-InlineYamlList {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )

    $escapedName = [regex]::Escape($Name)
    $match = [regex]::Match(
        $Text,
        "(?m)^\s+${escapedName}:\s*\[(?<values>[^\]]*)\]\s*$")
    if (-not $match.Success) {
        throw "Could not find inline YAML list '$Name'."
    }

    return @($match.Groups["values"].Value.Split(",") |
        ForEach-Object { $_.Trim().Trim('"', "'") })
}

function Get-YamlBlockList {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Name
    )

    $escapedName = [regex]::Escape($Name)
    $match = [regex]::Match(
        $Text,
        "(?ms)^\s+${escapedName}:\s*\r?\n(?<items>(?:\s+-\s+.+\r?\n?)+)")
    if (-not $match.Success) {
        throw "Could not find YAML block list '$Name'."
    }

    return @([regex]::Matches($match.Groups["items"].Value, "(?m)^\s+-\s+['`"]?(?<value>.+?)['`"]?\s*$") |
        ForEach-Object { $_.Groups["value"].Value })
}

function ConvertTo-GitHubPathRegex {
    param([Parameter(Mandatory)][string]$Glob)

    # Models the GitHub filter subset used here: `*` stays within one path
    # segment, `**` crosses segments and `**/` also matches zero directories.
    if ($Glob -match '[?+\[\]!]') {
        throw "Unsupported GitHub path glob syntax in '$Glob'."
    }

    $regex = [System.Text.StringBuilder]::new("^")
    for ($i = 0; $i -lt $Glob.Length; $i++) {
        if ($Glob[$i] -ne "*") {
            [void]$regex.Append([regex]::Escape([string]$Glob[$i]))
            continue
        }
        if ($i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq "*") {
            $i++
            if ($i + 1 -lt $Glob.Length -and $Glob[$i + 1] -eq "/") {
                $i++
                [void]$regex.Append("(?:.*/)?")
            }
            else {
                [void]$regex.Append(".*")
            }
            continue
        }
        [void]$regex.Append("[^/]*")
    }

    return $regex.Append("$").ToString()
}

function Test-PathMatches {
    param(
        [Parameter(Mandatory)][string[]]$Patterns,
        [Parameter(Mandatory)][string]$Path
    )

    # GitHub evaluates path patterns in order: a later `!` match excludes a
    # path and a later positive match includes it again.
    $matched = $false
    foreach ($pattern in $Patterns) {
        $negated = $pattern.StartsWith("!", [StringComparison]::Ordinal)
        $glob = if ($negated) { $pattern.Substring(1) } else { $pattern }
        if ([regex]::IsMatch($Path, (ConvertTo-GitHubPathRegex -Glob $glob))) {
            $matched = -not $negated
        }
    }

    return $matched
}

function Test-CodeQlTrigger {
    param(
        [Parameter(Mandatory)][ValidateSet("push", "pull_request", "schedule", "workflow_dispatch")]
        [string]$EventName,
        [string]$RefName,
        [string]$Path
    )

    if ($EventName -in @("schedule", "workflow_dispatch")) {
        return $true
    }

    if ($EventName -eq "push") {
        if ($RefName.StartsWith("refs/tags/", [StringComparison]::Ordinal)) {
            return $codeQlPushTags.Count -gt 0
        }
        if ($RefName -notin $codeQlPushBranches) {
            return $false
        }
        return Test-PathMatches -Patterns $codeQlPushPaths -Path $Path
    }

    if ($RefName -notin $codeQlPullRequestBranches) {
        return $false
    }
    return Test-PathMatches -Patterns $codeQlPullRequestPaths -Path $Path
}

$codeQlPush = Get-WorkflowTriggerBlock -Text $codeQlWorkflow -Name "push"
$codeQlPullRequest = Get-WorkflowTriggerBlock -Text $codeQlWorkflow -Name "pull_request"
$codeQlPushBranches = @(Get-InlineYamlList -Text $codeQlPush -Name "branches")
$codeQlPushTags = @(Get-InlineYamlList -Text $codeQlPush -Name "tags")
$codeQlPushPaths = @(Get-YamlBlockList -Text $codeQlPush -Name "paths")
$codeQlPullRequestBranches = @(Get-InlineYamlList -Text $codeQlPullRequest -Name "branches")
$codeQlPullRequestPaths = @(Get-YamlBlockList -Text $codeQlPullRequest -Name "paths")

$expectedCodeQlPaths = @(
    ".gitattributes",
    "**/.gitattributes",
    ".github/codeql/**",
    ".github/workflows/**",
    "src/**",
    "tests/**",
    "*.sln",
    "*.slnx",
    "*.props",
    "*.targets",
    "global.json",
    "NuGet.Config",
    "package.json",
    "package-lock.json",
    "!src/**/*.md",
    "!tests/**/*.md"
)
if (($codeQlPushBranches -join ",") -ne "master") {
    throw "CodeQL branch pushes must be limited to master; main uses scheduled scans."
}
if (($codeQlPushTags -join ",") -ne "**") {
    throw "CodeQL must preserve scanning for all tag pushes."
}
if (($codeQlPullRequestBranches -join ",") -ne "main,master") {
    throw "CodeQL pull requests must continue to target main and master."
}
if (($codeQlPushPaths -join "`n") -cne ($expectedCodeQlPaths -join "`n")) {
    throw "CodeQL push path filters changed unexpectedly."
}
if (($codeQlPullRequestPaths -join "`n") -cne ($expectedCodeQlPaths -join "`n")) {
    throw "CodeQL pull request path filters changed unexpectedly."
}
$codeQlTriggerCases = @(
    @{ Name = "feature branch code push"; Event = "push"; Ref = "feature/example"; Path = "src/OpenClaw.Shared/Example.cs"; Expected = $false },
    @{ Name = "main code push"; Event = "push"; Ref = "main"; Path = "src/OpenClaw.Shared/Example.cs"; Expected = $false },
    @{ Name = "master code push"; Event = "push"; Ref = "master"; Path = "src/OpenClaw.Shared/Example.cs"; Expected = $true },
    @{ Name = "release tag push"; Event = "push"; Ref = "refs/tags/v2026.9.3"; Path = "docs/release.md"; Expected = $true },
    @{ Name = "relevant pull request"; Event = "pull_request"; Ref = "main"; Path = "src/OpenClaw.Shared/Example.cs"; Expected = $true },
    @{ Name = "root attributes pull request"; Event = "pull_request"; Ref = "main"; Path = ".gitattributes"; Expected = $true },
    @{ Name = "nested attributes pull request"; Event = "pull_request"; Ref = "main"; Path = "src/.gitattributes"; Expected = $true },
    @{ Name = "nested build props pull request"; Event = "pull_request"; Ref = "main"; Path = "eng/Directory.Build.props"; Expected = $false },
    @{ Name = "hidden attributes pull request"; Event = "pull_request"; Ref = "main"; Path = ".github/.gitattributes"; Expected = $true },
    @{ Name = "master attributes push"; Event = "push"; Ref = "master"; Path = ".gitattributes"; Expected = $true },
    @{ Name = "docs-only pull request"; Event = "pull_request"; Ref = "main"; Path = "docs/example.md"; Expected = $false },
    @{ Name = "scoped source instructions pull request"; Event = "pull_request"; Ref = "main"; Path = "src/OpenClaw.Tray.WinUI/AGENTS.md"; Expected = $false },
    @{ Name = "test Markdown pull request"; Event = "pull_request"; Ref = "main"; Path = "tests/README.md"; Expected = $false },
    @{ Name = "master source Markdown push"; Event = "push"; Ref = "master"; Path = "src/OpenClaw.Shared/README.md"; Expected = $false },
    @{ Name = "workflow pull request"; Event = "pull_request"; Ref = "main"; Path = ".github/workflows/ci.yml"; Expected = $true },
    @{ Name = "Markdown workflow source pull request"; Event = "pull_request"; Ref = "main"; Path = ".github/workflows/example.md"; Expected = $true },
    @{ Name = "master Markdown workflow source push"; Event = "push"; Ref = "master"; Path = ".github/workflows/example.md"; Expected = $true },
    @{ Name = "agent-skill-only pull request"; Event = "pull_request"; Ref = "main"; Path = ".agents/skills/example/SKILL.md"; Expected = $false },
    @{ Name = "scheduled scan"; Event = "schedule"; Ref = ""; Path = ""; Expected = $true },
    @{ Name = "manual scan"; Event = "workflow_dispatch"; Ref = ""; Path = ""; Expected = $true }
)
foreach ($case in $codeQlTriggerCases) {
    $actual = Test-CodeQlTrigger `
        -EventName $case.Event `
        -RefName $case.Ref `
        -Path $case.Path
    if ($actual -ne $case.Expected) {
        throw "CodeQL trigger case '$($case.Name)' expected '$($case.Expected)' but got '$actual'."
    }
}
foreach ($requiredToken in @(
        "workflow_dispatch:",
        "schedule:",
        'cron: "29 */6 * * *"',
        "profile:",
        "default: all",
        "type: choice",
        'group: codeql-${{ github.workflow }}-',
        "github.event_name == 'schedule' && 'schedule'",
        "cancel-in-progress: `${{ github.event_name == 'pull_request' }}",
        "actions: read",
        "contents: read",
        "security-events: write",
        "inputs.profile == 'csharp'",
        "languages: csharp",
        "build-mode: manual",
        "config-file: ./.github/codeql/codeql-csharp-security.yml",
        'category: "/codeql/csharp"',
        "inputs.profile == 'actions'",
        "languages: actions",
        "config-file: ./.github/codeql/codeql-actions-security.yml",
        'category: "/codeql/actions"'
    )) {
    Assert-Contains `
        -Text $codeQlWorkflow `
        -Expected $requiredToken `
        -Message "CodeQL workflow is missing required contract '$requiredToken'."
}
# Fork pull requests do not receive the repository variable used by the retired
# setup-migration gate. Eligibility must depend only on draft/profile context.
foreach ($retiredGate in @("advanced-setup", "OPENCLAW_CODEQL_ADVANCED_SETUP", "inputs.advanced_setup")) {
    Assert-NotContains `
        -Text $codeQlWorkflow `
        -Unexpected $retiredGate `
        -Message "CodeQL must not depend on the retired migration gate '$retiredGate'."
}
foreach ($jobName in @("csharp", "actions")) {
    $codeQlJob = Get-JobBlock -Name $jobName -Text $codeQlWorkflow
    $expectedEligibility = "if: `${{ (github.event_name != 'pull_request' || !github.event.pull_request.draft) && (github.event_name != 'workflow_dispatch' || inputs.profile == 'all' || inputs.profile == '$jobName') }}"
    Assert-Contains `
        -Text $codeQlJob `
        -Expected $expectedEligibility `
        -Message "CodeQL job '$jobName' must admit non-draft fork PRs without repository variables and preserve manual profiles."
}

Assert-Contains `
    -Text $workflow `
    -Expected "group: `${{ github.workflow }}-`${{ github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || format('{0}-{1}', github.ref, github.sha) }}" `
    -Message "CI concurrency must group PR runs by PR number and push/tag runs by ref and SHA."
Assert-Contains `
    -Text $workflow `
    -Expected "cancel-in-progress: `${{ github.event_name == 'pull_request' }}" `
    -Message "CI cancellation must apply only to pull_request runs."

$classificationJob = Get-JobBlock "change-classification"
$classificationGuard = [regex]::Match(
    $classificationJob,
    '(?ms)^        if \(\$impact\.classification -notin @\([^\r\n]+\)\) \{\r?\n.*?^        \}')
if (-not $classificationGuard.Success) {
    throw "Classification output must retain its fail-closed admission guard."
}
$guard = [scriptblock]::Create($classificationGuard.Value)
foreach ($classification in @("docs_only", "fast_only", "targeted", "full", "", "unknown")) {
    $impact = [pscustomobject]@{ classification = $classification }
    $accepted = $true
    try { & $guard } catch { $accepted = $false }
    $expected = $classification -in @("docs_only", "fast_only", "targeted", "full")
    if ($accepted -ne $expected) {
        throw "Workflow classification guard expected '$classification' accepted=$expected."
    }
}
foreach ($token in @(
        "fetch-depth: 0",
        "./scripts/Get-CiChangeClassification.ps1",
        "classification: `${{ steps.classify.outputs.classification }}",
        "core_tests: `${{ steps.classify.outputs.core_tests }}",
        "tray_tests: `${{ steps.classify.outputs.tray_tests }}",
        "ui_tests: `${{ steps.classify.outputs.ui_tests }}",
        "setup_e2e: `${{ steps.classify.outputs.setup_e2e }}",
        "revocation_e2e: `${{ steps.classify.outputs.revocation_e2e }}",
        "network_e2e: `${{ steps.classify.outputs.network_e2e }}",
        "x64_release: `${{ steps.classify.outputs.x64_release }}",
        "arm64_release: `${{ steps.classify.outputs.arm64_release }}",
        "full: `${{ steps.classify.outputs.full }}"
    )) {
    Assert-Contains `
        -Text $classificationJob `
        -Expected $token `
        -Message "Change classification job is missing '$token'."
}

$fastValidationJob = Get-JobBlock "fast-validation"
$triageStep = Get-StepBlock -Text $fastValidationJob -Name "Test repository triage automation and dashboard"
Assert-Contains -Text $triageStep `
    -Expected "run: node --test .github/scripts/repository-triage.test.cjs .github/extensions/openclaw-triage-dashboard/triage-state.test.mjs" `
    -Message "The exact fast-only tooling suite must run in fast-validation."
foreach ($token in @("if:", "continue-on-error:")) {
    Assert-NotContains -Text $triageStep -Unexpected $token `
        -Message "Fast-only tooling tests must not be conditional or tolerate failures."
}
foreach ($token in @(
        "Ensure .squad stays untracked",
        "node --test .github/scripts/repository-triage.test.cjs",
        "./scripts/validate-docs.ps1",
        "./scripts/validate-agent-skills.ps1",
        "./scripts/test-agent-skills-validator.ps1",
        "./scripts/validate-test-timing-patterns.ps1",
        "./scripts/test-proof-evidence.ps1",
        "./scripts/test-agent-readiness.ps1",
        "./scripts/test-ci-change-classifier.ps1",
        "./scripts/test-ci-gate-results.ps1",
        "./scripts/test-ci-workflow-contract.ps1",
        "./scripts/Test-InstallerScriptCompiles.ps1 -RequireCompiler",
        "choco install innosetup -y --no-progress",
        "./scripts/test-stable-correction-release-validator.ps1"
    )) {
    Assert-Contains `
        -Text $fastValidationJob `
        -Expected $token `
        -Message "Always-running fast validation job is missing '$token'."
}

$proofJob = Get-JobBlock "proof-pool-contracts"
foreach ($job in @($fastValidationJob, $proofJob)) {
    $jobHeader = ($job -split '(?m)^    steps:\s*$', 2)[0]
    if ($jobHeader -match '(?m)^    (?:if|needs|continue-on-error):') {
        throw "Fast validation and proof-pool contracts must remain unconditional required jobs."
    }
}
foreach ($token in @(
        "./scripts/Get-CiProofPoolRegressionDecision.ps1",
        "steps.proof_pool_regression.outcome != 'success' || steps.proof_pool_regression.outputs.run != 'false'",
        "./scripts/test-proof-pool-validator.ps1"
    )) {
    Assert-Contains `
        -Text $proofJob `
        -Expected $token `
        -Message "Dedicated proof-pool contract job is missing '$token'."
}
Assert-NotContains `
    -Text $fastValidationJob `
    -Unexpected "./scripts/test-proof-pool-validator.ps1" `
    -Message "The heavyweight malformed proof-contract matrix must not block fast validation."

$testLanes = [ordered]@{
    "core-tests" = @{
        Output = "core_tests"
        Projects = @(
            "tests/OpenClaw.Shared.Tests",
            "tests/OpenClaw.Connection.Tests",
            "tests/OpenClaw.WinNode.Cli.Tests",
            "tests/OpenClaw.FileWorker.Tests"
        )
        Artifact = "test-results-core"
    }
    "tray-tests" = @{
        Output = "tray_tests"
        Projects = @(
            "tests/OpenClaw.Tray.Tests",
            "tests/OpenClaw.SetupEngine.Tests",
            "tests/OpenClaw.Tray.IntegrationTests"
        )
        Artifact = "test-results-tray"
    }
    "ui-tests" = @{
        Output = "ui_tests"
        Projects = @(
            "tests/OpenClaw.Tray.UITests",
            "tests/OpenClawTray.FunctionalUI.Tests"
        )
        Artifact = "test-results-ui"
    }
    "tray-ui-shard" = @{
        Output = "ui_tests"
        Projects = @(
            "tests/OpenClaw.Tray.UITests"
        )
        Artifact = "test-results-tray-ui-shard"
    }
}
foreach ($lane in $testLanes.GetEnumerator()) {
    $job = Get-JobBlock $lane.Key
    Assert-Contains `
        -Text $job `
        -Expected "needs.change-classification.outputs.$($lane.Value.Output) == 'true'" `
        -Message "Test lane '$($lane.Key)' does not consume its classifier output."
    Assert-Contains `
        -Text $job `
        -Expected "fetch-depth: 0" `
        -Message "Test lane '$($lane.Key)' must fetch full history for GitVersion.MsBuild."
    Assert-Contains `
        -Text $job `
        -Expected "OPENCLAW_CI_COLLECT_COVERAGE: `${{ github.event_name != 'pull_request' }}" `
        -Message "Test lane '$($lane.Key)' does not preserve push/tag coverage."
    Assert-Contains `
        -Text $job `
        -Expected "name: $($lane.Value.Artifact)" `
        -Message "Test lane '$($lane.Key)' is missing its TRX artifact."
    foreach ($project in $lane.Value.Projects) {
        Assert-Contains `
            -Text $job `
            -Expected $project `
            -Message "Test lane '$($lane.Key)' lost project '$project'."
    }
}

$uiJob = Get-JobBlock "ui-tests"
$trayUiShardJob = Get-JobBlock "tray-ui-shard"
foreach ($standardUiStep in @(
        (Get-StepBlock -Text $uiJob -Name "Run Tray UI Tests"),
        (Get-StepBlock -Text $trayUiShardJob -Name "Run Tray UI Tests"))) {
    foreach ($token in @(
            "OPENCLAW_UI_TEST_RESOURCE_PROFILE: Standard",
            "Category!=ProductionResources",
            "-CaptureHostExit"
        )) {
        Assert-Contains -Text $standardUiStep -Expected $token `
            -Message "Mixed UI tests must stay separate from the production-resource lane: '$token'."
    }
}
$productionUiStep = Get-StepBlock -Text $uiJob -Name "Run steady-resource Automations UI tests"
Assert-Contains -Text $productionUiStep `
    -Expected "if: `${{ !cancelled() && steps.ui-build.outcome == 'success' && steps.ui-runtime.outcome == 'success' && steps.ui-desktop.outcome == 'success' }}" `
    -Message "Independent Automations proof must run after unrelated UI failures when all launch prerequisites succeeded."
Assert-NotContains -Text $productionUiStep -Unexpected 'continue-on-error:' `
    -Message "Automations proof failures must continue to fail the UI job."
foreach ($token in @(
        "OPENCLAW_UI_TEST_RESOURCE_PROFILE: Production",
        "OPENCLAW_UI_TEST_APP_THEME: Light",
        "timeout-minutes: 15",
        "-HangTimeoutSeconds 300",
        "-CaptureHostExit",
        "OPENCLAW_RUN_KEYBOARD_PROOF: '1'",
        "OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS: '1'",
        'OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS: ${{ github.workspace }}\TestResults\AutomationsSteadyResources\captures',
        '-RequireAllTestsPassed',
        '-Filter "Category=ProductionResources&Category!=AnimationPreferenceEnabled&Category!=AnimationPreferenceDisabled"',
        "AutomationsSteadyResources.trx"
    )) {
    Assert-Contains -Text $productionUiStep -Expected $token `
        -Message "Steady-resource UI proof must run in its own configured test process: '$token'."
}

$overlapLanes = [ordered]@{
    "core-tests" = "core_tests"
    "tray-tests" = "tray_tests"
    "ui-tests" = "ui_tests"
    "tray-ui-shard" = "ui_tests"
    "gateway-fixture" = "ui_tests"
    "gateway-fixture-shard" = "ui_tests"
    "setup-e2e" = "setup_e2e"
    "revocation-e2e" = "revocation_e2e"
    "network-e2e" = "network_e2e"
}
foreach ($lane in $overlapLanes.GetEnumerator()) {
    $job = Get-JobBlock $lane.Key
    $jobHeader = ($job -split '(?m)^    steps:\s*$', 2)[0]
    Assert-Contains -Text $jobHeader -Expected "    needs: change-classification" `
        -Message "Lane '$($lane.Key)' must start after classification without waiting for fast validation."
    Assert-Contains -Text $jobHeader `
        -Expected ("    if: " + '${{ ' + "!cancelled() && needs.change-classification.result == 'success' && needs.change-classification.outputs.$($lane.Value) == 'true' }}") `
        -Message "Lane '$($lane.Key)' must preserve cancellation, classification success, and selected-lane admission."
    Assert-NotContains -Text $jobHeader -Unexpected "fast-validation" `
        -Message "Lane '$($lane.Key)' must overlap fast validation; CI Gate still requires its success."
}

$coreJob = Get-JobBlock "core-tests"
$sharedTestStep = Get-StepBlock -Text $coreJob -Name "Run Shared Tests"
Assert-Contains -Text $sharedTestStep -Expected "timeout-minutes: 15" `
    -Message "Shared tests must have a bounded CI step."
Assert-Contains -Text $sharedTestStep -Expected "-HangTimeoutSeconds 300" `
    -Message "Shared tests must collect the interrupted test sequence on a five-minute hang."
$coreCacheStep = Get-StepBlock -Text $coreJob -Name "Cache NuGet packages"
foreach ($token in @(
        "Configure core NuGet cache",
        "shell: pwsh",
        '"NUGET_PACKAGES=$env:RUNNER_TEMP\openclaw-core-nuget-packages" >> $env:GITHUB_ENV',
        'path: ${{ env.NUGET_PACKAGES }}',
        'nuget-core-${{ runner.os }}-${{ hashFiles(',
        "'global.json'",
        "'NuGet.Config'",
        "'Directory.Build.props'",
        "'Directory.Build.targets'",
        "'src/Directory.Build.props'",
        "'src/Directory.Build.targets'",
        "'tests/Directory.Build.props'",
        "'tests/OpenClaw.TestSupport/Directory.Build.props'",
        "'tests/OpenClaw.Shared.TestHost/Directory.Build.props'",
        "'src/OpenClaw.Shared/OpenClaw.Shared.csproj'",
        "'src/OpenClaw.Connection/OpenClaw.Connection.csproj'",
        "'src/OpenClaw.Cli/OpenClaw.Cli.csproj'",
        "'src/OpenClaw.WinNode.Cli/OpenClaw.WinNode.Cli.csproj'",
        "'src/OpenClaw.FileWorker/OpenClaw.FileWorker.csproj'",
        "'tests/OpenClaw.TestSupport/OpenClaw.TestSupport.csproj'",
        "'tests/OpenClaw.Shared.TestHost/OpenClaw.Shared.TestHost.csproj'",
        "'tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj'",
        "'tests/OpenClaw.Connection.Tests/OpenClaw.Connection.Tests.csproj'",
        "'tests/OpenClaw.WinNode.Cli.Tests/OpenClaw.WinNode.Cli.Tests.csproj'",
        "'tests/OpenClaw.FileWorker.Tests/OpenClaw.FileWorker.Tests.csproj'"
    )) {
    Assert-Contains -Text $coreJob -Expected $token -Message "Core NuGet cache is missing '$token'."
}
foreach ($token in @(
        "path: ~/.nuget/packages",
        'key: nuget-${{ runner.os }}-',
        'restore-keys: nuget-${{ runner.os }}-',
        "'**/*.csproj'",
        "'**/Directory.Packages.props'",
        "bin/",
        "obj/",
        "TestResults",
        "node_modules",
        ".dotnet"
    )) {
    Assert-NotContains -Text $coreCacheStep -Unexpected $token -Message "Core NuGet cache must not contain '$token'."
}

$coreJobIndex = $workflow.IndexOf($coreJob, [StringComparison]::Ordinal)
if ($coreJobIndex -lt 0) {
    throw "Could not isolate the core job from the workflow."
}
$nonCoreWorkflow = $workflow.Remove($coreJobIndex, $coreJob.Length)
foreach ($token in @(
        "path: ~/.nuget/packages",
        'key: nuget-${{ runner.os }}-${{ hashFiles(''**/*.csproj'', ''**/Directory.Packages.props'') }}',
        'restore-keys: nuget-${{ runner.os }}-'
    )) {
    $count = ([regex]::Matches($nonCoreWorkflow, [regex]::Escape($token))).Count
    if ($count -ne 11) {
        throw "Expected 11 non-core cache entries for '$token', found $count."
    }
}
Assert-NotContains `
    -Text $nonCoreWorkflow `
    -Unexpected "NUGET_PACKAGES" `
    -Message "Only the core job may override NUGET_PACKAGES."
Assert-NotContains `
    -Text $nonCoreWorkflow `
    -Unexpected "nuget-core-" `
    -Message "Only the core job may use the isolated NuGet cache key."

# Shared-key consumers must never register a post-job cache save. A duplicate
# writer can spend minutes archiving packages before losing the reservation.
foreach ($lane in @("tray-tests", "ui-tests", "tray-ui-shard", "gateway-fixture", "gateway-fixture-shard", "setup-e2e",
        "revocation-e2e", "network-e2e", "build-x64", "build-arm64", "build-msix")) {
    $cacheStep = Get-StepBlock -Text (Get-JobBlock $lane) -Name "Cache NuGet packages"
    $action = if ($lane -eq "tray-tests") { "actions/cache@v6" } else { "actions/cache/restore@v6" }
    Assert-Contains -Text $cacheStep -Expected "uses: $action" `
        -Message "Shared NuGet cache lane '$lane' must use '$action'."
    Assert-Contains -Text $cacheStep -Expected "continue-on-error: true" `
        -Message "Cache availability must not gate '$lane'."
}

$trayJob = Get-JobBlock "tray-tests"
foreach ($token in @(
        "OPENCLAW_TRAY_DATA_DIR:",
        "OPENCLAW_RUN_INTEGRATION: 1",
        "-Project tests/OpenClaw.Tray.IntegrationTests",
        "dotnet restore src/OpenClaw.Tray.WinUI -r win-x64",
        "dotnet build src/OpenClaw.Tray.WinUI -c Debug -r win-x64 --no-restore"
    )) {
    Assert-Contains -Text $trayJob -Expected $token -Message "Tray lane is missing '$token'."
}

$devSettingsStep = Get-StepBlock -Text $trayJob -Name "Run Dev containment settings tests"
foreach ($token in @(
        "dotnet test tests/OpenClaw.Tray.Tests",
        "-p:DevBuild=true",
        "FullyQualifiedName~SettingsManagerIsolationTests",
        "FullyQualifiedName~AppRefactorContractTests.SandboxPage_",
        "OpenClaw.Tray.DevSettings.Tests.trx"
    )) {
    Assert-Contains -Text $devSettingsStep -Expected $token -Message "Dev settings coverage is missing '$token'."
}
Assert-NotContains -Text $devSettingsStep -Unexpected "--no-build" `
    -Message "Dev settings tests must recompile the linked AppIdentity source with DEV_BUILD."

$uiJob = Get-JobBlock "ui-tests"
foreach ($token in @(
        "Install WindowsAppRuntime",
        "--filter Category=Accessibility",
        "Verify DevBuild identity marker"
    )) {
    Assert-Contains -Text $uiJob -Expected $token -Message "UI lane is missing '$token'."
}

$trayUiStep = Get-StepBlock -Text $uiJob -Name "Run Tray UI Tests"
$trayUiShardStep = Get-StepBlock -Text $trayUiShardJob -Name "Run Tray UI Tests"
# The ordinary Tray UI suite runs as two parallel shards. Both start with the same lane
# exclusions; ui-tests negates exactly the classes tray-ui-shard selects, so the union is
# the unsharded suite with no test run twice.
$trayUiBaseFilter = 'Category!=Accessibility&Category!=NativeOnboardingProof&Category!=GatewayFixture&Category!=ProductionResources&Category!=AnimationPreferenceEnabled&Category!=AnimationPreferenceDisabled'
function Get-StepTestFilter {
    param([Parameter(Mandatory)][string]$Step)
    $filterMatches = [regex]::Matches($Step, '-Filter "(?<filter>[^"]+)"')
    if ($filterMatches.Count -ne 1) { throw 'Each Tray UI shard must declare exactly one test filter.' }
    return $filterMatches[0].Groups['filter'].Value
}
$shardClassPattern = '^OpenClaw\.Tray\.UITests\.(?<class>[A-Za-z_][A-Za-z0-9_]*)\.$'
$primaryFilter = Get-StepTestFilter $trayUiStep
$shardFilter = Get-StepTestFilter $trayUiShardStep
if (-not $primaryFilter.StartsWith("$trayUiBaseFilter&", [StringComparison]::Ordinal) -or
    -not $shardFilter.StartsWith("$trayUiBaseFilter&(", [StringComparison]::Ordinal) -or
    -not $shardFilter.EndsWith(')', [StringComparison]::Ordinal)) {
    throw "Tray UI shards must both exclude cases owned by the accessibility, native-onboarding, Gateway fixture and animation-preference lanes."
}
$excludedClasses = @($primaryFilter.Substring($trayUiBaseFilter.Length + 1).Split('&') | ForEach-Object {
        if ($_ -notmatch '^FullyQualifiedName!~(?<prefix>.+)$' -or $Matches.prefix -notmatch $shardClassPattern) {
            throw "ui-tests may only add class exclusions to the Tray UI filter: '$_'."
        }
        $Matches.class
    })
$shardSelection = $shardFilter.Substring($trayUiBaseFilter.Length + 2, $shardFilter.Length - $trayUiBaseFilter.Length - 3)
$selectedClasses = @($shardSelection.Split('|') | ForEach-Object {
        if ($_ -notmatch '^FullyQualifiedName~(?<prefix>.+)$' -or $Matches.prefix -notmatch $shardClassPattern) {
            throw "tray-ui-shard may only select whole Tray UI test classes: '$_'."
        }
        $Matches.class
    })
if ($selectedClasses.Count -eq 0 -or
    @($selectedClasses | Sort-Object -Unique).Count -ne $selectedClasses.Count -or
    (Compare-Object -ReferenceObject @($selectedClasses | Sort-Object) -DifferenceObject @($excludedClasses | Sort-Object) -CaseSensitive)) {
    throw "ui-tests must exclude exactly the Tray UI classes tray-ui-shard selects. Selected: $($selectedClasses -join ', '); excluded: $($excludedClasses -join ', ')."
}
$uiTestSources = Get-ChildItem -LiteralPath (Join-Path $repoRootPath 'tests\OpenClaw.Tray.UITests') -Filter '*.cs' -File |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
foreach ($class in $selectedClasses) {
    if (-not ($uiTestSources | Where-Object { $_ -match "(?m)^namespace OpenClaw\.Tray\.UITests;" -and $_ -match "\bclass $class\b" })) {
        throw "Tray UI shard class '$class' does not exist in OpenClaw.Tray.UITests."
    }
}
foreach ($step in @($trayUiStep, $trayUiShardStep)) {
    Assert-NotContains -Text $step -Unexpected '-AnimationPreference ' `
        -Message 'The ordinary UI suite must retain its original Windows animation preference.'
}
Assert-NotContains -Text $trayUiShardJob -Unexpected '-AnimationPreference ' `
    -Message 'The Tray UI shard must not change the Windows animation preference.'
$enabledAnimationStep = Get-StepBlock -Text $uiJob -Name 'Run animation-enabled reveal proof'
foreach ($token in @(
        './scripts/Invoke-CiTest.ps1',
        '-AnimationPreference Enabled',
        '-RequireAllTestsPassed',
        '-Filter "Category=AnimationPreferenceEnabled"',
        "if: `${{ !cancelled() && steps.ui-build.outcome == 'success' && steps.ui-runtime.outcome == 'success' && steps.ui-desktop.outcome == 'success' }}"
    )) {
    Assert-Contains -Text $enabledAnimationStep -Expected $token -Message "Enabled animation proof lost required contract '$token'."
}
$disabledAnimationStep = Get-StepBlock -Text $uiJob -Name 'Run animations-disabled behavior proof'
foreach ($token in @(
        './scripts/Invoke-CiTest.ps1',
        '-AnimationPreference Disabled',
        '-RequireAllTestsPassed',
        '-Filter "FullyQualifiedName~PageHeroAnimationsDisabledTests"',
        "if: `${{ !cancelled() && steps.ui-build.outcome == 'success' && steps.ui-runtime.outcome == 'success' && steps.ui-desktop.outcome == 'success' }}"
    )) {
    Assert-Contains -Text $disabledAnimationStep -Expected $token -Message "Disabled animation proof lost required contract '$token'."
}
$localAiProofStep = Get-StepBlock -Text $uiJob -Name 'Capture Local AI hardware profiles before and after'
foreach ($token in @(
        './scripts/Invoke-LocalAiVisualProof.ps1',
        'timeout-minutes: 20',
        "if: `${{ !cancelled() && steps.ui-build.outcome == 'success' && steps.ui-runtime.outcome == 'success' && steps.ui-desktop.outcome == 'success' }}"
    )) {
    Assert-Contains -Text $localAiProofStep -Expected $token -Message "Local AI visual proof lost required contract '$token'; an earlier UI test failure must not skip it."
}
Assert-Contains -Text $workflow -Expected './scripts/test-native-animation-preference.ps1' `
    -Message 'Native animation prerequisite failures and restoration must be tested.'
foreach ($shard in @(
        @{ Name = 'ui-tests'; Job = $uiJob; Step = $trayUiStep; Trx = 'OpenClaw.Tray.UITests.trx' },
        @{ Name = 'tray-ui-shard'; Job = $trayUiShardJob; Step = $trayUiShardStep; Trx = 'OpenClaw.Tray.UITests.Shard.trx' })) {
    $trayUiJob = $shard.Job
    $shardStep = $shard.Step
    Assert-Contains -Text $shardStep -Expected "timeout-minutes: 25" `
        -Message "Tray UI tests in '$($shard.Name)' must have an outer timeout with headroom over their shard runtime rather than consuming the six-hour job limit."
    Assert-Contains -Text $shardStep -Expected "-HangTimeoutSeconds 300" `
        -Message "Tray UI tests in '$($shard.Name)' must collect the interrupted test sequence on a five-minute hang."
    Assert-Contains -Text $shardStep -Expected "-CollectNativeFailureMetadata" `
        -Message "Tray UI failures in '$($shard.Name)' must retain safe native fault metadata."
    Assert-Contains -Text $shardStep -Expected "-TrxFileName $($shard.Trx)" `
        -Message "Tray UI shard '$($shard.Name)' must keep a distinct TRX name."
    $trayUiCrashWindow = Get-StepBlock -Text $trayUiJob -Name "Mark Tray UI crash metadata window"
    Assert-Contains -Text $trayUiCrashWindow -Expected "GITHUB_OUTPUT" `
        -Message "Tray UI crash metadata in '$($shard.Name)' must be bounded by an explicit start time."
    Assert-NotContains -Text $trayUiJob -Unexpected 'path: .ci-pipeline' `
        -Message 'Nested pipeline checkout must not enter the product source manifest.'
    $trayUiCrashMetadata = Get-StepBlock -Text $trayUiJob -Name "Capture non-memory Tray UI crash metadata"
    foreach ($token in @(
            "if: always()",
            "steps.tray-ui-crash-window.outcome == 'success'",
            'git show "${env:GITHUB_SHA}:scripts/Capture-CiTestHostCrashMetadata.ps1"',
            '$LASTEXITCODE -ne 0',
            'Join-Path $env:RUNNER_TEMP',
            '& $helperPath -StartedUtc $env:TRAY_UI_TEST_STARTED_UTC',
            "TRAY_UI_TEST_STARTED_UTC"
        )) {
        Assert-Contains -Text $trayUiCrashMetadata -Expected $token `
            -Message "Tray UI crash metadata capture in '$($shard.Name)' is missing '$token'."
    }
    if ($trayUiJob.IndexOf('name: Mark Tray UI crash metadata window', [StringComparison]::Ordinal) -ge
        $trayUiJob.IndexOf('name: Run Tray UI Tests', [StringComparison]::Ordinal) -or
        $trayUiJob.IndexOf('name: Run Tray UI Tests', [StringComparison]::Ordinal) -ge
        $trayUiJob.IndexOf('name: Capture non-memory Tray UI crash metadata', [StringComparison]::Ordinal)) {
        throw "Tray UI crash metadata in '$($shard.Name)' must bracket only the Tray UI test run."
    }
    $trayUiUpload = Get-StepBlock -Text $trayUiJob -Name 'Upload Test Results'
    foreach ($token in @('always()', 'path: TestResults/')) {
        Assert-Contains -Text $trayUiUpload -Expected $token `
            -Message "Tray UI shard '$($shard.Name)' must retain its complete diagnostic archive: '$token'."
    }
    if ($trayUiJob.IndexOf('name: Configure UI desktop', [StringComparison]::Ordinal) -ge
        $trayUiJob.IndexOf('name: Run Tray UI Tests', [StringComparison]::Ordinal)) {
        throw "Hosted desktop geometry must be configured before '$($shard.Name)' runs Tray UI tests."
    }
}
$crashMetadataScript = Get-Content -LiteralPath (Join-Path $repoRootPath 'scripts\Capture-CiTestHostCrashMetadata.ps1') -Raw
foreach ($token in @(
        'LogName = "Application"',
        'Id = 1000',
        'ExceptionCode',
        'FaultingOffset',
        'ModuleName',
        'Get-WinEvent'
    )) {
    Assert-Contains -Text $crashMetadataScript -Expected $token `
        -Message "Non-memory Tray UI crash metadata must include '$token'."
}
foreach ($token in @('LocalDumps', 'MiniDumpWriteDump', 'Get-WinEvent -LogName "Security"')) {
    Assert-NotContains -Text $crashMetadataScript -Unexpected $token `
        -Message "Tray UI crash metadata must not collect memory dumps or unrelated event logs: '$token'."
}
. (Join-Path $repoRootPath 'scripts\Capture-CiTestHostCrashMetadata.ps1')
$syntheticCrashEvent = @'
<Event><EventData>
  <Data Name="AppName">testhost.exe</Data>
  <Data Name="AppPath">C:\runner\temp\testhost.exe</Data>
  <Data Name="ModuleName">C:\Windows\System32\KERNELBASE.dll</Data>
  <Data Name="ExceptionCode">0xc0000005</Data>
  <Data Name="FaultingOffset">0x0000000000001234</Data>
  <Data Name="Message">must-not-be-emitted</Data>
</EventData></Event>
'@
$syntheticMetadata = ConvertTo-CiTestHostCrashMetadata `
    -EventXml $syntheticCrashEvent `
    -EventId 1000 `
    -TimeCreated ([DateTimeOffset]::UtcNow)
if ($syntheticMetadata.application -ne 'testhost.exe' -or
    $syntheticMetadata.faultingModule -ne 'KERNELBASE.dll' -or
    $syntheticMetadata.exceptionCode -ne '0xc0000005' -or
    $syntheticMetadata.faultingOffset -ne '0x0000000000001234' -or
    $syntheticMetadata.PSObject.Properties.Name -contains 'Message' -or
    $syntheticMetadata.PSObject.Properties.Name -contains 'path') {
    throw 'Tray UI crash metadata must retain only the whitelisted process-failure fields.'
}
$syntheticNonTestEvent = @'
<Event><EventData>
  <Data Name="AppName">notepad.exe</Data>
  <Data Name="ModuleName">C:\Windows\System32\KERNELBASE.dll</Data>
  <Data Name="ExceptionCode">0xc0000005</Data>
</EventData></Event>
'@
if ($null -ne (ConvertTo-CiTestHostCrashMetadata `
            -EventXml $syntheticNonTestEvent `
            -EventId 1000 `
            -TimeCreated ([DateTimeOffset]::UtcNow))) {
    throw 'Tray UI crash metadata must exclude unrelated application failures.'
}

$fixtureJob = Get-JobBlock "gateway-fixture"
$fixtureShardJob = Get-JobBlock "gateway-fixture-shard"
$fixtureJobs = @(
    @{ Name = 'gateway-fixture'; Job = $fixtureJob; Shard = 'A'; Archive = 'test-results-gateway-fixture' },
    @{ Name = 'gateway-fixture-shard'; Job = $fixtureShardJob; Shard = 'B'; Archive = 'test-results-gateway-fixture-shard' })
$releaseFixtureBuild = Get-StepBlock -Text $fixtureJob -Name 'Build Release correlation fixture'
foreach ($token in @('dotnet build tests/OpenClaw.Tray.UITests -c Release -r win-x64 --no-restore -p:DevBuild=false -p:RequireSignedCleanupHost=false',
        'if ($LASTEXITCODE -ne 0)', 'TestResults\GatewayFixtureRelease\compiled',
        'win-x64\OpenClaw.Tray.WinUI.dll')) {
    Assert-Contains -Text $releaseFixtureBuild -Expected $token -Message "Release fixture build lost '$token'."
}
$releaseFixtureStep = Get-StepBlock -Text $fixtureJob -Name 'Run Release correlation fixture'
foreach ($token in @("steps.release-fixture-build.outcome == 'success'",
        "steps.ui-runtime.outcome == 'success'", "steps.ui-desktop.outcome == 'success'",
        'timeout-minutes: 15', "OPENCLAW_RUN_GATEWAY_FIXTURE_UI: '1'",
        "OPENCLAW_RUN_KEYBOARD_PROOF: '0'", "OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS: '1'",
        '\bin\Release\net10.0-windows10.0.22621.0\win-x64\OpenClaw.Tray.WinUI.exe',
        '\TestResults\GatewayFixtureRelease\captures', './scripts/Invoke-CiTest.ps1',
        '-Configuration Release', '-ResultsDirectory TestResults\GatewayFixtureRelease',
        '-TrxFileName GatewayFixtureRelease.trx', '-Runtime win-x64',
        '-Filter "FullyQualifiedName~GatewayFixtureUiTests.CompletedRunLateSnapshotLeavesNativeComposerReadyAndQueueDraining"',
        '-ExpectedTestCount 2', '-RequireAllTestsPassed', '-HangTimeoutSeconds 300',
        '-CollectNativeFailureMetadata', '-CaptureHostExit')) {
    Assert-Contains -Text $releaseFixtureStep -Expected $token -Message "Targeted Release fixture lost '$token'."
}
Assert-NotContains -Text $releaseFixtureStep -Unexpected 'continue-on-error:' -Message 'Release correlation proof must fail closed.'
$releaseReaderStep = Get-StepBlock -Text $fixtureJob -Name 'Run Release reader layout proof'
foreach ($token in @("steps.release-fixture-build.outcome == 'success'",
        "steps.ui-runtime.outcome == 'success'", "steps.ui-desktop.outcome == 'success'",
        'timeout-minutes: 5', 'OPENCLAW_UI_TEST_RESOURCE_PROFILE: Standard',
        '\TestResults\ReaderRelease\observations', './scripts/Invoke-CiTest.ps1',
        '-Configuration Release', '-ResultsDirectory TestResults\ReaderRelease',
        '-TrxFileName ReaderRelease.trx', '-Runtime win-x64',
        '-Filter "FullyQualifiedName~ReactorChatLayoutProofTests.ConversationFeedback_ThinkingToolReviewsAndHistoryRetainComposerWithoutSearch"',
        '-ExpectedTestCount 2', '-RequireAllTestsPassed', '-HangTimeoutSeconds 120',
        '-CollectNativeFailureMetadata', '-CaptureHostExit')) {
    Assert-Contains -Text $releaseReaderStep -Expected $token -Message "Targeted Release reader proof lost '$token'."
}
foreach ($token in @('continue-on-error:', 'OPENCLAW_VISUAL_TEST:', 'OPENCLAW_NATIVE_COMPOSITOR_CAPTURE:',
        'OPENCLAW_GATEWAY_FIXTURE_APP:', '-AnimationPreference')) {
    Assert-NotContains -Text $releaseReaderStep -Unexpected $token -Message "Release reader proof must preserve the mounted test path: '$token'."
}
$fixtureStep = Get-StepBlock -Text $fixtureJob -Name "Run mock Gateway application scenarios"
foreach ($fixture in $fixtureJobs) {
    $shardStep = Get-StepBlock -Text $fixture.Job -Name "Run mock Gateway application scenarios"
    # Each shard selects its scenarios through the step environment, which an older candidate's
    # runner ignores during alpha promotion. Each job then runs the full suite, so the step keeps
    # the full-suite budget.
    foreach ($token in @(
            'timeout-minutes: 30',
            "OPENCLAW_CI_STEP_TIMEOUT_MINUTES: '30'",
            "OPENCLAW_GATEWAY_FIXTURE_UI_SHARD: $($fixture.Shard)",
            './scripts/test-gateway-fixture.ps1',
            '-AppPath .\src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\win-x64\OpenClaw.Tray.WinUI.exe',
            '-ArtifactsDirectory .\TestResults\GatewayFixture',
            '-TestTimeoutSeconds 1500',
            '-Screenshots',
            '-NativeInputProof',
            '-NoBuild',
            'id: gateway-fixture',
            "if: `${{ !cancelled() && steps.ui-build.outcome == 'success' && steps.ui-runtime.outcome == 'success' && steps.ui-desktop.outcome == 'success' }}"
        )) {
        Assert-Contains -Text $shardStep -Expected $token -Message "Mock Gateway CI step in '$($fixture.Name)' is missing '$token'."
    }
    foreach ($token in @('continue-on-error:', 'OPENCLAW_RUN_MXC_E2E', 'OPENCLAW_RUN_E2E')) {
        Assert-NotContains -Text $shardStep -Unexpected $token -Message "Mock Gateway CI in '$($fixture.Name)' must run by default without real Gateway opt-ins: '$token'."
    }
}
if ($workflow -match '-UiShard\b') { throw 'CI must select fixture UI shards through OPENCLAW_GATEWAY_FIXTURE_UI_SHARD, not -UiShard.' }
if ([regex]::Matches($workflow, '(?m)^\s+OPENCLAW_GATEWAY_FIXTURE_UI_SHARD:').Count -ne 2) {
    throw 'Exactly the two fixture jobs may select a fixture UI shard.'
}
$accessibilityStep = Get-StepBlock -Text $uiJob -Name "Run Accessibility Tests (Axe.Windows)"
Assert-Contains -Text $accessibilityStep `
    -Expected "if: `${{ !cancelled() && steps.ui-build.outcome == 'success' && steps.ui-runtime.outcome == 'success' && steps.ui-desktop.outcome == 'success' }}" `
    -Message "Accessibility scans must run after fixture failures when their build, runtime, and desktop prerequisites succeeded."
foreach ($token in @('continue-on-error:', 'OPENCLAW_RUN_MXC_E2E', 'OPENCLAW_RUN_E2E')) {
    Assert-NotContains -Text $fixtureStep -Unexpected $token -Message "Mock Gateway CI must run by default without real Gateway opt-ins: '$token'."
}
Assert-Contains -Text $fixtureJob -Expected 'runs-on: windows-latest' `
    -Message "Mock Gateway application scenarios must use the ordinary Windows hosted runner."
if ($fixtureJob.IndexOf('name: Install WindowsAppRuntime', [StringComparison]::Ordinal) -ge
    $fixtureJob.IndexOf('name: Run mock Gateway application scenarios', [StringComparison]::Ordinal)) {
    throw 'Mock Gateway scenarios must run after matching WindowsAppRuntime installation.'
}
foreach ($token in @('DevBuild=true', 'Run Tray UI Tests', 'Run Accessibility Tests')) {
    Assert-NotContains -Text $fixtureJob -Unexpected $token `
        -Message "The fixture job must keep its non-Dev identity and not duplicate native UI tests: '$token'."
}
Assert-NotContains -Text $uiJob -Unexpected './scripts/test-gateway-fixture.ps1' `
    -Message 'Fixture scenarios must not remain on the serial native UI path.'
Assert-Contains -Text $fixtureJob -Expected 'uses: actions/cache/restore@v6' `
    -Message 'The new fixture runner must not add another shared NuGet cache writer.'
$fixtureRestore = Get-StepBlock -Text $fixtureJob -Name 'Restore UI test projects'
$fixtureBootstrapIndex = $fixtureRestore.IndexOf('./scripts/Initialize-ReactorPackages.ps1', [StringComparison]::Ordinal)
$fixtureRestoreIndex = $fixtureRestore.IndexOf('dotnet restore tests/OpenClaw.Tray.UITests', [StringComparison]::Ordinal)
if ($fixtureBootstrapIndex -lt 0 -or $fixtureRestoreIndex -lt 0 -or
    $fixtureBootstrapIndex -ge $fixtureRestoreIndex) {
    throw 'The fixture runner must initialize the verified local Reactor feed before restoring UI tests.'
}
Assert-Contains -Text $fixtureRestore -Expected "`$ErrorActionPreference = 'Stop'" `
    -Message 'The fixture restore must stop if Reactor package initialization fails.'
foreach ($name in @('Install WindowsAppRuntime', 'Configure UI desktop')) {
    foreach ($otherJob in @($fixtureJob, $fixtureShardJob, $trayUiShardJob)) {
        if ((Get-StepBlock -Text $uiJob -Name $name).Trim() -cne
            (Get-StepBlock -Text $otherJob -Name $name).Trim()) {
            throw "Native UI, Tray UI shard and fixture lanes must retain equivalent runtime/desktop preparation: '$name'."
        }
    }
}
Assert-Contains -Text $fixtureJob -Expected 'dotnet build tests/OpenClaw.Tray.UITests -c Debug -r win-x64 --no-restore' `
    -Message 'The fixture lane must build the current candidate UI test graph.'
Assert-Contains -Text $fixtureRestore -Expected 'dotnet restore tests/OpenClaw.Tray.IntegrationTests -r win-x64' `
    -Message 'The prebuilt fixture lane must restore its profile and compatibility test project.'
Assert-Contains -Text $fixtureJob -Expected 'dotnet build tests/OpenClaw.Tray.IntegrationTests -c Debug -r win-x64 --no-restore' `
    -Message 'The prebuilt fixture lane must build profile and compatibility tests before invoking the runner.'
Assert-Contains -Text $fixtureJob -Expected "ref: `${{ inputs.promotion_source_sha || github.sha }}" `
    -Message 'The fixture lane must use the exact candidate checkout, including alpha promotion.'
Assert-Contains -Text $fixtureJob -Expected '*promotion-version-tag' `
    -Message 'Fixture candidate versioning must preserve the promotion tag setup.'
$runtimeStep = Get-StepBlock -Text $uiJob -Name 'Install WindowsAppRuntime'
foreach ($token in @('Directory.Build.props', 'MicrosoftWindowsAppSDKVersion', '$channel/$version/windowsappruntimeinstall-x64.exe', '$LASTEXITCODE -ne 0')) {
    Assert-Contains -Text $runtimeStep -Expected $token -Message "UI runtime installation must match the candidate SDK and fail closed: '$token'."
}
$desktopStep = Get-StepBlock -Text $uiJob -Name 'Configure UI desktop'
Assert-Contains -Text $desktopStep -Expected './scripts/Set-CiDesktopResolution.ps1' `
    -Message 'Hosted UI proof must configure and verify the desktop before launching tests.'
Assert-NotContains -Text $desktopStep -Unexpected 'continue-on-error:' `
    -Message 'Unsupported hosted desktop geometry must fail explicitly.'
if ($uiJob.IndexOf('name: Configure UI desktop', [StringComparison]::Ordinal) -ge
    $uiJob.IndexOf('name: Run Functional UI Tests', [StringComparison]::Ordinal)) {
    throw 'Hosted desktop geometry must be configured before any UI tests run.'
}
Assert-Contains -Text $fixtureStep `
    -Expected "if: `${{ !cancelled() && steps.ui-build.outcome == 'success' && steps.ui-runtime.outcome == 'success' && steps.ui-desktop.outcome == 'success' }}" `
    -Message 'Fixture evidence must run after unrelated test failures, but require every launch prerequisite.'
foreach ($entry in @(
    @{ name = 'Build UI test projects'; id = 'ui-build' },
    @{ name = 'Install WindowsAppRuntime'; id = 'ui-runtime' },
    @{ name = 'Configure UI desktop'; id = 'ui-desktop' }
)) {
    Assert-Contains -Text (Get-StepBlock -Text $fixtureJob -Name $entry.name) -Expected "id: $($entry.id)" `
        -Message "Fixture launch prerequisite must expose its real step outcome: '$($entry.id)'."
}
$screenshotPreparation = Get-StepBlock -Text $fixtureJob -Name 'Prepare mock Gateway screenshot publisher'
foreach ($token in @('always()', "steps.gateway-fixture.outcome == 'failure'", 'npm ci', '--ignore-scripts')) {
    Assert-Contains -Text $screenshotPreparation -Expected $token -Message "Screenshot uploader preparation must run after failed fixtures and use locked dependencies: '$token'."
}
Assert-NotContains -Text $screenshotPreparation -Unexpected "steps.gateway-fixture.outcome == 'success'" `
    -Message 'Successful fixture runs must keep screenshots only in the complete diagnostic archive.'
$screenshotPublication = Get-StepBlock -Text $fixtureJob -Name 'Publish browsable mock Gateway screenshots'
foreach ($token in @('always()', "steps.screenshot-publisher.outcome == 'success'", 'actions/github-script@v9', 'await report.run({ core, context })')) {
    Assert-Contains -Text $screenshotPublication -Expected $token -Message "Screenshot previews must publish after test failures through the Actions artifact runtime: '$token'."
}
Assert-NotContains -Text $screenshotPublication -Unexpected 'continue-on-error:' `
    -Message 'Screenshot publication failures must remain visible failures.'
$uiUpload = Get-StepBlock -Text $uiJob -Name 'Upload Test Results'
foreach ($token in @('always()', 'test-results-ui', 'path: TestResults/')) {
    Assert-Contains -Text $uiUpload -Expected $token -Message "The complete UI diagnostic archive must be retained: '$token'."
}
$fixtureUpload = Get-StepBlock -Text $fixtureJob -Name 'Upload Test Results'
foreach ($token in @('always()', 'test-results-gateway-fixture', 'path: TestResults/')) {
    Assert-Contains -Text $fixtureUpload -Expected $token `
        -Message "The independent fixture diagnostic archive must be retained: '$token'."
}
# The fixture shard is a full, independent lane: same prerequisites, isolation and evidence.
$shardRestore = Get-StepBlock -Text $fixtureShardJob -Name 'Restore UI test projects'
$shardBootstrapIndex = $shardRestore.IndexOf('./scripts/Initialize-ReactorPackages.ps1', [StringComparison]::Ordinal)
$shardRestoreIndex = $shardRestore.IndexOf('dotnet restore tests/OpenClaw.Tray.UITests -r win-x64', [StringComparison]::Ordinal)
if ($shardBootstrapIndex -lt 0 -or $shardRestoreIndex -lt 0 -or $shardBootstrapIndex -ge $shardRestoreIndex -or
    -not $shardRestore.Contains("`$ErrorActionPreference = 'Stop'")) {
    throw 'The fixture shard must initialize the verified local Reactor feed before restoring UI tests.'
}
foreach ($token in @('runs-on: windows-latest', 'uses: actions/cache/restore@v6',
        'dotnet build tests/OpenClaw.Tray.UITests -c Debug -r win-x64 --no-restore',
        "ref: `${{ inputs.promotion_source_sha || github.sha }}", '*promotion-version-tag',
        'OPENCLAW_TRAY_DATA_DIR: ${{ github.workspace }}\TestData\OpenClawTrayFixtureTests',
        'OPENCLAW_TRAY_APPDATA_DIR: ${{ github.workspace }}\TestData\OpenClawTrayFixtureTests-Roaming',
        'OPENCLAW_TRAY_LOCALAPPDATA_DIR: ${{ github.workspace }}\TestData\OpenClawTrayFixtureTests-Local')) {
    Assert-Contains -Text $fixtureShardJob -Expected $token -Message "The fixture shard lane is missing '$token'."
}
# The current shard-B runner does not use the integration tests, but an older candidate's runner
# ignores the shard variable and runs its profile/app checks with -NoBuild during alpha promotion.
Assert-Contains -Text $shardRestore -Expected 'dotnet restore tests/OpenClaw.Tray.IntegrationTests -r win-x64' `
    -Message 'The fixture shard must restore the integration tests that an older candidate runner needs.'
Assert-Contains -Text $fixtureShardJob -Expected 'dotnet build tests/OpenClaw.Tray.IntegrationTests -c Debug -r win-x64 --no-restore' `
    -Message 'The fixture shard must build the integration tests that an older candidate runner needs.'
foreach ($token in @('DevBuild=true', 'Run Tray UI Tests', 'Run Accessibility Tests', 'Run Release', 'Run latest stable profile compatibility')) {
    Assert-NotContains -Text $fixtureShardJob -Unexpected $token `
        -Message "The fixture shard must run only its fixture UI scenarios with the non-Dev identity: '$token'."
}
if ($fixtureShardJob.IndexOf('name: Install WindowsAppRuntime', [StringComparison]::Ordinal) -ge
    $fixtureShardJob.IndexOf('name: Run mock Gateway application scenarios', [StringComparison]::Ordinal)) {
    throw 'Fixture shard scenarios must run after matching WindowsAppRuntime installation.'
}
foreach ($fixture in $fixtureJobs) {
    foreach ($entry in @(
        @{ name = 'Build UI test projects'; id = 'ui-build' },
        @{ name = 'Install WindowsAppRuntime'; id = 'ui-runtime' },
        @{ name = 'Configure UI desktop'; id = 'ui-desktop' }
    )) {
        Assert-Contains -Text (Get-StepBlock -Text $fixture.Job -Name $entry.name) -Expected "id: $($entry.id)" `
            -Message "Fixture launch prerequisite in '$($fixture.Name)' must expose its real step outcome: '$($entry.id)'."
    }
    $upload = Get-StepBlock -Text $fixture.Job -Name 'Upload Test Results'
    foreach ($token in @('always()', "name: $($fixture.Archive)`n", 'path: TestResults/')) {
        Assert-Contains -Text ($upload -replace "`r`n", "`n") -Expected $token `
            -Message "Fixture lane '$($fixture.Name)' must retain its own complete diagnostic archive: '$($token.Trim())'."
    }
    $preparation = Get-StepBlock -Text $fixture.Job -Name 'Prepare mock Gateway screenshot publisher'
    foreach ($token in @('always()', "steps.gateway-fixture.outcome == 'failure'", 'npm ci', '--ignore-scripts')) {
        Assert-Contains -Text $preparation -Expected $token -Message "Screenshot uploader preparation in '$($fixture.Name)' must run after failed fixtures: '$token'."
    }
    $publication = Get-StepBlock -Text $fixture.Job -Name 'Publish browsable mock Gateway screenshots'
    $reportCall = if ($fixture.Shard -eq 'A') { 'await report.run({ core, context })' } else { "await report.run({ core, context, archive: '$($fixture.Archive)' })" }
    foreach ($token in @('always()', "steps.screenshot-publisher.outcome == 'success'", 'actions/github-script@v9', $reportCall)) {
        Assert-Contains -Text $publication -Expected $token -Message "Screenshot previews in '$($fixture.Name)' must publish after test failures and name their archive: '$token'."
    }
    Assert-NotContains -Text $publication -Unexpected 'continue-on-error:' `
        -Message 'Screenshot publication failures must remain visible failures.'
}
$fixtureRunner = Get-Content -LiteralPath (Join-Path $repoRootPath 'scripts\test-gateway-fixture.ps1') -Raw
Assert-NotContains -Text $fixtureRunner -Unexpected '-p:Platform=' `
    -Message 'The fixture runner must reuse the RID-selected canonical outputs instead of rebuilding a separate Platform graph.'
Assert-Contains -Text $fixtureRunner -Expected "if (`$NoBuild) { `$testArguments += @('--no-build', '--no-restore') }" `
    -Message 'Prebuilt fixture execution must not spend its test deadline rebuilding or restoring.'
$releasedProfileStep = Get-StepBlock -Text $fixtureJob -Name 'Run latest stable profile compatibility'
Assert-NotContains -Text $releasedProfileStep -Unexpected '-TestTimeoutSeconds' `
    -Message 'The longer deadline belongs only to the ordinary mock-Gateway scenarios step.'
Assert-Contains -Text $releasedProfileStep -Expected '-NoBuild' `
    -Message 'Released-profile compatibility must reuse the same prebuilt integration tests.'
foreach ($token in @('releases/latest', 'Get-FileHash', 'SHA256', '$asset.digest.Substring(7)',
        'stable-release.json', '-StableAppPath $apps[0].FullName', '-ArtifactsDirectory $artifacts')) {
    Assert-Contains -Text $releasedProfileStep -Expected $token `
        -Message "Released-profile proof must use a verified portable baseline and retain provenance: '$token'."
}
Assert-Contains -Text $fixtureRunner -Expected "'FullyQualifiedName~GatewayFixtureUpgradeTests' 'fixture-released-profile'" `
    -Message 'Released-profile proof must select and admit the actual compatibility scenario.'
foreach ($token in @(
        "`$start.Environment['OPENCLAW_RUN_GATEWAY_FIXTURE_UI'] = '1'",
        "`$start.Environment['OPENCLAW_GATEWAY_FIXTURE_APP'] = `$resolvedApp",
        "(Get-FixtureUiFilter `$UiShard) `$uiResultName -EnableKeyboardProof:`$NativeInputProof",
        "'Category=GatewayFixture&KeyboardProof=Required' 'fixture-keyboard' -EnableKeyboardProof",
        "`$start.Environment['OPENCLAW_RUN_KEYBOARD_PROOF'] = if (`$EnableKeyboardProof) { '1' } else { '0' }",
        '[int]$counters.total -le 0 -or [int]$counters.passed -ne [int]$counters.total'
    )) {
    Assert-Contains -Text $fixtureRunner -Expected $token -Message "Fixture runner lost its explicit-app, category or all-pass contract: '$token'."
}
# Exercise the production child-launch construction without launching dotnet or native input.
$fixtureTokens = $null
$fixtureParseErrors = $null
$fixtureAst = [Management.Automation.Language.Parser]::ParseInput($fixtureRunner, [ref]$fixtureTokens, [ref]$fixtureParseErrors)
if ($fixtureParseErrors.Count -ne 0) { throw 'Fixture runner must parse before its launch contract is checked.' }
# The two fixture jobs partition the fixture UI scenarios. One prefix list drives both filters:
# shard B selects exactly those GatewayFixtureUiTests method-name prefixes and shard A negates
# exactly the same list, so together they select the unsharded filter with no scenario twice.
$partitionParts = @(@($fixtureAst.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
            $_.Left.Extent.Text -in @('$FixtureUiBaseFilter', '$FixtureUiShardBPrefixes')
        }) + @($fixtureAst.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-FixtureUiFilter'
        }, $true)))
if ($partitionParts.Count -ne 3) { throw 'The fixture runner must own one base filter, one shard prefix list and one filter builder.' }
$partition = & ([scriptblock]::Create((($partitionParts | ForEach-Object { $_.Extent.Text }) -join "`n") +
    "`n[pscustomobject]@{ Base = `$FixtureUiBaseFilter; Prefixes = @(`$FixtureUiShardBPrefixes); All = (Get-FixtureUiFilter All); A = (Get-FixtureUiFilter A); B = (Get-FixtureUiFilter B) }"))
$fixtureUiBase = 'Category=GatewayFixture&KeyboardProof!=Required'
$fixtureUiClass = 'OpenClaw.Tray.UITests.GatewayFixtureUiTests.'
if ($partition.Base -cne $fixtureUiBase -or $partition.All -cne $fixtureUiBase) {
    throw 'Unsharded fixture UI runs must keep the original category filter.'
}
$prefixes = @($partition.Prefixes)
if ($prefixes.Count -eq 0 -or @($prefixes | Sort-Object -Unique -CaseSensitive).Count -ne $prefixes.Count -or
    @($prefixes | Where-Object { $_ -cnotmatch '^[A-Z][A-Za-z0-9]*$' }).Count -ne 0) {
    throw 'Fixture shard prefixes must be distinct method-name prefixes.'
}
foreach ($prefix in $prefixes) {
    if (@($prefixes | Where-Object { $_ -cne $prefix -and $_.StartsWith($prefix, [StringComparison]::Ordinal) }).Count -ne 0) {
        throw "Fixture shard prefix '$prefix' already covers another listed prefix."
    }
}
if (-not $partition.A.StartsWith("$fixtureUiBase&", [StringComparison]::Ordinal) -or
    -not $partition.B.StartsWith("$fixtureUiBase&(", [StringComparison]::Ordinal) -or -not $partition.B.EndsWith(')', [StringComparison]::Ordinal)) {
    throw 'Both fixture UI shards must start from the unsharded category filter.'
}
$excludedPrefixes = @($partition.A.Substring($fixtureUiBase.Length + 1).Split('&') | ForEach-Object {
        if (-not $_.StartsWith("FullyQualifiedName!~$fixtureUiClass", [StringComparison]::Ordinal)) { throw "Fixture shard A may only negate listed prefixes: '$_'." }
        $_.Substring("FullyQualifiedName!~$fixtureUiClass".Length)
    })
$selectedPrefixes = @($partition.B.Substring($fixtureUiBase.Length + 2, $partition.B.Length - $fixtureUiBase.Length - 3).Split('|') | ForEach-Object {
        if (-not $_.StartsWith("FullyQualifiedName~$fixtureUiClass", [StringComparison]::Ordinal)) { throw "Fixture shard B may only select listed prefixes: '$_'." }
        $_.Substring("FullyQualifiedName~$fixtureUiClass".Length)
    })
foreach ($side in @($excludedPrefixes, $selectedPrefixes)) {
    if (Compare-Object -ReferenceObject @($prefixes | Sort-Object) -DifferenceObject @($side | Sort-Object) -CaseSensitive) {
        throw "Fixture shards must select and negate exactly the listed prefixes: $($prefixes -join ', ')."
    }
}
$fixtureUiMethods = @(Get-ChildItem -LiteralPath (Join-Path $repoRootPath 'tests\OpenClaw.Tray.UITests') -Filter '*.cs' -File |
    ForEach-Object { Get-Content -LiteralPath $_.FullName } -PipelineVariable sourceLine | ForEach-Object -Begin { $attributes = @() } -Process {
        # Only selected fixture scenarios count: Category=GatewayFixture without KeyboardProof=Required.
        $trimmed = $sourceLine.Trim()
        if ($trimmed.StartsWith('[', [StringComparison]::Ordinal)) { $attributes += $trimmed; return }
        if ($trimmed -match '^public\s+(?:async\s+)?(?:Task|void)\s+(?<name>[A-Z]\w*)\(' -and
            ($attributes -join ' ').Contains('[Trait("Category", "GatewayFixture")]') -and
            -not ($attributes -join ' ').Contains('[Trait("KeyboardProof", "Required")]')) { $Matches.name }
        if ($trimmed) { $attributes = @() }
    })
if ($fixtureUiMethods.Count -lt 20) { throw "Fixture shard proof found only $($fixtureUiMethods.Count) GatewayFixture scenarios; the scan is stale." }
foreach ($prefix in $prefixes) {
    if (-not ($fixtureUiMethods | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) })) {
        throw "Fixture shard prefix '$prefix' no longer selects any GatewayFixtureUiTests scenario."
    }
}
if (-not ($fixtureUiMethods | Where-Object { $method = $_; -not ($prefixes | Where-Object { $method.StartsWith($_, [StringComparison]::Ordinal) }) })) {
    throw 'Fixture shard A must keep at least one scenario.'
}
# Exercise the real parameter binder without resolving or launching an app.
$fixtureTimeoutProbe = [scriptblock]::Create($fixtureAst.ParamBlock.Extent.Text + "`n[int](`$TestTimeoutSeconds * 1000)")
if ((& $fixtureTimeoutProbe -AppPath 'unused-built-app.exe') -ne 900000) {
    throw 'Existing fixture callers must retain the 900-second default.'
}
# Exercise the real shard resolution: the variable selects a shard only when -UiShard is not passed.
$fixtureShardResolution = @($fixtureAst.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains('OPENCLAW_GATEWAY_FIXTURE_UI_SHARD')
    })
if ($fixtureShardResolution.Count -ne 1) { throw 'The fixture runner must resolve OPENCLAW_GATEWAY_FIXTURE_UI_SHARD in one place.' }
$fixtureShardProbe = [scriptblock]::Create($fixtureAst.ParamBlock.Extent.Text + "`n" + $fixtureShardResolution[0].Extent.Text + "`n`$UiShard")
$savedFixtureShard = $env:OPENCLAW_GATEWAY_FIXTURE_UI_SHARD
try {
    foreach ($case in @(
            @{ Env = $null; Arguments = @{}; Expected = 'All' },
            @{ Env = ' '; Arguments = @{}; Expected = 'All' },
            @{ Env = 'B'; Arguments = @{}; Expected = 'B' },
            @{ Env = 'B'; Arguments = @{ UiShard = 'A' }; Expected = 'A' },
            @{ Env = 'C'; Arguments = @{}; Expected = $null })) {
        $env:OPENCLAW_GATEWAY_FIXTURE_UI_SHARD = $case.Env
        $arguments = $case.Arguments
        $resolved = try { & $fixtureShardProbe -AppPath 'unused-built-app.exe' @arguments } catch { $null }
        if ($resolved -cne $case.Expected) {
            throw "Fixture shard for OPENCLAW_GATEWAY_FIXTURE_UI_SHARD='$($case.Env)' and -UiShard '$($case.Arguments.UiShard)' resolved to '$resolved', expected '$($case.Expected)'."
        }
    }
} finally {
    $env:OPENCLAW_GATEWAY_FIXTURE_UI_SHARD = $savedFixtureShard
}
foreach ($case in @(@{ Seconds = 1; Milliseconds = 1000 }, @{ Seconds = 1500; Milliseconds = 1500000 },
        @{ Seconds = 1800; Milliseconds = 1800000 })) {
    if ((& $fixtureTimeoutProbe -AppPath 'unused-built-app.exe' -TestTimeoutSeconds $case.Seconds) -ne $case.Milliseconds) {
        throw "Fixture timeout conversion failed for $($case.Seconds) seconds."
    }
}
foreach ($invalid in @(0, -1, 1801, [int]::MaxValue, ([long][int]::MaxValue + 1), [double]::PositiveInfinity)) {
    try {
        & $fixtureTimeoutProbe -AppPath 'unused-built-app.exe' -TestTimeoutSeconds $invalid | Out-Null
        throw "Fixture timeout accepted invalid or overflowing value: $invalid."
    }
    catch [System.Management.Automation.ParameterBindingException] { }
}
foreach ($token in @('$process.WaitForExit($TestTimeoutSeconds * 1000)', '$process.Kill($true)',
        '$process.ExitCode -ne 0', 'if (-not (Test-Path -LiteralPath $resultFile))')) {
    Assert-Contains -Text $fixtureRunner -Expected $token -Message "Fixture timeout/result admission lost '$token'."
}
$startInfoFunction = @($fixtureAst.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-FixtureTestStartInfo'
}, $true))
if ($startInfoFunction.Count -ne 1) { throw 'Expected one fixture child ProcessStartInfo owner.' }
& {
    param($Definition, $Root)
    . ([scriptblock]::Create($Definition))
    $repoRoot = $Root
    $Configuration = 'Debug'
    $NoBuild = $false
    $rid = 'win-x64'
    $resultsDirectory = Join-Path $Root 'TestResults'
    $resolvedApp = Join-Path $Root 'candidate.exe'
    $resolvedStableApp = $null
    $artifacts = $resultsDirectory
    $Screenshots = $true
    $inherited = $env:OPENCLAW_RUN_KEYBOARD_PROOF
    try {
        $env:OPENCLAW_RUN_KEYBOARD_PROOF = '1'
        foreach ($enabled in @($false, $true)) {
            $start = New-FixtureTestStartInfo -Project 'tests/fixture.csproj' -Filter 'Category=GatewayFixture&KeyboardProof!=Required' -ResultName 'fixture-ui' -EnableKeyboardProof:$enabled
            $expected = if ($enabled) { '1' } else { '0' }
            if ($start.Environment['OPENCLAW_RUN_KEYBOARD_PROOF'] -ne $expected -or $start.UseShellExecute -or
                $start.Environment['OPENCLAW_GATEWAY_FIXTURE_APP'] -ne $resolvedApp -or
                $start.Environment['OPENCLAW_RUN_GATEWAY_FIXTURE_UI'] -ne '1' -or
                -not $start.ArgumentList.Contains('Category=GatewayFixture&KeyboardProof!=Required')) {
                throw "Fixture UI native input must follow the explicit child option ($enabled), preserving owned-app/filter binding."
            }
        }
        foreach ($resultName in @('fixture-profile-app', 'fixture-released-profile')) {
            $resolvedStableApp = if ($resultName -eq 'fixture-released-profile') { Join-Path $Root 'stable.exe' } else { $null }
            $start = New-FixtureTestStartInfo -Project 'tests/fixture.csproj' -Filter 'profile' -ResultName $resultName
            if ($start.Environment['OPENCLAW_RUN_KEYBOARD_PROOF'] -ne '0') {
                throw 'Profile and upgrade children must not inherit native-input authorization.'
            }
        }
        $NoBuild = $true
        $start = New-FixtureTestStartInfo -Project 'tests/fixture.csproj' -Filter 'profile' -ResultName 'prebuilt'
        if (-not $start.ArgumentList.Contains('--no-build') -or -not $start.ArgumentList.Contains('--no-restore') -or
            @($start.ArgumentList | Where-Object { $_ -like '-p:Platform=*' }).Count -ne 0) {
            throw 'Prebuilt fixture execution must retain canonical RID outputs without building or restoring.'
        }
        if ($env:OPENCLAW_RUN_KEYBOARD_PROOF -ne '1') { throw 'Child launch planning changed the parent input opt-in.' }
    } finally { $env:OPENCLAW_RUN_KEYBOARD_PROOF = $inherited }
} $startInfoFunction[0].Extent.Text $repoRootPath
if ([regex]::Matches($fixtureRunner, [regex]::Escape('-EnableKeyboardProof:$NativeInputProof')).Count -ne 1) {
    throw 'The ordinary UI child must be the sole consumer of NativeInputProof.'
}
Assert-NotContains -Text $fixtureStep -Unexpected 'OPENCLAW_RUN_KEYBOARD_PROOF' -Message 'Fixture native input must use the explicit child option, not a step/global environment bypass.'
Assert-NotContains -Text $releasedProfileStep -Unexpected '-NativeInputProof' -Message 'Released-profile compatibility must not request native wheel input.'

foreach ($token in @('"--blame-hang"', '"--blame-hang-timeout"', '"--blame-hang-dump-type"', '"none"')) {
    Assert-Contains -Text $runner -Expected $token `
        -Message "CI test runner is missing hang diagnostic argument '$token'."
}

$runnerUses = [regex]::Matches(
    $workflow,
    "(?m)^\s+\./scripts/Invoke-CiTest\.ps1\s*$").Count
if ($runnerUses -ne 15) {
    throw "Expected 15 wrapped project runs including both Tray UI shards and targeted Release correlation and reader proofs, found $runnerUses."
}
$runnerParseErrors = $null
$runnerAst = [Management.Automation.Language.Parser]::ParseInput($runner, [ref]$null, [ref]$runnerParseErrors)
if ($runnerParseErrors.Count) { throw 'CI test runner must parse before checking its configuration contract.' }
$argumentStart = @($runnerAst.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.ToString() -eq '$testArguments'
})[0].Extent.StartOffset
$argumentEnd = @($runnerAst.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.ToString() -eq '$script:testExitCode'
})[0].Extent.StartOffset
$argumentStatements = $runnerAst.EndBlock.Statements | Where-Object {
    $_.Extent.StartOffset -ge $argumentStart -and $_.Extent.StartOffset -lt $argumentEnd
}
$argumentProbe = [scriptblock]::Create($runnerAst.ParamBlock.Extent.Text + "`n" +
    (($argumentStatements | ForEach-Object { $_.Extent.Text }) -join "`n") + "`n`$testArguments")
foreach ($configuration in @($null, 'Debug', 'Release')) {
    $parameters = @{ Project = 'unused'; ResultsDirectory = 'unused'; TrxFileName = 'probe.trx' }
    if ($configuration) { $parameters.Configuration = $configuration }
    $arguments = @(& $argumentProbe @parameters)
    $expectedConfiguration = if ($configuration) { $configuration } else { 'Debug' }
    if ($arguments[[Array]::IndexOf($arguments, '-c') + 1] -cne $expectedConfiguration -or
        $arguments -notcontains '--no-build') { throw 'CI configuration selection changed or rebuild was admitted.' }
}
$countGuard = @($runnerAst.EndBlock.Statements | Where-Object {
    $_ -is [Management.Automation.Language.IfStatementAst] -and
    $_.Extent.Text.Contains('$result.discovered -ne $ExpectedTestCount')
})
if ($countGuard.Count -ne 1) { throw 'Expected one selected-count admission guard.' }
$countProbe = [scriptblock]::Create($runnerAst.ParamBlock.Extent.Text + "`n" + $countGuard[0].Extent.Text)
foreach ($actual in @(1, 2, 3)) {
    $result = [pscustomobject]@{ discovered = $actual }
    $rejected = $false
    try { & $countProbe -Project unused -ResultsDirectory unused -TrxFileName probe.trx -ExpectedTestCount 2 }
    catch { $rejected = $true }
    if ($rejected -ne ($actual -ne 2)) { throw "Selected-count admission was incorrect for $actual results." }
}
$result = [pscustomobject]@{ discovered = 3 }
& $countProbe -Project unused -ResultsDirectory unused -TrxFileName probe.trx
foreach ($invalid in @(0, -1)) {
    $rejected = $false
    try { & $countProbe -Project unused -ResultsDirectory unused -TrxFileName probe.trx -ExpectedTestCount $invalid }
    catch [Management.Automation.ParameterBindingException] { $rejected = $true }
    if (-not $rejected) { throw "Invalid expected count was accepted: $invalid." }
}
if ($workflow -match "(?m)^\s+dotnet-coverage collect\s*$") {
    throw "Workflow test steps must not invoke dotnet-coverage directly."
}
foreach ($requiredRunnerToken in @(
        '"--logger"',
        '"trx;LogFileName=$TrxFileName"',
        'dotnet-coverage collect',
        '--output-format cobertura',
        '& dotnet @testArguments',
        'New-TestInvocation',
        'Read-TestResultContract',
        '-AllowSkipped'
    )) {
    Assert-Contains `
        -Text $runner `
        -Expected $requiredRunnerToken `
        -Message "CI test runner is missing required token '$requiredRunnerToken'."
}

# Step budget visibility: each timed single-runner step declares its own timeout-minutes in
# OPENCLAW_CI_STEP_TIMEOUT_MINUTES so the runner can warn before a passing suite outgrows it.
# Drift between the two values is rejected. It must never be a runner argument: alpha promotion
# runs this workflow against an older candidate's runner scripts, which reject unknown parameters.
$budgetedRunnerPattern = '(?m)^      run: >\r?\n\s+\./scripts/(?:Invoke-CiTest|Invoke-CiE2e|test-gateway-fixture)\.ps1\r?$'
# A multi-command step would understate its budget from inside the runner, so it must be named here.
$unbudgetedRunnerSteps = @('Run latest stable profile compatibility')
$budgetedSteps = 0
Assert-NotContains -Text $workflow -Unexpected '-StepTimeoutMinutes' `
    -Message 'CI must pass step budgets as OPENCLAW_CI_STEP_TIMEOUT_MINUTES, never as a runner argument that older promotion candidates reject.'
foreach ($stepText in [regex]::Split($workflow, '(?m)^(?=    - (?:name:|uses:))')) {
    $stepName = [regex]::Match($stepText, '(?m)^    - name: (.+?)\r?$').Groups[1].Value
    $declared = @([regex]::Matches($stepText, "(?m)^        OPENCLAW_CI_STEP_TIMEOUT_MINUTES: '(\d+)'\r?$") | ForEach-Object { [int]$_.Groups[1].Value })
    $stepTimeout = [regex]::Match($stepText, '(?m)^      timeout-minutes: (\d+)\r?$')
    if ($stepText -match $budgetedRunnerPattern -and $stepTimeout.Success) {
        if ($declared.Count -ne 1 -or $declared[0] -ne [int]$stepTimeout.Groups[1].Value -or
            -not ($stepText -match '(?m)^      env:\r?$')) {
            throw "CI step '$stepName' must set OPENCLAW_CI_STEP_TIMEOUT_MINUTES: '$($stepTimeout.Groups[1].Value)' to match its timeout-minutes."
        }
        $budgetedSteps++
    } elseif ($declared.Count -gt 0 -or $stepText.Contains('OPENCLAW_CI_STEP_TIMEOUT_MINUTES')) {
        throw "CI step '$stepName' declares a step budget without being a timed single-runner step."
    } elseif ($stepTimeout.Success -and $stepText -match '\./scripts/(?:Invoke-CiTest|Invoke-CiE2e|test-gateway-fixture)\.ps1' -and
        $stepName -notin $unbudgetedRunnerSteps) {
        throw "CI step '$stepName' runs a test runner with other commands. Make it a single-runner step with OPENCLAW_CI_STEP_TIMEOUT_MINUTES, or list it in `$unbudgetedRunnerSteps."
    }
}
if ($budgetedSteps -lt 13) { throw "Expected at least 13 budgeted runner steps, found $budgetedSteps." }
if (([regex]::Matches($workflow, '(?m)^\s+OPENCLAW_CI_STEP_TIMEOUT_MINUTES:')).Count -ne $budgetedSteps) {
    throw 'OPENCLAW_CI_STEP_TIMEOUT_MINUTES may only be set on budgeted runner steps, not at job or workflow level.'
}

# Alpha promotion runs this workflow from main against an older candidate's checkout and its own
# runner scripts. Bind every runner invocation's current arguments to the runners of the oldest
# alpha that must stay promotable and to the current runners. A parameter the candidate's runner
# does not declare fails here instead of failing a promotion; pass new CI options as step
# environment variables instead. Advance the pin when older alphas no longer need promotion.
# Pinned: v2026.11.3-alpha.11. Earlier alphas already lack runner options added on Nov 9-10
# (for example -Configuration and -ExpectedTestCount), which this check cannot restore.
$promotionCandidateSha = '3f47e83888db83740660d03c3ecd403550235187'
& git -C $repoRootPath cat-file -e "$promotionCandidateSha^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) { throw "Promotion compatibility needs candidate $promotionCandidateSha; check out with full history." }
$global:LASTEXITCODE = 0
$runnerPattern = '\./scripts/(?<script>Invoke-CiTest|Invoke-CiE2e|test-gateway-fixture)\.ps1'
$runnerInvocations = [Collections.Generic.List[object]]::new()
foreach ($stepText in [regex]::Split(($workflow -replace "`r`n", "`n"), '(?m)^(?=    - (?:name:|uses:))')) {
    $stepName = [regex]::Match($stepText, '(?m)^    - name: (.+)$').Groups[1].Value
    $folded = [regex]::Match($stepText, "(?ms)^      run: >\n(?<command>\s+$runnerPattern\n.*?)(?=\n\n|\n    - |\z)")
    if ($folded.Success) {
        $runnerInvocations.Add(@{ Step = $stepName; Script = $folded.Groups['script'].Value
            Command = (($folded.Groups['command'].Value -split "`n") | ForEach-Object { $_.Trim() }) -join ' ' })
        continue
    }
    # A literal run block may call a runner on backtick-continued lines among other commands.
    $lines = @($stepText -split "`n")
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index].Trim() -notmatch "^$runnerPattern") { continue }
        $script = $Matches.script
        $command = $lines[$index].Trim()
        while ($command.EndsWith('`') -and $index + 1 -lt $lines.Count) {
            $index++
            $command = $command.TrimEnd('`').TrimEnd() + ' ' + $lines[$index].Trim()
        }
        $runnerInvocations.Add(@{ Step = $stepName; Script = $script; Command = $command })
    }
}
# Runtime values are bound as placeholders: only the parameter names matter here.
foreach ($invocation in $runnerInvocations) {
    $command = [regex]::Replace($invocation.Command, '\$\{\{.*?\}\}', 'placeholder')
    $invocation.Command = [regex]::Replace($command, '\$[A-Za-z_][\w:]*(?:\[[^\]]*\])?(?:\.\w+)*', "'placeholder'")
}
# Count every mention of a runner outside YAML comments, in any path form, so an unrecognized
# call fails closed.
$workflowCommands = ($workflow -split "`r?`n" | Where-Object { -not $_.TrimStart().StartsWith('#', [StringComparison]::Ordinal) }) -join "`n"
$runnerMentionCount = [regex]::Matches($workflowCommands, '(?i)(?:Invoke-CiTest|Invoke-CiE2e|test-gateway-fixture)\.ps1').Count
if ($runnerInvocations.Count -ne $runnerMentionCount) {
    throw "Every CI runner invocation must be checked for promotion compatibility: found $runnerMentionCount runner reference(s) but recognized $($runnerInvocations.Count). Start the run line with ./scripts/<runner>.ps1 (folded 'run: >' or backtick-continued lines)."
}
# Compare parameter names from the parsed command with each runner's declared parameters. Nothing
# is evaluated, so missing mandatory values or runtime expressions cannot prompt or mislead.
$runnerParameters = @{}
foreach ($invocation in $runnerInvocations) {
    $parseErrors = $null
    $commandAst = [Management.Automation.Language.Parser]::ParseInput($invocation.Command, [ref]$null, [ref]$parseErrors).Find({ param($node)
            $node -is [Management.Automation.Language.CommandAst] }, $true)
    if ($parseErrors.Count -or -not $commandAst) {
        throw "Could not parse the runner call in CI step '$($invocation.Step)': $($parseErrors.Message -join '; ')"
    }
    if ($invocation.Command -match '(^|\s)--%(\s|$)') {
        throw "CI step '$($invocation.Step)' stops runner argument parsing with --%; pass named parameters so promotion compatibility can be checked."
    }
    if ($commandAst.CommandElements | Where-Object { $_ -is [Management.Automation.Language.VariableExpressionAst] -and $_.Splatted }) {
        throw "CI step '$($invocation.Step)' splats runner arguments; pass named parameters so promotion compatibility can be checked."
    }
    $passed = @($commandAst.CommandElements | Where-Object { $_ -is [Management.Automation.Language.CommandParameterAst] } |
        ForEach-Object { $_.ParameterName })
    foreach ($source in @('candidate', 'current')) {
        $key = "$source/$($invocation.Script)"
        if (-not $runnerParameters.ContainsKey($key)) {
            $runnerSource = if ($source -eq 'current') {
                Get-Content -LiteralPath (Join-Path $repoRootPath "scripts\$($invocation.Script).ps1") -Raw
            } else {
                (& git -C $repoRootPath show "${promotionCandidateSha}:scripts/$($invocation.Script).ps1") -join "`n"
            }
            if ($LASTEXITCODE -ne 0 -or -not $runnerSource) { throw "Could not read the $source $($invocation.Script) runner." }
            $declared = [Management.Automation.Language.Parser]::ParseInput($runnerSource, [ref]$null, [ref]$null).ParamBlock.Parameters
            $runnerParameters[$key] = @{
                Names = @($declared | ForEach-Object { $_.Name.VariablePath.UserPath })
                Mandatory = @($declared | Where-Object {
                        $_.Attributes | Where-Object { $_.TypeName.Name -eq 'Parameter' -and
                            ($_.NamedArguments | Where-Object { $_.ArgumentName -eq 'Mandatory' }) } } |
                    ForEach-Object { $_.Name.VariablePath.UserPath })
            }
        }
        $runnerContract = $runnerParameters[$key]
        $unknown = @($passed | Where-Object { $_ -notin $runnerContract.Names })
        $absent = @($runnerContract.Mandatory | Where-Object { $_ -notin $passed })
        if ($unknown.Count -or $absent.Count) {
            throw "CI step '$($invocation.Step)' does not bind to the $source $($invocation.Script) runner: unknown -$($unknown -join ', -'); missing mandatory -$($absent -join ', -')."
        }
    }
}
$global:LASTEXITCODE = 0
if ($runnerInvocations.Count -lt 21) { throw "Expected at least 21 runner invocations in the promotion binding check, found $($runnerInvocations.Count)." }
. (Join-Path $repoRootPath 'scripts\CiStepBudget.ps1')
foreach ($case in @(
        @{ Minutes = 0.5; Budget = 25; Warn = $false; Text = 'CI step budget: probe.trx used 0.5 of 25 minutes (2%) of its step timeout.' },
        @{ Minutes = 19.9; Budget = 25; Warn = $false; Text = 'CI step budget: probe.trx used 19.9 of 25 minutes (79%) of its step timeout.' },
        @{ Minutes = 20; Budget = 25; Warn = $true; Text = '::warning title=CI step budget::probe.trx used 20.0 of 25 minutes (80%25) of its step timeout.' },
        @{ Minutes = 15; Budget = 15; Warn = $true; Text = '::warning title=CI step budget::probe.trx used 15.0 of 15 minutes (100%25) of its step timeout.' }
    )) {
    $report = Get-CiStepBudgetReport -Elapsed ([TimeSpan]::FromMinutes($case.Minutes)) -StepTimeoutMinutes $case.Budget -Label 'probe.trx'
    if (-not $report.StartsWith($case.Text, [StringComparison]::Ordinal) -or
        $report.StartsWith('::warning', [StringComparison]::Ordinal) -ne $case.Warn) {
        throw "Step budget report for $($case.Minutes) of $($case.Budget) minutes was '$report'."
    }
}
if ((Get-CiStepBudgetReport -Elapsed ([TimeSpan]::FromMinutes(30)) -StepTimeoutMinutes 25 -Label "a`nb") -match "`n") {
    throw 'Step budget warnings must escape workflow command data.'
}
$priorBudget = $env:OPENCLAW_CI_STEP_TIMEOUT_MINUTES
try {
    foreach ($case in @(@{ Value = $null; Minutes = 0 }, @{ Value = '25'; Minutes = 25 }, @{ Value = ' 360 '; Minutes = 360 },
            @{ Value = '0'; Minutes = 0 }, @{ Value = '361'; Minutes = 0 }, @{ Value = '-5'; Minutes = 0 }, @{ Value = '2.5'; Minutes = 0 }, @{ Value = 'x'; Minutes = 0 })) {
        $env:OPENCLAW_CI_STEP_TIMEOUT_MINUTES = $case.Value
        $resolved = Get-CiStepTimeoutMinutes 3>$null
        if ($resolved -ne $case.Minutes) { throw "Step budget '$($case.Value)' resolved to $resolved, expected $($case.Minutes)." }
    }
} finally { $env:OPENCLAW_CI_STEP_TIMEOUT_MINUTES = $priorBudget }
foreach ($runnerScript in @($runner, $e2eRunner, (Get-Content -LiteralPath (Join-Path $repoRootPath 'scripts\test-gateway-fixture.ps1') -Raw))) {
    $budgetAst = [Management.Automation.Language.Parser]::ParseInput($runnerScript, [ref]$null, [ref]$null)
    $budgetProbe = [scriptblock]::Create(($budgetAst.ParamBlock.Extent.Text -replace '\[Parameter\(Mandatory\)\]', '') + "`n`$StepTimeoutMinutes")
    if ((& $budgetProbe) -ne 0 -or (& $budgetProbe -StepTimeoutMinutes 360) -ne 360) {
        throw 'CI runners must default to no step budget and accept the GitHub maximum.'
    }
    foreach ($invalid in @(-1, 361)) {
        $rejected = $false
        try { & $budgetProbe -StepTimeoutMinutes $invalid | Out-Null }
        catch [Management.Automation.ParameterBindingException] { $rejected = $true }
        if (-not $rejected) { throw "A CI runner accepted step budget $invalid." }
    }
    Assert-Contains -Text $runnerScript -Expected '$StepTimeoutMinutes = Get-CiStepTimeoutMinutes' `
        -Message 'Each CI runner must read OPENCLAW_CI_STEP_TIMEOUT_MINUTES when -StepTimeoutMinutes is not passed.'
    Assert-Contains -Text $runnerScript -Expected 'Write-CiStepBudget -Elapsed $stepBudget.Elapsed -StepTimeoutMinutes $StepTimeoutMinutes' `
        -Message 'Each CI runner must report its step budget.'
}

$e2eLanes = [ordered]@{
    "setup-e2e" = @{
        Output = "setup_e2e"
        Name = "setup-connect"
        Step = "Run setup and connect E2E tests"
        Filter = "OpenClaw.E2ETests.Setup.SetupAndConnectTests"
    }
    "revocation-e2e" = @{
        Output = "revocation_e2e"
        Name = "revocation-recovery"
        Step = "Run revocation recovery E2E tests"
        Filter = "OpenClaw.E2ETests.Setup.RevocationAndRecoveryTests"
    }
    "network-e2e" = @{
        Output = "network_e2e"
        Name = "network-recovery"
        Step = "Run network recovery E2E tests"
        Filter = "OpenClaw.E2ETests.Setup.NetworkRecoveryTests"
    }
}
foreach ($lane in $e2eLanes.GetEnumerator()) {
    $job = Get-JobBlock $lane.Key
    foreach ($token in @(
            "fetch-depth: 0",
            "needs.change-classification.outputs.$($lane.Value.Output) == 'true'",
            "OPENCLAW_RUN_E2E: 1",
            'OPENCLAW_E2E_GATEWAY_VERSION: ${{ needs.change-classification.outputs.gateway_version }}',
            'GATEWAY_RELEASE_JSON: ${{ needs.change-classification.outputs.gateway_release }}',
            "name: e2e-test-results-$($lane.Value.Name)",
            'TestResults/E2E/gateway-release.json',
            "./scripts/Invoke-CiE2e.ps1",
            "-Name $($lane.Value.Name)",
            $lane.Value.Filter,
            "TestResults/E2E/"
        )) {
        Assert-Contains `
            -Text $job `
            -Expected $token `
            -Message "E2E lane '$($lane.Key)' is missing '$token'."
    }
    $jobHeader = ($job -split '(?m)^    steps:\s*$', 2)[0]
    Assert-Contains -Text $jobHeader -Expected 'timeout-minutes: 25' `
        -Message "E2E lane '$($lane.Key)' must leave a stalled job bounded to 25 minutes."
    $testStep = Get-StepBlock -Text $job -Name $lane.Value.Step
    Assert-Contains -Text $testStep -Expected 'timeout-minutes: 20' `
        -Message "E2E lane '$($lane.Key)' must stop stalled tests before the job deadline."
    $uploadStep = Get-StepBlock -Text $job -Name 'Upload E2E Test Results & Logs'
    Assert-Contains -Text $uploadStep -Expected 'if: always()' `
        -Message "E2E lane '$($lane.Key)' must upload diagnostics after a test timeout."
    Assert-NotContains -Text $jobHeader -Unexpected 'continue-on-error:' `
        -Message "WSL Latest must be required in '$($lane.Key)'."
    Assert-NotContains -Text $jobHeader -Unexpected 'strategy:' `
        -Message "WSL lane '$($lane.Key)' must run once, without a packaged Gateway matrix."
    Assert-NotContains -Text $job -Unexpected 'Resolve-CiGateway.ps1' `
        -Message 'E2E shards must consume the workflow snapshot, never re-resolve moving selectors.'
}
$classification = Get-JobBlock 'change-classification'
foreach ($output in @('gateway_version: ${{ steps.gateway.outputs.version }}', 'gateway_release: ${{ steps.gateway.outputs.release }}')) {
    Assert-Contains -Text $classification -Expected $output `
        -Message 'CI must forward the exact WSL Latest version and provenance to every E2E job.'
}
$snapshot = Get-StepBlock -Text $classification -Name 'Snapshot latest WSL Gateway baseline'
Assert-Contains -Text $snapshot -Expected './scripts/Resolve-CiGateway.ps1' `
    -Message 'CI must resolve the WSL Latest baseline once.'
Assert-NotContains -Text $snapshot -Unexpected 'continue-on-error:' `
    -Message 'An unresolved baseline must fail CI rather than fall back.'
Assert-NotContains -Text $workflow -Unexpected 'packaging_release' `
    -Message 'WSL CI must not select a native packaging release.'
Assert-NotContains -Text $workflow -Unexpected 'gateway_matrix' `
    -Message 'WSL CI must not restore the native Packaged/Latest matrix.'
$gatewayResolver = Get-Content -LiteralPath (Join-Path $repoRootPath 'scripts\Resolve-CiGateway.ps1') -Raw
foreach ($token in @(
        'Resolve-GatewayNpmRelease -Selector latest',
        '"version=$($release.version)"',
        '"release=$($release | ConvertTo-Json -Compress)"',
        "'gateway-release.json'"
    )) {
    Assert-Contains -Text $gatewayResolver -Expected $token `
        -Message "WSL baseline resolution is missing '$token'."
}
foreach ($token in @('GatewayPackagedRelease', 'PackagingRelease', 'Save-PackagedGatewayArchive', 'gateway-matrix.json')) {
    Assert-NotContains -Text $gatewayResolver -Unexpected $token `
        -Message 'WSL Latest resolution must not depend on native MSIX releases or downloads.'
}
foreach ($lane in @('setup_e2e', 'revocation_e2e', 'network_e2e')) {
    Assert-Contains -Text $snapshot -Expected "steps.classify.outputs.$lane == 'true'" `
        -Message 'Baseline resolution must run only when an E2E lane needs it.'
}
foreach ($proofName in @(
        "RealGateway_SystemRun_ExecutesThroughWindowsNodeMxcSandbox",
        "RealGateway_SystemRun_BlocksWritesToTrayDataDirectoryInMxcSandbox",
        "UnownedListenerIsRejectedThenOwnedTunnelRecoversWithoutRepairing",
        "InitialHandshakeListenerReplacementWithholdsCredentialFrame",
        "InitialNodeHandshakeListenerReplacementWithholdsCredentialFrame"
    )) {
    Assert-Contains `
        -Text $e2eRunner `
        -Expected $proofName `
        -Message "E2E runner lost proof assertion '$proofName'."
}
foreach ($skipReasonToken in @(
        '$mxcProof.SelectSingleNode("Output/ErrorInfo/Message")',
        '$mxcProof.SelectSingleNode("Output/StdOut")',
        '$null -ne $_'
    )) {
    Assert-Contains `
        -Text $e2eRunner `
        -Expected $skipReasonToken `
        -Message "E2E runner lost null-safe MXC skip-reason handling '$skipReasonToken'."
}

$metadataJob = Get-JobBlock "metadata"
foreach ($token in @(
        "needs: change-classification",
        "fetch-depth: 0",
        "outputs.x64_release == 'true' || needs.change-classification.outputs.arm64_release == 'true'",
        "gittools/actions/gitversion/setup@v4",
        "gittools/actions/gitversion/execute@v4",
        "semVer: `${{ steps.release_version.outputs.semVer }}",
        "majorMinorPatch: `${{ steps.release_version.outputs.majorMinorPatch }}",
        "isPrerelease: `${{ steps.release_version.outputs.isPrerelease }}",
        "isStableCorrection: `${{ steps.release_version.outputs.isStableCorrection }}",
        "msixVersionInfo: `${{ steps.msix_preview.outputs.versionInfo }}",
        "msixSourceVersion: `${{ steps.msix_preview.outputs.sourceVersion }}",
        "Test-OpenClawStableCorrectionRelease.ps1"
    )) {
    Assert-Contains -Text $metadataJob -Expected $token -Message "Metadata job is missing '$token'."
}
Assert-NotContains `
    -Text $metadataJob `
    -Unexpected "fast-validation" `
    -Message "Release metadata must start independently of validation lanes."

$releaseBuilds = [ordered]@{
    "build-x64" = @{
        Output = "x64_release"
        Runtime = "win-x64"
        Runner = "windows-latest"
        Native = "Test-ReleaseNativeDependencies.ps1 -PayloadPath publish -RequireAppLocalVCRuntime"
    }
    "build-arm64" = @{
        Output = "arm64_release"
        Runtime = "win-arm64"
        Runner = "windows-11-arm"
        Native = "Test-ReleaseNativeDependencies.ps1 -PayloadPath publish -RequireAppLocalVCRuntime -SkipNativeLoadProbe"
    }
}
function Assert-JobDependencies {
    param([string]$Name, [string[]]$Expected)
    $job = Get-JobBlock $Name
    $match = [regex]::Match($job, '(?m)^    needs:\s*\[([^\]]+)\]')
    if (!$match.Success) { throw "Missing dependency list for $Name." }
    $actual = @($match.Groups[1].Value.Split(',') | ForEach-Object { $_.Trim() })
    if (Compare-Object $Expected $actual) {
        throw "Unexpected dependencies for ${Name}: $($actual -join ', ')."
    }
}

foreach ($build in $releaseBuilds.GetEnumerator()) {
    $job = Get-JobBlock $build.Key
    Assert-JobDependencies -Name $build.Key -Expected @('change-classification', 'metadata', 'sign-cleanup-host')
    foreach ($token in @(
            "fetch-depth: 0",
            "needs.change-classification.outputs.$($build.Value.Output) == 'true'",
            "runs-on: $($build.Value.Runner)",
            "OPENCLAW_BUILD_VERSION: `${{ needs.metadata.outputs.semVer }}",
            "dotnet publish src/OpenClaw.Tray.WinUI -c Release -r $($build.Value.Runtime) --self-contained --no-restore",
            $build.Value.Native,
            'Verify GitVersion assembly metadata'
        )) {
        Assert-Contains -Text $job -Expected $token -Message "Release build '$($build.Key)' is missing '$token'."
    }
    Assert-NotContains `
        -Text $job `
        -Unexpected "core-tests" `
        -Message "Release builds must start in parallel with tests and E2E."
}

foreach ($jobName in @('build-x64', 'build-arm64', 'build-msix')) {
    $job = Get-JobBlock $jobName
    $jobEnvironment = [regex]::Match($job, '(?ms)^    env:\r?\n.*?(?=^    \S|\z)')
    if (-not $jobEnvironment.Success) {
        throw "Release build '$jobName' is missing its job environment."
    }
    Assert-NotContains -Text $jobEnvironment.Value -Unexpected 'runner.' `
        -Message "Release build '$jobName' must not use the unavailable runner context in job-level env."
    $download = Get-StepBlock -Text $job -Name 'Download signed cleanup host'
    Assert-Contains -Text $download `
        -Expected 'path: ${{ runner.temp }}\signed-cleanup-host' `
        -Message "Release build '$jobName' must download the signed helper outside the checkout to preserve clean-source provenance."
    $binding = Get-StepBlock -Text $job -Name 'Bind signed cleanup host path'
    Assert-Contains -Text $binding -Expected "if: needs.sign-cleanup-host.result == 'success'" `
        -Message "Release build '$jobName' must bind its helper only after successful signing."
    Assert-Contains -Text $binding `
        -Expected '"OpenClawSignedCleanupHostPath=$env:RUNNER_TEMP\signed-cleanup-host\OpenClaw.GatewayCleanupHost.exe" >> $env:GITHUB_ENV' `
        -Message "Release build '$jobName' must export the runner-temporary helper path for subsequent build steps."
}

# Windows ARM64 pwsh crash workaround (PowerShell/PowerShell#28186, actions/runner-images#15107):
# only the setup-dotnet steps of jobs that can run on windows-11-arm carry the gen0 budget, as an
# active entry of the step's own env map (not a comment, `with:` input, or job/workflow env).
function Get-StepEnvironmentLines {
    param([Parameter(Mandatory)][string]$Step)

    $lines = $Step -split '\r?\n'
    $start = [Array]::FindIndex($lines, [Predicate[string]] { param($line) $line -ceq '      env:' })
    if ($start -lt 0) { return @() }
    $entries = foreach ($line in $lines[($start + 1)..($lines.Count - 1)]) {
        if ($line -notmatch '^        ') { break }
        if ($line.TrimStart().StartsWith('#')) { continue }
        $line.Trim()
    }
    return @($entries)
}
foreach ($armSetup in @(
        @{ Job = 'build-arm64'; Step = 'Setup .NET 10' },
        @{ Job = 'build-msix'; Step = 'Setup .NET from global.json' })) {
    $setupStep = Get-StepBlock -Text (Get-JobBlock $armSetup.Job) -Name $armSetup.Step
    if ((Get-StepEnvironmentLines -Step $setupStep) -cnotcontains "DOTNET_GCgen0size: '4000000'") {
        throw "ARM64-capable job '$($armSetup.Job)' must scope the pwsh CLR crash workaround to the env map of '$($armSetup.Step)'."
    }
}
$gen0Occurrences = [regex]::Matches($workflow, '(?im)^(?!\s*#).*DOTNET_GCgen0size').Count
if ($gen0Occurrences -ne 2) {
    throw "The pwsh CLR crash workaround must stay in the two ARM64-capable setup-dotnet steps only (found $gen0Occurrences active entries); builds and tests keep default GC settings. If you add an ARM64-capable setup-dotnet step, add it to this list and update the count."
}

$buildMsixJob = Get-JobBlock "build-msix"
Assert-JobDependencies -Name 'build-msix' -Expected @('change-classification', 'metadata', 'reserve-msix-version', 'sign-cleanup-host')
foreach ($token in @(
        "needs.metadata.result == 'success'",
        "(needs.reserve-msix-version.result == 'success' || needs.reserve-msix-version.result == 'skipped')",
        "needs.change-classification.outputs.x64_release == 'true' || needs.change-classification.outputs.arm64_release == 'true'",
        "architecture: [x64, arm64]",
        "matrix.architecture == 'arm64' && 'windows-11-arm' || 'windows-latest'",
        "fetch-depth: 0",
        "global-json-file: global.json",
        "OPENCLAW_BUILD_VERSION: `${{ needs.metadata.outputs.semVer }}",
        "DEV_MSIX_REVISION: `${{ github.run_number }}",
        '.\scripts\Build-StoreMsix.ps1 -Architecture',
        '.\scripts\setup-dev-msix-cert.ps1',
        '.\build.ps1 -Project WinUI -Configuration Release -Msix Dev',
        '-MsixRevision $env:DEV_MSIX_REVISION',
        '-MsixOutputDirectory "$env:RUNNER_TEMP\openclaw-dev-appx"',
        '.\scripts\Export-DevMsixArtifact.ps1',
        '.\scripts\Export-MigrationTestMsix.ps1 -Architecture',
        'id: migration-switch',
        "if: steps.migration-switch.outputs.enabled == 'true'",
        'name: openclaw-msix-dev-migration-test-${{ matrix.architecture }}',
        'artifacts/msix-migration-test/${{ matrix.architecture }}/OpenClaw-MigrationTest-${{ matrix.architecture }}.msix',
        'OpenClaw-MigrationTest.cer',
        'MSIX_VERSION_INFO: ${{ needs.reserve-msix-version.outputs.versionInfo || needs.metadata.outputs.msixVersionInfo }}',
        'MSIX_SOURCE_VERSION: ${{ needs.reserve-msix-version.outputs.sourceVersion || needs.metadata.outputs.msixSourceVersion }}',
        '-ExpectedVersion $info.packageBaseVersion',
        '-MsixBaseVersion $info.packageBaseVersion',
        '-VersionInfoPath "$env:RUNNER_TEMP\openclaw-msix-version.json"',
        'Assert-MsixVersionInfo',
        '-SourceCommit $env:OPENCLAW_RELEASE_SOURCE_SHA -SourceVersion $env:MSIX_SOURCE_VERSION',
        'refusing to build with a fallback version',
        '-CertificateThumbprint $thumbprint',
        'name: openclaw-msix-store-unsigned-${{ matrix.architecture }}',
        'name: openclaw-msix-dev-${{ matrix.architecture }}',
        'artifacts/msix/${{ matrix.architecture }}/OpenClaw-${{ matrix.architecture }}.msix',
        'artifacts/msix-dev/${{ matrix.architecture }}/OpenClaw-Dev-${{ matrix.architecture }}.msix',
        'msix-metadata.json',
        'OpenClaw-Dev.cer',
        'INSTALL.txt',
        'if-no-files-found: error',
        '.\scripts\setup-dev-msix-cert.ps1 -Remove'
    )) {
    Assert-Contains -Text $buildMsixJob -Expected $token -Message "MSIX artifact lane is missing '$token'."
}
foreach ($token in @('if: false', "`n    continue-on-error: true", 'Set-Content global.json', 'msbuild src/', 'Select-Object -First 1', 'Export-PfxCertificate', 'secrets.', 'id-token: write', 'contents: write', '-Reserve')) {
    Assert-NotContains -Text $buildMsixJob -Unexpected $token -Message "MSIX artifacts must not contain '$token'."
}

$buildMsixBundleJob = Get-JobBlock 'build-msix-bundle'
foreach ($token in @(
    'name: Multi-architecture Store MSIX bundle',
    'needs: [change-classification, metadata, reserve-msix-version, build-msix]',
    "needs.build-msix.result == 'success'",
    'name: openclaw-msix-store-unsigned-x64',
    'name: openclaw-msix-store-unsigned-arm64',
    '.\scripts\Build-StoreMsixBundle.ps1',
    '-PackageVersion ''${{ steps.version.outputs.packageVersion }}''',
    '-OutputPath artifacts\msix\bundle\OpenClaw.msixbundle',
    'name: openclaw-msix-store-unsigned-bundle',
    'path: artifacts/msix/bundle/OpenClaw.msixbundle',
    'Assert-MsixVersionInfo',
    '-SourceCommit $env:OPENCLAW_RELEASE_SOURCE_SHA -SourceVersion $env:MSIX_SOURCE_VERSION',
    'refusing to bundle with a fallback version'
)) {
    Assert-Contains -Text $buildMsixBundleJob -Expected $token -Message "MSIX bundle lane is missing '$token'."
}
foreach ($token in @('secrets.', 'id-token: write', 'contents: write', '-Reserve')) {
    Assert-NotContains -Text $buildMsixBundleJob -Unexpected $token -Message "MSIX bundle lane must not contain '$token'."
}

$reserveMsixJob = Get-JobBlock 'reserve-msix-version'
foreach ($token in @(
    'needs: [change-classification, metadata]',
    "needs.metadata.result == 'success'",
    "github.repository == 'openclaw/openclaw-windows-node'",
    "startsWith(github.ref, 'refs/tags/v')",
    "(github.event_name == 'push' || github.event_name == 'workflow_dispatch')",
    'contents: write', 'persist-credentials: false',
    'versionInfo: ${{ steps.reserve.outputs.versionInfo }}',
    'sourceVersion: ${{ steps.reserve.outputs.sourceVersion }}',
    '-SourceRef $env:OPENCLAW_RELEASE_REF -Repository $env:GITHUB_REPOSITORY -Reserve'
)) {
    Assert-Contains -Text $reserveMsixJob -Expected $token -Message "Official MSIX allocation is missing '$token'."
}
Assert-NotContains -Text $metadataJob -Unexpected 'contents: write' -Message 'Preview metadata must remain read-only.'
Assert-NotContains -Text $metadataJob -Unexpected '-Reserve' -Message 'Preview metadata must not allocate releases.'
$previewStep = Get-StepBlock -Text $metadataJob -Name 'Resolve MSIX preview version'
foreach ($token in @(
    '.\scripts\Get-OpenClawMsixPreviewSourceVersion.ps1',
    '-Repository openclaw/openclaw-windows-node',
    '-GitHubToken $env:GH_TOKEN',
    '"sourceVersion=$sourceVersion" >> $env:GITHUB_OUTPUT'
)) {
    Assert-Contains -Text $previewStep -Expected $token -Message "MSIX preview source contract is missing '$token'."
}
Assert-NotContains -Text $previewStep -Unexpected '$env:GITHUB_REPOSITORY' -Message 'Fork previews must read the canonical upstream reservation ledger.'
Assert-NotContains -Text $previewStep -Unexpected '${{ steps.release_version.outputs.semVer }}' -Message 'PR/main MSIX previews must not use the development GitVersion line.'

# Evaluate the actual context guards, including the complement used for previews.
function Convert-MsixGuard {
    param([string]$Text)
    $guard = [regex]::Match($Text, '(?m)^\s+if: \$\{\{\s*(?<condition>.*?)\s*\}\}\s*$')
    if (-not $guard.Success) { throw 'Missing MSIX workflow context guard.' }
    $condition = $guard.Groups['condition'].Value.
        Replace('!cancelled()', '(-not $cancelled)').
        Replace("startsWith(github.ref, 'refs/tags/v')", '$ref.StartsWith(''refs/tags/v'')').
        Replace('needs.metadata.result', '$metadataResult').
        Replace('github.repository', '$repository').
        Replace('github.event_name', '$eventName').
        Replace('inputs.promotion_alpha', '$promotionAlpha').
        Replace('!=', '-ne').Replace('==', '-eq').Replace('&&', '-and').Replace('||', '-or').Replace('!(', '-not (')
    [scriptblock]::Create('param($repository,$ref,$eventName,$metadataResult,$cancelled,$promotionAlpha = '''')' + "`n($condition)")
}
$reserveGuard = Convert-MsixGuard $reserveMsixJob
$previewGuard = Convert-MsixGuard $previewStep
if (-not (& $reserveGuard 'openclaw/openclaw-windows-node' 'refs/heads/main' 'workflow_dispatch' 'success' $false 'v2026.9.5-alpha.93') -or
    (& $previewGuard 'openclaw/openclaw-windows-node' 'refs/heads/main' 'workflow_dispatch' 'success' $false 'v2026.9.5-alpha.93')) {
    throw 'Validated main-based promotion must reserve an official version, not use a preview.'
}
foreach ($repository in @('openclaw/openclaw-windows-node', 'contributor/openclaw-windows-node')) {
    foreach ($ref in @('refs/tags/v2026.9.4', 'refs/tags/v2026.9.4-1', 'refs/tags/v2026.9.4-alpha.1', 'refs/heads/main', 'refs/pull/1/merge', 'refs/tags/msix-package/2026.9.4/401')) {
        foreach ($eventName in @('push', 'workflow_dispatch', 'pull_request', 'pull_request_target')) {
            $official = $repository -eq 'openclaw/openclaw-windows-node' -and
                $ref.StartsWith('refs/tags/v') -and $eventName -in @('push', 'workflow_dispatch')
            if ((& $reserveGuard $repository $ref $eventName 'success' $false) -ne $official -or
                (& $previewGuard $repository $ref $eventName 'success' $false) -ne (-not $official)) {
                throw "Incorrect MSIX allocation scope: $repository, $ref, $eventName"
            }
            if ((& $reserveGuard $repository $ref $eventName 'failure' $false) -or
                (& $reserveGuard $repository $ref $eventName 'success' $true)) {
                throw 'Failed or cancelled metadata must not reserve MSIX versions.'
            }
        }
    }
}

$ciGateJob = Get-JobBlock "ci-gate"
foreach ($token in @(
        "name: CI Gate",
        "if: `${{ always() }}",
        "needs: [change-classification, fast-validation, proof-pool-contracts, metadata, core-tests, tray-tests, ui-tests, tray-ui-shard, gateway-fixture, gateway-fixture-shard, setup-e2e, revocation-e2e, network-e2e, build-x64, build-arm64, build-msix, build-msix-bundle]",
        "./scripts/Assert-CiGateResults.ps1",
        "CLASSIFICATION_RESULT: `${{ needs.change-classification.result }}",
        "CLASSIFICATION: `${{ needs.change-classification.outputs.classification }}",
        "FAST_VALIDATION_RESULT: `${{ needs.fast-validation.result }}",
        "PROOF_POOL_CONTRACTS_RESULT: `${{ needs.proof-pool-contracts.result }}",
        "-ClassificationResult `$env:CLASSIFICATION_RESULT",
        "-Classification `$env:CLASSIFICATION",
        "-FastValidationResult `$env:FAST_VALIDATION_RESULT",
        "-ProofPoolContractsResult `$env:PROOF_POOL_CONTRACTS_RESULT",
        "-FullRequired `$env:FULL_REQUIRED",
        "-CoreRequired `$env:CORE_REQUIRED",
        "-TrayRequired `$env:TRAY_REQUIRED",
        "-UiRequired `$env:UI_REQUIRED",
        "TRAY_UI_SHARD_RESULT: `${{ needs.tray-ui-shard.result }}",
        "-TrayUiShardResult `$env:TRAY_UI_SHARD_RESULT",
        "GATEWAY_FIXTURE_RESULT: `${{ needs.gateway-fixture.result }}",
        "GATEWAY_FIXTURE_SHARD_RESULT: `${{ needs.gateway-fixture-shard.result }}",
        "-GatewayFixtureResult `$env:GATEWAY_FIXTURE_RESULT",
        "-GatewayFixtureShardResult `$env:GATEWAY_FIXTURE_SHARD_RESULT",
        "-SetupE2eRequired `$env:SETUP_E2E_REQUIRED",
        "-RevocationE2eRequired `$env:REVOCATION_E2E_REQUIRED",
        "-NetworkE2eRequired `$env:NETWORK_E2E_REQUIRED",
        "-X64ReleaseRequired `$env:X64_RELEASE_REQUIRED",
        "-Arm64ReleaseRequired `$env:ARM64_RELEASE_REQUIRED",
        "-MetadataResult `$env:METADATA_RESULT",
        "MSIX_RESULT: `${{ needs.build-msix.result }}",
        "-MsixResult `$env:MSIX_RESULT",
        "MSIX_BUNDLE_RESULT: `${{ needs.build-msix-bundle.result }}",
        "-MsixBundleResult `$env:MSIX_BUNDLE_RESULT"
    )) {
    Assert-Contains -Text $ciGateJob -Expected $token -Message "Stable CI Gate is missing '$token'."
}

$releasePreparationJob = Get-JobBlock "prepare-release-assets"
foreach ($token in @(
        "needs: [change-classification, fast-validation, metadata, reserve-msix-version, proof-pool-contracts, core-tests, tray-tests, build-x64, build-arm64]",
        "needs.fast-validation.result == 'success'",
        "needs.reserve-msix-version.result == 'success'",
        "needs.proof-pool-contracts.result == 'success'",
        "needs.core-tests.result == 'success'",
        "needs.tray-tests.result == 'success'",
        "group: openclaw-windows-node-release-preparation",
        "name: Upload prepared release assets",
        "name: openclaw-release-assets",
        "compression-level: 0",
        "if-no-files-found: error"
    )) {
    Assert-Contains -Text $releasePreparationJob -Expected $token -Message "Release preparation is missing '$token'."
}
Assert-NotContains -Text $releasePreparationJob -Unexpected "ci-gate" -Message "Release preparation must overlap the long CI tail."

$releaseJob = Get-JobBlock "release"
foreach ($token in @(
        "needs: [change-classification, metadata, reserve-msix-version, prepare-release-assets, build-msix-bundle, ci-gate]",
        "needs.reserve-msix-version.result == 'success'",
        "needs.prepare-release-assets.result == 'success'",
        "needs.build-msix-bundle.result == 'success'",
        "needs.ci-gate.result == 'success'",
        "needs.metadata.outputs.semVer",
        "needs.metadata.outputs.isPrerelease",
        "needs.metadata.outputs.isStableCorrection"
    )) {
    Assert-Contains -Text $releaseJob -Expected $token -Message "Tag release is missing '$token'."
}
$preparedAssetsDownload = Get-StepBlock -Text $releaseJob -Name 'Download prepared release assets'
Assert-Contains -Text $preparedAssetsDownload -Expected 'name: openclaw-release-assets' -Message 'Publication must consume the prepared signed assets.'
Assert-Contains -Text $preparedAssetsDownload -Expected 'path: .' -Message 'Prepared assets must retain the release action paths.'
$msixX64Download = Get-StepBlock -Text $releaseJob -Name 'Download x64 signed Dev MSIX release artifact'
$msixArm64Download = Get-StepBlock -Text $releaseJob -Name 'Download ARM64 signed Dev MSIX release artifact'
$msixTrust = Get-StepBlock -Text $releaseJob -Name 'Trust signed Dev MSIX certificates for validation'
$msixStage = Get-StepBlock -Text $releaseJob -Name 'Stage signed Dev MSIX release assets'
$msixUntrust = Get-StepBlock -Text $releaseJob -Name 'Remove trusted Dev MSIX certificates'
foreach ($step in @($msixX64Download, $msixArm64Download, $msixTrust, $msixStage)) {
    Assert-NotContains -Text $step -Unexpected 'if:' -Message "Every tag release must publish signed Dev MSIX assets."
}
Assert-Contains -Text $msixX64Download -Expected 'name: openclaw-msix-dev-x64' -Message 'Tag releases must use the signed x64 Dev artifact.'
Assert-Contains -Text $msixArm64Download -Expected 'name: openclaw-msix-dev-arm64' -Message 'Tag releases must use the signed ARM64 Dev artifact.'
foreach ($step in @($msixX64Download, $msixArm64Download)) {
    Assert-NotContains -Text $step -Unexpected 'openclaw-msix-store-unsigned-' -Message 'Unsigned Store packages must stay workflow-only.'
}
Assert-Contains -Text $msixStage -Expected '-ExpectedSourceCommit $env:OPENCLAW_RELEASE_SOURCE_SHA' -Message "Release staging must bind artifacts to the tag's source."
Assert-Contains -Text $msixStage -Expected '-Version $env:RELEASE_VERSION' -Message "Release staging must validate the release version."
Assert-Contains -Text $msixStage -Expected '-ExpectedRevision $env:DEV_MSIX_REVISION' -Message 'Release staging must bind the Dev package revision.'
Assert-Contains -Text $msixStage -Expected '-ExpectedWorkflowRunId $env:GITHUB_RUN_ID' -Message 'Release staging must bind artifacts to the publishing run.'
Assert-Contains -Text $msixStage -Expected '-VersionInfoPath "$env:RUNNER_TEMP\openclaw-msix-version.json"' -Message 'Release staging must require its exact reserved MSIX version.'
Assert-Contains -Text $msixStage -Expected 'MSIX_VERSION_INFO: ${{ needs.reserve-msix-version.outputs.versionInfo }}' -Message 'Release staging must not accept a preview.'
Assert-Contains -Text $msixTrust -Expected 'Import-Certificate' -Message 'Release staging must trust the public Dev signer before Authenticode validation.'
Assert-Contains -Text $msixTrust -Expected '$existing.Count -eq 0' -Message 'Release staging must preserve pre-existing certificate trust.'
Assert-Contains -Text $msixTrust -Expected 'Add-Content -LiteralPath $thumbprintsPath' -Message 'Release staging must record each imported certificate before continuing.'
Assert-Contains -Text $msixUntrust -Expected 'if: ${{ always() }}' -Message 'Release staging must clean up imported trust after failures.'
Assert-Contains -Text $msixUntrust -Expected 'Remove-Item -Force' -Message 'Release staging must remove only its recorded temporary trust.'
$createRelease = Get-StepBlock -Text $releaseJob -Name 'Create Release'
Assert-Contains -Text $createRelease -Expected '${{ steps.msix_release.outputs.files }}' -Message "Every tag release must add validated MSIX release files."
Assert-Contains -Text $createRelease -Expected '${{ steps.msix_release.outputs.notes }}' -Message "Every tag release must include signed Dev MSIX notes."
Assert-Contains -Text $createRelease -Expected 'fail_on_unmatched_files: true' -Message "Missing release files must fail publication."
Assert-Contains -Text $createRelease -Expected "make_latest: `${{ needs.metadata.outputs.isPrerelease == 'true' && 'false' || 'true' }}" -Message "Alpha releases must not become Latest."
Assert-Contains -Text $workflow -Expected "./scripts/test-msix-ci-artifacts.ps1" -Message "Fast validation must exercise the Dev artifact contracts."
Assert-Contains -Text $workflow -Expected "./scripts/test-dev-msix-release.ps1" -Message "Fast validation must exercise signed Dev release staging."
Assert-Contains -Text $workflow -Expected "./scripts/test-msix-versioning.ps1" -Message 'Fast validation must exercise allocation races and boundaries.'
Assert-Contains -Text $workflow -Expected "./scripts/test-msix-preview-source-version.ps1" -Message 'Fast validation must exercise latest-stable MSIX preview selection.'

$triggerPaths = @(
    ".github/workflows/ci.yml",
    ".github/proof-pools.json",
    ".github/proof-pools.schema.json",
    "scripts/validate-proof-pools.ps1",
    "scripts/test-proof-pool-validator.ps1",
    "scripts/test-validate-docs-proof-pool-flow.ps1",
    "scripts/validate-docs.ps1",
    "scripts/Get-CiProofPoolRegressionDecision.ps1",
    "scripts/Get-CiChangeClassification.ps1",
    "scripts/test-ci-change-classifier.ps1",
    "scripts/Assert-CiGateResults.ps1",
    "scripts/test-ci-gate-results.ps1",
    "scripts/validate-agent-skills.ps1",
    "scripts/test-agent-skills-validator.ps1",
    "scripts/test-ci-workflow-contract.ps1",
    "scripts/run-proof-tests.ps1",
    "scripts/Invoke-CiTest.ps1",
    "scripts/TestResultContract.ps1",
    "scripts/ProofEvidence.ps1",
    "scripts/validate-proof-evidence.ps1",
    "scripts/test-proof-evidence.ps1",
    "scripts/MarkdownValidation.ps1",
    "scripts/setup-dev.ps1",
    "scripts/test-agent-readiness.ps1"
)

$tempRoot = Join-Path (Join-Path $repoRootPath "TestResults") (
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
    $toolingPaths = @(
        ".github/scripts/repository-triage.cjs",
        ".github/scripts/repository-triage.test.cjs"
    )
    foreach ($toolingPath in $toolingPaths) {
        $fullPath = Join-Path $tempRoot $toolingPath
        New-Item -ItemType Directory -Path (Split-Path -Parent $fullPath) -Force | Out-Null
        Set-Content -LiteralPath $fullPath -Value "tooling baseline"
    }

    & git -C $tempRoot add .
    & git -C $tempRoot commit --quiet -m "baseline"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not commit temporary baseline."
    }

    foreach ($toolingPath in $toolingPaths) {
        $baseSha = (& git -C $tempRoot rev-parse HEAD).Trim()
        Add-Content -LiteralPath (Join-Path $tempRoot $toolingPath) -Value "tooling change"
        & git -C $tempRoot add .
        & git -C $tempRoot commit --quiet -m "tooling-only change"
        if ($LASTEXITCODE -ne 0) { throw "Could not commit tooling-only fixture." }
        $headSha = (& git -C $tempRoot rev-parse HEAD).Trim()
        $impact = (& (Join-Path $repoRootPath "scripts\Get-CiChangeClassification.ps1") `
            -EventName pull_request -BaseSha $baseSha -HeadSha $headSha -RepoRoot $tempRoot) | ConvertFrom-Json
        $decision = & $selectorPath -EventName pull_request `
            -BaseSha $baseSha -HeadSha $headSha -RepoRoot $tempRoot
        if ($impact.classification -ne "fast_only" -or $decision -ne "false") {
            throw "Tooling-only diff must be fast_only without changing proof-boundary selection."
        }
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
        # Keep the same base so the next diff includes the boundary and tooling.
        foreach ($toolingPath in $toolingPaths) {
            Add-Content -LiteralPath (Join-Path $tempRoot $toolingPath) -Value "mixed tooling change"
        }
        & git -C $tempRoot add .
        & git -C $tempRoot commit --quiet -m "mix tooling with $triggerPath"
        if ($LASTEXITCODE -ne 0) { throw "Could not commit mixed boundary fixture." }
        $headSha = (& git -C $tempRoot rev-parse HEAD).Trim()
        $mixedDecision = & $selectorPath -EventName pull_request `
            -BaseSha $baseSha -HeadSha $headSha -RepoRoot $tempRoot
        $mixedImpact = (& (Join-Path $repoRootPath "scripts\Get-CiChangeClassification.ps1") `
            -EventName pull_request -BaseSha $baseSha -HeadSha $headSha -RepoRoot $tempRoot) | ConvertFrom-Json
        if ($mixedDecision -ne "true" -or $mixedImpact.classification -ne "full") {
            throw "Tooling mixed with proof boundary '$triggerPath' must run full validation and proof regressions."
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

    $e2eHarness = Join-Path $tempRoot 'e2e-runner-harness.ps1'
    Set-Content -LiteralPath $e2eHarness -Value @'
param([string]$Runner, [string]$Root, [int]$Timeout, [int]$TestExit, [string]$Name, [switch]$Override, [int]$Budget)
$ErrorActionPreference = 'Stop'
Set-Location $Root
function Invoke-FakeCiProcess {
    param($FilePath, $ArgumentList, $IdleTimeoutSeconds, $OutputPath)
    Set-Content -LiteralPath 'dotnet-called.txt' -Value 'called'
    $values = @($ArgumentList)
    if ($FilePath -cne 'dotnet' -or $IdleTimeoutSeconds -ne $Timeout -or
        $OutputPath -notlike '*.console.log') {
        throw 'E2E invocation lost its idle deadline or retained console evidence.'
    }
    if ('--blame' -notin $values -or '--blame-hang' -in $values -or '--blame-crash' -in $values) {
        throw 'E2E diagnostics must preserve sequence evidence without a fixture wall timer or memory dumps.'
    }
    $index = [Array]::IndexOf($values, '--filter')
    if ($index -lt 0 -or $values[$index + 1] -cne 'Sentinel.Filter') {
        throw 'Unexpected E2E filter.'
    }
    if ($TestExit -eq 0) {
        $directory = $values[[Array]::IndexOf($values, '--results-directory') + 1]
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $logger = @($values | Where-Object { $_ -like 'trx;LogFileName=*' })[0]
        $report = Join-Path $directory $logger.Substring('trx;LogFileName='.Length)
        Set-Content -LiteralPath $report -Value '<TestRun><ResultSummary><Counters executed="1" /></ResultSummary><Results><UnitTestResult testName="Sentinel" outcome="Passed" /></Results></TestRun>'
    }
    $global:LASTEXITCODE = $TestExit
}
Copy-Item -LiteralPath $Runner -Destination (Join-Path $Root 'Invoke-CiE2e.ps1')
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $Runner) 'CiStepBudget.ps1') -Destination $Root
[IO.File]::WriteAllText((Join-Path $Root 'Invoke-CiProcess.ps1'), ${function:Invoke-FakeCiProcess}.ToString())
$parameters = @{ Name = $Name; Filter = 'Sentinel.Filter' }
if ($Override) { $parameters.HangTimeoutSeconds = $Timeout }
# CI passes the budget in the step environment, never as a runner argument.
if ($Budget) { $env:OPENCLAW_CI_STEP_TIMEOUT_MINUTES = [string]$Budget } else { Remove-Item Env:OPENCLAW_CI_STEP_TIMEOUT_MINUTES -ErrorAction SilentlyContinue }
& (Join-Path $Root 'Invoke-CiE2e.ps1') @parameters
exit $LASTEXITCODE
'@
    $shell = (Get-Process -Id $PID).Path
    $e2eRunnerCases = @(
        @{ Id = 'default'; Timeout = 600; TestExit = 0; ExpectedExit = 0 },
        @{ Id = 'override'; Timeout = 2; TestExit = 0; ExpectedExit = 0; Override = $true },
        @{ Id = 'invalid'; Timeout = 0; TestExit = 0; ExpectedExit = 1; Override = $true },
        @{ Id = 'failed'; Timeout = 600; TestExit = 9; ExpectedExit = 9 },
        @{ Id = 'budget'; Timeout = 600; TestExit = 0; ExpectedExit = 0; Budget = 20 },
        @{ Id = 'budget-failed'; Timeout = 600; TestExit = 9; ExpectedExit = 9; Budget = 20 },
        @{ Id = 'missing-proof'; Timeout = 600; TestExit = 0; ExpectedExit = 1; Name = 'setup-connect' }
    )
    foreach ($case in $e2eRunnerCases) {
        $caseRoot = Join-Path $tempRoot "e2e-runner/$($case.Id)"
        New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
        $caseArguments = @('-Runner', $e2eRunnerPath, '-Root', $caseRoot, '-Timeout', $case.Timeout,
            '-TestExit', $case.TestExit, '-Name', $(if ($case.ContainsKey('Name')) { $case.Name } else { 'network-recovery' }))
        if ($case.ContainsKey('Override')) { $caseArguments += '-Override' }
        if ($case.ContainsKey('Budget')) { $caseArguments += @('-Budget', $case.Budget) }
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $output = & $shell -NoProfile -File $e2eHarness @caseArguments 2>&1
            $exit = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousPreference }
        if ($exit -ne $case.ExpectedExit) {
            throw "E2E runner case '$($case.Id)' expected exit $($case.ExpectedExit), got ${exit}: $output"
        }
        if ($case.Id -eq 'invalid' -and (Test-Path (Join-Path $caseRoot 'dotnet-called.txt'))) {
            throw 'An invalid hang timeout must be rejected before starting tests.'
        }
        if ($case.Id -eq 'missing-proof' -and ($output -join "`n") -notmatch 'did not report the MXC proof') {
            throw "E2E hang protection must preserve required proof assertions: $output"
        }
        $budgetReported = ($output -join "`n") -match "CI step budget: E2E shard 'network-recovery' used \d+\.\d of 20 minutes \(\d+%\)"
        if ($budgetReported -ne $case.ContainsKey('Budget')) {
            throw "E2E runner case '$($case.Id)' must report its step budget only when one is passed, including on failure: $output"
        }
    }
    Write-Host "E2E runner hang-guard contracts passed: $($e2eRunnerCases.Count) cases."
    & (Join-Path $PSScriptRoot 'Test-CiProcess.Tests.ps1')
    # Expected child failures must not become the GitHub Actions step exit code.
    $global:LASTEXITCODE = 0

    $baselineCases = @(
        @{ Name = 'valid'; Version = '2026.9.9'; Json = '{"version":"2026.9.9"}'; Succeeds = $true },
        @{ Name = 'missing-version'; Version = ''; Json = '{"version":"2026.9.9"}'; Succeeds = $false },
        @{ Name = 'missing-metadata'; Version = '2026.9.9'; Json = ''; Succeeds = $false },
        @{ Name = 'malformed-metadata'; Version = '2026.9.9'; Json = '{invalid'; Succeeds = $false },
        @{ Name = 'mismatched-version'; Version = '2026.9.9'; Json = '{"version":"2026.9.8"}'; Succeeds = $false }
    )
    $previousGatewayVersion = $env:OPENCLAW_E2E_GATEWAY_VERSION
    $previousGatewayRelease = $env:GATEWAY_RELEASE_JSON
    try {
        foreach ($lane in $e2eLanes.Keys) {
            $step = Get-StepBlock -Text (Get-JobBlock $lane) -Name 'Record exact Gateway baseline'
            $body = ($step -split '(?m)^      run: \|\r?$', 2)[1] -replace '(?m)^        ', ''
            $recordBaseline = [scriptblock]::Create($body)
            foreach ($case in $baselineCases) {
                $caseRoot = Join-Path $tempRoot "baselines\$lane\$($case.Name)"
                New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
                $env:OPENCLAW_E2E_GATEWAY_VERSION = $case.Version
                $env:GATEWAY_RELEASE_JSON = $case.Json
                $succeeded = $false
                Push-Location $caseRoot
                try {
                    & $recordBaseline
                    $succeeded = $true
                }
                catch {
                    if ($case.Succeeds) { throw }
                }
                finally {
                    Pop-Location
                }
                $artifact = Join-Path $caseRoot 'TestResults\E2E\gateway-release.json'
                if ($succeeded -ne $case.Succeeds -or (Test-Path -LiteralPath $artifact) -ne $case.Succeeds) {
                    throw "WSL baseline case '$($case.Name)' in '$lane' must fail closed before publishing invalid metadata."
                }
                if ($case.Succeeds) {
                    $recorded = Get-Content -LiteralPath $artifact -Raw | ConvertFrom-Json
                    if ($recorded.version -cne $case.Version) {
                        throw "WSL baseline artifact in '$lane' did not retain the exact version."
                    }
                }
            }
        }
    }
    finally {
        $env:OPENCLAW_E2E_GATEWAY_VERSION = $previousGatewayVersion
        $env:GATEWAY_RELEASE_JSON = $previousGatewayRelease
    }
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "CI workflow contracts passed: CodeQL branch/path gating, conservative lane routing, isolated core NuGet caching, stable gate enforcement, decoupled metadata/release builds, preserved test inventory, and fail-closed proof routing." -ForegroundColor Green
