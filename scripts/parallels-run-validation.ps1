param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot,
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [Parameter(Mandatory = $true)]
    [string]$DonePath,
    [Parameter(Mandatory = $true)]
    [string]$PidPath
)

$ErrorActionPreference = "Stop"
$exitCode = 1

try {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) | Out-Null
    Remove-Item -LiteralPath $LogPath, $DonePath, $PidPath -Force -ErrorAction SilentlyContinue
    Set-Content -LiteralPath $PidPath -Value $PID -Encoding ASCII
    Set-Location $RepoRoot
    $env:OPENCLAW_REPO_ROOT = $RepoRoot

    & (Join-Path $RepoRoot "scripts\setup-dev.ps1") -RunValidation *> $LogPath
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Required validation failed with exit code $exitCode."
    }
} catch {
    $_ | Out-String | Add-Content -LiteralPath $LogPath -Encoding UTF8
    if ($exitCode -eq 0) {
        $exitCode = 1
    }
} finally {
    Set-Content -LiteralPath $DonePath -Value $exitCode -Encoding ASCII
}

exit $exitCode
