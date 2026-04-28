# provision-wsl.ps1 — Run WSL provisioning from Windows
# Usage: .\scripts\provision-wsl.ps1

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$bashScript = Join-Path $scriptDir "provision-wsl.sh"

if (-not (Test-Path $bashScript)) {
    Write-Host "❌ Cannot find provision-wsl.sh at: $bashScript" -ForegroundColor Red
    exit 1
}

# Convert Windows path to WSL path
$wslPath = wsl wslpath -u ($bashScript -replace '\\', '/')

Write-Host "🦞 Running WSL provisioning..." -ForegroundColor Cyan
wsl bash -c "bash '$wslPath'"

if ($LASTEXITCODE -eq 0) {
    Write-Host "`n✅ WSL provisioning complete!" -ForegroundColor Green
} else {
    Write-Host "`n❌ WSL provisioning failed (exit code: $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}
