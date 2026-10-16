<#
.SYNOPSIS
    Exercises signed Dev MSIX release staging and alpha scheduling.
.DESCRIPTION
    Uses synthetic signed-package archives, public certificates, and metadata.
    No product build, release upload, or signing service is invoked.
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stager = Join-Path $RepoRoot 'scripts\Stage-DevMsixReleaseAssets.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-dev-msix-release-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$sourceCommit = (& git -C $RepoRoot rev-parse HEAD) -join ''
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve fixture source commit.' }
$scenario = 0
$expectedIdentity = 'OpenClawFoundation.OpenClaw.Dev'
$expectedPublisher = 'CN=OpenClaw Local Development'
$expectedRunId = 456
$expectedRevision = 123
$rsa = [Security.Cryptography.RSA]::Create(2048)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    $expectedPublisher, $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
    [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$certificate = $request.CreateSelfSigned(
    [DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(30))
$signatureStatus = 'Valid'
$signatureCertificate = $certificate

function Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath)) { throw 'Signature probe received a missing package.' }
    [pscustomobject]@{ Status = $signatureStatus; SignerCertificate = $signatureCertificate }
}

function Assert-Fails {
    param([scriptblock]$Action, [string]$Expected)
    try {
        & $Action | Out-Null
        throw 'The operation unexpectedly succeeded.'
    }
    catch {
        if (-not $_.Exception.Message.Contains($Expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Expected '$Expected', received: $($_.Exception.Message)"
        }
    }
}

function New-Package {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Architecture,
        [string]$Version = '2026.7.202.123',
        [string]$Identity = $expectedIdentity,
        [string]$Publisher = $expectedPublisher,
        [switch]$OmitSignature
    )
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $manifest = $zip.CreateEntry('AppxManifest.xml')
        $writer = [IO.StreamWriter]::new($manifest.Open())
        try {
            $writer.Write(
                '<Package><Identity Name="{0}" Publisher="{1}" ProcessorArchitecture="{2}" Version="{3}" /></Package>',
                $Identity, $Publisher, $Architecture, $Version)
        }
        finally { $writer.Dispose() }
        if (-not $OmitSignature) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry('AppxSignature.p7x').Open())
            try { $writer.Write('Synthetic signature marker.') }
            finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function New-Fixture {
    param([string]$Version = '2026.7.2-alpha.4')

    $script:scenario++
    $inputPath = Join-Path $temporaryRoot "input-$scenario"
    $allocation = [ordered]@{
        schemaVersion = 1
        sourceVersion = $Version
        sourceCommit = $sourceCommit
        sourceRef = "refs/tags/v$Version"
        repository = 'openclaw/openclaw-windows-node'
        baseVersion = '2026.7.2'
        packageBaseVersion = '2026.7.202'
        storePackageVersion = '2026.7.202.0'
        packagingRevision = 2
        allocation = 'reserved'
        reservationRef = 'refs/tags/msix-package/2026.7.2/202'
    }
    $allocationPath = Join-Path $temporaryRoot "allocation-$scenario.json"
    $allocation | ConvertTo-Json | Set-Content -LiteralPath $allocationPath
    foreach ($architecture in @('x64', 'arm64')) {
        $directory = Join-Path $inputPath "openclaw-msix-dev-$architecture"
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $packageName = "OpenClaw-Dev-$architecture.msix"
        $packagePath = Join-Path $directory $packageName
        New-Package -Path $packagePath -Architecture $architecture
        $certificatePath = Join-Path $directory 'OpenClaw-Dev.cer'
        [IO.File]::WriteAllBytes(
            $certificatePath,
            $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        Set-Content -LiteralPath (Join-Path $directory 'INSTALL.txt') -Value "Install $packageName"
        [ordered]@{
            repository = 'https://github.com/openclaw/openclaw-windows-node'
            sourceCommit = $sourceCommit
            sourceTreeDirty = $false
            architecture = $architecture
            archive = $packageName
            sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
            signed = $true
            signing = 'development-only'
            identityName = $expectedIdentity
            packageVersion = '2026.7.202.123'
            publisher = $expectedPublisher
            certificate = 'OpenClaw-Dev.cer'
            certificateThumbprint = $certificate.Thumbprint
            certificateSha256 = (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash.ToLowerInvariant()
            certificateExpiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
            workflowRunId = $expectedRunId
            workflowRunAttempt = 1
            msixVersionAllocation = $allocation
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory 'msix-metadata.json')
    }
    @{
        ArtifactDirectory = $inputPath
        OutputDirectory = Join-Path $temporaryRoot "output-$scenario"
        Version = $Version
        ExpectedSourceCommit = $sourceCommit
        ExpectedRevision = $expectedRevision
        ExpectedWorkflowRunId = $expectedRunId
        VersionInfoPath = $allocationPath
    }
}

function Get-MetadataPath {
    param([hashtable]$Arguments, [string]$Architecture = 'arm64')
    Join-Path $Arguments.ArtifactDirectory "openclaw-msix-dev-$Architecture\msix-metadata.json"
}

$oldEvent = $env:EVENT_NAME
$oldSchedule = $env:SCHEDULE
$oldOutput = $env:GITHUB_OUTPUT
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $arguments = New-Fixture
    $assets = & $stager @arguments
    $names = @($assets.Files | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
    $expected = @('OpenClaw-Dev-arm64.zip', 'OpenClaw-Dev-x64.zip')
    if (@(Compare-Object $expected $names).Count -gt 0 -or
        @(Get-ChildItem -LiteralPath $arguments.OutputDirectory).Count -ne 2) {
        throw 'Release assets did not match the exact signed Dev allowlist.'
    }
    foreach ($architecture in @('x64', 'arm64')) {
        $zipPath = Join-Path $arguments.OutputDirectory "OpenClaw-Dev-$architecture.zip"
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $actualEntries = @($zip.Entries.FullName | Sort-Object)
            $expectedEntries = @(
                'INSTALL.txt', 'msix-metadata.json', 'OpenClaw-Dev.cer',
                "OpenClaw-Dev-$architecture.msix") | Sort-Object
            if (@(Compare-Object $expectedEntries $actualEntries).Count -gt 0) {
                throw "The $architecture release ZIP did not preserve the exact Dev artifact."
            }
        }
        finally { $zip.Dispose() }
    }
    foreach ($token in @(
        'Signed Dev MSIX packages', 'OpenClaw-Dev-x64.zip', 'OpenClaw-Dev-arm64.zip',
        'development-signed', 'Microsoft Store-signed', 'workflow artifacts',
        "actions/runs/$expectedRunId")) {
        if (-not $assets.Notes.Contains($token)) { throw "Release notes are missing '$token'." }
    }
    Assert-Fails { & $stager @arguments } 'absent or empty'

    foreach ($version in @('2026.7.2', '2026.7.2-3', '2026.7.2-beta.1')) {
        $arguments = New-Fixture -Version $version
        & $stager @arguments | Out-Null
    }

    foreach ($mutation in @(
        @{ Field = 'sourceCommit'; Value = ('b' * 40); Error = 'expected clean source' },
        @{ Field = 'sourceTreeDirty'; Value = $true; Error = 'expected clean source' },
        @{ Field = 'signed'; Value = $false; Error = 'signed metadata' },
        @{ Field = 'signing'; Value = 'store'; Error = 'signed metadata' },
        @{ Field = 'identityName'; Value = 'OpenClawFoundation.OpenClaw'; Error = 'signed metadata' },
        @{ Field = 'publisher'; Value = 'CN=Wrong'; Error = 'signed metadata' },
        @{ Field = 'architecture'; Value = 'x64'; Error = 'signed metadata' },
        @{ Field = 'packageVersion'; Value = '2026.7.202.122'; Error = 'signed metadata' },
        @{ Field = 'workflowRunId'; Value = 999; Error = 'signed metadata' },
        @{ Field = 'sha256'; Value = ('0' * 64); Error = 'package hash' },
        @{ Field = 'certificateSha256'; Value = ('0' * 64); Error = 'certificate hash' },
        @{ Field = 'certificateThumbprint'; Value = ('0' * 40); Error = 'public signer metadata' }
    )) {
        $arguments = New-Fixture
        $path = Get-MetadataPath $arguments
        $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $metadata.($mutation.Field) = $mutation.Value
        $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
        Assert-Fails { & $stager @arguments } $mutation.Error
        if (Test-Path -LiteralPath $arguments.OutputDirectory) {
            throw 'Rejected ARM64 input left partial release assets.'
        }
    }

    $arguments = New-Fixture
    Set-Content -LiteralPath (
        Join-Path $arguments.ArtifactDirectory 'openclaw-msix-dev-x64\extra.pfx') -Value 'not a key'
    Assert-Fails { & $stager @arguments } 'exactly'
    $arguments = New-Fixture
    $packagePath = Join-Path $arguments.ArtifactDirectory 'openclaw-msix-dev-arm64\OpenClaw-Dev-arm64.msix'
    [IO.File]::AppendAllText($packagePath, 'tampered')
    Assert-Fails { & $stager @arguments } 'package hash'
    $arguments = New-Fixture
    $packagePath = Join-Path $arguments.ArtifactDirectory 'openclaw-msix-dev-arm64\OpenClaw-Dev-arm64.msix'
    [IO.File]::Delete($packagePath)
    New-Package -Path $packagePath -Architecture arm64 -OmitSignature
    $path = Get-MetadataPath $arguments
    $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $metadata.sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
    Assert-Fails { & $stager @arguments } 'missing AppxSignature.p7x'
    $arguments = New-Fixture
    $path = Get-MetadataPath $arguments
    $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $metadata.msixVersionAllocation.allocation = 'preview'
    $metadata.msixVersionAllocation.reservationRef = $null
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
    Assert-Fails { & $stager @arguments } 'MSIX'
    $signatureStatus = 'HashMismatch'
    $arguments = New-Fixture
    Assert-Fails { & $stager @arguments } 'signature is not valid'
    $signatureStatus = 'Valid'

    $daily = Get-Content -LiteralPath (Join-Path $RepoRoot '.github\workflows\daily-alpha-release.yml') -Raw
    $scheduleBlock = [regex]::Match($daily, '(?ms)^        run: \|\r?\n(?<script>.*?)(?=^      - )')
    if (-not $scheduleBlock.Success) { throw 'The daily alpha workflow is missing its schedule selector.' }
    $scheduleScript = $scheduleBlock.Groups['script'].Value -replace '(?m)^          ', ''
    $bash = if ($IsWindows) {
        $git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
        Join-Path (Split-Path (Split-Path $git -Parent) -Parent) 'bin\bash.exe'
    } else {
        (Get-Command bash -CommandType Application | Select-Object -First 1).Source
    }
    if (-not (Test-Path -LiteralPath $bash)) { throw 'Git Bash is required to test the actual alpha workflow selector.' }
    foreach ($case in @(
        @{ Event = 'workflow_dispatch'; Schedule = ''; Offset = '+0000'; Expected = 'run=true' },
        @{ Event = 'schedule'; Schedule = '0 21 * * *'; Offset = '-0700'; Expected = 'run=true' },
        @{ Event = 'schedule'; Schedule = '0 22 * * *'; Offset = '-0700'; Expected = 'run=false' },
        @{ Event = 'schedule'; Schedule = '0 22 * * *'; Offset = '-0800'; Expected = 'run=true' },
        @{ Event = 'schedule'; Schedule = '0 21 * * *'; Offset = '-0800'; Expected = 'run=false' }
    )) {
        $env:EVENT_NAME = $case.Event
        $env:SCHEDULE = $case.Schedule
        $env:GITHUB_OUTPUT = (Join-Path $temporaryRoot 'schedule-output.txt').Replace('\', '/')
        [IO.File]::WriteAllText($env:GITHUB_OUTPUT, '')
        $clock = "date() { printf '%s\n' '$($case.Offset)'; }`n"
        & $bash --noprofile --norc -c ($clock + $scheduleScript)
        if ($LASTEXITCODE -ne 0) { throw 'The actual alpha workflow schedule selector failed.' }
        if ((Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw).Trim() -ne $case.Expected) {
            throw "Incorrect schedule selection for $($case.Event), $($case.Schedule), $($case.Offset)."
        }
    }
    Write-Host 'Dev MSIX release tests passed: exact signed assets, provenance, hashes, versions, no partial staging, and alpha scheduling.'
}
finally {
    $env:EVENT_NAME = $oldEvent
    $env:SCHEDULE = $oldSchedule
    $env:GITHUB_OUTPUT = $oldOutput
    $certificate.Dispose()
    $rsa.Dispose()
    [IO.Directory]::Delete($temporaryRoot, $true)
}
