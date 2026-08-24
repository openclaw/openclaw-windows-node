[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactsDirectory,

    [Parameter(Mandatory)]
    [string]$PolicyPath,

    [Parameter(Mandatory)]
    [string]$RequestedRef,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$PackagingCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-ZipEntryText {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $entries = @($Archive.Entries | Where-Object {
        $_.FullName -eq $Path
    })
    if ($entries.Count -ne 1) {
        throw "Expected one '$Path' entry; found $($entries.Count)."
    }

    $stream = $entries[0].Open()
    $reader = [IO.StreamReader]::new($stream)
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Get-ZipEntrySha256 {
    param(
        [Parameter(Mandatory)]
        [IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $entries = @($Archive.Entries | Where-Object {
        $_.FullName -eq $Path
    })
    if ($entries.Count -ne 1) {
        throw "Expected one '$Path' entry; found $($entries.Count)."
    }

    $stream = $entries[0].Open()
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString(
            $sha256.ComputeHash($stream)
        ).ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

$resolvedArtifactsDirectory = (
    Resolve-Path -LiteralPath $ArtifactsDirectory
).Path
$resolvedPolicyPath = (Resolve-Path -LiteralPath $PolicyPath).Path
$policy = Get-Content -LiteralPath $resolvedPolicyPath -Raw |
    ConvertFrom-Json

if (
    $policy.repository -ne 'https://github.com/openclaw/openclaw' -or
    $policy.approvedCommit -notmatch '^[0-9a-fA-F]{40}$' -or
    [string]::IsNullOrWhiteSpace([string]$policy.publisher)
) {
    throw 'The Gateway MSIX release policy is invalid.'
}

$approvedCommit = ([string]$policy.approvedCommit).ToLowerInvariant()
$normalizedRequestedRef = $RequestedRef.Trim().ToLowerInvariant()
if (
    $normalizedRequestedRef -notmatch '^[0-9a-f]{40}$' -or
    $normalizedRequestedRef -ne $approvedCommit
) {
    throw (
        'Official signing requires the approved immutable OpenClaw commit: ' +
        $approvedCommit
    )
}

$expectedPackagingCommit = $PackagingCommit.ToLowerInvariant()
$expectedPackageVersion = $null
foreach ($architecture in @('x64', 'arm64')) {
    $directory = Join-Path $resolvedArtifactsDirectory $architecture
    $metadataPath = Join-Path $directory 'msix-metadata.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "Missing $architecture MSIX metadata: $metadataPath"
    }

    $metadata = Get-Content -LiteralPath $metadataPath -Raw |
        ConvertFrom-Json
    $msixFiles = @(Get-ChildItem -LiteralPath $directory -Filter '*.msix' -File)
    if ($msixFiles.Count -ne 1) {
        throw (
            "Expected one unsigned $architecture MSIX in '$directory'; " +
            "found $($msixFiles.Count)."
        )
    }

    $msix = $msixFiles[0]
    if (
        $metadata.packagingRepository -ne
            'https://github.com/openclaw/openclaw-windows-node' -or
        $metadata.packagingCommit -ine $expectedPackagingCommit -or
        $metadata.sourceTreeDirty -ne $false -or
        $metadata.payloadRepository -ne $policy.repository -or
        $metadata.payloadRequestedRef -ine $approvedCommit -or
        $metadata.payloadResolvedCommit -ine $approvedCommit -or
        $metadata.architecture -ne $architecture -or
        $metadata.archive -ne $msix.Name -or
        $metadata.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $metadata.signed -ne $false -or
        $metadata.publisher -ne $policy.publisher
    ) {
        throw "The $architecture MSIX metadata is not eligible for signing."
    }

    if ($null -eq $expectedPackageVersion) {
        $expectedPackageVersion = [string]$metadata.packageVersion
    }
    elseif ($metadata.packageVersion -ne $expectedPackageVersion) {
        throw 'The x64 and ARM64 package versions do not match.'
    }

    $actualMsixHash = (
        Get-FileHash -LiteralPath $msix.FullName -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($actualMsixHash -ne ([string]$metadata.sha256).ToLowerInvariant()) {
        throw "The $architecture MSIX hash does not match its metadata."
    }

    $packageArchive = [IO.Compression.ZipFile]::OpenRead($msix.FullName)
    try {
        [xml]$manifest = Read-ZipEntryText `
            -Archive $packageArchive `
            -Path 'AppxManifest.xml'
        $identity = $manifest.SelectSingleNode(
            "/*[local-name()='Package']/*[local-name()='Identity']"
        )
        if (
            $null -eq $identity -or
            $identity.Publisher -ne $policy.publisher -or
            $identity.ProcessorArchitecture -ne $architecture -or
            $identity.Version -ne $metadata.packageVersion
        ) {
            throw "The $architecture MSIX manifest identity is unexpected."
        }

        $payloadMetadataPath = 'payload/payload-metadata.json'
        $payloadMetadata = Read-ZipEntryText `
            -Archive $packageArchive `
            -Path $payloadMetadataPath |
            ConvertFrom-Json
        if (
            $payloadMetadata.repository -ne $policy.repository -or
            $payloadMetadata.requestedRef -ine $approvedCommit -or
            $payloadMetadata.resolvedCommit -ine $approvedCommit -or
            $payloadMetadata.architecture -ne $architecture -or
            $payloadMetadata.archive -notmatch '^app-(x64|arm64)\.tar\.gz$' -or
            $payloadMetadata.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $payloadMetadata.resolvedCommit -ine
                $metadata.payloadResolvedCommit
        ) {
            throw "The embedded $architecture payload metadata is not approved."
        }

        $payloadArchivePath = "payload/$($payloadMetadata.archive)"
        $actualPayloadHash = Get-ZipEntrySha256 `
            -Archive $packageArchive `
            -Path $payloadArchivePath
        if (
            $actualPayloadHash -ne
                ([string]$payloadMetadata.sha256).ToLowerInvariant()
        ) {
            throw "The embedded $architecture payload hash is invalid."
        }
    }
    finally {
        $packageArchive.Dispose()
    }
}

Write-Host (
    "Authorized official signing for OpenClaw commit $approvedCommit " +
    "and Gateway MSIX version $expectedPackageVersion."
)
