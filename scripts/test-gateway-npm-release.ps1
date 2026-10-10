#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'GatewayNpmRelease.psm1') -Force
$module = Get-Module GatewayNpmRelease
$script:passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
function Assert-Throws([scriptblock]$Action, [string]$Expected) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        $script:passed++
        return
    }
    throw "Expected failure containing '$Expected'."
}

$latest = Resolve-GatewayBuildInput -Parameters @{}
Assert-True ($latest.NpmVersion -ceq 'latest' -and $latest.Patch -ceq 'npm-latest') 'Default must use npm latest.'
$packaged = Resolve-GatewayBuildInput -Parameters @{ GatewayChannel = 'Packaged'; PackagingRelease = 'v2026.9.9-msix.0' }
Assert-True (-not $packaged.NpmVersion -and $packaged.Patch -ceq 'packaged') 'Packaged must not resolve an npm tag.'
function Resolve-BoundInput {
    param([string]$GatewayChannel)
    Resolve-GatewayBuildInput -Parameters $PSBoundParameters
}
$bound = Resolve-BoundInput -GatewayChannel Latest
Assert-True ($bound.NpmVersion -ceq 'latest') 'Real PowerShell bound parameters must be supported, not just hashtables.'
foreach ($selector in @('OpenClawRef', 'OpenClawSourceDirectory', 'OpenClawPackageDirectory')) {
    $source = Resolve-GatewayBuildInput -Parameters @{ $selector = 'source-input' }
    Assert-True (-not $source.NpmVersion -and $source.Patch -ceq 'source') 'Explicit source inputs must not resolve npm.'
    Assert-Throws { Resolve-GatewayBuildInput -Parameters @{ $selector = 'input'; GatewayChannel = 'Packaged' } } 'only one Gateway input'
    Assert-Throws { Resolve-GatewayBuildInput -Parameters @{ $selector = 'input'; OpenClawNpmVersion = 'latest' } } 'only one Gateway input'
    Assert-Throws { Resolve-GatewayBuildInput -Parameters @{ $selector = '' } } 'must not be empty'
}
$exact = Resolve-GatewayBuildInput -Parameters @{ OpenClawNpmVersion = '2026.8.35'; Patch = 'Pinned' }
Assert-True ($exact.NpmVersion -ceq '2026.8.35' -and $exact.Patch -ceq 'pinned') 'Exact pins and explicit patch names must survive.'
Assert-Throws { Resolve-GatewayBuildInput -Parameters @{ Unregister = $true } } 'requires -Patch'
Assert-Throws { Resolve-GatewayBuildInput -Parameters @{ Patch = '../bad' } } '1 to 15'
$remove = Resolve-GatewayBuildInput -Parameters @{ Unregister = $true; Patch = 'npm-latest' }
Assert-True (-not $remove.NpmVersion) 'Unregister must not fetch npm.'
Assert-Throws { Resolve-GatewayBuildInput -Parameters @{ PackagingRelease = 'v2026.9.9-msix.0' } } 'requires -GatewayChannel Packaged'
Assert-Throws { Resolve-GatewayBuildInput -Parameters @{ GatewayChannel = 'Latest'; PackagingRelease = 'v2026.9.9-msix.0' } } 'requires -GatewayChannel Packaged'
Assert-Throws { Resolve-GatewayBuildInput -Parameters @{ GatewayChannel = 'Packaged'; PackagingRef = 'main' } } 'cannot override'
Assert-Throws { Resolve-GatewayBuildInput -Parameters @{ GatewayChannel = 'Packaged'; PackagingDirectory = 'checkout' } } 'cannot override'

$launch = Resolve-GatewayLaunchInput
Assert-True ($launch.Build -and $launch.Patch -ceq 'npm-latest') 'Local launch must prepare latest.'
$launch = Resolve-GatewayLaunchInput -GatewayChannel Packaged -PackagingRelease 'v2026.9.9-msix.0'
Assert-True ($launch.Build -and $launch.Patch -ceq 'packaged') 'Local launch must prepare the published payload when requested.'
$launch = Resolve-GatewayLaunchInput -ExistingPatch 'source'
Assert-True (-not $launch.Build -and $launch.Patch -ceq 'source') 'Explicit source patch must not be rebuilt.'
$launch = Resolve-GatewayLaunchInput -UseStoreGateway -ExistingPatch 'source'
Assert-True (-not $launch.Build -and -not $launch.Patch) 'Store opt-out must clear inherited patch for the child.'
Assert-Throws { Resolve-GatewayLaunchInput -UseStoreGateway -GatewayChannel Packaged } 'mutually exclusive'
Assert-Throws { Resolve-GatewayLaunchInput -GatewayChannel Packaged -ExistingPatch 'source' } 'cannot override'
Assert-Throws { Resolve-GatewayLaunchInput -PackagingRelease 'v2026.9.9-msix.0' } 'requires -GatewayChannel Packaged'
Assert-Throws { Resolve-GatewayLaunchInput -ExistingPatch '../bad' } 'patch identity'

$root = Join-Path ([IO.Path]::GetTempPath()) ('oc-npm-test-' + [guid]::NewGuid().ToString('N'))
try {
    $fixture = Join-Path $root 'fixture'
    $output = Join-Path $root 'output'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'package\dist'), $output -Force | Out-Null
    '{"name":"openclaw","version":"2026.8.35"}' | Set-Content (Join-Path $fixture 'package\package.json')
    @{ version = '2026.8.35'; commit = 'a' * 40 } | ConvertTo-Json |
        Set-Content (Join-Path $fixture 'package\dist\build-info.json')
    $archive = Join-Path $root 'fixture.tgz'
    & tar -czf $archive -C $fixture package
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create fixture archive.' }
    $integrity = 'sha512-' + [Convert]::ToBase64String([Convert]::FromHexString(
        (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash))
    $metadata = @{
        version = '2026.8.35'
        dist = @{ integrity = $integrity; tarball = 'https://registry.npmjs.org/openclaw/-/openclaw-2026.8.35.tgz' }
    }
    & $module {
        param($Metadata, $Archive)
        $script:response = $Metadata
        $script:archive = $Archive
        $script:requests = @()
        function script:Invoke-RestMethod {
            param($Uri, $TimeoutSec)
            $script:requests += $Uri
            $script:response
        }
        function script:Invoke-WebRequest {
            param($Uri, $OutFile, $TimeoutSec)
            Copy-Item -LiteralPath $script:archive -Destination $OutFile
        }
    } $metadata $archive
    foreach ($selector in @('latest', 'extended-stable', '2026.8.35')) {
        $release = Resolve-GatewayNpmRelease -Selector $selector
        Assert-True ($release.selector -ceq $selector -and $release.version -ceq '2026.8.35') 'Resolution must preserve channel and exact version.'
    }
    $requests = & $module { $script:requests }
    Assert-True ($requests[0] -ceq 'https://registry.npmjs.org/openclaw/latest' -and
        $requests[1] -ceq 'https://registry.npmjs.org/openclaw/extended-stable') 'Both npm tags must be resolved independently.'
    foreach ($invalid in @('main', 'beta', 'next', '*', '../latest', '2026.13.1', '2026.9.1-beta.1')) {
        Assert-Throws { Resolve-GatewayNpmRelease -Selector $invalid } 'does not match'
    }
    Assert-Throws { Resolve-GatewayNpmRelease -Selector '2026.9.9' } 'exact version'
    $metadata.version = '2026.9.1-beta.1'
    Assert-Throws { Resolve-GatewayNpmRelease -Selector latest } 'exact stable'
    $metadata.version = '2026.8.35'
    $metadata.dist.integrity = 'sha1-untrusted'
    Assert-Throws { Resolve-GatewayNpmRelease -Selector latest } 'SHA-512'
    $metadata.dist.integrity = $integrity
    $metadata.dist.tarball = 'https://example.com/openclaw.tgz'
    Assert-Throws { Resolve-GatewayNpmRelease -Selector latest } 'unexpected npm tarball'
    $metadata.dist.tarball = 'https://registry.npmjs.org/openclaw/-/openclaw-2026.8.35.tgz'

    $release = Resolve-GatewayNpmRelease -Selector extended-stable
    Save-GatewayNpmPackage -Release $release -PackageDirectory $output
    $source = Get-Content (Join-Path $output 'source.json') -Raw | ConvertFrom-Json
    Assert-True ($source.requestedRef -ceq 'npm:extended-stable' -and $source.resolvedCommit -ceq ('a' * 40) -and
        $source.packageVersion -ceq $release.version -and $source.npmIntegrity -ceq $integrity) 'Package provenance must match verified npm content.'
    Save-GatewayNpmPackage -Release $release -PackageDirectory $output
    Assert-True (Test-Path (Join-Path $output 'source.json')) 'An unchanged archive must be reusable.'
    'corrupt' | Set-Content (Join-Path $output 'openclaw.tgz')
    Assert-Throws { Save-GatewayNpmPackage -Release $release -PackageDirectory $output } 'SHA-512 integrity'
    Remove-Item -LiteralPath (Join-Path $output 'openclaw.tgz'), (Join-Path $output 'source.json')
    $release.integrity = 'sha512-' + [Convert]::ToBase64String([byte[]]::new(64))
    Assert-Throws { Save-GatewayNpmPackage -Release $release -PackageDirectory $output } 'SHA-512 integrity'
    Assert-True (@(Get-ChildItem $output).Count -eq 0) 'Failed downloads must not leave a usable package or partial file.'
    $release.integrity = $integrity
    $release.version = '2026.9.9'
    Assert-Throws { Save-GatewayNpmPackage -Release $release -PackageDirectory $output } 'archive identity'
    Assert-True (-not (Test-Path (Join-Path $output 'source.json'))) 'Identity mismatch must not produce provenance.'
}
finally {
    Remove-Module GatewayNpmRelease
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
Write-Host "Gateway npm release regressions: $script:passed passed."
