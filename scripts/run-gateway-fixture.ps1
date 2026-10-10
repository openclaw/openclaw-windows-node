<#
.SYNOPSIS
Launch the real app against a synthetic, read-only Gateway. Ctrl+C cleans up this run.
.DESCRIPTION
Build the app first. AppPath is required so this command never selects an installed
app or a stale build implicitly. The app must advertise fixture isolation support.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AppPath,
    [string]$ArtifactsDirectory,
    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\tests\OpenClaw.GatewayFixtureHost\OpenClaw.GatewayFixtureHost.csproj'
$resolvedApp = (Resolve-Path -LiteralPath $AppPath).Path
$hostArgs = @('--app', $resolvedApp)
if ($ArtifactsDirectory) {
    $hostArgs += @('--artifacts', [IO.Path]::GetFullPath($ArtifactsDirectory))
}
if ($DurationSeconds -gt 0) {
    $hostArgs += @('--duration-seconds', $DurationSeconds.ToString())
}
& dotnet run --project $project --configuration $Configuration -- @hostArgs
exit $LASTEXITCODE
