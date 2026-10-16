[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$releaseScript = Join-Path $PSScriptRoot 'Get-ContractLvtRelease.ps1'
$expected = @{
    X64 = '40080F40637E393116B19C00525130E62BF6C8191DC9D4C33207E67882AD75E9'
    Arm64 = '0ED7C2E1925C140EEF7A2C7A4B3BC220476F1468AD1E17F6DA98040D74CF68DA'
}
foreach ($architecture in $expected.Keys) {
    $release = & $releaseScript -Architecture $architecture
    if ($release.Version -cne '0.5.0' -or $release.Architecture -cne $architecture -or
        $release.ExecutableSha256 -cne $expected[$architecture]) {
        throw "Incorrect pinned driver for $architecture."
    }
}
$native = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ((& $releaseScript).ExecutableSha256 -cne $expected[$native]) {
    throw 'Default driver does not match the test host architecture.'
}
foreach ($architecture in @('X86', 'Arm', 'unknown', '')) {
    $rejected = $false
    try { & $releaseScript -Architecture $architecture | Out-Null }
    catch [InvalidOperationException] { $rejected = $true }
    catch [Management.Automation.RuntimeException] { $rejected = $true }
    if (-not $rejected) { throw "Unsupported architecture '$architecture' selected a driver." }
}
Write-Host 'Pinned lvt release checks passed: x64, ARM64, native default and four unsupported architectures.'
