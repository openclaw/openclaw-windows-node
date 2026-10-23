<#
.SYNOPSIS
    Exercises the installed package's real isolated-session lifecycle on a disposable Windows guest.
.DESCRIPTION
    Requires an approved signed MSIX and matching digests, a clean exact source checkout,
    and an unused current-user Gateway installation. Never falls back to WSL or npm.
    This proves package lifecycle, not authenticated Companion pairing or system.run containment.
#>
#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
    [Parameter(Mandatory)][string]$MsixPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$MsixSha256,
    [Parameter(Mandatory)][string]$WxcExecPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$WxcExecSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$PackagingSha,
    [Parameter(Mandatory)][switch]$DisposableHost,
    [ValidateRange(30, 900)][int]$CommandTimeoutSeconds = 180,
    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '..\TestResults\NativeGateway')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NativeGatewayProof.psm1') -Force
. (Join-Path $PSScriptRoot 'ProofEvidence.ps1')

if (-not $DisposableHost -or -not $IsWindows) { throw 'An explicitly authorized disposable Windows guest is required.' }
if ((Get-CimInstance Win32_OperatingSystem).ProductType -ne 1) { throw 'Native Gateway proof requires Windows client, not Windows Server.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Get-ProofSourceIdentity $repoRoot
if ($source.head -cne $SourceSha -or $source.dirtyFileCount -ne 0) {
    throw 'Native Gateway proof requires a clean checkout at the exact approved source SHA.'
}
foreach ($item in @(@($MsixPath, $MsixSha256), @($WxcExecPath, $WxcExecSha256))) {
    if (-not (Test-Path -LiteralPath $item[0] -PathType Leaf) -or
        (Get-FileHash -LiteralPath $item[0] -Algorithm SHA256).Hash.ToLowerInvariant() -cne $item[1]) {
        throw 'Native proof input is missing or its digest does not match.'
    }
}
if ([IO.Path]::GetExtension($MsixPath) -ine '.msix') { throw 'Supply an installable signed MSIX, not a submission archive or loose package.' }
$archive = [IO.Compression.ZipFile]::OpenRead($MsixPath)
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if (-not $entry) { throw 'MSIX manifest is missing.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($manifest.Package.Identity.Name -cne 'OpenClawFoundation.OpenClawGateway' -or
        $manifest.Package.Identity.Publisher -cne 'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC' -or
        $manifest.Package.Identity.Version -in @('0.0.0.0', '0.0.0.1')) {
        throw 'MSIX does not have the supported released Gateway identity.'
    }
} finally { $archive.Dispose() }
$existing = @(Get-AppxPackage -Name 'OpenClawFoundation.OpenClawGateway*')
if ($existing.Count) { throw 'Disposable proof requires no current-user Gateway registration. Existing packages are never removed.' }
Assert-NativeSessionProbe (Invoke-NativeProofProcess -FilePath $WxcExecPath -Arguments @('--probe') -TimeoutSeconds $CommandTimeoutSeconds)

$directory = Assert-ProofResultsDirectory -Path ([IO.Path]::GetFullPath($ResultsDirectory)) -RepoRoot $repoRoot
$invocation = Join-Path $directory ([guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $invocation
$receipt = [ordered]@{
    schemaVersion = 1
    scope = 'native-gateway-package-lifecycle'
    source = $source
    packagingSha = $PackagingSha
    msixSha256 = $MsixSha256
    wxcExecSha256 = $WxcExecSha256
    startedUtc = [datetime]::UtcNow.ToString('o')
    outcome = 'failed'
    operations = @()
    sessionCleanup = 'not-started'
    packageCleanup = 'not-started'
    doesNotProve = @('Companion authentication/pairing', 'system.run BaseContainer', 'WSL-MXC E2E', 'Azure lease cleanup')
}
$installed = $null
$installationAttempted = $false
try {
    # Windows validates signature/trust. This harness never installs a certificate.
    $installationAttempted = $true
    Add-AppxPackage -Path $MsixPath
    $packages = @(Get-AppxPackage -Name 'OpenClawFoundation.OpenClawGateway')
    if ($packages.Count -ne 1) { throw 'Expected exactly one installed Gateway package.' }
    $installed = $packages[0]
    if ($installed.Publisher -cne 'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC' -or
        $installed.Status -ne 'Ok' -or $installed.IsDevelopmentMode -or
        [string]$installed.Version -in @('0.0.0.0', '0.0.0.1')) {
        throw 'Gateway package identity, health or release registration is unsupported.'
    }
    $receipt.packageFullName = $installed.PackageFullName
    $alias = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps\$($installed.PackageFamilyName)\clawctl.exe"
    if (-not (Test-Path -LiteralPath $alias -PathType Leaf)) { throw 'Package-qualified clawctl alias is missing.' }
    $receipt.build = Get-NativeGatewayBuildIdentity `
        -Result (Invoke-NativeProofProcess -FilePath $alias -Arguments @('--version', '--json') -TimeoutSeconds $CommandTimeoutSeconds) `
        -PackagingSha $PackagingSha
    if ($receipt.build.packageVersion -cne [string]$installed.Version) {
        throw 'Registered package version does not match the installed CLI.'
    }
    $receipt.sessionCleanup = 'not-confirmed'
    $receipt.operations = @(Invoke-NativeGatewayLifecycle -InvokeCommand {
        param([string[]]$Arguments)
        Invoke-NativeProofProcess -FilePath $alias -Arguments $Arguments -TimeoutSeconds $CommandTimeoutSeconds
    })
    $receipt.sessionCleanup = 'passed'
    $current = Get-ProofSourceIdentity $repoRoot
    if (($source | ConvertTo-Json -Compress) -cne ($current | ConvertTo-Json -Compress)) {
        throw 'Source changed during native Gateway proof.'
    }
    $receipt.outcome = 'passed'
} finally {
    try {
        if ($installationAttempted) {
            # Include a partial registration when Add-AppxPackage failed after deployment.
            foreach ($package in @(Get-AppxPackage -Name 'OpenClawFoundation.OpenClawGateway')) {
                if ($package.Publisher -cne $manifest.Package.Identity.Publisher -or
                    [string]$package.Version -cne $manifest.Package.Identity.Version) {
                    throw 'Package identity changed before cleanup.'
                }
                Remove-AppxPackage -Package $package.PackageFullName
            }
            if (@(Get-AppxPackage -Name 'OpenClawFoundation.OpenClawGateway').Count) {
                throw 'Task-owned package remains registered.'
            }
        }
        $receipt.packageCleanup = 'passed'
    } catch {
        $receipt.outcome = 'failed'
        $receipt.packageCleanup = 'failed'
        throw 'Task-owned package cleanup failed.'
    } finally {
        $receipt.finishedUtc = [datetime]::UtcNow.ToString('o')
        $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $invocation 'native-gateway-proof.json') -Encoding utf8
    }
}
Write-Host "Native Gateway package lifecycle passed. Evidence: $invocation"
$global:LASTEXITCODE = 0
