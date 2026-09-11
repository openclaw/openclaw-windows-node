<#
.SYNOPSIS
    Validates a signed Dev MSIX and stages a public-only CI tester download.
.DESCRIPTION
    Requires exactly one package from the current build. Verifies its Dev
    identity, architecture, version, and trusted signature before exporting
    only the public certificate, package, provenance, and install instructions.
    The output directory must be absent or empty and is never deleted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$ExpectedRevision,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$PackageDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $PackageDirectory))
$OutputDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $OutputDirectory))
if ((Test-Path -LiteralPath $OutputDirectory) -and
    (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or
     @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0)) {
    throw "The Dev artifact output directory must be absent or empty: $OutputDirectory"
}

$baseVersion = $ExpectedVersion -replace '[-+].*$', ''
if ($baseVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Expected a three-part base version: $ExpectedVersion"
}
$expectedPackageVersion = "$baseVersion.$ExpectedRevision"
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.msix' -File -Recurse)
if ($packages.Count -ne 1) {
    throw "Expected one Dev MSIX in '$PackageDirectory'; found $($packages.Count)."
}
$package = $packages[0]

[xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj') -Raw
$expectedIdentity = $project.SelectSingleNode('/Project/Target/GenerateOpenClawAppxManifest').IdentityName
$expectedPublisher = $project.SelectSingleNode('/Project/PropertyGroup/OpenClawDevMsixPublisher').InnerText
$signature = Get-AuthenticodeSignature -LiteralPath $package.FullName
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
    throw "The Dev package signature is not trusted and valid: $($signature.Status)"
}
$certificate = $signature.SignerCertificate
if ($certificate.Thumbprint -ne $CertificateThumbprint -or $certificate.Subject -ne $expectedPublisher) {
    throw 'The Dev package signer does not match the provisioned development certificate.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    foreach ($entry in @('AppxManifest.xml', 'AppxSignature.p7x', 'OpenClaw.Tray.WinUI.exe', 'OpenClaw.Tray.WinUI.dll', 'coreclr.dll')) {
        if ($null -eq $archive.GetEntry($entry)) { throw "The Dev package is missing $entry." }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml]$manifest = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}
finally { $archive.Dispose() }

$identity = $manifest.Package.Identity
if ($identity.Name -ne $expectedIdentity -or $identity.Publisher -ne $expectedPublisher) {
    throw 'The package does not have the expected side-by-side Dev identity.'
}
if ($identity.ProcessorArchitecture -ne $Architecture -or $identity.Version -ne $expectedPackageVersion) {
    throw "Expected Dev package $expectedPackageVersion for $Architecture; found $($identity.Version) for $($identity.ProcessorArchitecture)."
}
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD) -join ''
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Unable to resolve the current source commit.'
}
$sourceTreeDirty = [bool](& git -C $repositoryRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the current source tree.' }

$packageName = "OpenClawCompanion-Dev-$Architecture.msix"
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $OutputDirectory $packageName)
$certificatePath = Join-Path $OutputDirectory 'OpenClaw-Dev.cer'
Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT | Out-Null
$packageHash = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory $packageName) -Algorithm SHA256).Hash.ToLowerInvariant()
$certificateHash = (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{
    repository = 'https://github.com/openclaw/openclaw-windows-node'
    sourceCommit = $sourceCommit.ToLowerInvariant()
    sourceTreeDirty = $sourceTreeDirty
    architecture = $Architecture
    archive = $packageName
    sha256 = $packageHash
    signed = $true
    signing = 'development-only'
    identityName = [string]$identity.Name
    packageVersion = [string]$identity.Version
    publisher = [string]$identity.Publisher
    certificate = 'OpenClaw-Dev.cer'
    certificateThumbprint = $certificate.Thumbprint
    certificateSha256 = $certificateHash
    certificateExpiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
    workflowRunId = $env:GITHUB_RUN_ID
    workflowRunAttempt = $env:GITHUB_RUN_ATTEMPT
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'msix-metadata.json') -Encoding utf8

@"
OpenClaw (Dev) CI tester package ($Architecture)

This is NOT a Microsoft Store-signed release. Install only from a workflow
and source revision you trust. Pull request builds can contain unreviewed code.
No private key is included. The public certificate is unique to this runner;
later runs and the other architecture may have different certificates.

Source: $sourceCommit
Package version: $($identity.Version)
Package SHA-256: $packageHash
Certificate thumbprint: $($certificate.Thumbprint)
Certificate SHA-256: $certificateHash

1. Extract this download. Compare its package and certificate hashes with
   msix-metadata.json using Get-FileHash -Algorithm SHA256.
2. Install Microsoft.VCLibs.140.00.UWPDesktop 14.0.33728.0 or newer for
   $Architecture if absent. Obtain it from Microsoft's documented VC++ runtime
   packages for Desktop Bridge apps, not an untrusted mirror:
   https://learn.microsoft.com/troubleshoot/developer/visualstudio/cpp/libraries/c-runtime-packages-desktop-bridge
   Check installed versions with:
   Get-AppxPackage Microsoft.VCLibs.140.00.UWPDesktop | Select-Object Version, Architecture
3. From this directory, in elevated PowerShell, explicitly trust the public
   development certificate (this changes machine trust):
   Import-Certificate -FilePath .\OpenClaw-Dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
4. As the intended Windows user, close the existing Dev app and install:
   Add-AppxPackage -Path .\$packageName
5. Launch OpenClaw (Dev) from Start. This uses the existing Dev identity, so an
   installed Dev package may be upgraded and its settings retained. It is not
   another isolated Dev installation.

CI uses the workflow run number as the Dev revision, bounded to 1-65535.
Rerunning the same workflow run keeps the same package version and is not a
new upgrade. Versions from other branches, forks, or local builds may be newer.
Do not uninstall or downgrade an existing Dev installation merely to bypass a
version error without first considering its retained settings and data.

When finished, remove this certificate from elevated PowerShell only if no
installed package still relies on it:
Remove-Item -LiteralPath 'Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)'

Store submission artifacts are separate unsigned CI downloads, not installers.
No MSIX package is published to GitHub Releases by this workflow.
"@ | Set-Content -LiteralPath (Join-Path $OutputDirectory 'INSTALL.txt') -Encoding utf8
Write-Host "Staged signed Dev tester artifact: $OutputDirectory"
