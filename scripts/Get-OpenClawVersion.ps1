<#
.SYNOPSIS
    Prints the GitVersion-derived OpenClaw version.

.DESCRIPTION
    Uses the repository-local GitVersion.Tool manifest so local scripts and CI
    derive versions from the same GitVersion.yml/tag history as release builds.
#>

[CmdletBinding()]
param(
    [ValidateSet("SemVer", "MajorMinorPatch")]
    [string]$Variable = "SemVer",

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    if (-not $NoRestore) {
        dotnet tool restore | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet tool restore failed."
        }
    }

    $gitVersionOutput = & dotnet tool run dotnet-gitversion -- /output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "GitVersion failed: $gitVersionOutput"
    }

    $gitVersion = $gitVersionOutput | ConvertFrom-Json
    $value = $gitVersion.$Variable
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "GitVersion did not return '$Variable'."
    }

    Write-Output $value
}
finally {
    Pop-Location
}
