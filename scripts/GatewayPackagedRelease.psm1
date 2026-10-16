Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-PackagingApi([string]$Path) {
    $json = & gh api --hostname github.com "repos/openclaw/openclaw-windows-packaging/$Path"
    if ($LASTEXITCODE -ne 0) { throw "Cannot read packaging release metadata: $Path." }
    ($json -join "`n") | ConvertFrom-Json
}

function Resolve-PackagedGatewayRelease {
    [CmdletBinding()]
    param(
        [ValidatePattern('\Av\d{4}\.\d{1,2}\.\d+(?:-\d+)?-msix\.\d+\z')]
        [string]$PackagingRelease,
        [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture
    )

    $path = if ($PackagingRelease) { "releases/tags/$PackagingRelease" } else { 'releases/latest' }
    $release = Invoke-PackagingApi $path
    $tag = [string]$release.tag_name
    if ($release.draft -ne $false -or $release.prerelease -ne $false -or
        $tag -cnotmatch '\Av\d{4}\.\d{1,2}\.\d+(?:-\d+)?-msix\.\d+\z' -or
        ($PackagingRelease -and $tag -cne $PackagingRelease)) {
        throw 'Packaged Gateway requires an exact published, non-prerelease packaging release.'
    }
    $name = "OpenClawGateway-$($tag.Substring(1))-$Architecture.msix"
    $assets = @($release.assets | Where-Object { $_.name -ceq $name })
    if ($assets.Count -ne 1) { throw "Packaging release '$tag' must contain exactly one '$name' asset." }
    $asset = $assets[0]
    $url = "https://github.com/openclaw/openclaw-windows-packaging/releases/download/$tag/$name"
    if ($asset.browser_download_url -cne $url -or
        $asset.digest -cnotmatch '\Asha256:[0-9a-f]{64}\z') {
        throw 'Packaging release asset must have its canonical download URL and SHA-256 digest.'
    }
    # target_commitish may be a moving branch. Resolve the release tag instead.
    $object = (Invoke-PackagingApi "git/ref/tags/$tag").object
    for ($depth = 0; $depth -lt 5 -and $object.type -eq 'tag'; $depth++) {
        if ($object.sha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Invalid packaging tag object.' }
        $object = (Invoke-PackagingApi "git/tags/$($object.sha)").object
    }
    if ($object.type -cne 'commit' -or $object.sha -cnotmatch '\A[0-9a-f]{40}\z') {
        throw 'Packaging release tag must resolve to an exact commit.'
    }
    [pscustomobject][ordered]@{
        packagingRelease = $tag
        packagingCommit = $object.sha
        architecture = $Architecture
        assetName = $name
        assetUrl = $url
        assetSha256 = $asset.digest.Substring(7)
    }
}

function Assert-PackagedGatewayArchive([object]$Release, [string]$ArchivePath) {
    if ($Release.assetSha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
        (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ine $Release.assetSha256) {
        throw "Packaged Gateway archive '$ArchivePath' failed SHA-256 verification."
    }
}

function Save-PackagedGatewayArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][string]$Directory
    )

    $path = Join-Path $Directory "$($Release.assetSha256).msix"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $download = Join-Path $Directory ('download-' + [guid]::NewGuid().ToString('N') + '.msix')
        try {
            Invoke-WebRequest -Uri $Release.assetUrl -OutFile $download -TimeoutSec 600
            Assert-PackagedGatewayArchive $Release $download
            Move-Item -LiteralPath $download -Destination $path
        }
        finally {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
        }
    }
    Assert-PackagedGatewayArchive $Release $path
    return $path
}

function Read-PackagedEntry([IO.Compression.ZipArchive]$Zip, [string]$Name) {
    $entries = @($Zip.Entries | Where-Object { $_.FullName -ieq $Name })
    if ($entries.Count -ne 1 -or $entries[0].Length -gt 16MB) {
        throw "Packaged Gateway must contain one bounded '$Name' entry."
    }
    $reader = [IO.StreamReader]::new($entries[0].Open())
    try { $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Read-PackagedGatewayMetadata([object]$Release, [IO.Compression.ZipArchive]$Zip) {
    [xml]$manifest = Read-PackagedEntry $Zip 'AppxManifest.xml'
    $identity = $manifest.Package.Identity
    if ($identity.Name -cne 'OpenClaw.Gateway' -or
        $identity.Publisher -cne 'CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US' -or
        $identity.ProcessorArchitecture -cne $Release.architecture) {
        throw 'Packaged Gateway identity or architecture does not match the selected release asset.'
    }
    $package = (Read-PackagedEntry $Zip 'app/package.json') | ConvertFrom-Json
    $build = (Read-PackagedEntry $Zip 'app/dist/build-info.json') | ConvertFrom-Json
    if ($package.name -cne 'openclaw' -or
        $package.version -cnotmatch '\A\d{4}\.(?:[1-9]|1[0-2])\.\d+(?:-\d+)?\z' -or
        $build.version -cne $package.version -or $build.commit -cnotmatch '\A[0-9a-f]{40}\z' -or
        [string]::IsNullOrWhiteSpace($build.buildId)) {
        throw 'Packaged Gateway package and build identities do not agree.'
    }
    $runtimePattern = "\Aruntime/node-v(?<version>\d+\.\d+\.\d+)-win-$($Release.architecture)\.zip\z"
    $runtimes = @($Zip.Entries | Where-Object { $_.FullName -cmatch $runtimePattern })
    if ($runtimes.Count -ne 1) { throw 'Packaged Gateway must contain exactly one matching Node.js runtime archive.' }
    $nodeVersion = [regex]::Match($runtimes[0].FullName, $runtimePattern).Groups['version'].Value
    [pscustomobject][ordered]@{
        channel = 'Packaged'
        version = $package.version
        gatewayCommit = $build.commit
        buildId = $build.buildId
        nodeVersion = $nodeVersion
        msixVersion = $identity.Version
        packagingRelease = $Release.packagingRelease
        packagingCommit = $Release.packagingCommit
        architecture = $Release.architecture
        assetSha256 = $Release.assetSha256
        assetUrl = $Release.assetUrl
    }
}

function Get-PackagedGatewayMetadata {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Release, [Parameter(Mandatory)][string]$ArchivePath)

    Assert-PackagedGatewayArchive $Release $ArchivePath
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try { Read-PackagedGatewayMetadata $Release $zip }
    finally { $zip.Dispose() }
}

function Assert-PackagedPayloadFiles([IO.Compression.ZipArchive]$Zip, [string]$Directory) {
    $inventory = (Read-PackagedEntry $Zip 'payload/payload-files.json') | ConvertFrom-Json
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $inventory.files) {
        Assert-PackagedPath ([string]$file.path)
        if (-not $names.Add($file.path) -or $file.sha256 -cnotmatch '\A[0-9a-f]{64}\z') {
            throw 'Invalid packaged Gateway file inventory.'
        }
        $path = Join-Path $Directory ($file.path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not [IO.File]::Exists($path) -or ([IO.FileInfo]$path).Length -ne $file.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $file.sha256) {
            throw "Packaged Gateway payload file '$($file.path)' does not match the release inventory."
        }
    }
    if (@(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force).Count -ne $names.Count) {
        throw 'Packaged Gateway payload has files outside the release inventory.'
    }
}

function Assert-PackagedPath([string]$Name) {
    if ($Name -match '[\\:<>"|?*\x00-\x1f]' -or
        @($Name.Split('/') | Where-Object {
            $_ -in @('', '.', '..') -or $_ -match '[. ]$' -or
            $_ -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)'
        }).Count -gt 0) {
        throw "Unsafe packaged Gateway path '$Name'."
    }
}

function Get-PackagedPayloadMetadata([object]$Metadata) {
    [ordered]@{
        repository = 'https://github.com/openclaw/openclaw'
        requestedRef = $Metadata.packagingRelease
        resolvedCommit = $Metadata.gatewayCommit
        packageVersion = $Metadata.version
        architecture = $Metadata.architecture
        layout = 'expanded-directory'
        nodeVersion = $Metadata.nodeVersion
    }
}

function Assert-PackagedGatewayPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$PayloadDirectory
    )

    Assert-PackagedGatewayArchive $Release $ArchivePath
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $expected = Get-PackagedPayloadMetadata (Read-PackagedGatewayMetadata $Release $zip)
        $metadataPath = Join-Path $PayloadDirectory 'payload-metadata.json'
        if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
            throw 'Cached packaged Gateway is missing deployment metadata.'
        }
        $cached = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json -AsHashtable
        if ($cached -isnot [Collections.IDictionary] -or $cached.Count -ne $expected.Count) {
            throw 'Cached packaged Gateway has mismatched deployment metadata.'
        }
        foreach ($field in $expected.Keys) {
            if (-not $cached.Contains($field) -or $cached[$field] -cne $expected[$field]) {
                throw "Cached packaged Gateway has mismatched deployment metadata: $field."
            }
        }
        Assert-PackagedPayloadFiles $zip (Join-Path $PayloadDirectory 'app')
    }
    finally { $zip.Dispose() }
}

function Expand-PackagedGatewayPayload {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$OutputDirectory
    )

    Assert-PackagedGatewayArchive $Release $ArchivePath
    if (Test-Path -LiteralPath $OutputDirectory) {
        throw "Refusing to overwrite packaged Gateway payload '$OutputDirectory'."
    }
    # The caller supplies a protected parent. Publish only a completely extracted
    # payload; an interrupted extraction must never become a reusable cache.
    $staging = "$OutputDirectory-staging-$([guid]::NewGuid().ToString('N'))"
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $metadata = Read-PackagedGatewayMetadata $Release $zip
        # MSIX stores OPC part names, so scoped npm paths contain %40 rather than @.
        # Decode once before path validation and duplicate detection.
        $entries = @($zip.Entries |
            Where-Object { $_.FullName.StartsWith('app/', [StringComparison]::Ordinal) -and $_.Name } |
            ForEach-Object { [pscustomobject]@{ Entry = $_; Path = [Uri]::UnescapeDataString($_.FullName) } })
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $entries) {
            $entry = $file.Entry
            $name = $file.Path
            Assert-PackagedPath $name
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or
                ($entry.ExternalAttributes -band 0x400) -ne 0 -or
                -not $names.Add($name)) {
                throw "Unsafe or duplicate packaged Gateway path '$name'."
            }
        }
        foreach ($required in @('app/openclaw.mjs', 'app/shell-completions/openclaw.ps1')) {
            if (-not $names.Contains($required)) { throw "Packaged Gateway payload is missing '$required'." }
        }
        New-Item -ItemType Directory -Path $staging | Out-Null
        foreach ($file in $entries) {
            $destination = Join-Path $staging ($file.Path.Replace('/', [IO.Path]::DirectorySeparatorChar))
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($file.Entry, $destination, $false)
        }
        Assert-PackagedPayloadFiles $zip (Join-Path $staging 'app')
        Get-PackagedPayloadMetadata $metadata |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'payload-metadata.json') -Encoding utf8
        $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'packaging-release.json') -Encoding utf8
        Move-Item -LiteralPath $staging -Destination $OutputDirectory
        return $metadata
    }
    finally {
        $zip.Dispose()
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
}

Export-ModuleMember -Function Resolve-PackagedGatewayRelease, Save-PackagedGatewayArchive,
    Get-PackagedGatewayMetadata, Expand-PackagedGatewayPayload, Assert-PackagedGatewayPayload
