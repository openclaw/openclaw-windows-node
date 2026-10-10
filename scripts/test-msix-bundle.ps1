<#
.SYNOPSIS
    Exercises multi-architecture Store MSIX bundle construction contracts.
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$builder = Join-Path $RepoRoot 'scripts\Build-StoreMsixBundle.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-msixbundle-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -Path $temporaryRoot -ItemType Directory -Force | Out-Null

function Assert-Fails {
    param([scriptblock]$Action, [string]$Expected)
    try {
        & $Action
        throw 'The operation unexpectedly succeeded.'
    }
    catch {
        if (-not $_.Exception.Message.Contains($Expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Expected '$Expected', received: $($_.Exception.Message)"
        }
    }
}

try {
    $x64Package = Join-Path $temporaryRoot 'x64.msix'
    $arm64Package = Join-Path $temporaryRoot 'arm64.msix'
    Set-Content -LiteralPath $x64Package -Value 'x64 package'
    Set-Content -LiteralPath $arm64Package -Value 'arm64 package'

    $argumentsPath = Join-Path $temporaryRoot 'makeappx-arguments.txt'
    $env:OPENCLAW_BUNDLE_TEST_ARGUMENTS = $argumentsPath
    $fakeMakeAppx = Join-Path $temporaryRoot 'MakeAppx.ps1'
    @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
$Arguments -join ' ' | Set-Content -LiteralPath $env:OPENCLAW_BUNDLE_TEST_ARGUMENTS
$outputIndex = [Array]::IndexOf($Arguments, '/p')
if ($outputIndex -lt 0 -or $outputIndex + 1 -ge $Arguments.Count) { exit 2 }
New-Item -ItemType File -Path $Arguments[$outputIndex + 1] -Force | Out-Null
exit 0
'@ | Set-Content -LiteralPath $fakeMakeAppx

    $bundle = Join-Path $temporaryRoot 'OpenClaw.msixbundle'
    & $builder -X64Package $x64Package -Arm64Package $arm64Package `
        -PackageVersion '2026.9.401.0' -OutputPath $bundle -MakeAppxPath $fakeMakeAppx
    if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
        throw 'The bundle builder did not preserve the MakeAppx output.'
    }
    $arguments = Get-Content -LiteralPath $argumentsPath -Raw
    foreach ($token in @('bundle', '/bv 2026.9.401.0', 'OpenClaw.msixbundle')) {
        if (-not $arguments.Contains($token, [StringComparison]::OrdinalIgnoreCase)) {
            throw "MakeAppx arguments are missing '$token': $arguments"
        }
    }

    $maximumBundle = Join-Path $temporaryRoot 'OpenClaw-maximum.msixbundle'
    & $builder -X64Package $x64Package -Arm64Package $arm64Package `
        -PackageVersion '2026.9.65535.0' -OutputPath $maximumBundle -MakeAppxPath $fakeMakeAppx
    $arguments = Get-Content -LiteralPath $argumentsPath -Raw
    if (-not $arguments.Contains('/bv 2026.9.65535.0', [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $maximumBundle -PathType Leaf)) {
        throw 'The bundle builder did not accept the allocator maximum 2026.9.65535.0.'
    }

    Assert-Fails { & $builder -X64Package $x64Package -Arm64Package $x64Package `
        -PackageVersion '2026.9.401.0' -OutputPath (Join-Path $temporaryRoot 'duplicate.msixbundle') `
        -MakeAppxPath $fakeMakeAppx } 'must be different'
    Assert-Fails { & $builder -X64Package $x64Package -Arm64Package $arm64Package `
        -PackageVersion '2026.9.401' -OutputPath (Join-Path $temporaryRoot 'short.msixbundle') `
        -MakeAppxPath $fakeMakeAppx } 'four numeric components'
    Assert-Fails { & $builder -X64Package $x64Package -Arm64Package $arm64Package `
        -PackageVersion '2026.9.401.1' -OutputPath (Join-Path $temporaryRoot 'revision.msixbundle') `
        -MakeAppxPath $fakeMakeAppx } 'must end in .0'
    Assert-Fails { & $builder -X64Package $x64Package -Arm64Package $arm64Package `
        -PackageVersion '2026.9.65536.0' -OutputPath (Join-Path $temporaryRoot 'overflow.msixbundle') `
        -MakeAppxPath $fakeMakeAppx } 'invalid msix bundle version component'
    Assert-Fails { & $builder -X64Package $x64Package -Arm64Package $arm64Package `
        -PackageVersion '2026.9.401.0' -OutputPath $bundle -MakeAppxPath $fakeMakeAppx } 'already exists'

    Write-Host 'Store MSIX bundle tests passed.'
}
finally {
    Remove-Item Env:OPENCLAW_BUNDLE_TEST_ARGUMENTS -ErrorAction SilentlyContinue
    if ([IO.Directory]::Exists($temporaryRoot)) {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}
