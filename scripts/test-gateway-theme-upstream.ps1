#Requires -Version 7.4
# Tests the theme-table sync's release resolution (scripts/GatewayThemeUpstream.psm1) with faked GitHub replies and
# payload metadata: the latest stable packaging release maps to the Gateway commit in its payload (not its tag), and
# every unresolvable step fails clearly. Payload download and verification are covered by
# test-gateway-packaged-release.ps1. Run with ./scripts/test-gateway-theme-upstream.ps1; no network or credentials.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'GatewayPackagedRelease.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'GatewayThemeUpstream.psm1') -Force
$packaging = Get-Module GatewayPackagedRelease
$upstream = Get-Module GatewayThemeUpstream
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

# A packaging release may ship an earlier Gateway than its own version, as v2026.9.9-msix.0 does here.
$release = @{
    tag_name = 'v2026.9.9-msix.0'; draft = $false; prerelease = $false; target_commitish = 'main'
    assets = @(@{
        name = 'OpenClawGateway-2026.9.9-msix.0-x64.msix'
        browser_download_url = 'https://github.com/openclaw/openclaw-windows-packaging/releases/download/v2026.9.9-msix.0/OpenClawGateway-2026.9.9-msix.0-x64.msix'
        digest = 'sha256:' + ('e' * 64)
    })
}
& $packaging {
    param($Release)
    $script:release = $Release
    function script:Invoke-PackagingApi([string]$Path) {
        if ($Path.StartsWith('releases/')) { return $script:release }
        return @{ object = @{ type = 'commit'; sha = 'b' * 40 } }
    }
} $release

$build = @{ version = '2026.8.20'; gatewayCommit = 'd' * 40 }
& $upstream {
    param($Build)
    $script:build = $Build
    $script:payloadRequests = [System.Collections.Generic.List[string]]::new()
    function script:Get-PackagedGatewayBuild([object]$Release, [string]$CacheDirectory) {
        $script:payloadRequests.Add("$($Release.packagingRelease)|$($Release.assetSha256)|$CacheDirectory")
        if (-not $script:build) { throw 'Packaged Gateway package and build identities do not agree.' }
        [pscustomobject]$script:build
    }
    function script:Invoke-UpstreamApi([string]$Path) {
        if ($Path -cmatch '\Acommits/(?<sha>d{40})\z') { return @{ sha = $Matches['sha'] } }
        throw "Cannot read openclaw/openclaw metadata: $Path."
    }
} $build

$cache = [IO.Path]::GetTempPath()
$table = Join-Path $cache ("theme-table-" + [guid]::NewGuid().ToString('N') + '.g.cs')
try {
    Set-Content -LiteralPath $table -Value "    public const string UpstreamRef = `"$('c' * 40)`";"

    $state = Resolve-GatewayThemeUpstream -TablePath $table -CacheDirectory $cache
    Assert-True ($state.packagingRelease -ceq 'v2026.9.9-msix.0' -and $state.gatewayVersion -ceq '2026.8.20') 'The latest stable release reports the Gateway it ships.'
    Assert-True ($state.upstreamRef -ceq ('d' * 40) -and -not $state.upToDate) 'The payload commit, not the packaging tag, is compared with the table.'
    Assert-True ((& $upstream { $script:payloadRequests[0] }) -ceq "v2026.9.9-msix.0|$('e' * 64)|$cache") 'The verified release asset is read from the cache directory.'

    Set-Content -LiteralPath $table -Value "    public const string UpstreamRef = `"$('d' * 40)`";"
    Assert-True (Resolve-GatewayThemeUpstream -TablePath $table -CacheDirectory $cache).upToDate 'A matching table is reported up to date.'

    $build.gatewayCommit = 'f' * 40
    Assert-Throws { Resolve-GatewayThemeUpstream -TablePath $table -CacheDirectory $cache } "commits/$('f' * 40)"
    $build.gatewayCommit = 'd' * 40

    $release.prerelease = $true
    Assert-Throws { Resolve-GatewayThemeUpstream -TablePath $table -CacheDirectory $cache } 'non-prerelease'
    $release.prerelease = $false

    & $upstream { $script:build = $null }
    Assert-Throws { Resolve-GatewayThemeUpstream -TablePath $table -CacheDirectory $cache } 'build identities do not agree'
    & $upstream { param($Build) $script:build = $Build } $build

    Set-Content -LiteralPath $table -Value '// no reference'
    Assert-Throws { Resolve-GatewayThemeUpstream -TablePath $table -CacheDirectory $cache } 'No UpstreamRef'
}
finally {
    Remove-Item -LiteralPath $table -ErrorAction SilentlyContinue
}

Write-Host "Gateway theme upstream tests passed: $script:passed assertions."