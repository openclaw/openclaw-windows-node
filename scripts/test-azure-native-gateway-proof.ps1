#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AzureNativeGatewayProof.psm1') -Force
$script:passed = 0
function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
function Assert-Throws([scriptblock]$Action, [string]$Expected) {
    try { & $Action }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        $script:passed++
        return
    }
    throw "Expected failure containing '$Expected'."
}

function New-EnvironmentFixture {
    return @{
        name = 'native-gateway-azure'; can_admins_bypass = $false
        deployment_branch_policy = @{ custom_branch_policies = $true; protected_branches = $false }
        protection_rules = @(@{
            type = 'required_reviewers'; prevent_self_review = $true
            reviewers = @(@{ type = 'Team'; reviewer = @{ id = 42 } })
        })
    }
}
$branches = @{ total_count = 1; branch_policies = @(@{ name = 'main'; type = 'branch' }) }
$environment = New-EnvironmentFixture
Assert-AzureNativeWorkflowAdmission 'openclaw/openclaw-windows-node' 'refs/heads/main' 'workflow_dispatch' $environment $branches
$script:passed++
foreach ($context in @(
    @('contributor/openclaw-windows-node', 'refs/heads/main', 'workflow_dispatch'),
    @('openclaw/openclaw-windows-node', 'refs/heads/feature', 'workflow_dispatch'),
    @('openclaw/openclaw-windows-node', 'refs/heads/main', 'pull_request_target')
)) {
    Assert-Throws { Assert-AzureNativeWorkflowAdmission @context $environment $branches } 'canonical main'
}
foreach ($fault in @('missing-reviewer', 'self-review', 'admin-bypass', 'string-bypass', 'wrong-branches')) {
    $environment = New-EnvironmentFixture
    $policy = @{ total_count = 1; branch_policies = @(@{ name = 'main'; type = 'branch' }) }
    switch ($fault) {
        'missing-reviewer' { $environment.protection_rules[0].reviewers = @() }
        'self-review' { $environment.protection_rules[0].prevent_self_review = $false }
        'admin-bypass' { $environment.can_admins_bypass = $true }
        'string-bypass' { $environment.can_admins_bypass = 'false' }
        'wrong-branches' { $policy.branch_policies[0].name = '*' }
    }
    Assert-Throws { Assert-AzureNativeWorkflowAdmission 'openclaw/openclaw-windows-node' 'refs/heads/main' 'workflow_dispatch' $environment $policy } 'existing protected environment'
}

function New-CheckpointFixture {
    return @{
        localState = 'metadata_available'; providerState = 'succeeded'; nextAction = 'fork_or_delete'
        record = @{
            id = 'chk_0123456789abcdef'; provider = 'azure'; kind = 'azure-os-disk-snapshot'
            targetOS = 'windows'; windowsMode = 'normal'
            ownership = @{ mode = 'coordinator'; origin = 'https://crabbox.openclaw.ai' }
            native = @{ provider = 'azure'; architecture = 'amd64'; imageId = 'test-snapshot-reference' }
        }
    }
}
$audit = New-CheckpointFixture
Assert-AzureNativeCheckpoint $audit 'chk_0123456789abcdef'
$script:passed++
$audit.providerState = 'Succeeded'
Assert-AzureNativeCheckpoint $audit 'chk_0123456789abcdef'
$script:passed++
foreach ($fault in @('pending', 'local', 'aws', 'wsl', 'marketplace', 'detached', 'missing-image', 'wrong-id', 'wrong-origin')) {
    $audit = New-CheckpointFixture
    switch ($fault) {
        'pending' { $audit.providerState = 'pending' }
        'local' { $audit.record.ownership.mode = 'operator' }
        'aws' { $audit.record.provider = 'aws' }
        'wsl' { $audit.record.windowsMode = 'wsl2' }
        'marketplace' { $audit.record.kind = 'azure-managed-image' }
        'detached' { $audit.record.native.detachedReason = 'unbound' }
        'missing-image' { $audit.record.native.imageId = '' }
        'wrong-id' { $audit.record.id = 'chk_ffffffffffffffff' }
        'wrong-origin' { $audit.record.ownership.origin = 'https://untrusted.invalid' }
    }
    Assert-Throws { Assert-AzureNativeCheckpoint $audit 'chk_0123456789abcdef' } 'coordinator-managed'
}

function New-EvidenceFixture([string]$InvocationId) {
    return @{
        invocationId = $InvocationId; inputsSha256 = ('b' * 64)
        proof = @{
            schemaVersion = 1; scope = 'native-gateway-package-lifecycle'; outcome = 'passed'
            source = @{ head = ('a' * 40); dirtyFileCount = 0 }
            packagingSha = ('c' * 40); msixSha256 = ('d' * 64); wxcExecSha256 = ('e' * 64)
            build = @{ packagingSha = ('c' * 40); packageVersion = '2026.10.20.0'; payloadVersion = '2026.10.20'; payloadSha = ('f' * 40) }
            operations = @('setup', 'companion prepare', 'start/status', 'restart/status', 'stop/status', 'teardown/status')
            sessionCleanup = 'passed'; packageCleanup = 'passed'; privateData = 'never-export-this-value'
        }
    }
}
$envelope = New-EvidenceFixture ('1' * 32)
$evidence = ConvertTo-AzureNativeEvidence ($envelope | ConvertTo-Json -Depth 12) ('a' * 40) ('b' * 64) ('1' * 32)
Assert-True (($evidence | ConvertTo-Json) -notmatch 'privateData|never-export') 'Guest JSON must be projected, not uploaded verbatim.'
foreach ($fault in @('wrong-source', 'dirty', 'string-count', 'wrong-inputs', 'old-invocation', 'skipped', 'cleanup', 'missing-stage', 'string-schema')) {
    $envelope = New-EvidenceFixture ('1' * 32)
    switch ($fault) {
        'wrong-source' { $envelope.proof.source.head = '2' * 40 }
        'dirty' { $envelope.proof.source.dirtyFileCount = 1 }
        'string-count' { $envelope.proof.source.dirtyFileCount = '0' }
        'wrong-inputs' { $envelope.inputsSha256 = '2' * 64 }
        'old-invocation' { $envelope.invocationId = '2' * 32 }
        'skipped' { $envelope.proof.outcome = 'skipped' }
        'cleanup' { $envelope.proof.sessionCleanup = 'not-confirmed' }
        'missing-stage' { $envelope.proof.operations = @('setup') }
        'string-schema' { $envelope.proof.schemaVersion = '1' }
    }
    Assert-Throws { ConvertTo-AzureNativeEvidence ($envelope | ConvertTo-Json -Depth 12) ('a' * 40) ('b' * 64) ('1' * 32) } 'incomplete or does not match'
}

# Real orchestration with fixture transport only. These tests never log in or allocate a VM.
$root = Join-Path ([IO.Path]::GetTempPath()) ("openclaw-azure-proof-test-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
try {
    foreach ($fault in @('', 'missing-config', 'login', 'personal', 'wrong-org', 'checkpoint', 'fork', 'fork-timeout', 'fork-identity', 'run', 'evidence', 'cleanup')) {
        $directory = Join-Path $root ([guid]::NewGuid().ToString('N'))
        $state = @{
            fault = $fault; directory = $directory; calls = [Collections.Generic.List[string]]::new()
            leaseId = $null; credentialInArguments = $false; forkPersisted = $false
        }
        $checkpoint = New-CheckpointFixture
        $newEvidence = ${function:New-EvidenceFixture}
        $invoke = {
            param([string[]]$Arguments, [string]$StandardInput, [int]$TimeoutSeconds)
            $state.calls.Add($Arguments[0])
            if (($Arguments -join ' ') -match 'test-auth-token') { $state.credentialInArguments = $true }
            $wire = @{}
            switch ($Arguments[0]) {
                'login' {
                    if ($StandardInput -cne "test-auth-token`n") { throw 'Fixture expected credential on stdin only.' }
                    if ($state.fault -eq 'login') { return @{ exitCode = 1; output = 'never-export-this-value' } }
                }
                'whoami' {
                    $wire = @{ auth = 'bearer'; org = 'openclaw' }
                    if ($state.fault -eq 'personal') { $wire.auth = 'github' }
                    if ($state.fault -eq 'wrong-org') { $wire.org = 'unrelated' }
                }
                'checkpoint' {
                    if ($Arguments[1] -eq 'inspect') {
                        $wire = $checkpoint
                        if ($state.fault -eq 'checkpoint') { $wire.providerState = 'unknown' }
                    } else {
                        $lease = $Arguments[[array]::IndexOf($Arguments, '--lease-id') + 1]
                        $intent = Get-Content -LiteralPath (Join-Path $state.directory 'lease.json') -Raw | ConvertFrom-Json -AsHashtable
                        $state.forkPersisted = $intent.leaseId -ceq $lease -and $intent.creationRequested -ceq $true
                        $state.leaseId = $lease
                        if ($state.fault -eq 'fork-timeout') { throw 'never-export-this-value' }
                        if ($state.fault -eq 'fork') { return @{ exitCode = 1; output = 'never-export-this-value' } }
                        $wire = @{ leaseId = $lease; provider = 'azure'; checkpointId = 'chk_0123456789abcdef'; workdir = '' }
                        if ($state.fault -eq 'fork-identity') { $wire.leaseId = 'cbx_ffffffffffff' }
                    }
                }
                'run' {
                    if ($state.fault -eq 'run') { return @{ exitCode = 1; output = 'never-export-this-value' } }
                    if ($StandardInput -notmatch '-InvocationId ([0-9a-f]{32})') { throw 'Missing fresh invocation binding.' }
                    $envelope = & $newEvidence $Matches[1]
                    if ($StandardInput -notmatch ('fetch --quiet --depth=1 origin ' + ('a' * 40))) { throw 'Missing exact remote source fetch.' }
                    if ($state.fault -eq 'evidence') { $envelope.proof.source.head = '0' * 40 }
                    $capture = $Arguments[[array]::IndexOf($Arguments, '--capture-stdout') + 1]
                    $envelope | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $capture
                }
                'stop' {
                    if ($Arguments[3] -cne $state.leaseId -or $Arguments[4] -cne '--wait') { throw 'Cleanup must wait for the exact task-owned lease.' }
                    if ($state.fault -eq 'cleanup') { return @{ exitCode = 1; output = 'never-export-this-value' } }
                }
                default { throw 'Unexpected fixture command.' }
            }
            return @{ exitCode = 0; output = ($wire | ConvertTo-Json -Depth 12 -Compress) }
        }.GetNewClosure()
        $parameters = @{
            InvokeCrabbox = $invoke; SourceSha = ('a' * 40); Token = 'test-auth-token'
            CheckpointId = 'chk_0123456789abcdef'; InputsSha256 = ('b' * 64); Class = 'standard'; TtlMinutes = '60'
            StateDirectory = $directory; OutputDirectory = (Join-Path $directory 'evidence')
            RunKey = '123:1:source'; RunUrl = 'https://github.com/openclaw/openclaw-windows-node/actions/runs/123/attempts/1'
        }
        if ($fault -eq 'missing-config') { $parameters.Token = '' }
        if ($fault) { Assert-Throws { Invoke-AzureNativeGatewayProof @parameters } 'Azure native proof failed:' }
        else { Invoke-AzureNativeGatewayProof @parameters; $script:passed++ }
        $summaryText = Get-Content -LiteralPath (Join-Path $parameters.OutputDirectory 'cloud-summary.json') -Raw
        $summary = $summaryText | ConvertFrom-Json -AsHashtable
        $expectedStage = switch ($fault) {
            'missing-config' { 'configuration' }
            { $_ -in @('login', 'personal', 'wrong-org') } { 'authentication' }
            'checkpoint' { 'checkpoint' }
            { $_ -in @('fork', 'fork-timeout', 'fork-identity') } { 'acquisition' }
            'run' { 'native-lifecycle' }
            default { 'evidence' }
        }
        Assert-True ($summary.stage -ceq $expectedStage) "Fixture failed for the wrong reason: $fault"
        Assert-True (-not $state.credentialInArguments -and $summaryText -notmatch 'never-export|test-auth-token|chk_') 'Credentials and operator image IDs must stay out of arguments and evidence.'
        $allocated = $fault -notin @('missing-config', 'login', 'personal', 'wrong-org', 'checkpoint')
        Assert-True (@($state.calls | Where-Object { $_ -eq 'stop' }).Count -eq [int]$allocated) 'Every attempted acquisition must receive one cleanup attempt.'
        if ($allocated) {
            Assert-True $state.forkPersisted 'Lease intent must be persisted before acquisition, including a lost response.'
            Assert-True ($summary.leaseId -ceq $state.leaseId) 'Report the actual requested lease identity.'
            $state.fault = ''
            $before = $state.calls.Count
            Stop-AzureNativeGatewayLease -InvokeCrabbox $invoke -StateDirectory $directory -RunKey '123:1:source'
            Assert-True ($state.calls.Count -eq ($before + [int]($fault -eq 'cleanup'))) 'Confirmed cleanup is idempotent; failed cleanup can be retried.'
            Assert-Throws { Stop-AzureNativeGatewayLease -InvokeCrabbox $invoke -StateDirectory $directory -RunKey 'other-run' } 'unowned lease intent'
        }
        if (-not $fault) {
            Assert-True (($state.calls -join ',') -ceq 'login,whoami,checkpoint,checkpoint,run,stop') 'Successful orchestration must exercise authentication, inspection, fork, run and confirmed cleanup.'
            Assert-True ($summary.outcome -ceq 'passed' -and $summary.cleanup -ceq 'passed') 'Only real proof plus confirmed cleanup can pass.'
        } else {
            Assert-True ($summary.outcome -ceq 'failed') 'A failed stage must never yield a passing receipt.'
        }
    }
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}

$workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\.github\workflows\native-gateway-azure.yml') -Raw
Assert-True ($workflow -match '(?m)^  workflow_dispatch:\r?$' -and $workflow -notmatch '(?m)^  (push|pull_request|pull_request_target|workflow_run|schedule):') 'Cloud credentials must not be available to PR or automatic triggers.'
Assert-True ($workflow -match 'needs: admission' -and $workflow -match 'environment: native-gateway-azure') 'Credentialed job must wait for existing-environment admission.'
Assert-True ($workflow -match 'always\(\).*steps.controller.outputs.binary' -and $workflow -match '-Mode Cleanup') 'Cancellation cleanup must remain a separate always step.'
Assert-True ($workflow -notmatch 'AZURE_CLIENT|azure/login|self-hosted|--github-runner|--admin|--download-on-failure') 'Do not reuse signing auth, register runners, use admin fallback or rely on unsupported native downloads.'
Assert-True ($workflow -match 'persist-credentials: false' -and $workflow -match 'ref: \$\{\{ github.sha \}\}') 'Checkout must bind exact source and not persist GitHub credentials for sync.'
Assert-True ($workflow -notmatch 'native-gateway-azure/\*\*|guest-stdout|guest-stderr|config.yaml') 'Upload only projected proof, not private runtime state.'
Assert-True ([regex]::Matches($workflow, 'Confirm-AzureNativeWorkflowEnvironment').Count -eq 2) 'Recheck protection after approval, before using cloud credentials.'
$controller = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-AzureNativeGatewayProof.ps1') -Raw
Assert-True ($controller.Contains("[Environment]::SetEnvironmentVariable(`$name, `$null, 'Process')")) 'Remove private CI inputs from the subprocess environment before invoking Crabbox.'
Assert-True ($controller.Contains("`$env:CRABBOX_ENV_ALLOW = 'CI'")) 'Remote environment forwarding must stay explicitly limited to CI.'
Write-Host "Azure native Gateway proof contracts: $script:passed passed (fixtures only; no cloud or native runtime proof)."
exit 0
