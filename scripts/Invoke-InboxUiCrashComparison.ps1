[CmdletBinding()]
param(
    [switch]$SelfTest,
    [switch]$SanitizeResults,
    [string]$SourceRoot,
    [ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedSha,
    [ValidateSet('main', 'pr2060')][string]$Case,
    [string]$ReportRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$MaxDiagnosticFiles = 24
$MaxTraceBytes = 32MB
$MaxExitRecords = 32
. (Join-Path $PSScriptRoot 'ComparisonLifecycleMetadata.ps1')

# VSTest v18.6.0 emits this callback at Error or Information. Parse its trace
# envelope and payload boundary, never an embedded stderr/serialized substring.
function ConvertTo-TestHostExitMetadata {
    param([string]$Line, [Collections.IDictionary]$Counters = $null)
    if ($null -ne $Counters) { $Counters.lines++ }
    $envelope = '^TpTrace (?:Error|Information):\s*\d+\s*:\s*(?<runner>\d+),\s*(?<thread>\d+),\s*(?<date>\d{4}/\d{2}/\d{2}),\s*(?<time>\d{2}:\d{2}:\d{2}\.\d{3}),\s*(?<ticks>\d+),\s*[A-Za-z0-9_.-]+,\s*(?<payload>[^\r\n]*)$'
    if ($Line -cnotmatch $envelope) {
        if ($null -ne $Counters) {
            $Counters.unmatchedEnvelopes++
            if ($Line.Contains('TestHostManagerCallbacks.ExitCallBack:')) { $Counters.unboundCallbackMarkers++ }
        }
        return $null
    }
    if ($null -ne $Counters) { $Counters.recognizedEnvelopes++ }
    $payload = $Matches.payload
    $runner = 0
    $thread = 0
    $ticks = 0L
    $when = [DateTime]::MinValue
    if (-not [int]::TryParse($Matches.runner, [ref]$runner) -or $runner -le 0 -or
        -not [int]::TryParse($Matches.thread, [ref]$thread) -or $thread -le 0 -or
        -not [long]::TryParse($Matches.ticks, [ref]$ticks) -or $ticks -lt 0 -or
        -not [DateTime]::TryParseExact(($Matches.date + ' ' + $Matches.time), 'yyyy/MM/dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$when)) { if ($null -ne $Counters) { $Counters.invalidEnvelopeFields++ }; return $null }
    if ($payload -cnotmatch '^TestHostManagerCallbacks\.ExitCallBack: Testhost processId: (?<pid>-?\d+) exited with exitcode: (?<code>-?\d+) error:') { return $null }
    if ($null -ne $Counters) { $Counters.callbackPrefixes++ }
    $processId = 0
    $exitValue = 0
    if (-not [int]::TryParse($Matches.pid, [ref]$processId) -or $processId -le 0 -or
        -not [int]::TryParse($Matches.code, [ref]$exitValue)) { if ($null -ne $Counters) { $Counters.invalidCallbackFields++ }; return $null }
    if ($null -ne $Counters) { $Counters.acceptedRecords++ }
    [pscustomobject][ordered]@{
        source = 'vstest-exit-callback-reported'
        runnerProcessId = $runner
        hostProcessId = $processId
        reportedExitCode = $exitValue
        reportedExitCodeHex = ('0x{0:X8}' -f ([long]$exitValue -band 4294967295L))
        # The producer ignores TryGetExitCode's availability Boolean and can use -1 for E_HANDLE.
        valueMayRepresentUnavailableCode = ($exitValue -eq 0 -or $exitValue -eq -1)
    }
}

# This helper owns the handle even when the parser or count guard throws before EOF.
function Read-ComparisonExitTraceFile {
    param([string]$Path, [Collections.Generic.List[object]]$Metadata, [Collections.IDictionary]$Counters)
    $reader = [IO.File]::OpenText($Path)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            $record = ConvertTo-TestHostExitMetadata -Line $line -Counters $Counters
            if ($null -ne $record) {
                if ($Metadata.Count -ge $MaxExitRecords) { throw 'Too many testhost exit records' }
                $Metadata.Add($record)
            }
        }
    }
    finally { $reader.Dispose() }
}

function Read-ComparisonXml {
    param([IO.TextReader]$InputReader)
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.CloseInput = $true
    $reader = [Xml.XmlReader]::Create($InputReader, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    }
    finally { $reader.Dispose() }
}

function Get-SafeTestMethod {
    param([string]$Name)
    $method = ($Name -split '\(', 2)[0].Trim()
    if ($method.Length -le 512 -and $method -cmatch '^(?:OpenClaw|OpenClawTray)(?:\.[A-Za-z_][A-Za-z0-9_]*)+$') { return $method }
    return $null
}

function ConvertTo-ResultMetadata {
    param([xml]$Document)
    if ($Document.DocumentElement.LocalName -cne 'TestRun' -or $Document.DocumentElement.NamespaceURI -cne 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') { throw 'Unexpected test result schema' }
    $ns = [Xml.XmlNamespaceManager]::new($Document.NameTable)
    $ns.AddNamespace('t', $Document.DocumentElement.NamespaceURI)
    $summary = $Document.SelectSingleNode('/t:TestRun/t:ResultSummary', $ns)
    if ($null -eq $summary) { throw 'Missing result summary' }
    $outcome = $summary.GetAttribute('outcome')
    if ($outcome -cnotin @('Passed', 'Completed', 'Failed', 'Aborted', 'NotExecuted')) { $outcome = 'Unknown' }
    $rows = @($Document.SelectNodes('/t:TestRun/t:Results//t:UnitTestResult[not(t:InnerResults)]', $ns))
    $cases = @(for ($i = 0; $i -lt $rows.Count; $i++) {
        $row = $rows[$i]
        $caseOutcome = $row.GetAttribute('outcome')
        if ($caseOutcome -cnotin @('Passed', 'Failed', 'NotExecuted', 'Aborted', 'Error', 'Timeout')) { $caseOutcome = 'Unknown' }
        [ordered]@{ ordinal = $i; method = (Get-SafeTestMethod ($row.GetAttribute('testName'))); outcome = $caseOutcome }
    })
    [ordered]@{
        schema = 'sanitized-test-results-v1'
        reportedOutcome = $outcome
        recordedCases = $rows.Count
        passed = @($rows | Where-Object { $_.GetAttribute('outcome') -ceq 'Passed' }).Count
        failed = @($rows | Where-Object { $_.GetAttribute('outcome') -ceq 'Failed' }).Count
        skipped = @($rows | Where-Object { $_.GetAttribute('outcome') -ceq 'NotExecuted' }).Count
        cases = @($cases)
    }
}

function Save-ComparisonResults {
    param([string]$Root)
    $destination = Join-Path $Root 'sanitized'
    New-Item -ItemType Directory -Path $destination -ErrorAction Stop | Out-Null
    $names = @('ordinary-ui.trx', 'functional.trx', 'OpenClaw.Shared.Tests.trx', 'OpenClaw.Tray.Tests.trx')
    $reports = @(Get-ChildItem -LiteralPath $Root -Filter '*.trx' -Recurse -File)
    if ($reports.Count -gt 4) { throw 'Unexpected test report inventory' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($report in $reports) {
        if ($report.Name -cnotin $names -or -not $seen.Add($report.Name) -or $report.Length -gt 16MB -or ($report.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Test report is outside the explicit allowlist' }
        $xml = Read-ComparisonXml ([IO.File]::OpenText($report.FullName))
        $safe = ConvertTo-ResultMetadata $xml
        $safe | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $destination ($report.BaseName + '.json')) -Encoding utf8
    }
    $sequences = @(Get-ChildItem -LiteralPath (Join-Path $Root 'ordinary') -Filter 'Sequence*.xml' -Recurse -File -ErrorAction SilentlyContinue)
    if ($sequences.Count -gt 8) { throw 'Unexpected sequence inventory' }
    for ($fileIndex = 0; $fileIndex -lt $sequences.Count; $fileIndex++) {
        $file = $sequences[$fileIndex]
        if ($file.Length -gt 16MB -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Sequence is outside the explicit allowlist' }
        $xml = Read-ComparisonXml ([IO.File]::OpenText($file.FullName))
        if ($xml.DocumentElement.LocalName -cne 'TestSequence') { throw 'Unexpected sequence schema' }
        $entries = @($xml.DocumentElement.ChildNodes | Where-Object NodeType -eq Element)
        $safe = @(for ($i = 0; $i -lt $entries.Count; $i++) {
            $completed = $false
            if (-not [bool]::TryParse($entries[$i].GetAttribute('Completed'), [ref]$completed)) { throw 'Invalid sequence completion flag' }
            [ordered]@{ ordinal = $i; method = (Get-SafeTestMethod ($entries[$i].GetAttribute('Name'))); completed = $completed }
        })
        [ordered]@{ schema = 'sanitized-test-sequence-v1'; entries = @($safe) } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $destination ("sequence-$fileIndex.json")) -Encoding utf8
    }
    [ordered]@{ reportedTrxFiles = $reports.Count; reportedSequenceFiles = $sequences.Count; ordinaryTrxPresent = $seen.Contains('ordinary-ui.trx') } | ConvertTo-Json | Set-Content (Join-Path $destination 'inventory.json') -Encoding utf8
}

# NativeCommandProcessor writes global:LASTEXITCODE, not a nearer script variable.
function Invoke-ComparisonNative {
    param([string]$Executable, [string[]]$Arguments, [ref]$ExitCode)
    $ExitCode.Value = $null
    $global:LASTEXITCODE = $null
    try { & $Executable @Arguments }
    finally { $ExitCode.Value = $global:LASTEXITCODE }
}

if ($SelfTest) {
    & (Join-Path $PSScriptRoot 'test-comparison-lifecycle.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Lifecycle schema self-tests failed' }
    $envelope = 'TpTrace Information: 0 : 100, 5, 2026/11/08, 23:10:00.100, 123456, vstest.console, '
    $callback = 'TestHostManagerCallbacks.ExitCallBack: Testhost processId: 42 exited with exitcode: '
    foreach ($code in @(-1073741819, 0, -1)) {
        $actual = ConvertTo-TestHostExitMetadata ($envelope + $callback + $code + ' error: SECRET-MUST-NOT-LEAK')
        if ($null -eq $actual -or $actual.reportedExitCode -ne $code -or $actual.hostProcessId -ne 42 -or $actual.runnerProcessId -ne 100) { throw 'Exit callback self-test failed' }
        $json = $actual | ConvertTo-Json -Compress
        if ($json.Contains('SECRET') -or @($actual.PSObject.Properties).Count -ne 6) { throw 'Exit metadata exported unapproved content' }
    }
    if ((ConvertTo-TestHostExitMetadata ($envelope + $callback + '-1073741819 error: hidden')).reportedExitCodeHex -cne '0xC0000005') { throw 'Signed exit-code representation changed' }
    $reject = @(
        ('STDERR: ' + $envelope + $callback + '0 error: hidden')
        ($envelope + 'AnotherProducer: quoted ' + $callback + '0 error: hidden')
        ($envelope + 'Testhost process 42 exited with exitcode: 0')
        ($envelope.Replace('2026/11/08', '2026/99/99') + $callback + '0 error: hidden')
        ($envelope + $callback + '3221225477 error: hidden')
        ($envelope + $callback + '-2147483649 error: hidden')
    )
    foreach ($line in $reject) { if ($null -ne (ConvertTo-TestHostExitMetadata $line)) { throw 'Invalid callback lookalike was accepted' } }
    $counts = [ordered]@{ lines = 0L; recognizedEnvelopes = 0L; unmatchedEnvelopes = 0L; unboundCallbackMarkers = 0L; invalidEnvelopeFields = 0L; callbackPrefixes = 0L; invalidCallbackFields = 0L; acceptedRecords = 0L }
    $null = ConvertTo-TestHostExitMetadata -Line ($envelope + $callback + '0 error: SECRET-MUST-NOT-LEAK') -Counters $counts
    $null = ConvertTo-TestHostExitMetadata -Line ('STDERR: ' + $callback + '0 error: SECRET-MUST-NOT-LEAK') -Counters $counts
    if ($counts.lines -ne 2 -or $counts.recognizedEnvelopes -ne 1 -or $counts.acceptedRecords -ne 1 -or $counts.unboundCallbackMarkers -ne 1 -or ($counts | ConvertTo-Json -Compress).Contains('SECRET')) { throw 'Parser stage-counter self-test failed' }
    $xmlText = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="OpenClaw.Tests.Sample.Fact(secret: SECRET-MUST-NOT-LEAK)" outcome="Passed"><Output><StdOut>SECRET-MUST-NOT-LEAK</StdOut></Output></UnitTestResult></Results><ResultSummary outcome="Completed"/></TestRun>'
    $safe = ConvertTo-ResultMetadata (Read-ComparisonXml ([IO.StringReader]::new($xmlText)))
    $json = $safe | ConvertTo-Json -Depth 6 -Compress
    if ($safe.passed -ne 1 -or $json.Contains('SECRET') -or $json.Contains('StdOut') -or $safe.cases[0].method -cne 'OpenClaw.Tests.Sample.Fact') { throw 'Test-result sanitizer self-test failed' }
    $dtdRejected = $false
    try { $null = Read-ComparisonXml ([IO.StringReader]::new('<!DOCTYPE root [<!ENTITY secret "SECRET-MUST-NOT-LEAK">]><root>&secret;</root>')) }
    catch [Xml.XmlException] { $dtdRejected = $true }
    if (-not $dtdRejected) { throw 'XML DTD was not rejected' }
    if (-not $IsWindows -or -not (Test-Path -LiteralPath $env:ComSpec -PathType Leaf)) { throw 'Native exit capture self-tests require the existing Windows command processor' }
    $priorNativePreference = $PSNativeCommandUseErrorActionPreference
    $priorGlobalExit = $global:LASTEXITCODE
    try {
        foreach ($nativePreference in @($false, $true)) {
            $PSNativeCommandUseErrorActionPreference = $nativePreference
            foreach ($expectedCode in @(0, 17)) {
                $LASTEXITCODE = 999 # A nearer-scope shadow must not affect capture.
                $observedCode = $null
                $threw = $false
                try { Invoke-ComparisonNative -Executable $env:ComSpec -Arguments @('/d', '/c', "exit $expectedCode") -ExitCode ([ref]$observedCode) }
                catch { $threw = $true }
                if ($null -eq $observedCode -or $observedCode -ne $expectedCode -or $threw -ne ($nativePreference -and $expectedCode -ne 0)) { throw 'Native zero/nonzero capture self-test failed' }
            }
        }
        $PSNativeCommandUseErrorActionPreference = $false
        $global:LASTEXITCODE = 17
        $observedCode = $null
        $launchFailed = $false
        $missing = Join-Path $env:RUNNER_TEMP ("inbox-missing-$([guid]::NewGuid().ToString('N')).exe")
        try { Invoke-ComparisonNative -Executable $missing -Arguments @() -ExitCode ([ref]$observedCode) }
        catch [Management.Automation.CommandNotFoundException] { $launchFailed = $true }
        if (-not $launchFailed -or $null -ne $observedCode) { throw 'Native launch failure inherited or fabricated an exit code' }
    }
    finally {
        $PSNativeCommandUseErrorActionPreference = $priorNativePreference
        $global:LASTEXITCODE = $priorGlobalExit
    }
    # Windows sharing semantics are the oracle: reject callback 33, then reopen/delete
    # immediately. Trailing data keeps this an early-abort path, not EOF disposal.
    $traceTestRoot = Join-Path $env:RUNNER_TEMP ("inbox-reader-selftest-$([guid]::NewGuid().ToString('N'))")
    $null = [IO.Directory]::CreateDirectory($traceTestRoot)
    $readerPriorPreference = $PSNativeCommandUseErrorActionPreference
    $readerPriorExit = $global:LASTEXITCODE
    try {
        $ownedTrace = Join-Path $traceTestRoot 'testplatform.log'
        $writer = [IO.File]::CreateText($ownedTrace)
        try {
            for ($i = 0; $i -lt 33; $i++) { $writer.WriteLine($envelope + $callback + '17 error: PRIVATE-SELFTEST-TAIL') }
            $writer.WriteLine('TRAILING-PRIVATE-SELFTEST-DATA' * 4096)
        } finally { $writer.Dispose() }
        if ((Get-Item -LiteralPath $ownedTrace).Length -gt $MaxTraceBytes) { throw 'Reader fixture exceeded the unchanged file limit' }
        $PSNativeCommandUseErrorActionPreference = $false
        $readerTestExit = $null
        Invoke-ComparisonNative -Executable $env:ComSpec -Arguments @('/d', '/c', 'exit 17') -ExitCode ([ref]$readerTestExit)
        if ($readerTestExit -ne 17) { throw 'Reader fixture did not observe its real native failure' }
        $limitRecords = [Collections.Generic.List[object]]::new()
        $limitCounts = [ordered]@{ lines = 0L; recognizedEnvelopes = 0L; unmatchedEnvelopes = 0L; unboundCallbackMarkers = 0L; invalidEnvelopeFields = 0L; callbackPrefixes = 0L; invalidCallbackFields = 0L; acceptedRecords = 0L }
        $limitRejected = $false
        try { Read-ComparisonExitTraceFile -Path $ownedTrace -Metadata $limitRecords -Counters $limitCounts }
        catch { $limitRejected = $_.Exception.Message -ceq 'Too many testhost exit records' }
        if (-not $limitRejected -or $limitRecords.Count -ne 32 -or $limitCounts.lines -ne 33 -or $limitCounts.acceptedRecords -ne 33) {
            throw 'Trace fixture did not reject at the intended callback cap'
        }
        $exclusive = [IO.File]::Open($ownedTrace, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try { if ($exclusive.Length -le 0) { throw 'Reader fixture unexpectedly became empty' } }
        finally { $exclusive.Dispose() }
        [IO.File]::Delete($ownedTrace)
        if ([IO.File]::Exists($ownedTrace) -or $readerTestExit -ne 17) { throw 'Trace cleanup changed the observed native failure or did not delete the file' }
    }
    catch { throw 'Owned trace-reader limit/disposal self-test failed.' }
    finally {
        $PSNativeCommandUseErrorActionPreference = $readerPriorPreference
        $global:LASTEXITCODE = $readerPriorExit
        try { [IO.Directory]::Delete($traceTestRoot, $true) }
        catch { throw 'Owned trace-reader self-test directory cleanup failed.' }
    }
    Write-Host 'Trace-reader self-test passed: callback cap, immediate exclusive reopen/delete and preserved native exit 17.'
    Write-Host 'Diagnostic self-tests passed: native exit capture, producer envelope, numeric bounds, lookalike rejection, result redaction and DTD rejection.'
    $global:LASTEXITCODE = 0
    return
}

if (-not $SourceRoot -or -not $ExpectedSha -or -not $Case -or -not $ReportRoot -or -not $env:RUNNER_TEMP) { throw 'Complete comparison invocation parameters are required' }
$sourcePath = [IO.Path]::GetFullPath($SourceRoot)
$reportPath = [IO.Path]::GetFullPath($ReportRoot)
if (-not $env:GITHUB_WORKSPACE -or $reportPath -cne [IO.Path]::GetFullPath((Join-Path $env:GITHUB_WORKSPACE "proof/$Case"))) { throw 'Report root must be the fixed task-owned comparison directory' }
if ((Get-Location).Path -cne $sourcePath) { throw 'Run the comparison from its pinned source checkout' }
$actualSha = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSha -cne $ExpectedSha) { throw 'Unexpected source SHA at diagnostic invocation' }
if ($env:OPENCLAW_CI_COLLECT_COVERAGE -cne 'false') { throw 'Comparison must retain PR non-coverage behavior' }
$pinned = @{ main = 'b5c37e14476f1e2d2eda38deabf631b3dc8ffc0c'; pr2060 = '3876d834ae5bbe2f75ef0a95b82120ddb55d6cfc' }
if ($ExpectedSha -cne $pinned[$Case]) { throw 'Comparison case is not its admitted immutable source' }
$instrumented = $Case -ceq 'pr2060'
$producerPresent = Test-Path -LiteralPath (Join-Path $sourcePath 'tests/OpenClaw.TestSupport/NativeUiExceptionDiagnostics.cs') -PathType Leaf
if ($producerPresent -ne $instrumented) { throw 'Unexpected product lifecycle instrumentation contract' }


New-Item -ItemType Directory -Path $reportPath -Force | Out-Null
. (Join-Path $sourcePath 'scripts/TestResultContract.ps1')
if ($SanitizeResults) {
    try { Save-ComparisonResults -Root $reportPath }
    catch { Write-Host ("Result sanitizer error type: " + $_.Exception.GetType().FullName); throw 'Result sanitization failed; raw result content is not exported.' }
    $global:LASTEXITCODE = 0
    return
}
# A fresh pipeline run is one paired experiment, not permission for rerun-attempt loops.
if ($env:GITHUB_RUN_ATTEMPT -cne '1') { throw 'This comparison admits one attempt per arm only' }
# CreateNew also refuses a second ordinary invocation inside the same arm/job.
$claim = [IO.File]::Open((Join-Path $reportPath 'ordinary-attempt.claim'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$claim.Dispose()
$invocation = New-TestInvocation -ResultsDirectory (Join-Path $reportPath 'ordinary') -TrxFileName 'ordinary-ui.trx'
$filter = 'Category!=Accessibility&Category!=NativeOnboardingProof&Category!=GatewayFixture'
[xml]$props = Get-Content (Join-Path $sourcePath 'Directory.Build.props')
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the .NET SDK' }
$runtimeVersions = @(& dotnet --list-runtimes | ForEach-Object {
    if ($_ -cmatch '^(?<name>Microsoft\.(?:NETCore|WindowsDesktop|AspNetCore)\.App) (?<version>[0-9A-Za-z.+-]+) \[') { [ordered]@{ name = $Matches.name; version = $Matches.version } }
})
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the .NET runtimes' }
$runtimePackageError = $null
$runtimePackages = @()
try { $runtimePackages = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime*' -ErrorAction Stop | ForEach-Object { [ordered]@{ name = $_.Name; version = $_.Version.ToString(); architecture = $_.Architecture.ToString() } } | Sort-Object { $_.name }, { $_.version }) }
catch { $runtimePackageError = $_.Exception.GetType().FullName }
$binaryNames = @('OpenClaw.Tray.UITests.dll', 'testhost.dll', 'testhost.exe', 'Microsoft.TestPlatform.CrossPlatEngine.dll', 'Microsoft.TestPlatform.TestHostRuntimeProvider.dll', 'Interop.UIAutomationClient.dll')
$binaryFiles = @(Get-ChildItem -LiteralPath (Join-Path $sourcePath 'tests/OpenClaw.Tray.UITests/bin') -File -Recurse | Where-Object { $_.Name -cin $binaryNames })
if ($binaryFiles.Count -gt 64) { throw 'Unexpected UI binary inventory' }
$binaries = @($binaryFiles | ForEach-Object {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName)
    [ordered]@{ path = [IO.Path]::GetRelativePath($sourcePath, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); fileVersion = "$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart).$($version.FilePrivatePart)" }
})
$runnerPath = Join-Path $env:DOTNET_ROOT "sdk/$sdk/vstest.console.dll"
$runnerBinary = $null
if (Test-Path -LiteralPath $runnerPath -PathType Leaf) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($runnerPath)
    $runnerBinary = [ordered]@{ sha256 = (Get-FileHash -LiteralPath $runnerPath -Algorithm SHA256).Hash.ToLowerInvariant(); fileVersion = "$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart).$($version.FilePrivatePart)" }
}
$manifest = [ordered]@{
    schema = 'inbox-ui-crash-comparison-v2'
    case = $Case
    sourceCommit = $actualSha
    sourceTree = (& git rev-parse 'HEAD^{tree}').Trim()
    pipelineCommit = $env:GITHUB_SHA
    runId = $env:GITHUB_RUN_ID
    runAttempt = $env:GITHUB_RUN_ATTEMPT
    startedUtc = $invocation.startedUtc
    sdk = $sdk
    runtimes = $runtimeVersions
    windowsAppRuntimePackages = $runtimePackages
    runtimePackageQueryErrorType = $runtimePackageError
    testBinaries = $binaries
    vstestRunnerBinary = $runnerBinary
    osVersion = [Environment]::OSVersion.Version.ToString()
    osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    runnerImage = $env:ImageOS
    runnerImageVersion = $env:ImageVersion
    windowsAppSdk = $props.SelectSingleNode('/Project/PropertyGroup/MicrosoftWindowsAppSDKVersion').InnerText.Trim()
    filter = $filter
    configuration = 'Debug'
    runtime = 'win-x64'
    coverage = $false
    ordinaryAttemptBudget = 1
    nugetCache = 'no-workflow-cache-cold-symmetric'
    lifecycleInstrumentation = $(if ($instrumented) { 'product-existing-sink' } else { 'not-instrumented' })
    configuredProductSourceRevision = $ExpectedSha
    diagnostics = 'VSTest trace reduced to typed exit metadata; no raw trace or dumps uploaded'
    gitVersionNoNormalize = $env:GitVersion_NoNormalizeEnabled
    gitVersionNoFetch = $env:GitVersion_NoFetchEnabled
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $reportPath 'manifest.json') -Encoding utf8

# Keep raw VSTest transport/host traces outside the curated artifact root.
if ($env:GITHUB_RUN_ID -notmatch '^\d+$' -or $env:GITHUB_RUN_ATTEMPT -notmatch '^\d+$') { throw 'Run identity is required for owned diagnostic cleanup' }
$rawRoot = Join-Path $env:RUNNER_TEMP ("inbox-ui-diagnostics-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT-$Case")
New-Item -ItemType Directory -Path $rawRoot -ErrorAction Stop | Out-Null
$rawTrace = Join-Path $rawRoot 'testplatform.log'
$lifecyclePath = Join-Path $rawRoot 'native-ui-exceptions.jsonl'
$arguments = @('test', 'tests/OpenClaw.Tray.UITests', '--no-build', '-c', 'Debug', '-r', 'win-x64', '--verbosity', 'normal', '--results-directory', $invocation.directory, '--logger', 'trx;LogFileName=ordinary-ui.trx', '--filter', $filter, '--blame-hang', '--blame-hang-timeout', '300s', '--blame-hang-dump-type', 'none', '--diag', ($rawTrace + ';tracelevel=info'))
$testExit = $null
$diagnosticError = $null
$nativeErrorType = $null
$lifecycleErrorType = $null
$environmentRestoreFailed = $false
$lifecycle = [ordered]@{ schema = 'validated-native-ui-lifecycle-v1'; status = $(if ($instrumented) { 'unavailable' } else { 'not-instrumented' }); sourceCommit = $ExpectedSha; invocationId = $invocation.id; recordCount = 0; records = @() }
$rawCleanup = $false
$traceFiles = 0
$metadata = [Collections.Generic.List[object]]::new()
$parseCounters = [ordered]@{ lines = 0L; recognizedEnvelopes = 0L; unmatchedEnvelopes = 0L; unboundCallbackMarkers = 0L; invalidEnvelopeFields = 0L; callbackPrefixes = 0L; invalidCallbackFields = 0L; acceptedRecords = 0L }
$previousMetadataPath = $env:OPENCLAW_UI_EXCEPTION_METADATA_PATH
$previousMetadataInvocation = $env:OPENCLAW_UI_EXCEPTION_INVOCATION_ID
$previousProductSource = $env:OPENCLAW_RELEASE_SOURCE_SHA
try {
    # The producer reads OPENCLAW_RELEASE_SOURCE_SHA, never pipeline GITHUB_SHA.
    # Main ignores these variables because its pinned source has no sink implementation.
    $env:OPENCLAW_UI_EXCEPTION_METADATA_PATH = $lifecyclePath
    $env:OPENCLAW_UI_EXCEPTION_INVOCATION_ID = $invocation.id
    $env:OPENCLAW_RELEASE_SOURCE_SHA = $ExpectedSha
    Invoke-ComparisonNative -Executable 'dotnet' -Arguments $arguments -ExitCode ([ref]$testExit)
}
catch { $nativeErrorType = $_.Exception.GetType().FullName }
finally {
    # Attempt each restoration even if an earlier restoration fails; report no values.
    foreach ($entry in @(
        @('OPENCLAW_UI_EXCEPTION_METADATA_PATH', $previousMetadataPath),
        @('OPENCLAW_UI_EXCEPTION_INVOCATION_ID', $previousMetadataInvocation),
        @('OPENCLAW_RELEASE_SOURCE_SHA', $previousProductSource))) {
        try { [Environment]::SetEnvironmentVariable($entry[0], $entry[1], 'Process') }
        catch { $environmentRestoreFailed = $true }
    }
    try {
        $files = @(Get-ChildItem -LiteralPath $rawRoot -File | Where-Object { $_.Name -cne 'native-ui-exceptions.jsonl' })
        $traceFiles = $files.Count
        if ($files.Count -gt $MaxDiagnosticFiles) { throw 'Unexpected diagnostic file inventory' }
        foreach ($file in $files) {
            if ($file.Name -notlike 'testplatform*' -or $file.Length -gt $MaxTraceBytes) { throw 'Diagnostic trace is outside the bounded inventory' }
            Read-ComparisonExitTraceFile -Path $file.FullName -Metadata $metadata -Counters $parseCounters
        }
    }
    catch { $diagnosticError = $_.Exception.GetType().FullName }
    finally {
        try {
            if ($instrumented) {
                if ($diagnosticError) { throw 'Lifecycle host binding was not successfully extracted' }
                $lifecycleHost = Get-ComparisonLifecycleHost -ExitMetadata @($metadata.ToArray())
                $validatedLifecycle = Read-ComparisonLifecycle -Path $lifecyclePath -ExpectedSource $ExpectedSha -InvocationId $invocation.id -HostProcessId $lifecycleHost
                $lifecycle = $validatedLifecycle
            } elseif (Test-Path -LiteralPath $lifecyclePath) { throw 'Uninstrumented baseline unexpectedly produced lifecycle data' }
            # Reconstruct validated primitives, never copy the producer JSONL into artifacts.
            $safeJson = $lifecycle | ConvertTo-Json -Depth 6 -Compress
            if ([Text.Encoding]::UTF8.GetByteCount($safeJson) -gt $ComparisonLifecycleMaxOutputBytes) { throw 'Validated lifecycle output exceeds its bound' }
            $pendingReceipt = Join-Path $reportPath 'native-ui-metadata.pending'
            [IO.File]::WriteAllText($pendingReceipt, $safeJson, [Text.UTF8Encoding]::new($false))
            [IO.File]::Move($pendingReceipt, (Join-Path $reportPath 'native-ui-metadata.json'))
        }
        catch { $lifecycleErrorType = $_.Exception.GetType().Name }
        try {
            Remove-Item -LiteralPath $rawRoot -Recurse -Force -ErrorAction Stop
            $rawCleanup = -not (Test-Path -LiteralPath $rawRoot)
        }
        catch { $diagnosticError = $_.Exception.GetType().FullName }
    }
    $distinct = @($metadata | ForEach-Object { "$($_.hostProcessId):$($_.reportedExitCode)" } | Select-Object -Unique)
    $metadataStatus = if ($distinct.Count -eq 0) { 'missing' } elseif ($distinct.Count -eq 1) { 'single-reported-exit' } else { 'multiple-or-conflicting-reported-exits' }
    $summary = [ordered]@{
        sourceCommit = $ExpectedSha
        case = $Case
        finishedUtc = [DateTime]::UtcNow.ToString('o')
        testCommandExitCode = $testExit
        traceFilesExamined = $traceFiles
        traceParserCounts = $parseCounters
        testHostExitMetadataStatus = $metadataStatus
        testHostExitMetadata = @($metadata.ToArray())
        diagnosticErrorType = $diagnosticError
        nativeErrorType = $nativeErrorType
        lifecycleStatus = $lifecycle.status
        lifecycleErrorType = $lifecycleErrorType
        environmentRestoreFailed = $environmentRestoreFailed
        rawTraceRemoved = $rawCleanup
    }
    try {
        $summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $reportPath 'exit-summary.json') -Encoding utf8
        Write-Host ('Bounded exit metadata: ' + ($summary | ConvertTo-Json -Depth 6 -Compress))
    } catch { $diagnosticError = $_.Exception.GetType().FullName } # Never replace an observed native failure with a receipt-write failure.
}
if ($null -eq $testExit) { throw 'The test command did not return an exit code' }
if ($testExit -ne 0) { exit $testExit }
if ($nativeErrorType -or $diagnosticError -or $lifecycleErrorType -or $environmentRestoreFailed -or -not $rawCleanup) { throw 'Diagnostic extraction or raw-trace cleanup failed; do not infer a valid comparison' }
if ($instrumented -and ($lifecycle.status -cne 'validated' -or $lifecycle.overflowed -or -not $lifecycle.closed)) { throw 'A successful comparison requires complete lifecycle metadata' }
$result = Read-TestResultContract -Path $invocation.report -StartedUtc $invocation.startedUtc -ExitCode 0 -AllowSkipped
Write-Host ('Validated ordinary UI result: ' + ($result | ConvertTo-Json -Compress))
if ((& git rev-parse HEAD).Trim() -cne $ExpectedSha) { throw 'Product source changed during the test' }
$global:LASTEXITCODE = 0
