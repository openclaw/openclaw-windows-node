[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Get-StoreSubmissionSource.ps1'
$repository = 'openclaw/openclaw-windows-node'
$script:checks = 0
function Assert-Fails([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $script:checks++
        return
    }
    throw "Expected rejection: $Pattern"
}
function Reset-Fixture {
    $global:StoreSourceTest_run = [pscustomobject]@{
        id = 123; status = 'completed'; conclusion = 'success'; run_attempt = 2
        repository = [pscustomobject]@{ full_name = $repository }
        head_repository = [pscustomobject]@{ full_name = $repository }
        path = '.github/workflows/ci.yml'; event = 'push'; head_branch = 'v2026.10.21'; head_sha = ('a' * 40)
    }
    $global:StoreSourceTest_artifacts = @([pscustomobject]@{ id = 456; name = 'openclaw-msix-store-unsigned-bundle'; expired = $false })
    $global:StoreSourceTest_artifacts += [pscustomobject]@{ id = 789; name = 'store-release-source-2'; expired = $false }
    $global:StoreSourceTest_witness = [pscustomobject]@{ runId = '123'; runAttempt = 2; releaseTag = 'v2026.10.21'; sourceSha = ('a' * 40); bundleSha256 = ('b' * 64) }
    $global:StoreSourceTest_release = [pscustomobject]@{ draft = $false; prerelease = $false; published_at = '2026-10-21'; tag_name = 'v2026.10.21' }
    $global:StoreSourceTest_commit = [pscustomobject]@{ sha = ('a' * 40) }
}
$api = {
    param($Path)
    switch -Wildcard ($Path) {
        'actions/runs/123' { return $global:StoreSourceTest_run }
        'actions/runs/123/artifacts*' { return [pscustomobject]@{ artifacts = $global:StoreSourceTest_artifacts } }
        'releases/tags/v2026.10.21' { return $global:StoreSourceTest_release }
        'commits/v2026.10.21' { return $global:StoreSourceTest_commit }
        default { throw "Unexpected API path: $Path" }
    }
}
$download = {
    param($Name, $Directory)
    if ($Name -cne 'store-release-source-2') { throw 'Wrong release witness downloaded.' }
    $global:StoreSourceTest_witness | ConvertTo-Json | Set-Content (Join-Path $Directory 'store-source.json') -Encoding utf8
}
function Invoke-Source { & $scriptPath -RunId 123 -ApiInvoker $api -DownloadInvoker $download }
Reset-Fixture
$result = Invoke-Source
if ($result.artifactId -cne '456') { throw 'Authorized release selected the wrong artifact.' }
$script:checks++
foreach ($field in @('conclusion', 'status')) {
    Reset-Fixture
    $global:StoreSourceTest_run.$field = 'failure'
    Assert-Fails { Invoke-Source } 'successful completed run'
}
Reset-Fixture
$global:StoreSourceTest_run.head_repository.full_name = 'fork/untrusted'
Assert-Fails { Invoke-Source } 'canonical repository'
Reset-Fixture
$global:StoreSourceTest_run.event = 'pull_request'
$global:StoreSourceTest_run.head_repository.full_name = 'fork/untrusted'
if ($null -ne (Invoke-Source)) { throw 'A fork PR was not skipped.' }
$script:checks++
foreach ($field in @('path', 'event', 'head_branch')) {
    Reset-Fixture
    $global:StoreSourceTest_run.$field = 'pull_request'
    if ($null -ne (Invoke-Source)) { throw "Untrusted $field was eligible." }
    $script:checks++
}
Reset-Fixture
$global:StoreSourceTest_artifacts[0].expired = $true
Assert-Fails { Invoke-Source } 'ambiguous or expired'
Reset-Fixture
$global:StoreSourceTest_artifacts += $global:StoreSourceTest_artifacts[0]
Assert-Fails { Invoke-Source } 'ambiguous or expired'
Reset-Fixture
$global:StoreSourceTest_artifacts = @()
if ($null -ne (Invoke-Source)) { throw 'A run without an authorized bundle was eligible.' }
$script:checks++
Reset-Fixture
$global:StoreSourceTest_run.head_branch = 'v2026.10.21-alpha.1'
if ($null -ne (Invoke-Source)) { throw 'Alpha was eligible.' }
$script:checks++
Reset-Fixture
$global:StoreSourceTest_run.path = '.github/workflows/promote-alpha-release.yml'
$global:StoreSourceTest_run.event = 'workflow_dispatch'
$global:StoreSourceTest_run.head_branch = 'main'
$global:StoreSourceTest_run.head_sha = ('c' * 40)
if ((Invoke-Source).bundleSha256 -cne ('b' * 64)) { throw 'Promotion did not bind the product source and bundle.' }
$script:checks++
foreach ($field in @('runId', 'runAttempt', 'releaseTag', 'sourceSha', 'bundleSha256')) {
    Reset-Fixture
    $global:StoreSourceTest_witness.$field = if ($field -eq 'runAttempt') { 1 } else { '1' }
    Assert-Fails { Invoke-Source } 'witness does not match'
}
Reset-Fixture
$global:StoreSourceTest_artifacts = @($global:StoreSourceTest_artifacts[0])
Assert-Fails { Invoke-Source } 'no current, unexpired'
Reset-Fixture
$global:StoreSourceTest_release.prerelease = $true
Assert-Fails { Invoke-Source } 'published stable release'
Reset-Fixture
$global:StoreSourceTest_commit.sha = ('d' * 40)
Assert-Fails { Invoke-Source } 'published stable release'
Reset-Fixture
$global:StoreSourceTest_run.head_sha = ('d' * 40)
Assert-Fails { Invoke-Source } 'release tag run'

Write-Host "Store source authorization: $script:checks scenarios passed."

Get-Variable -Name StoreSourceTest_* -Scope Global | Remove-Variable -Scope Global
