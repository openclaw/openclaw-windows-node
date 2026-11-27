#Requires -Version 7.4
# Tests scripts/Publish-GatewayThemeSync.ps1 offline: git and gh are replaced by PowerShell functions (the script runs
# them by name). Covers branch leases, commit provenance, unchanged or declined proposals, and recovery after a
# partial publication. Run with ./scripts/test-gateway-theme-sync-publish.ps1.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publish = Join-Path $PSScriptRoot 'Publish-GatewayThemeSync.ps1'
$script:passed = 0
$sha = 'a' * 40
$remoteHead = 'b' * 40
$global:ThemeSyncRemoteHead = $remoteHead

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}

# Scenario state read by the fakes.
$global:ThemeSyncScenario = $null
$global:ThemeSyncCalls = [System.Collections.Generic.List[string]]::new()

function global:git {
    $line = 'git ' + ($args -join ' ')
    $global:ThemeSyncCalls.Add($line)
    $global:LASTEXITCODE = 0
    $s = $global:ThemeSyncScenario
    if ($args -contains 'ls-remote') { if ($global:ThemeSyncScenario.RemoteExists) { "$($global:ThemeSyncRemoteHead)`trefs/heads/automation/gateway-theme-sync" }; return }
    if ($args -contains 'push' -and $s.FailPush) { $global:LASTEXITCODE = 1; return }
    if ($args -contains '--format=%P') { return ('c' * 40) }
    if ($args -contains '--format=%ae') { return $s.Author }
    if ($args[0] -eq 'diff') { return $s.Paths }
    if ($args[0] -eq 'merge-base' -and -not $s.OnBase) { $global:LASTEXITCODE = 1; return }
    if ($args[0] -eq 'rev-parse') {
        $revision = $args[1]
        if ($revision -like 'HEAD~1:*') { return 'base-table' }
        if ($revision -like "$($global:ThemeSyncRemoteHead)~1:*") { return $s.RemoteBase }
        if ($revision -like 'HEAD:*') { return 'new-table' }
        return $global:ThemeSyncScenario.RemoteTable
    }
}

function global:gh {
    $line = 'gh ' + ($args -join ' ')
    $global:ThemeSyncCalls.Add($line)
    $global:LASTEXITCODE = 0
    $s = $global:ThemeSyncScenario
    if ($args[0] -eq 'api') {
        if ($args[1] -like 'users/*') { return '42' }
        if ($args[1] -like '*/git/commits/*') { return $s.ClosedMessage }
        if ($args[1] -notmatch '/pulls\?state=(open|closed)&base=main&head=openclaw%3Aautomation%2Fgateway-theme-sync&per_page=1$') {
            throw "Unexpected PR query: $($args[1])"
        }
        $rows = $s[$Matches[1]]
        if ($Matches[1] -eq 'open' -and $s.OpenReads++ -gt 0 -and $s.Disappeared) { $rows = @() }
        return (ConvertTo-Json -InputObject @($rows) -Depth 5)
    }
    if ($args[0] -eq 'pr' -and $args[1] -eq 'create') { return 'https://github.com/openclaw/openclaw-windows-node/pull/999' }
}

function Invoke-Publish([hashtable]$Scenario) {
    $global:ThemeSyncScenario = @{
        RemoteExists = $false; RemoteTable = 'old-table'; RemoteBase = 'base-table'; open = @(); closed = @(); ClosedMessage = @()
        Author = '42+theme-sync[bot]@users.noreply.github.com'; Paths = @($env:THEME_TABLE); OnBase = $true
        Disappeared = $false; OpenReads = 0; FailPush = $false
    }
    foreach ($key in $Scenario.Keys) { $global:ThemeSyncScenario[$key] = $Scenario[$key] }
    $global:ThemeSyncCalls.Clear()
    & $publish | Out-Null
    , @($global:ThemeSyncCalls)
}

$summary = New-TemporaryFile
$saved = @{}
$environment = @{
    GITHUB_REPOSITORY = 'openclaw/openclaw-windows-node'; GITHUB_REF_NAME = 'main'; SYNC_BRANCH = 'automation/gateway-theme-sync'
    THEME_TABLE = 'src/OpenClaw.Shared/Themes/BuiltinGatewayThemes.g.cs'; APP_SLUG = 'theme-sync'
    PACKAGING_RELEASE = 'v2026.11.1-msix.0'; GATEWAY_VERSION = '2026.11.1'; UPSTREAM_REF = $sha; GH_TOKEN = 'test-token'
    GITHUB_STEP_SUMMARY = $summary.FullName; GITHUB_SERVER_URL = 'https://github.com'; GITHUB_RUN_ID = '1'
}
try {
    foreach ($name in $environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $environment[$name])
    }

    $calls = Invoke-Publish @{}
    Assert-True (($calls -match '^git .*push .*--force-with-lease=refs/heads/automation/gateway-theme-sync: ').Count -eq 1) 'A first publish pushes, requiring the branch not to exist.'
    Assert-True (($calls -match '^gh pr create').Count -eq 1) 'A first publish opens the pull request.'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('x-access-token:test-token'))
    Assert-True (-not ($calls -match "test-token|$([regex]::Escape($encoded))")) 'Neither clear nor encoded token belongs in argv.'
    Assert-True (($calls -match "(?s)^git commit .*--only .*Validation: https://github.com/.*/actions/runs/1.*Gateway-Upstream: $sha -- ").Count -eq 1) 'The commit carries its own immutable validation link and upstream SHA.'

    $open = @(@{ number = 7; user = @{login='theme-sync[bot]'}; head = @{repo=@{full_name=$env:GITHUB_REPOSITORY}; sha=$remoteHead}; merged_at = $null })
    $calls = Invoke-Publish @{ RemoteExists = $true; RemoteTable = 'new-table'; open = $open; closed = @() }
    Assert-True (-not ($calls -match ' push ')) 'An unchanged branch is not pushed.'
    Assert-True (-not ($calls -match '^gh pr (edit|create)')) 'An open pull request that already carries the table is not edited.'

    $calls = Invoke-Publish @{ RemoteExists = $true; RemoteTable = 'old-table'; open = $open; closed = @() }
    Assert-True (($calls -match "--force-with-lease=refs/heads/automation/gateway-theme-sync:$remoteHead").Count -eq 1) 'A changed table is pushed against the observed head.'
    Assert-True (-not ($calls -match '^gh pr (edit|create)')) 'The branch update needs no metadata edit that could race closure.'

    $calls = Invoke-Publish @{ RemoteExists = $true; closed = $open; ClosedMessage = @('Release update', "Gateway-Upstream: $sha") }
    Assert-True (-not ($calls -match '^git ')) 'A commit declined by closing its pull request publishes nothing.'
    Assert-True (($calls -match "^gh api .*/git/commits/$remoteHead ").Count -eq 1) 'Decline is read from the exact closed head, not editable PR prose.'

    $calls = Invoke-Publish @{ RemoteExists = $true; closed = $open; ClosedMessage = @("Gateway-Upstream: $('d'*40)") }
    Assert-True (($calls -match '^gh pr create').Count -eq 1) 'Declining an older commit does not suppress the new upstream.'

    $calls = Invoke-Publish @{ RemoteExists = $true; open = $open; Disappeared = $true }
    Assert-True (-not ($calls -match ' push |^gh pr (edit|create)')) 'A PR closed or merged during preparation stays untouched.'

    $calls = Invoke-Publish @{ RemoteExists = $true; RemoteTable = 'new-table'; RemoteBase = 'previous-base'; open = $open }
    Assert-True (($calls -match ' push ').Count -eq 1) 'A changed base table requires regenerating the branch against the new base.'

    foreach ($case in @(
        @{ Author = 'maintainer@example.invalid' }, @{ Paths = @($env:THEME_TABLE, 'other.txt') }, @{ OnBase = $false }, @{ FailPush = $true }
    )) {
        $expected = if ($case.ContainsKey('FailPush')) { 'git push failed' } elseif ($case.ContainsKey('OnBase')) { 'git merge-base failed' } else { 'Refusing to replace' }
        try { Invoke-Publish (@{ RemoteExists=$true } + $case) | Out-Null; throw 'Expected protection to stop publishing.' }
        catch { if (!$_.Exception.Message.StartsWith($expected)) { throw } }
        Assert-True (-not ($global:ThemeSyncCalls -match '^gh pr (create|edit)')) 'A foreign branch or rejected lease must not publish a PR.'
        if (!$case.ContainsKey('FailPush')) {
            Assert-True (-not ($global:ThemeSyncCalls -match ' push ')) 'Already-present human changes are not overwritten.'
        }
    }

    $calls = Invoke-Publish @{ RemoteExists = $true; RemoteTable = 'new-table'; open = @(); closed = @() }
    Assert-True (-not ($calls -match ' push ')) 'A current branch is not pushed again.'
    Assert-True (($calls -match '^gh pr create').Count -eq 1) 'A current branch without an open pull request gets one.'
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Remove-Item -LiteralPath $summary -ErrorAction SilentlyContinue
    Remove-Item function:global:git, function:global:gh -ErrorAction SilentlyContinue
    Remove-Variable ThemeSyncScenario, ThemeSyncCalls, ThemeSyncRemoteHead -Scope Global -ErrorAction SilentlyContinue
}

Write-Host "Gateway theme sync publish tests passed: $script:passed assertions."