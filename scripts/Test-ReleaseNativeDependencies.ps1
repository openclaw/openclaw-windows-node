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

    [DllImport("kernel32", SetLastError=true)]
    public static extern bool FreeLibrary(IntPtr hModule);
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

function Get-NativeLoadProbeFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    @(
        Get-ChildItem -LiteralPath $Directory -File -Filter "libsodium.dll"
        Get-ChildItem -LiteralPath $Directory -File -Filter "onnxruntime.dll"
        Get-ChildItem -LiteralPath $Directory -File -Filter "sherpa-onnx.dll"
        Get-ChildItem -LiteralPath $Directory -File -Filter "sherpa-onnx-c-api.dll"
    ) | Sort-Object FullName -Unique
}

function Add-NativeLoadProbeErrors {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File
    )

    if (-not $shouldProbeNativeLoad) {
        return
    }

    [OpenClawNativeDependencyProbe]::SetDllDirectory($File.DirectoryName) | Out-Null
    Push-Location $File.DirectoryName
    try {
        $handle = [OpenClawNativeDependencyProbe]::LoadLibrary($File.Name)
        $lastError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    } finally {
        Pop-Location
        [OpenClawNativeDependencyProbe]::SetDllDirectory($null) | Out-Null
    }

    if ($handle -eq [IntPtr]::Zero) {
        $errors.Add("Native dependency $(Get-RelativePath -Root $payloadRoot -Path $File.FullName) failed to load with app-local dependencies (Win32 error $lastError).")
        return
    }

    [OpenClawNativeDependencyProbe]::FreeLibrary($handle) | Out-Null
}

function Add-TtsNativeStackProbeErrors {
    if (-not $shouldProbeNativeLoad) {
        return
    }

    $requiredFiles = @(
        "Microsoft.ML.OnnxRuntime.dll"
        "onnxruntime.dll"
        "sherpa-onnx.dll"
        "sherpa-onnx-c-api.dll"
    )

    $filesByName = @{}
    foreach ($fileName in $requiredFiles) {
        $file = Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter $fileName | Select-Object -First 1
        if ($file) {
            $filesByName[$fileName] = $file
        } else {
            $errors.Add("Missing $fileName for TTS native stack probe.")
        }
    }

    if ($filesByName.Count -ne $requiredFiles.Count) {
        return
    }

    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if (-not $pwsh) {
        $errors.Add("Cannot run isolated TTS native stack probe because pwsh was not found.")
        return
    }

    $probeScript = @'
param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Set-Location $PayloadRoot

$sherpaAsm = [System.Reflection.Assembly]::LoadFrom((Join-Path $PayloadRoot "sherpa-onnx.dll"))
$versionType = $sherpaAsm.GetType("SherpaOnnx.VersionInfo", $true)
$version = $versionType.GetProperty("Version").GetValue($null)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "SherpaOnnx.VersionInfo.Version returned an empty version."
}

$onnxAsm = [System.Reflection.Assembly]::LoadFrom((Join-Path $PayloadRoot "Microsoft.ML.OnnxRuntime.dll"))
$ortType = $onnxAsm.GetType("Microsoft.ML.OnnxRuntime.OrtEnv", $true)
$instanceMethod = $ortType.GetMethod(
    "Instance",
    [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static,
    $null,
    [Type[]]@(),
    $null)
if ($instanceMethod) {
    $env = $instanceMethod.Invoke($null, @())
} else {
    $env = $ortType.GetProperty("Instance").GetValue($null)
}

if ($null -eq $env) {
    throw "Microsoft.ML.OnnxRuntime.OrtEnv did not initialize."
}

Write-Host "TTS native stack probe passed (Sherpa $version, ONNX Runtime initialized)."
'@

    $probePath = Join-Path ([System.IO.Path]::GetTempPath()) ("openclaw-tts-native-probe-{0}.ps1" -f [Guid]::NewGuid().ToString("N"))
    try {
        Set-Content -LiteralPath $probePath -Value $probeScript -Encoding UTF8
        $output = & $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $probePath -PayloadRoot $payloadRoot 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        try { if (Test-Path -LiteralPath $probePath) { Remove-Item -LiteralPath $probePath -Force } } catch { }
    }

    if ($exitCode -ne 0) {
        if ($exitCode -lt 0) {
            $errors.Add("TTS native stack probe crashed with exit code $exitCode. This usually means the app-local Sherpa/ONNX native dependency chain failed to initialize.")
            return
        }

        $tail = @($output | Select-Object -Last 12) -join " "
        $errors.Add("TTS native stack probe failed with exit code $exitCode. $tail")
    }
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
}

if ($shouldProbeNativeLoad) {
    $probeFiles = @(
        Get-ChildItem -LiteralPath $payloadRoot -Recurse -Directory |
            ForEach-Object { Get-NativeLoadProbeFiles -Directory $_.FullName }
        Get-NativeLoadProbeFiles -Directory $payloadRoot
    ) | Sort-Object FullName -Unique

    foreach ($probeFile in $probeFiles) {
        Add-NativeLoadProbeErrors -File $probeFile
    }

    Add-TtsNativeStackProbeErrors
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
