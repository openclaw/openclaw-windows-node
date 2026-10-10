Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-GatewayBuildInput {
    param([Parameter(Mandatory)][Collections.IDictionary]$Parameters)

    $selectors = @('OpenClawRef', 'OpenClawSourceDirectory', 'OpenClawPackageDirectory', 'OpenClawNpmVersion', 'GatewayChannel') |
        Where-Object { $Parameters.Keys -contains $_ }
    if (@($selectors).Count -gt 1) {
        throw 'Pass only one Gateway input: -GatewayChannel, -OpenClawNpmVersion, -OpenClawRef, -OpenClawSourceDirectory, or -OpenClawPackageDirectory.'
    }
    foreach ($selector in $selectors) {
        if ([string]::IsNullOrWhiteSpace($Parameters[$selector])) { throw "-$selector must not be empty." }
    }
    $npmVersion = $Parameters['OpenClawNpmVersion']
    $channel = $Parameters['GatewayChannel']
    if (-not $Parameters['Unregister'] -and @($selectors).Count -eq 0) {
        $channel = 'Latest'
    }
    if ($channel -and $channel -notin @('Latest', 'Packaged')) { throw 'GatewayChannel must be Latest or Packaged.' }
    if ($Parameters.Keys -contains 'PackagingRelease' -and
        ($channel -ne 'Packaged' -or [string]::IsNullOrWhiteSpace($Parameters['PackagingRelease']))) {
        throw '-PackagingRelease requires -GatewayChannel Packaged and a nonempty release tag.'
    }
    if ($channel -eq 'Packaged' -and
        ($Parameters.Keys -contains 'PackagingRef' -or $Parameters.Keys -contains 'PackagingDirectory')) {
        throw 'Packaged uses the release packaging commit; -PackagingRef and -PackagingDirectory cannot override it.'
    }
    if ($channel -eq 'Latest') { $npmVersion = 'latest' }
    $patch = $Parameters['Patch']
    if (-not $patch) {
        if ($Parameters['Unregister']) { throw '-Unregister requires -Patch to identify the development Gateway to remove.' }
        $patch = if ($channel -eq 'Packaged') { 'packaged' }
            elseif ($npmVersion -eq 'extended-stable') { 'npm-extstable' }
            elseif ($npmVersion) { 'npm-latest' }
            else { 'source' }
    }
    $patch = $patch.ToLowerInvariant()
    if ($patch -cnotmatch '^[a-z0-9](?:[a-z0-9-]{0,13}[a-z0-9])?$') {
        throw '-Patch must be 1 to 15 letters, digits, or hyphens, starting and ending with a letter or digit.'
    }
    [pscustomobject]@{ NpmVersion = $npmVersion; Patch = $patch; Channel = $channel }
}

function Resolve-GatewayLaunchInput {
    param(
        [switch]$UseStoreGateway,
        [ValidateSet('Latest', 'Packaged')][string]$GatewayChannel,
        [string]$PackagingRelease,
        [string]$ExistingPatch
    )

    if ($UseStoreGateway -and ($GatewayChannel -or $PackagingRelease)) {
        throw '-UseStoreGateway and Gateway channel selection are mutually exclusive.'
    }
    if ($GatewayChannel -and $ExistingPatch) {
        throw '-GatewayChannel cannot override OPENCLAW_NATIVE_GATEWAY_DEV_PATCH. Clear the variable first.'
    }
    if ($PackagingRelease -and $GatewayChannel -ne 'Packaged') {
        throw '-PackagingRelease requires -GatewayChannel Packaged.'
    }
    if ($ExistingPatch -and -not $UseStoreGateway -and
        $ExistingPatch -cnotmatch '^[a-z0-9](?:[a-z0-9-]{0,13}[a-z0-9])?$') {
        throw 'OPENCLAW_NATIVE_GATEWAY_DEV_PATCH must be a lowercase development patch identity.'
    }
    $patch = if ($UseStoreGateway) { $null }
        elseif ($ExistingPatch) { $ExistingPatch }
        elseif ($GatewayChannel -eq 'Packaged') { 'packaged' }
        else { 'npm-latest' }
    [pscustomobject]@{
        Patch = $patch
        Build = -not $UseStoreGateway -and -not $ExistingPatch
    }
}

function Resolve-GatewayNpmRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('\A(?:latest|extended-stable|\d{4}\.(?:[1-9]|1[0-2])\.\d+(?:-\d+)?)\z')]
        [string]$Selector
    )

    $metadata = Invoke-RestMethod -Uri "https://registry.npmjs.org/openclaw/$Selector" -TimeoutSec 60
    $version = [string]$metadata.version
    if ($version -cnotmatch '\A\d{4}\.(?:[1-9]|1[0-2])\.\d+(?:-\d+)?\z') {
        throw "npm selector '$Selector' did not resolve to an exact stable Gateway version."
    }
    if ($Selector -notin @('latest', 'extended-stable') -and $version -cne $Selector) {
        throw "npm returned Gateway '$version' for exact version '$Selector'."
    }
    $integrity = [string]$metadata.dist.integrity
    if ($integrity -cnotmatch '\Asha512-([A-Za-z0-9+/]{86}==)\z') {
        throw "Gateway '$version' has no supported npm SHA-512 integrity."
    }
    $tarball = [string]$metadata.dist.tarball
    if ($tarball -cne "https://registry.npmjs.org/openclaw/-/openclaw-$version.tgz") {
        throw "Gateway '$version' has an unexpected npm tarball URL."
    }
    [pscustomobject][ordered]@{
        selector = $Selector
        version = $version
        integrity = $integrity
        tarball = $tarball
    }
}

function Save-GatewayNpmPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][string]$PackageDirectory
    )

    # The caller owns the protected directory. Never extract an npm archive into
    # it: read only the two identity files after verifying the downloaded bytes.
    $archive = Join-Path $PackageDirectory 'openclaw.tgz'
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        $download = Join-Path $PackageDirectory ('download-' + [guid]::NewGuid().ToString('N') + '.tgz')
        try {
            Invoke-WebRequest -Uri $Release.tarball -OutFile $download -TimeoutSec 300
            Assert-GatewayNpmIntegrity -Archive $download -Integrity $Release.integrity
            Move-Item -LiteralPath $download -Destination $archive
        }
        finally {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download }
        }
    }
    Assert-GatewayNpmIntegrity -Archive $archive -Integrity $Release.integrity

    $packageText = & tar -xOf $archive package/package.json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read npm Gateway package.json.' }
    $package = ($packageText -join "`n") | ConvertFrom-Json
    $buildText = & tar -xOf $archive package/dist/build-info.json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read npm Gateway build-info.json.' }
    $build = ($buildText -join "`n") | ConvertFrom-Json
    if ($package.name -cne 'openclaw' -or $package.version -cne $Release.version -or
        $build.version -cne $Release.version -or $build.commit -cnotmatch '\A[0-9a-f]{40}\z') {
        throw 'The npm Gateway archive identity does not match its resolved release.'
    }
    $nodeVersion = & node -p 'process.versions.node'
    if ($LASTEXITCODE -ne 0 -or $nodeVersion -cnotmatch '\A\d+\.\d+\.\d+\z') {
        throw 'Cannot determine the native payload Node.js version.'
    }
    [ordered]@{
        repository = 'https://github.com/openclaw/openclaw'
        requestedRef = "npm:$($Release.selector)"
        resolvedCommit = $build.commit
        packageVersion = $Release.version
        nodeVersion = $nodeVersion
        packageSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        npmIntegrity = $Release.integrity
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PackageDirectory 'source.json') -Encoding utf8
}

function Assert-GatewayNpmIntegrity {
    param([string]$Archive, [string]$Integrity)

    $hash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA512).Hash
    $actual = 'sha512-' + [Convert]::ToBase64String([Convert]::FromHexString($hash))
    if ($actual -cne $Integrity) {
        throw "Gateway npm archive '$Archive' failed SHA-512 integrity verification."
    }
}

Export-ModuleMember -Function Resolve-GatewayBuildInput, Resolve-GatewayLaunchInput,
    Resolve-GatewayNpmRelease, Save-GatewayNpmPackage
