<#
.SYNOPSIS
    Inno Setup [UninstallRun] helper — removes the local WSL gateway via the
    OpenClaw tray CLI flag.

.DESCRIPTION
    INNO ORDERING CONTRACT
    ----------------------
    Per Inno Setup documentation, [UninstallRun] entries execute BEFORE the
    {app} directory is deleted.  OpenClawTray.exe is therefore guaranteed to
    be present when this script runs.

    WHAT THIS SCRIPT DOES
    ---------------------
    1. Locates OpenClawTray.exe in the same directory as this script ({app}).
    2. Invokes: OpenClawTray.exe --uninstall --confirm-destructive --json-output <log>
    3. Logs success or failure to {app}\uninstall-gateway-result.json.
    4. If the EXE is missing (e.g., partial install), logs the error and exits 0
       so the Inno uninstaller continues.  The user may need to clean up manually
       (see docs\uninstall-portable.md for manual steps).

    FALLBACK
    --------
    Exit 0 in all error cases so Inno does not abort the uninstall if gateway
    cleanup fails.  The result JSON captures the failure for post-mortem.

.NOTES
    Date:   2026-05-07
    Author: Aaron (Backend / Infrastructure Engineer)
    Branch: feat/wsl-gateway-uninstall
    Commit: 5 of 7

    Token / key material is NEVER written to the result log; the engine
    and CLI layer both redact sensitive fields before serializing.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir  = $PSScriptRoot
$exePath    = Join-Path $scriptDir 'OpenClaw.Tray.WinUI.exe'
$resultPath = Join-Path $scriptDir 'uninstall-gateway-result.json'
$errorPath  = Join-Path $scriptDir 'uninstall-gateway-error.log'

# ---------------------------------------------------------------------------
# EXE presence check — fallback if somehow missing
# ---------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $exePath)) {
    $msg = "[$(Get-Date -Format 'o')] Uninstall-LocalGateway.ps1: " +
           "OpenClawTray.exe not found at '$exePath'. " +
           "WSL gateway cleanup skipped.  Manual cleanup may be required."
    try { $msg | Out-File -LiteralPath $errorPath -Encoding UTF8 -Force } catch {}
    Write-Warning $msg
    exit 0
}

# ---------------------------------------------------------------------------
# Invoke CLI uninstall
# ---------------------------------------------------------------------------
$exitCode = 0
try {
    & $exePath --uninstall --confirm-destructive --json-output $resultPath
    $exitCode = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }

    if ($exitCode -eq 0) {
        Write-Host "OpenClaw local WSL gateway removed successfully." -ForegroundColor Green
    } else {
        Write-Warning "OpenClaw gateway uninstall exited $exitCode; see '$resultPath' for details."
    }
} catch {
    $msg = "[$(Get-Date -Format 'o')] Uninstall-LocalGateway.ps1 error: $($_.Exception.Message)"
    try { $msg | Out-File -LiteralPath $errorPath -Encoding UTF8 -Force } catch {}
    Write-Warning $msg
}

# Always exit 0 so Inno does not abort the broader uninstall.
exit 0
