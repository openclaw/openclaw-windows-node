[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$RunId,
    [scriptblock]$ApiInvoker,
    [scriptblock]$DownloadInvoker
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = 'openclaw/openclaw-windows-node'
if ($null -eq $ApiInvoker) {
    $ApiInvoker = {
        param($Path)
        $json = & gh api --paginate --slurp "repos/$repository/$Path"
        if ($LASTEXITCODE -ne 0) { throw 'Unable to read Store source provenance from GitHub.' }
        return @($json | ConvertFrom-Json)
    }
}
if ($null -eq $DownloadInvoker) {
    $DownloadInvoker = {
        param($Name, $Directory)
        & gh run download $RunId --repo $repository --name $Name --dir $Directory
        if ($LASTEXITCODE -ne 0) { throw 'Unable to download Store release witness.' }
    }
}
$run = @(& $ApiInvoker "actions/runs/$RunId")[0]

$tagRun = $run.path -ceq '.github/workflows/ci.yml' -and
    $run.event -cin @('push', 'workflow_dispatch') -and
    $run.head_branch -cmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9]+)?$'
$promotionRun = $run.path -ceq '.github/workflows/promote-alpha-release.yml' -and
    $run.event -ceq 'workflow_dispatch' -and $run.head_branch -ceq 'main'
if (-not ($tagRun -or $promotionRun)) { return $null }
if ($run.repository.full_name -cne $repository -or $run.head_repository.full_name -cne $repository -or
    [string]$run.id -cne $RunId -or $run.status -cne 'completed' -or $run.conclusion -cne 'success') {
    throw 'Store submission requires a successful completed run in the canonical repository.'
}
$pages = @(& $ApiInvoker "actions/runs/$RunId/artifacts?per_page=100")
$artifacts = @($pages | ForEach-Object { $_.artifacts })
$selected = @($artifacts | Where-Object name -CEQ 'openclaw-msix-store-unsigned-bundle')
if ($selected.Count -eq 0) { return $null }
if ($selected.Count -ne 1 -or $selected[0].expired) { throw 'Store bundle artifact is ambiguous or expired.' }
$witnessName = "store-release-source-$($run.run_attempt)"
$witnessArtifacts = @($artifacts | Where-Object name -CEQ $witnessName)
if ($witnessArtifacts.Count -ne 1 -or $witnessArtifacts[0].expired) {
    throw 'The release run has no current, unexpired Store release witness.'
}
$directory = Join-Path ([IO.Path]::GetTempPath()) "store-source-$([guid]::NewGuid().ToString('N'))"
New-Item $directory -ItemType Directory | Out-Null
try {
    & $DownloadInvoker $witnessName $directory | Out-Null
    $witness = Get-Content (Join-Path $directory 'store-source.json') -Raw | ConvertFrom-Json
    if ([string]$witness.runId -cne $RunId -or [int]$witness.runAttempt -ne [int]$run.run_attempt -or
        $witness.releaseTag -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9]+)?$' -or
        $witness.sourceSha -cnotmatch '^[0-9a-f]{40}$' -or
        $witness.bundleSha256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Store release witness does not match this run and attempt.'
    }
    if ($tagRun -and ($witness.releaseTag -cne $run.head_branch -or $witness.sourceSha -cne $run.head_sha)) {
        throw 'Store release witness does not match the release tag run.'
    }
    $release = @(& $ApiInvoker "releases/tags/$($witness.releaseTag)")[0]
    $commit = @(& $ApiInvoker "commits/$($witness.releaseTag)")[0]
    if ($release.draft -or $release.prerelease -or -not $release.published_at -or
        $release.tag_name -cne $witness.releaseTag -or $commit.sha -cne $witness.sourceSha) {
        throw 'Store source is not the published stable release commit.'
    }
    $bundleName = 'OpenClaw.msixbundle'
    $bundleSha256 = [string]$witness.bundleSha256
} finally { Remove-Item $directory -Recurse -Force }

[pscustomobject]@{
    artifactId = [string]$selected[0].id
    bundleName = $bundleName
    bundleSha256 = $bundleSha256
}
