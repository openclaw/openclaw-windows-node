[CmdletBinding()]
param(
    [string]$Architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
)
$ErrorActionPreference = 'Stop'
$hash = switch ($Architecture) {
    'X64' { '40080F40637E393116B19C00525130E62BF6C8191DC9D4C33207E67882AD75E9' }
    'Arm64' { '0ED7C2E1925C140EEF7A2C7A4B3BC220476F1468AD1E17F6DA98040D74CF68DA' }
    default { throw 'The pinned lvt journey lane requires Windows x64 or ARM64.' }
}
[pscustomobject]@{
    Version = '0.5.0'
    Architecture = $Architecture
    ExecutableSha256 = $hash
}
