<#
.SYNOPSIS
    Removes only the four retired npm MXC payload files from incremental output.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadPath)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PayloadPath)
function Assert-NoReparse([string]$Path) {
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Legacy payload cleanup refuses reparse traversal: $current"
            }
        }
    }
}
foreach ($relative in @(
    'tools\mxc\x64\wxc-exec.exe', 'tools\mxc\x64\wslcsdk.dll',
    'tools\mxc\arm64\wxc-exec.exe', 'tools\mxc\arm64\wslcsdk.dll'
)) {
    $path = Join-Path $root $relative
    Assert-NoReparse $path
    if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path }
}
foreach ($relative in @('tools\mxc\x64', 'tools\mxc\arm64', 'tools\mxc')) {
    $path = Join-Path $root $relative
    Assert-NoReparse $path
    if ((Test-Path -LiteralPath $path -PathType Container) -and
        @(Get-ChildItem -LiteralPath $path -Force).Count -eq 0) {
        Remove-Item -LiteralPath $path
    }
}
