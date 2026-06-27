<#
.SYNOPSIS
    Prepares a Windows checkout for OpenClaw developer and agent work.

.DESCRIPTION
    Installs missing local prerequisites with winget, refreshes the current
    process PATH, trusts the checkout for GitVersion, and runs the repository
    prerequisite check. Use -CheckOnly to report what is missing without
    installing anything.

.PARAMETER CheckOnly
    Check prerequisites without installing or changing git safe.directory.

.PARAMETER RunValidation
    After setup, run the full build plus the required shared and tray test
    projects used by AGENTS.md closeout validation.

.PARAMETER NoTrustRepository
    Do not add this checkout to git safe.directory.

.EXAMPLE
    .\scripts\setup-dev.ps1
    .\scripts\setup-dev.ps1 -CheckOnly
    .\scripts\setup-dev.ps1 -RunValidation
#>

param(
    [switch]$CheckOnly,
    [switch]$RunValidation,
    [switch]$NoTrustRepository
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Write-Header($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Success($text) { Write-Host "[OK] $text" -ForegroundColor Green }
function Write-WarningMessage($text) { Write-Host "[WARN] $text" -ForegroundColor Yellow }
function Write-ErrorMessage($text) { Write-Host "[ERROR] $text" -ForegroundColor Red }
function Write-Info($text) { Write-Host "  $text" -ForegroundColor Gray }

function Test-WindowsHost {
    $isWindowsVariable = Get-Variable -Name IsWindows -ErrorAction SilentlyContinue
    if ($isWindowsVariable) {
        return [bool]$isWindowsVariable.Value
    }

    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function Update-ProcessPath {
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path = @($machinePath, $userPath) -join ";"
}

function Test-CommandAvailable($name) {
    return $null -ne (Get-Command $name -ErrorAction SilentlyContinue)
}

function Test-DotNet10Sdk {
    if (-not (Test-CommandAvailable "dotnet")) {
        return $false
    }

    $sdks = & dotnet --list-sdks 2>$null
    return $LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match "^10\." })
}

function Test-NodeAndNpm {
    return (Test-CommandAvailable "node") -and (Test-CommandAvailable "npm")
}

function Get-WindowsSdkVersion {
    $windowsSdkPath = "${env:ProgramFiles(x86)}\Windows Kits\10\Include"
    if (-not (Test-Path $windowsSdkPath)) {
        return $null
    }

    $versions = @(
        Get-ChildItem $windowsSdkPath -Directory |
            Where-Object { $_.Name -match "^\d+\.\d+\.\d+\.\d+$" } |
            Sort-Object { [version]$_.Name } -Descending |
            Select-Object -ExpandProperty Name
    )

    if ($versions.Count -eq 0) {
        return $null
    }

    return $versions[0]
}

function Get-WebView2RuntimeVersion {
    $keys = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    )

    foreach ($key in $keys) {
        if (Test-Path $key) {
            $version = (Get-ItemProperty $key -ErrorAction SilentlyContinue).pv
            if ($version) {
                return $version
            }
        }
    }

    return $null
}

function Install-WingetPackage($id, $displayName) {
    if ($CheckOnly) {
        Write-WarningMessage "$displayName is missing."
        Write-Info "Install with: winget install --id $id -e"
        return
    }

    if (-not (Test-CommandAvailable "winget")) {
        throw "winget is not available. Install App Installer from the Microsoft Store, then rerun this script."
    }

    Write-Header "Installing $displayName"
    $arguments = @(
        "install",
        "--id", $id,
        "-e",
        "--accept-source-agreements",
        "--accept-package-agreements",
        "--disable-interactivity"
    )
    & winget @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install $displayName ($id)."
    }

    Update-ProcessPath
}

function ConvertTo-GitSafeDirectoryPath($path) {
    return ([System.IO.Path]::GetFullPath($path).TrimEnd("\") -replace "\\", "/")
}

function Test-GitSafeDirectoryContains($path) {
    if (-not (Test-CommandAvailable "git")) {
        return $false
    }

    $expected = (ConvertTo-GitSafeDirectoryPath $path).ToLowerInvariant()
    $safeDirectories = & git config --global --get-all safe.directory 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $safeDirectories) {
        return $false
    }

    foreach ($safeDirectory in $safeDirectories) {
        if ($safeDirectory -eq "*") {
            return $true
        }

        $normalized = ($safeDirectory.Trim().TrimEnd("\", "/") -replace "\\", "/").ToLowerInvariant()
        if ($normalized -eq $expected) {
            return $true
        }
    }

    return $false
}

function Ensure-RepositoryTrust {
    if ($NoTrustRepository -or $CheckOnly -or -not (Test-CommandAvailable "git")) {
        return
    }

    if (-not (Test-Path (Join-Path $repoRoot ".git"))) {
        return
    }

    if (Test-GitSafeDirectoryContains $repoRoot) {
        Write-Success "Repository already trusted for GitVersion."
        return
    }

    $safeDirectory = ConvertTo-GitSafeDirectoryPath $repoRoot
    Write-Info "Adding git safe.directory entry: $safeDirectory"
    & git config --global --add safe.directory $safeDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to add git safe.directory entry for $safeDirectory."
    }
    Write-Success "Repository trusted for GitVersion."
}

function Require-Prerequisite($name, $isAvailable, $packageId) {
    if ($isAvailable) {
        Write-Success "$name detected."
        return
    }

    Install-WingetPackage $packageId $name
}

if (-not (Test-WindowsHost)) {
    throw "OpenClaw Windows development requires Windows."
}

Write-Header "OpenClaw developer setup"
if ($CheckOnly) {
    Write-Info "CheckOnly mode: no packages will be installed and git safe.directory will not be changed."
}

Update-ProcessPath

Require-Prerequisite "Git" (Test-CommandAvailable "git") "Git.Git"
Require-Prerequisite ".NET 10 SDK" (Test-DotNet10Sdk) "Microsoft.DotNet.SDK.10"
Require-Prerequisite "Node.js LTS with npm" (Test-NodeAndNpm) "OpenJS.NodeJS.LTS"
Require-Prerequisite "Windows SDK 10.0.26100" ([bool](Get-WindowsSdkVersion)) "Microsoft.WindowsSDK.10.0.26100"

$webView2Version = Get-WebView2RuntimeVersion
if ($webView2Version) {
    Write-Success "WebView2 Runtime detected ($webView2Version)."
} else {
    Install-WingetPackage "Microsoft.EdgeWebView2Runtime" "WebView2 Runtime"
}

Update-ProcessPath
Ensure-RepositoryTrust

$missing = @()
if (-not (Test-CommandAvailable "git")) { $missing += "Git" }
if (-not (Test-DotNet10Sdk)) { $missing += ".NET 10 SDK" }
if (-not (Test-NodeAndNpm)) { $missing += "Node.js LTS with npm" }
if (-not (Get-WindowsSdkVersion)) { $missing += "Windows SDK 10.0.26100" }
if (-not (Get-WebView2RuntimeVersion)) { $missing += "WebView2 Runtime" }

if ($missing.Count -gt 0) {
    Write-ErrorMessage "Setup is incomplete:"
    foreach ($item in $missing) {
        Write-Info "- $item"
    }

    if (-not $CheckOnly) {
        Write-Info "If packages were just installed, open a new terminal and rerun .\scripts\setup-dev.ps1 -CheckOnly."
    }
    exit 1
}

Write-Header "Repository prerequisite check"
& "$repoRoot\build.ps1" -CheckOnly
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($RunValidation) {
    Write-Header "Required validation"
    $env:OPENCLAW_REPO_ROOT = $repoRoot

    & "$repoRoot\build.ps1"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet test "$repoRoot\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj" --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet test "$repoRoot\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj" --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Success "OpenClaw developer setup is ready."
