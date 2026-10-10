<#
.SYNOPSIS
    Returns validated MSIX version metadata. Only -Reserve creates Git refs.
.DESCRIPTION
    Reservation requires an existing source tag resolving to SourceCommit.
    After promotion policy validation, PromotionAlphaTag verifies the existing
    same-base alpha instead, leaving the public stable tag for publication.
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
    [string]$PromotionAlphaTag,
    [string]$GitHubToken = $env:GH_TOKEN,
    [string]$BaselinePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MsixVersioning.ps1')
if (-not $PSBoundParameters.ContainsKey('BaselinePath')) {
    $BaselinePath = Join-Path $PSScriptRoot '..\.github\msix-version-baseline.json'
}
$promotionArguments = @{}
if ($PSBoundParameters.ContainsKey('PromotionAlphaTag')) { $promotionArguments.PromotionAlphaTag = $PromotionAlphaTag }
Resolve-MsixPackageVersion @promotionArguments -SourceVersion $SourceVersion -SourceCommit $SourceCommit -SourceRef $SourceRef `
    -Repository $Repository -Reserve:$Reserve -GitHubToken $GitHubToken -BaselinePath $BaselinePath
