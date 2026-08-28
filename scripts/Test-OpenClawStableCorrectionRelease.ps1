[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)-[1-9]\d*$')]
    [string]$Tag,

    [string]$GitHubToken = $env:GITHUB_TOKEN
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-StableReleaseVersion {
    param([Parameter(Mandatory)][string]$Value)

    $match = [regex]::Match(
        $Value,
        '^v?(?<year>0|[1-9]\d*)\.(?<month>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<revision>[1-9]\d*))?$')
    if (-not $match.Success) {
        throw "Stable release version '$Value' has an unsupported format."
    }

    return @(
        [long]$match.Groups["year"].Value,
        [long]$match.Groups["month"].Value,
        [long]$match.Groups["patch"].Value,
        $(if ($match.Groups["revision"].Success) {
            [long]$match.Groups["revision"].Value
        } else {
            0L
        })
    )
}

function Assert-NewerStableRelease {
    param(
        [Parameter(Mandatory)][string]$Candidate,
        [Parameter(Mandatory)][string]$Current
    )

    $candidateParts = ConvertTo-StableReleaseVersion $Candidate
    $currentParts = ConvertTo-StableReleaseVersion $Current
    for ($index = 0; $index -lt $candidateParts.Count; $index++) {
        if ($candidateParts[$index] -gt $currentParts[$index]) {
            return
        }
        if ($candidateParts[$index] -lt $currentParts[$index]) {
            throw "Stable correction '$Candidate' does not advance current Windows release '$Current'."
        }
    }

    throw "Stable correction '$Candidate' is already the current Windows release."
}

$headers = @{
    Accept = "application/vnd.github+json"
    "User-Agent" = "openclaw-windows-node-release"
    "X-GitHub-Api-Version" = "2022-11-28"
}
if (-not [string]::IsNullOrWhiteSpace($GitHubToken)) {
    $headers.Authorization = "Bearer $GitHubToken"
}

$upstreamRelease = Invoke-RestMethod `
    -Headers $headers `
    -Uri "https://api.github.com/repos/openclaw/openclaw/releases/tags/$Tag"
if ($upstreamRelease.tag_name -cne $Tag -or
    $upstreamRelease.draft -or
    $upstreamRelease.prerelease -or
    $null -eq $upstreamRelease.published_at) {
    throw "Stable correction '$Tag' must match an exact published stable openclaw/openclaw release."
}

$currentWindowsRelease = Invoke-RestMethod `
    -Headers $headers `
    -Uri "https://api.github.com/repos/openclaw/openclaw-windows-node/releases/latest"
Assert-NewerStableRelease `
    -Candidate $Tag `
    -Current $currentWindowsRelease.tag_name

$parsedTag = ConvertTo-StableReleaseVersion $Tag
[pscustomobject]@{
    Tag = $Tag
    BaseVersion = "$($parsedTag[0]).$($parsedTag[1]).$($parsedTag[2])"
    CurrentWindowsTag = $currentWindowsRelease.tag_name
}
