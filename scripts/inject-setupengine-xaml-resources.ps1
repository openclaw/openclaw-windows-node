<#
.SYNOPSIS
  Injects SetupEngine WinUI XAML resources into an unsigned MSIX package.

.DESCRIPTION
  The tray MSIX package owns its own PRI resource map. SetupEngine.UI is a
  separate self-contained WinUI process copied under SetupEngine\. Its .xbf files
  and OpenClaw.SetupEngine.UI.pri must be present beside the child executable,
  but if those files are present while the tray project generates its PRI, their
  resource keys collide with tray XAML resources that share names like
  Pages/PermissionsPage.xbf.

  CI therefore builds the tray MSIX with the non-XAML SetupEngine payload, then
  uses this script to inject SetupEngine's child-process XAML resources into the
  unsigned MSIX before signing.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $MsixPath,

    [Parameter(Mandatory)]
    [string] $SetupEnginePublishDir,

    [string] $MakeAppxPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $MsixPath -PathType Leaf)) {
    throw "MSIX not found: $MsixPath"
}
if (-not (Test-Path -LiteralPath $SetupEnginePublishDir -PathType Container)) {
    throw "SetupEngine publish directory not found: $SetupEnginePublishDir"
}

if ([string]::IsNullOrWhiteSpace($MakeAppxPath)) {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $MakeAppxPath = Get-ChildItem $kitsRoot -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($MakeAppxPath)) {
        $globalPackages = (dotnet nuget locals global-packages -l) -replace '^global-packages:\s*', ''
        $MakeAppxPath = Get-ChildItem $globalPackages -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}
if ([string]::IsNullOrWhiteSpace($MakeAppxPath) -or -not (Test-Path -LiteralPath $MakeAppxPath -PathType Leaf)) {
    throw "makeappx.exe not found. Pass -MakeAppxPath or install Windows SDK build tools."
}

$resources = Get-ChildItem -LiteralPath $SetupEnginePublishDir -Recurse -File |
    Where-Object { $_.Extension -eq '.xbf' -or $_.Name -eq 'OpenClaw.SetupEngine.UI.pri' }
if (-not $resources) {
    throw "No SetupEngine XAML resources found in $SetupEnginePublishDir"
}
if (-not ($resources | Where-Object { $_.Name -eq 'OpenClaw.SetupEngine.UI.pri' })) {
    throw "SetupEngine publish output is missing OpenClaw.SetupEngine.UI.pri"
}
if (-not ($resources | Where-Object { $_.Name -eq 'SetupWindow.xbf' })) {
    throw "SetupEngine publish output is missing SetupWindow.xbf"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "openclaw-msix-inject-$([System.Guid]::NewGuid().ToString('N'))"
$extractDir = Join-Path $tempRoot 'package'
$repacked = Join-Path $tempRoot ([System.IO.Path]::GetFileName($MsixPath))

try {
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    & $MakeAppxPath unpack /p $MsixPath /d $extractDir /o | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx.exe unpack failed with exit code $LASTEXITCODE"
    }

    $publishRoot = (Resolve-Path -LiteralPath $SetupEnginePublishDir).Path.TrimEnd('\')
    foreach ($resource in $resources) {
        $relative = $resource.FullName.Substring($publishRoot.Length).TrimStart('\')
        $destination = Join-Path (Join-Path $extractDir 'SetupEngine') $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $resource.FullName -Destination $destination -Force
    }

    & $MakeAppxPath pack /d $extractDir /p $repacked /o | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx.exe failed with exit code $LASTEXITCODE"
    }

    Copy-Item -LiteralPath $repacked -Destination $MsixPath -Force
    Write-Host "Injected $($resources.Count) SetupEngine XAML resource file(s) into $MsixPath"
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
