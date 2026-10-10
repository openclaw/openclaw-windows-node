[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'TestResults\E2E')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'GatewayNpmRelease.psm1') -Force

if (-not $env:GITHUB_OUTPUT -or -not $env:GITHUB_STEP_SUMMARY) {
    throw 'Resolve-CiGateway requires GitHub Actions output and summary files.'
}
if ($env:OPENCLAW_E2E_GATEWAY_PACKAGE_TGZ) {
    throw 'WSL Latest CI cannot use a candidate Gateway tarball.'
}
# Resolve the moving selector once so all WSL E2E jobs use the same version.
$release = Resolve-GatewayNpmRelease -Selector latest
$release | Add-Member -NotePropertyName channel -NotePropertyValue Latest
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$release | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'gateway-release.json') -Encoding utf8
"version=$($release.version)" >> $env:GITHUB_OUTPUT
"release=$($release | ConvertTo-Json -Compress)" >> $env:GITHUB_OUTPUT
"WSL Gateway: npm latest resolved to ``$($release.version)``." >> $env:GITHUB_STEP_SUMMARY
Write-Host "WSL Gateway: Latest=$($release.version)"
