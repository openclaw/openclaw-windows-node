<#
.SYNOPSIS
    Inner-loop development script for OpenClaw Windows tray app.

.DESCRIPTION
    Builds the tray app, kills any running instance, and launches from build output.
    Use -Clean to clear settings and trigger first-run onboarding.
    Use -Tail to watch the log file in real-time after launch.

.EXAMPLE
    .\dev-loop.ps1              # Build + launch (keeps existing settings)
    .\dev-loop.ps1 -Clean       # Build + launch with clean settings (triggers onboarding)
    .\dev-loop.ps1 -Clean -Tail # Build + launch + watch logs
    .\dev-loop.ps1 -BuildOnly   # Build only, don't launch
    .\dev-loop.ps1 -Tail        # Build + launch + tail logs
#>
param(
    [switch]$Clean,
    [switch]$BuildOnly,
    [switch]$Tail
)

$ErrorActionPreference = "Stop"

$settingsFile = "$env:APPDATA\OpenClawTray\settings.json"
$logFile = "$env:LOCALAPPDATA\OpenClawTray\openclaw-tray.log"

# Detect architecture
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "win-arm64" } else { "win-x64" }
$exe = Join-Path $PSScriptRoot "src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.19041.0\$arch\OpenClaw.Tray.WinUI.exe"

Write-Host ""
Write-Host "  OpenClaw Dev Loop ($arch)" -ForegroundColor Magenta
Write-Host ""

# Kill running instance
$running = Get-Process "OpenClaw.Tray.WinUI" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "  Stopping running instance (PID $($running.Id))..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# Clean settings
if ($Clean) {
    if (Test-Path $settingsFile) {
        Copy-Item $settingsFile "$settingsFile.bak" -Force
        Remove-Item $settingsFile -Force
        Write-Host "  [OK] Settings cleared (backup at settings.json.bak)" -ForegroundColor Yellow
    } else {
        Write-Host "  [OK] No settings to clear (already clean)" -ForegroundColor Gray
    }
}

# Build
Write-Host "  Building WinUI..." -ForegroundColor Cyan
& "$PSScriptRoot\build.ps1" -Project WinUI
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "  [FAIL] Build failed!" -ForegroundColor Red
    Write-Host ""
    exit 1
}

if (-not (Test-Path $exe)) {
    Write-Host ""
    Write-Host "  [FAIL] Exe not found at: $exe" -ForegroundColor Red
    Write-Host "  Try a full build first: .\build.ps1" -ForegroundColor Gray
    Write-Host ""
    exit 1
}

if ($BuildOnly) {
    Write-Host ""
    Write-Host "  [OK] Build complete. Exe at:" -ForegroundColor Green
    Write-Host "    $exe" -ForegroundColor Gray
    Write-Host ""
    exit 0
}

# Launch
Write-Host ""
Write-Host "  Launching..." -ForegroundColor Green
$cleanLabel = if ($Clean) { " (clean start - onboarding will appear)" } else { "" }
Write-Host "  $exe$cleanLabel" -ForegroundColor Gray
Start-Process $exe

# Tail logs
if ($Tail) {
    Start-Sleep -Seconds 2
    if (Test-Path $logFile) {
        Write-Host ""
        Write-Host "  Tailing $logFile (Ctrl+C to stop)" -ForegroundColor Gray
        Write-Host ""
        Get-Content $logFile -Wait -Tail 30
    } else {
        Write-Host ""
        Write-Host "  Log file not found yet: $logFile" -ForegroundColor Yellow
        Write-Host "  Waiting for app to create it..." -ForegroundColor Gray
        while (-not (Test-Path $logFile)) { Start-Sleep -Seconds 1 }
        Get-Content $logFile -Wait -Tail 30
    }
} else {
    Write-Host ""
    Write-Host "  [OK] App launched. Quick commands:" -ForegroundColor Gray
    Write-Host "    Tail logs:    Get-Content `"$logFile`" -Wait -Tail 30" -ForegroundColor Gray
    Write-Host "    Check config: Get-Content `"$settingsFile`" | ConvertFrom-Json" -ForegroundColor Gray
    Write-Host "    Re-run:       .\dev-loop.ps1" -ForegroundColor Gray
    Write-Host "    Clean start:  .\dev-loop.ps1 -Clean" -ForegroundColor Gray
    Write-Host ""
}
