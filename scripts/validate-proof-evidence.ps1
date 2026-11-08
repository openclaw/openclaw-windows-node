[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$SummaryPath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string[]]$RequiredPoolId,
    [string[]]$RequiredCommandId = @(),
    [string]$RepoRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
. (Join-Path $PSScriptRoot 'ProofEvidence.ps1')
Test-ProofEvidence -RepoRoot $RepoRoot -SummaryPath $SummaryPath -RequiredPoolId $RequiredPoolId -RequiredCommandId $RequiredCommandId |
    ConvertTo-Json -Depth 5
