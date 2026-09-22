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
        foreach ($name in @(
            'AppxManifest.xml', 'AppxSignature.p7x', 'OpenClaw.Tray.WinUI.exe', 'OpenClaw.Tray.WinUI.dll',
            'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'Microsoft.ui.xaml.dll',
            'OpenClaw.SetupEngine.dll', 'OpenClaw.SetupEngine.UI.dll', "tools/mxc/$Architecture/wxc-exec.exe"
        )) {
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

function New-VersionInfo {
    param([string]$Path, [string]$SourceVersion, [string]$BaseVersion, [int]$Counter)
    $sourceCommit = (& git -C $RepoRoot rev-parse HEAD) -join ''
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve fixture source commit.' }
    $parts = $BaseVersion.Split('.')
    [ordered]@{
        schemaVersion = 1
        sourceVersion = $SourceVersion
        sourceCommit = $sourceCommit
        sourceRef = 'refs/pull/1/merge'
        repository = 'openclaw/openclaw-windows-node'
        baseVersion = $BaseVersion
        packageBaseVersion = "$($parts[0]).$($parts[1]).$Counter"
        storePackageVersion = "$($parts[0]).$($parts[1]).$Counter.0"
        packagingRevision = $Counter - [int]$parts[2] * 100
        allocation = 'preview'
        reservationRef = $null
    } | ConvertTo-Json | Set-Content -LiteralPath $Path
}

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($architecture in @('x64', 'arm64')) {
        $arguments = New-Arguments
        $arguments.Architecture = $architecture
        New-Package -Directory $arguments.PackageDirectory -Architecture $architecture
        # Unrelated build output must not be uploaded, even if it contains a key.
        Set-Content (Join-Path $arguments.PackageDirectory 'do-not-publish.pfx') 'not a real key'
        & $exporter @arguments
        $packageName = "OpenClaw-Dev-$architecture.msix"
        $expectedFiles = @('INSTALL.txt', 'msix-metadata.json', 'OpenClaw-Dev.cer', $packageName)
        $files = @(Get-ChildItem -LiteralPath $arguments.OutputDirectory -File | Select-Object -ExpandProperty Name)
        if (@(Compare-Object $expectedFiles $files).Count -gt 0) {
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
            $metadata.archive -ne $packageName -or $metadata.architecture -ne $architecture -or
            $metadata.identityName -ne 'OpenClawFoundation.OpenClaw.Dev' -or
            $metadata.packageVersion -ne '2026.7.2.123' -or $metadata.sha256 -ne $actualHash -or
            $metadata.certificateThumbprint -ne $certificate.Thumbprint -or $metadata.sourceCommit -notmatch '^[0-9a-f]{40}$') {
            throw 'Dev package provenance did not match its inputs.'
        }
        $instructions = Get-Content (Join-Path $arguments.OutputDirectory 'INSTALL.txt') -Raw
        if (-not $instructions.Contains("Add-AppxPackage -Path .\$packageName")) {
            throw 'Dev installation instructions did not name the exported package.'
        }
        Assert-Fails { & $exporter @arguments } 'must be absent or empty'

        $arguments = New-Arguments
        $arguments.Architecture = $architecture
        $arguments.ExpectedVersion = '2026.9.411'
        $arguments.VersionInfoPath = Join-Path $temporaryRoot "dev-allocation-$architecture.json"
        New-VersionInfo $arguments.VersionInfoPath '2026.9.4-1' '2026.9.4' 411
        New-Package -Directory $arguments.PackageDirectory -Architecture $architecture -Version '2026.9.411.123'
        & $exporter @arguments
        $metadata = Get-Content (Join-Path $arguments.OutputDirectory 'msix-metadata.json') -Raw | ConvertFrom-Json
        if ($metadata.packageVersion -ne '2026.9.411.123' -or
            $metadata.msixVersionAllocation.sourceVersion -ne '2026.9.4-1' -or
            $metadata.msixVersionAllocation.allocation -ne 'preview') {
            throw 'Dev artifact lost its shared packaging allocation.'
        }
        if (-not (Get-Content (Join-Path $arguments.OutputDirectory 'INSTALL.txt') -Raw).Contains('MSIX version allocation: preview')) {
            throw 'Dev instructions must identify unreserved preview versions.'
        }
    }

    $arguments = New-Arguments
    $arguments.VersionInfoPath = Join-Path $temporaryRoot 'mismatched-dev-allocation.json'
    New-VersionInfo $arguments.VersionInfoPath '2026.9.4' '2026.9.4' 411
    Assert-Fails { & $exporter @arguments } 'expected Dev base does not match'

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

    # Exercise the real Store builder/validator, replacing only the native publish.
    & {
        $storeBuilder = Join-Path $RepoRoot 'scripts\Build-StoreMsix.ps1'
        [xml]$sourceManifest = Get-Content (Join-Path $RepoRoot 'src\OpenClaw.Tray.WinUI\Package.appxmanifest') -Raw
        $probe = @{ Arguments = @(); Calls = 0; ProducedVersion = $null }
        function dotnet {
            $probe.Arguments = @($args)
            $probe.Calls++
            $output = ($args | Where-Object { $_ -like '-p:AppxPackageDir=*' }) -replace '^-p:AppxPackageDir=', ''
            $architecture = if ($args -contains 'win-arm64') { 'arm64' } else { 'x64' }
            $base = @($args | Where-Object { $_ -like '-p:MsixPackageBaseVersion=*' })
            $version = if ($probe.ProducedVersion) { $probe.ProducedVersion }
                elseif ($base.Count) { ($base[0] -replace '^-p:MsixPackageBaseVersion=', '') + '.0' }
                else { '2026.9.5.0' }
            New-Package -Directory $output -Name 'Store.msix' -Architecture $architecture -Version $version `
                -Identity $sourceManifest.Package.Identity.Name -Publisher $sourceManifest.Package.Identity.Publisher `
                -Omit 'AppxSignature.p7x'
            $global:LASTEXITCODE = 0
        }
        foreach ($architecture in @('x64', 'arm64')) {
            foreach ($counter in @(0, 401, 411)) {
                $arguments = @{ Architecture = $architecture; OutputDirectory = (Join-Path $temporaryRoot "store-$($probe.Calls)") }
                $expected = '2026.9.5.0'
                if ($counter) {
                    $arguments.VersionInfoPath = Join-Path $temporaryRoot "store-allocation-$counter.json"
                    New-VersionInfo $arguments.VersionInfoPath '2026.9.4-alpha.3' '2026.9.4' $counter
                    $expected = "2026.9.$counter.0"
                }
                & $storeBuilder @arguments
                $metadata = Get-Content (Join-Path $arguments.OutputDirectory 'msix-metadata.json') -Raw | ConvertFrom-Json
                if ($metadata.packageVersion -ne $expected -or $metadata.signed -or
                    $metadata.archive -ne "OpenClaw-$architecture.msix") {
                    throw 'Store metadata did not describe the actual allocated package.'
                }
                if ($counter -and ($probe.Arguments -notcontains "-p:MsixPackageBaseVersion=2026.9.$counter" -or
                    $metadata.msixVersionAllocation.storePackageVersion -ne $expected)) {
                    throw 'Store build/export did not use the selected manifest allocation.'
                }
                if (-not $counter -and $null -ne $metadata.msixVersionAllocation) {
                    throw 'Local unallocated builds must not claim a CI reservation.'
                }
                if (@($probe.Arguments | Where-Object {
                    $_ -match '^-p:(Version|UpdateVersionProperties|UpdateAssemblyInfo|AssemblyVersion|FileVersion|InformationalVersion)='
                }).Count) {
                    throw 'MSIX allocation must not change the app GitVersion or assembly metadata.'
                }
            }
        }
        $probe.ProducedVersion = '2026.9.5.0'
        $output = Join-Path $temporaryRoot 'store-version-mismatch'
        Assert-Fails {
            & $storeBuilder -Architecture x64 -OutputDirectory $output -VersionInfoPath $arguments.VersionInfoPath
        } 'does not match the allocated version'
        if (Test-Path (Join-Path $output 'msix-metadata.json')) { throw 'Rejected package received validated metadata.' }
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
    $bindBase = [scriptblock]::Create($attributes + "`n" + $ast.ParamBlock.Extent.Text + "`n`$MsixBaseVersion")
    foreach ($version in @('2026.9.401', '2026.9.411', '65535.65535.65535')) {
        if ((& $bindBase -Msix Dev -MsixBaseVersion $version) -ne $version) { throw 'Valid MSIX base was rejected.' }
    }
    foreach ($version in @('', '0.9.401', '2026.09.401', 'v2026.9.401', '2026.9.401.0', '65536.9.401', '2026.9.65536')) {
        Assert-Fails { & $bindBase -Msix Dev -MsixBaseVersion $version } 'cannot validate argument'
    }
    Assert-Fails { & (Join-Path $RepoRoot 'build.ps1') -MsixBaseVersion '2026.9.401' } '-MsixBaseVersion requires -Msix Dev.'
    & {
        $baseSelector = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Select-LocalDevMsixBaseVersion'
        }, $true)
        . ([scriptblock]::Create($baseSelector.Extent.Text))
        if ((Select-LocalDevMsixBaseVersion $null '2026.9.5') -ne $null) {
            throw 'Missing installed package must preserve the application-derived base.'
        }
        if ((Select-LocalDevMsixBaseVersion ([version]'2026.9.401.123') '2026.9.5') -ne '2026.9.401') {
            throw 'A higher installed Dev base must be reused.'
        }
        if ((Select-LocalDevMsixBaseVersion ([version]'2026.9.5.123') '2026.9.5') -ne $null) {
            throw 'An equal installed Dev base must preserve the application-derived base.'
        }
        if ((Select-LocalDevMsixBaseVersion ([version]'2026.8.999.123') '2026.9.5') -ne $null) {
            throw 'An older installed Dev base must not override a newer application base.'
        }

        $buildFunction = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Build-Project'
        }, $true)
        . ([scriptblock]::Create($buildFunction.Extent.Text))
        $probe = @{ Arguments = @() }
        function Invoke-DotNetCaptured($arguments) { $probe.Arguments = @($arguments); $global:LASTEXITCODE = 0 }
        $probeInstalledDevPackage = $null
        function Get-InstalledDevMsixPackage { $probeInstalledDevPackage }
        function Get-CurrentAppBaseVersion { '2026.9.5' }
        function Write-Success($message) {}
        $explicitMsixRevision = $true
        $MsixRevision = 123
        $MsixOutputDirectory = Join-Path $temporaryRoot 'dev-build'
        $DevBuild = $true
        $Configuration = 'Release'
        foreach ($rid in @('win-x64', 'win-arm64')) {
            foreach ($MsixBaseVersion in @('', '2026.9.411')) {
                foreach ($packageMsix in @($false, $true)) {
                    if (-not (Build-Project 'WinUI' (Join-Path $RepoRoot 'build.ps1') $true $packageMsix)) { throw 'Build argument probe failed.' }
                    $hasBase = @($probe.Arguments | Where-Object { $_ -like '-p:MsixPackageBaseVersion=*' }).Count -gt 0
                    if ($hasBase -ne ($packageMsix -and [bool]$MsixBaseVersion)) { throw 'MSIX base escaped its package-only scope.' }
                    if ($hasBase -and $probe.Arguments -notcontains '-p:MsixPackageBaseVersion=2026.9.411') { throw 'Wrong Dev package base.' }
                    if (@($probe.Arguments | Where-Object {
                        $_ -match '^-p:(Version|UpdateVersionProperties|UpdateAssemblyInfo|AssemblyVersion|FileVersion|InformationalVersion)='
                    }).Count) { throw 'Dev packaging must preserve app version metadata.' }
                }
            }
        }

        $explicitMsixRevision = $false
        $MsixBaseVersion = ''
        $probeInstalledDevPackage = [pscustomobject]@{ Version = [version]'2026.9.401.123' }
        if (-not (Build-Project 'WinUI' (Join-Path $RepoRoot 'build.ps1') $true $true)) {
            throw 'Installed Dev package build argument probe failed.'
        }
        if ($probe.Arguments -notcontains '-p:MsixPackageBaseVersion=2026.9.401' -or
            $probe.Arguments -notcontains '-p:MsixRevision=124') {
            throw 'A higher installed Dev package must supply its base and next revision.'
        }

        $MsixBaseVersion = '2026.9.411'
        if (-not (Build-Project 'WinUI' (Join-Path $RepoRoot 'build.ps1') $true $true)) {
            throw 'Explicit Dev package base precedence probe failed.'
        }
        if ($probe.Arguments -notcontains '-p:MsixPackageBaseVersion=2026.9.411') {
            throw 'An explicit Dev package base must override the installed package base.'
        }

        $MsixBaseVersion = ''
        $probeInstalledDevPackage = [pscustomobject]@{ Version = [version]'2026.8.999.123' }
        if (-not (Build-Project 'WinUI' (Join-Path $RepoRoot 'build.ps1') $true $true)) {
            throw 'Older installed Dev package build argument probe failed.'
        }
        if (@($probe.Arguments | Where-Object { $_ -like '-p:MsixPackageBaseVersion=*' }).Count -ne 0 -or
            $probe.Arguments -notcontains '-p:MsixRevision=124') {
            throw 'An older installed Dev base must not override the app base, but its next revision must remain monotonic.'
        }
    }
    Write-Host 'MSIX CI artifact contracts passed: version bounds, identity, architecture, signature rejection, exact package selection, provenance, and public-only exports.'
}
finally {
    $certificate.Dispose()
    $rsa.Dispose()
    [IO.Directory]::Delete($temporaryRoot, $true)
}
