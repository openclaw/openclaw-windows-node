#Requires -Version 7.4
Set-StrictMode -Version Latest
$script:BrokerControlTimeoutSeconds = 120
$script:LeaseAcquireTimeoutSeconds = 1200
$script:NativeLifecycleTimeoutSeconds = 1200
$script:LeaseCleanupTimeoutSeconds = 600

function Assert-AzureNativeWorkflowAdmission {
    param([string]$Repository, [string]$Ref, [string]$EventName, $Environment, $BranchPolicies)
    if ($Repository -cne 'openclaw/openclaw-windows-node' -or
        $Ref -cne 'refs/heads/main' -or $EventName -cne 'workflow_dispatch') {
        throw 'Azure native proof requires a manual dispatch from canonical main.'
    }
    $review = @($Environment.protection_rules | Where-Object { $_.type -ceq 'required_reviewers' })
    $policies = @($BranchPolicies.branch_policies)
    if ($Environment.name -cne 'native-gateway-azure' -or
        $Environment.can_admins_bypass -isnot [bool] -or
        $Environment.can_admins_bypass -cne $false -or
        $Environment.deployment_branch_policy.custom_branch_policies -isnot [bool] -or
        $Environment.deployment_branch_policy.custom_branch_policies -cne $true -or
        $review.Count -ne 1 -or $review[0].prevent_self_review -isnot [bool] -or $review[0].prevent_self_review -cne $true -or
        @($review[0].reviewers | Where-Object { $_.type -cin @('User', 'Team') -and $_.reviewer.id -gt 0 }).Count -eq 0 -or
        $BranchPolicies.total_count -ne 1 -or $policies.Count -ne 1 -or
        $policies[0].name -cne 'main' -or $policies[0].type -cne 'branch') {
        throw 'Azure native proof requires an existing protected environment: reviewers, no self-review/admin bypass, and main only.'
    }
}

function Assert-AzureNativeCheckpoint {
    param($Audit, [string]$CheckpointId)
    $record = $Audit.record
    if ($Audit.providerState -notin @('available', 'ready', 'succeeded', 'completed') -or
        $Audit.nextAction -cne 'fork_or_delete' -or $Audit['error'] -or
        $record.id -cne $CheckpointId -or $record.provider -cne 'azure' -or
        $record.targetOS -cne 'windows' -or $record.windowsMode -cne 'normal' -or
        $record.kind -cne 'azure-os-disk-snapshot' -or
        $record.ownership.mode -cne 'coordinator' -or
        $record.ownership.origin -cne 'https://crabbox.openclaw.ai' -or
        $record.native.provider -cne 'azure' -or
        $record.native.architecture -cne 'amd64' -or
        [string]::IsNullOrWhiteSpace($record.native.imageId) -or $record.native['detachedReason']) {
        throw 'Approved checkpoint is not an available coordinator-managed native Azure x64 OS-disk snapshot.'
    }
}

function Confirm-AzureNativeWorkflowEnvironment {
    $environment = & gh api repos/openclaw/openclaw-windows-node/environments/native-gateway-azure | ConvertFrom-Json -AsHashtable
    if ($LASTEXITCODE -ne 0) { throw 'Protected Azure proof environment is unavailable.' }
    $branches = & gh api repos/openclaw/openclaw-windows-node/environments/native-gateway-azure/deployment-branch-policies | ConvertFrom-Json -AsHashtable
    if ($LASTEXITCODE -ne 0) { throw 'Azure proof branch policy is unavailable.' }
    Assert-AzureNativeWorkflowAdmission -Repository $env:GITHUB_REPOSITORY -Ref $env:GITHUB_REF `
        -EventName $env:GITHUB_EVENT_NAME -Environment $environment -BranchPolicies $branches
}

function ConvertFrom-AzureNativeResult($Result) {
    if ($Result.exitCode -ne 0) { throw 'Crabbox command failed.' }
    try { $value = ConvertFrom-Json -InputObject $Result.output -AsHashtable -ErrorAction Stop }
    catch { throw 'Crabbox returned invalid JSON.' }
    if ($value -isnot [Collections.IDictionary]) { throw 'Crabbox returned a non-object response.' }
    return $value
}

function ConvertTo-AzureNativeEvidence {
    param([string]$Json, [string]$SourceSha, [string]$InputsSha256, [string]$InvocationId)
    try { $envelope = ConvertFrom-Json -InputObject $Json -AsHashtable -ErrorAction Stop }
    catch { throw 'Native proof evidence is not valid JSON.' }
    $proof = $envelope.proof
    $operations = @('setup', 'companion prepare', 'start/status', 'restart/status', 'stop/status', 'teardown/status')
    if ($envelope.invocationId -cne $InvocationId -or $envelope.inputsSha256 -cne $InputsSha256 -or
        ($proof.schemaVersion -isnot [int] -and $proof.schemaVersion -isnot [long]) -or
        $proof.schemaVersion -ne 1 -or $proof.scope -cne 'native-gateway-package-lifecycle' -or
        $proof.source.head -cne $SourceSha -or
        ($proof.source.dirtyFileCount -isnot [int] -and $proof.source.dirtyFileCount -isnot [long]) -or
        $proof.source.dirtyFileCount -ne 0 -or
        $proof.outcome -cne 'passed' -or $proof.sessionCleanup -cne 'passed' -or $proof.packageCleanup -cne 'passed' -or
        (@($proof.operations) -join '|') -cne ($operations -join '|') -or
        $proof.packagingSha -cnotmatch '^[0-9a-f]{40}$' -or
        $proof.msixSha256 -cnotmatch '^[0-9a-f]{64}$' -or $proof.wxcExecSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $proof.build.packagingSha -cne $proof.packagingSha -or $proof.build.payloadSha -cnotmatch '^[0-9a-f]{40}$' -or
        $proof.build.packageVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' -or
        $proof.build.payloadVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$') {
        throw 'Native proof evidence is incomplete or does not match this invocation and source.'
    }
    # Project only bounded proof fields. Never upload arbitrary guest JSON or transport logs.
    return [ordered]@{
        schemaVersion = 1; scope = 'native-gateway-package-lifecycle'; outcome = 'passed'
        invocationId = $InvocationId; sourceSha = $SourceSha; inputsSha256 = $InputsSha256
        packagingSha = $proof.packagingSha; payloadSha = $proof.build.payloadSha
        packageVersion = $proof.build.packageVersion; payloadVersion = $proof.build.payloadVersion
        msixSha256 = $proof.msixSha256; wxcExecSha256 = $proof.wxcExecSha256
        operations = $operations; sessionCleanup = 'passed'; packageCleanup = 'passed'
    }
}

function Stop-AzureNativeGatewayLease {
    param([scriptblock]$InvokeCrabbox, [string]$StateDirectory, [string]$RunKey)
    $path = Join-Path $StateDirectory 'lease.json'
    if (-not (Test-Path -LiteralPath $path)) { return }
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    if ($state.runKey -cne $RunKey -or $state.leaseId -cnotmatch '^cbx_[0-9a-f]{12}$' -or
        $state.creationRequested -cne $true) { throw 'Refusing cleanup of an unowned lease intent.' }
    if ($state.cleanup -ceq 'passed') { return }
    $result = & $InvokeCrabbox @('stop', '--provider', 'azure', $state.leaseId, '--wait') '' $script:LeaseCleanupTimeoutSeconds
    if ($result.exitCode -ne 0) { throw 'Azure lease cleanup is not confirmed. Coordinator TTL remains the backstop.' }
    $state.cleanup = 'passed'
    $state | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
}

function Invoke-AzureNativeGatewayProof {
    param(
        [scriptblock]$InvokeCrabbox, [string]$SourceSha, [string]$Token,
        [string]$CheckpointId, [string]$InputsSha256, [string]$Class, [string]$TtlMinutes,
        [string]$StateDirectory, [string]$OutputDirectory, [string]$RunKey, [string]$RunUrl
    )
    $null = New-Item -ItemType Directory -Path $StateDirectory -Force
    $null = New-Item -ItemType Directory -Path $OutputDirectory -Force
    if (Test-Path -LiteralPath (Join-Path $StateDirectory 'lease.json')) {
        throw 'A lease intent already exists. Clean it up rather than allocating a replacement.'
    }
    $summary = [ordered]@{
        schemaVersion = 1; provider = 'azure'; sourceSha = $SourceSha; runUrl = $RunUrl
        leaseId = $null; stage = 'configuration'; outcome = 'failed'; cleanup = 'not-required'
    }
    try {
        if ($SourceSha -cnotmatch '^[0-9a-f]{40}$' -or [string]::IsNullOrWhiteSpace($Token) -or
            $CheckpointId -cnotmatch '^chk_[0-9a-f]+$' -or $InputsSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $Class -cnotmatch '^[a-z][a-z0-9-]{0,30}$' -or
            $TtlMinutes -cnotmatch '^[0-9]{2,3}$' -or [int]$TtlMinutes -lt 60 -or [int]$TtlMinutes -gt 120) {
            throw 'Missing or invalid approved cloud configuration.'
        }
        $summary.stage = 'authentication'
        $login = & $InvokeCrabbox @('login', '--url', 'https://crabbox.openclaw.ai', '--provider', 'azure', '--token-stdin') "$Token`n" $script:BrokerControlTimeoutSeconds
        $Token = $null
        if ($login.exitCode -ne 0) { throw 'Broker login failed.' }
        $identity = ConvertFrom-AzureNativeResult (& $InvokeCrabbox @('whoami', '--json') '' $script:BrokerControlTimeoutSeconds)
        if ($identity.org -cne 'openclaw' -or $identity.auth -cne 'bearer') {
            throw 'CI requires an OpenClaw automation bearer credential, not a personal GitHub login.'
        }
        $summary.stage = 'checkpoint'
        $audit = ConvertFrom-AzureNativeResult (& $InvokeCrabbox @('checkpoint', 'inspect', $CheckpointId, '--verify', '--json') '' $script:BrokerControlTimeoutSeconds)
        Assert-AzureNativeCheckpoint $audit $CheckpointId
        $summary.stage = 'acquisition'
        $leaseId = 'cbx_' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(6)).ToLowerInvariant()
        $summary.leaseId = $leaseId
        $summary.cleanup = 'not-confirmed'
        # Persist before allocation. Fixed coordinator intent prevents lost-response retries creating another VM.
        @{
            runKey = $RunKey; leaseId = $leaseId; creationRequested = $true; cleanup = 'not-confirmed'
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $StateDirectory 'lease.json') -Encoding utf8
        $fork = ConvertFrom-AzureNativeResult (& $InvokeCrabbox @(
            'checkpoint', 'fork', $CheckpointId, '--provider', 'azure', '--target', 'windows',
            '--windows-mode', 'normal', '--arch', 'amd64', '--class', $Class,
            '--lease-id', $leaseId, '--ttl', "${TtlMinutes}m", '--idle-timeout', "${TtlMinutes}m", '--json'
        ) '' $script:LeaseAcquireTimeoutSeconds)
        if ($fork.leaseId -cne $leaseId -or $fork.provider -cne 'azure' -or $fork.checkpointId -cne $CheckpointId) {
            throw 'Acquired lease did not match the persisted intent.'
        }
        $summary.stage = 'native-lifecycle'
        $invocationId = [guid]::NewGuid().ToString('N')
        $capture = Join-Path $StateDirectory 'guest-stdout.json'
        # A separate immutable public checkout avoids relying on archive sync to carry Git metadata.
        $script = @'
$ErrorActionPreference = 'Stop'
$checkout = Join-Path $env:TEMP 'openclaw-native-__INVOCATION__'
if (Test-Path -LiteralPath $checkout) { throw 'Proof checkout already exists.' }
git init --quiet $checkout
if ($LASTEXITCODE -ne 0) { throw 'Proof checkout initialization failed.' }
git -C $checkout remote add origin https://github.com/openclaw/openclaw-windows-node.git
if ($LASTEXITCODE -ne 0) { throw 'Proof checkout origin failed.' }
git -C $checkout fetch --quiet --depth=1 origin __SOURCE__
if ($LASTEXITCODE -ne 0) { throw 'Exact proof source fetch failed.' }
git -C $checkout checkout --quiet --detach FETCH_HEAD
if ($LASTEXITCODE -ne 0) { throw 'Exact proof checkout failed.' }
& pwsh.exe -NoLogo -NoProfile -File (Join-Path $checkout 'scripts\Invoke-NativeGatewayAzureGuest.ps1') -SourceSha __SOURCE__ -InputsSha256 __INPUTS__ -InvocationId __INVOCATION__
exit $LASTEXITCODE
'@
        $script = $script.Replace('__SOURCE__', $SourceSha).Replace('__INPUTS__', $InputsSha256).Replace('__INVOCATION__', $invocationId)
        $run = & $InvokeCrabbox @(
            'run', '--provider', 'azure', '--target', 'windows', '--windows-mode', 'normal',
            '--id', $leaseId, '--no-hydrate', '--capture-stdout', $capture,
            '--capture-stderr', (Join-Path $StateDirectory 'guest-stderr.txt'), '--script-stdin', '--'
        ) $script $script:NativeLifecycleTimeoutSeconds
        if ($run.exitCode -ne 0) { throw 'Real native Gateway lifecycle failed.' }
        $summary.stage = 'evidence'
        $evidence = ConvertTo-AzureNativeEvidence -Json (Get-Content -LiteralPath $capture -Raw) `
            -SourceSha $SourceSha -InputsSha256 $InputsSha256 -InvocationId $invocationId
        $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'native-gateway-proof.json') -Encoding utf8
        $summary.outcome = 'passed'
    } catch {
        # Authentication, provider responses and guest exceptions are not safe log material.
        Write-Warning "Azure native proof failed at stage: $($summary.stage). Raw output remains private."
    } finally {
        try {
            Stop-AzureNativeGatewayLease -InvokeCrabbox $InvokeCrabbox -StateDirectory $StateDirectory -RunKey $RunKey
            if ($summary.leaseId) { $summary.cleanup = 'passed' }
        } catch {
            $summary.outcome = 'failed'
            $summary.cleanup = 'not-confirmed'
            Write-Warning 'Task-owned Azure lease cleanup is not confirmed. Escalate using the lease ID and coordinator TTL.'
        }
        $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'cloud-summary.json') -Encoding utf8
    }
    if ($summary.outcome -cne 'passed') { throw "Azure native proof failed: $($summary.stage); cleanup: $($summary.cleanup)." }
}

Export-ModuleMember -Function Assert-AzureNativeWorkflowAdmission, Confirm-AzureNativeWorkflowEnvironment, Assert-AzureNativeCheckpoint, ConvertTo-AzureNativeEvidence, Stop-AzureNativeGatewayLease, Invoke-AzureNativeGatewayProof
