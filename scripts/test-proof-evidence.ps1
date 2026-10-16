[CmdletBinding()]
param([string]$RepoRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
. (Join-Path $PSScriptRoot 'ProofEvidence.ps1')
$root = Join-Path $RepoRoot ('TestResults\proof-contract-' + [guid]::NewGuid().ToString('N'))
$sourceProbe = Join-Path $RepoRoot ('scripts\proof-source-probe-' + [guid]::NewGuid().ToString('N') + '.txt')
$artifactRoot = Join-Path $RepoRoot ('artifacts\proof-contract-' + [guid]::NewGuid().ToString('N'))
$unsupportedRoot = Join-Path $RepoRoot ('proof-output-' + [guid]::NewGuid().ToString('N'))
$lookalikeRoot = Join-Path $RepoRoot ('TestResults-lookalike-' + [guid]::NewGuid().ToString('N'))
$shell = (Get-Process -Id $PID).Path
$checks = 0
$runnerInvocations = 0
New-Item -ItemType Directory -Path $root | Out-Null

function Assert-Rejected([scriptblock]$Action, [string]$Reason) {
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Reason) { throw "Unexpected rejection (wanted $Reason): $($_.Exception.Message)" }
        $script:checks++
        return
    }
    throw "Expected rejection: $Reason"
}

function Write-Trx([string]$Path, [int]$Passed = 1, [int]$Skipped = 0, [int]$Failed = 0, [DateTime]$Time = [DateTime]::UtcNow) {
    $stamp = $Time.ToUniversalTime().ToString('o')
    $results = ''
    foreach ($outcome in @(@('Passed') * $Passed) + @(@('NotExecuted') * $Skipped) + @(@('Failed') * $Failed)) {
        $results += "<UnitTestResult executionId='$([guid]::NewGuid())' outcome='$outcome' />"
    }
    $total = $Passed + $Skipped + $Failed
    $executed = $Passed + $Failed
    $text = "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><Times start='$stamp' finish='$stamp'/><Results>$results</Results><ResultSummary outcome='Completed'><Counters total='$total' executed='$executed' passed='$Passed' failed='$Failed' notExecuted='$Skipped' error='0'/></ResultSummary></TestRun>"
    Set-Content -LiteralPath $Path -Value $text -Encoding UTF8
}

try {
    $invocation = New-TestInvocation -ResultsDirectory $root -TrxFileName 'winui-accessibility.trx'
    $report = $invocation.report
    $start = [DateTime]::Parse($invocation.startedUtc).ToUniversalTime()
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'missing'
    Set-Content $report '<not-xml'
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'Load|XML|end of file|unexpected'
    Set-Content $report '<TestRun/>'
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'namespace'
    Write-Trx $report 0
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'zero'
    Write-Trx $report 0 1
    Assert-Rejected { Read-TestResultContract $report $start 0 -AllowSkipped } 'zero'
    Write-Trx $report 1 1
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'Skips'
    $mixed = Read-TestResultContract $report $start 0 -AllowSkipped
    if ($mixed.outcome -ne 'passed-with-permitted-skips' -or $mixed.executed -ne 1 -or $mixed.skipped -ne 1) { throw 'Ordinary CI skip policy lost.' }
    $checks++
    (Get-Content $report -Raw).Replace("notExecuted='1'", "notExecuted='0'") | Set-Content $report
    $vstestMixed = Read-TestResultContract $report $start 0 -AllowSkipped
    if ($vstestMixed.executed -ne 1 -or $vstestMixed.skipped -ne 1) { throw 'Actual VSTest skipped-row format was rejected or lost skips.' }
    $checks++
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'Skips'
    (Get-Content $report -Raw).Replace("notExecuted='0'", "notExecuted='2'") | Set-Content $report
    Assert-Rejected { Read-TestResultContract $report $start 0 -AllowSkipped } 'counter'
    Write-Trx $report 1 0 1
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'failed tests'
    Write-Trx $report
    Assert-Rejected { Read-TestResultContract $report $start 7 } 'exit code 7'
    Write-Trx $report 1 0 0 ([DateTime]::UtcNow.AddDays(-1))
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'stale'
    Write-Trx $report
    (Get-Item $report).LastWriteTimeUtc = $start.AddMinutes(-1)
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'stale'
    Write-Trx $report
    foreach ($lagMilliseconds in @(10, 1000)) {
        (Get-Item $report).LastWriteTimeUtc = $start.AddMilliseconds(-$lagMilliseconds)
        $fresh = Read-TestResultContract $report $start 0
        if ($fresh.executed -ne 1 -or $fresh.outcome -ne 'passed') { throw 'Fresh report with filesystem clock granularity was rejected.' }
        $checks++
    }
    (Get-Item $report).LastWriteTimeUtc = $start.AddMilliseconds(-1001)
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'stale'
    Write-Trx $report 1 0 0 $start.AddSeconds(-2)
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'TRX start time'
    foreach ($replacement in @(
        @{ old = "total='1'"; new = "total='2'" },
        @{ old = "passed='1'"; new = "passed='-1'" },
        @{ old = "error='0'"; new = "error='1'" },
        @{ old = "executed='1'"; new = "executed='bogus'" }
    )) {
        Write-Trx $report
        (Get-Content $report -Raw).Replace($replacement.old, $replacement.new) | Set-Content $report
        Assert-Rejected { Read-TestResultContract $report $start 0 } 'counter'
    }
    Write-Trx $report
    $valid = Read-TestResultContract $report $start 0
    if ($valid.executed -ne 1 -or $valid.outcome -ne 'passed') { throw 'Fresh all-pass report failed.' }
    $checks++

    # Modeled grouped-leaf contract. Current Shared/Tray/CLI VSTest reports
    # were checked separately and contain flat results, not InnerResults.
    Write-Trx $report 2
    [xml]$grouped = Get-Content $report -Raw
    $parent = $grouped.CreateElement('UnitTestResult', $grouped.DocumentElement.NamespaceURI)
    $parent.SetAttribute('executionId', [guid]::NewGuid().ToString())
    $parent.SetAttribute('outcome', 'Passed')
    $inner = $grouped.CreateElement('InnerResults', $grouped.DocumentElement.NamespaceURI)
    $resultContainer = $grouped.DocumentElement.SelectSingleNode("*[local-name()='Results']")
    foreach ($leaf in @($resultContainer.ChildNodes)) { [void]$inner.AppendChild($leaf) }
    [void]$parent.AppendChild($inner)
    [void]$resultContainer.AppendChild($parent)
    $grouped.Save($report)
    $groupResult = Read-TestResultContract $report $start 0
    if ($groupResult.executed -ne 2 -or $groupResult.discovered -ne 2) { throw 'Grouped data-driven results were not counted as leaf cases.' }
    $checks++
    Write-Trx $report 2
    [xml]$duplicates = Get-Content $report -Raw
    foreach ($leaf in $duplicates.TestRun.Results.UnitTestResult) { $leaf.SetAttribute('executionId', 'duplicate') }
    $duplicates.Save($report)
    Assert-Rejected { Read-TestResultContract $report $start 0 } 'identities'
    Write-Trx $report

    $gatewayArguments = @{
        RepoRoot = $RepoRoot
        Project = 'tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj'
        Filter = 'FullyQualifiedName~OpenClaw.E2ETests.Setup.SetupAndConnectTests'
        ResultName = 'gateway-setup-connect'
        RuntimeIdentifier = 'win-x64'
    }
    $previousGate = [Environment]::GetEnvironmentVariable('OPENCLAW_RUN_E2E')
    $previousOllamaGate = [Environment]::GetEnvironmentVariable('OPENCLAW_RUN_OLLAMA_E2E')
    try {
        $env:OPENCLAW_RUN_E2E = $null
        $env:OPENCLAW_RUN_OLLAMA_E2E = $null
        Assert-Rejected { Get-ProofCommandBinding @gatewayArguments } 'requires OPENCLAW_RUN_E2E=1'
        $env:OPENCLAW_RUN_E2E = '1'
        Assert-Rejected { Get-ProofCommandBinding @gatewayArguments } 'requires OPENCLAW_RUN_OLLAMA_E2E=1'
        $env:OPENCLAW_RUN_OLLAMA_E2E = '1'
        $gatewayBinding = Get-ProofCommandBinding @gatewayArguments
        if ($gatewayBinding.environment.Count -ne 2 -or $gatewayBinding.environment.OPENCLAW_RUN_E2E -cne '1' -or
            $gatewayBinding.environment.OPENCLAW_RUN_OLLAMA_E2E -cne '1') { throw 'Gateway binding omitted a required selected-test gate.' }
        $checks++
        $gatewayInvocation = New-TestInvocation -ResultsDirectory $root -TrxFileName 'gateway-setup-connect.trx'
        Write-Trx $gatewayInvocation.report
        $gatewayResult = Read-TestResultContract $gatewayInvocation.report ([DateTime]$gatewayInvocation.startedUtc) 0
        Write-ProofEvidence $RepoRoot $gatewayBinding $gatewayInvocation (Get-ProofSourceIdentity $RepoRoot) 0 $gatewayResult ''
        $gatewaySummaryPath = Join-Path $gatewayInvocation.directory 'evidence-summary.json'
        $gatewaySummaryText = Get-Content $gatewaySummaryPath -Raw
        $env:OPENCLAW_RUN_E2E = $null
        $env:OPENCLAW_RUN_OLLAMA_E2E = $null
        $gateChecked = Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId)
        if (-not $gateChecked.validExecutionEvidence) { throw 'Recorded environment was incorrectly tied to the validating shell.' }
        $checks++
        $tamperedGate = $gatewaySummaryText | ConvertFrom-Json
        $tamperedGate.environment.OPENCLAW_RUN_E2E = '0'
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'environment gate'
        $tamperedGate = $gatewaySummaryText | ConvertFrom-Json
        $tamperedGate.environment.OPENCLAW_RUN_OLLAMA_E2E = '0'
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'OPENCLAW_RUN_OLLAMA_E2E=1 environment gate'
        $tamperedGate.environment.PSObject.Properties.Remove('OPENCLAW_RUN_OLLAMA_E2E')
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'environment observations'
        $tamperedGate = $gatewaySummaryText | ConvertFrom-Json
        $tamperedGate.environment | Add-Member -NotePropertyName 'PRIVATE_VALUE' -NotePropertyValue 'not-permitted'
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'undeclared environment'
        $tamperedGate.PSObject.Properties.Remove('environment')
        $tamperedGate | ConvertTo-Json -Depth 12 | Set-Content $gatewaySummaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($gatewaySummaryPath) @($gatewayBinding.poolId) } 'environment observations'
    } finally {
        $env:OPENCLAW_RUN_E2E = $previousGate
        $env:OPENCLAW_RUN_OLLAMA_E2E = $previousOllamaGate
    }

    $binding = Get-ProofCommandBinding $RepoRoot 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' 'Category=Accessibility' 'winui-accessibility' 'win-x64'
    if ($binding.poolId -ne 'windows-winui-interactive') { throw 'Inventory command binding failed.' }
    $source = Get-ProofSourceIdentity $RepoRoot
    $capture = Join-Path $root 'owned-window.png'
    [IO.File]::WriteAllBytes($capture, [byte[]]@(1, 2, 3))
    $receipt = Join-Path $root 'run.json'
    Set-Content $receipt '{"fixtureReceipt":"synthetic; digest reference only"}'
    $binary = Join-Path $root 'fixture.dll'
    [IO.File]::WriteAllBytes($binary, [byte[]]@(4, 5, 6))
    $artifacts = @((Get-ProofArtifact $capture $RepoRoot 'capture'), (Get-ProofArtifact $receipt $RepoRoot 'fixture-receipt'), (Get-ProofArtifact $binary $RepoRoot 'binary'))
    Write-ProofEvidence $RepoRoot $binding $invocation $source 0 $valid '' $artifacts
    $summaryPath = Join-Path $invocation.directory 'evidence-summary.json'
    $summaryText = Get-Content $summaryPath -Raw
    $checked = Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId)
    if (-not $checked.validExecutionEvidence -or $checked.completePoolProof) { throw 'Evidence must not claim a complete pool.' }
    $checks++
    Assert-Rejected { Assert-ProofResultsDirectory $unsupportedRoot $RepoRoot } 'under TestResults or artifacts'
    Assert-Rejected { Assert-ProofResultsDirectory $lookalikeRoot $RepoRoot } 'under TestResults or artifacts'
    Assert-Rejected { Assert-ProofResultsDirectory $RepoRoot $RepoRoot } 'under TestResults or artifacts'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @((Join-Path $unsupportedRoot 'evidence-summary.json')) @($binding.poolId) } 'under TestResults or artifacts'
    Assert-Rejected { Write-ProofEvidence $RepoRoot $binding @{ directory = $unsupportedRoot } $source 0 $valid '' } 'under TestResults or artifacts'
    $cli = (& (Join-Path $RepoRoot 'scripts\validate-proof-evidence.ps1') -RepoRoot $RepoRoot -SummaryPath $summaryPath -RequiredPoolId $binding.poolId) | ConvertFrom-Json
    if (-not $cli.validExecutionEvidence -or $cli.completePoolProof) { throw 'Read-only evidence CLI overclaims proof.' }
    $checks++
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @('windows-wsl-mxc') } 'Required pool'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) @('windows-winui-interactive/exercise-changed-ui') } 'Required command'
    Set-Content $capture 'tampered'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'digest mismatch'
    [IO.File]::WriteAllBytes($capture, [byte[]]@(1, 2, 3))
    Set-Content $binary 'tampered binary'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'digest mismatch'
    [IO.File]::WriteAllBytes($binary, [byte[]]@(4, 5, 6))
    Set-Content $sourceProbe 'new relevant source'
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'source identity is stale'
    Assert-Rejected { Write-ProofEvidence $RepoRoot $binding $invocation $source 0 $valid '' $artifacts } 'Source changed'
    Remove-Item $sourceProbe
    $blocked = Get-Content $summaryPath -Raw | ConvertFrom-Json
    if ($blocked.outcome -ne 'blocked' -or $blocked.blockedReason -ne 'source-changed-during-run') { throw 'Changed source emitted passing evidence.' }
    $checks++
    Set-Content $summaryPath $summaryText
    foreach ($case in @('head', 'pool', 'command', 'blocked', 'counts', 'assurance')) {
        $changed = $summaryText | ConvertFrom-Json
        switch ($case) {
            'head' { $changed.source.head = '0' * 40 }
            'pool' { $changed.poolId = 'missing-pool' }
            'command' { $changed.command = 'private arbitrary shell text' }
            'blocked' { $changed.outcome = 'blocked'; $changed.blockedReason = 'host-unavailable' }
            'counts' { $changed.results.passed = 50 }
            'assurance' { $changed.assurance.poolComplete = $true }
        }
        $changed | ConvertTo-Json -Depth 12 | Set-Content $summaryPath
        Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'stale|inventory|blocked|counters|overclaims'
    }
    Set-Content $summaryPath $summaryText
    (Get-Item $report).LastWriteTimeUtc = $start.AddMinutes(-1)
    Assert-Rejected { Test-ProofEvidence $RepoRoot @($summaryPath) @($binding.poolId) } 'stale'
    Write-Trx $report
    Assert-Rejected { Get-ProofArtifact (Join-Path $root 'settings.json') $RepoRoot 'fixture-receipt' } 'missing|allowlisted'
    if ($null -ne (Get-ProofCommandBinding $RepoRoot 'private' 'private' 'private' 'win-x64')) { throw 'Arbitrary command became inventory evidence.' }
    $checks++
    $noGate = Get-ProofRequiredEnvironment -Command 'runner' -ExecutedCommand 'runner'
    $oneGate = Get-ProofRequiredEnvironment -Command "`$env:OPENCLAW_RUN_E2E = '1'; runner" -ExecutedCommand 'runner'
    if ($noGate.Count -ne 0 -or $oneGate.Count -ne 1 -or $oneGate.OPENCLAW_RUN_E2E -cne '1') {
        throw 'Existing zero-gate or single-gate proof contract changed.'
    }
    $checks++
    Assert-Rejected { Get-ProofRequiredEnvironment -Command "`$env:PRIVATE_VALUE = '1'; runner" -ExecutedCommand 'runner' } 'inventory contract'

    # Child PowerShell supplies a controlled command instead of launching dotnet,
    # exercising the actual runner wiring without builds, apps, or machine changes.
    $harness = Join-Path $root 'runner-harness.ps1'
    $writer = ${function:Write-Trx}.ToString()
    Set-Content $harness @'
param([string]$Repo, [string]$Runner, [string]$Case, [string]$Results)
$ErrorActionPreference = 'Stop'
. (Join-Path $Repo 'scripts\ProofEvidence.ps1')
$sourceProbe = Join-Path $Repo ('scripts\runner-source-probe-' + [guid]::NewGuid().ToString('N') + '.txt')
function global:dotnet {
    if ($Case -in @('unsupported-output', 'lookalike-output', 'missing-e2e-gate', 'missing-ollama-gate')) {
        throw 'Unexpected test execution before prerequisite rejection.'
    }
    $values = @($args)
    $directory = $values[[Array]::IndexOf($values, '--results-directory') + 1]
    $logger = @($values | Where-Object { $_ -like 'trx;LogFileName=*' })[0]
    $report = Join-Path $directory $logger.Substring('trx;LogFileName='.Length)
    $global:LASTEXITCODE = 0
    if ($Case -eq 'missing') { return }
    if ($Case -eq 'malformed') { Set-Content $report '<bad'; return }
    $passed = if ($Case -in @('zero', 'all-skipped')) { 0 } else { 1 }
    $skipped = if ($Case -in @('mixed', 'all-skipped')) { 1 } else { 0 }
    $time = if ($Case -eq 'stale') { [DateTime]::UtcNow.AddDays(-1) } else { [DateTime]::UtcNow }
    Write-Trx $report $passed $skipped 0 $time
    if ($Case -eq 'nonzero') { $global:LASTEXITCODE = 9 }
    if ($Case -eq 'nonzero-source-change') {
        Set-Content -LiteralPath $sourceProbe 'synthetic changed source'
        $global:LASTEXITCODE = 9
    }
}
'@
    Add-Content $harness ("function Write-Trx {`n" + $writer + "`n}")
    Add-Content $harness @'
try {
    if ($Runner -eq 'gateway') {
        $env:OPENCLAW_RUN_E2E = if ($Case -eq 'missing-e2e-gate') { $null } else { '1' }
        $env:OPENCLAW_RUN_OLLAMA_E2E = if ($Case -eq 'missing-ollama-gate') { $null } else { '1' }
        & (Join-Path $Repo 'scripts\run-proof-tests.ps1') -Project 'tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj' -Filter 'FullyQualifiedName~OpenClaw.E2ETests.Setup.SetupAndConnectTests' -ResultName 'gateway-setup-connect' -RuntimeIdentifier win-x64 -ResultsDirectory $Results
    } elseif ($Runner -eq 'adhoc') {
        & (Join-Path $Repo 'scripts\run-proof-tests.ps1') -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'Category=AdHoc' -ResultName 'adhoc-proof' -RuntimeIdentifier win-x64 -ResultsDirectory $Results
        if (@(Get-ChildItem -LiteralPath $Results -Filter 'evidence-summary.json' -Recurse).Count) {
            throw 'Ad hoc command generated inventory evidence.'
        }
    } elseif ($Runner -eq 'proof') {
        & (Join-Path $Repo 'scripts\run-proof-tests.ps1') -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'Category=Accessibility' -ResultName 'winui-accessibility' -RuntimeIdentifier win-x64 -ResultsDirectory $Results
    } else {
        $env:OPENCLAW_CI_COLLECT_COVERAGE = 'false'
        & (Join-Path $Repo 'scripts\Invoke-CiTest.ps1') -Project 'unused.csproj' -ResultsDirectory $Results -TrxFileName 'ci.trx'
    }
    if ($Runner -in @('proof', 'gateway') -and $Case -in @('passed', 'artifact-output')) {
        $summary = @(Get-ChildItem -LiteralPath $Results -Filter 'evidence-summary.json' -Recurse | Sort-Object LastWriteTimeUtc)[-1].FullName
        $checked = Test-ProofEvidence $Repo @($summary) @()
        if (-not $checked.validExecutionEvidence) { throw 'Runner generated invalid evidence.' }
    }
} finally { if (Test-Path -LiteralPath $sourceProbe) { Remove-Item -LiteralPath $sourceProbe } }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'@
    foreach ($runner in @('proof', 'ci', 'gateway', 'adhoc')) {
        $runnerCases = @('passed', 'missing', 'malformed', 'zero', 'all-skipped', 'mixed', 'nonzero', 'stale')
        if ($runner -eq 'proof') { $runnerCases += @('nonzero-source-change', 'artifact-output', 'unsupported-output', 'lookalike-output') }
        if ($runner -eq 'gateway') { $runnerCases = @('passed', 'missing-e2e-gate', 'missing-ollama-gate') }
        if ($runner -eq 'adhoc') { $runnerCases = @('passed') }
        foreach ($case in $runnerCases) {
            $results = switch ($case) {
                'artifact-output' { $artifactRoot }
                'unsupported-output' { $unsupportedRoot }
                'lookalike-output' { $lookalikeRoot }
                default { $root }
            }
            if ($runner -eq 'adhoc') { $results = $unsupportedRoot }
            $savedPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $harness -Repo $RepoRoot -Runner $runner -Case $case -Results $results 2>&1
                $exit = $LASTEXITCODE
            } finally { $ErrorActionPreference = $savedPreference }
            $expectedPass = $case -in @('passed', 'artifact-output') -or ($case -eq 'mixed' -and $runner -eq 'ci')
            if (($exit -eq 0) -ne $expectedPass) { throw "$runner/$case unexpected exit ${exit}: $output" }
            $expectedRejection = switch ($case) {
                'unsupported-output' { 'under TestResults or artifacts' }
                'lookalike-output' { 'under TestResults or artifacts' }
                'missing-e2e-gate' { 'requires OPENCLAW_RUN_E2E=1' }
                'missing-ollama-gate' { 'requires OPENCLAW_RUN_OLLAMA_E2E=1' }
            }
            if ($expectedRejection -and (($output -join "`n") -notmatch $expectedRejection -or
                ($output -join "`n") -match 'Unexpected test execution')) { throw "Prerequisite was not rejected before execution: $output" }
            if ($case -in @('unsupported-output', 'lookalike-output') -and (Test-Path -LiteralPath $results)) {
                throw 'Unsupported output was created before rejection.'
            }
            if ($case -eq 'nonzero-source-change' -and
                (($output -join "`n") -notmatch 'Test command failed with exit code 9' -or
                 ($output -join "`n") -notmatch 'Secondary proof-evidence failure')) {
                throw "Secondary evidence failure replaced the primary command failure: $output"
            }
            $checks++
            $runnerInvocations++
        }
    }
    Write-Host "Proof result/evidence regressions passed: $checks checks (including $runnerInvocations controlled runner invocations)."
} finally {
    if (Test-Path $sourceProbe) { Remove-Item -LiteralPath $sourceProbe -Force }
    Remove-Item -LiteralPath $root -Recurse -Force
    if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
    if (Test-Path -LiteralPath $unsupportedRoot) { Remove-Item -LiteralPath $unsupportedRoot -Recurse -Force }
}
$global:LASTEXITCODE = 0
