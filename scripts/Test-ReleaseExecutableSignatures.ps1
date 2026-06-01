<#
.SYNOPSIS
    Verifies release payload executable signing policy.

.DESCRIPTION
    Classifies every .exe in a release payload. OpenClaw-owned executables must
    be signed when -RequireSignedOpenClaw is passed. Third-party executables,
    including wxc-exec.exe, must not be signed by the OpenClaw release signer.
    Unknown executables fail closed.

.PARAMETER PayloadPath
    Root directory of the release payload to inspect.

.PARAMETER RequireSignedOpenClaw
    Require OpenClaw-owned executables to have valid Authenticode signatures.

.PARAMETER OpenClawSignerPattern
    Regex used to identify the OpenClaw release signer in signer subjects.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadPath,

    [switch]$RequireSignedOpenClaw,

    [string]$OpenClawSignerPattern = "OpenClaw Foundation"
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

function Get-ExecutableClassification {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    switch -Regex ($RelativePath) {
        '^OpenClaw\.Tray\.WinUI\.exe$' { return "OpenClawOwned" }
        '^SetupEngine\\OpenClaw\.SetupEngine\.exe$' { return "OpenClawOwned" }
        '^SetupEngine\\OpenClaw\.SetupEngine\.UI\.exe$' { return "OpenClawOwned" }
        '(^|\\)createdump\.exe$' { return "ThirdPartyExcluded" }
        '(^|\\)RestartAgent\.exe$' { return "ThirdPartyExcluded" }
        '^tools\\mxc\\[^\\]+\\wxc-exec\.exe$' { return "ThirdPartyExcluded" }
        default { return "Unknown" }
    }
}

$executables = @(
    Get-ChildItem -LiteralPath $payloadRoot -Recurse -File -Filter *.exe |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = Get-RelativePath -Root $payloadRoot -Path $_.FullName
            $signature = Get-AuthenticodeSignature -LiteralPath $_.FullName
            $signerSubject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { "" }
            [pscustomobject]@{
                RelativePath = $relativePath
                Classification = Get-ExecutableClassification -RelativePath $relativePath
                SignatureStatus = $signature.Status.ToString()
                SignerSubject = $signerSubject
            }
        }
)

if ($executables.Count -eq 0) {
    throw "No executables found under $payloadRoot."
}

$executables | Format-Table -AutoSize

$errors = New-Object System.Collections.Generic.List[string]

foreach ($exe in $executables) {
    switch ($exe.Classification) {
        "OpenClawOwned" {
            if ($RequireSignedOpenClaw -and $exe.SignatureStatus -ne "Valid") {
                $errors.Add("OpenClaw executable is not validly signed: $($exe.RelativePath) [$($exe.SignatureStatus)]")
            }
        }
        "ThirdPartyExcluded" {
            if ($exe.SignatureStatus -eq "Valid" -and $exe.SignerSubject -match $OpenClawSignerPattern) {
                $errors.Add("Third-party executable appears to be signed by OpenClaw release signer: $($exe.RelativePath) [$($exe.SignerSubject)]")
            }
        }
        default {
            $errors.Add("Unknown executable in release payload: $($exe.RelativePath)")
        }
    }
}

if (-not ($executables | Where-Object RelativePath -eq "OpenClaw.Tray.WinUI.exe")) {
    $errors.Add("Missing OpenClaw.Tray.WinUI.exe.")
}
if (-not ($executables | Where-Object RelativePath -eq "SetupEngine\OpenClaw.SetupEngine.exe")) {
    $errors.Add("Missing SetupEngine\OpenClaw.SetupEngine.exe.")
}
if (-not ($executables | Where-Object RelativePath -eq "SetupEngine\OpenClaw.SetupEngine.UI.exe")) {
    $errors.Add("Missing SetupEngine\OpenClaw.SetupEngine.UI.exe.")
}
if (-not ($executables | Where-Object RelativePath -match '^tools\\mxc\\[^\\]+\\wxc-exec\.exe$')) {
    $errors.Add("Missing tools\mxc\<arch>\wxc-exec.exe third-party executable.")
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host "Release executable signing policy passed." -ForegroundColor Green
