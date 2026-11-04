<#
.SYNOPSIS
Records and summarizes Defender, file and CPU activity during bounded native Gateway startup.
.DESCRIPTION
Repeats the native Gateway startup trace used to attribute startup time to Microsoft Defender
real-time scanning. Run it from an unelevated PowerShell 7 prompt. It:
  1. creates a private run directory with inheritance disabled
  2. verifies the Node archive hash and extracts node.exe into the run directory
  3. asks for UAC approval to start an elevated recorder that owns one uniquely named WPR
     instance (Defender Scans.Light plus FileIO, DiskIO and CPU sampling)
  4. runs gateway-startup-trace\GatewayStartupComparison.ps1 unelevated: one host arm and
     one temporary MXC-isolated arm, each stopping before any listener starts
  5. stops the recording, writes a sanitized summary and removes scratch node.exe and the
     synthetic host profile

It changes no Defender, power or system settings and adds no exclusions. The ETL and
result files stay in the run directory and can contain private machine details; share only
the sanitized summary.

The startup guard is not a security sandbox. It patches selected Node APIs and redirects
profile variables; payload code can still use the filesystem or native modules. Only trace
trusted payloads.

The elevated recorder cancels its own instance on stop timeout or error, but it is not
crash-safe. If its window is closed or killed, recover with the printed
`wpr.exe -cancel -instancename <name>` command from an elevated prompt.
.EXAMPLE
.\scripts\Invoke-GatewayStartupTrace.ps1 -PayloadRoot C:\payloads\openclaw-2026.10.9-arm64 `
    -NodeArchive C:\payloads\node-v24.22.1-win-arm64.zip -NodeArchiveSha256 a0c8...9171
#>
param(
    [Parameter(Mandatory)][string]$PayloadRoot,
    [Parameter(Mandatory)][string]$NodeArchive,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$NodeArchiveSha256,
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'OpenClawStartupTraces'),
    [ValidateSet('isolated-then-host', 'host-then-isolated')][string]$Order = 'isolated-then-host',
    [string]$MxcSdkVersion = '1.0.1',
    [ValidateRange(10, 1600)][int]$BenchmarkTimeoutSeconds = 240,
    [ValidateRange(60, 3600)][int]$MaxRecordingSeconds = 900,
    [ValidateRange(10, 600)][int]$RecorderStartTimeoutSeconds = 120,
    [ValidateRange(10, 900)][int]$RecorderStopTimeoutSeconds = 300,
    [switch]$SkipRecording
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolDirectory = Join-Path $PSScriptRoot 'gateway-startup-trace'

Import-Module (Join-Path $toolDirectory 'GatewayStartupTrace.psm1') -Force

function Expand-NodeExecutable {
    param([string]$Archive, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName -match '^[^/]+/node\.exe$' })
        if ($entries.Count -ne 1) { throw 'The Node archive must contain exactly one <folder>/node.exe entry.' }
        $target = Join-Path $Destination 'node.exe'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $target)
        return $target
    } finally {
        $zip.Dispose()
    }
}

if ($PSVersionTable.PSEdition -ne 'Core') {
    throw 'Run this script with PowerShell 7 (pwsh). The MXC SDK cannot load in Windows PowerShell.'
}
if (Test-IsElevated) {
    throw 'Run this script unelevated. Only the recorder is elevated, through a UAC prompt, so the measured startup runs as the normal user.'
}
if ($MaxRecordingSeconds -le (2 * $BenchmarkTimeoutSeconds + 300)) {
    throw 'MaxRecordingSeconds must exceed two benchmark timeouts plus 300 seconds for setup and cleanup.'
}
$PayloadRoot = [IO.Path]::GetFullPath($PayloadRoot)
foreach ($required in @('payload-metadata.json', 'app\openclaw.mjs')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadRoot $required) -PathType Leaf)) {
        throw "The payload root is missing $required."
    }
}
$actualHash = (Get-FileHash -LiteralPath $NodeArchive -Algorithm SHA256).Hash
if ($actualHash -ne $NodeArchiveSha256.ToUpperInvariant()) {
    throw 'The Node archive SHA256 does not match -NodeArchiveSha256.'
}

$runId = [guid]::NewGuid().ToString('N')
if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}
$runDirectory = New-PrivateDirectory (Join-Path $OutputDirectory ("{0:yyyyMMdd-HHmmss}-{1}" -f [DateTime]::UtcNow, $runId.Substring(0, 8)))
$controlDirectory = Join-Path $runDirectory 'control'
New-Item -ItemType Directory -Path $controlDirectory | Out-Null
$resultPath = Join-Path $runDirectory 'comparison-result.json'
$tracePath = Join-Path $runDirectory 'startup-trace.etl'
$summaryPath = Join-Path $runDirectory 'startup-summary.json'
$instanceName = "OpenClawStartup-$runId"
$recorderPaths = [pscustomobject]@{
    Ready = Join-Path $controlDirectory 'recorder-ready.json'
    Stop = Join-Path $controlDirectory 'recorder-stop'
    Status = Join-Path $controlDirectory 'recorder-status.json'
}
$recorderLaunched = $false
$nodePath = $null
$comparisonExit = $null
$recorderResult = $null

Write-Host "Run directory: $runDirectory"
try {
    $nodePath = Expand-NodeExecutable -Archive $NodeArchive -Destination $runDirectory

    if (-not $SkipRecording) {
        $recorderLaunched = $true
        $recorderPaths = Start-GatewayTraceRecorder -ToolDirectory $toolDirectory -InstanceName $instanceName `
            -TracePath $tracePath -ControlDirectory $controlDirectory -MaxRecordingSeconds $MaxRecordingSeconds `
            -StartTimeoutSeconds $RecorderStartTimeoutSeconds
    }

    if (Test-IsElevated) { throw 'The comparison controller unexpectedly became elevated.' }
    $pwsh = (Get-Process -Id $PID).Path
    & $pwsh -NoProfile -File (Join-Path $toolDirectory 'GatewayStartupComparison.ps1') -PayloadRoot $PayloadRoot `
        -NodePath $nodePath -RunDirectory $runDirectory -ResultPath $resultPath -Order $Order `
        -MxcSdkVersion $MxcSdkVersion -BenchmarkTimeoutSeconds $BenchmarkTimeoutSeconds | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The comparison exited with code $LASTEXITCODE; see $resultPath." }
    $comparisonExit = 'completed'
} catch {
    $comparisonExit = 'failed: ' + $_.Exception.Message
    Write-Warning $comparisonExit
} finally {
    if ($recorderLaunched) {
        $recorderResult = Stop-GatewayTraceRecorder -InstanceName $instanceName -Paths $recorderPaths -StopTimeoutSeconds $RecorderStopTimeoutSeconds
    }
    if ($nodePath -and (Test-Path -LiteralPath $nodePath)) { Remove-Item -LiteralPath $nodePath -Force }
    $hostProfile = Join-Path $runDirectory 'host-profile'
    if (Test-Path -LiteralPath $hostProfile) { Remove-Item -LiteralPath $hostProfile -Recurse -Force }
}

Write-Host "Comparison: $comparisonExit"
if (-not $SkipRecording -and
    ($null -eq $recorderResult -or $recorderResult.outcome -ne 'saved' -or -not (Test-Path -LiteralPath $tracePath))) {
    $comparisonExit = "failed: WPR recording was not saved ($($recorderResult.outcome))."
    Write-Warning $comparisonExit
}
if ((Test-Path -LiteralPath $resultPath) -and
    ($SkipRecording -or ($recorderResult.outcome -eq 'saved' -and (Test-Path -LiteralPath $tracePath)))) {
    & (Join-Path $toolDirectory 'Get-GatewayStartupTraceSummary.ps1') -TracePath $tracePath -ResultPath $resultPath `
        -PayloadRoot $PayloadRoot -OutputPath $summaryPath | Out-Null
    Write-Host "Sanitized summary: $summaryPath"
} elseif (Test-Path -LiteralPath $resultPath) {
    Write-Host "Comparison result (no trace): $resultPath"
}
if ($comparisonExit -ne 'completed') { exit 1 }
