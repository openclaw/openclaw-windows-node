<#
.SYNOPSIS
Adds a content-free operator observation to an active chat trace.
.DESCRIPTION
Mark send when sending in the existing chat UI, first-response when the first response
is visible, and completed when the response ends. Console switching and human reaction
time are included. No text, session key, model name or credentials are recorded.
#>
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][ValidateSet('send', 'first-response', 'completed', 'failed', 'cancelled')][string]$Event
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ticks = [DateTime]::UtcNow.ToFileTimeUtc()
$control = Join-Path ([IO.Path]::GetFullPath($RunDirectory)) 'control'
$readyPath = Join-Path $control 'chat-ready'
if (-not (Test-Path -LiteralPath $readyPath -PathType Leaf) -or
    (Get-Content -LiteralPath $readyPath -Raw) -notmatch '^OpenClawStartup-[0-9a-f]{32}$' -or
    (Test-Path -LiteralPath (Join-Path $control 'recorder-stop'))) {
    throw 'The target run is not an active owned chat recording.'
}
Import-Module (Join-Path $PSScriptRoot 'gateway-startup-trace\GatewayResourceTrace.psm1') -Force
$markers = @(Get-ChatTraceMarkers -ControlDirectory $control -StartTicks 0 -EndTicks $ticks)
$marker = [pscustomobject]@{ name = $Event; ticks = $ticks }
Get-ChatTimingSummary -Markers @($markers + $marker) | Out-Null
$path = Join-Path $control "chat-marker-$Event.json"
$temporary = "$path.$([guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText($temporary, ($marker | ConvertTo-Json -Compress))
    [IO.File]::Move($temporary, $path, $false)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Write-Host "Recorded operator observation: $Event"
