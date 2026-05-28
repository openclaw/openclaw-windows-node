<#
.SYNOPSIS
  Re-packs an extracted MSIX payload directory.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PayloadDirectory,

    [Parameter(Mandatory)]
    [string] $MsixPath,

    [string] $MakeAppxPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PayloadDirectory -PathType Container)) {
    throw "Payload directory not found: $PayloadDirectory"
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

$tempPackage = Join-Path ([System.IO.Path]::GetTempPath()) "$([System.IO.Path]::GetFileNameWithoutExtension($MsixPath))-$([System.Guid]::NewGuid().ToString('N')).msix"
try {
    & $MakeAppxPath pack /d $PayloadDirectory /p $tempPackage /o | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx.exe failed with exit code $LASTEXITCODE"
    }

    Copy-Item -LiteralPath $tempPackage -Destination $MsixPath -Force
    Write-Host "Repacked MSIX: $MsixPath"
}
finally {
    Remove-Item -LiteralPath $tempPackage -Force -ErrorAction SilentlyContinue
}
