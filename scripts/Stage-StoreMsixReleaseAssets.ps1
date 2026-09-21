<#
.SYNOPSIS
    Stages validated unsigned Store MSIX packages for a GitHub release.
.DESCRIPTION
    Checks both architectures' provenance and hashes, then verifies that the
    multi-architecture bundle embeds those exact packages before copying files.
    Build-StoreMsix.ps1 owns package-content validation; this script preserves
    those exact bytes and never signs packages or submits them to Partner Center.
    Returns Files and Notes for the existing release publisher.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$VersionInfoPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$ArtifactDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $ArtifactDirectory))
$OutputDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $OutputDirectory))
if ((Test-Path -LiteralPath $OutputDirectory) -and
    (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or
     @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0)) {
    throw "The release output directory must be absent or empty: $OutputDirectory"
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\OpenClaw.Tray.WinUI\Package.appxmanifest') -Raw
. (Join-Path $PSScriptRoot 'MsixVersioning.ps1')
$versionInfo = Read-MsixVersionInfo `
    -Path ([IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $VersionInfoPath))) `
    -SourceCommit $ExpectedSourceCommit -SourceVersion $Version -RequireReserved
$expectedVersion = $versionInfo.storePackageVersion
$packages = foreach ($architecture in @('x64', 'arm64')) {
    $directory = Join-Path $ArtifactDirectory "openclaw-msix-store-unsigned-$architecture"
    $packageName = "OpenClaw-$architecture.msix"
    $expectedFiles = @($packageName, 'msix-metadata.json') | Sort-Object
    $entries = @(Get-ChildItem -LiteralPath $directory -Force)
    if ($entries.Count -ne 2 -or
        @($entries | Where-Object { $_.PSIsContainer -or $_.LinkType }).Count -gt 0 -or
        @(Compare-Object $expectedFiles @($entries.Name | Sort-Object)).Count -gt 0) {
        throw "Expected exactly the unsigned Store package and metadata for $architecture."
    }

    $metadataPath = Join-Path $directory 'msix-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if (-not $metadata.PSObject.Properties['msixVersionAllocation']) {
        throw "The $architecture Store artifact is missing its release allocation."
    }
    $packageAllocation = Assert-MsixVersionInfo -VersionInfo $metadata.msixVersionAllocation `
        -SourceCommit $ExpectedSourceCommit -SourceVersion $Version -RequireReserved
    foreach ($property in $versionInfo.PSObject.Properties) {
        if ($packageAllocation.($property.Name) -cne $property.Value) {
            throw "The $architecture Store artifact does not match the expected release allocation."
        }
    }
    if ($metadata.sourceTreeDirty -isnot [bool] -or $metadata.sourceTreeDirty -or
        $metadata.sourceCommit -ne $ExpectedSourceCommit) {
        throw "The $architecture Store artifact does not belong to the expected clean source commit."
    }
    if ($metadata.signed -isnot [bool] -or $metadata.signed -or
        $metadata.configuration -ne 'Release' -or
        $metadata.identityName -ne [string]$manifest.Package.Identity.Name -or
        $metadata.publisher -ne [string]$manifest.Package.Identity.Publisher -or
        $metadata.architecture -ne $architecture -or
        $metadata.packageVersion -ne $expectedVersion -or
        $metadata.archive -ne $packageName) {
        throw "The $architecture Store artifact identity, version, or unsigned metadata is invalid."
    }
    $packagePath = Join-Path $directory $packageName
    if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $metadata.sha256) {
        throw "The $architecture Store package hash does not match its metadata."
    }

    [pscustomobject]@{
        Architecture = $architecture
        Path = $packagePath
        Name = $packageName
        Metadata = $metadataPath
        Sha256 = $metadata.sha256
    }
}

$bundleDirectory = Join-Path $ArtifactDirectory 'openclaw-msix-store-unsigned-bundle'
$bundleName = 'OpenClaw.msixbundle'
$bundleEntries = @(Get-ChildItem -LiteralPath $bundleDirectory -Force)
if ($bundleEntries.Count -ne 1 -or $bundleEntries[0].PSIsContainer -or
    $bundleEntries[0].LinkType -or $bundleEntries[0].Name -ne $bundleName) {
    throw 'Expected exactly the unsigned multi-architecture Store MSIX bundle.'
}
$bundlePath = $bundleEntries[0].FullName

Add-Type -AssemblyName System.IO.Compression.FileSystem
$bundle = [IO.Compression.ZipFile]::OpenRead($bundlePath)
try {
    $bundleManifestEntry = @($bundle.Entries | Where-Object {
        $_.FullName -ceq 'AppxMetadata/AppxBundleManifest.xml'
    })
    if ($bundleManifestEntry.Count -ne 1) {
        throw 'The Store MSIX bundle is missing its unique bundle manifest.'
    }
    $reader = [IO.StreamReader]::new($bundleManifestEntry[0].Open())
    try { [xml]$bundleManifest = $reader.ReadToEnd() }
    finally { $reader.Dispose() }

    $identity = $bundleManifest.Bundle.Identity
    if ([string]$identity.Name -ne [string]$manifest.Package.Identity.Name -or
        [string]$identity.Publisher -ne [string]$manifest.Package.Identity.Publisher -or
        [string]$identity.Version -ne $expectedVersion) {
        throw 'The Store MSIX bundle identity or version does not match its packages.'
    }

    $manifestPackages = @($bundleManifest.Bundle.Packages.Package)
    if ($manifestPackages.Count -ne 2) {
        throw 'The Store MSIX bundle must contain exactly x64 and ARM64 packages.'
    }
    foreach ($package in $packages) {
        $manifestPackage = @($manifestPackages | Where-Object {
            [string]$_.Architecture -eq $package.Architecture -and
            [string]$_.FileName -eq $package.Name
        })
        $packageEntry = @($bundle.Entries | Where-Object { $_.FullName -ceq $package.Name })
        if ($manifestPackage.Count -ne 1 -or $packageEntry.Count -ne 1) {
            throw "The Store MSIX bundle is missing its $($package.Architecture) package."
        }

        $stream = $packageEntry[0].Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $embeddedHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '')
        }
        finally {
            $sha.Dispose()
            $stream.Dispose()
        }
        if ($embeddedHash -ne $package.Sha256) {
            throw "The Store MSIX bundle changed the $($package.Architecture) package bytes."
        }
    }
}
finally {
    $bundle.Dispose()
}

# A bad architecture or bundle must not leave a publishable partial set.
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$files = @($bundlePath | Copy-Item -Destination (Join-Path $OutputDirectory $bundleName) -PassThru |
    Select-Object -ExpandProperty FullName)
$files += foreach ($package in $packages) {
    $packageDestination = Join-Path $OutputDirectory $package.Name
    $metadataDestination = Join-Path $OutputDirectory "$($package.Name)-metadata.json"
    Copy-Item -LiteralPath $package.Path -Destination $packageDestination
    Copy-Item -LiteralPath $package.Metadata -Destination $metadataDestination
    $packageDestination
    $metadataDestination
}

[pscustomobject]@{
    Files = @($files)
    Notes = @"
### Unsigned Store submission packages

OpenClaw.msixbundle is the recommended unsigned Partner Center submission
input. It contains the exact x64 and ARM64 packages selected by Windows.
The standalone OpenClaw-x64.msix and OpenClaw-arm64.msix files and their
metadata remain available for architecture-specific inspection or fallback.
These files are not installers. Microsoft signs accepted Store submissions;
this workflow does not submit or retrieve Store packages.

The Windows package version is $expectedVersion, reserved for $Version.
Different official tags share the app patch's packaging counter; reruns of
this tag reuse its reservation. Dev-signed tester downloads remain in Actions.
"@
}
