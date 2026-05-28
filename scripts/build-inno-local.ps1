<#
.SYNOPSIS
    Build local OpenClaw Companion Inno installers for quick validation.

.DESCRIPTION
    Publishes the tray app and SetupEngine.UI into a production-style layout,
    then runs ISCC to create local unsigned installers.

    Use -NoPublish after changing only installer.iss or docs/tests; it reuses
    the existing publish-local-* payloads and only recompiles Inno.

.EXAMPLE
    .\scripts\build-inno-local.ps1 -Arch x64 -Fast
    .\scripts\build-inno-local.ps1 -Arch All
    .\scripts\build-inno-local.ps1 -Arch x64 -NoPublish -Fast
#>

[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64", "All")]
    [string]$Arch = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Version,

    [switch]$NoPublish,

    [switch]$Fast,

    [switch]$InstallInno
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Resolve-InnoCompiler {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    if ($InstallInno) {
        Write-Step "Installing Inno Setup with winget"
        winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements --disable-interactivity
        if ($LASTEXITCODE -ne 0) {
            throw "winget failed to install Inno Setup."
        }
        return Resolve-InnoCompiler
    }

    throw "Inno Setup compiler (ISCC.exe) was not found. Install it, or rerun with -InstallInno."
}

function Get-ProjectVersion {
    $projectPath = Join-Path $repoRoot "src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj"
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $project.Project.PropertyGroup |
        ForEach-Object { $_.Version } |
        Where-Object { $_ } |
        Select-Object -First 1
}

function Get-RidForArch {
    param([string]$Architecture)
    if ($Architecture -eq "arm64") {
        return "win-arm64"
    }
    return "win-x64"
}

function Publish-ArchitecturePayload {
    param(
        [string]$Architecture,
        [string]$RuntimeIdentifier,
        [string]$PublishVersion
    )

    $publishDir = Join-Path $repoRoot "publish-local-$Architecture"
    $setupPublishDir = Join-Path $repoRoot "publish-local-setup-$Architecture"

    Write-Step "Publishing $Architecture payload"
    Remove-Item -LiteralPath $publishDir, $setupPublishDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $publishDir | Out-Null

    dotnet publish .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained `
        -p:Version=$PublishVersion `
        -o $publishDir `
        -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Tray publish failed for $Architecture."
    }

    dotnet publish .\src\OpenClaw.SetupEngine.UI\OpenClaw.SetupEngine.UI.csproj `
        -c $Configuration `
        -r $RuntimeIdentifier `
        --self-contained `
        -p:Version=$PublishVersion `
        -o $setupPublishDir `
        -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "SetupEngine.UI publish failed for $Architecture."
    }

    $setupDest = Join-Path $publishDir "SetupEngine"
    New-Item -ItemType Directory -Path $setupDest -Force | Out-Null
    Copy-Item -Path (Join-Path $setupPublishDir "*") -Destination $setupDest -Recurse -Force
}

function Assert-PayloadReady {
    param([string]$Architecture)

    $publishDir = Join-Path $repoRoot "publish-local-$Architecture"
    $trayExe = Join-Path $publishDir "OpenClaw.Tray.WinUI.exe"
    $setupExe = Join-Path $publishDir "SetupEngine\OpenClaw.SetupEngine.UI.exe"

    if (-not (Test-Path -LiteralPath $trayExe)) {
        throw "Missing tray payload at $trayExe. Rerun without -NoPublish."
    }

    if (-not (Test-Path -LiteralPath $setupExe)) {
        throw "Missing setup payload at $setupExe. Rerun without -NoPublish."
    }

    return $publishDir
}

function Invoke-InnoCompiler {
    param(
        [string]$InnoCompiler,
        [string]$Architecture,
        [string]$PublishDir,
        [string]$InstallerVersion
    )

    Write-Step "Compiling $Architecture installer"

    $args = @(
        "/DMyAppVersion=$InstallerVersion",
        "/DMyAppArch=$Architecture",
        "/Dpublish=$PublishDir"
    )

    if ($Fast) {
        $args += "/DMyCompression=zip"
        $args += "/DMySolidCompression=no"
    }

    $args += ".\installer.iss"

    & $InnoCompiler @args
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed for $Architecture."
    }
}

if (-not $Version) {
    $Version = Get-ProjectVersion
}

if (-not $Version) {
    throw "Could not determine a version. Pass -Version explicitly."
}

$iscc = Resolve-InnoCompiler
$architectures = if ($Arch -eq "All") { @("x64", "arm64") } else { @($Arch) }

Write-Step "Using ISCC: $iscc"
Write-Host "Version: $Version"
Write-Host "Configuration: $Configuration"
Write-Host "Fast compression: $($Fast.IsPresent)"
Write-Host "No publish: $($NoPublish.IsPresent)"

foreach ($architecture in $architectures) {
    $rid = Get-RidForArch $architecture
    if (-not $NoPublish) {
        Publish-ArchitecturePayload -Architecture $architecture -RuntimeIdentifier $rid -PublishVersion $Version
    }

    $payload = Assert-PayloadReady $architecture
    Invoke-InnoCompiler -InnoCompiler $iscc -Architecture $architecture -PublishDir $payload -InstallerVersion $Version
}

Write-Step "Built installers"
Get-ChildItem -Path (Join-Path $repoRoot "Output\OpenClawCompanion-Setup-*.exe") |
    Sort-Object Name |
    ForEach-Object {
        "{0}`t{1:N2} MB`t{2}" -f $_.FullName, ($_.Length / 1MB), $_.LastWriteTime
    }
