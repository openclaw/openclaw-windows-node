<#
.SYNOPSIS
    Normalizes a CI checkout for GitVersion once, before parallel builds read it.

.DESCRIPTION
    GitVersion.MsBuild runs GitVersion once per project. On a build server each
    run first normalizes the checkout: it creates a local branch for every
    remote-tracking branch and attaches HEAD. GitVersion 7 performs those writes
    with one git process per reference and configuration change, so on a fresh
    checkout the first run rewrites .git/config and .git/HEAD for minutes. Sibling
    project builds read the same files at the same time (GitVersion and
    Microsoft.Build.Tasks.Git) without sharing deletion, so Windows rejects them
    while git replaces a file: "The process cannot access the file '.git\config'
    because it is being used by another process."

    This script performs the normalization once, serially, with the GitVersion
    tool pinned in .config/dotnet-tools.json. In GitHub Actions it then sets
    GitVersion_NoNormalizeEnabled and GitVersion_NoFetchEnabled for the remaining
    job steps, so every later GitVersion.MsBuild run only reads the prepared
    checkout.

    The script leaves the checkout unchanged when GitVersion_NoNormalizeEnabled is
    already 'true'. Release-tag and candidate-promotion runs set it that way to
    keep their selected version source.

.EXAMPLE
    # In a CI job, after setup-dotnet and before the first dotnet build:
    ./scripts/Initialize-GitVersionCheckout.ps1
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:GitVersion_NoNormalizeEnabled -eq 'true') {
    Write-Host 'GitVersion normalization is disabled for this run; leaving the checkout unchanged.'
    return
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore failed with exit code $LASTEXITCODE."
    }

    $arguments = @('tool', 'run', 'dotnet-gitversion', '--', $repoRoot, '--output', 'json', '--telemetry-opt-out')
    if ($env:GitVersion_NoFetchEnabled -eq 'true') {
        $arguments += '--no-fetch'
    }

    $output = (& dotnet @arguments) -join "`n"
    $exitCode = $LASTEXITCODE
    $jsonStart = $output.IndexOf('{')
    $jsonEnd = $output.LastIndexOf('}')
    $version = $null
    if ($exitCode -eq 0 -and $jsonStart -ge 0 -and $jsonEnd -gt $jsonStart) {
        try {
            $version = $output.Substring($jsonStart, $jsonEnd - $jsonStart + 1) | ConvertFrom-Json
        }
        catch {
            $version = $null
        }
    }
    if ($null -eq $version -or [string]::IsNullOrWhiteSpace($version.FullSemVer)) {
        Write-Host $output
        throw "GitVersion could not normalize the checkout (exit code $exitCode)."
    }
    Write-Host "Normalized the checkout once for GitVersion $($version.FullSemVer) on branch '$($version.BranchName)'."
}
finally {
    Pop-Location
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
    Add-Content -LiteralPath $env:GITHUB_ENV -Value @(
        'GitVersion_NoNormalizeEnabled=true',
        'GitVersion_NoFetchEnabled=true'
    )
}
