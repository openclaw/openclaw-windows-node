<#
.SYNOPSIS
    Exercises real GitVersion with an older alpha, a stable tag on the same
    commit, and newer main commits. Uses only a disposable local repository.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) "openclaw-promotion-version-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixture | Out-Null
$savedEnvironment = @{}
foreach ($name in @('GITHUB_ACTIONS', 'GITHUB_REF', 'GITHUB_SHA')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Invoke-FixtureGit {
    & git -C $fixture @args
    if ($LASTEXITCODE -ne 0) { throw "Fixture git command failed: $($args[0])" }
}
function Get-FixtureVersion {
    $json = & dotnet tool run dotnet-gitversion -- $fixture /output json /nonormalize /nocache
    if ($LASTEXITCODE -ne 0) { throw 'GitVersion fixture execution failed. Run dotnet tool restore if the tool is missing.' }
    ($json | ConvertFrom-Json).SemVer
}

Push-Location $root
try {
    Invoke-FixtureGit init --quiet --initial-branch=main
    Invoke-FixtureGit config user.name 'Promotion fixture'
    Invoke-FixtureGit config user.email 'promotion@example.invalid'
    Invoke-FixtureGit config commit.gpgsign false
    Invoke-FixtureGit config tag.gpgsign false
    Copy-Item -LiteralPath (Join-Path $root 'GitVersion.yml') -Destination $fixture
    Invoke-FixtureGit add GitVersion.yml
    Invoke-FixtureGit commit --quiet -m 'Baseline'
    Invoke-FixtureGit tag v2026.9.4
    Invoke-FixtureGit commit --quiet --allow-empty -m 'Candidate'
    $candidate = (Invoke-FixtureGit rev-parse HEAD).Trim()
    Invoke-FixtureGit tag v2026.9.5-alpha.93
    Invoke-FixtureGit commit --quiet --allow-empty -m 'Newer changes excluded from promotion'
    Invoke-FixtureGit tag v2026.9.5-alpha.94
    $pipeline = (Invoke-FixtureGit rev-parse HEAD).Trim()
    Invoke-FixtureGit checkout --quiet --detach $candidate
    if ((Get-FixtureVersion) -cne '2026.9.5-alpha.93') { throw 'Candidate did not resolve to its original alpha.' }
    Invoke-FixtureGit tag -a v2026.9.5 -m 'Stable promotion'
    $env:GITHUB_ACTIONS = 'true'
    $env:GITHUB_REF = 'refs/heads/main'
    $env:GITHUB_SHA = $pipeline
    if ((Get-FixtureVersion) -cne '2026.9.5') { throw 'Alpha and stable tags at the same SHA did not resolve to stable.' }
    if ((Invoke-FixtureGit rev-parse HEAD).Trim() -cne $candidate) { throw 'GitVersion replaced the product checkout with the pipeline revision.' }
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    Invoke-FixtureGit checkout --quiet main
    Invoke-FixtureGit commit --quiet --allow-empty -m 'Next daily alpha'
    $next = Get-FixtureVersion
    if ($next -cnotmatch '\A2026\.9\.6-alpha\.\d+\z') { throw "Unexpected next main version: $next" }
    Write-Host "Passed real GitVersion alpha/stable same-SHA and next-main tests ($next)."
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    Pop-Location
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
