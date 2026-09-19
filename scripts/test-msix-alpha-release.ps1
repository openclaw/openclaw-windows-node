<#
.SYNOPSIS
    Exercises alpha-only Store asset staging and workflow release selection.
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
    $script:scenario++
    $inputPath = Join-Path $temporaryRoot "input-$scenario"
    $allocation = [ordered]@{
        schemaVersion = 1
        sourceVersion = '2026.7.2-alpha.4'
        sourceCommit = $sourceCommit
        sourceRef = 'refs/tags/v2026.7.2-alpha.4'
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
    @{
        ArtifactDirectory = $inputPath
        OutputDirectory = Join-Path $temporaryRoot "output-$scenario"
        Version = '2026.7.2-alpha.4'
        ExpectedSourceCommit = $sourceCommit
        VersionInfoPath = $allocationPath
    }
}

$oldRef = $env:GITHUB_REF
$oldEvent = $env:EVENT_NAME
$oldSchedule = $env:SCHEDULE
$oldOutput = $env:GITHUB_OUTPUT
try {
    $arguments = New-Fixture
    $assets = & $stager @arguments
    $names = @($assets.Files | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
    $expected = @(
        'OpenClaw-arm64.msix',
        'OpenClaw-arm64.msix-metadata.json',
        'OpenClaw-x64.msix',
        'OpenClaw-x64.msix-metadata.json'
    )
    if (@(Compare-Object $expected $names).Count -gt 0 -or
        @(Get-ChildItem -LiteralPath $arguments.OutputDirectory).Count -ne 4) {
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
    foreach ($warning in @('OpenClaw-x64.msix', 'OpenClaw-arm64.msix', 'unsigned', 'not installers', '2026.7.202.0', 'reuse its reservation', 'Dev-signed tester downloads remain in Actions')) {
        if (-not $assets.Notes.Contains($warning)) { throw "Release notes are missing '$warning'." }
    }
    Assert-Fails { & $stager @arguments } 'absent or empty'

    foreach ($version in @('2026.7.2', '2026.7.2-3', '2026.7.2-beta.1', '2026.7.2-alpha', '2026.7.2-alpha.01', '2026.7.2-alpha.1+meta')) {
        $arguments = New-Fixture
        $arguments.Version = $version
        Assert-Fails { & $stager @arguments } 'cannot validate argument'
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

    # Execute the actual metadata selector rather than a test-only copy of its regex.
    $workflow = Get-Content -LiteralPath (Join-Path $RepoRoot '.github\workflows\ci.yml') -Raw
    $selectorLine = [regex]::Match($workflow, '(?m)^\s*\$isMsixAlpha = .+$')
    if (-not $selectorLine.Success) { throw 'The workflow is missing its alpha-only selector.' }
    $selector = [scriptblock]::Create($selectorLine.Value + "`n`$isMsixAlpha")
    foreach ($case in @(
        @{ Ref = 'refs/tags/v2026.7.2-alpha.4'; Prerelease = $true; Expected = $true },
        @{ Ref = 'refs/tags/v2026.7.2-alpha.0'; Prerelease = $true; Expected = $true },
        @{ Ref = 'refs/tags/v2026.7.2-alpha.4'; Prerelease = $false; Expected = $false },
        @{ Ref = 'refs/tags/v2026.7.2'; Prerelease = $false; Expected = $false },
        @{ Ref = 'refs/tags/v2026.7.2-3'; Prerelease = $false; Expected = $false },
        @{ Ref = 'refs/tags/v2026.7.2-beta.1'; Prerelease = $true; Expected = $false },
        @{ Ref = 'refs/tags/v2026.7.2-alpha.04'; Prerelease = $true; Expected = $false },
        @{ Ref = 'refs/tags/v2026.7.2-Alpha.4'; Prerelease = $true; Expected = $false },
        @{ Ref = 'refs/tags/v2026.7.2-alpha.4+build'; Prerelease = $true; Expected = $false },
        @{ Ref = 'refs/heads/v2026.7.2-alpha.4'; Prerelease = $true; Expected = $false },
        @{ Ref = 'refs/pull/1403/merge'; Prerelease = $true; Expected = $false }
    )) {
        $env:GITHUB_REF = $case.Ref
        $isPrerelease = $case.Prerelease
        if ((& $selector) -ne $case.Expected) { throw "Incorrect alpha selection for $($case.Ref)." }
    }
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
    Write-Host 'Store MSIX alpha release tests passed: manual/scheduled dispatch, alpha-only selection, exact unsigned assets, provenance, hashes, version checks, and no partial staging.'
}
finally {
    $env:GITHUB_REF = $oldRef
    $env:EVENT_NAME = $oldEvent
    $env:SCHEDULE = $oldSchedule
    $env:GITHUB_OUTPUT = $oldOutput
    [IO.Directory]::Delete($temporaryRoot, $true)
}
