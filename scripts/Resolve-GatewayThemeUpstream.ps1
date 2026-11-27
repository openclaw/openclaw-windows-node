<#
.SYNOPSIS
Reports whether the built-in Gateway theme table matches the Gateway in the latest stable packaged Gateway release.

.DESCRIPTION
Resolves the latest stable openclaw/openclaw-windows-packaging release (drafts and prereleases are refused), downloads
its x64 MSIX into -CacheDirectory (reused per digest), verifies it and reads the openclaw commit and version it ships
from app/dist/build-info.json, then compares that commit with BuiltinGatewayThemes.UpstreamRef. It changes nothing in
the repository; with -GitHubOutput it appends packaging_release, gateway_version, upstream_ref and up_to_date for a
workflow. It fails with a clear message when the release, payload or commit cannot be resolved. Needs gh
authenticated for github.com (GH_TOKEN in GitHub Actions).

.EXAMPLE
./scripts/Resolve-GatewayThemeUpstream.ps1
./scripts/Resolve-GatewayThemeUpstream.ps1 -CacheDirectory $env:RUNNER_TEMP -GitHubOutput $env:GITHUB_OUTPUT
#>
#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$TablePath = (Join-Path $PSScriptRoot '..\src\OpenClaw.Shared\Themes\BuiltinGatewayThemes.g.cs'),
    [string]$CacheDirectory = [IO.Path]::GetTempPath(),
    [string]$GitHubOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'GatewayThemeUpstream.psm1') -Force

$state = Resolve-GatewayThemeUpstream -TablePath $TablePath -CacheDirectory $CacheDirectory
if ($GitHubOutput) {
    @(
        "packaging_release=$($state.packagingRelease)"
        "gateway_version=$($state.gatewayVersion)"
        "upstream_ref=$($state.upstreamRef)"
        "up_to_date=$($state.upToDate.ToString().ToLowerInvariant())"
    ) | Add-Content -LiteralPath $GitHubOutput -Encoding utf8
}
$state