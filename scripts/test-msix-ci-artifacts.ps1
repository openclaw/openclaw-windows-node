<#
.SYNOPSIS
    Exercises Dev CI artifact validation and build argument contracts.
.DESCRIPTION
    Uses synthetic ZIPs and an in-memory signer. Authenticode is stubbed here;
    the packaging job separately verifies the actual Windows signature. No
    certificate is installed or trusted and no product build is launched.
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exporter = Join-Path $RepoRoot 'scripts\Export-DevMsixArtifact.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-msix-ci-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$rsa = [Security.Cryptography.RSA]::Create(2048)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    'CN=OpenClaw Local Development', $rsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(1))
$signatureStatus = 'Valid'
$scenarioNumber = 0

function Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath)) { throw 'Signature probe received a missing package.' }
    [pscustomobject]@{ Status = $signatureStatus; SignerCertificate = $certificate }
}

function Assert-Fails {
    param([scriptblock]$Action, [string]$Expected)
    try {
        & $Action
        throw 'The operation unexpectedly succeeded.'
    }
    catch {
        if (-not $_.Exception.Message.Contains($Expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Expected failure containing '$Expected', received: $($_.Exception.Message)"
        }
    }
}

function New-Package {
    param(
        [string]$Directory,
        [string]$Name = 'Dev.msix',
        [string]$Identity = 'OpenClawFoundation.OpenClaw.Dev',
        [string]$Publisher = 'CN=OpenClaw Local Development',
        [string]$Architecture = 'x64',
        [string]$Version = '2026.7.2.123',
        [string]$Omit = ''
    )
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $Directory $Name), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('AppxManifest.xml', 'AppxSignature.p7x', 'OpenClaw.Tray.WinUI.exe', 'OpenClaw.Tray.WinUI.dll', 'coreclr.dll')) {
            if ($name -eq $Omit) { continue }
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try {
                $content = if ($name -eq 'AppxManifest.xml') {
                    "<Package><Identity Name=`"$Identity`" Publisher=`"$Publisher`" ProcessorArchitecture=`"$Architecture`" Version=`"$Version`" /></Package>"
                } else { 'Synthetic contract-test content, not executable.' }
                $writer.Write($content)
            }
            finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function New-Arguments {
    $script:scenarioNumber++
    @{
        Architecture = 'x64'
        PackageDirectory = Join-Path $temporaryRoot "input-$scenarioNumber"
        ExpectedRevision = 123
        ExpectedVersion = '2026.7.2-alpha.4'
        CertificateThumbprint = $certificate.Thumbprint
        OutputDirectory = Join-Path $temporaryRoot "output-$scenarioNumber"
    }
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $arguments = New-Arguments
    New-Package -Directory $arguments.PackageDirectory
    # Unrelated build output must not be uploaded, even if it contains a key.
    Set-Content (Join-Path $arguments.PackageDirectory 'do-not-publish.pfx') 'not a real key'
    & $exporter @arguments
    $files = @(Get-ChildItem -LiteralPath $arguments.OutputDirectory -File | Sort-Object Name | Select-Object -ExpandProperty Name)
    if (($files -join ',') -ne 'INSTALL.txt,msix-metadata.json,OpenClaw-Dev.cer,OpenClawCompanion-Dev-x64.msix') {
        throw "Unexpected Dev artifact contents: $($files -join ',')"
    }
    $metadata = Get-Content (Join-Path $arguments.OutputDirectory 'msix-metadata.json') -Raw | ConvertFrom-Json
    $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Join-Path $arguments.OutputDirectory 'OpenClaw-Dev.cer'))
    try {
        if ($publicCertificate.HasPrivateKey -or $publicCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw 'The exported certificate is not the expected public-only signer.'
        }
    }
    finally { $publicCertificate.Dispose() }
    $actualHash = (Get-FileHash (Join-Path $arguments.OutputDirectory $metadata.archive) -Algorithm SHA256).Hash
    if (-not $metadata.signed -or $metadata.signing -ne 'development-only' -or
        $metadata.packageVersion -ne '2026.7.2.123' -or $metadata.sha256 -ne $actualHash -or
        $metadata.certificateThumbprint -ne $certificate.Thumbprint -or $metadata.sourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Dev package provenance did not match its inputs.'
    }
    Assert-Fails { & $exporter @arguments } 'must be absent or empty'

    $arguments = New-Arguments
    New-Item -ItemType Directory -Path $arguments.PackageDirectory | Out-Null
    Assert-Fails { & $exporter @arguments } 'found 0'
    New-Package -Directory $arguments.PackageDirectory
    New-Package -Directory $arguments.PackageDirectory -Name Other.msix
    Assert-Fails { & $exporter @arguments } 'found 2'

    foreach ($status in @('NotSigned', 'HashMismatch', 'NotTrusted')) {
        $signatureStatus = $status
        $arguments = New-Arguments
        New-Package -Directory $arguments.PackageDirectory
        Assert-Fails { & $exporter @arguments } 'signature is not trusted and valid'
        if (Test-Path $arguments.OutputDirectory) { throw 'Failed verification published output.' }
    }
    $signatureStatus = 'Valid'
    $arguments = New-Arguments
    New-Package -Directory $arguments.PackageDirectory
    $arguments.CertificateThumbprint = '0' * 40
    Assert-Fails { & $exporter @arguments } 'signer does not match'

    foreach ($mismatch in @(
        @{ Identity = 'OpenClawFoundation.OpenClaw'; Error = 'side-by-side Dev identity' },
        @{ Publisher = 'CN=Wrong'; Error = 'side-by-side Dev identity' },
        @{ Architecture = 'arm64'; Error = 'Expected Dev package' },
        @{ Version = '2026.7.2.122'; Error = 'Expected Dev package' },
        @{ Version = '2026.7.1.123'; Error = 'Expected Dev package' },
        @{ Omit = 'AppxSignature.p7x'; Error = 'missing AppxSignature.p7x' },
        @{ Omit = 'coreclr.dll'; Error = 'missing coreclr.dll' }
    )) {
        $arguments = New-Arguments
        $packageArguments = @{ Directory = $arguments.PackageDirectory }
        foreach ($key in $mismatch.Keys) { if ($key -ne 'Error') { $packageArguments[$key] = $mismatch[$key] } }
        New-Package @packageArguments
        Assert-Fails { & $exporter @arguments } $mismatch.Error
    }

    # Exercise the real parameter binder without executing build.ps1's body.
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'build.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'build.ps1 has syntax errors.' }
    $attributes = ($ast.ParamBlock.Attributes | ForEach-Object { $_.Extent.Text }) -join "`n"
    $bind = [scriptblock]::Create($attributes + "`n" + $ast.ParamBlock.Extent.Text + "`n`$MsixRevision")
    foreach ($revision in @(1, 65535)) {
        if ((& $bind -Msix Dev -MsixRevision $revision) -ne $revision) { throw 'A valid CI revision was rejected.' }
    }
    foreach ($revision in @(0, -1, 65536)) {
        Assert-Fails { & $bind -Msix Dev -MsixRevision $revision } 'cannot validate argument'
        $arguments.ExpectedRevision = $revision
        Assert-Fails { & $exporter @arguments } 'cannot validate argument'
    }
    Assert-Fails { & $bind -PackageMsix } 'parameter cannot be found'
    Write-Host 'MSIX CI artifact contracts passed: version bounds, identity, architecture, signature rejection, exact package selection, provenance, and public-only exports.'
}
finally {
    $certificate.Dispose()
    $rsa.Dispose()
    [IO.Directory]::Delete($temporaryRoot, $true)
}
