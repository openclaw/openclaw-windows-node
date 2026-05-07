<#
.SYNOPSIS
    Empirically determines whether OpenClawTray MSIX writes user data to real
    %APPDATA%/%LOCALAPPDATA% paths (Path A) or MSIX-virtualized package storage
    (Path B).  The verdict drives the MSIX uninstall strategy.

.DESCRIPTION
    OpenClawTray declares the runFullTrust restricted capability, which typically
    bypasses MSIX filesystem virtualization and writes to the real roaming/local
    app-data folders.  This cannot be assumed — it must be verified empirically
    before the MSIX uninstall surface is considered complete.

    VERDICT SEMANTICS
    -----------------
    PathA-OrphanRisk
        Files land in real %APPDATA%\OpenClawTray\ and/or %LOCALAPPDATA%\OpenClawTray\.
        Remove-AppxPackage does NOT clean these up.  The "Remove Local Gateway" in-tray
        button is the canonical pre-uninstall cleanup path.  A pre-removal warning banner
        (PackageHelper.IsPackaged() && setup-state.json exists) MUST ship in commit 5.

    PathB-CleanRemove
        Files land only inside %LOCALAPPDATA%\Packages\<PackageFamilyName>\.
        Remove-AppxPackage deletes them automatically.  WSL distro registration still
        requires explicit cleanup (not handled by MSIX removal).  In-tray warning banner
        is optional / informational only.

    Inconclusive
        The probe could not produce a definitive answer (no probe files found in either
        location, app launch failure, timeout, etc.).  Investigation is required before
        MSIX uninstall claims can be made.

    ## Notes for Aaron
    ==================
    Read verdict.json → field "verdict" to branch your commit-5 decisions:

    If "PathA-OrphanRisk":
      - Keep the "Remove Local Gateway" in-tray button as the canonical cleanup path.
      - MUST add in-app pre-uninstall warning banner gated on:
            PackageHelper.IsPackaged() && File.Exists(setupStatePath)
        so users are warned before removing the MSIX package.
      - The Inno uninstaller script (Uninstall-LocalGateway.ps1) targets real paths
        unconditionally — no change needed there.
      - Recovery: scripts/validate-wsl-gateway-uninstall.ps1 -Scenario Full
            -ConfirmDestructiveClean is still relevant for orphaned state.

    If "PathB-CleanRemove":
      - Remove-AppxPackage handles file-based artifact cleanup automatically.
      - MSIX section of the plan is limited to WSL distro cleanup only (steps 2-5 of
        canonical sequence: stop service, terminate, unregister distro, remove VHD dir).
      - In-app warning banner is optional.  You may still want it for WSL distro orphan
        risk (distro registration is NOT cleaned by Remove-AppxPackage in either path).
      - document in Artifact Catalog that MSIX path is path B.

    If "Inconclusive":
      - Block commit 5 MSIX claims.  Either re-run on a clean VM or defer MSIX
        validation to a tracked TODO.  Do NOT ship "MSIX removal is sufficient"
        language without a pass verdict.

    OPEN QUESTIONS FOR AARON (pre-commit-5)
    ========================================
    Q1: If -AutoSetup detection is infeasible on a given test machine (no interactive
        session, sandboxed runner), do you want to defer MSIX validation to a manual
        TODO tracked in the PR, or require a VM pre-condition before commit 5 merges?
        Default assumption: commit 5 is gated on a non-Inconclusive verdict.

    Q2: Should the in-app warning banner check PackageHelper.IsPackaged() at runtime,
        or check for APPX identity via Environment.GetEnvironmentVariable("LOCALAPPDATA")?
        The former is more robust.  Confirm the PackageHelper API is available in the
        Settings page code-behind at the time commit 5 lands.

    ⚠️  SECURITY NOTE: Do NOT run this script against a live, user-paired tray instance.
        Filesystem snapshots captured in -EvidenceDir would include settings.json
        which may contain gateway token fields.  Run only on a clean test machine or
        dedicated validation VM.

    CI ARTIFACT
    -----------
    The MSIX is produced by the build-msix CI job.  Download artifact:
        gh run download <run_id> --name openclaw-msix-win-x64 --dir ./msix-drop/
    Then pass the .msix file path to -MsixPath.

.PARAMETER MsixPath
    Absolute path to the OpenClawTray MSIX file (e.g. OpenClawTray_1.2.3.0_x64.msix)
    as produced by the build-msix CI job.  Required unless -SkipInstall is set.

.PARAMETER CertPath
    Optional path to a .cer or .pfx sideload certificate.  If provided, the cert is
    imported to Cert:\LocalMachine\TrustedPeople before Add-AppxPackage is called.

.PARAMETER EvidenceDir
    Directory where ALL captured artifacts land.  Defaults to
    .\msix-validation-evidence\<UTC-yyyyMMdd-HHmmss>\

.PARAMETER SkipInstall
    Assume the MSIX is already installed on this machine.  Skip Add-AppxPackage and
    jump directly to the probe + uninstall phases.

.PARAMETER AutoSetup
    Write a small probe settings.json and run.marker-bait directly to the expected real
    app-data paths, then launch the installed tray briefly (30 s), and infer storage
    routing from which files change / appear.  This avoids the manual UI walk-through.
    DEFAULT: enabled (the script defaults to -AutoSetup).

.PARAMETER SkipAutoSetup
    Disable the -AutoSetup logic.  The script emits a MANUAL-STEP-REQUIRED.txt and
    exits with code 3, asking the operator to walk through Setup-Locally in the tray
    UI, then re-run with -SkipInstall to continue from phase 5.

.PARAMETER WhatIf
    Print all planned actions without executing any destructive operations.

.PARAMETER Help
    Print usage and exit.

.EXAMPLE
    # Typical run (auto-probe mode):
    .\validate-msix-storage-paths.ps1 `
        -MsixPath C:\drop\OpenClawTray_1.2.3.0_x64.msix `
        -CertPath C:\drop\OpenClawTray.cer `
        -EvidenceDir C:\msix-evidence\run1

.EXAMPLE
    # Manual UI walk-through mode:
    # Step 1 — install and emit manual instructions:
    .\validate-msix-storage-paths.ps1 `
        -MsixPath C:\drop\OpenClawTray_1.2.3.0_x64.msix `
        -SkipAutoSetup `
        -EvidenceDir C:\msix-evidence\run1
    # (exit code 3 — operator walks through Setup-Locally)

    # Step 2 — probe + teardown (app already installed):
    .\validate-msix-storage-paths.ps1 `
        -SkipInstall `
        -EvidenceDir C:\msix-evidence\run1

.EXAMPLE
    # WhatIf dry-run:
    .\validate-msix-storage-paths.ps1 `
        -MsixPath C:\drop\OpenClawTray_1.2.3.0_x64.msix `
        -WhatIf

    EVIDENCE LAYOUT
    ---------------
    <EvidenceDir>/
      pre-appdata.txt           — recursive dir listing of %APPDATA%\OpenClawTray\ pre-install
      pre-localappdata.txt      — same for %LOCALAPPDATA%\OpenClawTray\
      pre-packages.txt          — %LOCALAPPDATA%\Packages\*OpenClaw* pre-install
      pre-appx.json             — Get-AppxPackage *OpenClaw* pre-install (JSON)
      install-stdout.txt        — Add-AppxPackage output
      install-stderr.txt        — Add-AppxPackage errors
      package-info.json         — PackageFamilyName, InstallLocation, PackageFullName
      post-appdata.txt          — same dirs post-install/probe
      post-localappdata.txt
      post-packages.txt
      post-appx.json
      post-uninstall-appdata.txt   — same dirs after Remove-AppxPackage
      post-uninstall-localappdata.txt
      post-uninstall-packages.txt
      verdict.json              — final verdict with removal_orphans
      summary.json              — script run metadata, all steps
      MANUAL-STEP-REQUIRED.txt  — emitted only when -SkipAutoSetup is used

    EXIT CODES
    ----------
    0  Script passed: non-Inconclusive verdict, all evidence files present, no errors.
    1  Script failed: Inconclusive verdict, incomplete evidence, or unhandled errors.
    2  Pre-flight blocked: OpenClaw* process running, or other blocking condition.
    3  Manual step required: -SkipAutoSetup was used; operator must walk UI flow.

.NOTES
    Date:   2026-05-07
    Author: Bostick (Tester/FIDO) — drafted pre-commit-7 for commit-7 verification.
    DO NOT RUN against a paired tray instance.  Snapshots capture settings.json content.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$MsixPath,
    [string]$CertPath,
    [string]$EvidenceDir,
    [switch]$SkipInstall,
    [switch]$AutoSetup,        # default ON — see logic below
    [switch]$SkipAutoSetup,    # force manual path
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Help
# ---------------------------------------------------------------------------
if ($Help) {
    Get-Help -Full $PSCommandPath
    exit 0
}

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
$SCRIPT_VERSION     = "1.0.0"
$SCRIPT_DATE        = "2026-05-07"
$PACKAGE_NAME_GLOB  = "*OpenClaw.Tray*"
# Publisher hash in PackageFamilyName is derived from the manifest Publisher DN.
# We resolve it at runtime from Get-AppxPackage after install.
$PROBE_SESSION_ID   = [System.Guid]::NewGuid().ToString("N")
$PROBE_MARKER_NAME  = ".msix-storage-probe-$PROBE_SESSION_ID"
$RUN_MARKER_NAME    = "run.marker"
$SETTINGS_FILE_NAME = "settings.json"

# ---------------------------------------------------------------------------
# Default -AutoSetup ON unless -SkipAutoSetup explicitly set
# ---------------------------------------------------------------------------
$effectiveAutoSetup = -not $SkipAutoSetup.IsPresent

# ---------------------------------------------------------------------------
# Evidence directory default
# ---------------------------------------------------------------------------
$runStamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
if ([string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $EvidenceDir = Join-Path (Get-Location) "msix-validation-evidence\$runStamp"
}

# ---------------------------------------------------------------------------
# Script-level state
# ---------------------------------------------------------------------------
$script:summary = [ordered]@{
    script            = "validate-msix-storage-paths"
    version           = $SCRIPT_VERSION
    date              = $SCRIPT_DATE
    startedAt         = (Get-Date).ToString("o")
    finishedAt        = $null
    status            = "Running"
    probeSessionId    = $PROBE_SESSION_ID
    autoSetup         = $effectiveAutoSetup
    evidenceDir       = $EvidenceDir
    msixPath          = $MsixPath
    packageFamilyName = $null
    packageFullName   = $null
    installLocation   = $null
    verdict           = $null
    steps             = @()
    error             = $null
}

# Script-level slot for engine result (populated by Invoke-CliEngineUninstall)
$script:engineResult = $null

# Exit-code sentinels
$EXIT_PASS            = 0
$EXIT_FAIL            = 1
$EXIT_PREFLIGHT_BLOCK = 2
$EXIT_MANUAL_REQUIRED = 3

# ---------------------------------------------------------------------------
# Logging helpers (mirror validate-wsl-gateway.ps1 patterns)
# ---------------------------------------------------------------------------
function Add-Step {
    param(
        [string]$Name,
        [string]$Status,      # Completed | Failed | Skipped | Warning
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
    $script:summary.steps += $entry

    $ts = (Get-Date).ToString("HH:mm:ss")
    $color = switch ($Status) {
        "Completed" { "Cyan"    }
        "Failed"    { "Red"     }
        "Skipped"   { "DarkGray" }
        "Warning"   { "Yellow"  }
        default     { "White"   }
    }
    Write-Host "[$ts] [$Status] $Name — $Message" -ForegroundColor $color
}

function Write-StepInfo {
    param([string]$Message)
    $ts = (Get-Date).ToString("HH:mm:ss")
    Write-Host "[$ts]   $Message" -ForegroundColor DarkCyan
}

function Write-Fatal {
    param([string]$Message)
    $ts = (Get-Date).ToString("HH:mm:ss")
    Write-Host "[$ts] [FATAL] $Message" -ForegroundColor Red
}

# ---------------------------------------------------------------------------
# Evidence directory helpers
# ---------------------------------------------------------------------------
function Ensure-EvidenceDir {
    if (-not (Test-Path -LiteralPath $EvidenceDir)) {
        New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
    }
}

function Get-EvidencePath {
    param([string]$FileName)
    Ensure-EvidenceDir
    return Join-Path $EvidenceDir $FileName
}

# ---------------------------------------------------------------------------
# Snapshot helpers
# ---------------------------------------------------------------------------
function Capture-DirListing {
    param(
        [string]$Path,
        [string]$OutFile
    )
    $dest = Get-EvidencePath $OutFile
    if (Test-Path -LiteralPath $Path) {
        try {
            $items = Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue |
                     Select-Object FullName, Length, LastWriteTimeUtc, Attributes |
                     Sort-Object FullName
            $lines = @("# Captured: $(Get-Date -Format 'o')  Path: $Path")
            foreach ($i in $items) {
                $lines += "$($i.LastWriteTimeUtc.ToString('o'))  $($i.Attributes.ToString().PadRight(12))  $($i.Length.ToString().PadLeft(12))  $($i.FullName)"
            }
            $lines | Set-Content -LiteralPath $dest -Encoding UTF8
            return $items.Count
        }
        catch {
            "# ERROR capturing $Path : $_" | Set-Content -LiteralPath $dest -Encoding UTF8
            return -1
        }
    }
    else {
        "# Path does not exist: $Path  (captured: $(Get-Date -Format 'o'))" |
            Set-Content -LiteralPath $dest -Encoding UTF8
        return 0
    }
}

function Capture-PackagesListing {
    param([string]$OutFile)
    $dest   = Get-EvidencePath $OutFile
    $pkgDir = Join-Path $env:LOCALAPPDATA "Packages"
    if (Test-Path -LiteralPath $pkgDir) {
        try {
            $items = Get-ChildItem -LiteralPath $pkgDir -Directory -Filter "*OpenClaw*" -Force -ErrorAction SilentlyContinue |
                     Select-Object FullName, CreationTimeUtc, LastWriteTimeUtc
            $lines = @("# Captured: $(Get-Date -Format 'o')  Packages filter: *OpenClaw*")
            foreach ($i in $items) {
                $lines += "created=$($i.CreationTimeUtc.ToString('o'))  modified=$($i.LastWriteTimeUtc.ToString('o'))  $($i.FullName)"
                # Recurse one level to show sub-dirs (LocalCache, LocalState, Settings, etc.)
                $subs = Get-ChildItem -LiteralPath $i.FullName -Directory -Force -ErrorAction SilentlyContinue
                foreach ($s in $subs) {
                    $lines += "  +  $($s.Name)"
                }
            }
            $lines | Set-Content -LiteralPath $dest -Encoding UTF8
            return $items.Count
        }
        catch {
            "# ERROR: $_" | Set-Content -LiteralPath $dest -Encoding UTF8
            return -1
        }
    }
    else {
        "# %LOCALAPPDATA%\Packages does not exist" | Set-Content -LiteralPath $dest -Encoding UTF8
        return 0
    }
}

function Capture-AppxPackage {
    param([string]$OutFile)
    $dest = Get-EvidencePath $OutFile
    try {
        $pkgs = Get-AppxPackage -Name "*OpenClaw*" -ErrorAction SilentlyContinue
        if ($null -eq $pkgs) { $pkgs = @() }
        $pkgs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $dest -Encoding UTF8
    }
    catch {
        @{ error = $_.ToString() } | ConvertTo-Json | Set-Content -LiteralPath $dest -Encoding UTF8
    }
}

function Capture-AllSnapshots {
    param([string]$Prefix)
    # Prefix is "pre" | "post" | "post-uninstall"
    $appDataDir      = Join-Path $env:APPDATA      "OpenClawTray"
    $localAppDataDir = Join-Path $env:LOCALAPPDATA "OpenClawTray"

    $countA = Capture-DirListing  -Path $appDataDir      -OutFile "$Prefix-appdata.txt"
    $countL = Capture-DirListing  -Path $localAppDataDir -OutFile "$Prefix-localappdata.txt"
    $countP = Capture-PackagesListing                     -OutFile "$Prefix-packages.txt"
    Capture-AppxPackage                                   -OutFile "$Prefix-appx.json"

    Add-Step "snapshot-$Prefix" "Completed" "Captured filesystem snapshots ($Prefix)." @{
        appDataItemCount      = $countA
        localAppDataItemCount = $countL
        openClawPackageCount  = $countP
    }
}

# ---------------------------------------------------------------------------
# Diff helper — returns list of new/changed paths in target vs baseline
# ---------------------------------------------------------------------------
function Get-NewPaths {
    param(
        [string]$BaselineFile,
        [string]$NewFile
    )
    if (-not (Test-Path -LiteralPath $BaselineFile)) { return @() }
    if (-not (Test-Path -LiteralPath $NewFile))      { return @() }

    $baseline = Get-Content -LiteralPath $BaselineFile | Where-Object { $_ -match '^\d{4}' } | ForEach-Object { ($_ -split '\s+', 5)[-1] }
    $current  = Get-Content -LiteralPath $NewFile      | Where-Object { $_ -match '^\d{4}' } | ForEach-Object { ($_ -split '\s+', 5)[-1] }

    $baseSet  = [System.Collections.Generic.HashSet[string]]::new($baseline, [System.StringComparer]::OrdinalIgnoreCase)
    $new      = $current | Where-Object { -not $baseSet.Contains($_) }
    return @($new)
}

# ---------------------------------------------------------------------------
# Write summary files
# ---------------------------------------------------------------------------
function Write-Summary {
    Ensure-EvidenceDir
    $script:summary.finishedAt = (Get-Date).ToString("o")
    $summaryPath = Get-EvidencePath "summary.json"
    $script:summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

# ---------------------------------------------------------------------------
# Invoke a process, capture stdout/stderr, record step
# ---------------------------------------------------------------------------
function Invoke-CapturedProcess {
    param(
        [string]$StepName,
        [string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = (Get-Location).Path,
        [switch]$IgnoreExitCode
    )
    Ensure-EvidenceDir
    $safeName  = $StepName -replace '[^a-zA-Z0-9_.-]', '-'
    $stdoutFile = Get-EvidencePath "$safeName.stdout.txt"
    $stderrFile = Get-EvidencePath "$safeName.stderr.txt"

    Push-Location $WorkingDirectory
    try {
        & $FilePath @ArgumentList > $stdoutFile 2> $stderrFile
        $exitCode = if ($null -eq $global:LASTEXITCODE) { 0 } else { [int]$global:LASTEXITCODE }
    }
    finally {
        Pop-Location
    }

    Add-Step $StepName "Completed" "Exit code $exitCode." @{
        file      = $FilePath
        arguments = ($ArgumentList -join " ")
        exitCode  = $exitCode
        stdout    = $stdoutFile
        stderr    = $stderrFile
    }

    if ($exitCode -ne 0 -and -not $IgnoreExitCode) {
        throw "$StepName failed with exit code $exitCode.  See $stderrFile"
    }
    return $exitCode
}

# ---------------------------------------------------------------------------
# PHASE 1 — PREFLIGHT
# ---------------------------------------------------------------------------
function Invoke-Preflight {
    Write-Host ""
    Write-Host "═══ PHASE 1: PREFLIGHT ═══" -ForegroundColor Magenta

    # 1a. Interactive session check
    if ($PSVersionTable.PSVersion.Major -ge 5) {
        $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        if ($sessionId -eq 0) {
            Write-Fatal "This script must run in an interactive Windows session (session 0 detected — likely a non-interactive service context).  MSIX installation requires a user session."
            return $EXIT_PREFLIGHT_BLOCK
        }
    }
    Add-Step "preflight-session-check" "Completed" "Interactive session confirmed (SessionId=$([System.Diagnostics.Process]::GetCurrentProcess().SessionId))."

    # 1b. Refuse if any OpenClaw* process is running
    $running = Get-Process -Name "OpenClaw*" -ErrorAction SilentlyContinue
    if ($running) {
        $pids = ($running | ForEach-Object { $_.Id }) -join ", "
        Write-Fatal "OpenClaw* process(es) are running (PIDs: $pids).  Stop them first:"
        foreach ($p in $running) {
            Write-Fatal "  Stop-Process -Id $($p.Id)   # $($p.ProcessName)"
        }
        Add-Step "preflight-process-check" "Failed" "OpenClaw processes running: PIDs $pids" @{ pids = $pids }
        return $EXIT_PREFLIGHT_BLOCK
    }
    Add-Step "preflight-process-check" "Completed" "No OpenClaw* processes running."

    # 1c. Validate -MsixPath (unless -SkipInstall)
    if (-not $SkipInstall) {
        if ([string]::IsNullOrWhiteSpace($MsixPath)) {
            Write-Fatal "-MsixPath is required unless -SkipInstall is set."
            Add-Step "preflight-msix-path" "Failed" "-MsixPath not provided."
            return $EXIT_PREFLIGHT_BLOCK
        }
        if (-not (Test-Path -LiteralPath $MsixPath)) {
            Write-Fatal "-MsixPath does not exist: $MsixPath"
            Add-Step "preflight-msix-path" "Failed" "File not found: $MsixPath"
            return $EXIT_PREFLIGHT_BLOCK
        }
        Add-Step "preflight-msix-path" "Completed" "MSIX file found: $MsixPath"
    }
    else {
        Add-Step "preflight-msix-path" "Skipped" "-SkipInstall set; skipping MSIX path validation."
    }

    # 1d. Check for pre-existing OpenClawTray package
    $existing = Get-AppxPackage -Name "OpenClaw.Tray" -ErrorAction SilentlyContinue
    if ($existing -and -not $SkipInstall) {
        Add-Step "preflight-existing-package" "Warning" "Pre-existing OpenClaw.Tray package found: $($existing.PackageFullName).  Will overwrite via Add-AppxPackage." @{
            existingFullName = $existing.PackageFullName
        }
    }
    elseif ($SkipInstall -and -not $existing) {
        Write-Fatal "-SkipInstall set but no OpenClaw.Tray package is installed.  Nothing to probe."
        Add-Step "preflight-existing-package" "Failed" "No installed package found but -SkipInstall was set."
        return $EXIT_PREFLIGHT_BLOCK
    }
    else {
        Add-Step "preflight-existing-package" "Completed" "No pre-existing OpenClaw.Tray package found (clean state)."
    }

    # 1e. WhatIf gate
    if ($WhatIfPreference) {
        Write-Host ""
        Write-Host "WhatIf mode — planned actions:" -ForegroundColor Yellow
        Write-Host "  1. Snapshot pre-install state"
        Write-Host "  2. Add-AppxPackage '$MsixPath'"
        if (-not [string]::IsNullOrWhiteSpace($CertPath)) {
            Write-Host "  2a. Import-Certificate '$CertPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
        }
        Write-Host "  3. Write probe files to real %APPDATA%\OpenClawTray\ + %LOCALAPPDATA%\OpenClawTray\"
        Write-Host "  4. Launch installed tray briefly (max 30 s)"
        Write-Host "  5. Kill tray by PID"
        Write-Host "  6. Snapshot post-probe state"
        Write-Host "  7. Compute diff + verdict"
        Write-Host "  8. Remove-AppxPackage"
        Write-Host "  9. Snapshot post-uninstall state"
        Write-Host "  Evidence dir: $EvidenceDir"
        Add-Step "whatif-output" "Completed" "WhatIf planned actions printed; no destructive operations performed."
        return $EXIT_PASS
    }

    return $null   # $null means "continue"
}

# ---------------------------------------------------------------------------
# PHASE 2 — SNAPSHOT PRE-INSTALL STATE
# ---------------------------------------------------------------------------
function Invoke-PreInstallSnapshot {
    Write-Host ""
    Write-Host "═══ PHASE 2: PRE-INSTALL SNAPSHOT ═══" -ForegroundColor Magenta
    Capture-AllSnapshots -Prefix "pre"
}

# ---------------------------------------------------------------------------
# PHASE 3 — INSTALL
# ---------------------------------------------------------------------------
function Invoke-Install {
    Write-Host ""
    Write-Host "═══ PHASE 3: INSTALL ═══" -ForegroundColor Magenta

    if ($SkipInstall) {
        Add-Step "install-msix" "Skipped" "-SkipInstall set; assuming MSIX already installed."
    }
    else {
        # 3a. Import cert if provided
        if (-not [string]::IsNullOrWhiteSpace($CertPath)) {
            Write-StepInfo "Importing sideload certificate: $CertPath"
            try {
                Import-Certificate -FilePath $CertPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
                Add-Step "import-certificate" "Completed" "Certificate imported to Cert:\LocalMachine\TrustedPeople."
            }
            catch {
                Add-Step "import-certificate" "Failed" "Certificate import failed: $_"
                throw
            }
        }

        # 3b. Add-AppxPackage
        Write-StepInfo "Installing: $MsixPath"
        $stdoutFile = Get-EvidencePath "install.stdout.txt"
        $stderrFile = Get-EvidencePath "install.stderr.txt"
        try {
            Add-AppxPackage -Path $MsixPath -ForceApplicationShutdown 2>&1 | Tee-Object -FilePath $stdoutFile
            Add-Step "install-msix" "Completed" "Add-AppxPackage succeeded." @{
                msixPath   = $MsixPath
                stdout     = $stdoutFile
            }
        }
        catch {
            $_ | Out-File -FilePath $stderrFile -Encoding UTF8
            Add-Step "install-msix" "Failed" "Add-AppxPackage threw: $_" @{
                msixPath = $MsixPath
                stderr   = $stderrFile
            }
            throw
        }
    }

    # 3c. Resolve PackageFamilyName and InstallLocation
    $pkg = Get-AppxPackage -Name "OpenClaw.Tray" -ErrorAction SilentlyContinue
    if (-not $pkg) {
        throw "Package OpenClaw.Tray not found after install step.  Cannot continue."
    }

    $script:summary.packageFamilyName = $pkg.PackageFamilyName
    $script:summary.packageFullName   = $pkg.PackageFullName
    $script:summary.installLocation   = $pkg.InstallLocation

    $pkgInfo = [ordered]@{
        packageFamilyName = $pkg.PackageFamilyName
        packageFullName   = $pkg.PackageFullName
        installLocation   = $pkg.InstallLocation
        version           = $pkg.Version
        architecture      = $pkg.Architecture
        publisherId       = $pkg.PublisherId
        capturedAt        = (Get-Date).ToString("o")
    }
    $pkgInfoPath = Get-EvidencePath "package-info.json"
    $pkgInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $pkgInfoPath -Encoding UTF8

    Add-Step "resolve-package-info" "Completed" "Package resolved: $($pkg.PackageFamilyName)" @{
        packageFamilyName = $pkg.PackageFamilyName
        installLocation   = $pkg.InstallLocation
        pkgInfoFile       = $pkgInfoPath
    }

    return $pkg
}

# ---------------------------------------------------------------------------
# PHASE 4 — TRIGGER / PROBE
# ---------------------------------------------------------------------------
function Invoke-ProbeSetup {
    param([object]$Pkg)

    Write-Host ""
    Write-Host "═══ PHASE 4: PROBE SETUP ═══" -ForegroundColor Magenta

    if (-not $effectiveAutoSetup) {
        # Manual path: emit instructions and exit 3
        $manualPath = Get-EvidencePath "MANUAL-STEP-REQUIRED.txt"
        $instructions = @(
            "MANUAL STEP REQUIRED — $(Get-Date -Format 'o')",
            "",
            "The script was run with -SkipAutoSetup.  You must manually walk through the",
            "OpenClaw Setup-Locally flow in the tray UI, then re-run the script to capture",
            "the post-setup snapshot.",
            "",
            "STEPS:",
            "  1. Launch the installed tray from Start / App list:",
            "       explorer.exe shell:AppsFolder\$($Pkg.PackageFamilyName)!App",
            "  2. Follow the Setup-Locally wizard in the tray UI until it completes.",
            "  3. Close the tray app (right-click tray icon → Exit, or Stop-Process -Id <pid>).",
            "  4. Re-run this script with -SkipInstall and the SAME -EvidenceDir:",
            "       .\validate-msix-storage-paths.ps1 -SkipInstall -EvidenceDir '$EvidenceDir'",
            "",
            "Evidence directory: $EvidenceDir",
            "PackageFamilyName:  $($Pkg.PackageFamilyName)"
        )
        $instructions | Set-Content -LiteralPath $manualPath -Encoding UTF8
        Add-Step "probe-setup" "Skipped" "-SkipAutoSetup: manual UI walk-through required.  See MANUAL-STEP-REQUIRED.txt."
        Write-Host ""
        Write-Host "Manual step required.  Instructions written to:" -ForegroundColor Yellow
        Write-Host "  $manualPath" -ForegroundColor Yellow
        return $EXIT_MANUAL_REQUIRED
    }

    # -AutoSetup path:
    # Write probe marker files to the REAL %APPDATA%\OpenClawTray\ and %LOCALAPPDATA%\OpenClawTray\
    # paths with a unique session ID.  Then launch the installed tray briefly.
    # The presence of a run.marker or modified settings.json at real paths tells us the app
    # uses real APPDATA (Path A).  If those paths stay untouched but files appear under
    # %LOCALAPPDATA%\Packages\<PFN>\, that confirms Path B.

    $realAppData       = Join-Path $env:APPDATA      "OpenClawTray"
    $realLocalAppData  = Join-Path $env:LOCALAPPDATA "OpenClawTray"

    # Ensure dirs exist so we can write probe files
    foreach ($dir in @($realAppData, $realLocalAppData)) {
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
    }

    # Write probe markers at real paths (unique per session)
    $probeAppData      = Join-Path $realAppData      $PROBE_MARKER_NAME
    $probeLocalAppData = Join-Path $realLocalAppData $PROBE_MARKER_NAME
    $probeContent      = @{ probeSessionId = $PROBE_SESSION_ID; createdAt = (Get-Date).ToString("o") } | ConvertTo-Json
    Set-Content -LiteralPath $probeAppData      -Value $probeContent -Encoding UTF8
    Set-Content -LiteralPath $probeLocalAppData -Value $probeContent -Encoding UTF8

    Add-Step "probe-write-markers" "Completed" "Probe marker files written to real APPDATA paths." @{
        appDataProbe      = $probeAppData
        localAppDataProbe = $probeLocalAppData
        probeSessionId    = $PROBE_SESSION_ID
    }

    # Launch the MSIX-installed tray using the shell:AppsFolder activation URI
    # (the canonical way to launch a packaged app without a Start tile shortcut)
    Write-StepInfo "Launching installed tray via shell:AppsFolder..."
    $pfn             = $Pkg.PackageFamilyName
    $appUserModelId  = "$pfn!App"
    $trayProcess     = $null

    try {
        Start-Process "explorer.exe" -ArgumentList "shell:AppsFolder\$appUserModelId"
        Add-Step "probe-launch-tray" "Completed" "explorer.exe shell:AppsFolder launched." @{ appUserModelId = $appUserModelId }
    }
    catch {
        Add-Step "probe-launch-tray" "Warning" "Launch attempt threw: $_.  Proceeding with probe anyway." @{ error = $_.ToString() }
    }

    # Wait up to 30 s for a OpenClaw* process to appear
    Write-StepInfo "Waiting up to 30 s for tray process to start..."
    $deadline     = (Get-Date).AddSeconds(30)
    $trayProcess  = $null
    while ((Get-Date) -lt $deadline) {
        $found = Get-Process -Name "OpenClaw*" -ErrorAction SilentlyContinue
        if ($found) {
            $trayProcess = $found | Select-Object -First 1
            break
        }
        Start-Sleep -Milliseconds 500
    }

    if ($trayProcess) {
        Write-StepInfo "Tray process started: PID $($trayProcess.Id) ($($trayProcess.ProcessName)).  Waiting 10 s for initialization..."
        Start-Sleep -Seconds 10
        Add-Step "probe-tray-running" "Completed" "Tray process detected." @{
            pid         = $trayProcess.Id
            processName = $trayProcess.ProcessName
        }

        # Kill tray by PID (never by name — policy requirement)
        try {
            Stop-Process -Id $trayProcess.Id -Force -ErrorAction SilentlyContinue
            Add-Step "probe-tray-stop" "Completed" "Tray process stopped." @{ pid = $trayProcess.Id }
        }
        catch {
            Add-Step "probe-tray-stop" "Warning" "Stop-Process threw: $_.  May have already exited." @{ error = $_.ToString() }
        }
        Start-Sleep -Seconds 2   # let file handles close
    }
    else {
        Add-Step "probe-tray-running" "Warning" "Tray process did not appear within 30 s.  Probe results may be Inconclusive."
    }

    # Clean up our own probe markers (they served their purpose; we don't want them
    # contaminating the diff as "app-written" files)
    foreach ($f in @($probeAppData, $probeLocalAppData)) {
        if (Test-Path -LiteralPath $f) {
            Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
        }
    }

    return $null  # continue
}

# ---------------------------------------------------------------------------
# PHASE 4a — CLI ENGINE UNINSTALL
# Invoke OpenClawTray.exe --uninstall CLI to:
#   1. Drive the engine's own cleanup (WSL distro, settings, etc.)
#   2. Capture engine postconditions for cross_check_consistent in verdict.json
#
# Runs AFTER the probe has triggered file writes so the filesystem diff is
# captured before engine cleanup happens (Phase 5 snapshots the post-probe
# state AFTER this phase so the diff reflects what the tray itself wrote,
# not the engine cleanup).
#
# NOTE: The EXE is taken from the MSIX install location so it is the same
# binary that was probed.  If the EXE is not found, the phase is skipped and
# cross_check_consistent will be false.
# ---------------------------------------------------------------------------
function Invoke-CliEngineUninstall {
    param([object]$Pkg)

    Write-Host ""
    Write-Host "═══ PHASE 4a: CLI ENGINE UNINSTALL ═══" -ForegroundColor Magenta

    # Locate the EXE inside the MSIX install location
    $installLocation = if ($Pkg) { $Pkg.InstallLocation } else { $script:summary.installLocation }
    $exePath = $null
    if (-not [string]::IsNullOrEmpty($installLocation)) {
        $candidate = Join-Path $installLocation "OpenClaw.Tray.WinUI.exe"
        if (Test-Path -LiteralPath $candidate) {
            $exePath = $candidate
        }
    }

    if ([string]::IsNullOrEmpty($exePath)) {
        Add-Step "cli-engine-uninstall" "Skipped" "OpenClaw.Tray.WinUI.exe not found in install location '$installLocation'.  cross_check_consistent will be false." @{
            installLocation = $installLocation
        }
        return [ordered]@{
            invoked          = $false
            exit_code        = $null
            success          = $false
            json_path        = $null
            postconditions   = $null
            skip_reason      = "EXE not found at install location"
        }
    }

    Write-StepInfo "Engine EXE: $exePath"

    Ensure-EvidenceDir
    $jsonOutputPath  = Get-EvidencePath "engine-uninstall-result.json"
    $stdoutPath      = Get-EvidencePath "cli-engine-uninstall.stdout.txt"
    $stderrPath      = Get-EvidencePath "cli-engine-uninstall.stderr.txt"

    $cliArgs = @('--uninstall', '--confirm-destructive', '--json-output', $jsonOutputPath)
    Write-StepInfo "Invoking: $exePath $($cliArgs -join ' ')"

    $exitCode = $null
    try {
        & $exePath @cliArgs > $stdoutPath 2> $stderrPath
        $exitCode = if ($null -eq $global:LASTEXITCODE) { 0 } else { [int]$global:LASTEXITCODE }
    }
    catch {
        $exitCode = -1
        "Exception: $($_.ToString())" | Set-Content -LiteralPath $stderrPath -Encoding UTF8
    }

    # Parse engine JSON result
    $engineJson       = $null
    $engineSuccess    = $false
    $enginePostconds  = $null
    if (Test-Path -LiteralPath $jsonOutputPath) {
        try {
            $raw          = Get-Content -LiteralPath $jsonOutputPath -Raw -Encoding UTF8
            $engineJson   = $raw | ConvertFrom-Json
            $engineSuccess = [bool]$engineJson.success
            # Build an ordered hashtable from the postconditions object
            $pc = $engineJson.postconditions
            if ($pc) {
                $enginePostconds = [ordered]@{
                    wsl_distro_absent     = [bool]$pc.wsl_distro_absent
                    autostart_cleared     = [bool]$pc.autostart_cleared
                    setup_state_absent    = [bool]$pc.setup_state_absent
                    device_token_cleared  = [bool]$pc.device_token_cleared
                    mcp_token_preserved   = [bool]$pc.mcp_token_preserved
                    keepalives_absent     = [bool]$pc.keepalives_absent
                    vhd_dir_absent        = [bool]$pc.vhd_dir_absent
                }
            }
        }
        catch {
            Write-StepInfo "Warning: could not parse engine JSON: $_"
        }
    }

    $stepStatus = if ($exitCode -eq 0) { "Completed" } else { "Warning" }
    Add-Step "cli-engine-uninstall" $stepStatus "CLI engine exit code $exitCode. success=$engineSuccess." @{
        exePath     = $exePath
        exitCode    = $exitCode
        success     = $engineSuccess
        jsonPath    = $jsonOutputPath
        stdout      = $stdoutPath
        stderr      = $stderrPath
    }

    $result = [ordered]@{
        invoked          = $true
        exit_code        = $exitCode
        success          = $engineSuccess
        json_path        = $jsonOutputPath
        postconditions   = $enginePostconds
        skip_reason      = $null
    }

    # Store in script-level slot so Invoke-Teardown can reference it
    $script:engineResult = $result

    return $result
}

# ---------------------------------------------------------------------------
# PHASE 5 — POST-INSTALL SNAPSHOT
# ---------------------------------------------------------------------------
function Invoke-PostInstallSnapshot {
    Write-Host ""
    Write-Host "═══ PHASE 5: POST-INSTALL SNAPSHOT ═══" -ForegroundColor Magenta
    Capture-AllSnapshots -Prefix "post"
}

# ---------------------------------------------------------------------------
# PHASE 6 — DIFF & VERDICT
# ---------------------------------------------------------------------------
function Invoke-Verdict {
    param(
        [object]$Pkg,
        [object]$EngineResult  # result from Invoke-CliEngineUninstall; may be $null
    )

    Write-Host ""
    Write-Host "═══ PHASE 6: DIFF & VERDICT ═══" -ForegroundColor Magenta

    $preAppData      = Get-EvidencePath "pre-appdata.txt"
    $preLocalAppData = Get-EvidencePath "pre-localappdata.txt"
    $prePackages     = Get-EvidencePath "pre-packages.txt"
    $postAppData     = Get-EvidencePath "post-appdata.txt"
    $postLocalAppData = Get-EvidencePath "post-localappdata.txt"
    $postPackages    = Get-EvidencePath "post-packages.txt"

    $newInAppData      = Get-NewPaths -BaselineFile $preAppData      -NewFile $postAppData
    $newInLocalAppData = Get-NewPaths -BaselineFile $preLocalAppData -NewFile $postLocalAppData
    $newInPackages     = Get-NewPaths -BaselineFile $prePackages     -NewFile $postPackages

    $writesToRealAppData      = $newInAppData.Count -gt 0
    $writesToRealLocalAppData = $newInLocalAppData.Count -gt 0

    # "Virtualized" = package-local storage gained new sub-dirs/files
    # We check if the PFN package dir gained a LocalState or LocalCache dir with content
    $pfn        = if ($Pkg) { $Pkg.PackageFamilyName } else { $script:summary.packageFamilyName }
    $pkgDir     = Join-Path $env:LOCALAPPDATA "Packages\$pfn"
    $pkgLocalState = Join-Path $pkgDir "LocalState"
    $pkgRoaming    = Join-Path $pkgDir "RoamingState"

    $writesToVirtualized = $false
    foreach ($candidate in @($pkgLocalState, $pkgRoaming)) {
        if (Test-Path -LiteralPath $candidate) {
            $items = Get-ChildItem -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
            if ($items.Count -gt 0) {
                $writesToVirtualized = $true
                break
            }
        }
    }

    # Determine verdict
    $verdict  = "Inconclusive"
    $reasoning = "No new files detected in either real APPDATA paths or MSIX package LocalState.  Possible causes: tray did not launch, first-run initialization skipped, or probe window too short."

    if ($writesToRealAppData -or $writesToRealLocalAppData) {
        $verdict   = "PathA-OrphanRisk"
        $reasoning = "New files detected in real APPDATA and/or LOCALAPPDATA paths.  runFullTrust is bypassing MSIX virtualization.  Remove-AppxPackage will NOT clean these files.  In-tray cleanup (Remove Local Gateway) and pre-removal warning banner are required."
    }
    elseif ($writesToVirtualized) {
        $verdict   = "PathB-CleanRemove"
        $reasoning = "No new files in real APPDATA paths.  New content detected under %LOCALAPPDATA%\Packages\$pfn\.  MSIX filesystem virtualization is active.  Remove-AppxPackage will clean file-based artifacts.  WSL distro still requires explicit cleanup."
    }
    elseif ($newInPackages.Count -gt 0 -and -not $writesToRealAppData -and -not $writesToRealLocalAppData) {
        $verdict   = "PathB-CleanRemove"
        $reasoning = "New package directories found under %LOCALAPPDATA%\Packages (filter *OpenClaw*) and no real APPDATA growth detected.  Consistent with virtualized storage."
    }

    $verdictData = [ordered]@{
        msix_writes_to_real_appdata      = $writesToRealAppData
        msix_writes_to_real_localappdata = $writesToRealLocalAppData
        msix_writes_to_virtualized_storage = $writesToVirtualized
        package_family_name              = $pfn
        install_location                 = if ($Pkg) { $Pkg.InstallLocation } else { $script:summary.installLocation }
        evidence_dir                     = $EvidenceDir
        verdict                          = $verdict
        reasoning                        = $reasoning
        new_real_appdata_files           = @($newInAppData)
        new_real_localappdata_files      = @($newInLocalAppData)
        removal_orphans                  = @()   # populated in phase 7
        # ------------------------------------------------------------------ 7A fields
        # Engine CLI postconditions from --uninstall --confirm-destructive --json-output.
        # cross_check_consistent is updated in Phase 7 (Invoke-Teardown) once orphan
        # data is available.  Initial value is $false; Teardown sets final value.
        engine_cli_invoked               = $false
        engine_cli_exit_code             = $null
        engine_postconditions            = $null
        cross_check_consistent           = $false
        # ------------------------------------------------------------------
        capturedAt                       = (Get-Date).ToString("o")
    }

    # Embed engine result if we have one (may have been populated by Invoke-CliEngineUninstall)
    if ($null -ne $EngineResult) {
        $verdictData.engine_cli_invoked   = [bool]$EngineResult.invoked
        $verdictData.engine_cli_exit_code = $EngineResult.exit_code
        $verdictData.engine_postconditions = $EngineResult.postconditions
    } elseif ($null -ne $script:engineResult) {
        $er = $script:engineResult
        $verdictData.engine_cli_invoked   = [bool]$er.invoked
        $verdictData.engine_cli_exit_code = $er.exit_code
        $verdictData.engine_postconditions = $er.postconditions
    }

    $verdictPath = Get-EvidencePath "verdict.json"
    $verdictData | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $verdictPath -Encoding UTF8

    $script:summary.verdict = $verdict

    # Color-coded console output
    $verdictColor = switch ($verdict) {
        "PathA-OrphanRisk"  { "Red"    }
        "PathB-CleanRemove" { "Green"  }
        default             { "Yellow" }
    }
    Write-Host ""
    Write-Host "┌─────────────────────────────────────────────────────────────┐" -ForegroundColor $verdictColor
    Write-Host "│  VERDICT: $verdict" -ForegroundColor $verdictColor
    Write-Host "│  $reasoning" -ForegroundColor $verdictColor
    Write-Host "│  Evidence: $verdictPath" -ForegroundColor $verdictColor
    Write-Host "└─────────────────────────────────────────────────────────────┘" -ForegroundColor $verdictColor
    Write-Host ""

    Add-Step "verdict" $( if ($verdict -eq "Inconclusive") { "Warning" } else { "Completed" } ) `
        "Verdict: $verdict" @{
            verdict                = $verdict
            writesToRealAppData    = $writesToRealAppData
            writesToRealLocal      = $writesToRealLocalAppData
            writesToVirtualized    = $writesToVirtualized
            newRealAppDataCount    = $newInAppData.Count
            newRealLocalCount      = $newInLocalAppData.Count
            verdictFile            = $verdictPath
        }

    return $verdictData
}

# ---------------------------------------------------------------------------
# PHASE 7 — TEARDOWN
# ---------------------------------------------------------------------------
function Invoke-Teardown {
    param(
        [object]$Pkg,
        [object]$VerdictData
    )

    Write-Host ""
    Write-Host "═══ PHASE 7: TEARDOWN ═══" -ForegroundColor Magenta

    if (-not $Pkg) {
        # Attempt to resolve by name in case we used -SkipInstall
        $Pkg = Get-AppxPackage -Name "OpenClaw.Tray" -ErrorAction SilentlyContinue
    }

    if (-not $Pkg) {
        Add-Step "teardown-remove-appx" "Skipped" "No OpenClaw.Tray package found to remove."
    }
    else {
        $pkgFullName = $Pkg.PackageFullName
        Write-StepInfo "Removing package: $pkgFullName"
        $removeStdout = Get-EvidencePath "remove-appx.stdout.txt"
        $removeStderr = Get-EvidencePath "remove-appx.stderr.txt"
        try {
            Remove-AppxPackage -Package $pkgFullName 2>&1 | Tee-Object -FilePath $removeStdout
            Add-Step "teardown-remove-appx" "Completed" "Remove-AppxPackage succeeded." @{
                packageFullName = $pkgFullName
                stdout          = $removeStdout
            }
        }
        catch {
            $_ | Out-File -FilePath $removeStderr -Encoding UTF8
            Add-Step "teardown-remove-appx" "Warning" "Remove-AppxPackage threw: $_.  Proceeding with post-uninstall snapshot." @{
                error  = $_.ToString()
                stderr = $removeStderr
            }
        }
    }

    # Post-uninstall snapshot
    Capture-AllSnapshots -Prefix "post-uninstall"

    # Compute orphans: files that still exist after removal that existed at post-install time
    $postAppData     = Get-EvidencePath "post-appdata.txt"
    $postLocalData   = Get-EvidencePath "post-localappdata.txt"
    $postUnAppData   = Get-EvidencePath "post-uninstall-appdata.txt"
    $postUnLocalData = Get-EvidencePath "post-uninstall-localappdata.txt"

    $orphansAppData  = Get-NewPaths -BaselineFile $postUnAppData   -NewFile $postAppData
    $orphansLocal    = Get-NewPaths -BaselineFile $postUnLocalData -NewFile $postLocalData

    # Items that survived removal (present in post-uninstall = NOT new relative to post = survivors)
    # Re-interpret: survivors = items in post-uninstall that were also in post (gained during run)
    # Simpler: items in post that are ALSO in post-uninstall = not cleaned up
    function Get-SurvivedPaths {
        param([string]$PostFile, [string]$PostUninstallFile)
        if (-not (Test-Path -LiteralPath $PostFile))          { return @() }
        if (-not (Test-Path -LiteralPath $PostUninstallFile)) { return @() }
        $postItems    = Get-Content -LiteralPath $PostFile          | Where-Object { $_ -match '^\d{4}' } | ForEach-Object { ($_ -split '\s+', 5)[-1] }
        $postUnItems  = Get-Content -LiteralPath $PostUninstallFile | Where-Object { $_ -match '^\d{4}' } | ForEach-Object { ($_ -split '\s+', 5)[-1] }
        $uninstallSet = [System.Collections.Generic.HashSet[string]]::new($postUnItems, [System.StringComparer]::OrdinalIgnoreCase)
        return @($postItems | Where-Object { $uninstallSet.Contains($_) })
    }

    $survivedAppData  = Get-SurvivedPaths -PostFile $postAppData   -PostUninstallFile $postUnAppData
    $survivedLocal    = Get-SurvivedPaths -PostFile $postLocalData  -PostUninstallFile $postUnLocalData
    $allSurvivors     = @($survivedAppData) + @($survivedLocal)

    # -----------------------------------------------------------------------
    # cross_check_consistent (7A requirement):
    #   "Do the engine postconditions match the empirical filesystem diff?"
    #
    #   For PathA-OrphanRisk: consistent = engine was invoked AND succeeded
    #   AND engine.postconditions.wsl_distro_absent == true (engine cleaned or
    #   confirmed WSL distro was never registered) WHILE files in real APPDATA
    #   survived Remove-AppxPackage (confirming MSIX does not auto-clean them).
    #   This is the definitive PathA evidence package.
    #
    #   For PathB-CleanRemove: consistent = engine was invoked AND succeeded
    #   AND no orphan files survived Remove-AppxPackage.
    #
    #   Inconclusive: always false.
    # -----------------------------------------------------------------------
    $crossCheckConsistent = $false
    $er = $script:engineResult
    if ($VerdictData -and $null -ne $er -and [bool]$er.invoked -and $er.exit_code -eq 0) {
        $enginePc             = $er.postconditions
        $engineWslAbsent      = if ($enginePc -and $null -ne $enginePc.wsl_distro_absent) { [bool]$enginePc.wsl_distro_absent } else { $false }
        $currentVerdict       = $VerdictData.verdict

        if ($currentVerdict -eq "PathA-OrphanRisk") {
            # PathA: real-APPDATA writes confirmed AND engine cleaned WSL (or no distro was ever
            # registered).  Orphans surviving MSIX removal confirm the orphan-risk claim.
            $crossCheckConsistent = $engineWslAbsent
        }
        elseif ($currentVerdict -eq "PathB-CleanRemove") {
            # PathB: no orphan files after MSIX removal AND engine completed successfully.
            $crossCheckConsistent = ($engineWslAbsent -and ($allSurvivors.Count -eq 0))
        }
        # Inconclusive → stays false
    }

    # Update verdict.json with removal_orphans AND cross_check_consistent
    if ($VerdictData) {
        $VerdictData.removal_orphans       = $allSurvivors
        $VerdictData.cross_check_consistent = $crossCheckConsistent
        $verdictPath = Get-EvidencePath "verdict.json"
        $VerdictData | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $verdictPath -Encoding UTF8
    }

    if ($allSurvivors.Count -gt 0) {
        Add-Step "teardown-orphan-check" "Warning" "$($allSurvivors.Count) file(s) survived Remove-AppxPackage (orphans)." @{
            orphans = $allSurvivors
        }
        Write-Host "⚠  $($allSurvivors.Count) orphan file(s) found after MSIX removal:" -ForegroundColor Yellow
        foreach ($o in $allSurvivors) { Write-Host "   $o" -ForegroundColor Yellow }
    }
    else {
        Add-Step "teardown-orphan-check" "Completed" "No orphan files detected after Remove-AppxPackage."
    }
}

# ---------------------------------------------------------------------------
# PASS/FAIL evaluation
# ---------------------------------------------------------------------------
function Get-FinalExitCode {
    param([object]$VerdictData)

    $hasAllEvidence = $true
    foreach ($required in @(
        "pre-appdata.txt", "pre-localappdata.txt", "pre-packages.txt", "pre-appx.json",
        "post-appdata.txt", "post-localappdata.txt", "post-packages.txt", "post-appx.json",
        "post-uninstall-appdata.txt", "post-uninstall-localappdata.txt", "post-uninstall-packages.txt",
        "verdict.json", "package-info.json"
    )) {
        $p = Get-EvidencePath $required
        if (-not (Test-Path -LiteralPath $p)) {
            Write-Host "  MISSING evidence file: $p" -ForegroundColor Red
            $hasAllEvidence = $false
        }
    }

    if (-not $hasAllEvidence) { return $EXIT_FAIL }

    $verdict = if ($VerdictData) { $VerdictData.verdict } else { $script:summary.verdict }
    if ($verdict -eq "Inconclusive") { return $EXIT_FAIL }
    if ([string]::IsNullOrWhiteSpace($verdict)) { return $EXIT_FAIL }

    return $EXIT_PASS
}

# ---------------------------------------------------------------------------
# MAIN
# ---------------------------------------------------------------------------
Ensure-EvidenceDir
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   validate-msix-storage-paths.ps1  v$SCRIPT_VERSION  ($SCRIPT_DATE)  ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  Evidence dir : $EvidenceDir"
Write-Host "  AutoSetup    : $effectiveAutoSetup"
Write-Host "  SkipInstall  : $($SkipInstall.IsPresent)"
if ($MsixPath) { Write-Host "  MsixPath     : $MsixPath" }
Write-Host ""

$exitCode    = $EXIT_FAIL
$pkg         = $null
$verdictData = $null

try {
    # Phase 1: Preflight
    $preflightResult = Invoke-Preflight
    if ($null -ne $preflightResult) {
        # Non-null means a terminal early result (WhatIf, preflight block, etc.)
        $exitCode = $preflightResult
    }
    else {
        # Phase 2: Pre-install snapshot
        Invoke-PreInstallSnapshot

        # Phase 3: Install
        $pkg = Invoke-Install

        # Phase 4: Probe setup
        $probeResult = Invoke-ProbeSetup -Pkg $pkg
        if ($null -ne $probeResult) {
            $exitCode = $probeResult   # manual step required (exit 3)
        }
        else {
            # Phase 5: Post-install snapshot
            Invoke-PostInstallSnapshot

            # Phase 4a: CLI Engine Uninstall — invoke Aaron's --uninstall flag to:
            #   1. Drive the engine's own gateway cleanup (WSL distro, settings, etc.)
            #   2. Capture engine postconditions for cross_check_consistent in verdict.json
            # Must run AFTER the probe snapshot so the post-probe state reflects what the
            # tray itself wrote (not engine cleanup).
            $engineResult = Invoke-CliEngineUninstall -Pkg $pkg

            # Phase 6: Verdict (includes engine data in verdict.json)
            $verdictData = Invoke-Verdict -Pkg $pkg -EngineResult $engineResult

            # Phase 7: Teardown (Remove-AppxPackage, orphan check, final cross_check update)
            Invoke-Teardown -Pkg $pkg -VerdictData $verdictData

            # Evaluate final exit code
            $exitCode = Get-FinalExitCode -VerdictData $verdictData
        }
    }

    $script:summary.status = if ($exitCode -eq $EXIT_PASS) { "Passed" } elseif ($exitCode -eq $EXIT_MANUAL_REQUIRED) { "ManualStepRequired" } elseif ($exitCode -eq $EXIT_PREFLIGHT_BLOCK) { "PreflightBlocked" } else { "Failed" }
}
catch {
    $script:summary.status = "Error"
    $script:summary.error  = $_.ToString()
    Add-Step "unhandled-error" "Failed" $_.ToString()
    Write-Fatal "Unhandled error: $_"
    $exitCode = $EXIT_FAIL
}
finally {
    Write-Summary
    Write-Host ""
    Write-Host "Summary written to: $(Get-EvidencePath 'summary.json')" -ForegroundColor Cyan
    Write-Host "Evidence directory: $EvidenceDir" -ForegroundColor Cyan
    $verdictDisplay = if ($verdictData) { $verdictData.verdict } elseif ($script:summary.verdict) { $script:summary.verdict } else { "N/A" }
    Write-Host "Verdict           : $verdictDisplay" -ForegroundColor $(
        switch ($verdictDisplay) {
            "PathA-OrphanRisk"  { "Red"    }
            "PathB-CleanRemove" { "Green"  }
            default             { "Yellow" }
        }
    )
    Write-Host "Exit code         : $exitCode" -ForegroundColor $(if ($exitCode -eq 0) { "Green" } else { "Yellow" })
    Write-Host ""
}

exit $exitCode
