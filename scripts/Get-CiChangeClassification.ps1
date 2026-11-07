<#
.SYNOPSIS
    Classifies changed paths into conservative CI validation lanes.

.DESCRIPTION
    Pull requests receive explicit lane decisions from recognized project
    boundaries. Unknown paths, build or workflow infrastructure, project files,
    classifier contracts, invalid revisions, empty diffs, and non-PR events
    select every lane fail-closed.
#>

[CmdletBinding()]
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$BaseSha,
    [string]$HeadSha = $env:GITHUB_SHA,
    [string]$RepoRoot,
    [string[]]$ChangedPaths,
    [switch]$Explain,
    [switch]$Human
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
$pathReasons = [System.Collections.Generic.List[object]]::new()
$decisionReason = 'recognized-paths'
$paths = @()

function New-Impact([string]$Classification) {
    [ordered]@{
        classification = $Classification
        core_tests = $false
        tray_tests = $false
        ui_tests = $false
        setup_e2e = $false
        revocation_e2e = $false
        network_e2e = $false
        x64_release = $false
        arm64_release = $false
        full = $false
    }
}

function New-FullImpact {
    param(
        [switch]$IncludeArm64
    )

    $impact = New-Impact "full"
    foreach ($lane in @(
            "core_tests",
            "tray_tests",
            "ui_tests",
            "setup_e2e",
            "revocation_e2e",
            "network_e2e",
            "x64_release",
            "full")) {
        $impact[$lane] = $true
    }
    if ($IncludeArm64) {
        $impact.arm64_release = $true
    }
    $impact
}

function Complete-Impact([System.Collections.IDictionary]$Impact) {
    $global:LASTEXITCODE = 0
    if (-not $Explain -and -not $Human) {
        $Impact | ConvertTo-Json -Compress
        return
    }
    $selectedPaths = @(
        foreach ($path in $paths) {
            $known = @($pathReasons | Where-Object { $_.path -ceq [string]$path })
            if ($known.Count) { $known[0] }
            else { [pscustomobject]@{ path = [string]$path; reason = $decisionReason; lanes = @() } }
        }
    )
    $localCommands = @()
    if ($Impact.classification -ne 'docs_only') {
        $localCommands = @(
            '.\build.ps1',
            'dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore',
            'dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore'
        )
    }
    $candidates = @()
    if ($Impact.classification -ne 'docs_only') {
        $inventory = Get-Content -LiteralPath (Join-Path $repoRootPath '.github\proof-pools.json') -Raw | ConvertFrom-Json
        $candidates = @(
            foreach ($pool in $inventory.pools) {
                [pscustomobject][ordered]@{
                    id = $pool.id
                    reason = 'Inventory candidate, not an automatic path-based requirement. Review requiredWhen against the behavior change.'
                    requiredWhen = @($pool.selection.requiredWhen)
                    commands = @($pool.authoritativeCommands)
                    prerequisites = @($pool.requiredCapabilities)
                    doesNotProve = @($pool.selection.doesNotProve)
                    evidencePolicy = $pool.evidencePolicy
                    applicability = 'review-required'
                    authorization = 'explicit-approval-required'
                    status = 'not-run'
                }
            }
        )
    }
    $plan = [ordered]@{
        schemaVersion = 1
        impact = $Impact
        reason = $decisionReason
        selectedPaths = $selectedPaths
        hostedLanes = @('change-classification', 'fast-validation', 'proof-pool-contracts', 'ci-gate') + @(
            if ($Impact.x64_release -or $Impact.arm64_release) { 'metadata'; 'build-msix'; 'build-msix-bundle' }
        ) + @(
            $Impact.Keys | Where-Object { $_ -notin @('classification', 'full') -and $Impact[$_] } | ForEach-Object {
                switch ($_) {
                    'x64_release' { 'build-x64' }
                    'arm64_release' { 'build-arm64' }
                    default { $_.Replace('_', '-') }
                }
            }
        )
        mandatoryLocal = [ordered]@{
            required = $Impact.classification -ne 'docs_only'
            commands = $localCommands
            note = 'Code changes require full build, Shared, and Tray even when hosted lanes are narrower. First-run tests must restore/build; a no-report exit is not a pass. Add subsystem-specific validation separately.'
        }
        customProofCandidates = $candidates
        boundary = 'Read-only plan. Hosted lanes describe impact-selected CI Gate jobs; event/authorization-conditioned reservation and publication jobs remain governed by ci.yml. No tests, runtime launches, authentication, host leases, or CI dispatch. Candidate inventory is not exhaustive behavioral analysis or proof.'
    }
    if ($Human) {
        Write-Output "Classification: $($Impact.classification) ($decisionReason)"
        foreach ($entry in $selectedPaths) { Write-Output "  $($entry.path): $($entry.reason); lanes=$($entry.lanes -join ', ')" }
        Write-Output "Hosted lanes: $($plan.hostedLanes -join ', ')"
        Write-Output "Mandatory local code-change closeout:"
        foreach ($command in $localCommands) { Write-Output "  $command" }
        if (-not $localCommands.Count) { Write-Output '  Documentation checks only; no runtime tests selected.' }
        foreach ($candidate in $candidates) {
            Write-Output "Candidate $($candidate.id): review applicability and obtain explicit approval; not run."
            Write-Output "  Required when: $($candidate.requiredWhen -join '; ')"
            Write-Output "  Prerequisites: $($candidate.prerequisites -join ', ')"
            foreach ($command in $candidate.commands) { Write-Output "  $($command.id): $($command.command)" }
            Write-Output "  Does not prove: $($candidate.doesNotProve -join '; ')"
        }
        Write-Output $plan.boundary
    } else { $plan | ConvertTo-Json -Depth 15 }
}

function Add-PathReason([string]$Path, [string]$Reason, [System.Collections.IDictionary]$Lanes) {
    $pathReasons.Add([pscustomobject]@{
        path = $Path
        reason = $Reason
        lanes = @($Lanes.Keys | Where-Object { $_ -notin @('classification', 'full') -and $Lanes[$_] })
    })
}

function Add-Lanes {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Impact,
        [switch]$Core,
        [switch]$Tray,
        [switch]$Ui,
        [switch]$SetupE2e,
        [switch]$RevocationE2e,
        [switch]$NetworkE2e
    )

    if ($Core) { $Impact.core_tests = $true }
    if ($Tray) { $Impact.tray_tests = $true }
    if ($Ui) { $Impact.ui_tests = $true }
    if ($SetupE2e) { $Impact.setup_e2e = $true }
    if ($RevocationE2e) { $Impact.revocation_e2e = $true }
    if ($NetworkE2e) { $Impact.network_e2e = $true }
}

function ConvertTo-NormalizedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $normalizedPath = $Path.Trim().Replace("\", "/")
    if ($normalizedPath.StartsWith("/", [StringComparison]::Ordinal) -or
        $normalizedPath.Contains("..") -or
        $normalizedPath.Contains([char]0)) {
        return $null
    }
    $normalizedPath
}

function Test-IsSafeDocumentationPath([string]$Path) {
    $rootDocumentation = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    @(
        "AGENTS.md",
        "DEVELOPMENT.md",
        "README.md",
        "SECURITY.md",
        ".github/pull_request_template.md",
        "src/OpenClaw.Tray.WinUI/AGENTS.md"
    ) | ForEach-Object { [void]$rootDocumentation.Add($_) }
    if ($rootDocumentation.Contains($Path)) {
        return $true
    }

    $extension = [System.IO.Path]::GetExtension($Path)
    $safeExtensions = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    @(
        ".md",
        ".excalidraw",
        ".svg",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp"
    ) | ForEach-Object { [void]$safeExtensions.Add($_) }
    if (-not $safeExtensions.Contains($extension)) {
        return $false
    }

    return $Path.StartsWith("docs/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith(".agents/skills/", [StringComparison]::OrdinalIgnoreCase)
}

function Test-IsBuildInfrastructurePath([string]$Path) {
    if ($Path -match '^(?i:\.github/(?:workflows|actions)/|scripts/|installer/|packaging/)' -or
        $Path -match '^(?i:build\.(?:ps1|cmd|bat)|global\.json|NuGet\.Config|Directory\.(?:Build|Packages)\.(?:props|targets)|installer\.iss)$') {
        return $true
    }

    return $Path -match '(?i)(?:^|/)(?:Directory\.(?:Build|Packages)\.(?:props|targets)|[^/]+\.(?:csproj|sln|slnx|wapproj|props|targets|appxmanifest|wxs|iss)|packages\.lock\.json|package(?:-lock)?\.json)$'
}

function Add-PathImpact {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Impact,
        [Parameter(Mandatory)][string]$Path
    )

    if ($Path.Equals(
            "src/OpenClaw.SetupEngine/GatewayInstallPolicy.cs",
            [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray -SetupE2e -RevocationE2e -NetworkE2e
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.Cli/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("src/OpenClaw.WinNode.Cli/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.Chat/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("src/OpenClawTray.FunctionalUI/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray -Ui
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.SetupEngine.UI/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray -Ui -SetupE2e
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.SetupEngine/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray -SetupE2e
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.Connection/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core -Tray -SetupE2e -RevocationE2e -NetworkE2e
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.Shared/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core -Tray -Ui -SetupE2e -RevocationE2e -NetworkE2e
        return $true
    }

    if ($Path.StartsWith("src/OpenClaw.Tray.WinUI/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray -Ui
        if ($Path -match '(?i)(?:setup|onboarding|pairing)') {
            Add-Lanes -Impact $Impact -SetupE2e
        }
        if ($Path -match '(?i)(?:connection|gateway|mcp|node|wsl)') {
            Add-Lanes -Impact $Impact -SetupE2e -RevocationE2e -NetworkE2e
        }
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.Shared.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.Connection.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.WinNode.Cli.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.Shared.TestHost/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.Tray.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.SetupEngine.Tests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClaw.Tray.IntegrationTests/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Tray
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.Tray.UITests/", [StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith("tests/OpenClawTray.FunctionalUI.Tests/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Ui
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.TestSupport/", [StringComparison]::OrdinalIgnoreCase)) {
        Add-Lanes -Impact $Impact -Core -Tray
        return $true
    }

    if ($Path.StartsWith("tests/OpenClaw.E2ETests/", [StringComparison]::OrdinalIgnoreCase)) {
        if ($Path -match '(?i)RevocationAndRecovery') {
            Add-Lanes -Impact $Impact -RevocationE2e
        } elseif ($Path -match '(?i)NetworkRecovery') {
            Add-Lanes -Impact $Impact -NetworkE2e
        } elseif ($Path -match '(?i)(?:SetupAndConnect|MxcSetupAndConnect|MirroredWslPortLease|SshOwnershipAdversarialProof|TrayExecutableResolution)') {
            Add-Lanes -Impact $Impact -SetupE2e
        } else {
            Add-Lanes -Impact $Impact -SetupE2e -RevocationE2e -NetworkE2e
        }
        return $true
    }

    return $false
}

if ($EventName -ne "pull_request") {
    $decisionReason = 'non-pull-request-event'
    $paths = @($ChangedPaths)
    Complete-Impact (New-FullImpact -IncludeArm64)
    return
}

$paths = @()
if ($PSBoundParameters.ContainsKey("ChangedPaths")) {
    $paths = @($ChangedPaths)
} else {
    $shaPattern = "^[0-9a-fA-F]{40}$"
    if ([string]::IsNullOrWhiteSpace($BaseSha) -or
        [string]::IsNullOrWhiteSpace($HeadSha) -or
        $BaseSha -notmatch $shaPattern -or
        $HeadSha -notmatch $shaPattern) {
        Write-Warning "CI diff revisions are missing or invalid. Selecting full validation."
        $decisionReason = 'invalid-or-missing-revisions'
        Complete-Impact (New-FullImpact)
        return
    }

    foreach ($sha in @($BaseSha, $HeadSha)) {
        $revisionAvailable = $false
        try {
            & git -C $repoRootPath cat-file -e "$sha^{commit}" 2>$null
            $revisionAvailable = $LASTEXITCODE -eq 0
        } catch { $revisionAvailable = $false }
        if (-not $revisionAvailable) {
            $global:LASTEXITCODE = 0
            Write-Warning "CI diff revision '$sha' is unavailable. Selecting full validation."
            $decisionReason = 'unavailable-revision'
            Complete-Impact (New-FullImpact)
            return
        }
    }

    try {
        $diffOutput = @(
            & git -C $repoRootPath diff --name-only --no-renames "$BaseSha...$HeadSha" -- 2>&1
        )
        $gitExitCode = $LASTEXITCODE
        $global:LASTEXITCODE = 0
    } catch {
        Write-Warning "CI diff failed. Selecting full validation: $($_.Exception.Message)"
        $decisionReason = 'diff-failed'
        Complete-Impact (New-FullImpact)
        return
    }

    if ($gitExitCode -ne 0) {
        $detail = ($diffOutput | Out-String).Trim()
        Write-Warning "CI diff exited with code $gitExitCode. Selecting full validation: $detail"
        $decisionReason = 'diff-failed'
        Complete-Impact (New-FullImpact)
        return
    }
    $paths = @($diffOutput)
}

if ($paths.Count -eq 0) {
    Write-Warning "CI diff contained no changed paths. Selecting full validation."
    $decisionReason = 'empty-diff'
    Complete-Impact (New-FullImpact)
    return
}

$impact = New-Impact "targeted"
$hasProductChange = $false
$hasFastOnlyChange = $false
foreach ($changedPath in $paths) {
    $normalizedPath = ConvertTo-NormalizedPath ([string]$changedPath)
    if ($null -eq $normalizedPath) {
        $decisionReason = 'invalid-path'
        Add-PathReason ([string]$changedPath) $decisionReason (New-FullImpact)
        Complete-Impact (New-FullImpact)
        return
    }
    # These repository-only tools are exercised by always-on fast-validation.
    if ($normalizedPath -cin @(
            ".github/scripts/repository-triage.cjs",
            ".github/scripts/repository-triage.test.cjs")) {
        $hasFastOnlyChange = $true
        Add-PathReason $changedPath 'always-on-repository-tooling-checks' (New-Impact 'fast_only')
        continue
    }
    if ($normalizedPath.Equals(
            ".agents/skills/winnode/SKILL.md",
            [StringComparison]::OrdinalIgnoreCase)) {
        $hasProductChange = $true
        Add-Lanes -Impact $impact -Core
        $skillImpact = New-Impact 'targeted'
        Add-Lanes -Impact $skillImpact -Core
        Add-PathReason $changedPath 'shipped-winnode-command-contract' $skillImpact
        continue
    }
    if (Test-IsSafeDocumentationPath $normalizedPath) {
        Add-PathReason $changedPath 'maintained-documentation-checks' (New-Impact 'docs_only')
        continue
    }

    $hasProductChange = $true
    if (Test-IsBuildInfrastructurePath $normalizedPath) {
        $decisionReason = 'build-or-validation-infrastructure'
        Add-PathReason $changedPath $decisionReason (New-FullImpact)
        Complete-Impact (New-FullImpact)
        return
    }
    $pathImpact = New-Impact 'targeted'
    if (-not (Add-PathImpact -Impact $pathImpact -Path $normalizedPath)) {
        $decisionReason = 'unknown-path'
        Add-PathReason $changedPath $decisionReason (New-FullImpact)
        Complete-Impact (New-FullImpact)
        return
    }
    Add-PathReason $changedPath 'recognized-project-boundary' $pathImpact
    foreach ($key in $pathImpact.Keys) {
        if ($key -ne 'classification' -and $pathImpact[$key]) { $impact[$key] = $true }
    }
}

if (-not $hasProductChange) {
    $classification = if ($hasFastOnlyChange) { "fast_only" } else { "docs_only" }
    Complete-Impact (New-Impact $classification)
    return
}

Complete-Impact $impact
