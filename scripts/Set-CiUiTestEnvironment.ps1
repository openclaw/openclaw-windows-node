<#
.SYNOPSIS
    Exports the UI lane's isolated product roots to the GitHub environment file.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$EnvironmentFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repository = (Resolve-Path -LiteralPath $RepoRoot -ErrorAction Stop).ProviderPath
if (-not (Test-Path -LiteralPath $repository -PathType Container)) {
    throw "The UI test repository root must be a directory."
}

$roots = [ordered]@{
    OPENCLAW_REPO_ROOT = $repository
    OPENCLAW_TRAY_DATA_DIR = Join-Path $repository "TestData\OpenClawTrayUITests"
    OPENCLAW_TRAY_APPDATA_DIR = Join-Path $repository "TestData\OpenClawTrayUITests-Roaming"
    OPENCLAW_TRAY_LOCALAPPDATA_DIR = Join-Path $repository "TestData\OpenClawTrayUITests-Local"
}
$lines = foreach ($entry in $roots.GetEnumerator()) {
    "$($entry.Key)=$($entry.Value)"
}
[IO.File]::AppendAllText(
    $EnvironmentFile,
    ([string]::Join("`n", [string[]]$lines) + "`n"),
    [Text.UTF8Encoding]::new($false))
