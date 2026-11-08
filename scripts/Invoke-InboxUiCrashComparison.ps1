[CmdletBinding()]
param(
    [switch]$SelfTest,
    [switch]$SanitizeResults,
    [string]$SourceRoot,
    [ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedSha,
    [ValidateSet('main', 'pr2063')][string]$Case,
    [string]$ReportRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$MaxDiagnosticFiles = 24
$MaxTraceBytes = 32MB
$MaxExitRecords = 32

# VSTest v18.6.0 emits this callback at Error or Information. Parse its trace
# envelope and payload boundary, never an embedded stderr/serialized substring.
function ConvertTo-TestHostExitMetadata {
    param([string]$Line)
    $envelope = '^TpTrace (?:Error|Information):\s*\d+\s*:\s*(?<runner>\d+),\s*(?<thread>\d+),\s*(?<date>\d{4}/\d{2}/\d{2}),\s*(?<time>\d{2}:\d{2}:\d{2}\.\d{3}),\s*(?<ticks>\d+),\s*[A-Za-z0-9_.-]+,\s*(?<payload>[^\r\n]*)$'
    if ($Line -cnotmatch $envelope) { return $null }
    $payload = $Matches.payload
    $runner = 0
    $thread = 0
    $ticks = 0L
    $when = [DateTime]::MinValue
    if (-not [int]::TryParse($Matches.runner, [ref]$runner) -or $runner -le 0 -or
        -not [int]::TryParse($Matches.thread, [ref]$thread) -or $thread -le 0 -or
        -not [long]::TryParse($Matches.ticks, [ref]$ticks) -or $ticks -lt 0 -or
        -not [DateTime]::TryParseExact(($Matches.date + ' ' + $Matches.time), 'yyyy/MM/dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$when)) { return $null }
    if ($payload -cnotmatch '^TestHostManagerCallbacks\.ExitCallBack: Testhost processId: (?<pid>-?\d+) exited with exitcode: (?<code>-?\d+) error:') { return $null }
    $processId = 0
    $exitValue = 0
    if (-not [int]::TryParse($Matches.pid, [ref]$processId) -or $processId -le 0 -or
        -not [int]::TryParse($Matches.code, [ref]$exitValue)) { return $null }
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

if ($SelfTest) {
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
    $xmlText = '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="OpenClaw.Tests.Sample.Fact(secret: SECRET-MUST-NOT-LEAK)" outcome="Passed"><Output><StdOut>SECRET-MUST-NOT-LEAK</StdOut></Output></UnitTestResult></Results><ResultSummary outcome="Completed"/></TestRun>'
    $safe = ConvertTo-ResultMetadata (Read-ComparisonXml ([IO.StringReader]::new($xmlText)))
    $json = $safe | ConvertTo-Json -Depth 6 -Compress
    if ($safe.passed -ne 1 -or $json.Contains('SECRET') -or $json.Contains('StdOut') -or $safe.cases[0].method -cne 'OpenClaw.Tests.Sample.Fact') { throw 'Test-result sanitizer self-test failed' }
    $dtdRejected = $false
    try { $null = Read-ComparisonXml ([IO.StringReader]::new('<!DOCTYPE root [<!ENTITY secret "SECRET-MUST-NOT-LEAK">]><root>&secret;</root>')) }
    catch [Xml.XmlException] { $dtdRejected = $true }
    if (-not $dtdRejected) { throw 'XML DTD was not rejected' }
    Write-Host 'Diagnostic self-tests passed: producer envelope, numeric bounds, lookalike rejection, result redaction and DTD rejection.'
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

New-Item -ItemType Directory -Path $reportPath -Force | Out-Null
. (Join-Path $sourcePath 'scripts/TestResultContract.ps1')
if ($SanitizeResults) {
    try { Save-ComparisonResults -Root $reportPath }
    catch { Write-Host ("Result sanitizer error type: " + $_.Exception.GetType().FullName); throw 'Result sanitization failed; raw result content is not exported.' }
    $global:LASTEXITCODE = 0
    return
}
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
    schema = 'inbox-ui-crash-comparison-v1'
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
$arguments = @('test', 'tests/OpenClaw.Tray.UITests', '--no-build', '-c', 'Debug', '-r', 'win-x64', '--verbosity', 'normal', '--results-directory', $invocation.directory, '--logger', 'trx;LogFileName=ordinary-ui.trx', '--filter', $filter, '--blame-hang', '--blame-hang-timeout', '300s', '--blame-hang-dump-type', 'none', '--diag', ($rawTrace + ';tracelevel=info'))
$testExit = $null
$diagnosticError = $null
$rawCleanup = $false
$traceFiles = 0
$metadata = [Collections.Generic.List[object]]::new()
try {
    $LASTEXITCODE = $null
    & dotnet @arguments
    $testExit = $LASTEXITCODE
}
finally {
    # Preserve a native nonzero exit even if the host enables native-command exceptions.
    if ($null -eq $testExit -and $null -ne $LASTEXITCODE) { $testExit = $LASTEXITCODE }
    try {
        $files = @(Get-ChildItem -LiteralPath $rawRoot -File)
        $traceFiles = $files.Count
        if ($files.Count -gt $MaxDiagnosticFiles) { throw 'Unexpected diagnostic file inventory' }
        foreach ($file in $files) {
            if ($file.Name -notlike 'testplatform*' -or $file.Length -gt $MaxTraceBytes) { throw 'Diagnostic trace is outside the bounded inventory' }
            foreach ($line in [IO.File]::ReadLines($file.FullName)) {
                $record = ConvertTo-TestHostExitMetadata $line
                if ($null -ne $record) {
                    if ($metadata.Count -ge $MaxExitRecords) { throw 'Too many testhost exit records' }
                    $metadata.Add($record)
                }
            }
        }
    }
    catch { $diagnosticError = $_.Exception.GetType().FullName }
    finally {
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
        testHostExitMetadataStatus = $metadataStatus
        testHostExitMetadata = @($metadata.ToArray())
        diagnosticErrorType = $diagnosticError
        rawTraceRemoved = $rawCleanup
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $reportPath 'exit-summary.json') -Encoding utf8
    Write-Host ('Bounded exit metadata: ' + ($summary | ConvertTo-Json -Depth 6 -Compress))
}
if ($null -eq $testExit) { throw 'The test command did not return an exit code' }
if ($testExit -ne 0) { exit $testExit }
if ($diagnosticError -or -not $rawCleanup) { throw 'Diagnostic extraction or raw-trace cleanup failed; do not infer a valid comparison' }
$result = Read-TestResultContract -Path $invocation.report -StartedUtc $invocation.startedUtc -ExitCode 0 -AllowSkipped
Write-Host ('Validated ordinary UI result: ' + ($result | ConvertTo-Json -Compress))
if ((& git rev-parse HEAD).Trim() -cne $ExpectedSha) { throw 'Product source changed during the test' }
$global:LASTEXITCODE = 0
