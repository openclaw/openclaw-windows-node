[CmdletBinding()]
param(
    [ValidatePattern('\Av\d{4}\.\d{1,2}\.\d+(?:-\d+)?-msix\.\d+\z')]
    [string]$PackagingRelease,
    [string]$CacheDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'openclaw-ci-gateway'),
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'TestResults\E2E')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'GatewayNpmRelease.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'GatewayPackagedRelease.psm1') -Force

if (-not $env:GITHUB_OUTPUT -or -not $env:GITHUB_STEP_SUMMARY) {
    throw 'Resolve-CiGateway requires GitHub Actions output and summary files.'
}
if ($env:OPENCLAW_E2E_GATEWAY_PACKAGE_TGZ) {
    throw 'The Gateway compatibility matrix cannot use a candidate Gateway tarball.'
}
New-Item -ItemType Directory -Path $CacheDirectory, $OutputDirectory -Force | Out-Null
$arguments = @{ Architecture = 'x64' }
if ($PackagingRelease) { $arguments.PackagingRelease = $PackagingRelease }
$release = Resolve-PackagedGatewayRelease @arguments
$archive = Save-PackagedGatewayArchive -Release $release -Directory $CacheDirectory
$packaged = Get-PackagedGatewayMetadata -Release $release -ArchivePath $archive
# Resolve moving selectors exactly once per workflow, not independently in each shard.
$latest = Resolve-GatewayNpmRelease -Selector latest
$packagedNpm = Resolve-GatewayNpmRelease -Selector $packaged.version
$packaged | Add-Member -NotePropertyName npmIntegrity -NotePropertyValue $packagedNpm.integrity
$packaged | Add-Member -NotePropertyName npmTarball -NotePropertyValue $packagedNpm.tarball
$latest | Add-Member -NotePropertyName channel -NotePropertyValue Latest
$matrix = @{ include = @($packaged, $latest) }
$matrix | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'gateway-matrix.json') -Encoding utf8
"matrix=$($matrix | ConvertTo-Json -Depth 5 -Compress)" >> $env:GITHUB_OUTPUT
"Gateway matrix: packaged ``$($packaged.packagingRelease)`` contains ``$($packaged.version)``; npm latest resolved to ``$($latest.version)``." >> $env:GITHUB_STEP_SUMMARY
Write-Host "Gateway matrix: Packaged=$($packaged.version) ($($packaged.packagingRelease)); Latest=$($latest.version)"
