<#
.SYNOPSIS
    Deterministic offline regressions for daily alpha release-line selection.
#>

[CmdletBinding()]
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$selector = Join-Path $RepoRoot "scripts\Resolve-DailyAlphaVersion.ps1"
if (-not (Test-Path -LiteralPath $selector)) {
    throw "Daily alpha version selector not found at '$selector'."
}

$accepted = 0
$rejected = 0

function Assert-SelectedVersion {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$GitVersionSemVer,
        [Parameter(Mandatory)][string]$GatewayTag,
        [Parameter(Mandatory)][string]$Expected
    )

    $actual = & $selector `
        -GitVersionSemVer $GitVersionSemVer `
        -GatewayTag $GatewayTag
    if ($actual -cne $Expected) {
        throw "Case '$Name': expected '$Expected' but received '$actual'."
    }

    $script:accepted++
    Write-Host "  accepted: $Name ($GitVersionSemVer + $GatewayTag -> $actual)"
}

function Assert-RejectedVersion {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$GitVersionSemVer,
        [Parameter(Mandatory)][string]$GatewayTag,
        [Parameter(Mandatory)][string]$ExpectedMessage
    )

    $failure = $null
    try {
        & $selector `
            -GitVersionSemVer $GitVersionSemVer `
            -GatewayTag $GatewayTag |
            Out-Null
    } catch {
        $failure = $_.Exception.Message
    }

    if ($null -eq $failure) {
        throw "Case '$Name': invalid input was accepted."
    }
    if (-not $failure.Contains($ExpectedMessage, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Case '$Name': expected '$ExpectedMessage' but received '$failure'."
    }

    $script:rejected++
    Write-Host "  rejected: $Name"
}

Write-Host "Validating daily alpha release-line selection..."
Assert-SelectedVersion `
    -Name "newer Gateway starts a matching Windows train" `
    -GitVersionSemVer "2026.9.5-alpha.79" `
    -GatewayTag "v2026.9.7" `
    -Expected "2026.9.7-alpha.1"
Assert-SelectedVersion `
    -Name "Gateway correction uses its base release line" `
    -GitVersionSemVer "2026.9.5-alpha.79" `
    -GatewayTag "v2026.9.7-2" `
    -Expected "2026.9.7-alpha.1"
Assert-SelectedVersion `
    -Name "matching release line preserves GitVersion sequence" `
    -GitVersionSemVer "2026.9.7-alpha.12" `
    -GatewayTag "v2026.9.7" `
    -Expected "2026.9.7-alpha.12"
Assert-SelectedVersion `
    -Name "older Gateway never downgrades Windows" `
    -GitVersionSemVer "2026.9.8-alpha.3" `
    -GatewayTag "v2026.9.7" `
    -Expected "2026.9.8-alpha.3"
Assert-SelectedVersion `
    -Name "numeric comparison handles multi-digit patches" `
    -GitVersionSemVer "2026.8.34-alpha.4" `
    -GatewayTag "v2026.9.7" `
    -Expected "2026.9.7-alpha.1"

Assert-RejectedVersion `
    -Name "stable GitVersion output" `
    -GitVersionSemVer "2026.9.5" `
    -GatewayTag "v2026.9.7" `
    -ExpectedMessage "GitVersion alpha"
Assert-RejectedVersion `
    -Name "Gateway prerelease" `
    -GitVersionSemVer "2026.9.5-alpha.79" `
    -GatewayTag "v2026.9.7-beta.1" `
    -ExpectedMessage "Gateway release tag"
Assert-RejectedVersion `
    -Name "missing Gateway tag prefix" `
    -GitVersionSemVer "2026.9.5-alpha.79" `
    -GatewayTag "2026.9.7" `
    -ExpectedMessage "Gateway release tag"

Write-Host "Daily alpha version regressions passed: $accepted accepted, $rejected rejected."
