<#
.SYNOPSIS
    Proves exact alpha and stable tags on a selective release branch while
    newer main changes remain excluded. Uses the pinned GitVersion tool.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixtures = Join-Path ([IO.Path]::GetTempPath()) "openclaw-release-version-$([guid]::NewGuid().ToString('N'))"
$environmentNames = @('GITHUB_ACTIONS', 'GITHUB_REF', 'GITHUB_SHA', 'GITHUB_HEAD_REF', 'GITHUB_BASE_REF')
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    [Environment]::SetEnvironmentVariable($name, $null)
}

function Invoke-FixtureGit {
    param([string]$Repository)
    & git -C $Repository @args
    if ($LASTEXITCODE -ne 0) { throw "Fixture git command failed: $($args[0])" }
}

function Assert-FixtureVersion {
    param([string]$Repository, [string]$ExpectedVersion, [string]$ExpectedSha)
    $json = & dotnet tool run dotnet-gitversion -- $Repository /output json /nonormalize /nocache
    if ($LASTEXITCODE -ne 0) { throw 'GitVersion fixture execution failed. Run dotnet tool restore first.' }
    $version = $json | ConvertFrom-Json
    if ($version.SemVer -cne $ExpectedVersion) {
        throw "Expected '$ExpectedVersion', got '$($version.SemVer)' on '$($version.BranchName)'."
    }
    if ($version.Sha -cne $ExpectedSha -or (Invoke-FixtureGit $Repository rev-parse HEAD).Trim() -cne $ExpectedSha) {
        throw 'GitVersion selected a different product commit.'
    }
}

$cases = @(
    @{ Name = 'lightweight-alpha'; Version = '2026.10.5-alpha.2'; Annotated = $false },
    @{ Name = 'annotated-alpha'; Version = '2026.10.5-alpha.2'; Annotated = $true },
    @{ Name = 'lightweight-stable'; Version = '2026.10.5'; Annotated = $false },
    @{ Name = 'annotated-stable'; Version = '2026.10.5'; Annotated = $true }
)

New-Item -ItemType Directory -Path $fixtures | Out-Null
Push-Location $root
try {
    foreach ($case in $cases) {
        $repository = Join-Path $fixtures $case.Name
        New-Item -ItemType Directory -Path $repository | Out-Null
        Invoke-FixtureGit $repository init --quiet --initial-branch=main
        Invoke-FixtureGit $repository config user.name 'Release version fixture'
        Invoke-FixtureGit $repository config user.email 'release@example.invalid'
        Invoke-FixtureGit $repository config commit.gpgsign false
        Invoke-FixtureGit $repository config tag.gpgsign false
        Copy-Item -LiteralPath (Join-Path $root 'GitVersion.yml') -Destination $repository
        Invoke-FixtureGit $repository add GitVersion.yml
        Invoke-FixtureGit $repository commit --quiet -m 'Stable baseline'
        Invoke-FixtureGit $repository tag v2026.9.8
        Invoke-FixtureGit $repository commit --quiet --allow-empty -m 'Previous published alpha'
        Invoke-FixtureGit $repository tag v2026.10.4-alpha.6
        $previous = (Invoke-FixtureGit $repository rev-parse HEAD).Trim()
        Invoke-FixtureGit $repository commit --quiet --allow-empty -m 'Main changes excluded from the backport'
        Invoke-FixtureGit $repository tag v2026.10.5-alpha.1
        $main = (Invoke-FixtureGit $repository rev-parse HEAD).Trim()

        $tag = "v$($case.Version)"
        Invoke-FixtureGit $repository checkout --quiet -b "release/$tag" $previous
        Invoke-FixtureGit $repository commit --quiet --allow-empty -m 'Selected backport only'
        $candidate = (Invoke-FixtureGit $repository rev-parse HEAD).Trim()
        if ($case.Annotated) {
            Invoke-FixtureGit $repository tag -a $tag -m 'Selective release'
        } else {
            Invoke-FixtureGit $repository tag $tag
        }
        Assert-FixtureVersion $repository $case.Version $candidate

        Invoke-FixtureGit $repository checkout --quiet --detach $candidate
        $env:GITHUB_ACTIONS = 'true'
        $env:GITHUB_REF = "refs/tags/$tag"
        $env:GITHUB_SHA = $candidate
        try {
            Assert-FixtureVersion $repository $case.Version $candidate
        } finally {
            foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $null) }
        }

        Invoke-FixtureGit $repository checkout --quiet main
        Assert-FixtureVersion $repository '2026.10.5-alpha.1' $main
        Write-Host "Passed $($case.Name): exact release branch and tag checkout; newer main unchanged."
    }
    Write-Host 'Passed real GitVersion release-branch regression: 4 fixtures, 12 version/source checks.'
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    Pop-Location
    Remove-Item -LiteralPath $fixtures -Recurse -Force
}
