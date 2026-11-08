[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Config,
    [Parameter(Mandatory)][string] $LeaseId,
    [Parameter(Mandatory)][string] $RunRoot,
    [Parameter(Mandatory)][ValidateSet('Disabled','Record')][string] $Mode
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\StartupProof.ps1"
. "$PSScriptRoot\StartupProof.Native.ps1"
$c = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
if ($c.leaseId -cne $LeaseId -or $c.computer -cne $env:COMPUTERNAME -or
    $LeaseId -notmatch '^cbx_[a-f0-9]+$' -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Only the exact newly authorized guest and PowerShell 7 may execute this entrypoint.'
}
$trace = [StartupTraceAdapter]::new([StartupTraceMode]$Mode, {
    param($ctx,$mode)
    # Load the recorder and watchdog before starting the measured observer interval.
    $callerPath = Join-Path $ctx.output 'caller-ready.json'
    $ctx.caller | Select-Object * -ExcludeProperty actualProcess |
        ConvertTo-Json -Depth 8 | Set-Content "$callerPath.writing" -Encoding utf8NoBOM
    Move-Item "$callerPath.writing" $callerPath
    Wait-StartupReceipt (Join-Path (Split-Path $ctx.output) 'control\recorder-prepared.json') 60
}, {
    param($ctx,$mode)
    if ($ctx.state.observer.ExecutionBlocked) { throw 'Observer qualification blocked before recorder readiness.' }
    @{ ready = $true; bindingHash = $ctx.bindingHash; leaseId = $ctx.config.leaseId } |
        ConvertTo-Json | Set-Content (Join-Path $ctx.output 'observer-qualified.json')
    $receipt = Wait-StartupReceipt (Join-Path (Split-Path $ctx.output) 'control\trace-ready.json') 60
    if ($receipt.bindingHash -cne $ctx.bindingHash -or $receipt.leaseId -cne $ctx.config.leaseId) {
        throw 'Recorder source/caller binding mismatch.'
    }
    $receipt
}, {
    param($ctx,$mode)
    $control = Join-Path (Split-Path $ctx.output) 'control'
    @{ finished = $true } | ConvertTo-Json | Set-Content (Join-Path $control 'scenario-finished.json')
    Wait-StartupReceipt (Join-Path $control 'trace-stopped.json') 60
})
$dry = if ($Mode -ceq 'Record') { Get-Content $c.dryRunReceipt -Raw | ConvertFrom-Json } else { $null }
$counter = [int](Test-Path -LiteralPath $c.traceStartsFile)
$run = Invoke-StartupProofRun -Config $c -Trace $trace -Operations (New-StartupNativeOperations) `
    -RunDirectory (Join-Path $RunRoot 'scenario') -DryRunReceipt $dry -TraceStartsCounter $counter
if (!$run.completed -or !$run.functionalContractsPassed) { throw 'Full startup wrapper failed. Preserve run.json.' }
