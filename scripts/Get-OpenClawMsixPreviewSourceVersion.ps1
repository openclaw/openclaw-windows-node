<#
.SYNOPSIS
    Resolves the numeric app base used for unreserved MSIX preview packages.
.DESCRIPTION
    PR and ordinary main builds preview the next package version on the latest
    published stable Windows release line. This affects only MSIX package
    manifests; GitVersion, assemblies, EXE/ZIP artifacts, and release tags
    retain their existing versions.

    Pass CurrentWindowsTag for deterministic/offline evaluation. Otherwise the
    latest published release is read from the canonical upstream repository.
#>
[CmdletBinding()]
param(
    [string]$GitHubToken = $env:GH_TOKEN,
    [string]$CurrentWindowsTag,
    [string]$LatestReleaseJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-MsixPreviewBase {
    param([Parameter(Mandatory)][string]$Tag)

    $match = [regex]::Match(
        $Tag,
        '^v(?<base>[1-9]\d*\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))(?:-[1-9]\d*)?$')
    if (-not $match.Success) {
        throw "Latest Windows release tag '$Tag' is not a stable or numeric-correction release."
    }
    return $match.Groups['base'].Value
}

$tag = $CurrentWindowsTag
if ([string]::IsNullOrWhiteSpace($tag)) {
    if (-not [string]::IsNullOrWhiteSpace($LatestReleaseJson)) {
        try {
            $release = ConvertFrom-Json -InputObject $LatestReleaseJson -ErrorAction Stop
        }
        catch {
            throw 'The supplied latest Windows release response is malformed.'
        }
    }
    else {
        $headers = @{
            Accept = 'application/vnd.github+json'
            'User-Agent' = 'openclaw-windows-node-msix-preview'
            'X-GitHub-Api-Version' = '2022-11-28'
        }
        if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
            $headers.Authorization = [string]::Concat('Bearer ', $GitHubToken)
        }
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                $release = Invoke-RestMethod `
                    -Headers $headers `
                    -Uri 'https://api.github.com/repos/openclaw/openclaw-windows-node/releases/latest' `
                    -MaximumRedirection 0 `
                    -ErrorAction Stop
                break
            }
            catch {
                if ($attempt -eq 3) {
                    throw 'Could not resolve the latest published Windows release for the MSIX preview after three attempts.'
                }
                Start-Sleep -Seconds $attempt
            }
        }
    }
    if ($null -eq $release -or
        $null -eq $release.PSObject.Properties['tag_name'] -or
        [string]::IsNullOrWhiteSpace([string]$release.tag_name) -or
        ($release.PSObject.Properties['draft'] -and $release.draft) -or
        ($release.PSObject.Properties['prerelease'] -and $release.prerelease) -or
        $null -eq $release.PSObject.Properties['published_at'] -or
        $null -eq $release.published_at) {
        throw 'The latest Windows release is missing or is not a published stable release.'
    }
    $tag = [string]$release.tag_name
}

ConvertTo-MsixPreviewBase -Tag $tag
