Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
Finds the openclaw/openclaw commit that the latest stable packaged Gateway ships, and whether the Companion's
built-in theme table (BuiltinGatewayThemes.UpstreamRef) already matches it. Users run the MSIX-packaged
Gateway, so the table tracks the Gateway inside the latest stable openclaw-windows-packaging release. The commit is
read from that release's verified payload (app/dist/build-info.json), not inferred from the packaging tag: a
packaging release may bundle an earlier Gateway than its own version suggests.

Usage:
    Import-Module ./scripts/GatewayThemeUpstream.psm1
    $state = Resolve-GatewayThemeUpstream -TablePath src/OpenClaw.Shared/Themes/BuiltinGatewayThemes.g.cs -CacheDirectory $env:RUNNER_TEMP
    if (-not $state.upToDate) { ./scripts/Update-GatewayThemeTable.ps1 -UpstreamRef $state.upstreamRef }
#>

Import-Module (Join-Path $PSScriptRoot 'GatewayPackagedRelease.psm1')

function Invoke-UpstreamApi([string]$Path) {
    $json = & gh api --hostname github.com "repos/openclaw/openclaw/$Path"
    if ($LASTEXITCODE -ne 0) { throw "Cannot read openclaw/openclaw metadata: $Path." }
    ($json -join "`n") | ConvertFrom-Json
}

function Get-PackagedGatewayBuild([object]$Release, [string]$CacheDirectory) {
    # Downloads the release asset once per digest, verifies its SHA-256 and reads the embedded build identity.
    $archive = Save-PackagedGatewayArchive -Release $Release -Directory $CacheDirectory
    Get-PackagedGatewayMetadata -Release $Release -ArchivePath $archive
}

function Get-ThemeTableUpstreamRef([string]$TablePath) {
    $match = [regex]::Match((Get-Content -Raw -LiteralPath $TablePath), 'UpstreamRef = "(?<sha>[0-9a-f]{40})"')
    if (-not $match.Success) { throw "No UpstreamRef found in $TablePath." }
    $match.Groups['sha'].Value
}

function Resolve-GatewayThemeUpstream {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TablePath,
        [Parameter(Mandatory)][string]$CacheDirectory
    )

    # Stable releases only: drafts, prereleases, unexpected tags and unverifiable assets fail here.
    $release = Resolve-PackagedGatewayRelease -Architecture x64
    $gateway = Get-PackagedGatewayBuild $release $CacheDirectory
    # The theme generator reads upstream sources at this commit, so it must be a public openclaw/openclaw commit.
    if ((Invoke-UpstreamApi "commits/$($gateway.gatewayCommit)").sha -cne $gateway.gatewayCommit) {
        throw "Packaged Gateway commit $($gateway.gatewayCommit) is not an openclaw/openclaw commit."
    }
    $current = Get-ThemeTableUpstreamRef $TablePath
    [pscustomobject][ordered]@{
        packagingRelease = $release.packagingRelease
        gatewayVersion = $gateway.version
        upstreamRef = $gateway.gatewayCommit
        currentRef = $current
        upToDate = $gateway.gatewayCommit -ceq $current
    }
}

Export-ModuleMember -Function Resolve-GatewayThemeUpstream