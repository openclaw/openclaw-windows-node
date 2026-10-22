#requires -Version 5.1
<#
.SYNOPSIS
    Preflights an eligible signed Inno source for current-policy Store migration tests.
.DESCRIPTION
    Checks source labels, payload, release identity, executable FileVersion and
    ProductVersion, and gateway-root entry types. This is not the Store detector
    or a signature, architecture, target-version, or full data-inventory check.
    Alpha binaries cannot be made eligible by changing registry labels.

    Close the relevant Inno and Store apps yourself before using this helper.
    It never enumerates or stops processes. Mutations require exclusive ownership
    of store-migration\prepare.lock, shared with migration and gateway uninstall.
    Binaries and allowed native setup draft files are never modified.
.PARAMETER TargetVersion
    Optional equivalent source registration label, not the Store package version.
    Defaults to the complete installed DisplayVersion, including any correction.
    Both current and requested labels must match the executable metadata.
.PARAMETER Revert
    Restore saved original labels if they still identify this binary. Also supports
    undoing historical alpha relabels, without claiming that alpha can migrate.
    Refuses after a completion receipt or unexpected label drift.
.PARAMETER MoveBlockingFiles
    Quarantine unknown ordinary gateway-root files. Allowed native setup draft
    files stay in place. Reparse points and unexpected directories are refused.
.PARAMETER ResetMigrationState
    Remove interrupted consent/intent records and record-write temporary files.
    Preserves prepare.lock, unknown files, and the completion receipt by default.
    Works without an installed Inno source. Cannot be combined with label options.
.PARAMETER IncludeCompletionReceipt
    Explicitly include completed.dpapi in a reset. Without this receipt and without
    Store registration, later gateway uninstall can unregister the WSL distro and
    delete its generated state. Only use for an intentionally disposable cold start.
.EXAMPLE
    .\scripts\Enable-StoreMigrationTest.ps1 -WhatIf
.EXAMPLE
    .\scripts\Enable-StoreMigrationTest.ps1 -ResetMigrationState -WhatIf
.EXAMPLE
    .\scripts\Enable-StoreMigrationTest.ps1 -Revert
.NOTES
    See docs/STORE_MIGRATION_TESTING.md for eligible artifacts, proof and recovery.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $TargetVersion,
    [switch] $Revert,
    [switch] $MoveBlockingFiles,
    [switch] $ResetMigrationState,
    [switch] $IncludeCompletionReceipt
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AppId = '{M0LTB0T-TRAY-4PP1-D3N7}_is1'
$BackupVersionValue = 'OpenClawMigrationTestOriginalDisplayVersion'
$BackupNameValue = 'OpenClawMigrationTestOriginalDisplayName'
$MigrationStateDirectory = Join-Path $env:APPDATA 'OpenClawTray\store-migration'
$CompletionReceiptFile = 'completed.dpapi'
$MigrationStateFiles = @('consent.lock', 'intent.dpapi', 'consent.dpapi')
$RequiredPayload = @(
    'OpenClaw.Tray.WinUI.exe', 'unins000.exe', 'app-identity.txt',
    'Test-InnoMigration.ps1', 'MigrationRecordCodec.cs', 'Uninstall-LocalGateway.ps1'
)

function Get-RegistrationKeyPath {
    $candidates = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )
    $found = @($candidates | Where-Object { Test-Path -LiteralPath $_ })
    if ($found.Count -eq 0) {
        throw 'No per-user OpenClaw Companion (Inno) registration found. Install an eligible signed source first, or use a standalone reset.'
    }
    if ($found.Count -gt 1) {
        throw 'Multiple per-user registration paths exist. Resolve the ambiguous installation before testing.'
    }
    return $found[0]
}

# Keep this normalization in step with MigrationVersionPolicy. Product metadata
# is significant: a numeric FileVersion alone does not establish a stable release.
function ConvertTo-NumericVersion {
    param([string] $Value)
    if ($Value -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z') { return $null }
    $parsed = $null
    if (-not [version]::TryParse($Value, [ref]$parsed)) { return $null }
    return [version]::new($parsed.Major, $parsed.Minor, $parsed.Build, [Math]::Max(0, $parsed.Revision))
}

function Get-ProductRelease {
    param([string] $Value)
    if ($Value -cmatch '\A([^+]+)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z') {
        return $Matches[1]
    }
    return $null
}

function Test-ExecutableVersionCompatible {
    param([string] $Release, [string] $FileVersion, [string] $ProductVersion)
    $file = ConvertTo-NumericVersion $FileVersion
    if ($null -eq $file) { return $false }
    $numeric = ConvertTo-NumericVersion $Release
    if ($null -ne $numeric) {
        if ($numeric -ne $file) { return $false }
        if ([string]::IsNullOrEmpty($ProductVersion)) { return $true }
        $product = ConvertTo-NumericVersion (Get-ProductRelease $ProductVersion)
        return $null -ne $product -and $numeric -eq $product
    }
    if ($Release -cnotmatch '\A([0-9]+\.[0-9]+\.[0-9]+)-([1-9][0-9]*)\z') { return $false }
    $base = ConvertTo-NumericVersion $Matches[1]
    $correction = 0
    if (-not [int]::TryParse($Matches[2], [ref]$correction)) { return $false }
    return $null -ne $base -and $base -eq $file -and (Get-ProductRelease $ProductVersion) -ceq $Release
}

function Assert-NoReparsePointAncestors {
    param([string] $Path)
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        try {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing migration helper operation: '$current' is a reparse point."
            }
        }
        catch [IO.FileNotFoundException] {}
        catch [IO.DirectoryNotFoundException] {}
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Open-MigrationOperationLock {
    $lockPath = Join-Path $MigrationStateDirectory 'prepare.lock'
    Assert-NoReparsePointAncestors $lockPath
    $exists = Test-Path -LiteralPath $lockPath
    if ($exists -and -not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        throw "Migration lock '$lockPath' is not an ordinary file."
    }
    if (-not $exists) {
        if (-not $PSCmdlet.ShouldProcess($lockPath, 'Create migration operation lock')) {
            Write-Host 'No lock created. No authoritative scan or changes performed.'
            return $null
        }
        New-Item -ItemType Directory -Path $MigrationStateDirectory -Force -Confirm:$false | Out-Null
    }
    Assert-NoReparsePointAncestors $lockPath
    try {
        return [IO.FileStream]::new(
            $lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw "Cannot acquire exclusive migration lock '$lockPath'. Close the relevant Inno and Store apps yourself and wait for migration or gateway uninstall to finish. No records or labels were changed. $($_.Exception.Message)"
    }
}

function Get-StaleMigrationRecords {
    $names = @($MigrationStateFiles)
    if ($IncludeCompletionReceipt) { $names += $CompletionReceiptFile }
    foreach ($item in Get-ChildItem -LiteralPath $MigrationStateDirectory -Force) {
        Assert-NoReparsePointAncestors $item.FullName
        $isRecord = $names -contains $item.Name -or $item.Name -cmatch '\A\.[0-9a-fA-F]{32}\.tmp\z'
        if ($item.PSIsContainer -and ($isRecord -or $item.Name -in @('prepare.lock', $CompletionReceiptFile))) {
            throw "Migration record '$($item.FullName)' is not an ordinary file."
        }
        if ($isRecord) { $item }
    }
}

function Assert-NoCompletionReceipt {
    $receipt = Join-Path $MigrationStateDirectory $CompletionReceiptFile
    Assert-NoReparsePointAncestors $receipt
    if (Test-Path -LiteralPath $receipt) {
        throw "Completion receipt '$receipt' exists. Finish migration and finalization instead of changing labels."
    }
}

function Get-BlockingGatewayFiles {
    $directory = Join-Path $env:APPDATA 'OpenClawTray\gateways'
    Assert-NoReparsePointAncestors $directory
    if (-not (Test-Path -LiteralPath $directory)) { return }
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw "Gateway root '$directory' is not a directory."
    }
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        Assert-NoReparsePointAncestors $item.FullName
        # MigrationInventory uses ordinal names and lowercase hex for atomic siblings.
        $allowedDraft = $item.Name -ceq 'native-setup-draft.json' -or
            $item.Name -cmatch '\A\.native-setup-draft\.json\.[0-9a-f]{32}\.tmp\z'
        if ($allowedDraft -and $item.PSIsContainer) {
            throw "Native setup file '$($item.FullName)' is unexpectedly a directory."
        }
        if (-not $item.PSIsContainer -and -not $allowedDraft) { $item }
    }
}

function Assert-SourcePayload {
    param($Properties)
    $directory = [string]$Properties.InstallLocation
    $canonical = Join-Path $env:LOCALAPPDATA 'OpenClawTray'
    if ($directory -ine $canonical -and $directory -ine "$canonical\") {
        throw "InstallLocation '$directory' is not the canonical per-user location '$canonical'."
    }
    if ($Properties.Publisher -cne 'OpenClaw Foundation' -or
        $Properties.DisplayName -cne "OpenClaw Companion version $($Properties.DisplayVersion)") {
        throw 'The current registration publisher or DisplayName does not match the source label.'
    }
    if ($Properties.UninstallString -ine ('"' + (Join-Path $canonical 'unins000.exe') + '"')) {
        throw 'The uninstall command is not the quoted canonical unins000.exe path.'
    }
    foreach ($name in $RequiredPayload) {
        $path = Join-Path $directory $name
        Assert-NoReparsePointAncestors $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required migration payload '$path' is missing or is not an ordinary file. Use an eligible signed source."
        }
    }
    $identity = Get-Content -LiteralPath (Join-Path $directory 'app-identity.txt') -Raw
    if ($identity -cnotin @('release', "release`n", "release`r`n")) {
        throw 'Installed application identity is not release.'
    }
    return (Get-Item -LiteralPath (Join-Path $directory 'OpenClaw.Tray.WinUI.exe')).VersionInfo
}

if ($IncludeCompletionReceipt -and -not $ResetMigrationState) {
    throw '-IncludeCompletionReceipt only applies to -ResetMigrationState.'
}
if ($ResetMigrationState -and ($Revert -or $MoveBlockingFiles -or $PSBoundParameters.ContainsKey('TargetVersion'))) {
    throw '-ResetMigrationState cannot be combined with label or quarantine options.'
}
if ($Revert -and ($MoveBlockingFiles -or $PSBoundParameters.ContainsKey('TargetVersion'))) {
    throw '-Revert cannot be combined with target or quarantine options.'
}

if ($ResetMigrationState) {
    Assert-NoReparsePointAncestors (Join-Path $MigrationStateDirectory 'prepare.lock')
    if (-not (Test-Path -LiteralPath $MigrationStateDirectory)) {
        Write-Host 'No migration state. Nothing to reset.'
        return
    }
    if (-not (Test-Path -LiteralPath $MigrationStateDirectory -PathType Container)) {
        throw "Migration state '$MigrationStateDirectory' is not a directory."
    }
    # Read-only no-op detection avoids creating even a lock for receipt-only state.
    # Never use this preliminary list for deletion; rescan under exclusive ownership.
    if (@(Get-StaleMigrationRecords).Count -eq 0) {
        Write-Host 'No resettable records. Completion receipt, operation lock and unknown entries are preserved.'
        return
    }
    $operationLock = Open-MigrationOperationLock
    if ($null -eq $operationLock) { return }
    try {
        $records = @(Get-StaleMigrationRecords)
        if ($IncludeCompletionReceipt) {
            Write-Warning 'Including completed.dpapi. Without it and Store registration, later gateway uninstall can unregister the WSL distro and delete generated state.'
        }
        $removed = 0
        foreach ($item in $records) {
            Assert-NoReparsePointAncestors $item.FullName
            if ($PSCmdlet.ShouldProcess($item.FullName, 'Delete migration record')) {
                Remove-Item -LiteralPath $item.FullName -Force -Confirm:$false
                $removed++
            }
        }
        $remaining = @(Get-StaleMigrationRecords).Count
        Write-Host "Reset removed $removed record(s); $remaining resettable record(s) remain. prepare.lock and unknown entries were preserved."
    }
    finally { $operationLock.Dispose() }
    return
}

$keyPath = Get-RegistrationKeyPath
$props = Get-ItemProperty -LiteralPath $keyPath
$binary = Assert-SourcePayload $props
$currentVersion = [string]$props.DisplayVersion
$originalVersion = $props.PSObject.Properties[$BackupVersionValue]
$originalName = $props.PSObject.Properties[$BackupNameValue]

if ($Revert) {
    if (-not $originalVersion -or -not $originalName -or
        $originalName.Value -cne "OpenClaw Companion version $($originalVersion.Value)") {
        throw 'No complete, coherent saved original label found. Inspect the saved values before recovery.'
    }
    $currentMatches = Test-ExecutableVersionCompatible $currentVersion $binary.FileVersion $binary.ProductVersion
    $originalMatches = Test-ExecutableVersionCompatible $originalVersion.Value $binary.FileVersion $binary.ProductVersion
    # Historical helper backups lack an applied-label marker. Only restore a
    # prerelease when its exact ProductVersion and original numeric core agree.
    $legacyMatches = $false
    if ($originalVersion.Value -cmatch '\A([0-9]+\.[0-9]+\.[0-9]+)-[A-Za-z][0-9A-Za-z.-]*\z') {
        $legacyCore = $Matches[1]
        $legacyMatches = (Get-ProductRelease $binary.ProductVersion) -ceq $originalVersion.Value -and
            $currentVersion -ceq $legacyCore -and
            (ConvertTo-NumericVersion $legacyCore) -eq (ConvertTo-NumericVersion $binary.FileVersion) -and
            $null -ne (ConvertTo-NumericVersion $legacyCore)
    }
    if (-not (($currentMatches -and $originalMatches) -or $legacyMatches)) {
        throw 'Saved/current label or binary metadata drifted. Refusing to overwrite an unexpected installation.'
    }
}
else {
    if (-not $PSBoundParameters.ContainsKey('TargetVersion')) { $TargetVersion = $currentVersion }
    foreach ($label in @($currentVersion, $TargetVersion)) {
        if (-not (Test-ExecutableVersionCompatible $label $binary.FileVersion $binary.ProductVersion)) {
            throw "Label '$label' is incompatible with FileVersion '$($binary.FileVersion)' and ProductVersion '$($binary.ProductVersion)'. Current policy rejects alpha ProductVersion; use an eligible signed source, not an alpha relabel."
        }
    }
    if ($originalVersion -or $originalName) {
        throw 'Saved labels already exist. Use -Revert to restore them before another relabel.'
    }
}

Write-Host 'Local preflight passed: source labels, payload, release identity and executable versions.'
Write-Host 'This does not prove Store admission, signatures/provenance, PE architecture, target minimum version or full data validity.'
# No filesystem or registry mutation occurs before the metadata checks above.
$operationLock = Open-MigrationOperationLock
if ($null -eq $operationLock) { return }
try {
    Assert-NoCompletionReceipt
    # Re-read under ownership so a preflight snapshot cannot overwrite label drift.
    $lockedProps = Get-ItemProperty -LiteralPath $keyPath
    foreach ($name in @('InstallLocation', 'Publisher', 'UninstallString', 'DisplayVersion', 'DisplayName', $BackupVersionValue, $BackupNameValue)) {
        $before = $props.PSObject.Properties[$name]
        $after = $lockedProps.PSObject.Properties[$name]
        if (($null -eq $before) -ne ($null -eq $after) -or
            ($null -ne $before -and [string]$before.Value -cne [string]$after.Value)) {
            throw "Registration '$name' changed during preflight. No labels were changed."
        }
    }
    if ($Revert) {
        if ($PSCmdlet.ShouldProcess($keyPath, 'Restore saved original label')) {
            Set-ItemProperty -LiteralPath $keyPath -Name DisplayVersion -Value $originalVersion.Value -Type String -Confirm:$false
            Set-ItemProperty -LiteralPath $keyPath -Name DisplayName -Value $originalName.Value -Type String -Confirm:$false
            Remove-ItemProperty -LiteralPath $keyPath -Name $BackupVersionValue -Confirm:$false
            Remove-ItemProperty -LiteralPath $keyPath -Name $BackupNameValue -Confirm:$false
            Write-Host "Restored saved label '$($originalVersion.Value)'. This does not establish migration eligibility."
        }
        return
    }

    $blocking = @(Get-BlockingGatewayFiles)
    if ($blocking.Count -gt 0) {
        $names = ($blocking | ForEach-Object { $_.Name }) -join ', '
        if (-not $MoveBlockingFiles) {
            throw "Unknown gateway-root files: $names. Inspect them; -MoveBlockingFiles can quarantine ordinary files only. Allowed native setup files are preserved."
        }
        $quarantine = Join-Path $env:APPDATA ("OpenClawTray\gateways-blocking-" + [guid]::NewGuid().ToString('N'))
        Assert-NoReparsePointAncestors $quarantine
        if (-not $PSCmdlet.ShouldProcess($names, "Quarantine unknown files in $quarantine")) {
            Write-Host 'No files moved and no labels changed.'
            return
        }
        New-Item -ItemType Directory -Path $quarantine -Confirm:$false | Out-Null
        foreach ($item in $blocking) {
            Assert-NoReparsePointAncestors $item.FullName
            Move-Item -LiteralPath $item.FullName -Destination (Join-Path $quarantine $item.Name) -Confirm:$false
        }
        Write-Host "Quarantined unknown files in '$quarantine'. Review before restoring; never overwrite newer state."
    }
    if ($currentVersion -ceq $TargetVersion) {
        Write-Host "No relabel necessary for '$currentVersion'. Gateway-root entry check passed; the Store app still owns admission."
        return
    }
    if ($PSCmdlet.ShouldProcess($keyPath, "Relabel $currentVersion -> $TargetVersion")) {
        New-ItemProperty -LiteralPath $keyPath -Name $BackupVersionValue -Value $currentVersion -PropertyType String -Confirm:$false | Out-Null
        New-ItemProperty -LiteralPath $keyPath -Name $BackupNameValue -Value $props.DisplayName -PropertyType String -Confirm:$false | Out-Null
        Set-ItemProperty -LiteralPath $keyPath -Name DisplayVersion -Value $TargetVersion -Type String -Confirm:$false
        Set-ItemProperty -LiteralPath $keyPath -Name DisplayName -Value "OpenClaw Companion version $TargetVersion" -Type String -Confirm:$false
        Write-Host "Relabelled $currentVersion -> $TargetVersion. Saved originals can be restored with -Revert before migration completes."
    }
}
finally { $operationLock.Dispose() }
