<#
.SYNOPSIS
    Verifies native release payload dependencies needed on clean Windows hosts.

.DESCRIPTION
    The Windows node uses NSec.Cryptography, which loads libsodium.dll, and
    SherpaOnnx + OnnxRuntime for Piper TTS. These native binaries import the
    Visual C++ runtime, so app-local/installer payloads must include a runtime
    DLL version that satisfies every bundled native library's requirements.

    VC++ runtime version compatibility:
    - VCRuntime.CefSharp.140 1.0.5 ships 14.29 (VS 2019 16.11).
    - OnnxRuntime 1.26 and sherpa-onnx 1.13 are compiled with VS 2022 and
      require a VC++ runtime >= 14.40. Bundling 14.29 causes sherpa-onnx-c-api
      to fail DLL initialisation (0x8007045A), crash-looping the tray on
      startup. See issue #703.
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

# Minimum VC++ runtime file version required by the bundled native stack.
# OnnxRuntime 1.26 and sherpa-onnx 1.13 are compiled with VS 2022 (MSVC 14.40+).
# VCRuntime.CefSharp.140 1.0.5 ships 14.29 (VS 2019) which is too old; see issue #703.
$Script:MinVCRuntimeVersion = [Version]"14.40.0.0"

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

            # Enforce minimum VC++ runtime version required by OnnxRuntime/sherpa-onnx.
            # FileVersionInfo reads Windows PE metadata; skip when not on Windows.
            if ($runningOnWindows -and $runtimeFile.Name -eq "vcruntime140.dll") {
                $fvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($runtimeFile.FullName)
                $actual = [Version]"$($fvi.FileMajorPart).$($fvi.FileMinorPart).$($fvi.FileBuildPart).$($fvi.FilePrivatePart)"
                if ($actual -lt $Script:MinVCRuntimeVersion) {
                    $errors.Add(
                        "$(Get-RelativePath -Root $payloadRoot -Path $runtimeFile.FullName) " +
                        "file version $actual is below minimum $Script:MinVCRuntimeVersion " +
                        "required by OnnxRuntime/sherpa-onnx (issue #703). " +
                        "Update VCRuntime.CefSharp.140 to a package version that ships $Script:MinVCRuntimeVersion+ DLLs."
                    )
                }
            }
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
