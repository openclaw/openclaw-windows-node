[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$TestExitCode,
    [Parameter(Mandatory)][string]$TrxPath,
    [Parameter(Mandatory)][string]$ProtocolResultPath,
    [bool]$AllowProtocolMismatch = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$settings = [Xml.XmlReaderSettings]::new()
$settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$settings.XmlResolver = $null
$reader = [Xml.XmlReader]::Create([IO.Path]::GetFullPath($TrxPath), $settings)
try {
    $trx = [Xml.XmlDocument]::new()
    $trx.XmlResolver = $null
    $trx.Load($reader)
} finally { $reader.Dispose() }
if ($trx.DocumentElement.LocalName -cne 'TestRun' -or
    $trx.DocumentElement.NamespaceURI -cne 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') {
    throw 'Malformed candidate E2E TRX root or namespace.'
}
$ns = [Xml.XmlNamespaceManager]::new($trx.NameTable)
$ns.AddNamespace('t', $trx.DocumentElement.NamespaceURI)
$summary = $trx.SelectSingleNode('/t:TestRun/t:ResultSummary', $ns)
$counters = $trx.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $ns)
$results = @($trx.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns))
if ($null -eq $summary -or $null -eq $counters -or $results.Count -ne 1 -or
    $null -ne $trx.SelectSingleNode('//t:InnerResults', $ns)) {
    throw 'Candidate E2E must report exactly one selected test result.'
}
$expectedTest = 'OpenClaw.E2ETests.Setup.SetupAndConnectTests.FullSetup_SafeNodeInvocation_RoutesThroughRealGateway'
$test = $results[0]
if ($test.GetAttribute('testName') -cne $expectedTest) {
    throw 'Candidate E2E did not execute the expected setup, connect, and node invocation test.'
}
$passed = $test.GetAttribute('outcome') -ceq 'Passed'
$failed = $test.GetAttribute('outcome') -ceq 'Failed'
if (-not ($passed -or $failed)) { throw 'Candidate E2E skips are not execution proof.' }
$expectedCounters = @{ total = 1; executed = 1; passed = [int]$passed; failed = [int]$failed; notExecuted = 0 }
foreach ($name in $expectedCounters.Keys) {
    $count = 0
    if (-not [int]::TryParse($counters.GetAttribute($name), [ref]$count) -or $count -ne $expectedCounters[$name]) {
        throw "Candidate E2E TRX counter $name disagrees with its sole executed test."
    }
}
foreach ($attribute in $counters.Attributes) {
    if ($expectedCounters.ContainsKey($attribute.Name)) { continue }
    $count = 0
    if (-not [int]::TryParse($attribute.Value, [ref]$count) -or $count -ne 0) {
        throw 'Candidate E2E TRX has an invalid or nonzero auxiliary counter.'
    }
}
if ($null -ne $summary.SelectSingleNode('t:Output/t:ErrorInfo | t:Output/t:StdErr', $ns)) {
    throw 'Candidate E2E TRX contains a run-level error.'
}
$runErrors = @($summary.SelectNodes('t:RunInfos/t:RunInfo[@outcome!="Passed" and @outcome!="Completed"]', $ns))
# xUnit reports the test's own failure as an Error RunInfo too. Only that
# exact notice is redundant; collection cleanup and test-host errors are not.
$failureNotice = '^\[xUnit\.net \d+:\d+:\d+(?:\.\d+)?\]\s+' + [regex]::Escape($expectedTest) + ' \[FAIL\]$'
if ($runErrors.Count -gt 1 -or ($runErrors.Count -eq 1 -and
    (-not $failed -or $runErrors[0].GetAttribute('outcome') -cne 'Error' -or
     $runErrors[0].InnerText.Trim() -cnotmatch $failureNotice))) {
    throw 'Candidate E2E TRX contains a run-level error beyond the selected test failure.'
}
if ($TestExitCode -eq 0) {
    if (-not $passed -or $summary.GetAttribute('outcome') -cnotin @('Passed', 'Completed')) {
        throw 'Candidate E2E successful exit requires a passed TRX result.'
    }
    return 'passed'
}
if (-not $AllowProtocolMismatch -or -not $failed -or $summary.GetAttribute('outcome') -cne 'Failed') {
    throw 'Candidate E2E failed without an eligible protocol-mismatch exception.'
}
$message = $test.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
$exception = 'OpenClaw.E2ETests.Setup.E2EProtocolMismatchException : OPENCLAW_E2E_PROTOCOL_MISMATCH: fixture_initialization'
if ($null -eq $message -or $message.InnerText.Trim() -cne $exception) {
    throw 'Candidate E2E failed for a reason other than the sole expected protocol-mismatch exception.'
}
$result = Get-Content -LiteralPath $ProtocolResultPath -Raw | ConvertFrom-Json -AsHashtable
if ($result -isnot [System.Collections.IDictionary] -or
    $result.schema_version -isnot [long] -or $result.schema_version -ne 1 -or
    $result.outcome -isnot [string] -or
    $result.outcome -cne 'protocol_mismatch' -or
    $result.phase -isnot [string] -or
    $result.phase -cne 'fixture_initialization' -or
    $result.source -isnot [string] -or
    $result.source -cnotin @('app.status.nodeError', 'openclaw-tray.log.node_rx_error_code', 'openclaw-tray.log.operator_handshake_error_code') -or
    $result.code -isnot [string] -or
    $result.code -cne 'PROTOCOL_MISMATCH') {
    throw 'Candidate E2E failed with an invalid protocol-mismatch result.'
}
return 'protocol_mismatch'
