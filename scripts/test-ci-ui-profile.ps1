<#
.SYNOPSIS
    Exercises the UI lane's environment export without starting the app or using a real profile.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$exporter = Join-Path $RepoRoot "scripts\Set-CiUiTestEnvironment.ps1"
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-ui-profile-" + [guid]::NewGuid().ToString("N"))
$workspace = Join-Path $scratch "checkout with spaces"
$environmentFile = Join-Path $scratch "github-env"
$null = New-Item -ItemType Directory -Path $workspace

function Read-EnvironmentExport([string]$Path) {
    $bindings = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $pair = $line.Split("=", 2)
        if ($pair.Count -ne 2 -or $bindings.ContainsKey($pair[0])) {
            throw "The UI environment export contains a malformed or duplicate binding."
        }
        $bindings[$pair[0]] = $pair[1]
    }
    return $bindings
}

try {
    [IO.File]::WriteAllText($environmentFile, "UNRELATED_VARIABLE=preserved`n")
    & $exporter -RepoRoot $workspace -EnvironmentFile $environmentFile

    $actual = Read-EnvironmentExport $environmentFile
    $expected = [ordered]@{
        OPENCLAW_REPO_ROOT = $workspace
        OPENCLAW_TRAY_DATA_DIR = "$workspace\TestData\OpenClawTrayUITests"
        OPENCLAW_TRAY_APPDATA_DIR = "$workspace\TestData\OpenClawTrayUITests-Roaming"
        OPENCLAW_TRAY_LOCALAPPDATA_DIR = "$workspace\TestData\OpenClawTrayUITests-Local"
    }
    foreach ($entry in $expected.GetEnumerator()) {
        if (-not $actual.ContainsKey($entry.Key) -or $actual[$entry.Key] -cne $entry.Value) {
            throw "UI isolation contract failed for $($entry.Key): missing or misdirected root."
        }
        Write-Output "PASS $($entry.Key)"
    }

    if ($actual.Count -ne 5 -or $actual["UNRELATED_VARIABLE"] -cne "preserved") {
        throw "The UI environment export must preserve unrelated bindings without adding other overrides."
    }
    Write-Output "PASS unrelated environment preserved"

    $defaultEnvironmentFile = Join-Path $scratch "default-github-env"
    Push-Location $workspace
    try {
        & $exporter -EnvironmentFile $defaultEnvironmentFile
    } finally {
        Pop-Location
    }
    $default = Read-EnvironmentExport $defaultEnvironmentFile
    $expectedRepository = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath
    if ($default["OPENCLAW_REPO_ROOT"] -cne $expectedRepository) {
        throw "The default UI repository root must follow the exporter checkout, not the caller's directory."
    }
    Write-Output "PASS default repository follows checkout"
    Write-Output "UI profile isolation: 6 checks passed."
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
