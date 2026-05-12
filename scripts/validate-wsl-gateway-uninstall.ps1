<#
.SYNOPSIS
    Empirical verification of LocalGatewayUninstall postconditions.
    Mirror of validate-wsl-gateway.ps1 for the inverse (uninstall) direction.

.DESCRIPTION
    PURPOSE
    -------
    This script duplicates the LocalGatewayUninstall step sequence in PowerShell
    so Bostick can validate end-to-end without invoking the tray. The C# engine
    remains the production code path; this script MUST stay aligned with it.

    Source of truth for steps:
        src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewayUninstall.cs

    Shared helpers dot-sourced from:
        scripts/_uninstall-helpers.ps1
        (Test-IsOpenClawOwnedDistroName, Invoke-WslCommand, Stop-OpenClawProcessByPid,
         Assert-DryRunGate, Add-Step)

    MODES
    -----
    PreflightOnly
        Verifies the distro is registered, validates the distro-name guard, and
        snapshots pre-state to pre-state.json.  Non-destructive.  If the distro
        is absent, exits 0 immediately with verdict "AlreadyClean".

    Full
        PreflightOnly snapshot -> execute 13-step uninstall sequence in
        PowerShell -> post-state snapshot -> writes verdict.json.
        Requires -ConfirmDestructive unless -DryRun is also set.

    PostconditionOnly
        Skips snapshot and destruction.  Evaluates current filesystem /
        registry / WSL state against the uninstall-completed expectations.
        Use after an in-tray uninstall or a manual cleanup.

    OUTPUT LAYOUT
    -------------
    <OutputDir>/
        pre-state.json          state snapshot before uninstall
        post-state.json         state snapshot after uninstall (Full only)
        steps.json              ordered step audit trail (Full only)
        verdict.json            final pass/fail verdict + postconditions
        summary.md              human-readable summary

    EXIT CODES
    ----------
    0  PASS     All postconditions match expected uninstall-complete state.
    1  FAIL     One or more postconditions do not match.
    2  BLOCKED  Preflight blocked (wrong distro name, -ConfirmDestructive missing).
    3  ERROR    Full mode failed mid-execution (unhandled exception).

.PARAMETER Mode
    Required.  One of: PreflightOnly | Full | PostconditionOnly.

.PARAMETER ConfirmDestructive
    Required for Full mode unless -DryRun is also set.  Safety gate.

.PARAMETER DistroName
    WSL distro name to target.  Default: OpenClawGateway.
    Must be exactly "OpenClawGateway" (no prefix variants).

.PARAMETER PreserveLogs
    When $true (default), gateway logs are not deleted in Full mode.
    Mirrors LocalGatewayUninstallOptions.PreserveLogs.

.PARAMETER PreserveExecPolicy
    When $true (default), exec-policy.json is not deleted in Full mode.
    Mirrors LocalGatewayUninstallOptions.PreserveExecPolicy.

.PARAMETER OutputDir
    Directory for output artifacts.
    Default: .\uninstall-validation-output\<utc-timestamp>\

.PARAMETER DryRun
    When set with Full mode, executes step logic with no destructive mutations.
    Each step records status "DryRun".  Verdict is "DryRunComplete".

.PARAMETER Help
    Prints usage and exits 0.

.EXAMPLE
    # Non-destructive preflight + pre-state snapshot:
    .\validate-wsl-gateway-uninstall.ps1 -Mode PreflightOnly

.EXAMPLE
    # Dry-run: records every step without destroying anything:
    .\validate-wsl-gateway-uninstall.ps1 -Mode Full -DryRun

.EXAMPLE
    # Live full uninstall + postcondition verification:
    .\validate-wsl-gateway-uninstall.ps1 -Mode Full -ConfirmDestructive

.EXAMPLE
    # Verify postconditions after in-tray uninstall:
    .\validate-wsl-gateway-uninstall.ps1 -Mode PostconditionOnly

.EXAMPLE
    # Full uninstall preserving logs but deleting exec-policy:
    .\validate-wsl-gateway-uninstall.ps1 -Mode Full -ConfirmDestructive -PreserveExecPolicy $false

.NOTES
    Date:   2026-05-07
    Author: Aaron (Backend / Infrastructure Engineer)
    Branch: feat/wsl-gateway-uninstall

    File I/O against WSL is via `wsl bash -c` only.
    NEVER use \\wsl$ / \\wsl.localhost paths.
    Token / key material is redacted in all output artifacts.
#>

[CmdletBinding()]
param(
    # Mode is effectively required — validated manually so that -Help works without it.
    [string]$Mode = "",

    [switch]$ConfirmDestructive,

    [string]$DistroName = "OpenClawGateway",

    [bool]$PreserveLogs = $true,

    [bool]$PreserveExecPolicy = $true,

    [string]$OutputDir = "",

    [switch]$DryRun,

    # When set, -Mode Full skips the CLI delegate and executes the inline
    # PowerShell step replication instead.  Use for diagnostics or when
    # OpenClawTray.exe is not available (e.g. standalone script on bare machine).
    [switch]$NoCli,

    # Path to OpenClawTray.exe.  Auto-detected from common locations when empty.
    [string]$ExePath = "",

    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\_uninstall-helpers.ps1"

# Step audit trail required by Add-Step (from _uninstall-helpers.ps1).
$script:steps = @()

# ---------------------------------------------------------------------------
# Help
# ---------------------------------------------------------------------------

if ($Help -or [string]::IsNullOrEmpty($Mode)) {
    Write-Host @'
validate-wsl-gateway-uninstall.ps1  —  OpenClaw WSL gateway uninstall validator

USAGE:
  .\validate-wsl-gateway-uninstall.ps1 -Mode <mode> [options]

MODES (required):
  PreflightOnly       Snapshot pre-state only.  Non-destructive.
  Full                Snapshot -> execute 13-step uninstall -> verdict.
  PostconditionOnly   Verify current state against uninstall-complete expectations.

OPTIONS:
  -ConfirmDestructive     Required for Full mode (unless -DryRun is also set).
  -DistroName <name>      Default: OpenClawGateway (must be exact match)
  -PreserveLogs <bool>    Default: $true  (do not delete gateway logs)
  -PreserveExecPolicy <bool>  Default: $true  (do not delete exec-policy.json)
  -OutputDir <path>       Default: .\uninstall-validation-output\<utc-timestamp>\
  -DryRun                 Full mode records steps without any destruction.
  -NoCli                  Full mode: skip CLI delegate; use inline PS replication.
                          Use for diagnostics or when the EXE is unavailable.
  -ExePath <path>         Explicit path to OpenClawTray.exe (auto-detected when empty).
  -Help                   Show this help.

EXIT CODES:
  0  PASS     All postconditions match uninstall-complete state.
  1  FAIL     One or more postconditions do not match.
  2  BLOCKED  Preflight blocked (wrong distro name / missing -ConfirmDestructive).
  3  ERROR    Full mode failed mid-execution.

EXAMPLES:
  # Non-destructive preflight + pre-state snapshot:
  .\validate-wsl-gateway-uninstall.ps1 -Mode PreflightOnly

  # Dry-run (records steps, no destruction):
  .\validate-wsl-gateway-uninstall.ps1 -Mode Full -DryRun

  # Live full uninstall + verification (via CLI delegate — default):
  .\validate-wsl-gateway-uninstall.ps1 -Mode Full -ConfirmDestructive

  # Live full uninstall via inline PS replication (diagnostic fallback):
  .\validate-wsl-gateway-uninstall.ps1 -Mode Full -ConfirmDestructive -NoCli

  # Verify state after in-tray uninstall:
  .\validate-wsl-gateway-uninstall.ps1 -Mode PostconditionOnly
'@
    exit 0
}

# ---------------------------------------------------------------------------
# Parameter validation
# ---------------------------------------------------------------------------

$validModes = @('PreflightOnly', 'Full', 'PostconditionOnly')
if ($Mode -notin $validModes) {
    Write-Host "ERROR: -Mode must be one of: $($validModes -join ', '). Got: '$Mode'" -ForegroundColor Red
    exit 2
}

if (-not (Test-IsOpenClawOwnedDistroName -Name $DistroName)) {
    Write-Host "ERROR: Refusing to operate on distro '$DistroName': name must be exactly 'OpenClawGateway'. Pass -DistroName OpenClawGateway." -ForegroundColor Red
    exit 2
}

if ($Mode -eq 'Full' -and (-not $DryRun) -and (-not $ConfirmDestructive)) {
    Write-Host "ERROR: -ConfirmDestructive is required for -Mode Full when -DryRun is not set.  This prevents accidental destructive operations." -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------------------
# Path constants
# ---------------------------------------------------------------------------

$appData        = $env:APPDATA
$localAppData   = $env:LOCALAPPDATA

$setupStatePath  = Join-Path $localAppData "OpenClawTray\setup-state.json"
$deviceKeyPath   = Join-Path $appData      "OpenClawTray\device-key-ed25519.json"
$mcpTokenPath    = Join-Path $appData      "OpenClawTray\mcp-token.txt"
$settingsPath    = Join-Path $appData      "OpenClawTray\settings.json"
$logsDir         = Join-Path $localAppData "OpenClawTray\Logs"
$execPolicyPath  = Join-Path $localAppData "OpenClawTray\exec-policy.json"
$vhdDirPath      = Join-Path $localAppData "OpenClawTray\wsl\$DistroName"

$autoStartRegKey  = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$autoStartAppName = "OpenClawTray"

# ---------------------------------------------------------------------------
# Output directory
# ---------------------------------------------------------------------------

$utcStamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmssZ")
if ([string]::IsNullOrEmpty($OutputDir)) {
    $OutputDir = Join-Path (Get-Location) "uninstall-validation-output\$utcStamp"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$preStatePath  = Join-Path $OutputDir "pre-state.json"
$postStatePath = Join-Path $OutputDir "post-state.json"
$stepsPath     = Join-Path $OutputDir "steps.json"
$verdictPath   = Join-Path $OutputDir "verdict.json"
$summaryMdPath = Join-Path $OutputDir "summary.md"

# ---------------------------------------------------------------------------
# Token redaction
# ---------------------------------------------------------------------------

function Invoke-Redact {
    param([string]$Content)
    if ([string]::IsNullOrEmpty($Content)) { return $Content }
    # Redact JSON fields containing key/token material.
    $r = $Content -replace '("(?i:deviceToken|device_token|token|bootstrapToken|bootstrap_token|PrivateKeyBase64|PublicKeyBase64)"\s*:\s*")[^"]+(")', '$1<redacted>$2'
    # Redact bare key=value / key: value patterns.
    $r = $r -replace '(?i)((?:device|bootstrap|gateway|auth|mcp)[_-]?token\s*[:=]\s*)[^\s,"''}{]+', '$1<redacted>'
    return $r
}

# ---------------------------------------------------------------------------
# WSL helpers
# ---------------------------------------------------------------------------

function Get-WslDistroLines {
    try {
        $raw = & wsl --list --quiet 2>&1
        return ($raw | Out-String) -split "`r?`n" |
            ForEach-Object { ($_ -replace '\x00', '').Trim() } |
            Where-Object { $_ }
    } catch {
        return @()
    }
}

function Test-DistroRegistered {
    param([string]$Name)
    $lines = Get-WslDistroLines
    return (@($lines | Where-Object { $_ -eq $Name })).Count -gt 0
}

# ---------------------------------------------------------------------------
# Process helpers
# ---------------------------------------------------------------------------

function Get-WslKeepalivePids {
    param([string]$Name)
    $found = [System.Collections.Generic.List[int]]::new()
    try {
        $wslProcs = Get-CimInstance -ClassName Win32_Process -Filter "Name LIKE 'wsl%'" -ErrorAction SilentlyContinue
        foreach ($proc in $wslProcs) {
            $cmd = $proc.CommandLine
            if ($cmd -and
                $cmd -match [regex]::Escape($Name) -and
                $cmd -match 'sleep\s+2147483647') {
                $found.Add([int]$proc.ProcessId)
            }
        }
    } catch { }
    return $found.ToArray()
}

function Get-OpenClawProcessSnapshot {
    try {
        $procs = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like 'OpenClaw*' }
        return @($procs | ForEach-Object {
            [ordered]@{
                pid  = $_.Id
                name = $_.Name
                path = try { $_.MainModule.FileName } catch { '' }
            }
        })
    } catch { return @() }
}

# ---------------------------------------------------------------------------
# Registry helpers
# ---------------------------------------------------------------------------

function Test-AutoStartRegistryPresent {
    try {
        $val = Get-ItemProperty -LiteralPath $autoStartRegKey -Name $autoStartAppName -ErrorAction SilentlyContinue
        return ($null -ne $val -and $null -ne $val.$autoStartAppName)
    } catch { return $false }
}

function Remove-AutoStartRegistryValue {
    try {
        $existing = Get-ItemProperty -LiteralPath $autoStartRegKey -Name $autoStartAppName -ErrorAction SilentlyContinue
        if ($null -ne $existing) {
            Remove-ItemProperty -LiteralPath $autoStartRegKey -Name $autoStartAppName -Force -ErrorAction Stop
            return $true
        }
        return $false
    } catch {
        return $false
    }
}

# ---------------------------------------------------------------------------
# State snapshot
# ---------------------------------------------------------------------------

function Get-StateSnapshot {
    param([string]$Label)

    $allDistros      = Get-WslDistroLines
    $openClawDistros = @($allDistros | Where-Object { $_ -like '*OpenClaw*' })
    $registered      = Test-DistroRegistered -Name $DistroName

    $snapshot = [ordered]@{
        captured_at            = (Get-Date).ToString("o")
        label                  = $Label
        distro_name            = $DistroName
        distro_registered      = $registered
        wsl_distros_openclaw   = $openClawDistros
        autostart_registry     = Test-AutoStartRegistryPresent
        settings_autostart     = $null
        device_token_is_null   = $null
        files                  = [ordered]@{
            setup_state_exists   = (Test-Path -LiteralPath $setupStatePath)
            device_key_exists    = (Test-Path -LiteralPath $deviceKeyPath)
            mcp_token_exists     = (Test-Path -LiteralPath $mcpTokenPath)
            settings_exists      = (Test-Path -LiteralPath $settingsPath)
            exec_policy_exists   = (Test-Path -LiteralPath $execPolicyPath)
            vhd_dir_exists       = (Test-Path -LiteralPath $vhdDirPath)
        }
        processes_openclaw     = @()
    }

    # settings.AutoStart — read-only (no token content exposed)
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $j = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $snapshot.settings_autostart = if ($j.PSObject.Properties['AutoStart']) { [bool]$j.AutoStart } else { $null }
        } catch { $snapshot.settings_autostart = '<parse-error>' }
    }

    # DeviceToken null check — only reports bool, never exposes value
    if (Test-Path -LiteralPath $deviceKeyPath) {
        try {
            $j = Get-Content -LiteralPath $deviceKeyPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $dtVal = if ($j.PSObject.Properties['DeviceToken']) { $j.DeviceToken } else { $null }
            $snapshot.device_token_is_null = [string]::IsNullOrEmpty($dtVal)
        } catch { $snapshot.device_token_is_null = '<parse-error>' }
    } else {
        $snapshot.device_token_is_null = '<file-absent>'
    }

    $snapshot.processes_openclaw = Get-OpenClawProcessSnapshot

    return $snapshot
}

function Get-TrayExePath {
    param([string]$Hint)

    # 1. Explicit hint from caller
    if ($Hint -and (Test-Path -LiteralPath $Hint)) { return $Hint }

    # 2. Same directory as this script (Inno install layout: script is in {app})
    $candidate = Join-Path $PSScriptRoot 'OpenClaw.Tray.WinUI.exe'
    if (Test-Path -LiteralPath $candidate) { return $candidate }

    # 3. Parent directory (repo layout: scripts\ sibling of publish\)
    $candidate = Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\OpenClaw.Tray.WinUI.exe'
    if (Test-Path -LiteralPath $candidate) { return $candidate }

    return $null
}

# ---------------------------------------------------------------------------
# Postcondition evaluation
# ---------------------------------------------------------------------------

function Get-Postconditions {
    # WSL distro absent?
    $wslDistroAbsent = -not (Test-DistroRegistered -Name $DistroName)

    # Autostart cleared: registry absent AND settings.AutoStart == false
    $regAbsent       = -not (Test-AutoStartRegistryPresent)
    $autoStartFalse  = $false
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $j = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $autoStartFalse = if ($j.PSObject.Properties['AutoStart']) { -not [bool]$j.AutoStart } else { $true }
        } catch { $autoStartFalse = $false }
    } else {
        # File absent means no auto-start configuration exists; treat as cleared.
        $autoStartFalse = $true
    }
    $autostartCleared = $regAbsent -and $autoStartFalse

    # setup-state.json absent?
    $setupStateAbsent = -not (Test-Path -LiteralPath $setupStatePath)

    # DeviceToken cleared: file absent OR DeviceToken null/empty
    $deviceTokenCleared = $false
    if (-not (Test-Path -LiteralPath $deviceKeyPath)) {
        $deviceTokenCleared = $true
    } else {
        try {
            $j = Get-Content -LiteralPath $deviceKeyPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $dtVal = if ($j.PSObject.Properties['DeviceToken']) { $j.DeviceToken } else { $null }
            $deviceTokenCleared = [string]::IsNullOrEmpty($dtVal)
        } catch { $deviceTokenCleared = $false }
    }

    # device-key-ed25519.json preserved (file itself must NOT have been deleted per v3 §C).
    # If the machine never ran setup the file may legitimately be absent;
    # in that case we report absent but do not fail (it can't be "preserved" if it never existed).
    $deviceKeyFilePreserved = Test-Path -LiteralPath $deviceKeyPath

    # mcp-token.txt preserved (never touched — any state is correct by definition per v3 §F).
    $mcpTokenPreserved = $true

    # No OpenClaw keepalive processes running.
    $keepalivePids    = @(Get-WslKeepalivePids -Name $DistroName)
    $keepalivesAbsent = ($keepalivePids.Count -eq 0)

    return [ordered]@{
        wsl_distro_absent         = $wslDistroAbsent
        autostart_cleared         = $autostartCleared
        setup_state_absent        = $setupStateAbsent
        device_token_cleared      = $deviceTokenCleared
        device_key_file_preserved = $deviceKeyFilePreserved
        mcp_token_preserved       = $mcpTokenPreserved
        keepalives_absent         = $keepalivesAbsent
        vhd_dir_absent            = (-not (Test-Path -LiteralPath $vhdDirPath))
    }
}

function Get-Verdict {
    param(
        [System.Collections.Specialized.OrderedDictionary]$Postconditions,
        [string[]]$Errors
    )

    # Required postconditions (device_key_file_preserved and mcp_token_preserved are advisory).
    $required   = @('wsl_distro_absent', 'autostart_cleared', 'setup_state_absent',
                    'device_token_cleared', 'keepalives_absent', 'vhd_dir_absent')
    $failedKeys = @($required | Where-Object { $Postconditions[$_] -ne $true })
    $errCount   = if ($null -eq $Errors) { 0 } else { @($Errors).Count }

    if ($failedKeys.Count -eq 0 -and $errCount -eq 0) { return 'PASS' }
    if ($failedKeys.Count -eq $required.Count)         { return 'FAIL' }
    return 'PARTIAL'
}

# ---------------------------------------------------------------------------
# 13-step uninstall execution (PowerShell mirror of LocalGatewayUninstall.cs)
# ---------------------------------------------------------------------------
# IMPORTANT: This sequence MUST stay aligned with:
#   src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewayUninstall.cs
# The C# engine remains the production code path.
# Any divergence found here should be filed as a commit-5 integration gap.
# ---------------------------------------------------------------------------

function Invoke-UninstallSteps {
    param([bool]$IsDryRun)

    $stepErrors = [System.Collections.Generic.List[string]]::new()

    # ------------------------------------------------------------------ Step 1
    # Preflight gate
    # ------------------------------------------------------------------ Step 1
    if ($IsDryRun) {
        Add-Step -Name 'preflight-gate' -Status 'DryRun' `
            -Message 'DryRun=true; no destructive changes will be made.'
    } else {
        Add-Step -Name 'preflight-gate' -Status 'Executed' `
            -Message 'ConfirmDestructive=true; proceeding with live uninstall.'
    }

    # ------------------------------------------------------------------ Step 2
    # Stop WSL keepalive process
    # ------------------------------------------------------------------ Step 2
    try {
        $kPids = @(Get-WslKeepalivePids -Name $DistroName)
        if ($kPids.Count -eq 0) {
            Add-Step -Name 'stop-keepalive-process' -Status 'Skipped' `
                -Message "No WSL keepalive process found for '$DistroName'."
        } elseif ($IsDryRun) {
            Add-Step -Name 'stop-keepalive-process' -Status 'DryRun' `
                -Message "Would stop $($kPids.Count) keepalive PID(s): $($kPids -join ', ')."
        } else {
            $stopped = 0
            foreach ($pid in $kPids) {
                try {
                    Stop-OpenClawProcessByPid -ProcessId $pid -Force
                    $stopped++
                } catch {
                    $stepErrors.Add("stop-keepalive-process PID $($pid): $($_.Exception.Message)")
                }
            }
            Add-Step -Name 'stop-keepalive-process' -Status 'Executed' `
                -Message "Stopped $stopped / $($kPids.Count) keepalive process(es)."
        }
    } catch {
        $stepErrors.Add("stop-keepalive-process: $($_.Exception.Message)")
        Add-Step -Name 'stop-keepalive-process' -Status 'Failed' -Message $_.Exception.Message
    }

    # ------------------------------------------------------------------ Step 3
    # Stop systemd gateway service inside WSL
    # ------------------------------------------------------------------ Step 3
    try {
        if (-not (Test-DistroRegistered -Name $DistroName)) {
            Add-Step -Name 'stop-systemd-gateway-service' -Status 'Skipped' `
                -Message "Distro '$DistroName' not registered."
        } elseif ($IsDryRun) {
            Add-Step -Name 'stop-systemd-gateway-service' -Status 'DryRun' `
                -Message "Would run: wsl -d $DistroName bash -c 'sudo systemctl stop openclaw-gateway 2>&1 || true'"
        } else {
            $r      = Invoke-WslCommand -Command 'sudo systemctl stop openclaw-gateway 2>&1 || true' -DistroName $DistroName
            $detail = ($r.Stdout + ' ' + $r.Stderr).Trim()
            Add-Step -Name 'stop-systemd-gateway-service' -Status 'Executed' `
                -Message (if ($detail) { $detail } else { 'Service stop command issued.' })
        }
    } catch {
        $stepErrors.Add("stop-systemd-gateway-service: $($_.Exception.Message)")
        Add-Step -Name 'stop-systemd-gateway-service' -Status 'Failed' -Message $_.Exception.Message
    }

    # ------------------------------------------------------------------ Step 4
    # Revoke operator token — best-effort HTTP call; gateway may already be down.
    # ------------------------------------------------------------------ Step 4
    try {
        $hasToken = $false
        $gwUrl    = 'ws://localhost:18789'
        if (Test-Path -LiteralPath $settingsPath) {
            $settingsJson = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($settingsJson.PSObject.Properties['Token']) {
                $hasToken = -not [string]::IsNullOrWhiteSpace($settingsJson.Token)
            }
            if ($settingsJson.PSObject.Properties['GatewayUrl']) {
                $gwUrl = $settingsJson.GatewayUrl
            }
        }

        if (-not $hasToken) {
            Add-Step -Name 'revoke-operator-token' -Status 'Skipped' `
                -Message 'No operator token in settings.json.'
        } elseif ($IsDryRun) {
            Add-Step -Name 'revoke-operator-token' -Status 'DryRun' `
                -Message 'Would POST to /api/v1/operator/disconnect. Token: ***REDACTED***'
        } else {
            $httpBase = ($gwUrl -replace '^ws://', 'http://' -replace '^wss://', 'https://').TrimEnd('/')
            $token    = $settingsJson.Token
            try {
                $resp = Invoke-WebRequest -Uri "$httpBase/api/v1/operator/disconnect" -Method Post `
                    -Headers @{ Authorization = "Bearer $token" } `
                    -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
                Add-Step -Name 'revoke-operator-token' -Status 'Executed' `
                    -Message "Response: $([int]$resp.StatusCode). Token: ***REDACTED***"
            } catch {
                # Gateway likely already down — absorb and continue.
                Add-Step -Name 'revoke-operator-token' -Status 'Executed' `
                    -Message "Best-effort revoke failed ($($_.Exception.GetType().Name)); gateway likely down. Token: ***REDACTED***"
            }
        }
    } catch {
        $stepErrors.Add("revoke-operator-token: $($_.Exception.Message)")
        Add-Step -Name 'revoke-operator-token' -Status 'Failed' -Message $_.Exception.Message
    }

    # ------------------------------------------------------------------ Step 5
    # Unregister WSL distro.
    # Safety guard: only "OpenClawGateway" may be unregistered (mirrors C# AllowedDistroName).
    # wsl --terminate first to cleanly stop the distro before unregistering.
    # ------------------------------------------------------------------ Step 5
    try {
        if ($DistroName -ne 'OpenClawGateway') {
            $guard = "Refused to unregister '$DistroName': only 'OpenClawGateway' is allowed."
            $stepErrors.Add($guard)
            Add-Step -Name 'unregister-wsl-distro' -Status 'Failed' -Message $guard
        } elseif (-not (Test-DistroRegistered -Name $DistroName)) {
            Add-Step -Name 'unregister-wsl-distro' -Status 'Skipped' `
                -Message "Distro '$DistroName' not registered."
        } elseif ($IsDryRun) {
            Add-Step -Name 'unregister-wsl-distro' -Status 'DryRun' `
                -Message "Would run: wsl --terminate $DistroName && wsl --unregister $DistroName"
        } else {
            & wsl --terminate $DistroName 2>&1 | Out-Null
            & wsl --unregister $DistroName 2>&1 | Out-Null
            $ec = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }
            if ($ec -eq 0 -or $ec -eq 1) {
                Add-Step -Name 'unregister-wsl-distro' -Status 'Executed' `
                    -Message "wsl --unregister completed (exit $ec)."
            } else {
                $msg = "wsl --unregister exited $ec."
                $stepErrors.Add($msg)
                Add-Step -Name 'unregister-wsl-distro' -Status 'Failed' -Message $msg
            }
        }
    } catch {
        $stepErrors.Add("unregister-wsl-distro: $($_.Exception.Message)")
        Add-Step -Name 'unregister-wsl-distro' -Status 'Failed' -Message $_.Exception.Message
    }

    # ------------------------------------------------------------------ Step 6
    # Reset autostart.
    # CRITICAL ORDERING (v3 §B): persist settings BEFORE deleting the registry value.
    # ------------------------------------------------------------------ Step 6
    try {
        if ($IsDryRun) {
            Add-Step -Name 'reset-autostart' -Status 'DryRun' `
                -Message "Would set settings.AutoStart=false and save, then delete HKCU\...\Run\$autoStartAppName."
        } else {
            # 6a — Update settings.json (AutoStart=false) FIRST.
            if (Test-Path -LiteralPath $settingsPath) {
                $sRaw = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8
                $sObj = $sRaw | ConvertFrom-Json
                $sObj.AutoStart = $false
                $sObj | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
                Add-Step -Name 'persist-settings-autostart-false' -Status 'Executed' `
                    -Message 'AutoStart=false persisted to settings.json.'
            } else {
                Add-Step -Name 'persist-settings-autostart-false' -Status 'Skipped' `
                    -Message 'settings.json not found; no AutoStart field to update.'
            }

            # 6b — Delete registry Run value SECOND (idempotent per v3 §B).
            $removed = Remove-AutoStartRegistryValue
            if ($removed) {
                Add-Step -Name 'delete-autostart-registry' -Status 'Executed' `
                    -Message "Deleted HKCU\...\Run\$autoStartAppName."
            } else {
                Add-Step -Name 'delete-autostart-registry' -Status 'Skipped' `
                    -Message "Registry value '$autoStartAppName' was not present."
            }
        }
    } catch {
        $stepErrors.Add("reset-autostart: $($_.Exception.Message)")
        Add-Step -Name 'reset-autostart' -Status 'Failed' -Message $_.Exception.Message
    }

    # ------------------------------------------------------------------ Step 7
    # Null DeviceToken field — preserve the file per v3 §C.
    # ------------------------------------------------------------------ Step 7
    try {
        if (-not (Test-Path -LiteralPath $deviceKeyPath)) {
            Add-Step -Name 'null-device-token' -Status 'Skipped' `
                -Message 'device-key-ed25519.json not found; nothing to null.'
        } elseif ($IsDryRun) {
            Add-Step -Name 'null-device-token' -Status 'DryRun' `
                -Message 'Would null DeviceToken field; keypair file preserved. Value: ***REDACTED***'
        } else {
            $kRaw = Get-Content -LiteralPath $deviceKeyPath -Raw -Encoding UTF8
            $kObj = $kRaw | ConvertFrom-Json
            $dtVal = if ($kObj.PSObject.Properties['DeviceToken']) { $kObj.DeviceToken } else { $null }
            if (-not [string]::IsNullOrEmpty($dtVal)) {
                $kObj.DeviceToken = $null
                if ($kObj.PSObject.Properties['DeviceTokenScopes']) { $kObj.DeviceTokenScopes = $null }
                $kObj | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $deviceKeyPath -Encoding UTF8
                Add-Step -Name 'null-device-token' -Status 'Executed' `
                    -Message 'DeviceToken set to null; keypair file preserved. Value: ***REDACTED***'
            } else {
                Add-Step -Name 'null-device-token' -Status 'Skipped' `
                    -Message 'DeviceToken already null or absent; file preserved.'
            }
        }
    } catch {
        $stepErrors.Add("null-device-token: $($_.Exception.Message)")
        Add-Step -Name 'null-device-token' -Status 'Failed' -Message $_.Exception.Message
    }

    # ------------------------------------------------------------------ Step 8
    # Delete setup-state.json
    # ------------------------------------------------------------------ Step 8
    try {
        if (-not (Test-Path -LiteralPath $setupStatePath)) {
            Add-Step -Name 'delete-setup-state' -Status 'Skipped' -Message 'setup-state.json not found.'
        } elseif ($IsDryRun) {
            Add-Step -Name 'delete-setup-state' -Status 'DryRun' `
                -Message "Would delete: $setupStatePath"
        } else {
            Remove-Item -LiteralPath $setupStatePath -Force
            Add-Step -Name 'delete-setup-state' -Status 'Executed' `
                -Message "Deleted $setupStatePath."
        }
    } catch {
        $stepErrors.Add("delete-setup-state: $($_.Exception.Message)")
        Add-Step -Name 'delete-setup-state' -Status 'Failed' -Message $_.Exception.Message
    }

    # ------------------------------------------------------------------ Step 9
    # Delete gateway logs (unless PreserveLogs=true).
    # ------------------------------------------------------------------ Step 9
    try {
        if ($PreserveLogs) {
            Add-Step -Name 'delete-gateway-logs' -Status 'Skipped' -Message 'PreserveLogs=true.'
        } elseif (-not (Test-Path -LiteralPath $logsDir)) {
            Add-Step -Name 'delete-gateway-logs' -Status 'Skipped' `
                -Message "Logs directory not found: $logsDir"
        } elseif ($IsDryRun) {
            $cnt = (Get-ChildItem -LiteralPath $logsDir -File -ErrorAction SilentlyContinue).Count
            Add-Step -Name 'delete-gateway-logs' -Status 'DryRun' `
                -Message "Would delete $cnt file(s) matching *.log / crash.log in $logsDir."
        } else {
            $logFiles = Get-ChildItem -LiteralPath $logsDir -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -eq '.log' -or $_.Name -eq 'crash.log' }
            $deleted = 0
            foreach ($f in $logFiles) {
                try { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop; $deleted++ } catch { }
            }
            Add-Step -Name 'delete-gateway-logs' -Status 'Executed' `
                -Message "Deleted $deleted log file(s)."
        }
    } catch {
        $stepErrors.Add("delete-gateway-logs: $($_.Exception.Message)")
        Add-Step -Name 'delete-gateway-logs' -Status 'Failed' -Message $_.Exception.Message
    }

    # ----------------------------------------------------------------- Step 10
    # Delete exec-policy.json (unless PreserveExecPolicy=true).
    # ----------------------------------------------------------------- Step 10
    try {
        if ($PreserveExecPolicy) {
            Add-Step -Name 'delete-exec-policy' -Status 'Skipped' -Message 'PreserveExecPolicy=true.'
        } elseif (-not (Test-Path -LiteralPath $execPolicyPath)) {
            Add-Step -Name 'delete-exec-policy' -Status 'Skipped' -Message 'exec-policy.json not found.'
        } elseif ($IsDryRun) {
            Add-Step -Name 'delete-exec-policy' -Status 'DryRun' `
                -Message "Would delete: $execPolicyPath"
        } else {
            Remove-Item -LiteralPath $execPolicyPath -Force
            Add-Step -Name 'delete-exec-policy' -Status 'Executed' `
                -Message "Deleted $execPolicyPath."
        }
    } catch {
        $stepErrors.Add("delete-exec-policy: $($_.Exception.Message)")
        Add-Step -Name 'delete-exec-policy' -Status 'Failed' -Message $_.Exception.Message
    }

    # ----------------------------------------------------------------- Step 11
    # Reset onboarding settings: Token, BootstrapToken, GatewayUrl.
    # EnableMcpServer is deliberately left as-is (v3 §F / Q-M):
    # it controls whether RequiresSetup fires post-uninstall.
    # ----------------------------------------------------------------- Step 11
    try {
        if ($IsDryRun) {
            Add-Step -Name 'reset-onboarding-settings' -Status 'DryRun' `
                -Message "Would reset Token='', BootstrapToken='', GatewayUrl='ws://localhost:18789'. EnableMcpServer preserved. Tokens: ***REDACTED***"
        } elseif (-not (Test-Path -LiteralPath $settingsPath)) {
            Add-Step -Name 'reset-onboarding-settings' -Status 'Skipped' `
                -Message 'settings.json not found.'
        } else {
            $sRaw = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8
            $sObj = $sRaw | ConvertFrom-Json
            foreach ($field in @('Token', 'BootstrapToken')) {
                if ($sObj.PSObject.Properties[$field]) { $sObj.$field = '' }
            }
            if ($sObj.PSObject.Properties['GatewayUrl']) {
                $sObj.GatewayUrl = 'ws://localhost:18789'
            }
            # EnableMcpServer: NOT touched.
            $sObj | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
            Add-Step -Name 'reset-onboarding-settings' -Status 'Executed' `
                -Message 'Token=***REDACTED***, BootstrapToken=***REDACTED***, GatewayUrl reset. EnableMcpServer preserved.'
        }
    } catch {
        $stepErrors.Add("reset-onboarding-settings: $($_.Exception.Message)")
        Add-Step -Name 'reset-onboarding-settings' -Status 'Failed' -Message $_.Exception.Message
    }

    # ----------------------------------------------------------------- Step 12
    # Preserve mcp-token.txt — no-op; logged for audit clarity.
    # Per v3 §F: mcp-token is not a gateway artifact and is NEVER deleted.
    # ----------------------------------------------------------------- Step 12
    Add-Step -Name 'preserve-mcp-token' -Status 'Skipped' `
        -Message 'mcp-token.txt preserved unconditionally (v3 §F). Not a gateway artifact.'

    return $stepErrors.ToArray()
}

# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------

function Write-SummaryMarkdown {
    param([System.Collections.Specialized.OrderedDictionary]$VerdictData)

    $lines = @(
        "# OpenClaw WSL Gateway Uninstall Validation",
        "",
        "| Field       | Value |",
        "|-------------|-------|",
        "| Mode        | $Mode |",
        "| DistroName  | $DistroName |",
        "| DryRun      | $($DryRun.IsPresent) |",
        "| Verdict     | $($VerdictData.verdict) |",
        "| StartedAt   | $($VerdictData.started_at) |",
        "| FinishedAt  | $($VerdictData.finished_at) |",
        "| OutputDir   | $OutputDir |",
        ""
    )

    if ($VerdictData.postconditions -and $VerdictData.postconditions.Count -gt 0) {
        $lines += "## Postconditions", ""
        foreach ($key in $VerdictData.postconditions.Keys) {
            $val  = $VerdictData.postconditions[$key]
            $icon = if ($val -eq $true) { 'PASS' } elseif ($val -eq $false) { 'FAIL' } else { 'INFO' }
            $lines += "- [$icon] $key : $val"
        }
        $lines += ""
    }

    if ($VerdictData.errors -and $VerdictData.errors.Count -gt 0) {
        $lines += "## Errors", ""
        foreach ($e in $VerdictData.errors) { $lines += "- $e" }
        $lines += ""
    }

    if ($script:steps.Count -gt 0) {
        $lines += "## Steps", ""
        foreach ($step in $script:steps) {
            $lines += "- [$($step.status)] $($step.name): $($step.message)"
        }
        $lines += ""
    }

    $lines | Set-Content -LiteralPath $summaryMdPath -Encoding UTF8
}

function Write-ColorVerdict {
    param([string]$Verdict)

    $color = switch ($Verdict) {
        'PASS'            { 'Green' }
        'DryRunComplete'  { 'Cyan' }
        'AlreadyClean'    { 'Cyan' }
        'PreflightOnly'   { 'Cyan' }
        'PARTIAL'         { 'Yellow' }
        'FAIL'            { 'Red' }
        'ERROR'           { 'Red' }
        default           { 'White' }
    }

    Write-Host ""
    Write-Host "════════════════════════════════════════════" -ForegroundColor $color
    Write-Host "  VERDICT : $Verdict"                         -ForegroundColor $color
    Write-Host "  Output  : $OutputDir"                       -ForegroundColor $color
    Write-Host "════════════════════════════════════════════" -ForegroundColor $color
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

$verdictData = [ordered]@{
    mode                 = $Mode
    dry_run              = $DryRun.IsPresent
    started_at           = (Get-Date).ToString("o")
    finished_at          = $null
    distro_name          = $DistroName
    preflight_passed     = $false
    destruction_executed = $false
    postconditions       = [ordered]@{}
    verdict              = 'UNKNOWN'
    errors               = @()
}

$exitCode = 0

try {
    switch ($Mode) {

        # ===================================================================
        'PreflightOnly' {
        # ===================================================================
            $registered = Test-DistroRegistered -Name $DistroName

            if (-not $registered) {
                Add-Step -Name 'distro-check' -Status 'Passed' `
                    -Message "Distro '$DistroName' is not registered — AlreadyClean."
                $verdictData.preflight_passed = $true
                $verdictData.verdict          = 'AlreadyClean'
                Write-Host "AlreadyClean: '$DistroName' not registered. Nothing to uninstall." -ForegroundColor Cyan
            } else {
                Add-Step -Name 'distro-check' -Status 'Passed' `
                    -Message "Distro '$DistroName' is registered."

                $preState = Get-StateSnapshot -Label 'preflight'
                $preState | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $preStatePath -Encoding UTF8
                Add-Step -Name 'pre-state-snapshot' -Status 'Completed' `
                    -Message "Pre-state snapshot written to: $preStatePath"

                $verdictData.preflight_passed = $true
                $verdictData.verdict          = 'PreflightOnly'
                Write-Host "PreflightOnly: '$DistroName' registered. Pre-state: $preStatePath" -ForegroundColor Green
            }
            $exitCode = 0
        }

        # ===================================================================
        'Full' {
        # ===================================================================
            # Pre-state snapshot.
            $preState = Get-StateSnapshot -Label 'pre-uninstall'
            $preState | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $preStatePath -Encoding UTF8
            Add-Step -Name 'pre-state-snapshot' -Status 'Completed' `
                -Message "Pre-state snapshot written to: $preStatePath"
            $verdictData.preflight_passed = $true

            if (-not $preState.distro_registered) {
                Write-Host "Note: '$DistroName' is not registered. Postcondition evaluation may show AlreadyClean." `
                    -ForegroundColor Yellow
            }

            $isDryRun   = $DryRun.IsPresent
            $stepErrors = @()

            # ----------------------------------------------------------------
            # CLI delegate path (default) — eliminates engine/script drift
            # -NoCli falls back to the inline PS replication below.
            # ----------------------------------------------------------------
            $trayExe    = Get-TrayExePath -Hint $ExePath
            $useCliPath = (-not $NoCli.IsPresent) -and ($null -ne $trayExe)

            if ($useCliPath) {
                Write-Host "Delegating to CLI: $trayExe" -ForegroundColor Cyan

                $cliJsonPath = Join-Path $OutputDir 'uninstall-result.json'
                $cliArgs     = @('--uninstall', '--json-output', $cliJsonPath)

                if ($isDryRun)            { $cliArgs += '--dry-run' }
                if (-not $isDryRun)       { $cliArgs += '--confirm-destructive' }

                try {
                    & $trayExe @cliArgs
                    $cliExitCode = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }

                    Add-Step -Name 'cli-uninstall-delegate' -Status 'Completed' `
                        -Message "Exit code: $cliExitCode. JSON: $cliJsonPath"
                    $verdictData.destruction_executed = -not $isDryRun

                    # Merge CLI result JSON into step audit trail
                    if (Test-Path -LiteralPath $cliJsonPath) {
                        try {
                            $cliResult = Get-Content -LiteralPath $cliJsonPath -Raw | ConvertFrom-Json
                            foreach ($step in $cliResult.steps) {
                                Add-Step -Name $step.name -Status $step.status -Message $step.detail
                            }
                            if ($cliResult.errors.Count -gt 0) {
                                $stepErrors = @($cliResult.errors)
                            }
                            # Persist CLI step JSON as the primary step audit trail
                            $cliJsonPath | Out-Null  # already written to OutputDir
                        } catch {
                            $stepErrors += "cli-result-parse: $($_.Exception.Message)"
                        }
                    }

                    if ($cliExitCode -ne 0) {
                        $stepErrors += "CLI exited $cliExitCode"
                    }
                } catch {
                    $stepErrors += "cli-delegate: $($_.Exception.Message)"
                    Add-Step -Name 'cli-uninstall-delegate' -Status 'Failed' `
                        -Message $_.Exception.Message
                }

            } else {
                # ----------------------------------------------------------------
                # Inline PS replication (diagnostic fallback / -NoCli)
                # ----------------------------------------------------------------
                if ($NoCli.IsPresent) {
                    Write-Host "Inline PS replication (-NoCli)." -ForegroundColor Yellow
                } else {
                    Write-Host "OpenClawTray.exe not found; falling back to inline PS replication." `
                        -ForegroundColor Yellow
                }

                $stepErrors = Invoke-UninstallSteps -IsDryRun $isDryRun
                $verdictData.destruction_executed = -not $isDryRun
            }

            $verdictData.errors = @($stepErrors)

            # Save step audit trail.
            $script:steps | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $stepsPath -Encoding UTF8

            # Post-state snapshot.
            $postState = Get-StateSnapshot -Label 'post-uninstall'
            $postState | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $postStatePath -Encoding UTF8
            Add-Step -Name 'post-state-snapshot' -Status 'Completed' `
                -Message "Post-state snapshot written to: $postStatePath"

            # Postconditions + verdict.
            if ($isDryRun) {
                $verdictData.verdict = 'DryRunComplete'
                $exitCode = 0
                Write-Host "DryRun complete. No state was mutated." -ForegroundColor Cyan
            } else {
                $postconditions          = Get-Postconditions
                $verdictData.postconditions = $postconditions
                $verdict                 = Get-Verdict -Postconditions $postconditions -Errors $stepErrors
                $verdictData.verdict     = $verdict
                $exitCode = if ($verdict -eq 'PASS') { 0 } else { 1 }
            }
        }

        # ===================================================================
        'PostconditionOnly' {
        # ===================================================================
            Add-Step -Name 'postcondition-check' -Status 'Running' `
                -Message 'Evaluating current state against uninstall-complete expectations.'

            $postconditions             = Get-Postconditions
            $verdictData.postconditions = $postconditions
            $verdict                    = Get-Verdict -Postconditions $postconditions -Errors @()
            $verdictData.verdict        = $verdict
            $exitCode = if ($verdict -eq 'PASS') { 0 } else { 1 }

            Add-Step -Name 'postcondition-check' -Status 'Completed' `
                -Message "Verdict: $verdict."
        }
    }

} catch {
    $verdictData.verdict = 'ERROR'
    $verdictData.errors  = @($verdictData.errors) + @($_.Exception.Message)
    Add-Step -Name 'execution-error' -Status 'Failed' -Message $_.Exception.Message
    $stackTrace = if ($_.ScriptStackTrace) { $_.ScriptStackTrace } else { '<no stack trace>' }
    Write-Host "ERROR: Unhandled error in -Mode $Mode : $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Stack: $stackTrace" -ForegroundColor Red
    $exitCode = 3

} finally {
    $verdictData.finished_at = (Get-Date).ToString("o")

    # Write verdict.json (always, even on error).
    $verdictData | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $verdictPath -Encoding UTF8

    # Write summary.md.
    Write-SummaryMarkdown -VerdictData $verdictData

    Write-Host "Verdict JSON : $verdictPath"
    Write-Host "Summary MD   : $summaryMdPath"
}

Write-ColorVerdict -Verdict $verdictData.verdict
exit $exitCode
