<#
.SYNOPSIS
    Verifies native release payload dependencies needed on clean Windows hosts.

.DESCRIPTION
    The Windows node uses NSec.Cryptography, which loads libsodium.dll. The
    NuGet-provided Windows libsodium binary imports the Visual C++ runtime, so
    app-local/installer payloads must make that runtime available before the
    tray can generate or load device keys.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadPath,

    [switch]$RequireAppLocalVCRuntime,

    [switch]$RequireInstallerVCRedist,

    [string]$InstallerVCRedistPath,

    [switch]$SkipNativeLoadProbe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$payloadRoot = (Resolve-Path -LiteralPath $PayloadPath).Path
$errors = New-Object System.Collections.Generic.List[string]
$runningOnWindows = $env:OS -eq "Windows_NT"
$shouldProbeNativeLoad = $runningOnWindows -and -not $SkipNativeLoadProbe

if ($shouldProbeNativeLoad -and
    -not ([System.Management.Automation.PSTypeName]"OpenClawNativeDependencyProbe").Type) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class OpenClawNativeDependencyProbe {
    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool SetDllDirectory(string lpPathName);
}
"@
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    [System.IO.Path]::GetRelativePath($Root, $Path).Replace('/', '\')
}

function Add-MicrosoftSignatureErrors {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File
    )

    if (-not $runningOnWindows) {
        return
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $File.FullName
    if ($signature.Status -ne "Valid") {
        $errors.Add("Microsoft runtime file $(Get-RelativePath -Root $payloadRoot -Path $File.FullName) has Authenticode status $($signature.Status).")
        return
    }

    $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { "" }
    if ($subject -notmatch "O=Microsoft Corporation") {
        $errors.Add("Microsoft runtime file $(Get-RelativePath -Root $payloadRoot -Path $File.FullName) was not signed by Microsoft Corporation: $subject")
    }
}

function Get-VCRuntimeFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    @(
        Get-ChildItem -LiteralPath $Directory -File -Filter "vcruntime140*.dll"
        Get-ChildItem -LiteralPath $Directory -File -Filter "msvcp140*.dll"
        Get-ChildItem -LiteralPath $Directory -File -Filter "concrt140.dll"
    ) | Sort-Object FullName -Unique
}

# onnxruntime >= 1.20 is built with a VS 2022 toolchain that requires
# VC++ runtime 14.38+. Shipping older DLLs app-locally shadows the system
# runtime and causes 0x8007045A DllNotFoundException at startup.
# Floor: 14.38.33130.0 (VS 17.8, the first 14.38 release).
$script:VCRuntimeMinVersion = [version]"14.38.33130.0"

function Add-VCRuntimeVersionFloorErrors {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File
    )

    if (-not $runningOnWindows) {
        return
    }

    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($File.FullName)
    if (-not $vi -or -not $vi.FileVersion) {
        $errors.Add("Cannot read file version from $(Get-RelativePath -Root $payloadRoot -Path $File.FullName).")
        return
    }

    $fileVer = [version]::new($vi.FileMajorPart, $vi.FileMinorPart, $vi.FileBuildPart, $vi.FilePrivatePart)
    if ($fileVer -lt $script:VCRuntimeMinVersion) {
        $errors.Add("VC++ runtime $(Get-RelativePath -Root $payloadRoot -Path $File.FullName) is version $fileVer, which is older than the minimum $($script:VCRuntimeMinVersion) required by onnxruntime. Update the VC++ Redistributable or Visual Studio install.")
    }
}

$libsodiumFiles = @(
    Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter libsodium.dll |
        Sort-Object FullName
)

if ($libsodiumFiles.Count -eq 0) {
    $errors.Add("Missing libsodium.dll under $payloadRoot.")
}

foreach ($libsodium in $libsodiumFiles) {
    $runtimePath = Join-Path $libsodium.DirectoryName "vcruntime140.dll"
    if ($RequireAppLocalVCRuntime -and -not (Test-Path -LiteralPath $runtimePath)) {
        $errors.Add("Missing app-local vcruntime140.dll next to $(Get-RelativePath -Root $payloadRoot -Path $libsodium.FullName).")
    }

    if ($RequireAppLocalVCRuntime) {
        foreach ($runtimeFile in Get-VCRuntimeFiles -Directory $libsodium.DirectoryName) {
            Add-MicrosoftSignatureErrors -File $runtimeFile
            Add-VCRuntimeVersionFloorErrors -File $runtimeFile
        }
    }

    if ($shouldProbeNativeLoad) {
        [OpenClawNativeDependencyProbe]::SetDllDirectory($libsodium.DirectoryName) | Out-Null
        Push-Location $libsodium.DirectoryName
        try {
            $handle = [OpenClawNativeDependencyProbe]::LoadLibrary("libsodium.dll")
            $lastError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        } finally {
            Pop-Location
        }

        if ($handle -eq [IntPtr]::Zero) {
            $errors.Add("libsodium.dll failed to load from $(Get-RelativePath -Root $payloadRoot -Path $libsodium.FullName) (Win32 error $lastError).")
        }
    }
}

if ($RequireInstallerVCRedist) {
    $redist = if ([string]::IsNullOrWhiteSpace($InstallerVCRedistPath)) {
        Join-Path $payloadRoot "vc_redist.x64.exe"
    } else {
        $InstallerVCRedistPath
    }

    if (-not (Test-Path -LiteralPath $redist)) {
        $errors.Add("Missing bundled Visual C++ Runtime redistributable at $redist.")
    } elseif ($runningOnWindows) {
        Add-MicrosoftSignatureErrors -File (Get-Item -LiteralPath $redist)
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Release native dependency policy passed." -ForegroundColor Green
