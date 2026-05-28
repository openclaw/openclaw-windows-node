<#
.SYNOPSIS
    Builds and launches the WinUI tray app for local development.

.DESCRIPTION
    Uses `winapp run` with the app package manifest so local launches match the
    Windows App SDK activation path used during validation. Do not launch the
    generated OpenClaw.Tray.WinUI.exe directly; direct EXE startup bypasses the
    manifest/package identity path and can hide launch-time issues.

    By default this helper refuses to run outside `master` to avoid accidentally
    launching a stale or experimental worktree. Use -AllowNonMaster when you
    intentionally want to preview a PR or feature branch.

.PARAMETER NoBuild
    Skip the build step and launch the existing Debug output.

.PARAMETER Configuration
    Build/output configuration to use. Defaults to Debug.

.PARAMETER AllowNonMaster
    Allow launching from a branch other than master.

.EXAMPLE
    .\run-app-local.ps1

.EXAMPLE
    .\run-app-local.ps1 -NoBuild

.EXAMPLE
    .\run-app-local.ps1 -AllowNonMaster
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$AllowNonMaster
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne "master" -and -not $AllowNonMaster) {
    throw "Refusing to run: current branch is '$branch', expected 'master'. Use -AllowNonMaster to preview this branch intentionally."
}

if (-not $NoBuild) {
    & "$repoRoot\build.ps1" -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$winapp = Get-Command winapp -ErrorAction SilentlyContinue
if (-not $winapp) {
    throw "winapp CLI was not found. Install Microsoft WinAppCLI or run this from an environment where winapp is on PATH."
}

$projectPath = Join-Path $repoRoot "src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj"
$manifestPath = Join-Path $repoRoot "src\OpenClaw.Tray.WinUI\Package.appxmanifest"
[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
$targetFramework = ($projectXml.Project.PropertyGroup | Where-Object { $_.TargetFramework } | Select-Object -First 1).TargetFramework
if (-not $targetFramework) {
    throw "Unable to determine TargetFramework from $projectPath."
}

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$runtimeIdentifier = if ($architecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { "win-arm64" } else { "win-x64" }
$outputDir = Join-Path $repoRoot "src\OpenClaw.Tray.WinUI\bin\$Configuration\$targetFramework\$runtimeIdentifier"

if (-not (Test-Path $outputDir)) {
    throw "Build output folder not found: $outputDir. Run without -NoBuild first."
}
if (-not (Test-Path $manifestPath)) {
    throw "Manifest not found: $manifestPath."
}

Write-Host "Launching OpenClaw Tray with winapp ($runtimeIdentifier, $Configuration)..."
& $winapp.Source run $outputDir --manifest $manifestPath --debug-output
exit $LASTEXITCODE
