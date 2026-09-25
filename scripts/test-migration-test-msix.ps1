<#
.SYNOPSIS
    Exercises developer migration test package validation contracts.
.DESCRIPTION
    Uses synthetic ZIPs, a stubbed Authenticode probe, and a fake SignTool. The
    happy path creates a short-lived current-user certificate that the exporter
    removes itself. No certificate is trusted and no product build is launched.
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$exporter = Join-Path $RepoRoot 'scripts\Export-MigrationTestMsix.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-migration-msix-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$scenarioNumber = 0

[xml]$sourceManifest = Get-Content -LiteralPath (
    Join-Path $RepoRoot 'src\OpenClaw.Tray.WinUI\Package.appxmanifest') -Raw
$productionIdentity = [string]$sourceManifest.Package.Identity.Name
$productionPublisher = [string]$sourceManifest.Package.Identity.Publisher

$fakeSignTool = Join-Path $temporaryRoot 'SignTool.ps1'
$signToolLog = Join-Path $temporaryRoot 'signtool-args.txt'
@"
param([Parameter(ValueFromRemainingArguments = `$true)][string[]]`$Arguments)
`$Arguments -join ' ' | Set-Content -LiteralPath '$signToolLog'
if (`$Arguments[0] -ne 'sign') { exit 2 }
if (`$Arguments -notcontains '/fd') { exit 4 }
if (`$Arguments[[array]::IndexOf(`$Arguments, '/fd') + 1] -ne 'SHA256') { exit 5 }
if (`$Arguments -notcontains '/sha1') { exit 6 }
`$thumbprint = `$Arguments[[array]::IndexOf(`$Arguments, '/sha1') + 1]
if (`$thumbprint -notmatch '^[0-9A-Fa-f]{40}`$') { exit 7 }
if (-not (Test-Path -LiteralPath `$Arguments[-1])) { exit 3 }
exit 0
"@ | Set-Content -LiteralPath $fakeSignTool

function Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath)) { throw 'Signature probe received a missing package.' }
    $signer = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.FriendlyName -like 'OpenClaw Migration Test Signing*' } |
        Sort-Object NotBefore -Descending |
        Select-Object -First 1
    [pscustomobject]@{ Status = 'Valid'; SignerCertificate = $signer }
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
        [string]$Name = 'OpenClaw-x64.msix',
        [string]$Identity = $productionIdentity,
        [string]$Publisher = $productionPublisher,
        [string]$Architecture = 'x64',
        [string]$Version = '2026.9.5.0',
        [switch]$Signed,
        [switch]$WithoutMigration,
        [string]$Omit = ''
    )
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $Directory $Name), [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @('AppxManifest.xml', 'OpenClaw.Tray.WinUI.exe', 'OpenClaw.Tray.WinUI.dll')
        if ($Signed) { $entries += 'AppxSignature.p7x' }
        foreach ($name in $entries) {
            if ($name -eq $Omit) { continue }
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try {
                $content = switch ($name) {
                    'AppxManifest.xml' {
                        "<Package><Identity Name=`"$Identity`" Publisher=`"$Publisher`" ProcessorArchitecture=`"$Architecture`" Version=`"$Version`" /></Package>"
                    }
                    'OpenClaw.Tray.WinUI.dll' {
                        if ($WithoutMigration) { 'Synthetic assembly without migration metadata.' }
                        else { 'Synthetic assembly. MigrationMinimumSourceVersion 2026.9.5.0' }
                    }
                    default { 'Synthetic contract-test content, not executable.' }
                }
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
        OutputDirectory = Join-Path $temporaryRoot "output-$scenarioNumber"
        SignToolPath = $fakeSignTool
    }
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # The submission asset must never be re-signed and republished.
    $arguments = New-Arguments
    New-Package -Directory $arguments.PackageDirectory -Signed
    Assert-Fails { & $exporter @arguments } 'already signed'

    # A package built with migration disabled would install but prove nothing.
    $arguments = New-Arguments
    New-Package -Directory $arguments.PackageDirectory -WithoutMigration
    Assert-Fails { & $exporter @arguments } 'not built with production migration enabled'

    foreach ($mismatch in @(
        @{ Identity = 'OpenClawFoundation.OpenClaw.Dev'; Error = 'production Store identity' },
        @{ Publisher = 'CN=Wrong'; Error = 'production Store identity' },
        @{ Architecture = 'arm64'; Error = 'Expected a x64 package' }
    )) {
        $arguments = New-Arguments
        $package = @{ Directory = $arguments.PackageDirectory }
        foreach ($key in @('Identity', 'Publisher', 'Architecture')) {
            if ($mismatch.ContainsKey($key)) { $package[$key] = $mismatch[$key] }
        }
        New-Package @package
        Assert-Fails { & $exporter @arguments } $mismatch.Error
        if (Test-Path $arguments.OutputDirectory) { throw 'Failed verification published output.' }
    }

    $arguments = New-Arguments
    New-Package -Directory $arguments.PackageDirectory
    New-Package -Directory $arguments.PackageDirectory -Name 'extra.msix'
    Assert-Fails { & $exporter @arguments } 'found 2'

    $arguments = New-Arguments
    New-Package -Directory $arguments.PackageDirectory
    New-Item -ItemType Directory -Path $arguments.OutputDirectory -Force | Out-Null
    Set-Content (Join-Path $arguments.OutputDirectory 'stale.txt') 'stale'
    Assert-Fails { & $exporter @arguments } 'must be absent or empty'

    $arguments = New-Arguments
    New-Package -Directory $arguments.PackageDirectory
    & $exporter @arguments
    $packageName = 'OpenClaw-MigrationTest-x64.msix'
    $expectedFiles = @('INSTALL.txt', 'msix-metadata.json', 'OpenClaw-MigrationTest.cer', $packageName)
    $files = @(Get-ChildItem -LiteralPath $arguments.OutputDirectory -File | Select-Object -ExpandProperty Name)
    if (@(Compare-Object $expectedFiles $files).Count -gt 0) {
        throw "Unexpected migration test artifact contents: $($files -join ',')"
    }
    $metadata = Get-Content (Join-Path $arguments.OutputDirectory 'msix-metadata.json') -Raw | ConvertFrom-Json
    $actualHash = (Get-FileHash (Join-Path $arguments.OutputDirectory $packageName) -Algorithm SHA256).Hash
    if (-not $metadata.signed -or $metadata.signing -ne 'migration-test-only' -or
        $metadata.identityName -ne $productionIdentity -or $metadata.publisher -ne $productionPublisher -or
        $metadata.sideBySideWithStore -or -not $metadata.migrationEnabled -or
        $metadata.sha256 -ne $actualHash -or $metadata.sourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Migration test package provenance did not match its inputs.'
    }
    $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Join-Path $arguments.OutputDirectory 'OpenClaw-MigrationTest.cer'))
    try {
        $signToolArguments = Get-Content -LiteralPath $signToolLog -Raw
        $expectedSignToolArguments =
            "sign /fd SHA256 /sha1 $($publicCertificate.Thumbprint) " +
            (Join-Path $arguments.OutputDirectory $packageName)
        if ($signToolArguments.Trim() -ne $expectedSignToolArguments) {
            throw "SignTool received unexpected arguments: $signToolArguments"
        }
        if ($publicCertificate.HasPrivateKey -or
            $publicCertificate.Thumbprint -ne $metadata.certificateThumbprint) {
            throw 'The exported certificate is not the expected public-only signer.'
        }
        if (Get-Item "Cert:\CurrentUser\My\$($publicCertificate.Thumbprint)" -ErrorAction SilentlyContinue) {
            throw 'The disposable signing key was left in the current user store.'
        }
    }
    finally { $publicCertificate.Dispose() }

    $instructions = Get-Content (Join-Path $arguments.OutputDirectory 'INSTALL.txt') -Raw
    foreach ($warning in @(
        "Add-AppxPackage -Path .\$packageName",
        'NOT the Dev package identity',
        'It is NOT side-by-side',
        'real data directories',
        'uninstall it')) {
        if (-not $instructions.Contains($warning)) {
            throw "Migration test instructions omitted '$warning'."
        }
    }

    Write-Host 'Migration test MSIX artifact contracts passed.'
}
finally {
    Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -like 'OpenClaw Migration Test Signing*' } |
        ForEach-Object { Remove-Item "Cert:\CurrentUser\My\$($_.Thumbprint)" -Force -ErrorAction SilentlyContinue }
    if ([IO.Directory]::Exists($temporaryRoot)) {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}
$global:LASTEXITCODE = 0
