<#
.SYNOPSIS
    Tests stable-line selection for read-only MSIX previews.
#>
[CmdletBinding()]
param([string]$RepoRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path $scriptRoot -Parent
}
$resolver = Join-Path $RepoRoot 'scripts\Get-OpenClawMsixPreviewSourceVersion.ps1'

function Assert-Fails {
    param([scriptblock]$Action, [string]$Expected)
    try {
        & $Action | Out-Null
        throw 'The operation unexpectedly succeeded.'
    }
    catch {
        if ($_.Exception.Message.IndexOf($Expected, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Expected '$Expected', received '$($_.Exception.Message)'."
        }
    }
}

foreach ($case in @(
    @{ Tag = 'v2026.9.4'; Expected = '2026.9.4' },
    @{ Tag = 'v2026.9.4-1'; Expected = '2026.9.4' },
    @{ Tag = 'v2026.10.1-11'; Expected = '2026.10.1' },
    @{ Tag = 'v65535.65535.655'; Expected = '65535.65535.655' }
)) {
    if ((& $resolver -CurrentWindowsTag $case.Tag) -cne $case.Expected) {
        throw "Unexpected preview base for $($case.Tag)."
    }
}
foreach ($tag in @(
    '', '2026.9.4', 'v0.9.4', 'v2026.09.4', 'v2026.9.04',
    'v2026.9.4-0', 'v2026.9.4-01', 'v2026.9.4-alpha.1', 'v2026.9.4+meta'
)) {
    if ($tag -eq '') {
        continue
    }
    Assert-Fails { & $resolver -CurrentWindowsTag $tag } 'not a stable or numeric-correction'
}

$releaseJson = @{
    tag_name = 'v2026.9.4'
    draft = $false
    prerelease = $false
    published_at = '2026-09-15T04:42:44Z'
} | ConvertTo-Json -Compress
if ((& $resolver -LatestReleaseJson $releaseJson) -cne '2026.9.4') {
    throw 'Published latest-release response did not resolve the stable app base.'
}

foreach ($mutation in @(
    @{ tag_name = 'v2026.9.4-alpha.1'; draft = $false; prerelease = $true; published_at = 'x' },
    @{ tag_name = 'v2026.9.4'; draft = $true; prerelease = $false; published_at = 'x' },
    @{ tag_name = 'v2026.9.4'; draft = $false; prerelease = $false; published_at = $null },
    @{ tag_name = ''; draft = $false; prerelease = $false; published_at = 'x' }
)) {
    Assert-Fails {
        & $resolver -LatestReleaseJson ($mutation | ConvertTo-Json -Compress)
    } 'missing or is not a published stable'
}

Assert-Fails { & $resolver -LatestReleaseJson '{not-json' } 'malformed'

$source = Get-Content -LiteralPath $resolver -Raw
foreach ($token in @(
    'https://api.github.com/repos/openclaw/openclaw-windows-node/releases/latest',
    "Accept = 'application/vnd.github+json'",
    "'X-GitHub-Api-Version' = '2022-11-28'",
    '$headers.Authorization = [string]::Concat(''Bearer '', $GitHubToken)',
    '-MaximumRedirection 0',
    'for ($attempt = 1; $attempt -le 3; $attempt++)',
    'Start-Sleep -Seconds $attempt',
    'after three attempts'
)) {
    if ($source.IndexOf($token, [StringComparison]::Ordinal) -lt 0) {
        throw "Latest-release request contract is missing '$token'."
    }
}
foreach ($throwMatch in [regex]::Matches($source, "(?m)^\s*throw\s+(?<message>.+)$")) {
    if ($throwMatch.Groups['message'].Value.IndexOf('GitHubToken', [StringComparison]::Ordinal) -ge 0 -or
        $throwMatch.Groups['message'].Value.IndexOf('Authorization', [StringComparison]::Ordinal) -ge 0) {
        throw 'Latest-release errors must not include token-bearing request details.'
    }
}

Write-Host 'MSIX preview source tests passed: stable/correction bases, canonical API lookup, and fail-closed release validation.'
