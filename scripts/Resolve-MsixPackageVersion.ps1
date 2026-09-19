<#
.SYNOPSIS
    Returns validated MSIX version metadata. Only -Reserve creates Git refs.
.DESCRIPTION
    Reservation requires an existing source tag resolving to SourceCommit.
    Read-only previews use the next unallocated counter and may change between
    reruns when official releases reserve additional versions.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceVersion,
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$SourceRef,
    [Parameter(Mandatory)][string]$Repository,
    [switch]$Reserve,
    [string]$GitHubToken = $env:GH_TOKEN,
    [string]$BaselinePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MsixVersioning.ps1')
if (-not $PSBoundParameters.ContainsKey('BaselinePath')) {
    $BaselinePath = Join-Path $PSScriptRoot '..\.github\msix-version-baseline.json'
}
Resolve-MsixPackageVersion -SourceVersion $SourceVersion -SourceCommit $SourceCommit -SourceRef $SourceRef `
    -Repository $Repository -Reserve:$Reserve -GitHubToken $GitHubToken -BaselinePath $BaselinePath
