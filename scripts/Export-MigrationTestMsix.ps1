<#
.SYNOPSIS
    Test-signs the production-identity Store package for developer migration testing.
.DESCRIPTION
    The unsigned Store submission package cannot be installed, and the Dev package
    is barred from migration by identity. This produces the only artifact that can
    exercise the Store-side migration UI and handoff: the production-identity
    package signed with a disposable certificate whose subject matches the
    production publisher.

    The package keeps the production identity on purpose, so it shares a package
    family name with the Microsoft Store release and is not side-by-side with it.
    The disposable private key is removed before the script returns.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateNotNullOrEmpty()][string]$VersionInfoPath,
    [string]$SignToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$PackageDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $PackageDirectory))
$OutputDirectory = [IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $OutputDirectory))
if ((Test-Path -LiteralPath $OutputDirectory) -and
    (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or
     @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0)) {
    throw "The migration test artifact output directory must be absent or empty: $OutputDirectory"
}

function Resolve-SignTool {
    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        return (Resolve-Path -LiteralPath $SignToolPath).Path
    }

    $command = Get-Command SignTool.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $windowsKits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    # Prefer the host architecture so an ARM64 runner does not sign through emulation,
    # and order by SDK version rather than lexicographically so 10.0.26100 beats 10.0.9xxxx.
    $preferred = switch ($env:PROCESSOR_ARCHITECTURE) {
        'ARM64' { @('arm64', 'x64') }
        default { @('x64') }
    }
    $candidate = Get-ChildItem -LiteralPath $windowsKits -Filter SignTool.exe -File -Recurse `
        -ErrorAction SilentlyContinue |
        Where-Object { $preferred -contains $_.Directory.Name } |
        Sort-Object `
            @{ Expression = { $preferred.IndexOf($_.Directory.Name) } }, `
            @{ Expression = {
                $parsed = [version]'0.0.0.0'
                if ([version]::TryParse($_.Directory.Parent.Name, [ref]$parsed)) { $parsed }
                else { [version]'0.0.0.0' }
            }; Descending = $true } |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw 'SignTool.exe was not found in PATH or the Windows 10 SDK.'
    }

    $candidate.FullName
}

$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.msix' -File -Recurse)
if ($packages.Count -ne 1) {
    throw "Expected one Store MSIX in '$PackageDirectory'; found $($packages.Count)."
}
$package = $packages[0]

[xml]$sourceManifest = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'src\OpenClaw.Tray.WinUI\Package.appxmanifest') -Raw
$expectedIdentity = [string]$sourceManifest.Package.Identity.Name
$expectedPublisher = [string]$sourceManifest.Package.Identity.Publisher
if ([string]::IsNullOrWhiteSpace($expectedIdentity) -or [string]::IsNullOrWhiteSpace($expectedPublisher)) {
    throw 'The source manifest does not declare a production identity.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    if ($null -ne $archive.GetEntry('AppxSignature.p7x')) {
        throw 'The Store package is already signed; refusing to re-sign a submission asset.'
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml]$manifest = $reader.ReadToEnd() }
    finally { $reader.Dispose() }

    # The artifact exists to exercise migration, so prove the build actually
    # compiled it in rather than shipping a silently disabled package.
    $assembly = $archive.GetEntry('OpenClaw.Tray.WinUI.dll')
    if ($null -eq $assembly) { throw 'The Store package is missing OpenClaw.Tray.WinUI.dll.' }
    $buffer = [IO.MemoryStream]::new()
    $assemblyStream = $assembly.Open()
    try { $assemblyStream.CopyTo($buffer) }
    finally { $assemblyStream.Dispose() }
    $assemblyText = [Text.Encoding]::UTF8.GetString($buffer.ToArray())
    $buffer.Dispose()
    if (-not $assemblyText.Contains('MigrationMinimumSourceVersion')) {
        throw 'The Store package was not built with production migration enabled.'
    }
}
finally { $archive.Dispose() }

$identity = $manifest.Package.Identity
if ([string]$identity.Name -ne $expectedIdentity -or [string]$identity.Publisher -ne $expectedPublisher) {
    throw 'The package does not carry the production Store identity required for migration.'
}
if ([string]$identity.ProcessorArchitecture -ne $Architecture) {
    throw "Expected a $Architecture package; found $($identity.ProcessorArchitecture)."
}

$versionInfo = $null
if ($VersionInfoPath) {
    . (Join-Path $PSScriptRoot 'MsixVersioning.ps1')
    $allocationCommit = (& git -C $repositoryRoot rev-parse HEAD) -join ''
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve source for the MSIX allocation.' }
    $versionInfo = Read-MsixVersionInfo `
        -Path ([IO.Path]::GetFullPath([IO.Path]::Combine($repositoryRoot, $VersionInfoPath))) `
        -SourceCommit $allocationCommit
}
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD) -join ''
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Unable to resolve the current source commit.'
}
$sourceTreeDirty = [bool](& git -C $repositoryRoot status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the current source tree.' }

$packageName = "OpenClaw-MigrationTest-$Architecture.msix"
$certificateName = 'OpenClaw-MigrationTest.cer'
$friendlyName = "OpenClaw Migration Test Signing $([guid]::NewGuid().ToString('N'))"
$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $expectedPublisher `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -KeyExportPolicy NonExportable `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddDays(30) `
    -FriendlyName $friendlyName `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
try {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $signedPackage = Join-Path $OutputDirectory $packageName
    Copy-Item -LiteralPath $package.FullName -Destination $signedPackage

    & (Resolve-SignTool) sign /fd SHA256 /sha1 $certificate.Thumbprint $signedPackage
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool.exe failed to sign the migration test package. Exit code: $LASTEXITCODE."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $signedPackage
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
        throw 'The migration test package was not signed by the disposable certificate.'
    }

    $certificatePath = Join-Path $OutputDirectory $certificateName
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT | Out-Null
    $packageHash = (Get-FileHash -LiteralPath $signedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $certificateHash = (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash.ToLowerInvariant()

    [ordered]@{
        repository = 'https://github.com/openclaw/openclaw-windows-node'
        sourceCommit = $sourceCommit.ToLowerInvariant()
        sourceTreeDirty = $sourceTreeDirty
        architecture = $Architecture
        archive = $packageName
        sha256 = $packageHash
        signed = $true
        signing = 'migration-test-only'
        identityName = [string]$identity.Name
        packageVersion = [string]$identity.Version
        publisher = [string]$identity.Publisher
        sideBySideWithStore = $false
        migrationEnabled = $true
        certificate = $certificateName
        certificateThumbprint = $certificate.Thumbprint
        certificateSha256 = $certificateHash
        certificateExpiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
        workflowRunId = $env:GITHUB_RUN_ID
        workflowRunAttempt = $env:GITHUB_RUN_ATTEMPT
        msixVersionAllocation = $versionInfo
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'msix-metadata.json') -Encoding utf8

    @"
OpenClaw migration test package ($Architecture)

FOR DEVELOPERS ONLY. "Dev" in this artifact name means "for developers". This is
NOT the Dev package identity, and it is NOT a Microsoft Store-signed release.
Install only from a workflow and source revision you trust. Pull request builds
can contain unreviewed code. No private key is included.

Read this before installing:

* This package carries the production Store identity, so it shares a package
  family name with the Microsoft Store release. It is NOT side-by-side. Uninstall
  the Store build of OpenClaw first, and expect to uninstall this package before
  taking a Store update later.
* Migration deliberately runs against your real data directories. Data path
  redirection is rejected by the Store migration guard, so this cannot be pointed
  at a scratch profile.
* With an Inno "OpenClaw Companion" installation present, this package will offer
  to adopt it and then uninstall it. Use a disposable machine or virtual machine.
* With no Inno installation present, it starts normally and shows nothing.

Source: $sourceCommit
Package version: $($identity.Version)
Package SHA-256: $packageHash
Certificate thumbprint: $($certificate.Thumbprint)
Certificate SHA-256: $certificateHash

1. Extract this download. Compare its package and certificate hashes with
   msix-metadata.json using Get-FileHash -Algorithm SHA256.
2. Install Microsoft.VCLibs.140.00.UWPDesktop 14.0.33728.0 or newer for
   $Architecture if absent:
   https://learn.microsoft.com/troubleshoot/developer/visualstudio/cpp/libraries/c-runtime-packages-desktop-bridge
3. From this directory, in elevated PowerShell, explicitly trust the public
   certificate (this changes machine trust):
   Import-Certificate -FilePath .\$certificateName -CertStoreLocation Cert:\LocalMachine\TrustedPeople
4. As the intended Windows user, install:
   Add-AppxPackage -Path .\$packageName
5. Launch OpenClaw from Start to exercise the Store-side migration experience.

When finished, remove the package and then remove this certificate from elevated
PowerShell only if no installed package still relies on it:
Remove-Item -LiteralPath 'Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)'

The unsigned Store submission artifact remains the Partner Center asset. This
test-signed package stays workflow-only and must never be published.
"@ | Set-Content -LiteralPath (Join-Path $OutputDirectory 'INSTALL.txt') -Encoding utf8
}
finally {
    # Never throw from finally: it would mask an in-flight failure. Report here, verify below.
    try {
        Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction Stop
    } catch {
        Write-Warning "Could not remove the disposable signing key: $($_.Exception.Message)"
    }

    # Also verified here, because a failure in the body terminates the script before the
    # check below. That is exactly when a key is most likely to have been left behind.
    # Both the probe and the report are guarded: under -ErrorActionPreference Stop, or a
    # caller's WarningPreference of Stop, either could throw and replace an in-flight failure.
    try {
        if (Test-Path -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)") {
            Write-Warning ("The disposable signing key $($certificate.Thumbprint) is still in " +
                           'Cert:\CurrentUser\My. Remove it before trusting this host again.')
        }
    } catch {
        Write-Host "Could not verify disposable signing key removal: $($_.Exception.Message)"
    }
}

if (Test-Path -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)") {
    throw ("The disposable signing key $($certificate.Thumbprint) is still in Cert:\CurrentUser\My. " +
           'Remove it before trusting this host again.')
}

Write-Host "Staged test-signed migration package: $OutputDirectory"
