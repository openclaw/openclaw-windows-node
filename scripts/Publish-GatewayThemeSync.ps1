<#
.SYNOPSIS
Commits a regenerated built-in Gateway theme table to the fixed sync branch and opens or updates its pull request.

.DESCRIPTION
Called by .github/workflows/gateway-theme-sync.yml after Update-GatewayThemeTable.ps1 and the theme tests pass.
It commits only the theme table on top of the checked-out default branch and force-updates the bot-owned
SYNC_BRANCH only when the regenerated table, or the default branch's copy of it, differs from what the branch holds,
so a daily run does not restart CI while the default branch merely moves; an open pull request that already carries
the table is left untouched. A pull request for the same commit that a maintainer closed without merging is respected
until the packaged Gateway moves again. Pull requests from forks that reuse the branch name are ignored. GH_TOKEN must
be the sync GitHub App token: pull requests opened with GITHUB_TOKEN do not start CI. The token is passed to git as a
per-command header, never stored in git config. scripts/test-gateway-theme-sync-publish.ps1 covers each path offline.

.EXAMPLE
# In the workflow, with GH_TOKEN, APP_SLUG, SYNC_BRANCH, THEME_TABLE, PACKAGING_RELEASE, GATEWAY_VERSION and UPSTREAM_REF set:
./scripts/Publish-GatewayThemeSync.ps1
#>
#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RequiredEnv([string]$Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) { throw "$Name is required." }
    $value
}

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    $output = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command $($Arguments[0]) failed with exit code $LASTEXITCODE." }
    $output
}

function Add-Summary([string]$Line) {
    if ($env:GITHUB_STEP_SUMMARY) { $Line | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
    Write-Host $Line
}

$repo = Get-RequiredEnv 'GITHUB_REPOSITORY'
$baseBranch = Get-RequiredEnv 'GITHUB_REF_NAME'
$branch = Get-RequiredEnv 'SYNC_BRANCH'
$table = Get-RequiredEnv 'THEME_TABLE'
$appSlug = Get-RequiredEnv 'APP_SLUG'
$release = Get-RequiredEnv 'PACKAGING_RELEASE'
$version = Get-RequiredEnv 'GATEWAY_VERSION'
$sha = Get-RequiredEnv 'UPSTREAM_REF'
if ($sha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'UPSTREAM_REF must be a commit SHA.' }

# Only this repository's own sync pull requests; a fork branch with the same name is not ours to edit.
function Get-SyncPullRequests([string]$State) {
    $json = Invoke-Checked 'gh' @('pr', 'list', '--repo', $repo, '--head', $branch, '--base', $baseBranch, '--state', $State,
        '--limit', '20', '--json', 'number,isCrossRepository,mergedAt,body')
    @(($json -join "`n") | ConvertFrom-Json | Where-Object { -not $_.isCrossRepository })
}

$open = @(Get-SyncPullRequests 'open') | Select-Object -First 1
if (-not $open) {
    $declined = @(Get-SyncPullRequests 'closed') |
        Where-Object { -not $_.mergedAt -and $_.body -and $_.body.Contains($sha) } | Select-Object -First 1
    if ($declined) {
        Add-Summary "Pull request #$($declined.number) for $sha was closed without merging; nothing is published until the packaged Gateway moves."
        return
    }
}

$basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$(Get-RequiredEnv 'GH_TOKEN')"))
Write-Host "::add-mask::$basic"
$auth = @('-c', "http.https://github.com/.extraheader=AUTHORIZATION: basic $basic")
$remote = "https://github.com/$repo.git"

$bot = "$appSlug[bot]"
$botId = Invoke-Checked 'gh' @('api', "users/$bot", '--jq', '.id')
Invoke-Checked 'git' @('config', 'user.name', $bot) | Out-Null
Invoke-Checked 'git' @('config', 'user.email', "$botId+$bot@users.noreply.github.com") | Out-Null

Invoke-Checked 'git' @('add', '--', $table) | Out-Null
$message = "chore(themes): sync built-in Gateway themes to openclaw $version`n`nPackaging release $release ships openclaw $version ($sha)."
Invoke-Checked 'git' @('commit', '--quiet', '-m', $message) | Out-Null

$remoteHead = (Invoke-Checked 'git' ($auth + @('ls-remote', '--heads', $remote, "refs/heads/$branch"))) -split '\s+' | Select-Object -First 1
$current = $false
if ($remoteHead) {
    Invoke-Checked 'git' ($auth + @('fetch', '--quiet', '--no-tags', $remote, $remoteHead)) | Out-Null
    $tableAt = { param($Revision) Invoke-Checked 'git' @('rev-parse', "${Revision}:$table") }
    # The branch is one table commit on the default branch: it is current when it carries the same table on a base
    # whose table is unchanged, so it still merges cleanly however far the default branch has moved.
    $current = (& $tableAt 'HEAD') -ceq (& $tableAt $remoteHead) -and (& $tableAt 'HEAD~1') -ceq (& $tableAt "$remoteHead~1")
}
if ($current -and $open) {
    # Editing an unchanged pull request would only churn its body (the run link) and restart review intake.
    Add-Summary "Pull request #$($open.number) already carries this table."
    return
}
if ($current) {
    Add-Summary "$branch already carries this table."
}
else {
    $lease = "--force-with-lease=refs/heads/${branch}:$remoteHead"
    Invoke-Checked 'git' ($auth + @('push', '--quiet', $lease, $remote, "HEAD:refs/heads/$branch")) | Out-Null
}

$title = "chore(themes): sync built-in Gateway themes to openclaw $version"
$run = "$env:GITHUB_SERVER_URL/$repo/actions/runs/$env:GITHUB_RUN_ID"
$body = @"
The latest stable packaged Gateway, [$release](https://github.com/openclaw/openclaw-windows-packaging/releases/tag/$release), ships openclaw $version, built from [$sha](https://github.com/openclaw/openclaw/commit/$sha) according to its verified payload. This regenerates the Companion's built-in Gateway theme table at that commit with ``scripts/Update-GatewayThemeTable.ps1 -UpstreamRef $sha``.

The [sync run]($run) passed the Shared and Tray ``GatewayTheme`` tests on the regenerated table, including the check that every built-in theme font is bundled with the Companion. Review the table diff for new or changed themes before merging; full CI runs on this pull request. Closing this pull request without merging skips this commit until the packaged Gateway moves again.

## Required proof pools

- ``none``: generated theme data only; the regenerated values are covered by the theme tests above and CI.
"@
$bodyFile = New-TemporaryFile
try {
    Set-Content -LiteralPath $bodyFile -Value $body -Encoding utf8
    if ($open) {
        Invoke-Checked 'gh' @('pr', 'edit', $open.number, '--repo', $repo, '--title', $title, '--body-file', $bodyFile) | Out-Null
        Add-Summary "Updated pull request #$($open.number)."
    }
    else {
        $url = Invoke-Checked 'gh' @('pr', 'create', '--repo', $repo, '--base', $baseBranch, '--head', $branch, '--title', $title, '--body-file', $bodyFile)
        Add-Summary "Opened $url."
    }
}
finally {
    Remove-Item -LiteralPath $bodyFile -ErrorAction SilentlyContinue
}