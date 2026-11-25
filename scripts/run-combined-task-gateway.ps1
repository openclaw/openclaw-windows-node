<#
.SYNOPSIS
Start an isolated compiled Gateway and synthetic model fixture for native task-card proof.
.DESCRIPTION
Requires an explicit prepared Gateway source checkout and fresh task output. No downloads,
package registration, live configuration adoption or installed-Gateway fallback occurs.
Keep this process alive during the native test. Send one stdin line/EOF or Ctrl+C to stop it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$GatewaySourceDirectory,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[a-fA-F0-9]{32}$')] [string]$Nonce
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $GatewaySourceDirectory).Path
$run = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($relative in @('package.json', 'scripts\tsx.mjs', 'dist\entry.js', 'dist\build-info.json', 'node_modules')) {
    if (!(Test-Path -LiteralPath (Join-Path $root $relative))) { throw "Prepared Gateway input missing: $relative" }
}
foreach ($relative in @('gateway-state', 'gateway-home', 'gateway-receipt.json')) {
    if (Test-Path -LiteralPath (Join-Path $run $relative)) { throw 'Use a fresh task output; never adopt an existing Gateway profile.' }
}
$fixture = Join-Path $PSScriptRoot 'fixtures\combined-task-gateway.mjs'
$node = (Get-Command node.exe -ErrorAction Stop).Source
$psi = [Diagnostics.ProcessStartInfo]::new($node)
$psi.UseShellExecute = $false
$psi.WorkingDirectory = $root
foreach ($key in @($psi.Environment.Keys)) {
    if ($key -match '^OPENCLAW_(STATE_DIR|HOME|CONFIG_PATH|.*PROFILE.*)$') { $psi.Environment.Remove($key) | Out-Null }
}
foreach ($directory in @($run, (Join-Path $run 'fixture-home'), (Join-Path $run 'fixture-state'), (Join-Path $run 'temp'))) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}
$psi.Environment['OPENCLAW_HOME'] = Join-Path $run 'fixture-home'
$psi.Environment['OPENCLAW_STATE_DIR'] = Join-Path $run 'fixture-state'
$psi.Environment['OPENCLAW_CONFIG_PATH'] = Join-Path $run 'fixture-state\openclaw.json'
$psi.Environment['TEMP'] = Join-Path $run 'temp'
$psi.Environment['TMP'] = Join-Path $run 'temp'
foreach ($name in @('OPENCLAW_SKIP_CHANNELS', 'OPENCLAW_SKIP_CRON', 'OPENCLAW_SKIP_GMAIL_WATCHER',
    'OPENCLAW_SKIP_CANVAS_HOST', 'OPENCLAW_SKIP_BROWSER_CONTROL_SERVER', 'OPENCLAW_DISABLE_BUNDLED_PLUGINS', 'OPENCLAW_TEST')) {
    $psi.Environment[$name] = '1'
}
foreach ($argument in @('--import', ([Uri](Join-Path $root 'scripts\tsx.mjs')).AbsoluteUri, $fixture, $root, $run, $Nonce)) {
    $psi.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($psi)
try { $process.WaitForExit(); $code = $process.ExitCode }
finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
exit $code
