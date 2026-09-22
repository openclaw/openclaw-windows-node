<#
.SYNOPSIS
    Builds one unsigned multi-architecture Store MSIX bundle.
.DESCRIPTION
    Combines the validated x64 and ARM64 Store packages without changing their
    bytes. The bundle receives the same four-part package version so Partner
    Center can ingest one architecture-selecting submission asset.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$X64Package,
    [Parameter(Mandatory)][string]$Arm64Package,
    [Parameter(Mandatory)][string]$PackageVersion,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$MakeAppxPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-MakeAppx {
    if (-not [string]::IsNullOrWhiteSpace($MakeAppxPath)) {
        return (Resolve-Path -LiteralPath $MakeAppxPath).Path
    }

    $command = Get-Command MakeAppx.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $windowsKits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -LiteralPath $windowsKits -Filter MakeAppx.exe -File -Recurse `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw 'MakeAppx.exe was not found in PATH or the Windows 10 SDK.'
    }

    $candidate.FullName
}

foreach ($package in @($X64Package, $Arm64Package)) {
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
        throw "Required MSIX package was not found: $package"
    }
    if ([IO.Path]::GetExtension($package) -ine '.msix') {
        throw "Bundle input must be an MSIX package: $package"
    }
}

$segments = @($PackageVersion.Split('.'))
if ($segments.Count -ne 4) {
    throw 'PackageVersion must contain four numeric components.'
}
foreach ($segment in $segments) {
    [uint16]$value = 0
    if (-not [uint16]::TryParse($segment, [ref]$value)) {
        throw "Invalid MSIX bundle version component: $segment"
    }
}
if ($segments[3] -ne '0') {
    throw "Store MSIX bundle versions must end in .0: $PackageVersion"
}

$resolvedX64Package = (Resolve-Path -LiteralPath $X64Package).Path
$resolvedArm64Package = (Resolve-Path -LiteralPath $Arm64Package).Path
if ($resolvedX64Package -eq $resolvedArm64Package) {
    throw 'The x64 and ARM64 bundle inputs must be different packages.'
}

$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($resolvedOutputPath) -ine '.msixbundle') {
    throw 'OutputPath must use the .msixbundle extension.'
}
if (Test-Path -LiteralPath $resolvedOutputPath) {
    throw "MSIX bundle output already exists: $resolvedOutputPath"
}

$outputDirectory = Split-Path $resolvedOutputPath -Parent
New-Item -Path $outputDirectory -ItemType Directory -Force | Out-Null
$workRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-msixbundle-$([guid]::NewGuid().ToString('N'))"
$bundleInput = Join-Path $workRoot 'packages'
New-Item -Path $bundleInput -ItemType Directory -Force | Out-Null

try {
    Copy-Item -LiteralPath $resolvedX64Package -Destination (Join-Path $bundleInput 'OpenClaw-x64.msix')
    Copy-Item -LiteralPath $resolvedArm64Package -Destination (Join-Path $bundleInput 'OpenClaw-arm64.msix')

    $resolvedMakeAppx = Resolve-MakeAppx
    & $resolvedMakeAppx bundle /v /bv $PackageVersion /d $bundleInput /p $resolvedOutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "MakeAppx.exe failed to build the MSIX bundle. Exit code: $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
        throw 'MakeAppx.exe completed without producing the requested bundle.'
    }

    Write-Host "Unsigned Store MSIX bundle is ready: $resolvedOutputPath"
}
finally {
    if ([IO.Directory]::Exists($workRoot)) {
        [IO.Directory]::Delete($workRoot, $true)
    }
}
