<#
.SYNOPSIS
    Builds and launches the WinUI tray app for local development.

.DESCRIPTION
    Builds the tray app, then launches the unpackaged WinUI executable directly
    for the common local-development path.

    Use -UseWinApp when you specifically want Microsoft WinAppCLI (`winapp run`)
    to launch with Package.appxmanifest for packaged/MSIX-adjacent validation.

    Use -Isolated (or -DataDir) to run multiple worktrees side-by-side without
    sharing settings, logs, run markers, device identities, or mutex names.

    By default this helper refuses to run outside `main` to avoid accidentally
    launching a stale or experimental worktree. Use -AllowNonMain when you
    intentionally want to preview a PR or feature branch.

.PARAMETER NoBuild
    Skip the build step and launch the existing Debug output.

.PARAMETER Configuration
    Build/output configuration to use. Defaults to Debug.

.PARAMETER AllowNonMain
    Allow launching from a branch other than main.

    -AllowNonMaster is kept as a deprecated alias for one transition cycle.

.PARAMETER Isolated
    Set OPENCLAW_TRAY_DATA_DIR to a stable temp directory unique to this worktree
    and branch so multiple local launches can run side-by-side.

.PARAMETER DataDir
    Set OPENCLAW_TRAY_DATA_DIR to an explicit directory for this launch.

.PARAMETER UpdateChannel
    Set OPENCLAW_UPDATE_CHANNEL for this launch. Use alpha for prerelease update
    testing after building a lower-version Release baseline.

.PARAMETER UseWinApp
    Launch through Microsoft WinAppCLI (`winapp run`) with Package.appxmanifest
    instead of directly starting the unpackaged executable.

.PARAMETER NoDebugOutput
    With -UseWinApp, launch without winapp --debug-output.

.PARAMETER Wait
    Wait for the launched process to exit. Direct launches return immediately by
    default after printing the PID.

.PARAMETER DryRun
    Print the launch command and environment without starting the app.

.EXAMPLE
    .\run-app-local.ps1

.EXAMPLE
    .\run-app-local.ps1 -NoBuild

.EXAMPLE
    .\run-app-local.ps1 -Isolated

.EXAMPLE
    .\run-app-local.ps1 -Configuration Release -Isolated -UpdateChannel alpha -AllowNonMain

.EXAMPLE
    .\run-app-local.ps1 -UseWinApp -NoBuild
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [Alias("AllowNonMaster")]
    [switch]$AllowNonMain,

    [switch]$Isolated,

    [string]$DataDir,

    [ValidateSet("stable", "alpha", "prerelease")]
    [string]$UpdateChannel,

    [switch]$UseWinApp,

    [switch]$NoDebugOutput,

    [switch]$Wait,

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

function Resolve-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ([System.IO.Path]::IsPathRooted($expanded)) {
        return [System.IO.Path]::GetFullPath($expanded)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $expanded))
}

function Get-SafePathSegment {
    param([Parameter(Mandatory = $true)][string]$Value)

    $safe = [regex]::Replace($Value, "[^A-Za-z0-9._-]+", "-").Trim("-")
    if ($safe) { return $safe }
    return "default"
}

function Get-ShortHash {
    param([Parameter(Mandatory = $true)][string]$Value)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant()))
        return -join ($bytes[0..3] | ForEach-Object { $_.ToString("x2") })
    } finally {
        $sha.Dispose()
    }
}

$branch = (git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne "main" -and -not $AllowNonMain) {
    throw "Refusing to run: current branch is '$branch', expected 'main'. Use -AllowNonMain to preview this branch intentionally."
}

if (-not $NoBuild) {
    & "$repoRoot\build.ps1" -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
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
$exePath = Join-Path $outputDir "OpenClaw.Tray.WinUI.exe"

if (-not (Test-Path $outputDir)) {
    throw "Build output folder not found: $outputDir. Run without -NoBuild first."
}
if (-not (Test-Path $exePath)) {
    throw "Tray executable not found: $exePath. Run without -NoBuild first."
}

$winapp = $null
if ($UseWinApp) {
    $winapp = Get-Command winapp -ErrorAction SilentlyContinue
    if (-not $winapp) {
        throw "winapp CLI was not found. Install Microsoft WinAppCLI (winget install Microsoft.WinAppCLI) or run /winui-setup."
    }
}

if ($UseWinApp -and -not (Test-Path $manifestPath)) {
    throw "Manifest not found: $manifestPath."
}

$previousDataDir = $env:OPENCLAW_TRAY_DATA_DIR
$previousUpdateChannel = $env:OPENCLAW_UPDATE_CHANNEL
$exitCode = 0

try {
    $effectiveDataDir = $null
    if ($DataDir) {
        $effectiveDataDir = Resolve-FullPath $DataDir
    } elseif ($Isolated) {
        $repoName = Get-SafePathSegment (Split-Path -Leaf $repoRoot)
        $safeBranch = Get-SafePathSegment $branch
        $repoHash = Get-ShortHash $repoRoot
        $effectiveDataDir = Join-Path $env:TEMP "OpenClawTray\$repoName-$safeBranch-$repoHash"
    }

    if ($effectiveDataDir) {
        New-Item -ItemType Directory -Path $effectiveDataDir -Force | Out-Null
        $env:OPENCLAW_TRAY_DATA_DIR = $effectiveDataDir
    }

    if ($UpdateChannel) {
        $env:OPENCLAW_UPDATE_CHANNEL = $UpdateChannel
    }

    if ($UseWinApp) {
        $winappArgs = @("run", $outputDir, "--manifest", $manifestPath, "--executable", "OpenClaw.Tray.WinUI.exe")
        if (-not $NoDebugOutput) {
            $winappArgs += "--debug-output"
        }
    }

    Write-Host "Launching OpenClaw Tray" -ForegroundColor Cyan
    Write-Host "  Branch:        $branch"
    Write-Host "  Configuration: $Configuration"
    Write-Host "  Runtime:       $runtimeIdentifier"
    Write-Host "  Output:        $outputDir"
    Write-Host "  Mode:          $(if ($UseWinApp) { 'WinAppCLI manifest activation' } else { 'Direct unpackaged executable' })"
    if ($env:OPENCLAW_TRAY_DATA_DIR) {
        Write-Host "  Data dir:      $env:OPENCLAW_TRAY_DATA_DIR"
    }
    if ($env:OPENCLAW_UPDATE_CHANNEL) {
        Write-Host "  Update channel: $env:OPENCLAW_UPDATE_CHANNEL"
    }
    if ($UseWinApp) {
        Write-Host "  Launcher:      $($winapp.Source)"
        Write-Host "  Command:       winapp $($winappArgs -join ' ')"
    } else {
        Write-Host "  Launcher:      $exePath"
    }

    if ($DryRun) {
        return
    }

    if ($UseWinApp) {
        & $winapp.Source @winappArgs
        $exitCode = $LASTEXITCODE
    } elseif ($Wait) {
        $process = Start-Process -FilePath $exePath -WorkingDirectory $outputDir -Wait -PassThru
        $exitCode = $process.ExitCode
    } else {
        $process = Start-Process -FilePath $exePath -WorkingDirectory $outputDir -PassThru
        Write-Host "Started OpenClaw Tray (PID: $($process.Id))" -ForegroundColor Green
        $exitCode = 0
    }
} finally {
    $env:OPENCLAW_TRAY_DATA_DIR = $previousDataDir
    $env:OPENCLAW_UPDATE_CHANNEL = $previousUpdateChannel
}

exit $exitCode
