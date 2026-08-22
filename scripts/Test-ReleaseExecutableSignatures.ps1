<#
.SYNOPSIS
    Verifies release payload binary signing policy.

.DESCRIPTION
    Classifies every .exe and .dll in a release payload. OpenClaw-owned binaries
    must be signed when -RequireSignedOpenClaw is passed. Third-party binaries,
    including wxc-exec.exe, must not be signed by the OpenClaw release signer.
    Unknown executables and unknown OpenClaw-named binaries fail closed.

.PARAMETER PayloadPath
    Root directory of the release payload to inspect.

.PARAMETER RequireSignedOpenClaw
    Require OpenClaw-owned binaries to have valid Authenticode signatures.

.PARAMETER OpenClawSignerSubject
    Exact certificate subject required for OpenClaw-owned binaries.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadPath,

    [switch]$RequireSignedOpenClaw,

    [string]$OpenClawSignerSubject = "CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$payloadRoot = (Resolve-Path -LiteralPath $PayloadPath).Path

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    [System.IO.Path]::GetRelativePath($Root, $Path).Replace('/', '\')
}

function Get-BinaryClassification {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    switch -Regex ($RelativePath) {
        '^OpenClaw\.Tray\.WinUI\.exe$' { return "OpenClawOwned" }
        '^OpenClaw\.Tray\.WinUI\.dll$' { return "OpenClawOwned" }
        '^OpenClaw\.Chat\.dll$' { return "OpenClawOwned" }
        '^OpenClaw\.Connection\.dll$' { return "OpenClawOwned" }
        '^OpenClaw\.SetupEngine\.UI\.dll$' { return "OpenClawOwned" }
        '^OpenClaw\.SetupEngine\.dll$' { return "OpenClawOwned" }
        '^OpenClaw\.Shared\.dll$' { return "OpenClawOwned" }
        '^OpenClawTray\.FunctionalUI\.dll$' { return "OpenClawOwned" }
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
            if ($RequireSignedOpenClaw -and $binary.SignatureStatus -ne "Valid") {
                $errors.Add("OpenClaw binary is not validly signed: $($binary.RelativePath) [$($binary.SignatureStatus)]")
            }
            elseif ($RequireSignedOpenClaw -and
                    -not [string]::Equals(
                        $binary.SignerSubject,
                        $OpenClawSignerSubject,
                        [StringComparison]::OrdinalIgnoreCase)) {
                $errors.Add("OpenClaw binary is not signed by the expected OpenClaw signer: $($binary.RelativePath) [$($binary.SignerSubject)]")
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

@(
    "OpenClaw.Tray.WinUI.exe",
    "OpenClaw.Tray.WinUI.dll",
    "OpenClaw.Chat.dll",
    "OpenClaw.Connection.dll",
    "OpenClaw.SetupEngine.UI.dll",
    "OpenClaw.SetupEngine.dll",
    "OpenClaw.Shared.dll",
    "OpenClawTray.FunctionalUI.dll"
) | ForEach-Object {
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
if (-not ($binaries | Where-Object RelativePath -match '^tools\\mxc\\[^\\]+\\wxc-exec\.exe$')) {
    $errors.Add("Missing tools\mxc\<arch>\wxc-exec.exe third-party executable.")
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Release binary signing policy passed." -ForegroundColor Green
