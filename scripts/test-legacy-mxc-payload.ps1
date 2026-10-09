[CmdletBinding()]
param([string]$RepoRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$root = Join-Path $RepoRoot "TestResults\legacy-mxc-fixture-$([guid]::NewGuid().ToString('N'))"
$cleanup = Join-Path $RepoRoot 'scripts\Remove-LegacyMxcPayload.ps1'
$files = @('tools\mxc\x64\wxc-exec.exe', 'tools\mxc\x64\wslcsdk.dll',
    'tools\mxc\arm64\wxc-exec.exe', 'tools\mxc\arm64\wslcsdk.dll')
try {
    foreach ($relative in $files + @('wslcsdk.dll', 'tools\mxc\x64\unknown.txt', 'settings.json')) {
        $path = Join-Path $root $relative
        New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
        Set-Content -LiteralPath $path 'synthetic fixture'
    }
    & $cleanup -PayloadPath $root
    & $cleanup -PayloadPath $root
    foreach ($relative in $files) {
        if (Test-Path -LiteralPath (Join-Path $root $relative)) { throw "Retired file retained: $relative" }
    }
    foreach ($relative in @('wslcsdk.dll', 'tools\mxc\x64\unknown.txt', 'settings.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $relative))) { throw "Unowned file removed: $relative" }
    }
    if (Test-Path -LiteralPath (Join-Path $root 'tools\mxc\arm64')) { throw 'Empty legacy directory retained.' }
    Remove-Item -LiteralPath (Join-Path $root 'tools\mxc\x64\unknown.txt')
    & $cleanup -PayloadPath $root
    if (Test-Path -LiteralPath (Join-Path $root 'tools\mxc')) { throw 'Empty MXC parent retained.' }
    $victim = Join-Path $root 'outside'
    New-Item -ItemType Directory -Path (Join-Path $victim 'x64') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $victim 'x64\wslcsdk.dll') 'must survive'
    $link = Join-Path $root 'tools\mxc'
    New-Item -ItemType Junction -Path $link -Target $victim | Out-Null
    $refused = $false
    try { & $cleanup -PayloadPath $root }
    catch { $refused = $_.Exception.Message.Contains('reparse traversal') }
    if (-not $refused) { throw 'Cleanup followed a directory reparse point.' }
    if (-not (Test-Path -LiteralPath (Join-Path $victim 'x64\wslcsdk.dll'))) { throw 'Junction target changed.' }
    [IO.Directory]::Delete($link)
    Write-Host 'Legacy payload fixtures passed: exact cleanup, idempotence, preservation, empty dirs and reparse refusal.'
}
finally {
    if ($link -and (Test-Path -LiteralPath $link)) { [IO.Directory]::Delete($link) }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
