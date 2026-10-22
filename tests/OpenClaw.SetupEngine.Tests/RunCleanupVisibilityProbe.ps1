param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ArgumentsBase64,
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Token,
    [Parameter(Mandatory)][string]$ReadyName,
    [Parameter(Mandatory)][string]$ReleaseName,
    [Parameter(Mandatory)][string]$ResultPath,
    [switch]$PositiveControl,
    [string]$ExternalPidFile,
    [string]$ExpectedExternalHostPath,
    [switch]$NoWindowStarter
)
$ErrorActionPreference = 'Stop'
# Task Scheduler starts the cleanup WinExe outside the starter's process tree.
# Supply its exact signed deployment path to cover that parent and its startup.
Add-Type -Path @((Join-Path $PSScriptRoot 'CleanupVisibilityRunState.cs'),
    (Join-Path $PSScriptRoot 'CleanupVisibilityProbe.cs')) -ReferencedAssemblies System.Core
$probe = [CleanupVisibilityProbe]::new($Token)
try {
    $arguments = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($ArgumentsBase64))
    $result = $probe.Run($Executable, $arguments, $Directory, $ReadyName, $ReleaseName, $PositiveControl.IsPresent, 40,
        $ExternalPidFile, $NoWindowStarter.IsPresent, $ExpectedExternalHostPath)
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
} catch {
    $probe.Snapshot() | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    throw
} finally { $probe.Dispose() }
