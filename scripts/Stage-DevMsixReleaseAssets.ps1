<#
.SYNOPSIS
    Stages validated signed Dev MSIX packages for a GitHub release.
.DESCRIPTION
    Checks both architectures' provenance, identity, package and certificate
    hashes, and signature presence before creating architecture-specific ZIPs.
    Unsigned Store packages remain workflow artifacts for Partner Center.
    Returns Files and Notes for the existing release publisher.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$ExpectedRevision,
    [Parameter(Mandatory)][ValidateRange(1, [long]::MaxValue)][long]$ExpectedWorkflowRunId,
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

. (Join-Path $PSScriptRoot 'MsixVersioning.ps1')
$versionInfo = Read-MsixVersionInfo `
    -Path ([IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $VersionInfoPath))) `
    -SourceCommit $ExpectedSourceCommit -SourceVersion $Version -RequireReserved
$expectedPackageVersion = "$($versionInfo.packageBaseVersion).$ExpectedRevision"
[xml]$project = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj') -Raw
$expectedIdentity = [string]$project.SelectSingleNode(
    '/Project/Target/GenerateOpenClawAppxManifest').IdentityName
$expectedPublisher = [string]$project.SelectSingleNode(
    '/Project/PropertyGroup/OpenClawDevMsixPublisher').InnerText

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = foreach ($architecture in @('x64', 'arm64')) {
    $directory = Join-Path $ArtifactDirectory "openclaw-msix-dev-$architecture"
    $packageName = "OpenClaw-Dev-$architecture.msix"
    $expectedFiles = @('INSTALL.txt', 'msix-metadata.json', 'OpenClaw-Dev.cer', $packageName) | Sort-Object
    $entries = @(Get-ChildItem -LiteralPath $directory -Force)
    if ($entries.Count -ne 4 -or
        @($entries | Where-Object { $_.PSIsContainer -or $_.LinkType }).Count -gt 0 -or
        @(Compare-Object $expectedFiles @($entries.Name | Sort-Object)).Count -gt 0) {
        throw "Expected exactly the signed Dev package, certificate, metadata, and instructions for $architecture."
    }

    $metadataPath = Join-Path $directory 'msix-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if (-not $metadata.PSObject.Properties['msixVersionAllocation']) {
        throw "The $architecture Dev artifact is missing its release allocation."
    }
    $packageAllocation = Assert-MsixVersionInfo -VersionInfo $metadata.msixVersionAllocation `
        -SourceCommit $ExpectedSourceCommit -SourceVersion $Version -RequireReserved
    foreach ($property in $versionInfo.PSObject.Properties) {
        if ($packageAllocation.($property.Name) -cne $property.Value) {
            throw "The $architecture Dev artifact does not match the expected release allocation."
        }
    }
    if ($metadata.sourceTreeDirty -isnot [bool] -or $metadata.sourceTreeDirty -or
        $metadata.sourceCommit -ine $ExpectedSourceCommit) {
        throw "The $architecture Dev artifact does not belong to the expected clean source commit."
    }
    if ($metadata.signed -isnot [bool] -or -not $metadata.signed -or
        $metadata.signing -ne 'development-only' -or
        $metadata.identityName -ne $expectedIdentity -or
        $metadata.publisher -ne $expectedPublisher -or
        $metadata.architecture -ne $architecture -or
        $metadata.packageVersion -ne $expectedPackageVersion -or
        $metadata.archive -ne $packageName -or
        $metadata.certificate -ne 'OpenClaw-Dev.cer' -or
        [long]$metadata.workflowRunId -ne $ExpectedWorkflowRunId) {
        throw "The $architecture Dev artifact identity, version, run, or signed metadata is invalid."
    }

    $packagePath = Join-Path $directory $packageName
    $certificatePath = Join-Path $directory 'OpenClaw-Dev.cer'
    if (-not (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.Equals(
        [string]$metadata.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The $architecture Dev package hash does not match its metadata."
    }
    if (-not (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash.Equals(
        [string]$metadata.certificateSha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The $architecture Dev certificate hash does not match its metadata."
    }

    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    try {
        if ($certificate.HasPrivateKey -or $certificate.Subject -ne $expectedPublisher -or
            -not $certificate.Thumbprint.Equals(
                [string]$metadata.certificateThumbprint, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The $architecture Dev certificate does not match its public signer metadata."
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $packagePath
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
            -not $signature.SignerCertificate.Thumbprint.Equals(
                $certificate.Thumbprint, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The $architecture Dev package signature is not valid for its published certificate."
        }
    }
    finally { $certificate.Dispose() }

    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        foreach ($requiredEntry in @('AppxManifest.xml', 'AppxSignature.p7x')) {
            if ($null -eq $archive.GetEntry($requiredEntry)) {
                throw "The $architecture Dev package is missing $requiredEntry."
            }
        }
        $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
        try { [xml]$manifest = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
    $identity = $manifest.Package.Identity
    if ([string]$identity.Name -ne $expectedIdentity -or
        [string]$identity.Publisher -ne $expectedPublisher -or
        [string]$identity.ProcessorArchitecture -ne $architecture -or
        [string]$identity.Version -ne $expectedPackageVersion) {
        throw "The $architecture Dev package manifest does not match its release metadata."
    }

    [pscustomobject]@{
        Architecture = $architecture
        Directory = $directory
        Files = $entries
        PackageVersion = [string]$identity.Version
    }
}

# Validate both architectures before creating any publishable output.
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$files = foreach ($package in $packages) {
    $zipPath = Join-Path $OutputDirectory "OpenClaw-Dev-$($package.Architecture).zip"
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $package.Files | Sort-Object Name) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $file.Name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
    $zipPath
}

[pscustomobject]@{
    Files = @($files)
    Notes = @"
### Signed Dev MSIX packages

- **Dev MSIX (x64)**: ``OpenClaw-Dev-x64.zip``
- **Dev MSIX (ARM64)**: ``OpenClaw-Dev-arm64.zip``

Each ZIP contains a signed Dev-identity MSIX, its matching public certificate,
provenance metadata, and ``INSTALL.txt``. Extract it and follow the included
instructions. These packages are development-signed for sideloading, not
Microsoft Store-signed.

Unsigned Store submission packages remain available only as workflow artifacts
in the corresponding [Actions run](https://github.com/openclaw/openclaw-windows-node/actions/runs/$ExpectedWorkflowRunId).
"@
}
