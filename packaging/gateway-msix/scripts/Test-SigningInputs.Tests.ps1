[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$policyPath = Join-Path $repositoryRoot 'release-policy.json'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
$approvedCommit = [string]$policy.approvedCommit
$packagingCommit = '1111111111111111111111111111111111111111'
$testRoot = Join-Path $env:TEMP (
    "openclaw-signing-policy-$([guid]::NewGuid().ToString('N'))"
)

function New-TestArtifact {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [ValidateSet('x64', 'arm64')]
        [string]$Architecture,

        [string]$PayloadCommit = $approvedCommit,

        [bool]$SourceTreeDirty = $false
    )

    $directory = Join-Path $Root $Architecture
    $staging = Join-Path $Root ".$Architecture-package"
    $payloadDirectory = Join-Path $staging 'payload'
    New-Item -Path $directory, $payloadDirectory -ItemType Directory -Force |
        Out-Null

    $payloadArchiveName = "app-$Architecture.tar.gz"
    $payloadArchivePath = Join-Path $payloadDirectory $payloadArchiveName
    [IO.File]::WriteAllBytes(
        $payloadArchivePath,
        [Text.Encoding]::UTF8.GetBytes("payload-$Architecture")
    )
    $payloadHash = (
        Get-FileHash -LiteralPath $payloadArchivePath -Algorithm SHA256
    ).Hash.ToLowerInvariant()

    [ordered]@{
        repository = $policy.repository
        requestedRef = $PayloadCommit
        resolvedCommit = $PayloadCommit
        packageVersion = '2026.7.1-2'
        architecture = $Architecture
        archive = $payloadArchiveName
        sha256 = $payloadHash
        nodeVersion = 'v24.16.0'
        npmVersion = '11.5.1'
    } |
        ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $payloadDirectory 'payload-metadata.json') `
            -Encoding utf8

    @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="OpenClaw.Gateway"
            Publisher="$($policy.publisher)"
            Version="0.1.1.0"
            ProcessorArchitecture="$Architecture" />
</Package>
"@ | Set-Content `
        -LiteralPath (Join-Path $staging 'AppxManifest.xml') `
        -Encoding utf8

    $msixName = "OpenClawGateway-$Architecture.msix"
    $msixPath = Join-Path $directory $msixName
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $msixPath)
    Remove-Item -LiteralPath $staging -Recurse -Force

    $msixHash = (
        Get-FileHash -LiteralPath $msixPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    [ordered]@{
        packagingRepository =
            'https://github.com/openclaw/openclaw-windows-node'
        packagingCommit = $packagingCommit
        sourceTreeDirty = $SourceTreeDirty
        payloadRepository = $policy.repository
        payloadRequestedRef = $approvedCommit
        payloadResolvedCommit = $approvedCommit
        architecture = $Architecture
        archive = $msixName
        sha256 = $msixHash
        signed = $false
        packageVersion = '0.1.1.0'
        publisher = $policy.publisher
        nodeVersion = '24.16.0'
        nodeArchive = "node-v24.16.0-win-$Architecture.zip"
        nodeArchiveSha256 = ('2' * 64)
    } |
        ConvertTo-Json |
        Set-Content `
            -LiteralPath (Join-Path $directory 'msix-metadata.json') `
            -Encoding utf8
}

function Invoke-PolicyValidation {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [string]$RequestedRef = $approvedCommit
    )

    & (Join-Path $PSScriptRoot 'Test-SigningInputs.ps1') `
        -ArtifactsDirectory $Root `
        -PolicyPath $policyPath `
        -RequestedRef $RequestedRef `
        -PackagingCommit $packagingCommit
}

function Assert-Fails {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw (
                "Expected failure matching '$MessagePattern'; received: " +
                $_.Exception.Message
            )
        }
        return
    }

    throw "Expected failure matching '$MessagePattern', but the action succeeded."
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -Path $testRoot -ItemType Directory | Out-Null

    New-TestArtifact -Root $testRoot -Architecture x64
    New-TestArtifact -Root $testRoot -Architecture arm64
    Invoke-PolicyValidation -Root $testRoot

    Assert-Fails `
        -MessagePattern 'approved immutable OpenClaw commit' `
        -Action {
            Invoke-PolicyValidation `
                -Root $testRoot `
                -RequestedRef 'v2026.7.1-2'
        }

    $x64MetadataPath = Join-Path $testRoot 'x64\msix-metadata.json'
    $x64Metadata = Get-Content -LiteralPath $x64MetadataPath -Raw |
        ConvertFrom-Json
    $x64Metadata.sourceTreeDirty = $true
    $x64Metadata |
        ConvertTo-Json |
        Set-Content -LiteralPath $x64MetadataPath -Encoding utf8
    Assert-Fails `
        -MessagePattern 'metadata is not eligible' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Remove-Item -LiteralPath $testRoot -Recurse -Force
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    New-TestArtifact -Root $testRoot -Architecture x64
    New-TestArtifact `
        -Root $testRoot `
        -Architecture arm64 `
        -PayloadCommit ('3' * 40)
    Assert-Fails `
        -MessagePattern 'embedded arm64 payload metadata is not approved' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Remove-Item -LiteralPath $testRoot -Recurse -Force
    New-Item -Path $testRoot -ItemType Directory | Out-Null
    New-TestArtifact -Root $testRoot -Architecture x64
    New-TestArtifact -Root $testRoot -Architecture arm64
    Add-Content `
        -LiteralPath (Join-Path $testRoot 'x64\OpenClawGateway-x64.msix') `
        -Value 'tampered'
    Assert-Fails `
        -MessagePattern 'MSIX hash does not match' `
        -Action {
            Invoke-PolicyValidation -Root $testRoot
        }

    Write-Host 'Gateway MSIX signing policy tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
