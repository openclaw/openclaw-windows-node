<#
.SYNOPSIS
    Build script for OpenClaw Windows Hub

.DESCRIPTION
    Builds all projects, checks prerequisites, and provides clear guidance.

.PARAMETER Project
    Which project to build: All, Tray, WinUI, Shared, CommandPalette, Cli
    Default: All

.PARAMETER Configuration
    Build configuration: Debug, Release
    Default: Debug

.PARAMETER CheckOnly
    Only check prerequisites, don't build

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Project WinUI -Configuration Release
    .\build.ps1 -CheckOnly
#>

param(
    [ValidateSet("All", "Tray", "WinUI", "Shared", "CommandPalette", "Cli", "WinNodeCli", "SetupEngine")]
    [string]$Project = "All",
    
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

# Colors for output
function Write-Header($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Success($text) { Write-Host "✅ $text" -ForegroundColor Green }
function Write-Warning($text) { Write-Host "⚠️  $text" -ForegroundColor Yellow }
function Write-Error($text) { Write-Host "❌ $text" -ForegroundColor Red }
function Write-Info($text) { Write-Host "   $text" -ForegroundColor Gray }

# Track issues
$issues = @()

Write-Host @"

  🦞 OpenClaw Windows Hub - Build Script
  =======================================

"@ -ForegroundColor Magenta

# =============================================================================
# PREREQUISITE CHECKS
# =============================================================================

Write-Header "Checking Prerequisites"

# Check OS
if ($env:OS -ne "Windows_NT") {
    Write-Error "This project requires Windows"
    exit 1
}
Write-Success "Windows detected"

# Check .NET SDK
$dotnetVersion = $null
try {
    $dotnetVersion = & dotnet --version 2>$null
} catch {}

if (-not $dotnetVersion) {
    Write-Error ".NET SDK not found"
    Write-Info "Download from: https://dotnet.microsoft.com/download"
    $issues += "Missing .NET SDK"
} else {
    Write-Success ".NET SDK: $dotnetVersion"
    
    # Check for .NET 10 (needed for all projects)
    $sdks = & dotnet --list-sdks 2>$null
    $hasNet10 = $sdks | Where-Object { $_ -match "^10\." }
    
    if (-not $hasNet10) {
        Write-Error ".NET 10 SDK not found (required for all projects)"
        Write-Info "Download preview from: https://dotnet.microsoft.com/download/dotnet/10.0"
        $issues += "Missing .NET 10 SDK"
    } else {
        Write-Success ".NET 10 SDK available"
    }
}

# Check Windows SDK (for WinUI)
$windowsSdkPath = "${env:ProgramFiles(x86)}\Windows Kits\10\Include"
if (Test-Path $windowsSdkPath) {
    $sdkVersions = Get-ChildItem $windowsSdkPath -Directory | Select-Object -ExpandProperty Name | Sort-Object -Descending
    Write-Success "Windows SDK: $($sdkVersions[0])"
} else {
    Write-Warning "Windows 10 SDK not found (needed for WinUI build)"
    Write-Info "Install via Visual Studio Installer or standalone SDK"
    $issues += "Windows 10 SDK not detected"
}

# Check WebView2 Runtime (for WinUI chat window)
$webView2Key = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
$webView2KeyAlt = "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
$webView2Version = $null

if (Test-Path $webView2Key) {
    $webView2Version = (Get-ItemProperty $webView2Key -ErrorAction SilentlyContinue).pv
} elseif (Test-Path $webView2KeyAlt) {
    $webView2Version = (Get-ItemProperty $webView2KeyAlt -ErrorAction SilentlyContinue).pv
}

if ($webView2Version) {
    Write-Success "WebView2 Runtime: $webView2Version"
} else {
    Write-Warning "WebView2 Runtime not detected (needed for WinUI chat window)"
    Write-Info "Usually pre-installed on Windows 10/11. Get from: https://developer.microsoft.com/microsoft-edge/webview2"
    # Not a hard failure - app will fall back to browser
}

# Check architecture
$arch = $env:PROCESSOR_ARCHITECTURE
Write-Success "Architecture: $arch"
if ($arch -eq "ARM64") {
    Write-Info "ARM64 detected - builds will target ARM64 by default"
}

# Summary
Write-Header "Prerequisite Summary"

if ($issues.Count -eq 0) {
    Write-Success "All prerequisites met!"
} else {
    Write-Warning "$($issues.Count) issue(s) found:"
    foreach ($issue in $issues) {
        Write-Info "- $issue"
    }
}

if ($CheckOnly) {
    Write-Host "`nRun without -CheckOnly to build.`n"
    exit 0
}

# =============================================================================
# BUILD
# =============================================================================

Write-Header "Building Projects ($Configuration)"

# Detect runtime identifier based on architecture
$rid = if ($arch -eq "ARM64") { "win-arm64" } else { "win-x64" }
Write-Info "Runtime identifier: $rid"

$buildResults = @{}

function Build-Project($name, $path, $useRid = $false) {
    Write-Host "`nBuilding $name..." -ForegroundColor White
    
    if (-not (Test-Path $path)) {
        Write-Error "Project not found: $path"
        return $false
    }
    
    # WinUI requires runtime identifier for self-contained WebView2 support
    if ($useRid) {
        $result = & dotnet build $path -c $Configuration -r $rid 2>&1
    } else {
        $result = & dotnet build $path -c $Configuration 2>&1
    }
    $exitCode = $LASTEXITCODE
    
    if ($exitCode -eq 0) {
        Write-Success "$name built successfully"
        return $true
    } else {
        Write-Error "$name build failed"
        # Show relevant error lines
        $result | Select-String "error" | Select-Object -First 5 | ForEach-Object {
            Write-Info $_.Line
        }
        return $false
    }
}

function Get-ProjectTargetFramework($path) {
    if (-not (Test-Path $path)) {
        return $null
    }

    [xml]$projectXml = Get-Content $path -Raw
    return $projectXml.Project.PropertyGroup |
        ForEach-Object { $_.TargetFramework } |
        Where-Object { $_ } |
        Select-Object -First 1
}

$projects = @{
    "Shared" = @{ Path = "src/OpenClaw.Shared/OpenClaw.Shared.csproj"; UseRid = $false }
    "Cli" = @{ Path = "src/OpenClaw.Cli/OpenClaw.Cli.csproj"; UseRid = $false }
    "WinNodeCli" = @{ Path = "src/OpenClaw.WinNode.Cli/OpenClaw.WinNode.Cli.csproj"; UseRid = $false }
    "Tray" = @{ Path = "src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj"; UseRid = $true }
    "WinUI" = @{ Path = "src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj"; UseRid = $true }
    "CommandPalette" = @{ Path = "src/OpenClaw.CommandPalette/OpenClaw.CommandPalette.csproj"; UseRid = $false }
    "SetupEngine" = @{ Path = "src/OpenClaw.SetupEngine.UI/OpenClaw.SetupEngine.UI.csproj"; UseRid = $true }
}

$toBuild = if ($Project -eq "All") { @("Shared", "Cli", "WinNodeCli", "SetupEngine", "WinUI") } else { @($Project) }

# Always build Shared first if building other projects
if ($Project -ne "Shared" -and $Project -ne "All" -and $toBuild -notcontains "Shared") {
    $toBuild = @("Shared") + $toBuild
}

foreach ($proj in $toBuild) {
    if ($projects.ContainsKey($proj)) {
        $projInfo = $projects[$proj]
        $buildResults[$proj] = Build-Project $proj $projInfo.Path $projInfo.UseRid
    }
}

# =============================================================================
# POST-BUILD: Copy SetupEngine.UI into WinUI output so the tray can find it
# =============================================================================
if (($buildResults.ContainsKey("SetupEngine") -and $buildResults["SetupEngine"]) -and
    (($buildResults.ContainsKey("WinUI") -and $buildResults["WinUI"]) -or ($buildResults.ContainsKey("Tray") -and $buildResults["Tray"]))) {
    $setupTfm = Get-ProjectTargetFramework $projects["SetupEngine"].Path
    $winUITfm = Get-ProjectTargetFramework $projects["WinUI"].Path
    if ($setupTfm -and $winUITfm) {
        $setupOutDir = "src\OpenClaw.SetupEngine.UI\bin\$Configuration\$setupTfm\$rid"
        $winUIOutDir = "src\OpenClaw.Tray.WinUI\bin\$Configuration\$winUITfm\$rid"
        $destDir = Join-Path $winUIOutDir "SetupEngine"
        if (Test-Path $setupOutDir) {
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            Copy-Item "$setupOutDir\*" $destDir -Recurse -Force
            Write-Info "Copied SetupEngine.UI output → $destDir"
        }
    }
}
# =============================================================================

Write-Header "Build Summary"

$successCount = ($buildResults.Values | Where-Object { $_ -eq $true }).Count
$failCount = ($buildResults.Values | Where-Object { $_ -eq $false }).Count

foreach ($proj in $buildResults.Keys) {
    if ($buildResults[$proj]) {
        Write-Success "$proj"
    } else {
        Write-Error "$proj"
    }
}

Write-Host ""
if ($failCount -eq 0) {
    Write-Host "🦞 All builds succeeded!" -ForegroundColor Green
    
    Write-Host "`nTo run:" -ForegroundColor Cyan
    if (($buildResults.ContainsKey("WinUI") -and $buildResults["WinUI"]) -or ($buildResults.ContainsKey("Tray") -and $buildResults["Tray"])) {
        $winUIProjectPath = $projects["WinUI"].Path
        $winUITargetFramework = Get-ProjectTargetFramework $winUIProjectPath
        $winUIProjectDirectory = (Split-Path -Parent $winUIProjectPath).Replace("/", "\")

        if ($winUITargetFramework) {
            $winUIOutputDirectory = ".\$winUIProjectDirectory\bin\$Configuration\$winUITargetFramework\$rid"
            $winUIManifestPath = ".\$winUIProjectDirectory\Package.appxmanifest"
            Write-Host "  WinUI:    .\run-app-local.ps1 -NoBuild" -ForegroundColor White
            Write-Host "            winapp run `"$winUIOutputDirectory`" --manifest `"$winUIManifestPath`" --debug-output" -ForegroundColor DarkGray
        } else {
            Write-Warning "Unable to determine WinUI target framework from $winUIProjectPath"
        }
    }
} else {
    Write-Host "❌ $failCount build(s) failed" -ForegroundColor Red
    exit 1
}

Write-Host ""
