<#
.SYNOPSIS
    Relabels a prerelease OpenClaw Companion (Inno) installation as a stable
    release so the Store app will offer the migration flow.

.DESCRIPTION
    The Store app refuses to migrate from an Inno registration whose
    DisplayVersion is a prerelease such as 2026.9.5-alpha.64. Every build that
    carries the migration payload today is a prerelease, so testing the flow
    requires relabelling the registration.

    Only two registry strings change. The installed binaries are untouched and
    remain genuine and signed. This is faithful because GitVersion stamps the
    prerelease binary with the stable core version already (alpha.64 ships
    FileVersion 2026.9.5.0), so every field the migration detector inspects
    matches a real stable build.

    The original values are saved under the same key and restored by -Revert.

.PARAMETER TargetVersion
    Stable version to present. Defaults to the core of the installed
    prerelease version, which is what a real stable build would carry.

.PARAMETER Revert
    Restore the original DisplayVersion and DisplayName.

.PARAMETER MoveBlockingFiles
    Move stray files out of the gateways folder so preparation can run. They
    are moved, not deleted, into a timestamped folder alongside it.

.PARAMETER ResetMigrationState
    Delete the store-migration records so the next launch re-evaluates from
    scratch. A finished migration consumes its own records, so this is only
    needed after a run that was interrupted part way through and left a
    stale consent or intent behind.

    The completion receipt is left alone. See -IncludeCompletionReceipt.

.PARAMETER IncludeCompletionReceipt
    Also delete completed.dpapi, the receipt that proves a migration finished.
    Leave this off unless you need a true cold start, because the gateway
    uninstaller reads the receipt to decide whether to preserve your WSL
    gateway. Without it, and with the Store app unregistered, uninstalling
    will unregister the distro and delete the generated state it holds.

.EXAMPLE
    .\scripts\Enable-StoreMigrationTest.ps1
    Relabels 2026.9.5-alpha.64 as 2026.9.5.

.EXAMPLE
    .\scripts\Enable-StoreMigrationTest.ps1 -Revert
    Puts the original prerelease label back.

.EXAMPLE
    .\scripts\Enable-StoreMigrationTest.ps1 -ResetMigrationState
    Clears a half-finished migration so the next test starts clean.

.NOTES
    Uninstalling the Inno app removes the key, so a completed migration needs
    no revert. Use -Revert only if you abandon the test before uninstalling.

    Full procedure: docs/STORE_MIGRATION_TESTING.md
    Store-side counterpart: scripts/Export-MigrationTestMsix.ps1
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

# Everything the migration writes, from MigrationRecordCodec and
# InnoMigrationConsentStore. All of it lives in one folder under roaming
# AppData, not in the package-virtualized location, so uninstalling either app
# leaves it behind.
$MigrationStateDirectory = Join-Path $env:APPDATA 'OpenClawTray\store-migration'
$MigrationStateFiles = @(
    'consent.lock',
    'prepare.lock',
    'intent.dpapi',
    'consent.dpapi'
)

# Deliberately not in the list above. Test-InnoMigration.ps1 reads this file to
# decide whether a gateway belongs to a migrated Store install: a valid receipt
# exits 10 and Uninstall-LocalGateway.ps1 preserves the distro, while an absent
# receipt with the Store package unregistered exits 0 and authorizes
# wsl --unregister. Deleting it as a matter of routine would hand a later
# uninstall permission to destroy a gateway it should have kept.
$CompletionReceiptFile = 'completed.dpapi'

# Files the migration detector requires in the install directory. Several of
# these also live in scripts/ in this repository; the installer copies them into
# the install directory as migration payload, so these are install-dir names,
# not repository paths.
$RequiredPayload = @(
    'OpenClaw.Tray.WinUI.exe',
    'unins000.exe',
    'app-identity.txt',
    'Test-InnoMigration.ps1',
    'MigrationRecordCodec.cs',
    'Uninstall-LocalGateway.ps1'
)

function Get-RegistrationKeyPath {
    $candidates = @(
        "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId",
        "HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
    )
    $found = @($candidates | Where-Object { Test-Path $_ })

    if ($found.Count -eq 0) {
        throw "No OpenClaw Companion (Inno) installation found. Install the alpha release first."
    }
    if ($found.Count -gt 1) {
        throw "Registrations exist in both registry views. The migration detector rejects this. Uninstall and reinstall once."
    }
    return $found[0]
}

function Get-StableCore {
    param([string] $Version)

    if ($Version -notmatch '^v?(\d+)\.(\d+)\.(\d+)') {
        throw "DisplayVersion '$Version' does not start with a major.minor.patch core."
    }
    return "$($Matches[1]).$($Matches[2]).$($Matches[3])"
}

function Get-StaleMigrationRecords {
    # Known record and lock names, plus the .<guid>.tmp that MigrationRecordStorage
    # leaves behind when a record write is interrupted before the final move.
    $names = @($MigrationStateFiles)
    if ($IncludeCompletionReceipt) {
        $names += $CompletionReceiptFile
    }
    return @(Get-ChildItem -LiteralPath $MigrationStateDirectory -Force -File -ErrorAction Stop |
        Where-Object { $names -contains $_.Name -or $_.Name -match '^\.[0-9a-fA-F]{32}\.tmp$' })
}

function Stop-TrayProcesses {
    $procs = @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like '*OpenClaw*' })

    if ($procs.Count -eq 0) {
        Write-Host '  no OpenClaw processes running'
        return @()
    }

    $stopping = @()
    $skipped = @()
    foreach ($p in $procs) {
        if ($PSCmdlet.ShouldProcess("PID $($p.Id) ($($p.ProcessName))", 'Stop process')) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            Write-Host "  stopped $($p.ProcessName) (PID $($p.Id))"
            $stopping += $p
        }
        else {
            # Never attempted, so its handles are certainly still open. Counting
            # it as exited would let -WhatIf and a declined -Confirm report a
            # confirmation that was never made.
            $skipped += $p
        }
    }

    # Stop-Process returns before the kernel has finished tearing the process
    # down, so its file handles can still be open. Wait for confirmed exit
    # before anything tries to delete what the app held open.
    $deadline = (Get-Date).AddSeconds(10)
    $unconfirmed = @()
    foreach ($p in $stopping) {
        $remaining = [int][Math]::Max(0, ((New-TimeSpan -End $deadline).TotalMilliseconds))
        try {
            $p.WaitForExit($remaining) | Out-Null
            if (-not $p.HasExited) { $unconfirmed += $p }
        }
        catch {
            # Access denied or an already-reaped handle. Treat as unconfirmed.
            $unconfirmed += $p
        }
    }

    foreach ($s in $unconfirmed) {
        Write-Host "  WARNING: PID $($s.Id) ($($s.ProcessName)) did not exit" -ForegroundColor Yellow
    }
    foreach ($s in $skipped) {
        Write-Host "  WARNING: PID $($s.Id) ($($s.ProcessName)) was not stopped, so its files stay locked" -ForegroundColor Yellow
    }
    return @($unconfirmed + $skipped)
}

if ($IncludeCompletionReceipt -and -not $ResetMigrationState) {
    throw '-IncludeCompletionReceipt only applies to -ResetMigrationState.'
}

# ------------------------------------------------ reset migration state ----
# Runs before the installation lookup because a reset is still useful once the
# source app is gone, which is the state a completed migration leaves behind.
if ($ResetMigrationState) {
    if ($IncludeCompletionReceipt) {
        Write-Host "Including $CompletionReceiptFile. Uninstalling the gateway after this can unregister the WSL distro." -ForegroundColor Yellow
    }

    if (-not (Test-Path -LiteralPath $MigrationStateDirectory -PathType Container)) {
        Write-Host "No migration state at $MigrationStateDirectory. Nothing to reset." -ForegroundColor Yellow
        return
    }

    # Scanned before the tray is stopped so a no-op reset does not shut the app
    # down for nothing. This result decides that and nothing else; the list
    # actually acted on is taken again once the app has exited.
    if (@(Get-StaleMigrationRecords).Count -eq 0) {
        Write-Host 'Migration state is already clear.' -ForegroundColor Green
        return
    }

    # Both lock files are held open for as long as the tray runs.
    Write-Host 'Closing the tray app...' -ForegroundColor Cyan
    $survivors = @(Stop-TrayProcesses)
    if ($survivors.Count -gt 0 -and -not $WhatIfPreference) {
        throw "Could not confirm $($survivors.Count) OpenClaw process(es) exited. Close them and re-run."
    }

    $stale = @(Get-StaleMigrationRecords)
    if ($stale.Count -eq 0) {
        Write-Host 'Migration state is already clear.' -ForegroundColor Green
        return
    }

    $removed = 0
    foreach ($item in $stale) {
        if ($PSCmdlet.ShouldProcess($item.FullName, 'Delete migration record')) {
            Remove-Item -LiteralPath $item.FullName -Force
            Write-Host "  removed $($item.Name)"
            $removed++
        }
    }

    if ($removed -eq 0) {
        return
    }

    $leftover = @(Get-StaleMigrationRecords)
    if ($leftover.Count -gt 0) {
        $names = ($leftover | ForEach-Object { $_.Name }) -join ', '
        throw "Migration state is not clear: $names remain. Close OpenClaw and re-run."
    }

    Write-Host ''
    Write-Host 'Migration state cleared.' -ForegroundColor Green
    return
}

$keyPath = Get-RegistrationKeyPath
$props = Get-ItemProperty -Path $keyPath

# ---------------------------------------------------------------- revert ----
if ($Revert) {
    $originalVersion = $props.PSObject.Properties[$BackupVersionValue]
    $originalName = $props.PSObject.Properties[$BackupNameValue]

    if (-not $originalVersion -or -not $originalName) {
        throw "No saved original label found under $keyPath. This installation was not relabelled by this script."
    }

    if ($PSCmdlet.ShouldProcess($keyPath, 'Restore original label')) {
        Set-ItemProperty $keyPath -Name DisplayVersion -Value $originalVersion.Value -Type String
        Set-ItemProperty $keyPath -Name DisplayName    -Value $originalName.Value    -Type String
        Remove-ItemProperty $keyPath -Name $BackupVersionValue
        Remove-ItemProperty $keyPath -Name $BackupNameValue
        Write-Host "Restored DisplayName and DisplayVersion to '$($originalVersion.Value)'." -ForegroundColor Green
    }
    return
}

# ---------------------------------------------------------- preflight -------
$installDir = $props.InstallLocation
if ([string]::IsNullOrWhiteSpace($installDir) -or -not (Test-Path $installDir)) {
    throw "InstallLocation '$installDir' does not exist."
}

$currentVersion = $props.DisplayVersion
if ($props.PSObject.Properties[$BackupVersionValue]) {
    throw "This installation is already relabelled (currently '$currentVersion'). Run with -Revert first."
}

if (-not $TargetVersion) {
    $TargetVersion = Get-StableCore -Version $currentVersion
}

if ($currentVersion -eq $TargetVersion) {
    Write-Host "DisplayVersion is already '$TargetVersion'. Nothing to do." -ForegroundColor Yellow
    return
}

Write-Host "Checking the installation can actually migrate..." -ForegroundColor Cyan

# The detector requires the payload. Without it the Store app says "update the
# exe" no matter what the label says, which looks identical to the block this
# script exists to bypass.
$missing = @($RequiredPayload | Where-Object { -not (Test-Path (Join-Path $installDir $_)) })
if ($missing.Count -gt 0) {
    throw "This build predates the migration payload. Missing: $($missing -join ', '). Use a 2026.9.5-alpha or later release."
}

$identity = (Get-Content (Join-Path $installDir 'app-identity.txt') -Raw).Trim()
if ($identity -ne 'release') {
    throw "Installed identity is '$identity', not 'release'. The detector only migrates release builds, not dev builds."
}

# The detector cross-checks the registry label against the binary. If these
# disagree it reports the installation as unsupported.
$exe = Join-Path $installDir 'OpenClaw.Tray.WinUI.exe'
$peVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
$expectedPe = "$TargetVersion.0"
if ($peVersion -ne $expectedPe) {
    throw "Binary is stamped FileVersion $peVersion but '$TargetVersion' requires $expectedPe. Pick a -TargetVersion matching the binary."
}

Write-Host "  payload present, identity release, binary $peVersion" -ForegroundColor Green

# Migration capture requires every entry under gateways\ to be a directory, but
# native gateway setup writes native-setup-draft.json into that same folder. A
# stray file there fails preparation with "Migration source has an unexpected
# file type", which names neither the folder nor the file.
$gatewaysDir = Join-Path $env:APPDATA 'OpenClawTray\gateways'
if (Test-Path $gatewaysDir) {
    $blocking = @(Get-ChildItem $gatewaysDir -Force | Where-Object {
        -not $_.PSIsContainer -or $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)
    })

    if ($blocking.Count -gt 0) {
        $names = ($blocking | ForEach-Object { $_.Name }) -join ', '
        if (-not $MoveBlockingFiles) {
            throw @"
Migration will fail: $gatewaysDir contains non-directory entries ($names).
Capture requires that folder to hold only gateway identity directories.
Re-run with -MoveBlockingFiles to move them aside, or move them yourself.
Only do this when native gateway setup is not mid-flight; a moved draft
abandons an unfinished setup.
"@
        }

        $quarantine = Join-Path $env:APPDATA "OpenClawTray\gateways-blocking-$(Get-Date -Format yyyyMMdd-HHmmss)"
        if ($PSCmdlet.ShouldProcess($names, "Move out of gateways folder")) {
            New-Item -ItemType Directory -Path $quarantine -Force | Out-Null
            foreach ($item in $blocking) { Move-Item $item.FullName (Join-Path $quarantine $item.Name) -Force }
            Write-Host "  moved $names to $quarantine" -ForegroundColor Yellow
        }
    }
    else {
        Write-Host '  gateways folder clean' -ForegroundColor Green
    }
}

# ------------------------------------------------------------- relabel ------
Write-Host 'Closing the tray app (migration fails while it runs)...' -ForegroundColor Cyan
Stop-TrayProcesses | Out-Null

$newName = "OpenClaw Companion version $TargetVersion"

if ($PSCmdlet.ShouldProcess($keyPath, "Relabel $currentVersion -> $TargetVersion")) {
    # Saved before the overwrite so -Revert can restore the real label.
    New-ItemProperty $keyPath -Name $BackupVersionValue -Value $currentVersion   -PropertyType String -Force | Out-Null
    New-ItemProperty $keyPath -Name $BackupNameValue    -Value $props.DisplayName -PropertyType String -Force | Out-Null

    # DisplayName is validated against DisplayVersion, so both must change.
    Set-ItemProperty $keyPath -Name DisplayVersion -Value $TargetVersion -Type String
    Set-ItemProperty $keyPath -Name DisplayName    -Value $newName      -Type String

    Write-Host ''
    Write-Host "Relabelled $currentVersion -> $TargetVersion" -ForegroundColor Green
    Write-Host ''
    Write-Host 'Next steps:'
    Write-Host '  1. Launch the Store version of OpenClaw.'
    Write-Host '  2. Choose Migrate on the "Move to the Store version" screen.'
    Write-Host "  3. When prompted, uninstall only `"$newName`"."
    Write-Host '  4. Return to the Store app and choose Retry to finish.'
    Write-Host ''
    Write-Host 'If you see "could not exclude source processes across sessions",'
    Write-Host 'the tray app restarted. Close it and choose Retry.'
    Write-Host ''
    Write-Host 'Finish the flow or stop at the consent screen. Stopping after' -ForegroundColor Yellow
    Write-Host 'Migrate leaves the old app unable to start.' -ForegroundColor Yellow
}
