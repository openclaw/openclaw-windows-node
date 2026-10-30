#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{64}$')][string]$InputsSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$InvocationId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
try {
    $inventory = 'C:\OpenClawProof\inputs.json'
    if (-not $IsWindows -or (Get-FileHash -LiteralPath $inventory -Algorithm SHA256).Hash.ToLowerInvariant() -cne $InputsSha256) {
        throw 'Approved guest input inventory is unavailable.'
    }
    $inputs = Get-Content -LiteralPath $inventory -Raw | ConvertFrom-Json -AsHashtable
    if (($inputs.schemaVersion -isnot [int] -and $inputs.schemaVersion -isnot [long]) -or $inputs.schemaVersion -ne 1) {
        throw 'Unsupported guest input inventory.'
    }
    $results = Join-Path $PSScriptRoot '..\TestResults\NativeGatewayAzure'
    if (Test-Path -LiteralPath $results) { throw 'Proof output already exists.' }
    & (Join-Path $PSScriptRoot 'Test-NativeGatewayProof.ps1') -SourceSha $SourceSha `
        -MsixPath $inputs.msixPath -MsixSha256 $inputs.msixSha256 `
        -WxcExecPath $inputs.wxcExecPath -WxcExecSha256 $inputs.wxcExecSha256 `
        -PackagingSha $inputs.packagingSha -DisposableHost -ResultsDirectory $results *> $null
    $receipts = @(Get-ChildItem -LiteralPath $results -Recurse -File -Filter 'native-gateway-proof.json')
    if ($receipts.Count -ne 1) { throw 'Expected one fresh native receipt.' }
    $proof = Get-Content -LiteralPath $receipts[0].FullName -Raw | ConvertFrom-Json -AsHashtable
    if ($proof.outcome -cne 'passed') { throw 'Native lifecycle did not pass.' }
    @{ invocationId = $InvocationId; inputsSha256 = $InputsSha256; proof = $proof } | ConvertTo-Json -Depth 12 -Compress
} catch {
    Write-Error 'Native Gateway guest proof failed. Raw package output and private inventory are not exported.' -ErrorAction Continue
    exit 1
}
exit 0
