[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StorePublicationRequest.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "companion-store-request-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null
$eventPath = Join-Path $testRoot 'event.json'
$script:checks = 0
$script:calls = [Collections.Generic.List[object]]::new()
$script:dispatchExitCode = 0

function gh {
    if ($args[0] -ceq 'api') {
        $expected = @('api', '--paginate', '--slurp', 'repos/openclaw/openclaw-windows-node/actions/runs/123/artifacts?per_page=100')
        if (($args -join '|') -cne ($expected -join '|')) { throw 'Unexpected artifact request.' }
        $global:LASTEXITCODE = 0
        return ConvertTo-Json -InputObject @(@{ artifacts = $script:artifacts }) -Depth 10
    }
    $script:calls.Add(@($args))
    $global:LASTEXITCODE = $script:dispatchExitCode
}

function Reset-Fixture {
    $script:calls.Clear()
    $script:dispatchExitCode = 0
    $script:artifacts = @(@{ name = 'store-release-source-2'; expired = $false })
    $script:event = @{
        workflow_run = @{
            id = 123; run_attempt = 2; status = 'completed'; conclusion = 'success'
            repository = @{ full_name = 'openclaw/openclaw-windows-node' }
            head_repository = @{ full_name = 'openclaw/openclaw-windows-node' }
            path = '.github/workflows/ci.yml'; event = 'push'; head_branch = 'v2026.10.21'
        }
    }
}

function Invoke-Request {
    param(
        [string]$Repository = 'openclaw/openclaw-windows-node',
        [string]$Ref = 'refs/heads/main',
        [string]$EventName = 'workflow_run',
        [string]$RunId
    )
    [IO.File]::WriteAllText($eventPath, ($script:event | ConvertTo-Json -Depth 10))
    $selected = Get-CompanionStoreRequestRunId -Repository $Repository -Ref $Ref `
        -EventName $EventName -RunId $RunId -EventPath $eventPath
    if ($null -ne $selected) { Request-CompanionStorePublication -RunId $selected }
    return $selected
}

function Assert-Requested {
    param([string]$RunId)
    $expected = @('workflow', 'run', 'windows-store-publish.yml', '--repo', 'openclaw/releases',
        '--ref', 'main', '-f', 'product=companion', '-f', "run_id=$RunId")
    if ($script:calls.Count -ne 1 -or
        ($script:calls[0] -join '|') -cne ($expected -join '|')) {
        throw 'The request did not dispatch the fixed central main publisher with the exact source run ID.'
    }
    $script:checks++
}

function Assert-Skipped {
    if ($null -ne (Invoke-Request) -or $script:calls.Count -ne 0) {
        throw 'An ineligible completed event dispatched a central publication request.'
    }
    $script:checks++
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Pattern)
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        if ($script:calls.Count -ne 0) { throw 'A rejected request reached GitHub.' }
        $script:checks++
        return
    }
    throw "Expected request rejection: $Pattern"
}

try {
    Reset-Fixture
    Invoke-Request -EventName workflow_dispatch -RunId 456 | Out-Null
    Assert-Requested 456

    Reset-Fixture
    Invoke-Request | Out-Null
    Assert-Requested 123

    foreach ($artifactCase in @(
        @{ items = @() },
        @{ items = @(@{ name = 'store-release-source-1'; expired = $false }) },
        @{ items = @(@{ name = 'store-release-source-2'; expired = $true }) }
    )) {
        Reset-Fixture
        $script:event.workflow_run.path = '.github/workflows/promote-alpha-release.yml'
        $script:event.workflow_run.event = 'workflow_dispatch'
        $script:event.workflow_run.head_branch = 'main'
        $script:artifacts = $artifactCase.items
        Assert-Skipped
    }

    Reset-Fixture
    $script:event.workflow_run.event = 'workflow_dispatch'
    $script:event.workflow_run.head_branch = 'v2026.10.21-1'
    Invoke-Request | Out-Null
    Assert-Requested 123

    Reset-Fixture
    $script:event.workflow_run.path = '.github/workflows/promote-alpha-release.yml'
    $script:event.workflow_run.event = 'workflow_dispatch'
    $script:event.workflow_run.head_branch = 'main'
    Invoke-Request | Out-Null
    Assert-Requested 123

    foreach ($runId in @('', '0', '0123', '-1', '1e3', '123/other', "123`n")) {
        Reset-Fixture
        Assert-Rejected { Invoke-Request -EventName workflow_dispatch -RunId $runId } 'canonical positive integer'
    }
    foreach ($origin in @(
        @{ Repository = 'fork/openclaw-windows-node' },
        @{ Ref = 'refs/heads/feature' },
        @{ EventName = 'pull_request' }
    )) {
        Reset-Fixture
        Assert-Rejected { Invoke-Request @origin -RunId 123 } 'main in|workflow_dispatch or workflow_run'
    }

    foreach ($change in @(
        @{ Field = 'status'; Value = 'in_progress' },
        @{ Field = 'conclusion'; Value = 'failure' },
        @{ Field = 'path'; Value = '.github/workflows/other.yml' },
        @{ Field = 'event'; Value = 'pull_request' },
        @{ Field = 'head_branch'; Value = 'main' },
        @{ Field = 'head_branch'; Value = 'v2026.10.21-alpha.1' },
        @{ Field = 'head_branch'; Value = "v2026.10.21`n" }
    )) {
        Reset-Fixture
        $script:event.workflow_run[$change.Field] = $change.Value
        Assert-Skipped
    }
    foreach ($field in @('repository', 'head_repository')) {
        Reset-Fixture
        $script:event.workflow_run[$field].full_name = 'fork/openclaw-windows-node'
        Assert-Skipped
    }
    foreach ($change in @(
        @{ Field = 'event'; Value = 'push' },
        @{ Field = 'head_branch'; Value = 'v2026.10.21' }
    )) {
        Reset-Fixture
        $script:event.workflow_run.path = '.github/workflows/promote-alpha-release.yml'
        $script:event.workflow_run.event = 'workflow_dispatch'
        $script:event.workflow_run.head_branch = 'main'
        $script:event.workflow_run[$change.Field] = $change.Value
        Assert-Skipped
    }

    Reset-Fixture
    $script:event.workflow_run.id = '0123'
    Assert-Rejected { Invoke-Request } 'canonical positive integer'

    Reset-Fixture
    Assert-Rejected { Request-CompanionStorePublication -RunId "123`n" } 'ValidatePattern|does not match'

    Reset-Fixture
    $script:dispatchExitCode = 1
    try {
        Invoke-Request | Out-Null
        throw 'Expected an unconfirmed request failure.'
    } catch {
        if ($_.Exception.Message -notmatch 'not confirmed.*before retrying') { throw }
    }
    Assert-Requested 123
    $global:LASTEXITCODE = 0

    Write-Host "Companion Store publication requests: $script:checks scenarios passed."
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
