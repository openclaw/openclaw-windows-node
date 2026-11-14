#Requires -Version 7.4
# Tests scripts/Publish-GatewayThemeSync.ps1 offline: git and gh are replaced by PowerShell functions (the script runs
# them by name), so each path is checked by the commands it would have run. Covers a first publish, an unchanged open
# pull request (no push, no edit), a changed table (push and edit), a declined commit (nothing published) and a current
# branch without an open pull request (create only). Run with ./scripts/test-gateway-theme-sync-publish.ps1.
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
    if ($args -contains 'ls-remote') { if ($global:ThemeSyncScenario.RemoteExists) { "$($global:ThemeSyncRemoteHead)`trefs/heads/automation/gateway-theme-sync" }; return }
    if ($args[0] -eq 'rev-parse') {
        $revision = $args[1]
        if ($revision -like 'HEAD~1:*' -or $revision -like "$($global:ThemeSyncRemoteHead)~1:*") { return 'base-table' }
        if ($revision -like 'HEAD:*') { return 'new-table' }
        return $global:ThemeSyncScenario.RemoteTable
    }
}

function global:gh {
    $line = 'gh ' + ($args -join ' ')
    $global:ThemeSyncCalls.Add($line)
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'api') { return '42' }
    if ($args[0] -eq 'pr' -and $args[1] -eq 'list') {
        $state = $args[$args.IndexOf('--state') + 1]
        return (ConvertTo-Json -InputObject @($global:ThemeSyncScenario.$state) -Depth 4)
    }
    if ($args[0] -eq 'pr' -and $args[1] -eq 'create') { return 'https://github.com/openclaw/openclaw-windows-node/pull/999' }
}

function Invoke-Publish([hashtable]$Scenario) {
    $global:ThemeSyncScenario = [pscustomobject]$Scenario
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

    $calls = Invoke-Publish @{ RemoteExists = $false; RemoteTable = $null; open = @(); closed = @() }
    Assert-True (($calls -match '^git .*push .*--force-with-lease=refs/heads/automation/gateway-theme-sync: ').Count -eq 1) 'A first publish pushes, requiring the branch not to exist.'
    Assert-True (($calls -match '^gh pr create').Count -eq 1) 'A first publish opens the pull request.'
    Assert-True (-not ($calls -match 'test-token')) 'The token never appears on a command line in clear text.'

    $open = @([pscustomobject]@{ number = 7; isCrossRepository = $false; mergedAt = $null; body = "built from $sha" })
    $calls = Invoke-Publish @{ RemoteExists = $true; RemoteTable = 'new-table'; open = $open; closed = @() }
    Assert-True (-not ($calls -match ' push ')) 'An unchanged branch is not pushed.'
    Assert-True (-not ($calls -match '^gh pr (edit|create)')) 'An open pull request that already carries the table is not edited.'

    $calls = Invoke-Publish @{ RemoteExists = $true; RemoteTable = 'old-table'; open = $open; closed = @() }
    Assert-True (($calls -match "--force-with-lease=refs/heads/automation/gateway-theme-sync:$remoteHead").Count -eq 1) 'A changed table is pushed against the observed head.'
    Assert-True (($calls -match '^gh pr edit 7 ').Count -eq 1) 'The open pull request is updated.'

    $fork = @([pscustomobject]@{ number = 8; isCrossRepository = $true; mergedAt = $null; body = '' })
    $declined = @([pscustomobject]@{ number = 6; isCrossRepository = $false; mergedAt = $null; body = "built from $sha" })
    $calls = Invoke-Publish @{ RemoteExists = $true; RemoteTable = 'old-table'; open = $fork; closed = $declined }
    Assert-True (-not ($calls -match '^git ')) 'A commit declined by closing its pull request publishes nothing.'
    Assert-True (-not ($calls -match '^gh pr (edit|create)')) 'A fork pull request with the same branch name is not edited.'

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