<#
.SYNOPSIS
    Dev-loop helper: kill → backup/wipe state → optionally wipe WSL distro → build x64 → (optionally) launch tray.

.DESCRIPTION
    Consolidates the full dev-reset cycle used during OpenClaw tray development.
    Idempotent: no error if nothing is running, state dirs are absent, or the WSL
    distro is not registered.

    Process kills are always by PID (Stop-Process -Id). Name-based kills are
    forbidden in this repo.

    WSL file operations use 'wsl bash -c' — never \\wsl$\ paths (which trigger
    Windows permission prompts via the 9P protocol).

.PARAMETER WipeWslDistro
    Also unregister the OpenClawGateway WSL distro (wsl --unregister).
    Default: off (preserve the distro).

.PARAMETER CaptureDir
    If set, exports OPENCLAW_VISUAL_TEST=1 and OPENCLAW_VISUAL_TEST_DIR=<path>
    before launching the tray so the app auto-captures screenshots.

.PARAMETER SkipBuild
    Skip the 'dotnet build' step. Useful when you have just built.

.PARAMETER DontLaunch
    Reset and (optionally) build, but do not launch the tray.

.PARAMETER WorktreePath
    Root of the git worktree to operate in.
    Default: result of 'git rev-parse --show-toplevel' in the current directory.

.PARAMETER NoBackup
    Instead of backing up state dirs to TEMP, delete them directly.
    Faster, but no rollback.

.EXAMPLE
    .\scripts\dev-reset-rebuild-launch.ps1
    Standard reset + rebuild + launch (no WSL wipe, no capture).

.EXAMPLE
    .\scripts\dev-reset-rebuild-launch.ps1 -WipeWslDistro
    Full clean slate: also unregister the OpenClawGateway WSL distro.

.EXAMPLE
    .\scripts\dev-reset-rebuild-launch.ps1 -DontLaunch
    Reset + build only (useful before testing manually).

.EXAMPLE
    .\scripts\dev-reset-rebuild-launch.ps1 -CaptureDir .\visual-test-output\my-test
    Reset + build + launch with OPENCLAW_VISUAL_TEST capture enabled.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$WipeWslDistro,
    [string]$CaptureDir = "",
    [switch]$SkipBuild,
    [switch]$DontLaunch,
    [string]$WorktreePath = "",
    [switch]$NoBackup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ─── Resolve worktree path ────────────────────────────────────────────────────

if ([string]::IsNullOrWhiteSpace($WorktreePath)) {
    $gitTop = & git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitTop)) {
        Write-Error "Cannot resolve worktree path: not inside a git repository and -WorktreePath was not supplied."
        exit 1
    }
    $WorktreePath = $gitTop.Trim()
}
$WorktreePath = (Resolve-Path -LiteralPath $WorktreePath).Path

# ─── Constants ────────────────────────────────────────────────────────────────

$DistroName      = "OpenClawGateway"
$TrayProject     = Join-Path $WorktreePath "src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj"
$AppDataDir      = Join-Path $env:APPDATA "OpenClawTray"
$LocalAppDataDir = Join-Path $env:LOCALAPPDATA "OpenClawTray"
$timestamp       = (Get-Date).ToString("yyyy-MM-ddTHH-mm-ss")
$BackupRoot      = Join-Path $env:TEMP "openclaw-test-backup-$timestamp"

# ─── Summary state ────────────────────────────────────────────────────────────

$summary = [ordered]@{
    backupPath  = $null
    distroState = "not-checked"
    buildResult = "skipped"
    launchPid   = $null
}

# ─── Helpers ──────────────────────────────────────────────────────────────────

function Write-Step {
    param([string]$Icon, [string]$Message)
    Write-Host "  $Icon  $Message"
}
function Write-OK   { param([string]$m) Write-Step "✓" $m }
function Write-Skip { param([string]$m) Write-Step "-" $m }
function Write-Fail { param([string]$m) Write-Step "x" $m }

function Get-OpenClawProcesses {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "OpenClaw*" })
}

function Get-WslDistros {
    $out = & wsl.exe --list --quiet 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $out) { return @() }
    @($out | ForEach-Object { ($_ -replace "`0", "").Trim() } | Where-Object { $_ })
}

# ─── Banner ───────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "============================================================"
Write-Host "     OpenClaw Dev Loop -- Reset / Rebuild / Launch"
Write-Host "============================================================"
Write-Host "  Timestamp    : $timestamp"
Write-Host "  WorktreePath : $WorktreePath"
Write-Host "  WipeWslDistro: $WipeWslDistro   SkipBuild: $SkipBuild   DontLaunch: $DontLaunch"
Write-Host "  NoBackup     : $NoBackup   CaptureDir: $(if ($CaptureDir) { $CaptureDir } else { '(none)' })"
if ($WhatIfPreference) {
    Write-Host "  *** WHATIF MODE -- no state will be changed ***"
}
Write-Host ""

# =============================================================================
# STEP 1 -- Kill OpenClaw* processes (by PID; name-based kills are forbidden)
# =============================================================================

Write-Host "STEP 1: Kill OpenClaw* processes"
$procs = @(Get-OpenClawProcesses)

if ($procs.Count -eq 0) {
    Write-Skip "No OpenClaw* processes running"
}
else {
    foreach ($p in $procs) {
        if ($PSCmdlet.ShouldProcess("PID $($p.Id) ($($p.ProcessName))", "Stop-Process -Id")) {
            try {
                Stop-Process -Id $p.Id -Force
                Write-OK "Stopped PID $($p.Id) ($($p.ProcessName))"
            }
            catch {
                Write-Fail "Failed to stop PID $($p.Id): $_"
                exit 1
            }
        }
        else {
            Write-Skip "WhatIf: would stop PID $($p.Id) ($($p.ProcessName))"
        }
    }
    if (-not $WhatIfPreference) {
        Start-Sleep -Milliseconds 500  # brief pause for file-lock release
    }
}

# =============================================================================
# STEP 2 -- Backup or wipe tray state dirs
# =============================================================================

Write-Host ""
Write-Host "STEP 2: $(if ($NoBackup) { 'Wipe' } else { 'Backup' }) tray state dirs"

function Invoke-StateDirReset {
    param([string]$Path, [string]$Label)

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Skip "$Label not present -- nothing to do"
        return
    }

    if ($NoBackup) {
        if ($PSCmdlet.ShouldProcess($Path, "Remove-Item -Recurse -Force")) {
            Remove-Item -LiteralPath $Path -Recurse -Force
            Write-OK "Deleted $Label ($Path)"
        }
        else {
            Write-Skip "WhatIf: would delete $Label ($Path)"
        }
    }
    else {
        $dest = Join-Path $BackupRoot $Label
        if ($PSCmdlet.ShouldProcess($Path, "Copy-Item to backup then Remove-Item")) {
            New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
            Copy-Item -LiteralPath $Path -Destination $dest -Recurse -Force
            Remove-Item -LiteralPath $Path -Recurse -Force
            Write-OK "Backed up $Label --> $dest"
            $script:summary.backupPath = $BackupRoot
        }
        else {
            Write-Skip "WhatIf: would backup $Label --> $dest, then remove source"
            $script:summary.backupPath = "(whatif) $BackupRoot"
        }
    }
}

Invoke-StateDirReset -Path $AppDataDir      -Label "AppData_OpenClawTray"
Invoke-StateDirReset -Path $LocalAppDataDir -Label "LocalAppData_OpenClawTray"

# =============================================================================
# STEP 3 -- Optionally wipe the WSL distro
# =============================================================================

Write-Host ""
Write-Host "STEP 3: WSL distro ($DistroName)"

$distros      = @(Get-WslDistros)
$distroExists = $distros -contains $DistroName

if (-not $WipeWslDistro) {
    Write-Skip "-WipeWslDistro not set -- preserving $DistroName"
    $summary.distroState = if ($distroExists) { "preserved" } else { "absent" }
}
elseif (-not $distroExists) {
    Write-Skip "$DistroName is not registered -- nothing to unregister"
    $summary.distroState = "absent"
}
else {
    if ($PSCmdlet.ShouldProcess($DistroName, "wsl --terminate then wsl --unregister")) {
        & wsl.exe --terminate $DistroName 2>$null  # ignore exit code -- distro may already be stopped
        & wsl.exe --unregister $DistroName
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "wsl --unregister $DistroName failed (exit $LASTEXITCODE)"
            exit 1
        }
        Write-OK "Unregistered WSL distro $DistroName"
        $summary.distroState = "unregistered"
    }
    else {
        Write-Skip "WhatIf: would terminate + unregister WSL distro $DistroName"
        $summary.distroState = "(whatif) would-unregister"
    }
}

# =============================================================================
# STEP 4 -- Build x64 tray
# =============================================================================

Write-Host ""
Write-Host "STEP 4: Build x64 tray"

if ($SkipBuild) {
    Write-Skip "-SkipBuild set -- skipping dotnet build"
    $summary.buildResult = "skipped"
}
else {
    if (-not (Test-Path -LiteralPath $TrayProject)) {
        Write-Fail "Tray project not found: $TrayProject"
        exit 1
    }

    if ($PSCmdlet.ShouldProcess($TrayProject, "dotnet build -p:Platform=x64 --no-restore -v q")) {
        Write-Verbose "Running: dotnet build `"$TrayProject`" -p:Platform=x64 --no-restore -v q"
        & dotnet build $TrayProject -p:Platform=x64 --no-restore -v q
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "dotnet build failed (exit $LASTEXITCODE)"
            $summary.buildResult = "failed"
            exit 1
        }
        Write-OK "Build succeeded"
        $summary.buildResult = "succeeded"
    }
    else {
        Write-Skip "WhatIf: would run: dotnet build `"$TrayProject`" -p:Platform=x64 --no-restore -v q"
        $summary.buildResult = "(whatif) would-build"
    }
}

# =============================================================================
# STEP 5 -- Launch tray
# =============================================================================

Write-Host ""
Write-Host "STEP 5: Launch tray"

if ($DontLaunch) {
    Write-Skip "-DontLaunch set -- not launching"
}
else {
    if ($PSCmdlet.ShouldProcess($TrayProject, "dotnet run -p:Platform=x64")) {
        if ($CaptureDir) {
            $captureAbs = if ([System.IO.Path]::IsPathRooted($CaptureDir)) {
                $CaptureDir
            }
            else {
                Join-Path $WorktreePath $CaptureDir
            }
            $env:OPENCLAW_VISUAL_TEST     = "1"
            $env:OPENCLAW_VISUAL_TEST_DIR = $captureAbs
            Write-Verbose "Set OPENCLAW_VISUAL_TEST=1  OPENCLAW_VISUAL_TEST_DIR=$captureAbs"
        }

        Write-Verbose "Launching: dotnet run --project `"$TrayProject`" -p:Platform=x64"
        $launchProc = Start-Process -FilePath "dotnet" `
            -ArgumentList "run", "--project", $TrayProject, "-p:Platform=x64" `
            -PassThru -WorkingDirectory $WorktreePath
        $summary.launchPid = $launchProc.Id
        Write-OK "Tray launched (PID $($launchProc.Id))"
    }
    else {
        Write-Skip "WhatIf: would launch: dotnet run --project `"$TrayProject`" -p:Platform=x64"
        if ($CaptureDir) {
            Write-Skip "WhatIf: would also set OPENCLAW_VISUAL_TEST=1 and OPENCLAW_VISUAL_TEST_DIR=$CaptureDir"
        }
    }
}

# =============================================================================
# Summary
# =============================================================================

Write-Host ""
Write-Host "---------------------------- Summary ----------------------------"
Write-Host "  Backup path  : $(if ($summary.backupPath) { $summary.backupPath } elseif ($NoBackup) { '(deleted directly)' } else { '(nothing backed up)' })"
Write-Host "  Distro state : $($summary.distroState)"
Write-Host "  Build result : $($summary.buildResult)"
Write-Host "  Launch PID   : $(if ($summary.launchPid) { $summary.launchPid } else { '(not launched)' })"
Write-Host "-----------------------------------------------------------------"
Write-Host ""
