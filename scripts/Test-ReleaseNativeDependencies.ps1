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

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        $errors.Add("Cannot run isolated TTS native stack probe because dotnet was not found.")
        return
    }

    $probeProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@

    $probeProgram = @'
using System.Reflection;

if (args.Length != 1)
{
    Console.Error.WriteLine("Expected payload root argument.");
    return 2;
}

var payloadRoot = Path.GetFullPath(args[0]);
Directory.SetCurrentDirectory(payloadRoot);

AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    var assemblyName = new AssemblyName(eventArgs.Name).Name;
    if (string.IsNullOrWhiteSpace(assemblyName))
    {
        return null;
    }

    var candidate = Path.Combine(payloadRoot, assemblyName + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

var sherpaAsm = Assembly.LoadFrom(Path.Combine(payloadRoot, "sherpa-onnx.dll"));
var versionType = sherpaAsm.GetType("SherpaOnnx.VersionInfo", true)!;
var version = versionType.GetProperty("Version")!.GetValue(null)?.ToString();
if (string.IsNullOrWhiteSpace(version))
{
    throw new InvalidOperationException("SherpaOnnx.VersionInfo.Version returned an empty version.");
}

var onnxAsm = Assembly.LoadFrom(Path.Combine(payloadRoot, "Microsoft.ML.OnnxRuntime.dll"));
var ortType = onnxAsm.GetType("Microsoft.ML.OnnxRuntime.OrtEnv", true)!;
var instanceMethod = ortType.GetMethod(
    "Instance",
    BindingFlags.Public | BindingFlags.Static,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
var env = instanceMethod is not null
    ? instanceMethod.Invoke(null, null)
    : ortType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

if (env is null)
{
    throw new InvalidOperationException("Microsoft.ML.OnnxRuntime.OrtEnv did not initialize.");
}

Console.WriteLine($"TTS native stack probe passed (Sherpa {version}, ONNX Runtime initialized).");
return 0;
'@

    $probeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("openclaw-tts-native-probe-{0}" -f [Guid]::NewGuid().ToString("N"))
    try {
        New-Item -ItemType Directory -Path $probeDir | Out-Null
        $projectPath = Join-Path $probeDir "OpenClaw.TtsNativeProbe.csproj"
        $programPath = Join-Path $probeDir "Program.cs"
        Set-Content -LiteralPath $projectPath -Value $probeProject -Encoding UTF8
        Set-Content -LiteralPath $programPath -Value $probeProgram -Encoding UTF8

        $buildOutput = & $dotnet.Source build $projectPath -c Release -v:q 2>&1
        $buildExitCode = $LASTEXITCODE
        if ($buildExitCode -ne 0) {
            $tail = @($buildOutput | Select-Object -Last 12) -join " "
            $errors.Add("TTS native stack probe build failed with exit code $buildExitCode. $tail")
            return
        }

        $probeDll = Join-Path $probeDir "bin\Release\net10.0\OpenClaw.TtsNativeProbe.dll"
        $output = & $dotnet.Source $probeDll $payloadRoot 2>&1
        $exitCode = $LASTEXITCODE
    } finally {
        try { if (Test-Path -LiteralPath $probeDir) { Remove-Item -LiteralPath $probeDir -Recurse -Force } } catch { }
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
