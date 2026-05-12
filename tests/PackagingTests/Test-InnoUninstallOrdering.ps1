<#
.SYNOPSIS
    Packaging test — verifies that Inno Setup's [UninstallRun] entry for
    Uninstall-LocalGateway.ps1 runs BEFORE {app} directory deletion.

.DESCRIPTION
    WHAT THIS TEST VERIFIES
    -----------------------
    RubberDucky finding 8 requires a packaging test that proves the script at
    {app}\Uninstall-LocalGateway.ps1 can run (and does run) BEFORE Inno Setup
    deletes the {app}\ directory during a silent uninstall.

    HOW IT WORKS
    ------------
    1. [BUILD]    Require a pre-built Inno installer (.exe) via -InstallerPath, or
                  attempt to locate one in the expected build output path.
    2. [INSTALL]  Run the installer silently to a temp prefix directory.
    3. [VERIFY]   Assert that {app}\OpenClaw.Tray.WinUI.exe and
                  {app}\Uninstall-LocalGateway.ps1 both exist post-install.
    4. [UNINSTALL] Run unins000.exe /VERYSILENT /LOG=<logfile>.
    5. [PARSE LOG] Grep the Inno uninstall log for:
                   a) Evidence that Uninstall-LocalGateway.ps1 was invoked (or
                      that the [UninstallRun] powershell entry ran).
                   b) Evidence that {app}\ directory was deleted.
                   c) Ordering: (a) appears BEFORE (b) in the log (by line number).
    6. [CLEANUP]  Remove temp install directory and any WSL residual state created
                  by the test.  (A fresh Inno install with no real gateway means
                  no WSL distro is ever registered, so WSL cleanup is a no-op.)
    7. [VERDICT]
                  PASS = files existed post-install AND hook line found before dir-
                         deletion line in the uninstall log.
                  FAIL = any of: files missing post-install, hook did not run,
                         hook ran AFTER directory deletion, or uninstall crashed.
                  SKIP = no Inno installer available at the expected/given path.

    NOTES ON INNO LOG FORMAT
    ------------------------
    When Inno runs with /LOG=<path> it writes a plain-text log with entries like:
        Log opened. (YYYY-MM-DD)
        ...
        -- Run entry #0: Filename: powershell.exe ...Uninstall-LocalGateway.ps1...
        ...
        Dir: C:\...\OpenClawTray (directory): deleted.
    Line ordering is the authoritative source of truth for the ordering check.

.PARAMETER InstallerPath
    Absolute path to the Inno-produced installer EXE.  If omitted the test
    searches standard build-output locations (publish-x64\installer\,
    Output\OpenClawTray-Setup-x64.exe).  If still not found the test exits
    with SKIP.

.PARAMETER TempInstallDir
    Base directory under which a unique per-run subdirectory is created for
    the test installation.  Defaults to $env:TEMP\InnoOrderingTest.
    The test cleans up this directory after completion (pass or fail).

.PARAMETER KeepTempDir
    When set, do NOT remove the temp install directory after the test.
    Use for post-mortem investigation of a FAIL result.

.PARAMETER OutputDir
    Directory to write test artifacts (log, verdict.json, summary.md).
    Defaults to .\packaging-test-output\<utc-timestamp>\.

.EXAMPLE
    # Typical run (will locate installer automatically):
    .\Test-InnoUninstallOrdering.ps1

.EXAMPLE
    # Explicit installer path:
    .\Test-InnoUninstallOrdering.ps1 -InstallerPath C:\build\OpenClawTray-Setup-x64.exe

.EXAMPLE
    # Keep temp dir for debugging:
    .\Test-InnoUninstallOrdering.ps1 -InstallerPath C:\build\OpenClawTray-Setup-x64.exe -KeepTempDir

.NOTES
    Date:   2026-05-07
    Author: Bostick (Tester / Quality / Validation)
    Branch: feat/wsl-gateway-uninstall

    Style mirrors validate-wsl-gateway-uninstall.ps1:
      - Set-StrictMode -Version Latest
      - $ErrorActionPreference = 'Stop'
      - Structured step logging
      - Stops processes by PID only
      - No \\wsl$ or \\wsl.localhost paths

    EXIT CODES
    ----------
    0  PASS   Ordering confirmed: hook ran, app dir deleted after.
    1  FAIL   Ordering wrong, files missing, hook didn't run, or uninstall crashed.
    2  SKIP   No installer available; test cannot run on this machine.
    3  ERROR  Unexpected script error.
#>

[CmdletBinding()]
param(
    [string]$InstallerPath = "",
    [string]$TempInstallDir = "",
    [switch]$KeepTempDir,
    [string]$OutputDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Exit-code sentinels
# ---------------------------------------------------------------------------
$EXIT_PASS  = 0
$EXIT_FAIL  = 1
$EXIT_SKIP  = 2
$EXIT_ERROR = 3

# ---------------------------------------------------------------------------
# Script-level state
# ---------------------------------------------------------------------------
$script:steps   = [System.Collections.Generic.List[object]]::new()
$script:verdict = 'UNKNOWN'
$utcStamp       = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmssZ")

if ([string]::IsNullOrEmpty($OutputDir)) {
    $OutputDir = Join-Path (Get-Location) "packaging-test-output\$utcStamp"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# ---------------------------------------------------------------------------
# Logging helpers (mirror validate-wsl-gateway patterns)
# ---------------------------------------------------------------------------
function Add-Step {
    param(
        [string]$Name,
        [string]$Status,   # Passed | Failed | Skipped | Warning | Info
        [string]$Message,
        [hashtable]$Data = @{}
    )
    $entry = [ordered]@{
        name      = $Name
        status    = $Status
        message   = $Message
        data      = $Data
        timestamp = (Get-Date).ToString("o")
    }
    $script:steps.Add($entry)

    $ts    = (Get-Date).ToString("HH:mm:ss")
    $color = switch ($Status) {
        "Passed"  { "Green"   }
        "Failed"  { "Red"     }
        "Skipped" { "DarkGray" }
        "Warning" { "Yellow"  }
        "Info"    { "Cyan"    }
        default   { "White"   }
    }
    Write-Host "[$ts] [$Status] $Name — $Message" -ForegroundColor $color
}

function Write-Info {
    param([string]$Message)
    $ts = (Get-Date).ToString("HH:mm:ss")
    Write-Host "[$ts]   $Message" -ForegroundColor DarkCyan
}

# ---------------------------------------------------------------------------
# Write verdict JSON + summary MD
# ---------------------------------------------------------------------------
function Write-Results {
    param(
        [string]$Verdict,
        [string]$Notes = "",
        [int]$ExitCode = $EXIT_FAIL
    )

    $verdictData = [ordered]@{
        verdict      = $Verdict
        exit_code    = $ExitCode
        notes        = $Notes
        started_at   = $script:startedAt
        finished_at  = (Get-Date).ToString("o")
        installer    = $script:installerPath
        temp_dir     = $script:tempInstallPath
        output_dir   = $OutputDir
        steps        = @($script:steps)
    }

    $verdictPath = Join-Path $OutputDir "verdict.json"
    $verdictData | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $verdictPath -Encoding UTF8

    $summaryPath = Join-Path $OutputDir "summary.md"
    $lines = @(
        "# Inno Uninstall Ordering Test",
        "",
        "| Field     | Value |",
        "|-----------|-------|",
        "| Verdict   | $Verdict |",
        "| ExitCode  | $ExitCode |",
        "| Installer | $($script:installerPath) |",
        "| OutputDir | $OutputDir |",
        "| Date      | 2026-05-07 |",
        "",
        "## Notes", "",
        $Notes, "",
        "## Steps", ""
    )
    foreach ($s in $script:steps) {
        $lines += "- [$($s.status)] $($s.name): $($s.message)"
    }
    $lines | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    $verdictColor = switch ($Verdict) {
        "PASS"    { "Green"  }
        "SKIP"    { "Cyan"   }
        "FAIL"    { "Red"    }
        "ERROR"   { "Red"    }
        default   { "Yellow" }
    }
    Write-Host ""
    Write-Host "════════════════════════════════════════" -ForegroundColor $verdictColor
    Write-Host "  VERDICT  : $Verdict"                    -ForegroundColor $verdictColor
    Write-Host "  ExitCode : $ExitCode"                   -ForegroundColor $verdictColor
    Write-Host "  Output   : $OutputDir"                  -ForegroundColor $verdictColor
    Write-Host "════════════════════════════════════════" -ForegroundColor $verdictColor
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Installer locator
# ---------------------------------------------------------------------------
function Find-Installer {
    # Caller-supplied hint
    if (-not [string]::IsNullOrEmpty($InstallerPath) -and (Test-Path -LiteralPath $InstallerPath)) {
        return $InstallerPath
    }

    # Script is in tests\PackagingTests\ → repo root is 3 levels up
    $repoRoot   = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
    $candidates = @(
        (Join-Path $repoRoot "Output\OpenClawTray-Setup-x64.exe"),
        (Join-Path $repoRoot "installer-output\OpenClawTray-Setup-x64.exe"),
        (Join-Path $repoRoot "publish-x64\installer\OpenClawTray-Setup-x64.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }

    # Search Output/ recursively for any matching file
    foreach ($searchRoot in @((Join-Path $repoRoot "Output"), (Join-Path $repoRoot "installer-output"))) {
        if (Test-Path -LiteralPath $searchRoot) {
            $found = Get-ChildItem -LiteralPath $searchRoot -Recurse -Filter "OpenClawTray-Setup*.exe" -ErrorAction SilentlyContinue |
                     Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($found) { return $found.FullName }
        }
    }

    return $null
}

# ---------------------------------------------------------------------------
# Parse Inno uninstall log for ordering evidence
# ---------------------------------------------------------------------------
function Test-UninstallLogOrdering {
    param([string]$LogPath)

    if (-not (Test-Path -LiteralPath $LogPath)) {
        return [ordered]@{
            log_found            = $false
            hook_line_index      = -1
            dir_delete_line_index = -1
            hook_ran             = $false
            dir_deleted          = $false
            ordering_correct     = $false
            notes                = "Log file not found: $LogPath"
        }
    }

    $lines = Get-Content -LiteralPath $LogPath -Encoding UTF8 -ErrorAction SilentlyContinue

    $hookIdx    = -1
    $dirDelIdx  = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        # Hook evidence: Inno logs [UninstallRun] execution in multiple ways:
        #   "-- Run entry #0:"  (start of run-entry block)
        #   "Executing: powershell.exe" ... "Uninstall-LocalGateway" (run detail)
        #   "Process exit code: 0" (completion)
        # We key on the first mention of the script name in the run section.
        if ($hookIdx -eq -1) {
            if ($line -match 'Uninstall-LocalGateway' -or
                $line -match 'UninstallLocalGateway' -or
                ($line -match 'Run entry' -and $line -match 'powershell') -or
                ($line -match 'Exec:.*powershell.*Uninstall') -or
                ($line -match 'StatusMsg.*Removing local WSL gateway')) {
                $hookIdx = $i
            }
        }

        # Directory deletion evidence:
        #   "Dir: C:\...\OpenClawTray (directory): deleted."
        #   "Deleting directory: C:\..."
        if ($dirDelIdx -eq -1) {
            if (($line -match '(?i)deleting directory') -or
                ($line -match '(?i)Dir:.*directory.*delet')) {
                # Ensure it's the {app} directory, not a subdirectory cleanup
                if ($line -match 'OpenClawTray' -or $line -match [regex]::Escape($script:appDirPattern)) {
                    $dirDelIdx = $i
                }
            }
        }
    }

    $hookRan       = ($hookIdx   -ge 0)
    $dirDeleted    = ($dirDelIdx -ge 0)
    $orderingOk    = $hookRan -and $dirDeleted -and ($hookIdx -lt $dirDelIdx)

    return [ordered]@{
        log_found             = $true
        log_line_count        = $lines.Count
        hook_line_index       = $hookIdx
        hook_line_text        = if ($hookIdx -ge 0) { $lines[$hookIdx] } else { "" }
        dir_delete_line_index = $dirDelIdx
        dir_delete_line_text  = if ($dirDelIdx -ge 0) { $lines[$dirDelIdx] } else { "" }
        hook_ran              = $hookRan
        dir_deleted           = $dirDeleted
        ordering_correct      = $orderingOk
        notes                 = if ($orderingOk) {
                                    "hook at line $hookIdx < dir-delete at line $dirDelIdx — ordering CORRECT"
                                } elseif (-not $hookRan) {
                                    "hook entry not found in log — [UninstallRun] may not have run"
                                } elseif (-not $dirDeleted) {
                                    "dir-delete entry not found in log — check Inno verbosity"
                                } else {
                                    "ORDERING WRONG: hook at line $hookIdx >= dir-delete at line $dirDelIdx"
                                }
    }
}

# ---------------------------------------------------------------------------
# WSL residual cleanup (no-op for a clean install with no gateway)
# ---------------------------------------------------------------------------
function Invoke-WslCleanupCheck {
    $wslLines = @()
    try {
        $raw = & wsl --list --quiet 2>&1
        $wslLines = ($raw | Out-String) -split "`r?`n" |
                    ForEach-Object { ($_ -replace '\x00', '').Trim() } |
                    Where-Object { $_ }
    }
    catch { }

    $openClawDistros = @($wslLines | Where-Object { $_ -like '*OpenClawGateway*' })
    if ($openClawDistros.Count -gt 0) {
        Add-Step "wsl-cleanup-check" "Warning" "$($openClawDistros.Count) OpenClawGateway distro(s) still registered after uninstall.  Unexpected for a fresh-install test." @{
            distros = $openClawDistros
        }
    }
    else {
        Add-Step "wsl-cleanup-check" "Passed" "No OpenClawGateway WSL distros registered (expected for a fresh-install test)."
    }
}

# ---------------------------------------------------------------------------
# MAIN
# ---------------------------------------------------------------------------
$script:startedAt      = (Get-Date).ToString("o")
$script:installerPath  = ""
$script:tempInstallPath = ""
$script:appDirPattern  = ""

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   Test-InnoUninstallOrdering.ps1  (2026-05-07)              ║" -ForegroundColor Cyan
Write-Host "║   Verifies [UninstallRun] hook runs BEFORE {app} deletion   ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  OutputDir : $OutputDir"
Write-Host ""

$exitCode = $EXIT_ERROR

try {

    # =====================================================================
    # STEP 1 — Locate installer
    # =====================================================================
    $foundInstaller = Find-Installer
    if ([string]::IsNullOrEmpty($foundInstaller)) {
        Add-Step "locate-installer" "Skipped" "No Inno installer found.  Pass -InstallerPath to specify one explicitly." @{
            searchedPaths = @("Output\OpenClawTray-Setup-x64.exe",
                              "installer-output\OpenClawTray-Setup-x64.exe",
                              "publish-x64\installer\OpenClawTray-Setup-x64.exe")
        }
        Write-Info "SKIP: No installer available on this machine.  Build the installer first or use -InstallerPath."
        Write-Results -Verdict "SKIP" -ExitCode $EXIT_SKIP `
            -Notes "Installer not found.  Build with 'iscc installer.iss' or pass -InstallerPath."
        exit $EXIT_SKIP
    }

    $script:installerPath = $foundInstaller
    Add-Step "locate-installer" "Passed" "Installer found: $foundInstaller"
    Write-Info "Installer: $foundInstaller"

    # =====================================================================
    # STEP 2 — Create a temp install prefix
    # =====================================================================
    if ([string]::IsNullOrEmpty($TempInstallDir)) {
        $TempInstallDir = Join-Path $env:TEMP "InnoOrderingTest"
    }
    $runId           = [System.Guid]::NewGuid().ToString("N").Substring(0, 8)
    $tempInstallPath = Join-Path $TempInstallDir "run-$runId"
    New-Item -ItemType Directory -Force -Path $tempInstallPath | Out-Null
    $script:tempInstallPath  = $tempInstallPath
    $script:appDirPattern    = $tempInstallPath  # Inno will install to this dir

    Add-Step "create-temp-dir" "Passed" "Temp install prefix: $tempInstallPath"
    Write-Info "Temp install dir: $tempInstallPath"

    # =====================================================================
    # STEP 3 — Silent install
    # =====================================================================
    Write-Info "Running silent install..."
    $installLog    = Join-Path $OutputDir "install.log"
    $installArgs   = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
                       "/DIR=$tempInstallPath", "/LOG=$installLog")

    try {
        $proc = Start-Process -FilePath $foundInstaller -ArgumentList $installArgs `
                              -Wait -PassThru -WindowStyle Hidden
        $installExitCode = $proc.ExitCode
        Add-Step "silent-install" "Passed" "Installer exited $installExitCode." @{
            installerPath = $foundInstaller
            installDir    = $tempInstallPath
            logPath       = $installLog
            exitCode      = $installExitCode
        }
        Write-Info "Install exit code: $installExitCode"
    }
    catch {
        Add-Step "silent-install" "Failed" "Installer threw: $($_.Exception.Message)"
        Write-Results -Verdict "FAIL" -ExitCode $EXIT_FAIL `
            -Notes "Silent install threw an exception: $($_.Exception.Message)"
        exit $EXIT_FAIL
    }

    # =====================================================================
    # STEP 4 — Verify post-install file presence
    # =====================================================================
    $exePath          = Join-Path $tempInstallPath "OpenClaw.Tray.WinUI.exe"
    $hookScriptPath   = Join-Path $tempInstallPath "Uninstall-LocalGateway.ps1"

    $exeExists        = Test-Path -LiteralPath $exePath
    $hookScriptExists = Test-Path -LiteralPath $hookScriptPath

    if (-not $exeExists -or -not $hookScriptExists) {
        Add-Step "verify-post-install-files" "Failed" "Expected files missing post-install." @{
            "OpenClaw.Tray.WinUI.exe exists"    = $exeExists
            "Uninstall-LocalGateway.ps1 exists" = $hookScriptExists
        }
        Write-Results -Verdict "FAIL" -ExitCode $EXIT_FAIL `
            -Notes "Post-install file check failed.  EXE=$exeExists  Hook=$hookScriptExists"
        exit $EXIT_FAIL
    }

    Add-Step "verify-post-install-files" "Passed" "Both required files exist post-install." @{
        exePath        = $exePath
        hookScriptPath = $hookScriptPath
    }

    # =====================================================================
    # STEP 5 — Locate unins000.exe
    # =====================================================================
    $uninsExe = Join-Path $tempInstallPath "unins000.exe"
    if (-not (Test-Path -LiteralPath $uninsExe)) {
        # Inno may produce unins001.exe etc. if a previous install left a unins000.
        $uninsExe = Get-ChildItem -LiteralPath $tempInstallPath -Filter "unins*.exe" `
                         -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1 |
                    ForEach-Object { $_.FullName }
        if ([string]::IsNullOrEmpty($uninsExe)) {
            Add-Step "locate-uninstaller" "Failed" "unins000.exe not found in $tempInstallPath"
            Write-Results -Verdict "FAIL" -ExitCode $EXIT_FAIL -Notes "Inno uninstaller not found."
            exit $EXIT_FAIL
        }
    }
    Add-Step "locate-uninstaller" "Passed" "Uninstaller: $uninsExe"

    # =====================================================================
    # STEP 6 — Silent uninstall with log capture
    # =====================================================================
    $uninstallLog  = Join-Path $OutputDir "uninstall.log"
    $uninstallArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
                       "/LOG=$uninstallLog")

    Write-Info "Running silent uninstall..."
    try {
        $proc2           = Start-Process -FilePath $uninsExe -ArgumentList $uninstallArgs `
                                         -Wait -PassThru -WindowStyle Hidden
        $uninstallExitCode = $proc2.ExitCode
        Add-Step "silent-uninstall" "Passed" "Uninstaller exited $uninstallExitCode." @{
            uninsExe       = $uninsExe
            logPath        = $uninstallLog
            exitCode       = $uninstallExitCode
        }
        Write-Info "Uninstall exit code: $uninstallExitCode"
    }
    catch {
        Add-Step "silent-uninstall" "Failed" "Uninstaller threw: $($_.Exception.Message)"
        Write-Results -Verdict "FAIL" -ExitCode $EXIT_FAIL `
            -Notes "Silent uninstall threw: $($_.Exception.Message)"
        exit $EXIT_FAIL
    }

    # =====================================================================
    # STEP 7 — Parse log: verify hook ran AND ordering is correct
    # =====================================================================
    Write-Info "Parsing uninstall log for ordering evidence..."
    $ordering = Test-UninstallLogOrdering -LogPath $uninstallLog

    # Save ordering analysis
    $orderingPath = Join-Path $OutputDir "ordering-analysis.json"
    $ordering | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $orderingPath -Encoding UTF8

    $orderingStatus = if ($ordering.ordering_correct) { "Passed" } `
                      elseif (-not $ordering.log_found) { "Warning" } `
                      else { "Failed" }

    Add-Step "log-ordering-check" $orderingStatus $ordering.notes @{
        log_path              = $uninstallLog
        hook_line             = $ordering.hook_line_index
        hook_line_text        = $ordering.hook_line_text
        dir_delete_line       = $ordering.dir_delete_line_index
        dir_delete_line_text  = $ordering.dir_delete_line_text
        ordering_correct      = $ordering.ordering_correct
        analysis_file         = $orderingPath
    }

    # =====================================================================
    # STEP 7b — Supplemental: verify {app} dir was deleted after uninstall
    # =====================================================================
    $appDirGone = -not (Test-Path -LiteralPath $tempInstallPath)
    if ($appDirGone) {
        Add-Step "verify-app-dir-deleted" "Passed" "{app} directory removed by uninstaller (expected)."
    }
    else {
        Add-Step "verify-app-dir-deleted" "Warning" "{app} directory still exists after uninstall: $tempInstallPath"
    }

    # =====================================================================
    # STEP 8 — WSL cleanup check
    # =====================================================================
    Invoke-WslCleanupCheck

    # =====================================================================
    # STEP 9 — Final verdict
    # =====================================================================

    # Determine if the log ordering check was conclusive.
    # If the log wasn't found or the hook was not in it, try a weaker check:
    # look for evidence in the uninstall log that Uninstall-LocalGateway.ps1 was
    # at least attempted (it may exit 0 quietly without verbose log entries).
    $hookConfirmed = $ordering.hook_ran

    if (-not $hookConfirmed -and $ordering.log_found) {
        # Secondary check: scan raw log for the script name anywhere
        $rawLog = Get-Content -LiteralPath $uninstallLog -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($rawLog -match 'Uninstall-LocalGateway') {
            Add-Step "secondary-hook-check" "Passed" "Secondary scan found 'Uninstall-LocalGateway' in uninstall log."
            $hookConfirmed = $true
        }
        else {
            Add-Step "secondary-hook-check" "Warning" "'Uninstall-LocalGateway' not found anywhere in uninstall log.  The [UninstallRun] entry may not have been executed."
        }
    }
    elseif (-not $ordering.log_found) {
        Add-Step "secondary-hook-check" "Warning" "Cannot perform secondary check: uninstall log not found."
    }

    # Determine pass/fail/skip
    if (-not $ordering.log_found) {
        # No log = can't confirm ordering; FAIL with guidance
        $finalVerdict = "FAIL"
        $notes        = "Uninstall log not produced.  Ensure Inno's /LOG= switch works for this installer version."
        $exitCode     = $EXIT_FAIL
    }
    elseif ($ordering.ordering_correct) {
        $finalVerdict = "PASS"
        $notes        = $ordering.notes
        $exitCode     = $EXIT_PASS
    }
    elseif (-not $hookConfirmed) {
        $finalVerdict = "FAIL"
        $notes        = "Hook not confirmed in log.  [UninstallRun] entry may be missing or not triggered."
        $exitCode     = $EXIT_FAIL
    }
    else {
        $finalVerdict = "FAIL"
        $notes        = $ordering.notes
        $exitCode     = $EXIT_FAIL
    }

    $script:verdict = $finalVerdict
    Write-Results -Verdict $finalVerdict -ExitCode $exitCode -Notes $notes

}
catch {
    $errMsg = $_.Exception.Message
    Add-Step "unhandled-error" "Failed" $errMsg
    Write-Host "ERROR: $errMsg" -ForegroundColor Red
    Write-Results -Verdict "ERROR" -ExitCode $EXIT_ERROR -Notes $errMsg
    $exitCode = $EXIT_ERROR
}
finally {
    # Cleanup temp install directory unless -KeepTempDir or it was already removed by uninstall
    if (-not $KeepTempDir -and -not [string]::IsNullOrEmpty($script:tempInstallPath)) {
        if (Test-Path -LiteralPath $script:tempInstallPath) {
            try {
                Remove-Item -LiteralPath $script:tempInstallPath -Recurse -Force -ErrorAction SilentlyContinue
                Write-Info "Temp install dir removed: $($script:tempInstallPath)"
            }
            catch {
                Write-Info "Warning: could not remove temp dir: $($script:tempInstallPath) — $($_.Exception.Message)"
            }
        }
    }

    # Also remove the parent temp base dir if it was auto-created and is now empty
    if (-not $KeepTempDir -and -not [string]::IsNullOrEmpty($TempInstallDir)) {
        if (Test-Path -LiteralPath $TempInstallDir) {
            $remaining = @(Get-ChildItem -LiteralPath $TempInstallDir -ErrorAction SilentlyContinue)
            if ($remaining.Count -eq 0) {
                Remove-Item -LiteralPath $TempInstallDir -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

exit $exitCode
