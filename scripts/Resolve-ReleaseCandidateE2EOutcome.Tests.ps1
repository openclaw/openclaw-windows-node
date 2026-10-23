[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Resolve-ReleaseCandidateE2EOutcome.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "candidate-outcome-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$trxPath = Join-Path $root 'candidate.trx'
$receiptPath = Join-Path $root 'protocol.json'
$script:checks = 0
$exception = 'OpenClaw.E2ETests.Setup.E2EProtocolMismatchException : OPENCLAW_E2E_PROTOCOL_MISMATCH: fixture_initialization'

function Reset-Fixture([string]$Outcome = 'Failed', [string]$Message = $exception) {
    $passed = [int]($Outcome -ceq 'Passed')
    $failed = [int]($Outcome -ceq 'Failed')
    $escapedMessage = [Security.SecurityElement]::Escape($Message)
    $script:trx = [xml]@"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testName="OpenClaw.E2ETests.Setup.SetupAndConnectTests.FullSetup_SafeNodeInvocation_RoutesThroughRealGateway" outcome="$Outcome">
    <Output><ErrorInfo><Message>$escapedMessage</Message></ErrorInfo></Output>
  </UnitTestResult></Results>
  <ResultSummary outcome="$Outcome"><Counters total="1" executed="1" passed="$passed" failed="$failed" notExecuted="0" error="0" timeout="0" aborted="0" /></ResultSummary>
</TestRun>
"@
    if ($Outcome -ceq 'Failed') {
        $script:trx.TestRun.ResultSummary.InnerXml += '<RunInfos xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><RunInfo outcome="Error"><Text>[xUnit.net 00:00:00.08]     OpenClaw.E2ETests.Setup.SetupAndConnectTests.FullSetup_SafeNodeInvocation_RoutesThroughRealGateway [FAIL]</Text></RunInfo></RunInfos>'
    }
    $script:receipt = @{
        schema_version = 1
        outcome = 'protocol_mismatch'
        phase = 'fixture_initialization'
        source = 'app.status.nodeError'
        code = 'PROTOCOL_MISMATCH'
    }
}
function Invoke-Outcome([int]$ExitCode = 1, [bool]$Allow = $true) {
    $script:trx.Save($trxPath)
    $script:receipt | ConvertTo-Json -Compress | Set-Content -LiteralPath $receiptPath
    & $scriptPath -TestExitCode $ExitCode -TrxPath $trxPath -ProtocolResultPath $receiptPath -AllowProtocolMismatch $Allow
}
function Assert-Outcome([string]$Expected, [int]$ExitCode = 1, [bool]$Allow = $true) {
    $output = @(Invoke-Outcome -ExitCode $ExitCode -Allow $Allow)
    if ($output.Count -ne 1 -or $output[0] -cne $Expected) { throw "Expected only $Expected; received $output" }
    $script:checks++
}
function Assert-Rejected([string]$Pattern, [int]$ExitCode = 1, [bool]$Allow = $true) {
    try { Invoke-Outcome -ExitCode $ExitCode -Allow $Allow } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $script:checks++
        return
    }
    throw "Expected rejection: $Pattern"
}
try {
    Reset-Fixture -Outcome 'Passed' -Message ''
    Assert-Outcome -Expected 'passed' -ExitCode 0 -Allow $false
    Reset-Fixture
    Assert-Outcome -Expected 'protocol_mismatch'
    $script:receipt.source = 'openclaw-tray.log.node_rx_error_code'
    Assert-Outcome -Expected 'protocol_mismatch'
    $script:receipt.source = 'openclaw-tray.log.operator_handshake_error_code'
    Assert-Outcome -Expected 'protocol_mismatch'
    Assert-Rejected -Pattern 'without an eligible' -Allow $false
    Assert-Rejected -Pattern 'successful exit requires' -ExitCode 0
    Reset-Fixture -Outcome 'Passed' -Message ''
    Assert-Rejected -Pattern 'without an eligible'

    # A receipt cannot excuse an unrelated failure, even when it names the marker.
    foreach ($message in @(
        'Xunit.Sdk.TrueException : unrelated node invocation failed',
        'Xunit.Sdk.TrueException : OPENCLAW_E2E_PROTOCOL_MISMATCH: fixture_initialization',
        "System.AggregateException : One or more errors occurred. (OPENCLAW_E2E_PROTOCOL_MISMATCH: fixture_initialization) (unrelated fixture failure)`n---- ${exception}`n---- System.InvalidOperationException : unrelated fixture failure"
    )) {
        Reset-Fixture -Message $message
        Assert-Rejected -Pattern 'other than the sole expected'
    }
    foreach ($name in @('total', 'executed', 'notExecuted', 'error', 'aborted')) {
        Reset-Fixture
        $script:trx.TestRun.ResultSummary.Counters.SetAttribute($name, '2')
        Assert-Rejected -Pattern 'counter'
    }
    Reset-Fixture -Outcome 'NotExecuted' -Message ''
    Assert-Rejected -Pattern 'skips are not execution proof'
    Reset-Fixture
    $script:trx.TestRun.Results.UnitTestResult.SetAttribute('testName', 'Some.Other.Test')
    Assert-Rejected -Pattern 'expected setup, connect'
    Reset-Fixture
    $null = $script:trx.TestRun.Results.AppendChild($script:trx.TestRun.Results.UnitTestResult.CloneNode($true))
    Assert-Rejected -Pattern 'exactly one selected'
    Reset-Fixture
    $script:trx.TestRun.ResultSummary.InnerXml += '<RunInfos xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><RunInfo outcome="Error"><Text>test host crashed</Text></RunInfo></RunInfos>'
    Assert-Rejected -Pattern 'run-level error'
    foreach ($field in @('schema_version', 'outcome', 'phase', 'source', 'code')) {
        Reset-Fixture
        $script:receipt[$field] = 'invalid'
        Assert-Rejected -Pattern 'invalid protocol-mismatch result'
    }
    Reset-Fixture
    $script:receipt.outcome = @('protocol_mismatch')
    Assert-Rejected -Pattern 'invalid protocol-mismatch result'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
Write-Host "Candidate E2E outcome classification: $script:checks scenarios passed."
