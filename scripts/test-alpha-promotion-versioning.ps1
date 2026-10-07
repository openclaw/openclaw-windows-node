<#
.SYNOPSIS
    Exercises real GitVersion with an older alpha, a stable tag on the same
    commit, checkout-only candidate tags, and newer main commits. Uses disposable repositories.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path ([IO.Path]::GetTempPath()) "openclaw-promotion-version-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixture | Out-Null
$savedEnvironment = @{}
foreach ($name in @('GITHUB_ACTIONS', 'GITHUB_REF', 'GITHUB_SHA', 'OPENCLAW_RELEASE_TAG', 'OPENCLAW_RELEASE_SOURCE_SHA')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Invoke-FixtureGit {
    & git -C $fixture @args
    if ($LASTEXITCODE -ne 0) { throw "Fixture git command failed: $($args[0])" }
}
function Get-FixtureVersion {
    param([string]$Repository = $fixture)
    $json = & dotnet tool run dotnet-gitversion -- $Repository /output json /nonormalize /nocache
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
    # Execute the actual workflow snippet in a separate runner checkout.
    $checkout = Join-Path $fixture 'runner-checkout'
    & git clone --quiet --no-local $fixture $checkout
    if ($LASTEXITCODE -ne 0) { throw 'Could not clone the candidate fixture.' }
    & git -C $checkout checkout --quiet --detach $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Could not select the candidate fixture.' }
    $workflow = Get-Content (Join-Path $root '.github/workflows/ci.yml') -Raw
    $localTagStep = [regex]::Match($workflow,
        '(?ms)      name: Create checkout-only stable tag for candidate versioning\r?\n.*?      run: \|\r?\n(?<script>(?:        [^\r\n]*\r?\n)+)')
    if (-not $localTagStep.Success) { throw 'Missing checkout-only tag step.' }
    $tagScript = [scriptblock]::Create(($localTagStep.Groups['script'].Value -replace '(?m)^        ', ''))
    $env:OPENCLAW_RELEASE_TAG = 'v2026.9.5'
    $env:OPENCLAW_RELEASE_SOURCE_SHA = $candidate
    Push-Location $checkout
    try {
        & $tagScript
        & $tagScript # Identical checkout-only tags are safe to reuse.
    } finally { Pop-Location }
    $env:GITHUB_ACTIONS = 'true'
    $env:GITHUB_REF = 'refs/heads/main'
    $env:GITHUB_SHA = $pipeline
    if ((Get-FixtureVersion $checkout) -cne '2026.9.5') { throw 'Alpha and stable tags at the same SHA did not resolve to stable.' }
    if ((& git -C $checkout rev-parse HEAD).Trim() -cne $candidate) { throw 'GitVersion replaced the product checkout with the pipeline revision.' }
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    & git -C $fixture show-ref --verify --quiet refs/tags/v2026.9.5
    if ($LASTEXITCODE -eq 0) { throw 'Preparation leaked a public stable tag.' }
    Invoke-FixtureGit checkout --quiet main
    if ((Get-FixtureVersion) -cne '2026.9.5-alpha.94') { throw 'Preparation advanced main versioning.' }
    Push-Location $checkout
    try {
        $env:OPENCLAW_RELEASE_TAG = 'v2026.9.5'
        $env:OPENCLAW_RELEASE_SOURCE_SHA = $pipeline
        try {
            & $tagScript
            throw 'Conflicting checkout-only tag was accepted.'
        } catch {
            if (-not $_.Exception.Message.Contains('another candidate')) { throw }
        }
    } finally { Pop-Location }
    # Public tag creation represents the approved publication boundary.
    Invoke-FixtureGit tag -a v2026.9.5 $candidate -m 'Stable promotion'
    Invoke-FixtureGit commit --quiet --allow-empty -m 'Next daily alpha'
    $next = Get-FixtureVersion
    if ($next -cnotmatch '\A2026\.9\.6-alpha\.\d+\z') { throw "Unexpected next main version: $next" }
    Write-Host "Passed real GitVersion checkout-only cancellation, same-SHA stable build, conflict, and published next-main tests ($next)."
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    Pop-Location
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
