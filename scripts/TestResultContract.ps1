function New-TestInvocation {
    param([string]$ResultsDirectory, [string]$TrxFileName)
    if ([IO.Path]::GetFileName($TrxFileName) -ne $TrxFileName -or $TrxFileName -notmatch '\.trx$') {
        throw "Expected a TRX file name without directories."
    }
    $id = [guid]::NewGuid().ToString('N')
    $directory = Join-Path ([IO.Path]::GetFullPath($ResultsDirectory)) $id
    New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
    [pscustomobject]@{
        id = $id
        startedUtc = [DateTime]::UtcNow.ToString('o')
        directory = $directory
        report = Join-Path $directory $TrxFileName
    }
}

function Read-TestResultContract {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][DateTime]$StartedUtc,
        [Parameter(Mandatory)][int]$ExitCode,
        [switch]$AllowSkipped
    )
    if ($ExitCode -ne 0) { throw "Test command failed with exit code $ExitCode." }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Expected TRX report is missing." }
    $file = Get-Item -LiteralPath $Path
    # Windows file timestamps can lag the high-resolution invocation clock.
    if ($file.LastWriteTimeUtc -lt $StartedUtc.ToUniversalTime().AddSeconds(-1)) { throw "TRX report is stale." }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($file.FullName, $settings)
    try {
        $trx = [Xml.XmlDocument]::new()
        $trx.XmlResolver = $null
        $trx.Load($reader)
    } finally { $reader.Dispose() }
    if ($trx.DocumentElement.LocalName -cne 'TestRun' -or
        $trx.DocumentElement.NamespaceURI -cne 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') {
        throw "Malformed TRX root or namespace."
    }
    $ns = [Xml.XmlNamespaceManager]::new($trx.NameTable)
    $ns.AddNamespace('t', $trx.DocumentElement.NamespaceURI)
    $times = $trx.SelectSingleNode('/t:TestRun/t:Times', $ns)
    if ($null -eq $times) { throw "Malformed TRX: missing invocation times." }
    foreach ($attribute in @('start', 'finish')) {
        $time = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($times.GetAttribute($attribute), [ref]$time) -or
            $time.UtcDateTime -lt $StartedUtc.ToUniversalTime().AddSeconds(-1) -or
            $time.UtcDateTime -gt [DateTime]::UtcNow.AddSeconds(1)) {
            throw "TRX $attribute time is missing, invalid, or stale."
        }
    }
    if ([DateTimeOffset]::Parse($times.GetAttribute('finish')) -lt [DateTimeOffset]::Parse($times.GetAttribute('start'))) {
        throw "Malformed TRX: finish precedes start."
    }
    $summary = $trx.SelectSingleNode('/t:TestRun/t:ResultSummary', $ns)
    $counters = $trx.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $ns)
    if ($null -eq $summary -or $null -eq $counters -or $summary.GetAttribute('outcome') -cnotin @('Passed', 'Completed')) {
        throw "TRX result summary is missing or unsuccessful."
    }
    $counts = @{}
    foreach ($name in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
        $value = 0
        if (-not [int]::TryParse($counters.GetAttribute($name), [ref]$value) -or $value -lt 0) {
            throw "Malformed TRX counter: $name."
        }
        $counts[$name] = $value
    }
    foreach ($attribute in $counters.Attributes) {
        if ($counts.ContainsKey($attribute.Name)) { continue }
        $value = 0
        if (-not [int]::TryParse($attribute.Value, [ref]$value) -or $value -ne 0) {
            throw "TRX has a nonzero or invalid auxiliary counter: $($attribute.Name)."
        }
    }
    $results = @($trx.SelectNodes('/t:TestRun/t:Results//t:UnitTestResult[not(t:InnerResults)]', $ns))
    $passed = @($results | Where-Object { $_.GetAttribute('outcome') -ceq 'Passed' }).Count
    $failed = @($results | Where-Object { $_.GetAttribute('outcome') -ceq 'Failed' }).Count
    $skipped = @($results | Where-Object { $_.GetAttribute('outcome') -ceq 'NotExecuted' }).Count
    $identities = @($results | ForEach-Object { $_.GetAttribute('executionId') } | Select-Object -Unique)
    if ($results.Count -eq 0 -or $counts.executed -eq 0) { throw "TRX reports zero actual execution (empty or all skipped)." }
    if ($identities.Count -ne $results.Count -or $identities -contains '') {
        throw "TRX result execution identities are missing or duplicated."
    }
    # VSTest can leave notExecuted at zero while emitting skipped result rows.
    # Leaf outcomes and total minus executed must still agree independently.
    if ($passed + $failed + $skipped -ne $results.Count -or $counts.total -ne $results.Count -or
        $counts.passed -ne $passed -or $counts.failed -ne $failed -or
        $counts.executed -ne $passed + $failed -or $counts.total - $counts.executed -ne $skipped -or
        ($counts.notExecuted -ne $skipped -and $counts.notExecuted -ne 0)) {
        throw "TRX leaf results and counters disagree."
    }
    if ($failed -gt 0) { throw "TRX contains failed tests." }
    if ($skipped -gt 0 -and -not $AllowSkipped) { throw "Every selected proof test must pass. Skips are not complete proof." }
    [pscustomobject][ordered]@{
        discovered = $results.Count
        executed = $counts.executed
        passed = $passed
        failed = $failed
        skipped = $skipped
        outcome = if ($skipped) { 'passed-with-permitted-skips' } else { 'passed' }
    }
}
