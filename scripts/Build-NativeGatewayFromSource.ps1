<#
.SYNOPSIS
    Dev helper: build the native (MSIX, isolated-session) OpenClaw Gateway from source and
    register it side by side with any Microsoft Store Gateway.

.DESCRIPTION
    Builds OpenClaw from an openclaw/openclaw ref (default main), a local checkout, or a
    prebuilt openclaw.tgz, then packages it with openclaw/openclaw-windows-packaging and
    registers it as a Developer Mode loose layout under the patched identity
    OpenClawFoundation.OpenClawGateway-<Patch> (commands openclaw-<Patch> and clawctl-<Patch>).

    Companion uses this package only when it is started with
    OPENCLAW_NATIVE_GATEWAY_DEV_PATCH=<Patch>. Without that variable Companion keeps using the
    Microsoft Store Gateway. This script never touches Companion settings or the Store package.

    The registration links the payload under -WorkRoot and the packaging checkout's
    artifacts\local-package, so neither may be deleted while the package is registered.
    The Gateway runs as a separate isolated agent account, which must be able to read both,
    including every parent directory. Neither may be under your user profile.
    New work directories grant other authenticated accounts read/execute only. Existing
    writable trees or replaceable ancestors are rejected without modifying their permissions.
    Only one source build or unregister may run on this machine at a time.

    Requires Windows Developer Mode, git, the .NET SDK and Visual Studio Build Tools needed by
    the packaging repo, and (for source builds) Node.js and pnpm matching the source's
    packageManager. Node.js must be win32/<Architecture>.

.PARAMETER OpenClawRef
    Branch, tag, or full SHA of https://github.com/openclaw/openclaw.git. Default: main.

.PARAMETER OpenClawSourceDirectory
    Existing local openclaw checkout to build as-is. Uncommitted changes are allowed.

.PARAMETER OpenClawPackageDirectory
    Prebuilt directory containing openclaw.tgz and source.json. Skips the source build.

.PARAMETER PackagingRef
    Ref of https://github.com/openclaw/openclaw-windows-packaging.git. Default: main.

.PARAMETER PackagingDirectory
    Existing packaging checkout, used as-is (no fetch or checkout).

.PARAMETER Patch
    Patched identity suffix: 1 to 15 letters, digits, or hyphens, lowercased. Default: source.

.PARAMETER Architecture
    x64 or arm64. Default: this device's OS architecture.

.PARAMETER WorkRoot
    Checkouts, packages, and payloads. Must be outside your user profile so the isolated agent
    account can read the payload. Default: %SystemDrive%\OpenClawDev\native-gateway-source.

.PARAMETER Force
    Build a new payload even when a matching one exists, and re-register.

.PARAMETER Unregister
    Tear down the patch's isolated session and remove its registration. Payloads are kept.

.EXAMPLE
    .\scripts\Build-NativeGatewayFromSource.ps1
    Build main and register OpenClawFoundation.OpenClawGateway-source.

.EXAMPLE
    .\scripts\Build-NativeGatewayFromSource.ps1 -OpenClawSourceDirectory D:\src\openclaw -Patch mywork
    Build a local checkout, including uncommitted changes, as openclaw-mywork / clawctl-mywork.

.EXAMPLE
    .\scripts\Build-NativeGatewayFromSource.ps1 -Unregister -Patch source
    Remove the registration created by the first example.
#>

#Requires -Version 7.4

[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(ParameterSetName = 'Build')]
    [string]$OpenClawRef = 'main',

    [Parameter(ParameterSetName = 'Build')]
    [string]$OpenClawSourceDirectory,

    [Parameter(ParameterSetName = 'Build')]
    [string]$OpenClawPackageDirectory,

    [Parameter(ParameterSetName = 'Build')]
    [string]$PackagingRef = 'main',

    [string]$PackagingDirectory,

    [string]$Patch = 'source',

    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$WorkRoot = (Join-Path $env:SystemDrive 'OpenClawDev\native-gateway-source'),

    [Parameter(ParameterSetName = 'Build')]
    [switch]$Force,

    [Parameter(Mandatory, ParameterSetName = 'Unregister')]
    [switch]$Unregister
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NativeGatewaySourceBuild.psm1') -Force

$openClawUrl = 'https://github.com/openclaw/openclaw.git'
$packagingUrl = 'https://github.com/openclaw/openclaw-windows-packaging.git'
$storePublisher = 'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC'

function Assert-ExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

# Checks the exit code before touching output, so a failing command reports its step, not a null-method error.
function Get-NativeOutput([string]$Step, [scriptblock]$Command) {
    $output = & $Command
    Assert-ExitCode $Step
    (@($output) -join "`n").Trim()
}

function Assert-Command([string]$Name, [string]$Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found on PATH. $Hint"
    }
}

function Sync-Checkout([string]$Url, [string]$Path, [string]$Ref) {
    if (-not (Test-Path -LiteralPath $Path)) {
        & git clone --filter=blob:none $Url $Path
        Assert-ExitCode "git clone $Url"
    }
    $status = & git -C $Path status --porcelain
    Assert-ExitCode "git status in $Path"
    if ($status) {
        throw "Refusing to update '$Path': it has uncommitted changes. Commit or discard them, or pass the matching *Directory parameter to build it as-is."
    }
    & git -C $Path fetch origin $Ref
    Assert-ExitCode "git fetch $Ref"
    & git -C $Path checkout --detach FETCH_HEAD
    Assert-ExitCode "git checkout $Ref"
}

function Get-AliasPath([object]$Package, [string]$Command) {
    $aliasDirectory = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$($Package.PackageFamilyName)"
    Join-Path $aliasDirectory "$Command-$Patch.exe"
}

# The isolated agent account resolves the package layout and payload through every parent
# directory; a user profile denies it even lstat, so Node.js fails to load the payload.
function Assert-OutsideUserProfile([string]$Path, [string]$Parameter) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $userProfile = [IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\')
    if ($full -ieq $userProfile -or $full.StartsWith("$userProfile\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Parameter '$full' is inside your user profile, which the Gateway's isolated agent account cannot read. Use a directory such as $env:SystemDrive\OpenClawDev."
    }
}

# --- Validation and derived values ---------------------------------------------------------

if (-not $IsWindows) {
    throw 'The native Gateway can only be built and registered on Windows.'
}

$Patch = $Patch.ToLowerInvariant()
if ($Patch -cnotmatch '^[a-z0-9](?:[a-z0-9-]{0,13}[a-z0-9])?$') {
    throw '-Patch must be 1 to 15 letters, digits, or hyphens, starting and ending with a letter or digit.'
}

$sourceSelectors = @('OpenClawRef', 'OpenClawSourceDirectory', 'OpenClawPackageDirectory') |
    Where-Object { $PSBoundParameters.ContainsKey($_) }
if (@($sourceSelectors).Count -gt 1) {
    throw 'Pass at most one of -OpenClawRef, -OpenClawSourceDirectory, and -OpenClawPackageDirectory.'
}

if (-not $Architecture) {
    $Architecture = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'Arm64' { 'arm64' }
        'X64' { 'x64' }
        default { throw "Unsupported OS architecture '$_'. Pass -Architecture x64 or arm64." }
    }
}

# Anchor every derived path to one absolute root: native tools resolve relative paths against
# their own working directory (the OpenClaw checkout during packaging), not the caller's.
# Resolves against the PowerShell location, which .NET's GetFullPath does not track.
$WorkRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkRoot)

$packageName = "OpenClawFoundation.OpenClawGateway-$Patch"
$packagingRoot = if ($PackagingDirectory) {
    (Resolve-Path -LiteralPath $PackagingDirectory).Path
}
else {
    Join-Path $WorkRoot 'openclaw-windows-packaging'
}
$deployScript = Join-Path $packagingRoot 'scripts\Deploy-LocalPackage.ps1'
if (-not $Unregister) {
    Assert-OutsideUserProfile $WorkRoot '-WorkRoot'
    Assert-OutsideUserProfile $packagingRoot '-PackagingDirectory'
}

# --- Unregister ------------------------------------------------------------------------------

Invoke-NativeGatewaySourceBuildLocked {
if ($Unregister) {
    if (-not (Test-Path -LiteralPath $deployScript -PathType Leaf)) {
        throw "Deploy-LocalPackage.ps1 was not found under '$packagingRoot'. Pass -PackagingDirectory with the packaging checkout used to register the patch."
    }
    Assert-NativeGatewayTreeAcl $packagingRoot
    $registered = @(Get-AppxPackage -Name $packageName)
    if ($registered.Count -gt 0) {
        # Check ownership before teardown so a registration Deploy-LocalPackage will refuse to
        # remove (other checkout or architecture) does not lose its isolated session.
        $expectedLayout = Join-Path $packagingRoot "artifacts\local-package\patches\$Patch\$Architecture\layout"
        $installLocation = [IO.Path]::GetFullPath([string]$registered[0].InstallLocation).TrimEnd('\')
        if (-not $registered[0].IsDevelopmentMode -or $installLocation -ine $expectedLayout.TrimEnd('\')) {
            throw "$packageName is registered from $installLocation, not $expectedLayout. Pass the -PackagingDirectory and -Architecture used to register it."
        }
        $clawctl = Get-AliasPath $registered[0] 'clawctl'
        if (-not (Test-Path -LiteralPath $clawctl)) {
            throw "$clawctl was not found, so the isolated session cannot be torn down first. Re-enable the package's clawctl-$Patch alias in Windows Settings, then retry."
        }
        & $clawctl teardown --force
        Assert-ExitCode "clawctl-$Patch teardown"
    }
    & $deployScript -Unregister -Patch $Patch -Architecture $Architecture
    Write-Host "Unregistered $packageName. Payloads under $WorkRoot were kept and can now be deleted manually."
    return
}

Initialize-NativeGatewayDirectory $WorkRoot
if (Test-Path -LiteralPath $packagingRoot) {
    Assert-NativeGatewayTreeAcl $packagingRoot
}

# --- Build: tools ------------------------------------------------------------------------------

Assert-Command 'git' 'Install Git for Windows.'
# Build-Payload.ps1 always installs and inspects the payload with local Node.js, even for a prebuilt package.
Assert-Command 'node' 'Install Node.js 24.16 or later.'
if (-not $OpenClawPackageDirectory) {
    Assert-Command 'pnpm' 'Install pnpm with: npm install -g pnpm'
}
$nodeTarget = Get-NativeOutput 'node -p' { node -p "process.platform + '/' + process.arch" }
if ($nodeTarget -cne "win32/$Architecture") {
    throw "Node.js must be win32/$Architecture to build the $Architecture payload; found $nodeTarget."
}

# --- Build: packaging checkout -----------------------------------------------------------------

if (-not $PackagingDirectory) {
    Sync-Checkout $packagingUrl $packagingRoot $PackagingRef
}
# The payload embeds the packaging checkout's gateway-isolation plugin, so it keys payload reuse too.
$packagingCommit = (Get-NativeOutput 'git rev-parse HEAD (packaging)' { git -C $packagingRoot rev-parse HEAD }).ToLowerInvariant()
$packagingDirty = [bool](Get-NativeOutput "git status in $packagingRoot" { git -C $packagingRoot status --porcelain })
if ($packagingDirty) {
    Write-Warning "Packaging with uncommitted changes from $packagingRoot."
}

# --- Build: source identity ----------------------------------------------------------------------

$sourceRoot = $null
$dirty = $false
$packageSha = $null
if ($OpenClawPackageDirectory) {
    $packageDir = (Resolve-Path -LiteralPath $OpenClawPackageDirectory).Path
    Assert-NativeGatewayTreeAcl $packageDir
    $sourceJson = Read-NativeGatewaySourceMetadata $packageDir
    $commit = ([string]$sourceJson.resolvedCommit).Trim().ToLowerInvariant()
    $version = [string]$sourceJson.packageVersion
    # Keep the producer's requested ref: Test-OpenClawPackage.ps1 rewrites it into source.json.
    $requestedRef = if ($sourceJson.Contains('requestedRef') -and $sourceJson.requestedRef) {
        [string]$sourceJson.requestedRef
    }
    else {
        $commit
    }
    $packageSha = [string]$sourceJson.packageSha256
}
else {
    if ($OpenClawSourceDirectory) {
        $sourceRoot = (Resolve-Path -LiteralPath $OpenClawSourceDirectory).Path
        Assert-NativeGatewayTreeAcl $sourceRoot
        $status = & git -C $sourceRoot status --porcelain
        Assert-ExitCode "git status in $sourceRoot"
        $dirty = [bool]$status
        if ($dirty) {
            Write-Warning "Building uncommitted changes from $sourceRoot."
        }
    }
    else {
        $sourceRoot = Join-Path $WorkRoot 'openclaw'
        if (Test-Path -LiteralPath $sourceRoot) {
            Assert-NativeGatewayTreeAcl $sourceRoot
        }
        Sync-Checkout $openClawUrl $sourceRoot $OpenClawRef
    }

    $commit = (Get-NativeOutput 'git rev-parse HEAD' { git -C $sourceRoot rev-parse HEAD }).ToLowerInvariant()
    $requestedRef = if ($OpenClawSourceDirectory) { $commit } else { $OpenClawRef }

    $sourcePackage = Get-Content -LiteralPath (Join-Path $sourceRoot 'package.json') -Raw | ConvertFrom-Json
    if ($sourcePackage.name -cne 'openclaw') {
        throw "'$sourceRoot' is not an openclaw checkout (package.json name is '$($sourcePackage.name)')."
    }
    $version = [string]$sourcePackage.version

    $wantPnpm = ([string]$sourcePackage.packageManager -replace '^pnpm@', '' -split '\+')[0]
    $foundPnpm = Get-NativeOutput 'pnpm --version' { pnpm --version }
    if ($foundPnpm.Split('.')[0] -ne $wantPnpm.Split('.')[0]) {
        throw "pnpm $foundPnpm does not match the source's packageManager pnpm@$wantPnpm. Install it with: npm install -g pnpm@$wantPnpm"
    }
}

# --- Build: payload identity -----------------------------------------------------------------

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')
# Short SHAs keep deep node_modules paths under the payload within MAX_PATH; metadata holds the full commit.
$baseId = "$($commit.Substring(0, 12))-p$($packagingCommit.Substring(0, 12))"
$payloadId = if ($dirty -or $packagingDirty) {
    "$baseId-dirty-$stamp"
}
elseif ($Force) {
    "$baseId-$stamp"
}
elseif ($OpenClawPackageDirectory) {
    "$baseId-$($packageSha.Substring(0, 12))"
}
else {
    $baseId
}
$payloadDir = Join-Path $WorkRoot "payloads\$Architecture\$payloadId"
Initialize-NativeGatewayDirectory (Split-Path $payloadDir -Parent)

$reuse = $false
if (Test-Path -LiteralPath $payloadDir) {
    Assert-NativeGatewayTreeAcl $payloadDir
    $metadataPath = Join-Path $payloadDir 'payload-metadata.json'
    $metadata = if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
        Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    }
    if ($metadata -and $metadata.resolvedCommit -eq $commit -and $metadata.architecture -eq $Architecture) {
        Write-Host "Reusing payload $payloadDir"
        $reuse = $true
    }
    else {
        # Never delete it: a registration may link it.
        throw "Payload directory '$payloadDir' exists but is incomplete. Re-run with -Force to build a new one."
    }
}

if (-not $reuse) {
    # --- Build: package (openclaw.tgz + source.json) ---------------------------------------------

    if (-not $OpenClawPackageDirectory) {
        Initialize-NativeGatewayDirectory (Join-Path $WorkRoot 'packages')
        $packageDir = Join-Path $WorkRoot "packages\$payloadId"
        if (Test-Path -LiteralPath $packageDir) {
            Assert-NativeGatewayTreeAcl $packageDir
            Remove-Item -LiteralPath $packageDir -Recurse -Force
        }
        New-Item -ItemType Directory -Path $packageDir | Out-Null

        # Same sequence as openclaw-windows-packaging .github/workflows/gateway-msix.yml build-package.
        Push-Location $sourceRoot
        $previousReleaseBuild = $env:OPENCLAW_CONTROL_UI_RELEASE_BUILD
        try {
            & pnpm install --frozen-lockfile
            Assert-ExitCode 'pnpm install'
            $env:OPENCLAW_CONTROL_UI_RELEASE_BUILD = '1'
            & pnpm build
            Assert-ExitCode 'pnpm build'
            & node scripts/package-openclaw-for-docker.mjs --allow-unreleased-changelog --skip-build --pnpm-pack `
                --output-dir $packageDir --output-name openclaw.tgz
            Assert-ExitCode 'package-openclaw-for-docker.mjs'
        }
        finally {
            $env:OPENCLAW_CONTROL_UI_RELEASE_BUILD = $previousReleaseBuild
            Pop-Location
        }

        $nodeVersion = Get-NativeOutput 'node -p process.versions.node' { node -p 'process.versions.node' }
        [ordered]@{
            repository = 'https://github.com/openclaw/openclaw'
            requestedRef = $requestedRef
            resolvedCommit = $commit
            packageVersion = $version
            nodeVersion = $nodeVersion
            packageSha256 = (Get-FileHash -LiteralPath (Join-Path $packageDir 'openclaw.tgz') -Algorithm SHA256).Hash.ToLowerInvariant()
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDir 'source.json') -Encoding utf8
    }

    & (Join-Path $packagingRoot 'scripts\Test-OpenClawPackage.ps1') -PackageDirectory $packageDir `
        -ExpectedCommit $commit -ExpectedVersion $version -RequestedRef $requestedRef

    # --- Build: payload ----------------------------------------------------------------------------

    $runnerTemp = Join-Path $WorkRoot ('temp-' + [guid]::NewGuid().ToString('N'))
    Initialize-NativeGatewayDirectory $runnerTemp
    $previousRunnerTemp = $env:RUNNER_TEMP
    try {
        $env:RUNNER_TEMP = $runnerTemp
        & (Join-Path $packagingRoot 'scripts\Build-Payload.ps1') -PackageDirectory $packageDir `
            -Architecture $Architecture -OutputDirectory $payloadDir
    }
    catch {
        # Not registered yet, so a partial payload is safe to remove.
        if (Test-Path -LiteralPath $payloadDir) {
            Remove-Item -LiteralPath $payloadDir -Recurse -Force
        }
        throw
    }
    finally {
        $env:RUNNER_TEMP = $previousRunnerTemp
        Remove-Item -LiteralPath $runnerTemp -Recurse -Force
    }
}

# --- Build: deploy -----------------------------------------------------------------------------

& $deployScript -Patch $Patch -Architecture $Architecture -PayloadDirectory $payloadDir -Force:$Force

# --- Build: verify and report ------------------------------------------------------------------

$registered = @(Get-AppxPackage -Name $packageName)
if ($registered.Count -ne 1 -or $registered[0].Publisher -cne $storePublisher) {
    throw "Expected exactly one $packageName registration published by $storePublisher."
}
$clawctl = Get-AliasPath $registered[0] 'clawctl'
$openclaw = Get-AliasPath $registered[0] 'openclaw'
if (-not (Test-Path -LiteralPath $clawctl) -or -not (Test-Path -LiteralPath $openclaw)) {
    throw "The package-qualified aliases were not found under $(Split-Path $clawctl). Companion requires them."
}
$versionText = (& $clawctl --version) -join "`n"
if ($LASTEXITCODE -ne 0 -or
    ($versionText -notmatch [regex]::Escape($commit) -and $versionText -notmatch [regex]::Escape($commit.Substring(0, 12)))) {
    throw "clawctl-$Patch reports a different payload than $commit. Output: $versionText"
}

Write-Host ''
Write-Host "Registered $($registered[0].PackageFamilyName)"
Write-Host "OpenClaw $version ($commit)"
Write-Host "Payload: $payloadDir"
Write-Host ''
Write-Host "To use it in Companion, start Companion from a shell with: `$env:OPENCLAW_NATIVE_GATEWAY_DEV_PATCH = '$Patch'"
Write-Host 'Then choose the local native Gateway in setup. Without this variable Companion keeps using the Microsoft Store Gateway.'
Write-Host "Do not delete $WorkRoot or $packagingRoot while registered. Remove with: .\scripts\Build-NativeGatewayFromSource.ps1 -Unregister -Patch $Patch"
}
