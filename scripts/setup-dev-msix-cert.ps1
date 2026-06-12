<#
.SYNOPSIS
    Provisions a self-signed certificate and PFX for locally building and
    installing a signed OpenClaw Companion MSIX.

.DESCRIPTION
    Reads the Publisher subject from src\OpenClaw.Tray.WinUI\Package.appxmanifest,
    creates a code-signing certificate with that exact subject (if one doesn't
    already exist), adds the public cert to LocalMachine\TrustedPeople so AppX
    deployment will accept packages signed with it, and exports the cert +
    private key to %LOCALAPPDATA%\OpenClawTray\dev-msix.pfx.

    The OpenClaw.Tray.WinUI project auto-detects the PFX at that well-known
    path: when present, the MSIX build signs with it; when absent, the MSIX
    is unsigned. So after running this script once, a normal build/publish
    of the tray produces a signed .msix that installs with plain
    Add-AppxPackage -- no -AllowUnsigned, no env-var plumbing.

    The script is idempotent: re-running reuses the existing cert and just
    re-exports the PFX. Pass -Force to discard and recreate. Requires an
    elevated PowerShell because writing to LocalMachine\TrustedPeople
    requires admin rights.

.PARAMETER Force
    Delete any existing matching cert (both stores) and PFX, then create fresh.

.PARAMETER SkipTrust
    Create the cert and PFX but skip the LocalMachine\TrustedPeople step.
    Useful when running non-elevated to inspect what will be created. A
    package signed with this cert will not be installable until the public
    cert is separately imported into LocalMachine\TrustedPeople.

.EXAMPLE
    # In an elevated PowerShell:
    .\scripts\setup-dev-msix-cert.ps1

.EXAMPLE
    .\scripts\setup-dev-msix-cert.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$SkipTrust
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$manifestPath = Join-Path $repoRoot 'src\OpenClaw.Tray.WinUI\Package.appxmanifest'
$pfxDir = Join-Path $env:LOCALAPPDATA 'OpenClawTray'
$pfxPath = Join-Path $pfxDir 'dev-msix.pfx'
# Password is also hardcoded in OpenClaw.Tray.WinUI.csproj's
# PackageCertificatePassword. If you change one, change both.
$pfxPassword = 'openclaw-dev'

function Write-Step([string]$Message) {
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

if (-not (Test-Path $manifestPath)) {
    throw "Manifest not found at $manifestPath"
}

# Pull Publisher from the manifest so this stays in sync if Publisher ever changes.
[xml]$manifest = Get-Content -LiteralPath $manifestPath
$ns = New-Object System.Xml.XmlNamespaceManager $manifest.NameTable
$ns.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$identity = $manifest.SelectSingleNode('/p:Package/p:Identity', $ns)
if (-not $identity) { throw "Could not find <Identity> in $manifestPath" }
$subject = $identity.Publisher
if ([string]::IsNullOrWhiteSpace($subject)) { throw "Identity/@Publisher is empty in $manifestPath" }

Write-Step 'Manifest Publisher'
Write-Host $subject

# Admin check is only needed when we're going to write to LocalMachine.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $SkipTrust) {
    throw "This script must run as Administrator to add the certificate to LocalMachine\TrustedPeople. " +
          "Re-run from an elevated PowerShell, or pass -SkipTrust to only create the cert in CurrentUser\My."
}

# Match on Subject + Code Signing EKU (1.3.6.1.5.5.7.3.3).
$codeSigningOid = '1.3.6.1.5.5.7.3.3'
$existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq $subject -and
    ($_.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId }) -contains $codeSigningOid
}

if ($existing -and $Force) {
    Write-Step 'Removing existing certificate(s) (-Force)'
    foreach ($c in $existing) {
        Write-Host "Removing CurrentUser\My\$($c.Thumbprint)"
        Remove-Item "Cert:\CurrentUser\My\$($c.Thumbprint)" -Force
        $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $c.Thumbprint }
        if ($trusted) {
            Write-Host "Removing LocalMachine\TrustedPeople\$($c.Thumbprint)"
            Remove-Item "Cert:\LocalMachine\TrustedPeople\$($c.Thumbprint)" -Force
        }
    }
    if (Test-Path $pfxPath) {
        Write-Host "Removing $pfxPath"
        Remove-Item $pfxPath -Force
    }
    $existing = $null
}

if ($existing) {
    $cert = $existing | Sort-Object NotAfter -Descending | Select-Object -First 1
    Write-Step 'Reusing existing certificate'
} else {
    Write-Step 'Creating new self-signed certificate'
    # Explicit TextExtension entries (Code Signing EKU + empty Basic Constraints)
    # guard against quirks in older Windows PowerShell where -Type CodeSigningCert
    # alone has occasionally produced certs AppX deployment rejects.
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(3) `
        -FriendlyName 'OpenClaw Dev MSIX Signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

Write-Host "Thumbprint: $($cert.Thumbprint)"
Write-Host "NotAfter:   $($cert.NotAfter)"

if (-not $SkipTrust) {
    $alreadyTrusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
    if ($alreadyTrusted) {
        Write-Step 'LocalMachine\TrustedPeople already contains this cert'
    } else {
        Write-Step 'Trusting certificate in LocalMachine\TrustedPeople'
        # Public-only (.cer) import. The private key lives in CurrentUser\My
        # (and the exported PFX); TrustedPeople only needs the public cert
        # for AppX to validate package signatures.
        $tempCer = Join-Path $env:TEMP "openclaw-dev-msix-$($cert.Thumbprint).cer"
        try {
            Export-Certificate -Cert $cert -FilePath $tempCer | Out-Null
            Import-Certificate -FilePath $tempCer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        } finally {
            if (Test-Path $tempCer) { Remove-Item $tempCer -Force }
        }
    }
}

Write-Step "Exporting PFX to $pfxPath"
if (-not (Test-Path $pfxDir)) {
    New-Item -ItemType Directory -Path $pfxDir -Force | Out-Null
}
$securePwd = ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText
# Always re-export so the PFX matches the currently-active cert. Export-PfxCertificate
# overwrites without prompting.
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd -Force | Out-Null
Write-Host "PFX written. The OpenClaw.Tray.WinUI MSBuild project auto-detects this file."

Write-Step 'Next steps'
Write-Host @"
Build the MSIX (PackageMsix is already true in the project):

    dotnet publish src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -c Debug -r win-x64 --nologo

Install:

    Add-AppxPackage -Path .\src\OpenClaw.Tray.WinUI\AppPackages\<...>\<...>.msix

Disable dev signing (revert to unsigned output) without removing the cert:

    Remove-Item '$pfxPath'

Fully clean up dev signing artifacts:

    .\scripts\setup-dev-msix-cert.ps1 -Force   # then delete the PFX, or:
    Remove-Item '$pfxPath' -Force
    Get-ChildItem Cert:\CurrentUser\My\$($cert.Thumbprint) | Remove-Item
    Get-ChildItem Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint) | Remove-Item
"@

