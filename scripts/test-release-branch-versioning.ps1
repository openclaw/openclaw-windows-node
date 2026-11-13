<#
.SYNOPSIS
    Proves exact alpha and stable tags on a selective release branch while
    newer main changes remain excluded, and a stable version for merge queue
    group branches. Uses the pinned GitVersion tool.
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

    # Merge queue groups build on gh-readonly-queue/<base>/pr-<n>-<base sha> branches with normalization on,
    # as the metadata job does. Another group's branch appearing or disappearing between the metadata
    # and publish jobs must not change the version, or the publish job's ProductVersion check fails.
    $queue = Join-Path $fixtures 'merge-queue'
    $origin = Join-Path $fixtures 'merge-queue-origin.git'
    New-Item -ItemType Directory -Path $queue | Out-Null
    Invoke-FixtureGit $queue init --quiet --initial-branch=main
    Invoke-FixtureGit $queue config user.name 'Release version fixture'
    Invoke-FixtureGit $queue config user.email 'release@example.invalid'
    Invoke-FixtureGit $queue config commit.gpgsign false
    Invoke-FixtureGit $queue config tag.gpgsign false
    Copy-Item -LiteralPath (Join-Path $root 'GitVersion.yml') -Destination $queue
    Invoke-FixtureGit $queue add GitVersion.yml
    Invoke-FixtureGit $queue commit --quiet -m 'Stable baseline'
    Invoke-FixtureGit $queue tag v2026.9.8
    Invoke-FixtureGit $queue commit --quiet --allow-empty -m 'Published alpha'
    Invoke-FixtureGit $queue tag v2026.10.5-alpha.1
    Invoke-FixtureGit $queue commit --quiet --allow-empty -m 'Main after the alpha'
    $base = (Invoke-FixtureGit $queue rev-parse HEAD).Trim()
    Invoke-FixtureGit $queue checkout --quiet --detach $base
    Invoke-FixtureGit $queue commit --quiet --allow-empty -m 'First queued pull request'
    $group = (Invoke-FixtureGit $queue rev-parse HEAD).Trim()
    $groupBranch = "gh-readonly-queue/main/pr-1-$base"
    & git clone --quiet --bare $queue $origin
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the merge queue origin fixture.' }
    Invoke-FixtureGit $queue remote add origin $origin
    Invoke-FixtureGit $queue push --quiet origin "${group}:refs/heads/$groupBranch"
    Invoke-FixtureGit $queue fetch --quiet origin
    $readQueueVersion = {
        Invoke-FixtureGit $queue checkout --quiet --detach $group
        foreach ($local in @(Invoke-FixtureGit $queue for-each-ref --format='%(refname:short)' refs/heads)) {
            if ($local -cne 'main') { Invoke-FixtureGit $queue branch --quiet -D $local }
        }
        $env:GITHUB_ACTIONS = 'true'
        $env:GITHUB_REF = "refs/heads/$groupBranch"
        $env:GITHUB_SHA = $group
        try {
            $json = & dotnet tool run dotnet-gitversion -- $queue /output json /nocache
            if ($LASTEXITCODE -ne 0) { throw 'GitVersion failed on a merge queue branch.' }
        } finally {
            foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $null) }
        }
        $version = $json | ConvertFrom-Json
        if ($version.Sha -cne $group) { throw 'GitVersion selected a different merge group commit.' }
        $version.SemVer
    }
    $alone = & $readQueueVersion
    if ($alone -cnotmatch "^2026\.10\.5-gh-readonly-queue-main-pr-1-$base\.1$") { throw "Unexpected merge queue version '$alone'." }
    Invoke-FixtureGit $queue checkout --quiet --detach $group
    Invoke-FixtureGit $queue commit --quiet --allow-empty -m 'Second queued pull request'
    Invoke-FixtureGit $queue push --quiet origin "HEAD:refs/heads/gh-readonly-queue/main/pr-2-$group"
    Invoke-FixtureGit $queue fetch --quiet origin
    $withNext = & $readQueueVersion
    if ($withNext -cne $alone) { throw "A later merge group changed this group's version from '$alone' to '$withNext'." }
    Write-Host "Passed merge queue group versioning: $alone, unchanged by later groups."
    Write-Host 'Passed real GitVersion regression: 4 release-branch fixtures (12 version/source checks) and 1 merge queue fixture (2 checks).'
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    Pop-Location
    Remove-Item -LiteralPath $fixtures -Recurse -Force
}
