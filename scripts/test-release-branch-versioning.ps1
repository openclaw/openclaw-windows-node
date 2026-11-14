<#
.SYNOPSIS
    Proves exact alpha and stable tags on a selective release branch while
    newer main changes remain excluded, and a stable version for merge queue
    group branches. Also exercises GitVersion.MsBuild on release retries after
    an annotated MSIX reservation exists. Uses the repository's GitVersion pins.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixtures = Join-Path ([IO.Path]::GetTempPath()) "openclaw-release-version-$([guid]::NewGuid().ToString('N'))"
$environmentNames = @('GITHUB_ACTIONS', 'GITHUB_REF', 'GITHUB_REF_TYPE', 'GITHUB_SHA',
    'GITHUB_HEAD_REF', 'GITHUB_BASE_REF', 'GITHUB_ENV',
    'GitVersion_NoNormalizeEnabled', 'GitVersion_NoFetchEnabled',
    'OPENCLAW_RELEASE_SOURCE_SHA', 'OPENCLAW_RELEASE_REF', 'OPENCLAW_RELEASE_TAG')
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

    $buildProps = [xml](Get-Content (Join-Path $root 'src\Directory.Build.props') -Raw)
    $packageVersion = $buildProps.SelectSingleNode('//PackageReference[@Include="GitVersion.MsBuild"]').Version
    $workflow = Get-Content (Join-Path $root '.github\workflows\ci.yml') -Raw
    $sourceStep = [regex]::Match($workflow,
        '(?ms)      name: Bind tagged versioning to the checked-out release\r?\n.*?      run: \|\r?\n(?<script>(?:        [^\r\n]*\r?\n)+)')
    if (-not $sourceStep.Success) { throw 'Missing tagged release source binding step.' }
    $sourceScript = [scriptblock]::Create(($sourceStep.Groups['script'].Value -replace '(?m)^        ', ''))
    foreach ($case in @(
        @{ Name = 'alpha-retry'; Version = '2026.11.3-alpha.90'; Annotated = $false },
        @{ Name = 'stable-retry'; Version = '2026.11.3'; Annotated = $true }
    )) {
        $source = Join-Path $fixtures $case.Name
        New-Item -ItemType Directory -Path $source | Out-Null
        Invoke-FixtureGit $source init --quiet --initial-branch=main
        Invoke-FixtureGit $source config user.name 'Release retry fixture'
        Invoke-FixtureGit $source config user.email 'release@example.invalid'
        Invoke-FixtureGit $source config commit.gpgsign false
        Invoke-FixtureGit $source config tag.gpgsign false
        Copy-Item -LiteralPath (Join-Path $root 'GitVersion.yml') -Destination $source
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="GitVersion.MsBuild" Version="$packageVersion" PrivateAssets="all" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $source 'VersionProbe.csproj')
        Invoke-FixtureGit $source add GitVersion.yml VersionProbe.csproj
        Invoke-FixtureGit $source commit --quiet -m 'Stable baseline'
        Invoke-FixtureGit $source tag v2026.11.2
        Invoke-FixtureGit $source commit --quiet --allow-empty -m 'Release candidate'
        $candidate = (Invoke-FixtureGit $source rev-parse HEAD).Trim()
        $tag = "v$($case.Version)"
        if ($case.Annotated) {
            Invoke-FixtureGit $source tag -a $tag -m 'Release'
        } else {
            Invoke-FixtureGit $source tag $tag
        }
        $main = $candidate
        $reservation = 'msix-package/2026.11.3/305'

        foreach ($attempt in @('initial', 'retry-negative', 'retry')) {
            if ($attempt -eq 'retry-negative') {
                Invoke-FixtureGit $source tag -a $reservation $candidate -m 'MSIX reservation'
                Invoke-FixtureGit $source commit --quiet --allow-empty -m 'Main moved beyond the release'
                $main = (Invoke-FixtureGit $source rev-parse HEAD).Trim()
                Invoke-FixtureGit $source branch feature/after-release
            }
            $checkout = Join-Path $fixtures "$($case.Name)-$attempt"
            New-Item -ItemType Directory -Path $checkout | Out-Null
            # Match actions/checkout at a SHA: remote branches and tags, no local main or origin/HEAD.
            Invoke-FixtureGit $checkout init --quiet
            Invoke-FixtureGit $checkout remote add origin $source
            Invoke-FixtureGit $checkout fetch --quiet --tags origin '+refs/heads/*:refs/remotes/origin/*'
            Invoke-FixtureGit $checkout checkout --quiet --detach $candidate
            $project = Join-Path $checkout 'VersionProbe.csproj'
            & dotnet restore $project --verbosity quiet
            if ($LASTEXITCODE -ne 0) { throw 'Could not restore the pinned GitVersion.MsBuild fixture.' }
            $env:GITHUB_ACTIONS = 'true'
            $env:GITHUB_REF = "refs/tags/$tag"
            $env:GITHUB_REF_TYPE = 'tag'
            $env:GITHUB_SHA = $candidate
            $env:GITHUB_ENV = Join-Path $fixtures "$($case.Name)-$attempt.env"
            $env:GitVersion_NoNormalizeEnabled = ($attempt -eq 'retry').ToString().ToLowerInvariant()
            $env:GitVersion_NoFetchEnabled = $env:GitVersion_NoNormalizeEnabled
            $arguments = @($project, '--no-restore', '-nologo', '-verbosity:minimal',
                '-property:GitVersion_NoCacheEnabled=true')
            try {
                if ($attempt -eq 'retry') {
                    $env:OPENCLAW_RELEASE_SOURCE_SHA = $main
                    $env:OPENCLAW_RELEASE_REF = "refs/tags/$tag"
                    $env:OPENCLAW_RELEASE_TAG = $tag
                    Push-Location $checkout
                    try {
                        try {
                            & $sourceScript
                            throw 'Mismatched checkout source was accepted.'
                        } catch {
                            if (-not $_.Exception.Message.Contains('Release checkout does not match')) { throw }
                        }
                        $env:OPENCLAW_RELEASE_SOURCE_SHA = $candidate
                        $env:OPENCLAW_RELEASE_REF = 'refs/tags/v2026.11.2'
                        try {
                            & $sourceScript
                            throw 'Mismatched release tag was accepted.'
                        } catch {
                            if (-not $_.Exception.Message.Contains('Release tag does not match')) { throw }
                        }
                        $env:OPENCLAW_RELEASE_REF = "refs/tags/$tag"
                        & $sourceScript
                    } finally { Pop-Location }
                }
                $output = & dotnet build @arguments 2>&1
                if ($attempt -eq 'retry-negative') {
                    if ($LASTEXITCODE -eq 0 -or "$output" -notlike '*Found more than one remote tip*' -or
                        "$output" -notlike "*refs/tags/$reservation^{}*" -or "$output" -notlike "*refs/tags/$tag*") {
                        throw "Negative control did not reproduce the two-tag normalization failure: $output"
                    }
                    Write-Host "Reproduced $($case.Name): normalization rejects release and annotated reservation tips."
                    continue
                }
                if ($LASTEXITCODE -ne 0) { throw "Release $attempt versioning failed: $output" }
                if ($attempt -eq 'retry') {
                    # The metadata action must use the same no-normalization policy.
                    Assert-FixtureVersion $checkout $case.Version $candidate
                    if ((Invoke-FixtureGit $checkout cat-file -t "refs/tags/$reservation").Trim() -cne 'tag' -or
                        (Invoke-FixtureGit $checkout rev-parse "$reservation^{commit}").Trim() -cne $candidate) {
                        throw 'Versioning changed the annotated reservation.'
                    }
                }
                $version = Get-Content (Join-Path $checkout 'obj\gitversion.json') -Raw | ConvertFrom-Json
                $assembly = Join-Path $checkout 'bin\Debug\net10.0\VersionProbe.dll'
                $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion.Split('+')[0]
                if ($version.SemVer -cne $case.Version -or $version.Sha -cne $candidate -or
                    $productVersion -cne $case.Version -or
                    (Invoke-FixtureGit $checkout rev-parse HEAD).Trim() -cne $candidate -or
                    (Invoke-FixtureGit $checkout rev-parse refs/remotes/origin/main).Trim() -cne $main) {
                    throw "Release $attempt selected the wrong version or source: $output"
                }
            } finally {
                foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $null) }
            }
        }
        Write-Host "Passed $($case.Name): initial and retry MSBuild versions match $($case.Version) at $candidate."
    }
    Write-Host 'Passed real GitVersion regression: 4 release-branch fixtures (12 version/source checks) and 1 merge queue fixture (2 checks).'
    Write-Host 'Passed 2 release-retry fixtures: 4 MSBuild version/source/assembly checks, 2 expected normalization failures, 2 metadata tool checks, 4 source/tag rejection checks.'
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    Pop-Location
    Remove-Item -LiteralPath $fixtures -Recurse -Force
}
