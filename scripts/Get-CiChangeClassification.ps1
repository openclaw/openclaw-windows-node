<#
.SYNOPSIS
    Classifies a workflow change as docs_only or full.

.DESCRIPTION
    Pull requests use the docs-only fast path only when every changed path is
    explicitly allowlisted. All non-PR events and every indeterminate diff
    classify as full.
#>

[CmdletBinding()]
param(
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$BaseSha,
    [string]$HeadSha = $env:GITHUB_SHA,
    [string]$RepoRoot,
    [string[]]$ChangedPaths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $scriptRoot
}
$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)

function Complete-Classification([string]$Classification) {
    $global:LASTEXITCODE = 0
    $Classification
}

function Test-IsSafeDocumentationPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    $normalizedPath = $Path.Trim().Replace("\", "/")
    if ($normalizedPath.StartsWith("/", [StringComparison]::Ordinal) -or
        $normalizedPath.Contains("..", [StringComparison]::Ordinal) -or
        $normalizedPath.Contains([char]0)) {
        return $false
    }

    $rootMarkdown = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    @(
        "AGENTS.md",
        "DEVELOPMENT.md",
        "README.md",
        "SECURITY.md",
        ".github/pull_request_template.md"
    ) | ForEach-Object { [void]$rootMarkdown.Add($_) }
    if ($rootMarkdown.Contains($normalizedPath)) {
        return $true
    }

    $extension = [System.IO.Path]::GetExtension($normalizedPath)
    $safeDocumentationExtensions = [System.Collections.Generic.HashSet[string]]::new(
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
    ) | ForEach-Object { [void]$safeDocumentationExtensions.Add($_) }

    if (-not $safeDocumentationExtensions.Contains($extension)) {
        return $false
    }

    if ($normalizedPath.StartsWith("docs/", [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $normalizedPath.StartsWith(
        ".agents/skills/",
        [StringComparison]::OrdinalIgnoreCase)
}

if ($EventName -ne "pull_request") {
    Complete-Classification "full"
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
        Complete-Classification "full"
        return
    }

    foreach ($sha in @($BaseSha, $HeadSha)) {
        & git -C $repoRootPath cat-file -e "$sha^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) {
            $global:LASTEXITCODE = 0
            Write-Warning "CI diff revision '$sha' is unavailable. Selecting full validation."
            Complete-Classification "full"
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
        Complete-Classification "full"
        return
    }

    if ($gitExitCode -ne 0) {
        $detail = ($diffOutput | Out-String).Trim()
        Write-Warning "CI diff exited with code $gitExitCode. Selecting full validation: $detail"
        Complete-Classification "full"
        return
    }
    $paths = @($diffOutput)
}

if ($paths.Count -eq 0) {
    Write-Warning "CI diff contained no changed paths. Selecting full validation."
    Complete-Classification "full"
    return
}

foreach ($changedPath in $paths) {
    if (-not (Test-IsSafeDocumentationPath ([string]$changedPath))) {
        Complete-Classification "full"
        return
    }
}

Complete-Classification "docs_only"
