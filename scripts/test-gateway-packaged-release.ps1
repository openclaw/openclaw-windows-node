#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'GatewayPackagedRelease.psm1') -Force
$module = Get-Module GatewayPackagedRelease
$script:passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
function Assert-Throws([scriptblock]$Action, [string]$Expected) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        $script:passed++
        return
    }
    throw "Expected failure containing '$Expected'."
}

function New-Fixture {
    param(
        [string]$Path,
        [string]$Architecture = 'arm64',
        [string]$BuildVersion = '2026.8.20',
        [string]$Commit = ('a' * 40),
        [string]$ExtraPath,
        [int]$ExtraAttributes = 0,
        [switch]$MissingEntrypoint,
        [switch]$MissingRuntime
    )
    $files = [ordered]@{
        'app/package.json' = '{"name":"openclaw","version":"2026.8.20"}'
        'app/dist/build-info.json' = (@{ version = $BuildVersion; commit = $Commit; buildId = 'packaging-build-identity' } | ConvertTo-Json)
        'app/shell-completions/openclaw.ps1' = '# fixture'
        'app/node_modules/@scope/types/LICENSE' = 'scoped package fixture'
        'app/percent%40literal.txt' = 'literal percent fixture'
    }
    if (-not $MissingEntrypoint) { $files['app/openclaw.mjs'] = '// released bytes, not npm bytes' }
    $inventory = @($files.GetEnumerator() | ForEach-Object {
        $bytes = [Text.Encoding]::UTF8.GetBytes($_.Value)
        @{
            path = $_.Key.Substring(4)
            length = $bytes.Length
            sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        }
    })
    $files['payload/payload-files.json'] = @{ files = $inventory } | ConvertTo-Json -Depth 5
    $files['AppxManifest.xml'] = @"
<Package><Identity Name="OpenClaw.Gateway" Version="2026.9.900.0" ProcessorArchitecture="$Architecture" Publisher="CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US" /></Package>
"@
    if (-not $MissingRuntime) { $files["runtime/node-v24.21.0-win-$Architecture.zip"] = 'runtime fixture' }
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files.GetEnumerator()) {
            $partName = ($file.Key.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
            $entry = $zip.CreateEntry($partName)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($file.Value) }
            finally { $writer.Dispose() }
        }
        if ($ExtraPath) {
            $entry = $zip.CreateEntry($ExtraPath)
            $entry.ExternalAttributes = $ExtraAttributes
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('unsafe') }
            finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('oc-packaged-test-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $archive = Join-Path $root 'fixture.msix'
    New-Fixture -Path $archive
    $asset = @{
        name = 'OpenClawGateway-2026.9.9-msix.0-arm64.msix'
        browser_download_url = 'https://github.com/openclaw/openclaw-windows-packaging/releases/download/v2026.9.9-msix.0/OpenClawGateway-2026.9.9-msix.0-arm64.msix'
        digest = 'sha256:' + (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $response = @{
        tag_name = 'v2026.9.9-msix.0'; draft = $false; prerelease = $false; assets = @($asset)
        target_commitish = 'main'
    }
    $tagObject = @{ type = 'commit'; sha = 'b' * 40 }
    & $module {
        param($Response, $TagObject, $Archive)
        $script:response = $Response
        $script:tagObject = $TagObject
        $script:archive = $Archive
        $script:requests = @()
        function script:Invoke-PackagingApi([string]$Path) {
            $script:requests += $Path
            if ($Path.StartsWith('releases/')) { return $script:response }
            if ($Path.StartsWith('git/tags/')) { return @{ object = @{ type = 'commit'; sha = 'b' * 40 } } }
            return @{ object = $script:tagObject }
        }
        function script:Invoke-WebRequest {
            param($Uri, $OutFile, $TimeoutSec)
            Copy-Item -LiteralPath $script:archive -Destination $OutFile
        }
    } $response $tagObject $archive
    $release = Resolve-PackagedGatewayRelease -Architecture arm64
    Assert-True ($release.packagingRelease -ceq 'v2026.9.9-msix.0' -and $release.packagingCommit -ceq ('b' * 40)) 'Latest published release must resolve to an exact packaging commit.'
    $tagObject.type = 'tag'
    $pinned = Resolve-PackagedGatewayRelease -Architecture arm64 -PackagingRelease v2026.9.9-msix.0
    Assert-True ($pinned.packagingCommit -ceq ('b' * 40)) 'Annotated release tags must be dereferenced.'
    $tagObject.type = 'commit'
    $requests = & $module { $script:requests }
    Assert-True ($requests -contains 'releases/latest' -and $requests -contains 'releases/tags/v2026.9.9-msix.0') 'Explicit pins must not consult latest or policy.'
    $response.draft = $true
    Assert-Throws { Resolve-PackagedGatewayRelease -Architecture arm64 } 'published'
    $response.draft = $false
    $response.prerelease = $true
    Assert-Throws { Resolve-PackagedGatewayRelease -Architecture arm64 } 'published'
    $response.prerelease = $false
    Assert-Throws { Resolve-PackagedGatewayRelease -Architecture x64 } 'exactly one'
    Assert-Throws { Resolve-PackagedGatewayRelease -Architecture arm64 -PackagingRelease v2026.9.8-msix.0 } 'published'
    $tagObject.type = 'tree'
    Assert-Throws { Resolve-PackagedGatewayRelease -Architecture arm64 } 'exact commit'
    $tagObject.type = 'commit'
    $digest = $asset.digest
    $asset.digest = $null
    Assert-Throws { Resolve-PackagedGatewayRelease -Architecture arm64 } 'SHA-256'
    $asset.digest = $digest
    $url = $asset.browser_download_url
    $asset.browser_download_url = 'https://example.com/fixture.msix'
    Assert-Throws { Resolve-PackagedGatewayRelease -Architecture arm64 } 'canonical'
    $asset.browser_download_url = $url
    $response.assets = @($asset, $asset)
    Assert-Throws { Resolve-PackagedGatewayRelease -Architecture arm64 } 'exactly one'
    $response.assets = @($asset)

    $cache = Join-Path $root 'cache'
    New-Item -ItemType Directory -Path $cache | Out-Null
    $saved = Save-PackagedGatewayArchive -Release $release -Directory $cache
    Assert-True ((Save-PackagedGatewayArchive -Release $release -Directory $cache) -ceq $saved) 'Verified download must be reusable.'
    $metadata = Get-PackagedGatewayMetadata -Release $release -ArchivePath $saved
    Assert-True ($metadata.version -ceq '2026.8.20' -and $metadata.msixVersion -ceq '2026.9.900.0') 'An older Gateway under a newer MSIX identity must use the embedded payload version.'
    Assert-True ($metadata.gatewayCommit -ceq ('a' * 40) -and $metadata.nodeVersion -ceq '24.21.0' -and
        $metadata.buildId -ceq 'packaging-build-identity') 'Runtime and build provenance must come from released bytes.'
    $payload = Join-Path $root 'payload'
    $null = Expand-PackagedGatewayPayload -Release $release -ArchivePath $saved -OutputDirectory $payload
    $payloadMetadata = Get-Content -LiteralPath (Join-Path $payload 'payload-metadata.json') -Raw | ConvertFrom-Json
    Assert-True ($payloadMetadata.layout -ceq 'expanded-directory' -and $payloadMetadata.packageVersion -ceq '2026.8.20') 'Extracted payload must satisfy upstream deployment metadata.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $payload 'app\openclaw.mjs') -Raw) -ceq '// released bytes, not npm bytes') 'Native payload must not be replaced by same-version npm bytes.'
    Assert-True (Test-Path -LiteralPath (Join-Path $payload 'app\node_modules\@scope\types\LICENSE')) 'MSIX part names must decode scoped package paths.'
    Assert-True (Test-Path -LiteralPath (Join-Path $payload 'app\percent%40literal.txt')) 'MSIX part names must be decoded exactly once.'
    Assert-PackagedGatewayPayload -Release $release -ArchivePath $saved -PayloadDirectory $payload
    $metadataPath = Join-Path $payload 'payload-metadata.json'
    $originalMetadata = Get-Content -LiteralPath $metadataPath -Raw
    foreach ($field in @('repository', 'requestedRef', 'resolvedCommit', 'packageVersion', 'architecture', 'layout', 'nodeVersion')) {
        $modified = $originalMetadata | ConvertFrom-Json -AsHashtable
        $modified[$field] = 'modified'
        $modified | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
        Assert-Throws { Assert-PackagedGatewayPayload -Release $release -ArchivePath $saved -PayloadDirectory $payload } "deployment metadata: $field"
    }
    $modified = $originalMetadata | ConvertFrom-Json -AsHashtable
    $modified['extra'] = 'modified'
    $modified | ConvertTo-Json | Set-Content -LiteralPath $metadataPath
    Assert-Throws { Assert-PackagedGatewayPayload -Release $release -ArchivePath $saved -PayloadDirectory $payload } 'mismatched deployment metadata'
    Remove-Item -LiteralPath $metadataPath
    Assert-Throws { Assert-PackagedGatewayPayload -Release $release -ArchivePath $saved -PayloadDirectory $payload } 'missing deployment metadata'
    $originalMetadata | Set-Content -LiteralPath $metadataPath
    Assert-PackagedGatewayPayload -Release $release -ArchivePath $saved -PayloadDirectory $payload
    Assert-Throws { Expand-PackagedGatewayPayload -Release $release -ArchivePath $saved -OutputDirectory $payload } 'Refusing to overwrite'
    'modified' | Set-Content -LiteralPath (Join-Path $payload 'app\openclaw.mjs')
    Assert-Throws { Assert-PackagedGatewayPayload -Release $release -ArchivePath $saved -PayloadDirectory $payload } 'release inventory'
    'corrupt' | Set-Content -LiteralPath $saved
    Assert-Throws { Save-PackagedGatewayArchive -Release $release -Directory $cache } 'SHA-256'
    Remove-Item -LiteralPath $saved
    $release.assetSha256 = '0' * 64
    Assert-Throws { Save-PackagedGatewayArchive -Release $release -Directory $cache } 'SHA-256'
    Assert-True (@(Get-ChildItem -LiteralPath $cache).Count -eq 0) 'Failed downloads must not leave reusable files.'

    foreach ($case in @(
        @{ Architecture = 'x64'; Error = 'identity or architecture' },
        @{ BuildVersion = '2026.9.9'; Error = 'identities do not agree' },
        @{ Commit = 'not-a-commit'; Error = 'identities do not agree' },
        @{ MissingRuntime = $true; Error = 'runtime archive' }
    )) {
        $errorText = $case.Error
        $case.Remove('Error')
        New-Fixture -Path $archive @case
        $release.assetSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-Throws { Get-PackagedGatewayMetadata -Release $release -ArchivePath $archive } $errorText
    }
    foreach ($case in @(
        @{ ExtraPath = 'app/../escaped.txt' },
        @{ ExtraPath = 'app/%2e%2e/escaped.txt' },
        @{ ExtraPath = 'app/foo\bar' },
        @{ ExtraPath = 'app/foo%5cbar' },
        @{ ExtraPath = 'app/foo:stream' },
        @{ ExtraPath = 'app/foo%3astream' },
        @{ ExtraPath = 'app/CON.txt' },
        @{ ExtraPath = 'app/trailing./file' },
        @{ ExtraPath = 'app/OPENCLAW.MJS' },
        @{ ExtraPath = 'app/%4fPENCLAW.MJS' },
        @{ ExtraPath = 'app/link'; ExtraAttributes = -1610612736 },
        @{ ExtraPath = 'app/link'; ExtraAttributes = 1024 }
    )) {
        New-Fixture -Path $archive @case
        $release.assetSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $badPayload = Join-Path $root 'bad-payload'
        Assert-Throws { Expand-PackagedGatewayPayload -Release $release -ArchivePath $archive -OutputDirectory $badPayload } 'Unsafe'
        Assert-True (-not (Test-Path -LiteralPath $badPayload)) 'Unsafe extraction must never publish a payload.'
    }
    New-Fixture -Path $archive -MissingEntrypoint
    $release.assetSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-Throws { Expand-PackagedGatewayPayload -Release $release -ArchivePath $archive -OutputDirectory (Join-Path $root 'missing') } 'missing'
    Assert-True (@(Get-ChildItem -LiteralPath $root -Filter '*-staging-*').Count -eq 0) 'Failed extraction must clean its staging directory.'
}
finally {
    Remove-Module GatewayPackagedRelease
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
Write-Host "Packaged Gateway regressions: $script:passed passed."
