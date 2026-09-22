<#
.SYNOPSIS
    Exercises Store asset staging for tag releases and alpha scheduling.
.DESCRIPTION
    Uses synthetic package bytes and metadata. Real package-content validation
    remains in Build-StoreMsix.ps1; no build, signing, or release API is invoked.
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$stager = Join-Path $RepoRoot 'scripts\Stage-StoreMsixReleaseAssets.ps1'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "openclaw-msix-alpha-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$sourceCommit = 'a' * 40
$scenario = 0
[xml]$manifest = Get-Content -LiteralPath (Join-Path $RepoRoot 'src\OpenClaw.Tray.WinUI\Package.appxmanifest') -Raw

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
        $directory = Join-Path $inputPath "openclaw-msix-store-unsigned-$architecture"
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        $packageName = "OpenClaw-$architecture.msix"
        $packagePath = Join-Path $directory $packageName
        Set-Content -LiteralPath $packagePath -Value "Synthetic $architecture package fixture."
        [ordered]@{
            sourceCommit = $sourceCommit
            sourceTreeDirty = $false
            signed = $false
            configuration = 'Release'
            identityName = [string]$manifest.Package.Identity.Name
            publisher = [string]$manifest.Package.Identity.Publisher
            architecture = $architecture
            packageVersion = '2026.7.202.0'
            archive = $packageName
            sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
            msixVersionAllocation = $allocation
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory 'msix-metadata.json')
    }
    $bundleDirectory = Join-Path $inputPath 'openclaw-msix-store-unsigned-bundle'
    New-Item -ItemType Directory -Path $bundleDirectory -Force | Out-Null
    $bundlePath = Join-Path $bundleDirectory 'OpenClaw.msixbundle'
    $bundle = [IO.Compression.ZipFile]::Open($bundlePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $manifestEntry = $bundle.CreateEntry('AppxMetadata/AppxBundleManifest.xml')
        $writer = [IO.StreamWriter]::new($manifestEntry.Open())
        try {
            $writer.Write(
                '<Bundle><Identity Name="{0}" Publisher="{1}" Version="2026.7.202.0" />' +
                '<Packages><Package FileName="OpenClaw-x64.msix" Architecture="x64" />' +
                '<Package FileName="OpenClaw-arm64.msix" Architecture="arm64" /></Packages></Bundle>',
                [string]$manifest.Package.Identity.Name,
                [Security.SecurityElement]::Escape([string]$manifest.Package.Identity.Publisher))
        }
        finally { $writer.Dispose() }
        foreach ($architecture in @('x64', 'arm64')) {
            $packagePath = Join-Path $inputPath "openclaw-msix-store-unsigned-$architecture\OpenClaw-$architecture.msix"
            $entry = $bundle.CreateEntry("OpenClaw-$architecture.msix")
            $source = [IO.File]::OpenRead($packagePath)
            $destination = $entry.Open()
            try { $source.CopyTo($destination) }
            finally { $destination.Dispose(); $source.Dispose() }
        }
    }
    finally { $bundle.Dispose() }
    @{
        ArtifactDirectory = $inputPath
        OutputDirectory = Join-Path $temporaryRoot "output-$scenario"
        Version = $Version
        ExpectedSourceCommit = $sourceCommit
        VersionInfoPath = $allocationPath
    }
}

$oldRef = $env:GITHUB_REF
$oldEvent = $env:EVENT_NAME
$oldSchedule = $env:SCHEDULE
$oldOutput = $env:GITHUB_OUTPUT
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $arguments = New-Fixture
    $assets = & $stager @arguments
    $names = @($assets.Files | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
    $expected = @(
        'OpenClaw.msixbundle',
        'OpenClaw-arm64.msix',
        'OpenClaw-arm64.msix-metadata.json',
        'OpenClaw-x64.msix',
        'OpenClaw-x64.msix-metadata.json'
    )
    if (@(Compare-Object $expected $names).Count -gt 0 -or
        @(Get-ChildItem -LiteralPath $arguments.OutputDirectory).Count -ne 5) {
        throw 'Release assets did not match the exact public Store allowlist.'
    }
    foreach ($architecture in @('x64', 'arm64')) {
        $original = Join-Path $arguments.ArtifactDirectory "openclaw-msix-store-unsigned-$architecture\OpenClaw-$architecture.msix"
        $copy = Join-Path $arguments.OutputDirectory "OpenClaw-$architecture.msix"
        if ((Get-FileHash -LiteralPath $original).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) {
            throw 'Staging changed the validated package bytes.'
        }
        $metadata = Get-Content -LiteralPath "$copy-metadata.json" -Raw | ConvertFrom-Json
        if ($metadata.archive -ne [IO.Path]::GetFileName($copy) -or
            $metadata.sha256 -ne (Get-FileHash -LiteralPath $copy).Hash) {
            throw 'Published metadata did not describe the released file.'
        }
    }
    foreach ($warning in @('OpenClaw.msixbundle', 'recommended', 'OpenClaw-x64.msix', 'OpenClaw-arm64.msix', 'unsigned', 'not installers', '2026.7.202.0', 'reuse its reservation', 'Dev-signed tester downloads remain in Actions')) {
        if (-not $assets.Notes.Contains($warning)) { throw "Release notes are missing '$warning'." }
    }
    Assert-Fails { & $stager @arguments } 'absent or empty'

    foreach ($version in @('2026.7.2', '2026.7.2-3', '2026.7.2-beta.1')) {
        $arguments = New-Fixture -Version $version
        & $stager @arguments | Out-Null
    }
    foreach ($version in @('v2026.7.2', '2026.7', '2026.7.2-alpha.01', '2026.7.2+meta+extra')) {
        $arguments = New-Fixture
        $arguments.Version = $version
        Assert-Fails { & $stager @arguments } 'MSIX'
    }
    foreach ($mutation in @(
        @{ Field = 'sourceCommit'; Value = ('b' * 40); Error = 'expected clean source' },
        @{ Field = 'sourceTreeDirty'; Value = $true; Error = 'expected clean source' },
        @{ Field = 'sourceTreeDirty'; Value = 'false'; Error = 'expected clean source' },
        @{ Field = 'signed'; Value = $true; Error = 'unsigned metadata' },
        @{ Field = 'signed'; Value = 'false'; Error = 'unsigned metadata' },
        @{ Field = 'configuration'; Value = 'Debug'; Error = 'unsigned metadata' },
        @{ Field = 'identityName'; Value = 'OpenClawFoundation.OpenClaw.Dev'; Error = 'unsigned metadata' },
        @{ Field = 'publisher'; Value = 'CN=OpenClaw Local Development'; Error = 'unsigned metadata' },
        @{ Field = 'architecture'; Value = 'x64'; Error = 'unsigned metadata' },
        @{ Field = 'packageVersion'; Value = '2026.7.2.123'; Error = 'unsigned metadata' },
        @{ Field = 'archive'; Value = '..\other.msix'; Error = 'unsigned metadata' },
        @{ Field = 'sha256'; Value = ('0' * 64); Error = 'hash does not match' }
    )) {
        $arguments = New-Fixture
        $path = Join-Path $arguments.ArtifactDirectory 'openclaw-msix-store-unsigned-arm64\msix-metadata.json'
        $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $metadata.($mutation.Field) = $mutation.Value
        $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
        Assert-Fails { & $stager @arguments } $mutation.Error
        if (Test-Path -LiteralPath $arguments.OutputDirectory) { throw 'Rejected ARM64 input left partial release assets.' }
    }
    $arguments = New-Fixture
    Set-Content -LiteralPath (Join-Path $arguments.ArtifactDirectory 'openclaw-msix-store-unsigned-x64\extra.pfx') 'not a real key'
    Assert-Fails { & $stager @arguments } 'exactly'
    $arguments = New-Fixture
    Remove-Item -LiteralPath (Join-Path $arguments.ArtifactDirectory 'openclaw-msix-store-unsigned-arm64\OpenClaw-arm64.msix')
    Assert-Fails { & $stager @arguments } 'exactly'
    $arguments = New-Fixture
    Remove-Item -LiteralPath (Join-Path $arguments.ArtifactDirectory 'openclaw-msix-store-unsigned-bundle\OpenClaw.msixbundle')
    Assert-Fails { & $stager @arguments } 'exactly the unsigned multi-architecture'
    $arguments = New-Fixture
    $bundlePath = Join-Path $arguments.ArtifactDirectory 'openclaw-msix-store-unsigned-bundle\OpenClaw.msixbundle'
    $bundle = [IO.Compression.ZipFile]::Open($bundlePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $bundle.GetEntry('OpenClaw-arm64.msix')
        $entry.Delete()
        $entry = $bundle.CreateEntry('OpenClaw-arm64.msix')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('changed package bytes') }
        finally { $writer.Dispose() }
    }
    finally { $bundle.Dispose() }
    Assert-Fails { & $stager @arguments } 'changed the arm64 package bytes'
    if (Test-Path -LiteralPath $arguments.OutputDirectory) { throw 'Rejected bundle left partial release assets.' }
    $arguments = New-Fixture
    $arguments.Version = '2026.7.3-alpha.4'
    Assert-Fails { & $stager @arguments } 'source version'

    foreach ($mutation in @(
        @{ Field = 'allocation'; Value = 'preview' },
        @{ Field = 'sourceCommit'; Value = ('b' * 40) },
        @{ Field = 'sourceVersion'; Value = '2026.7.2-alpha.3' },
        @{ Field = 'reservationRef'; Value = 'refs/tags/msix-package/2026.7.2/203' }
    )) {
        $arguments = New-Fixture
        $path = Join-Path $arguments.ArtifactDirectory 'openclaw-msix-store-unsigned-arm64\msix-metadata.json'
        $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $metadata.msixVersionAllocation.($mutation.Field) = $mutation.Value
        $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
        Assert-Fails { & $stager @arguments } 'MSIX'
        if (Test-Path -LiteralPath $arguments.OutputDirectory) { throw 'Rejected allocation left partial release assets.' }
    }
    $arguments = New-Fixture
    $path = Join-Path $arguments.ArtifactDirectory 'openclaw-msix-store-unsigned-arm64\msix-metadata.json'
    $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $metadata.PSObject.Properties.Remove('msixVersionAllocation')
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
    Assert-Fails { & $stager @arguments } 'missing its release allocation'
    $arguments = New-Fixture
    $allocation = Get-Content -LiteralPath $arguments.VersionInfoPath -Raw | ConvertFrom-Json
    $allocation.allocation = 'preview'
    $allocation.reservationRef = $null
    $allocation | ConvertTo-Json | Set-Content -LiteralPath $arguments.VersionInfoPath
    Assert-Fails { & $stager @arguments } 'MSIX'

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
        # Stub only the external clock so both Pacific offsets run on any host.
        $clock = "date() { printf '%s\n' '$($case.Offset)'; }`n"
        & $bash --noprofile --norc -c ($clock + $scheduleScript)
        if ($LASTEXITCODE -ne 0) { throw 'The actual alpha workflow schedule selector failed.' }
        if ((Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw).Trim() -ne $case.Expected) {
            throw "Incorrect schedule selection for $($case.Event), $($case.Schedule), $($case.Offset)."
        }
    }
    Write-Host 'Store MSIX release tests passed: stable/correction/prerelease staging, alpha scheduling, exact unsigned assets, provenance, hashes, version checks, and no partial staging.'
}
finally {
    $env:GITHUB_REF = $oldRef
    $env:EVENT_NAME = $oldEvent
    $env:SCHEDULE = $oldSchedule
    $env:GITHUB_OUTPUT = $oldOutput
    [IO.Directory]::Delete($temporaryRoot, $true)
}
