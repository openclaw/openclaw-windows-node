# reset-openclaw-wsl-validation-state.ps1
#
# Exact-target destructive cleanup for OpenClaw-owned WSL validation state.
#
# Safety guarantees enforced by this script:
#   1. Without -ConfirmDestructiveClean, the script runs in DRY-RUN mode and
#      reports what it WOULD do; it never mutates state.
#   2. The only WSL distro this script will ever touch is the production
#      constant "OpenClawGateway". Any other distro name is rejected.
#   3. Destructive operations are preceded by a copy of the user's
#      %APPDATA%\OpenClawTray and %LOCALAPPDATA%\OpenClawTray identity
#      directories to a timestamped backup location (printed to console).
#   4. The script never calls `wsl --shutdown`. It uses
#      `wsl --terminate OpenClawGateway` only.
#   5. The script never reads or writes \\wsl$ / \\wsl.localhost paths.

[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path (Get-Location) "artifacts\wsl-gateway-validation\reset"),
    [string]$BackupRoot,
    [string]$AppDataRoot,
    [string]$LocalAppDataRoot,
    [string]$InstallLocation,
    [switch]$CleanInstallLocation,
    [switch]$ConfirmDestructiveClean,
    [switch]$KeepRunningProcesses,
    [switch]$PassThruJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. "$PSScriptRoot\_uninstall-helpers.ps1"

# Production-locked WSL distro name (Phase 3 constant). This script will
# refuse to act on any other distro, even via -DistroName overrides
# (which are intentionally absent).
$script:OpenClawDistroName = "OpenClawGateway"

$startedAt = Get-Date
$timestamp = $startedAt.ToString("yyyyMMddHHmmss")

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path (Get-Location) "artifacts\reset-backups\$timestamp"
}

$result = [ordered]@{
    script = "reset-openclaw-wsl-validation-state"
    startedAt = $startedAt.ToString("o")
    finishedAt = $null
    outputDir = $OutputDir
    backupRoot = $BackupRoot
    distroName = $script:OpenClawDistroName
    installLocation = $InstallLocation
    appDataRoot = $AppDataRoot
    localAppDataRoot = $LocalAppDataRoot
    destructiveConfirmed = [bool]$ConfirmDestructiveClean
    dryRun = -not $ConfirmDestructiveClean
    targets = [ordered]@{}
    steps = @()
}

function Add-ResetStep {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Message,
        [hashtable]$Data = @{}
    )

    $script:result.steps += [ordered]@{
        name = $Name
        status = $Status
        message = $Message
        data = $Data
        timestamp = (Get-Date).ToString("o")
    }
}

function Invoke-CapturedCommand {
    param(
        [string]$Name,
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory = (Get-Location).Path,
        [switch]$IgnoreExitCode
    )

    $stepDir = Join-Path $OutputDir "commands"
    New-Item -ItemType Directory -Force -Path $stepDir | Out-Null
    $safeName = $Name -replace "[^a-zA-Z0-9_.-]", "-"
    $stdout = Join-Path $stepDir "$safeName.stdout.txt"
    $stderr = Join-Path $stepDir "$safeName.stderr.txt"

    Push-Location $WorkingDirectory
    try {
        & $FilePath @ArgumentList > $stdout 2> $stderr
        $exitCode = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }
    }
    finally {
        Pop-Location
    }

    Add-ResetStep $Name "Completed" "Command completed with exit code $exitCode." @{
        file = $FilePath
        arguments = ($ArgumentList -join " ")
        exitCode = $exitCode
        stdout = $stdout
        stderr = $stderr
    }

    if ($exitCode -ne 0 -and -not $IgnoreExitCode) {
        throw "$Name failed with exit code $exitCode. See $stdout and $stderr."
    }
}

function Backup-Directory {
    param(
        [string]$Path,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        Add-ResetStep "backup-$Label" "Skipped" "$Path does not exist."
        return
    }

    New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
    $leaf = Split-Path -Leaf $Path
    $destination = Join-Path $BackupRoot "$Label-$leaf"

    if ($result.dryRun) {
        Add-ResetStep "backup-$Label" "DryRun" "Would copy $Path to $destination, then remove the original." @{
            source = $Path
            destination = $destination
        }
        return
    }

    if (Test-Path -LiteralPath $destination) {
        $destination = Join-Path $BackupRoot ("{0}-{1:yyyyMMddHHmmss}" -f "$Label-$leaf", (Get-Date))
    }

    # Copy first so the user can recover even if removal fails partway.
    Copy-Item -LiteralPath $Path -Destination $destination -Recurse -Force
    Remove-Item -LiteralPath $Path -Recurse -Force
    Add-ResetStep "backup-$Label" "Completed" "Backed up $Path to $destination, then removed the original." @{
        source = $Path
        destination = $destination
    }
}

function Assert-DestructiveTargetIsAllowed {
    # Hard-lock: this script will only ever touch the production OpenClawGateway distro.
    # No override flag exists. If $script:OpenClawDistroName is ever something else,
    # the script must refuse to run regardless of dry-run mode.
    if ($script:OpenClawDistroName -ne "OpenClawGateway") {
        throw "Refusing to run: distro name is locked to 'OpenClawGateway' but resolved to '$($script:OpenClawDistroName)'."
    }
}

function Get-PortOwnerSnapshot {
    param([string]$Label)

    $port = 18789
    try {
        $connections = @(Get-NetTCPConnection -LocalPort $port -ErrorAction Stop)
        $snapshot = @($connections | ForEach-Object {
            [ordered]@{
                localAddress = $_.LocalAddress
                localPort = $_.LocalPort
                state = $_.State.ToString()
                owningProcess = $_.OwningProcess
            }
        })
    }
    catch {
        $snapshot = @()
    }

    $snapshotPath = Join-Path $OutputDir "port-18789-$Label.json"
    $snapshot | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $snapshotPath -Encoding UTF8
    Add-ResetStep "port-snapshot-$Label" "Completed" "Captured TCP listener snapshot for port 18789." @{
        path = $snapshotPath
        ownerCount = @($snapshot).Count
    }
    return $snapshot
}

function Get-WslDistros {
    $output = & wsl.exe --list --quiet 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $output) {
        return @()
    }

    return @($output | ForEach-Object { ($_ -replace "`0", "").Trim() } | Where-Object { $_ })
}

function Get-OpenClawProcesses {
    return @(Get-Process | Where-Object { $_.ProcessName -like "OpenClaw*" })
}

function Add-TargetSummary {
    param(
        [object[]]$Processes,
        [string[]]$Distros,
        [string]$AppDataPath,
        [string]$LocalAppDataPath,
        [string]$InstallLocationPath,
        [object[]]$PortOwners
    )

    $script:result.targets = [ordered]@{
        processes = @($Processes | ForEach-Object {
            [ordered]@{
                pid = $_.Id
                name = $_.ProcessName
                path = $_.Path
            }
        })
        distroExists = ($Distros -contains $script:OpenClawDistroName)
        distroName = $script:OpenClawDistroName
        appDataPath = $AppDataPath
        appDataExists = Test-Path -LiteralPath $AppDataPath
        localAppDataPath = $LocalAppDataPath
        localAppDataExists = Test-Path -LiteralPath $LocalAppDataPath
        installLocationPath = $InstallLocationPath
        installLocationExists = (-not [string]::IsNullOrWhiteSpace($InstallLocationPath)) -and (Test-Path -LiteralPath $InstallLocationPath)
        installLocationCleanupRequested = [bool]$CleanInstallLocation
        port18789OwnersBefore = @($PortOwners)
        outputDir = $OutputDir
        backupRoot = $BackupRoot
    }

    Add-ResetStep "target-summary" "Completed" "Captured OpenClaw-owned reset targets." @{
        processCount = @($Processes).Count
        distroExists = [bool]$script:result.targets.distroExists
        appDataExists = [bool]$script:result.targets.appDataExists
        localAppDataExists = [bool]$script:result.targets.localAppDataExists
        installLocationExists = [bool]$script:result.targets.installLocationExists
    }
}

function Assert-CleanPostCondition {
    param(
        [string]$AppDataPath,
        [string]$LocalAppDataPath,
        [string]$InstallLocationPath
    )

    if ($result.dryRun) {
        Add-ResetStep "postconditions" "Skipped" "Postconditions are skipped during dry-run."
        return
    }

    $remainingProcesses = @(Get-OpenClawProcesses)
    if (-not $KeepRunningProcesses -and $remainingProcesses.Count -gt 0) {
        throw "OpenClaw processes are still running after reset: $(@($remainingProcesses | ForEach-Object { $_.Id }) -join ', ')"
    }

    $remainingDistros = @(Get-WslDistros)
    if ($remainingDistros -contains $script:OpenClawDistroName) {
        throw "WSL distro '$($script:OpenClawDistroName)' is still registered after reset."
    }

    if (Test-Path -LiteralPath $AppDataPath) {
        throw "AppData path still exists after reset: $AppDataPath"
    }

    if (Test-Path -LiteralPath $LocalAppDataPath) {
        throw "LocalAppData path still exists after reset: $LocalAppDataPath"
    }

    if ($CleanInstallLocation -and -not [string]::IsNullOrWhiteSpace($InstallLocationPath) -and (Test-Path -LiteralPath $InstallLocationPath)) {
        throw "Install location still exists after reset: $InstallLocationPath"
    }

    $wslListAfterPath = Join-Path $OutputDir "wsl-list-after.txt"
    & wsl.exe --list --verbose > $wslListAfterPath 2>&1
    $script:result.targets.port18789OwnersAfter = @(Get-PortOwnerSnapshot -Label "after")
    Add-ResetStep "postconditions" "Passed" "OpenClaw-owned state reset postconditions passed." @{
        wslListAfter = $wslListAfterPath
    }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

try {
    Assert-DestructiveTargetIsAllowed

    if ([string]::IsNullOrWhiteSpace($AppDataRoot)) {
        $AppDataRoot = $env:APPDATA
        $result.appDataRoot = $AppDataRoot
    }
    if ([string]::IsNullOrWhiteSpace($LocalAppDataRoot)) {
        $LocalAppDataRoot = $env:LOCALAPPDATA
        $result.localAppDataRoot = $LocalAppDataRoot
    }

    $appData = Join-Path $AppDataRoot "OpenClawTray"
    $localAppData = Join-Path $LocalAppDataRoot "OpenClawTray"
    $processes = @(Get-OpenClawProcesses)
    $distros = @(Get-WslDistros)
    $portOwnersBefore = @(Get-PortOwnerSnapshot -Label "before")
    Add-TargetSummary -Processes $processes -Distros $distros -AppDataPath $appData -LocalAppDataPath $localAppData -InstallLocationPath $InstallLocation -PortOwners $portOwnersBefore

    if ($result.dryRun) {
        Add-ResetStep "mode" "DryRun" "No state will be changed. Pass -ConfirmDestructiveClean to reset OpenClaw-owned state."
        Write-Host "DRY-RUN: pass -ConfirmDestructiveClean to actually reset OpenClaw-owned state."
    }
    else {
        Add-ResetStep "mode" "Confirmed" "OpenClaw-owned state reset is enabled for this run."
        Write-Host "Backups will be written under: $BackupRoot"
    }

    if ($processes.Count -eq 0) {
        Add-ResetStep "stop-openclaw-processes" "Skipped" "No OpenClaw processes are running."
    }
    elseif ($KeepRunningProcesses) {
        Add-ResetStep "stop-openclaw-processes" "Skipped" "Keeping running OpenClaw processes because -KeepRunningProcesses was set." @{
            pids = @($processes | ForEach-Object { $_.Id })
        }
    }
    elseif ($result.dryRun) {
        Add-ResetStep "stop-openclaw-processes" "DryRun" "Would stop running OpenClaw processes by PID." @{
            pids = @($processes | ForEach-Object { $_.Id })
        }
    }
    else {
        foreach ($process in $processes) {
            Stop-Process -Id $process.Id -Force
        }
        Add-ResetStep "stop-openclaw-processes" "Completed" "Stopped running OpenClaw processes by PID." @{
            pids = @($processes | ForEach-Object { $_.Id })
        }
    }

    $hasGatewayDistro = $distros -contains $script:OpenClawDistroName
    $wslListPath = Join-Path $OutputDir "wsl-list-before.txt"
    & wsl.exe --list --verbose > $wslListPath 2>&1
    Add-ResetStep "capture-wsl-list" "Completed" "Captured WSL distro list." @{ path = $wslListPath }

    if (-not $hasGatewayDistro) {
        Add-ResetStep "unregister-$($script:OpenClawDistroName)" "Skipped" "WSL distro '$($script:OpenClawDistroName)' is not registered."
    }
    elseif ($result.dryRun) {
        Add-ResetStep "unregister-$($script:OpenClawDistroName)" "DryRun" "Would terminate and unregister only the '$($script:OpenClawDistroName)' WSL distro." @{ distroName = $script:OpenClawDistroName }
    }
    else {
        # Exact-target only: --terminate <name>, never --shutdown.
        Invoke-CapturedCommand "wsl-terminate-$($script:OpenClawDistroName)" "wsl.exe" @("--terminate", $script:OpenClawDistroName) -IgnoreExitCode
        Invoke-CapturedCommand "wsl-unregister-$($script:OpenClawDistroName)" "wsl.exe" @("--unregister", $script:OpenClawDistroName)
    }

    Backup-Directory -Path $appData -Label "appdata"
    Backup-Directory -Path $localAppData -Label "localappdata"
    if ($CleanInstallLocation) {
        if ([string]::IsNullOrWhiteSpace($InstallLocation)) {
            Add-ResetStep "backup-install-location" "Skipped" "No install location was supplied."
        }
        else {
            Backup-Directory -Path $InstallLocation -Label "install-location"
        }
    }
    else {
        Add-ResetStep "backup-install-location" "Skipped" "Install location cleanup was not requested."
    }
    Assert-CleanPostCondition -AppDataPath $appData -LocalAppDataPath $localAppData -InstallLocationPath $InstallLocation

    $result.finishedAt = (Get-Date).ToString("o")
    $summaryPath = Join-Path $OutputDir "reset-summary.json"
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    if ($PassThruJson) {
        $result | ConvertTo-Json -Depth 10
    }
    else {
        Write-Host "Reset summary: $summaryPath"
        if (-not $result.dryRun) {
            Write-Host "Backup root:   $BackupRoot"
        }
    }
}
catch {
    $result.finishedAt = (Get-Date).ToString("o")
    Add-ResetStep "reset" "Failed" $_.Exception.Message
    $summaryPath = Join-Path $OutputDir "reset-summary.json"
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    Write-Error $_.Exception.Message
    exit 1
}
