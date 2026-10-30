#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Run', 'Cleanup')][string]$Mode = 'Run',
    [Parameter(Mandatory)][string]$CrabboxPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_REPOSITORY -cne 'openclaw/openclaw-windows-node' -or
    $env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or
    $env:GITHUB_RUN_ID -cnotmatch '^[0-9]+$' -or $env:GITHUB_RUN_ATTEMPT -cnotmatch '^[0-9]+$' -or
    $env:GITHUB_SHA -cnotmatch '^[0-9a-f]{40}$' -or -not $env:RUNNER_TEMP) {
    throw 'This controller is restricted to the canonical manual Actions lane.'
}
Import-Module (Join-Path $PSScriptRoot 'NativeGatewayProof.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'AzureNativeGatewayProof.psm1') -Force
$state = Join-Path $env:RUNNER_TEMP 'native-gateway-azure'
$env:CRABBOX_CONFIG = Join-Path $state 'config.yaml'
$env:CRABBOX_OWNER = 'openclaw-windows-node-ci'
$env:CRABBOX_ORG = 'openclaw'
$env:CRABBOX_ENV_ALLOW = 'CI'
$runKey = "$($env:GITHUB_RUN_ID):$($env:GITHUB_RUN_ATTEMPT):$($env:GITHUB_SHA)"
$invoke = {
    param([string[]]$Arguments, [string]$StandardInput, [int]$TimeoutSeconds)
    Invoke-NativeProofProcess -FilePath $CrabboxPath -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds -StandardInput $StandardInput
}.GetNewClosure()
if ($Mode -eq 'Cleanup') {
    Stop-AzureNativeGatewayLease -InvokeCrabbox $invoke -StateDirectory $state -RunKey $runKey
} else {
    . (Join-Path $PSScriptRoot 'ProofEvidence.ps1')
    $source = Get-ProofSourceIdentity ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))
    if ($source.head -cne $env:GITHUB_SHA -or $source.dirtyFileCount -ne 0) { throw 'Controller checkout does not match the approved clean source.' }
    $privateInputs = @{
        Token = $env:CRABBOX_AUTOMATION_TOKEN
        CheckpointId = $env:CRABBOX_AZURE_CHECKPOINT_ID
        InputsSha256 = $env:NATIVE_GATEWAY_INPUTS_SHA256
    }
    # Do not expose CI secrets to subprocess environments or future remote env allowlists.
    foreach ($name in @('CRABBOX_AUTOMATION_TOKEN', 'CRABBOX_AZURE_CHECKPOINT_ID', 'NATIVE_GATEWAY_INPUTS_SHA256')) {
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
    try {
        Invoke-AzureNativeGatewayProof @privateInputs -InvokeCrabbox $invoke -SourceSha $env:GITHUB_SHA `
            -Class $env:CRABBOX_AZURE_CLASS -TtlMinutes $env:CRABBOX_AZURE_TTL_MINUTES -StateDirectory $state `
            -OutputDirectory (Join-Path $env:RUNNER_TEMP 'native-gateway-evidence') -RunKey $runKey `
            -RunUrl "https://github.com/openclaw/openclaw-windows-node/actions/runs/$($env:GITHUB_RUN_ID)/attempts/$($env:GITHUB_RUN_ATTEMPT)"
    } finally { $privateInputs.Clear() }
}
exit 0
