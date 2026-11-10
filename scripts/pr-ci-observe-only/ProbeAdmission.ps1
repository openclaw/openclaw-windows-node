# Narrow ObserveOnly admission policies. No process launch, memory access or public raw diagnostics.
function Receive-ObserveCommandResult {
    param([Parameter(Mandatory)]$Result, [Parameter(Mandatory)][hashtable]$Outcomes, [switch]$Probe)
    $key = if ($Probe) { 'probe' } else { 'setup' }
    if ($null -ne $Result.ExitCode -and $Result.ExitCode -isnot [int]) { throw 'native-exit-type' }
    # Store the known value (or explicit unknown) BEFORE any secondary failure is raised.
    $Outcomes[$key] = $Result.ExitCode
    if ($Result.LifecycleSucceeded -isnot [bool] -or -not $Result.LifecycleSucceeded) { throw 'owned-lifecycle' }
    if ($null -eq $Result.ExitCode) { throw 'native-exit-unknown' }
    if ($Result.ExitCode -ne 0 -and -not $Probe) { throw 'native-step-failed' }
    return $Result
}

function Get-ObserveExitCode {
    param([Parameter(Mandatory)][hashtable]$Outcomes, [Parameter(Mandatory)][bool]$AdmissionPassed)
    if ($null -ne $Outcomes.probe -and $Outcomes.probe -ne 0) { return $Outcomes.probe }
    if ($null -ne $Outcomes.setup -and $Outcomes.setup -ne 0) { return $Outcomes.setup }
    if ($AdmissionPassed -and $null -ne $Outcomes.probe -and $Outcomes.probe -eq 0) { return 0 }
    return 1
}

function Read-ObserveProbeResult {
    param(
        [Parameter(Mandatory)][string]$ProductRoot,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][DateTime]$StartedUtc,
        [Parameter(Mandatory)][AllowNull()][Nullable[int]]$ExitCode,
        [Parameter(Mandatory)][string]$ExpectedFact
    )
    if ($null -eq $ExitCode) { throw 'native-exit-unknown' }
    if ((Get-Item -LiteralPath $Path).Length -gt 4194304) { throw 'probe-report-oversize' }
    # ProductRoot was bound to the exact product commit/tree by the caller. Do not duplicate this contract.
    . (Join-Path $ProductRoot 'scripts/TestResultContract.ps1')
    $counts = Read-TestResultContract -Path $Path -StartedUtc $StartedUtc -ExitCode $ExitCode
    if ($counts.discovered -ne 1 -or $counts.executed -ne 1 -or $counts.passed -ne 1 -or $counts.failed -ne 0 -or $counts.skipped -ne 0) {
        throw 'probe-result-count'
    }
    # The shared contract owns validity/coherence. Add only this probe's exact fact-name restriction.
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 4194304
    $reader = [Xml.XmlReader]::Create([IO.Path]::GetFullPath($Path), $settings)
    try {
        $trx = [Xml.XmlDocument]::new()
        $trx.XmlResolver = $null
        $trx.Load($reader)
    } finally { $reader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($trx.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $rows = @($trx.SelectNodes('/t:TestRun/t:Results//t:UnitTestResult[not(t:InnerResults)]', $ns))
    if ($rows.Count -ne 1 -or $rows[0].GetAttribute('testName') -cne $ExpectedFact) { throw 'probe-fact-mismatch' }
    return $counts
}
