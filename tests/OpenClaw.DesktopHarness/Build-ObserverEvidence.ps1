[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$output = New-Item -ItemType Directory -Path $OutputDirectory -Force
$assembly = Join-Path $output.FullName 'QuietLaneCaptureObserver.dll'
if (Test-Path -LiteralPath $assembly) {
    throw "Output already exists: $assembly. Preserve previous proof artifacts."
}
$sources = Get-ChildItem (Join-Path $PSScriptRoot 'Observation') -Filter '*.cs' |
    ForEach-Object FullName
# Compilation only. The guest controller can load this with Add-Type -Path.
Add-Type -Path $sources -OutputAssembly $assembly
[pscustomobject]@{
    observerSource = $assembly
    observerSha256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
    compiledOnly = $true
    nativeCallsExecuted = $false
} | ConvertTo-Json
