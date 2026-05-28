<#
.SYNOPSIS
  Unpacks an MSIX package so its inner payload can be signed before final package signing.

.DESCRIPTION
  MSIX package signing protects package integrity, but executable payloads can
  still be inspected or launched outside their package context during diagnostics.
  This script prepares an unsigned MSIX for inner Authenticode signing by
  extracting it to a directory and verifying that all script payloads are
  signable PowerShell files.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $MsixPath,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [string] $MakeAppxPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $MsixPath -PathType Leaf)) {
    throw "MSIX not found: $MsixPath"
}

Remove-Item -LiteralPath $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

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

& $MakeAppxPath unpack /p $MsixPath /d $OutputDirectory /o | Write-Host
if ($LASTEXITCODE -ne 0) {
    throw "makeappx.exe unpack failed with exit code $LASTEXITCODE"
}

$signableExtensions = @('.exe', '.dll', '.ps1', '.psm1', '.psd1')
$unsupportedScriptExtensions = @('.cmd', '.bat', '.vbs', '.js')

$payloadFiles = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File
$unsupportedScripts = @($payloadFiles | Where-Object { $_.Extension.ToLowerInvariant() -in $unsupportedScriptExtensions })
if ($unsupportedScripts.Count -gt 0) {
    $list = ($unsupportedScripts | ForEach-Object { $_.FullName.Substring($OutputDirectory.Length).TrimStart('\') }) -join ', '
    throw "MSIX contains script files that cannot be Authenticode-signed by the release workflow: $list"
}

$signableFiles = @($payloadFiles | Where-Object { $_.Extension.ToLowerInvariant() -in $signableExtensions })
if ($signableFiles.Count -eq 0) {
    throw "No signable payload files found in $MsixPath"
}

Write-Host "Prepared $($signableFiles.Count) signable MSIX payload file(s) under $OutputDirectory"
