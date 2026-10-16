. (Join-Path $PSScriptRoot 'TestResultContract.ps1')
. (Join-Path $PSScriptRoot 'MarkdownValidation.ps1')

function Get-ProofSourceIdentity([string]$RepoRoot) {
    $head = & git -C $RepoRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[a-f0-9]{40}$') { throw 'Cannot establish repository HEAD.' }
    $paths = @(& git -C $RepoRoot -c core.quotepath=false diff --name-only HEAD --)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot establish dirty source identity.' }
    $untracked = @(& git -C $RepoRoot -c core.quotepath=false ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot establish untracked source identity.' }
    $entries = @(
        foreach ($path in (@($paths) + @($untracked) | Sort-Object -Unique)) {
            # Generated evidence cannot make its own source identity stale.
            if ($path -match '^(TestResults|artifacts)/|(^|/)(bin|obj|node_modules)/') { continue }
            if ($path -match '(?:^|/)\.env(?:\.|$)|(?:^|/)(?:settings|gateways|credentials|secrets|mcp-token|device-key-ed25519)\.(?:json|txt)$|\.(?:pem|pfx|key)$') {
                # Private runtime material is not relevant source evidence.
                continue
            }
            $full = Assert-RepositoryPath -Path (Join-Path $RepoRoot $path) -Root $RepoRoot
            $hash = if (Test-Path -LiteralPath $full -PathType Leaf) {
                (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
            } else { 'deleted' }
            # Persist only a digest of this metadata, not source paths or contents.
            "$path`0$hash"
        }
    )
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($entries -join "`n")))).Replace('-', '') }
    finally { $sha.Dispose() }
    [pscustomobject][ordered]@{ head = [string]$head; dirtySha256 = $digest; dirtyFileCount = $entries.Count }
}

function Assert-ProofResultsDirectory {
    param([string]$Path, [string]$RepoRoot)
    $full = Assert-RepositoryPath -Path ([IO.Path]::GetFullPath($Path)) -Root $RepoRoot
    $relative = $full.Substring([IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').Length).TrimStart('\', '/')
    if ($relative -notmatch '^(TestResults|artifacts)([\\/]|$)') {
        throw 'Inventory-bound proof results must be stored under TestResults or artifacts. Use -ResultsDirectory .\TestResults\proof-output.'
    }
    return $full
}

function Get-ProofRequiredEnvironment {
    param([string]$Command, [string]$ExecutedCommand)
    $environment = [ordered]@{}
    if ($Command -ceq $ExecutedCommand) { return $environment }
    $prefix = "`$env:OPENCLAW_RUN_E2E = '1'; "
    if ($Command -ceq ($prefix + $ExecutedCommand)) {
        $environment.OPENCLAW_RUN_E2E = '1'
    } elseif ($Command -ceq ($prefix + "`$env:OPENCLAW_RUN_OLLAMA_E2E = '1'; " + $ExecutedCommand)) {
        $environment.OPENCLAW_RUN_E2E = '1'
        $environment.OPENCLAW_RUN_OLLAMA_E2E = '1'
    } else {
        throw 'Evidence command does not match its inventory contract.'
    }
    return $environment
}

function Get-ProofCommandBinding {
    param([string]$RepoRoot, [string]$Project, [string]$Filter, [string]$ResultName, [string]$RuntimeIdentifier)
    # Only inventory-owned commands may be recorded. Never persist arbitrary
    # filters, test payloads, environment dumps, or caller-supplied shell text.
    $inventory = Get-Content -LiteralPath (Join-Path $RepoRoot '.github\proof-pools.json') -Raw | ConvertFrom-Json
    $invoked = ".\scripts\run-proof-tests.ps1 -Project '$($Project.Replace('/', '\'))' -Filter '$Filter' -ResultName '$ResultName'"
    if ($RuntimeIdentifier) { $invoked += " -RuntimeIdentifier $RuntimeIdentifier" }
    $bindings = @(
        foreach ($pool in $inventory.pools) {
            foreach ($command in $pool.authoritativeCommands) {
                if ($command.kind -ne 'proof-test') { continue }
                if ($command.command -ceq $invoked -or
                    $command.command -ceq ("`$env:OPENCLAW_RUN_E2E = '1'; " + $invoked) -or
                    $command.command -ceq ("`$env:OPENCLAW_RUN_E2E = '1'; `$env:OPENCLAW_RUN_OLLAMA_E2E = '1'; " + $invoked)) {
                    $environment = Get-ProofRequiredEnvironment -Command $command.command -ExecutedCommand $invoked
                    foreach ($name in $environment.Keys) {
                        if ([Environment]::GetEnvironmentVariable($name) -cne $environment[$name]) {
                            throw "Inventory proof requires $name=1 before execution."
                        }
                    }
                    [pscustomobject]@{ poolId = $pool.id; commandId = $command.id; command = $command.command; executedCommand = $invoked; environment = $environment }
                }
            }
        }
    )
    if ($bindings.Count -gt 1) { throw 'Ambiguous proof command in inventory.' }
    if ($bindings.Count -eq 1) { return $bindings[0] }
}

function Get-ProofArtifact {
    param([string]$Path, [string]$RepoRoot, [string]$Kind)
    $full = Assert-RepositoryPath -Path ([IO.Path]::GetFullPath($Path)) -Root $RepoRoot
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw 'Evidence artifact is missing.' }
    $allowed = switch ($Kind) {
        'test-report' { [IO.Path]::GetExtension($full) -eq '.trx' }
        'capture' { [IO.Path]::GetExtension($full) -eq '.png' }
        'binary' { [IO.Path]::GetExtension($full) -in @('.exe', '.dll') }
        'native-receipt' { [IO.Path]::GetFileName($full) -in @('manifest.json', 'full-source-manifest.json', 'build-manifest.json') -or [IO.Path]::GetFileName($full) -match '^source-[a-f0-9-]+\.json$' }
        'fixture-receipt' { [IO.Path]::GetFileName($full) -eq 'run.json' }
        default { $false }
    }
    if (-not $allowed) { throw 'Artifact kind/path is not allowlisted. Do not attach credentials, arbitrary JSON, or logs.' }
    [pscustomobject][ordered]@{
        kind = $Kind
        path = $full.Substring([IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/').Length + 1).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
    }
}

function Write-ProofEvidence {
    param(
        [string]$RepoRoot, $Binding, $Invocation, $Source, [int]$ExitCode,
        $Result, [string]$BlockedReason, [object[]]$Artifacts = @()
    )
    [void](Assert-ProofResultsDirectory -Path $Invocation.directory -RepoRoot $RepoRoot)
    $current = Get-ProofSourceIdentity $RepoRoot
    if (($current | ConvertTo-Json -Compress) -cne ($Source | ConvertTo-Json -Compress)) {
        $BlockedReason = 'source-changed-during-run'
    }
    $references = @($Artifacts)
    if (Test-Path -LiteralPath $Invocation.report -PathType Leaf) {
        $references += Get-ProofArtifact -Path $Invocation.report -RepoRoot $RepoRoot -Kind 'test-report'
    }
    $summary = [ordered]@{
        schemaVersion = 1
        poolId = $Binding.poolId
        commandId = $Binding.commandId
        command = $Binding.command
        executedCommand = $Binding.executedCommand
        environment = $Binding.environment
        invocationId = $Invocation.id
        startedUtc = $Invocation.startedUtc
        finishedUtc = [DateTime]::UtcNow.ToString('o')
        exitCode = $ExitCode
        results = $Result
        outcome = if ($BlockedReason) { 'blocked' } else { 'passed' }
        blockedReason = $BlockedReason
        source = $Source
        artifacts = $references
        assurance = [ordered]@{
            scope = 'selected-test-execution-only'
            source = 'head-and-dirty-tree-identity'
            binary = 'not-verified-by-summary'
            receipts = 'digest-references-only; existing native freeze and fixture validators remain authoritative'
            poolComplete = $false
        }
    }
    $path = Join-Path $Invocation.directory 'evidence-summary.json'
    $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host "Local proof evidence: $path"
    if ($BlockedReason -eq 'source-changed-during-run') { throw 'Source changed during proof. Evidence is stale.' }
}

function Test-ProofEvidence {
    param([string]$RepoRoot, [string[]]$SummaryPath, [string[]]$RequiredPoolId, [string[]]$RequiredCommandId = @())
    $inventory = Get-Content -LiteralPath (Join-Path $RepoRoot '.github\proof-pools.json') -Raw | ConvertFrom-Json
    $source = Get-ProofSourceIdentity $RepoRoot
    $seenPools = @()
    $seenCommands = @()
    foreach ($path in $SummaryPath) {
        $summaryFile = Assert-RepositoryPath -Path ([IO.Path]::GetFullPath($path)) -Root $RepoRoot
        [void](Assert-ProofResultsDirectory -Path (Split-Path $summaryFile -Parent) -RepoRoot $RepoRoot)
        if ([IO.Path]::GetFileName($summaryFile) -cne 'evidence-summary.json') { throw 'Expected an evidence-summary.json, not arbitrary JSON.' }
        $summary = Get-Content -LiteralPath $summaryFile -Raw | ConvertFrom-Json
        if ($summary.schemaVersion -ne 1 -or $summary.outcome -ne 'passed' -or $summary.blockedReason -or $summary.exitCode -ne 0) {
            throw 'Evidence is unsupported, blocked, or failed.'
        }
        if ($summary.invocationId -cnotmatch '^[a-f0-9]{32}$' -or
            (Split-Path (Split-Path $summaryFile -Parent) -Leaf) -cne $summary.invocationId) { throw 'Evidence invocation identity does not match its directory.' }
        $pool = @($inventory.pools | Where-Object { $_.id -ceq $summary.poolId })
        if ($pool.Count -ne 1) { throw 'Evidence pool is not declared in inventory.' }
        $command = @($pool[0].authoritativeCommands | Where-Object { $_.id -ceq $summary.commandId -and $_.kind -ceq 'proof-test' })
        if ($command.Count -ne 1 -or $command[0].command -cne $summary.command) {
            throw 'Evidence command does not match its inventory contract.'
        }
        $requiredEnvironment = Get-ProofRequiredEnvironment -Command $summary.command -ExecutedCommand $summary.executedCommand
        if ($null -eq $summary.PSObject.Properties['environment'] -or $summary.environment -isnot [pscustomobject]) {
            throw 'Evidence is missing its declared environment observations.'
        }
        $environmentNames = @($summary.environment.PSObject.Properties | ForEach-Object { $_.Name })
        if ($environmentNames.Count -ne $requiredEnvironment.Count -or
            @($environmentNames | Where-Object { $_ -cnotin $requiredEnvironment.Keys }).Count) {
            throw 'Evidence contains missing or undeclared environment observations.'
        }
        foreach ($name in $requiredEnvironment.Keys) {
            if ($summary.environment.$name -cne $requiredEnvironment[$name]) {
                throw "Evidence did not observe the required $name=1 environment gate."
            }
        }
        if ($summary.source.head -cne $source.head -or $summary.source.dirtySha256 -cne $source.dirtySha256 -or
            $summary.source.dirtyFileCount -ne $source.dirtyFileCount) { throw 'Evidence source identity is stale.' }
        if ($summary.assurance.poolComplete -ne $false -or $summary.assurance.binary -cne 'not-verified-by-summary' -or
            $summary.assurance.scope -cne 'selected-test-execution-only') { throw 'Summary overclaims pool or binary assurance.' }
        # Newer PowerShell JSON readers materialize ISO timestamps as DateTime.
        # A string round-trip would drop its UTC kind and use the local offset.
        $start = [DateTimeOffset]$summary.startedUtc
        $finish = [DateTimeOffset]$summary.finishedUtc
        if ($finish -lt $start -or $finish.UtcDateTime -gt [DateTime]::UtcNow.AddSeconds(1)) { throw 'Invalid evidence invocation times.' }
        $reports = @($summary.artifacts | Where-Object { $_.kind -ceq 'test-report' })
        if ($reports.Count -ne 1) { throw 'Evidence requires exactly one expected test report.' }
        if ($summary.executedCommand -notmatch "-ResultName '(?<name>[a-z0-9-]+)'" -or
            [IO.Path]::GetFileName($reports[0].path) -cne "$($Matches['name']).trx") {
            throw 'Evidence does not reference its expected command report.'
        }
        foreach ($artifact in $summary.artifacts) {
            $full = Join-Path $RepoRoot $artifact.path
            $actual = Get-ProofArtifact -Path $full -RepoRoot $RepoRoot -Kind $artifact.kind
            if ($actual.sha256 -cne $artifact.sha256) { throw 'Evidence artifact digest mismatch.' }
            if ($artifact.kind -eq 'test-report') {
                [void](Assert-ProofResultsDirectory -Path (Split-Path $full -Parent) -RepoRoot $RepoRoot)
                if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($full)) -ine (Split-Path $summaryFile -Parent)) {
                    throw 'TRX is not from the evidence invocation.'
                }
            }
        }
        $result = Read-TestResultContract -Path (Join-Path $RepoRoot $reports[0].path) -StartedUtc $start.UtcDateTime -ExitCode $summary.exitCode
        foreach ($property in @('discovered', 'executed', 'passed', 'failed', 'skipped', 'outcome')) {
            if ($summary.results.$property -cne $result.$property) { throw 'Evidence counters disagree with TRX.' }
        }
        $seenPools += $summary.poolId
        $seenCommands += "$($summary.poolId)/$($summary.commandId)"
    }
    foreach ($id in $RequiredPoolId) { if ($id -cnotin $seenPools) { throw "Required pool evidence is missing: $id" } }
    foreach ($id in $RequiredCommandId) { if ($id -cnotin $seenCommands) { throw "Required command evidence is missing: $id" } }
    [pscustomobject]@{
        schemaVersion = 1
        validExecutionEvidence = $true
        pools = @($seenPools | Select-Object -Unique)
        completePoolProof = $false
        boundary = 'Local correspondence check, not a signed attestation, source-to-binary provenance, visual acceptance, or host-capability proof. Existing native/fixture receipts require their owning verifier.'
    }
}
