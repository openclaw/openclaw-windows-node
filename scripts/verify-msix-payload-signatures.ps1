<#
.SYNOPSIS
  Verifies that an MSIX and all signable payload files inside it are signed.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $MsixPath,

    [string] $MakeAppxPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $MsixPath -PathType Leaf)) {
    throw "MSIX not found: $MsixPath"
}

$outerSignature = Get-AuthenticodeSignature -LiteralPath $MsixPath
if ($outerSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Outer MSIX signature is not valid for $MsixPath. Status=$($outerSignature.Status)"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "openclaw-msix-verify-$([System.Guid]::NewGuid().ToString('N'))"
$signableExtensions = @('.exe', '.dll', '.ps1', '.psm1', '.psd1')
$unsupportedScriptExtensions = @('.cmd', '.bat', '.vbs', '.js')

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
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

    & $MakeAppxPath unpack /p $MsixPath /d $tempRoot /o | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "makeappx.exe unpack failed with exit code $LASTEXITCODE"
    }

    $payloadFiles = Get-ChildItem -LiteralPath $tempRoot -Recurse -File
    $unsupportedScripts = @($payloadFiles | Where-Object { $_.Extension.ToLowerInvariant() -in $unsupportedScriptExtensions })
    if ($unsupportedScripts.Count -gt 0) {
        $list = ($unsupportedScripts | ForEach-Object { $_.FullName.Substring($tempRoot.Length).TrimStart('\') }) -join ', '
        throw "MSIX contains script files that cannot be Authenticode-signed by this workflow: $list"
    }

    $signableFiles = @($payloadFiles | Where-Object { $_.Extension.ToLowerInvariant() -in $signableExtensions })
    $invalid = @()
    foreach ($file in $signableFiles) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            $invalid += [pscustomobject]@{
                Path = $file.FullName.Substring($tempRoot.Length).TrimStart('\')
                Status = $signature.Status.ToString()
            }
        }
    }

    if ($invalid.Count -gt 0) {
        $details = ($invalid | ConvertTo-Json -Depth 3)
        throw "Unsigned or invalid signable payload file(s) in $MsixPath`: $details"
    }

    Write-Host "Verified $($signableFiles.Count) signed payload file(s) and valid outer MSIX signature for $MsixPath"
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
