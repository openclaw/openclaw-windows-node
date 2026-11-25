<#
.SYNOPSIS
Commits a regenerated built-in Gateway theme table to the fixed sync branch and opens or updates its pull request.

.DESCRIPTION
Called by .github/workflows/gateway-theme-sync.yml after Update-GatewayThemeTable.ps1 and the theme tests pass.
It commits only the theme table on top of the checked-out default branch and force-updates the bot-owned
SYNC_BRANCH only when the regenerated table, or the default branch's copy of it, differs from what the branch holds,
so a daily run does not restart CI while the default branch merely moves; an open pull request that already carries
the table is left untouched. The commit records the release and test run; the pull request description stays valid
across updates and is never rewritten. A pull request for the same commit that a maintainer closed without merging is respected
until the packaged Gateway moves again. Pull requests from forks that reuse the branch name are ignored. GH_TOKEN must
be the sync GitHub App token: pull requests opened with GITHUB_TOKEN do not start CI. The token is passed to git as a
per-command environment header, never in argv or stored config. scripts/test-gateway-theme-sync-publish.ps1 covers this offline.

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
    if ($LASTEXITCODE -ne 0) {
        $verb = if ($Arguments[0] -like '--config-env=*') { $Arguments[1] } else { $Arguments[0] }
        throw "$Command $verb failed with exit code $LASTEXITCODE."
    }
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

$bot = "$appSlug[bot]"
$run = "$env:GITHUB_SERVER_URL/$repo/actions/runs/$env:GITHUB_RUN_ID"
$remote = "https://github.com/$repo.git"
$ref = "refs/heads/$branch"

# GitHub retains the PR's head SHA at closure even if the branch is later reused.
function Get-SyncPullRequest([string]$State) {
    $owner = $repo.Split('/')[0]
    $query = 'state={0}&base={1}&head={2}&per_page=1' -f $State, [uri]::EscapeDataString($baseBranch), [uri]::EscapeDataString("${owner}:$branch")
    $json = Invoke-Checked 'gh' @('api', "repos/$repo/pulls?$query")
    $pr = ($json -join "`n") | ConvertFrom-Json | Select-Object -First 1
    if ($pr -and ($pr.head.repo.full_name -cne $repo -or $pr.user.login -cne $bot)) {
        throw "The latest $State pull request for $branch is not owned by the sync App."
    }
    $pr
}

function Test-Declined {
    $last = Get-SyncPullRequest 'closed'
    if ($last -and -not $last.merged_at) {
        $closedHead = $last.head.sha
        if ($closedHead -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Missing closed sync pull request head.' }
        $message = Invoke-Checked 'gh' @('api', "repos/$repo/git/commits/$closedHead", '--jq', '.message')
        if (@($message) -ccontains "Gateway-Upstream: $sha") {
            Add-Summary "Pull request #$($last.number) for $sha was closed without merging; nothing is published."
            return $true
        }
    }
    $false
}

function Invoke-RemoteGit([string[]]$Arguments) {
    $previous = $env:THEME_SYNC_GIT_AUTH
    try {
        $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$(Get-RequiredEnv 'GH_TOKEN')"))
        $env:THEME_SYNC_GIT_AUTH = "AUTHORIZATION: basic $basic"
        Invoke-Checked 'git' (@('--config-env=http.https://github.com/.extraheader=THEME_SYNC_GIT_AUTH') + $Arguments)
    } finally {
        [Environment]::SetEnvironmentVariable('THEME_SYNC_GIT_AUTH', $previous)
    }
}

$open = Get-SyncPullRequest 'open'
if (-not $open -and (Test-Declined)) { return }
$botId = Invoke-Checked 'gh' @('api', "users/$bot", '--jq', '.id')
$email = "$botId+$bot@users.noreply.github.com"
Invoke-Checked 'git' @('config', 'user.name', $bot) | Out-Null
Invoke-Checked 'git' @('config', 'user.email', $email) | Out-Null

$message = "chore(themes): sync built-in Gateway themes to openclaw $version`n`nPackaging release $release ships openclaw $version ($sha).`nValidation: $run`n`nGateway-Upstream: $sha"
Invoke-Checked 'git' @('commit', '--quiet', '--only', '-m', $message, '--', $table) | Out-Null

$remoteHead = (Invoke-RemoteGit @('ls-remote', '--heads', $remote, $ref)) -split '\s+' | Select-Object -First 1
$current = $false
if ($remoteHead) {
    Invoke-RemoteGit @('fetch', '--quiet', '--no-tags', $remote, $remoteHead) | Out-Null
    # A lease only detects writes after observation. Never replace intervening human work.
    $parent = Invoke-Checked 'git' @('show', '-s', '--format=%P', $remoteHead)
    if ($parent -cnotmatch '\A[0-9a-f]{40}\z' -or
        (Invoke-Checked 'git' @('show', '-s', '--format=%ae', $remoteHead)) -cne $email -or
        ((Invoke-Checked 'git' @('diff', '--name-only', $parent, $remoteHead, '--')) -join "`n") -cne $table) {
        throw "Refusing to replace ${branch}: expected one App-authored change to the theme table."
    }
    Invoke-Checked 'git' @('merge-base', '--is-ancestor', $parent, 'HEAD~1') | Out-Null
    $tableAt = { param($Revision) Invoke-Checked 'git' @('rev-parse', "${Revision}:$table") }
    # The branch is one table commit on the default branch: it is current when it carries the same table on a base
    # whose table is unchanged, so it still merges cleanly however far the default branch has moved.
    $current = (& $tableAt 'HEAD') -ceq (& $tableAt $remoteHead) -and (& $tableAt 'HEAD~1') -ceq (& $tableAt "$remoteHead~1")
}
$latest = Get-SyncPullRequest 'open'
if ($open -and (!$latest -or $latest.number -ne $open.number)) {
    Add-Summary 'The sync pull request changed while preparing the update. Leaving it alone.'
    return
}
if ($latest -and $latest.head.sha -cne $remoteHead) { throw 'Sync branch changed during preparation.' }
if (-not $latest -and (Test-Declined)) { return }
if (-not $current) {
    Invoke-RemoteGit @('push', '--quiet', "--force-with-lease=${ref}:$remoteHead", $remote, "HEAD:$ref") | Out-Null
}
if ($latest) {
    Add-Summary "The sync table is on $branch for pull request #$($latest.number)."
    return
}

$title = 'chore(themes): sync built-in Gateway themes with the packaged release'
$body = @"
The latest commit on this pull request records the packaging release, upstream Gateway SHA and generation/test run for the table in **Files changed**. The sync reads the release's SHA-256-matched payload and runs the Shared and Tray ``GatewayTheme`` tests before pushing. Full CI runs on the pull request.

Review added, changed and removed themes before merging. A removed built-in ID no longer resolves for people who saved it. Closing without merging declines the upstream SHA in the commit at closure.

## Required proof pools

- ``none``: generated theme data only; the regenerated values are covered by the theme tests above and CI.
"@
$bodyFile = New-TemporaryFile
try {
    Set-Content -LiteralPath $bodyFile -Value $body -Encoding utf8
    $url = Invoke-Checked 'gh' @('pr', 'create', '--repo', $repo, '--base', $baseBranch, '--head', $branch, '--title', $title, '--body-file', $bodyFile)
    Add-Summary "Opened $url."
}
finally {
    Remove-Item -LiteralPath $bodyFile -ErrorAction SilentlyContinue
}