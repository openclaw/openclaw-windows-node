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
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $MsixPath -PathType Leaf)) {
    throw "MSIX not found: $MsixPath"
}

Remove-Item -LiteralPath $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Expand-Archive -LiteralPath $MsixPath -DestinationPath $OutputDirectory -Force

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
