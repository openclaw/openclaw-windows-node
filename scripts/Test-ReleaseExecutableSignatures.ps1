<#
.SYNOPSIS
    Verifies release payload and installer binary signing policy.

.DESCRIPTION
    Classifies every .exe and .dll in a release payload. OpenClaw-owned binaries
    must be signed when -RequireSignedOpenClaw is passed. Third-party binaries,
    including wxc-exec.exe, must not be signed by the OpenClaw release signer.
    Unknown executables and unknown OpenClaw-named binaries fail closed.
    Installer mode requires both architecture-specific installers to be signed.
    Required OpenClaw signatures must be valid and have an Authenticode timestamp.

.PARAMETER PayloadPath
    Root directory of the release payload to inspect.

.PARAMETER RequireSignedOpenClaw
    Require OpenClaw-owned payload binaries to have valid, timestamped signatures.

.PARAMETER InstallerPath
    Directory containing the final x64 and ARM64 installers after signing.

.PARAMETER OpenClawSignerSubject
    Exact certificate subject required for OpenClaw-owned binaries.
#>
[CmdletBinding(DefaultParameterSetName = "Payload")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Payload")]
    [string]$PayloadPath,

    [Parameter(Mandatory = $true, ParameterSetName = "Installers")]
    [string]$InstallerPath,

    [Parameter(ParameterSetName = "Payload")]
    [switch]$RequireSignedOpenClaw,

    [string]$OpenClawSignerSubject = "CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$isInstaller = $PSCmdlet.ParameterSetName -eq "Installers"
$artifactPath = if ($isInstaller) { $InstallerPath } else { $PayloadPath }
$payloadRoot = (Resolve-Path -LiteralPath $artifactPath).Path
$requireSigned = $isInstaller -or $RequireSignedOpenClaw
$requiredBinaries = if ($isInstaller) {
    @("OpenClawCompanion-Setup-x64.exe", "OpenClawCompanion-Setup-arm64.exe")
} else {
    @(
        "OpenClaw.Tray.WinUI.exe",
        "OpenClaw.Tray.WinUI.dll",
        "OpenClaw.Chat.dll",
        "OpenClaw.Connection.dll",
        "OpenClaw.SetupEngine.UI.dll",
        "OpenClaw.SetupEngine.dll",
        "OpenClaw.Shared.dll",
        "OpenClawTray.FunctionalUI.dll"
    )
}

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    [System.IO.Path]::GetRelativePath($Root, $Path).Replace('/', '\')
}

function Get-BinaryClassification {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    if ($requiredBinaries -contains $RelativePath) { return "OpenClawOwned" }
    if ($isInstaller) { return "UnknownExecutable" }

    switch -Regex ($RelativePath) {
        '(^|\\)createdump\.exe$' { return "ThirdPartyExcluded" }
        '(^|\\)RestartAgent\.exe$' { return "ThirdPartyExcluded" }
        '^tools\\mxc\\[^\\]+\\wxc-exec\.exe$' { return "ThirdPartyExcluded" }
        '(^|\\)OpenClaw[^\\]*\.(exe|dll)$' { return "UnknownOpenClaw" }
        '\.dll$' { return "ThirdPartyExcluded" }
        default { return "UnknownExecutable" }
    }
}

$binaries = @(
    Get-ChildItem -LiteralPath $payloadRoot -Recurse -File |
        Where-Object { $_.Extension -in ".exe", ".dll" } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = Get-RelativePath -Root $payloadRoot -Path $_.FullName
            $signature = Get-AuthenticodeSignature -LiteralPath $_.FullName
            $signerSubject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { "" }
            [pscustomobject]@{
                RelativePath = $relativePath
                Classification = Get-BinaryClassification -RelativePath $relativePath
                SignatureStatus = $signature.Status.ToString()
                SignerSubject = $signerSubject
                HasTimestamp = $null -ne $signature.TimeStamperCertificate
            }
        }
)

if ($binaries.Count -eq 0) {
    throw "No executable or DLL binaries found under $payloadRoot."
}

$binaries | Format-Table -AutoSize

$errors = New-Object System.Collections.Generic.List[string]

foreach ($binary in $binaries) {
    switch ($binary.Classification) {
        "OpenClawOwned" {
            if ($requireSigned -and $binary.SignatureStatus -ne "Valid") {
                $errors.Add("OpenClaw binary is not validly signed: $($binary.RelativePath) [$($binary.SignatureStatus)]")
            }
            elseif ($requireSigned -and
                    -not [string]::Equals(
                        $binary.SignerSubject,
                        $OpenClawSignerSubject,
                        [StringComparison]::OrdinalIgnoreCase)) {
                $errors.Add("OpenClaw binary is not signed by the expected OpenClaw signer: $($binary.RelativePath) [$($binary.SignerSubject)]")
            }
            elseif ($requireSigned -and -not $binary.HasTimestamp) {
                $errors.Add("OpenClaw binary has no Authenticode timestamp: $($binary.RelativePath)")
            }
        }
        "ThirdPartyExcluded" {
            if ($binary.SignatureStatus -eq "Valid" -and
                [string]::Equals(
                    $binary.SignerSubject,
                    $OpenClawSignerSubject,
                    [StringComparison]::OrdinalIgnoreCase)) {
                $errors.Add("Third-party binary appears to be signed by OpenClaw release signer: $($binary.RelativePath) [$($binary.SignerSubject)]")
            }
        }
        "UnknownOpenClaw" {
            $errors.Add("Unknown OpenClaw binary in release payload: $($binary.RelativePath)")
        }
        default {
            $errors.Add("Unknown executable in release payload: $($binary.RelativePath)")
        }
    }
}

$requiredBinaries | ForEach-Object {
    $requiredBinary = $_
    if (-not ($binaries | Where-Object RelativePath -eq $requiredBinary)) {
        $errors.Add("Missing OpenClaw binary: $_.")
    }
}
if ($binaries | Where-Object RelativePath -eq "SetupEngine\OpenClaw.SetupEngine.UI.exe") {
    $errors.Add("SetupEngine\OpenClaw.SetupEngine.UI.exe should not be present in the release payload.")
}
if ($binaries | Where-Object RelativePath -eq "SetupEngine\OpenClaw.SetupEngine.exe") {
    $errors.Add("SetupEngine\OpenClaw.SetupEngine.exe should not be present in the release payload.")
}
if (-not $isInstaller -and -not ($binaries | Where-Object RelativePath -match '^tools\\mxc\\[^\\]+\\wxc-exec\.exe$')) {
    $errors.Add("Missing tools\mxc\<arch>\wxc-exec.exe third-party executable.")
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Release binary signing policy passed." -ForegroundColor Green
