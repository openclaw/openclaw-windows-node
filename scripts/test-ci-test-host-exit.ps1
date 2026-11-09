<#
.SYNOPSIS
    Proves CI exit capture against native Windows processes and private synthetic traces.
.EXAMPLE
    pwsh -NoProfile -File ./scripts/test-ci-test-host-exit.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CiTestHostExit.ps1')
if (-not $IsWindows -or -not $env:RUNNER_TEMP -or -not (Test-Path -LiteralPath $env:ComSpec -PathType Leaf)) { throw 'Native exit tests require Windows, RUNNER_TEMP and the existing command processor.' }
$envelope = 'TpTrace Information: 0 : 100, 5, 2026/11/08, 23:10:00.100, 123456, vstest.console, '
$callback = 'TestHostManagerCallbacks.ExitCallBack: Testhost processId: 42 exited with exitcode: '
$line = $envelope + $callback + '0 error: SECRET-MUST-NOT-LEAK'
$counts = New-CiExitCounters
$positive = @(
    @{ Level = 'Information'; HostId = 42; Code = 0; Hex = '0x00000000'; Uncertain = $true }
    @{ Level = 'Error'; HostId = 42; Code = -1073741819; Hex = '0xC0000005'; Uncertain = $false }
    @{ Level = 'Error'; HostId = 42; Code = -1; Hex = '0xFFFFFFFF'; Uncertain = $true }
    @{ Level = 'Information'; HostId = -1; Code = 0; Hex = '0x00000000'; Uncertain = $true }
    @{ Level = 'Error'; HostId = -1; Code = 17; Hex = '0x00000011'; Uncertain = $false }
)
foreach ($case in $positive) {
    $sample = $envelope.Replace('Information', $case.Level) + $callback.Replace('42', [string]$case.HostId) + $case.Code + ' error: SECRET-MUST-NOT-LEAK'
    $actual = ConvertTo-CiHostExit $sample $counts
    $expectedHost = if ($case.HostId -eq -1) { $null } else { $case.HostId }
    if ($null -eq $actual -or $actual.runnerProcessId -ne 100 -or $actual.hostProcessId -ne $expectedHost -or $actual.hostProcessIdAvailable -ne ($case.HostId -gt 0) -or
        $actual.reportedExitCode -ne $case.Code -or $actual.reportedExitCodeHex -cne $case.Hex -or $actual.valueMayRepresentUnavailableCode -ne $case.Uncertain) { throw 'Reported numeric exit metadata changed.' }
    if (($actual | ConvertTo-Json -Compress).Contains('SECRET') -or @($actual.PSObject.Properties).Count -ne 6) { throw 'Exit record exposed unapproved fields.' }
}
if ($counts.accepted -ne 5 -or $counts.callbacks -ne 5 -or $counts.envelopes -ne 5 -or $counts.unavailableHostIds -ne 2 -or $counts.invalidHostIds -ne 0) { throw 'Unavailable host ID counters changed.' }
$reject = @(
    ('STDERR: ' + $line), ($envelope + 'AnotherProducer: ' + $callback + '0 error: hidden'), ($line + [char]10)
    $line.Replace('2026/11/08', '2026/99/99'), $line.Replace('100, 5,', '0, 5,'), $line.Replace('100, 5,', '100, 0,')
    $line.Replace('123456,', '9223372036854775808,'), $line.Replace('processId: 42', 'processId: 0'), $line.Replace('processId: 42', 'processId: -2')
    $line.Replace('processId: 42', 'processId: 2147483648'), $line.Replace('exitcode: 0', 'exitcode: 2147483648'), $line.Replace('exitcode: 0', 'exitcode: -2147483649')
)
$rejectedCounts = New-CiExitCounters
foreach ($sample in $reject) { if ($null -ne (ConvertTo-CiHostExit $sample $rejectedCounts)) { throw 'A lookalike or invalid numeric callback was accepted.' } }
if ($rejectedCounts.lines -ne 12 -or $rejectedCounts.unboundCallbacks -ne 2 -or $rejectedCounts.invalidEnvelopes -ne 4 -or $rejectedCounts.invalidHostIds -ne 3 -or $rejectedCounts.invalidExitCodes -ne 2 -or $rejectedCounts.accepted -ne 0) { throw 'Parser rejection counters changed.' }
foreach ($case in @('valid', 'empty', 'unexpected-file', 'record-limit', 'file-limit', 'byte-limit')) {
    $root = Join-Path $env:RUNNER_TEMP ("ci-exit-selftest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    try {
        switch ($case) {
            'valid' { Set-Content -LiteralPath (Join-Path $root 'testplatform.log') -Value $line }
            'unexpected-file' { Set-Content -LiteralPath (Join-Path $root 'SECRET.txt') -Value 'SECRET' }
            'record-limit' { Set-Content -LiteralPath (Join-Path $root 'testplatform.log') -Value (@($line) * 33) }
            'file-limit' { 1..25 | ForEach-Object { New-Item -ItemType File -Path (Join-Path $root "testplatform$_.log") | Out-Null } }
            'byte-limit' { $file = [IO.File]::Create((Join-Path $root 'testplatform.log')); try { $file.SetLength(33554433) } finally { $file.Dispose() } }
        }
        $summary = Complete-CiHostExitTrace -Root $root -NativeExitCode 17
        if (-not $summary.rawTraceRemoved -or (Test-Path -LiteralPath $root) -or $summary.nativeExitCode -ne 17 -or ($summary | ConvertTo-Json -Depth 5 -Compress).Contains('SECRET') -or
            [bool]$summary.diagnosticErrorType -ne ($case -notin @('valid', 'empty'))) { throw 'Trace bounds, redaction or cleanup failed.' }
        if ($case -eq 'valid' -and ($summary.records.Count -ne 1 -or $summary.counters.accepted -ne 1)) { throw 'Collector lost the accepted exit record.' }
        if ($case -eq 'empty' -and ($summary.records.Count -ne 0 -or $summary.counters.accepted -ne 0)) { throw 'An absent callback fabricated an exit record.' }
    } finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
}
$priorPreference = $PSNativeCommandUseErrorActionPreference
$priorExit = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
$priorValue = if ($null -ne $priorExit) { $priorExit.Value } else { $null }
try {
    foreach ($preference in @($false, $true)) {
        $PSNativeCommandUseErrorActionPreference = $preference
        foreach ($expected in @(0, 17)) {
            $LASTEXITCODE = 999 # Deliberately shadow the global variable native execution updates.
            $observed = $null; $threw = $false
            try { Invoke-CiNativeWithExit -ExitCode ([ref]$observed) -Command { & $env:ComSpec /d /c "exit $expected" } }
            catch { $threw = $true }
            if ($null -eq $observed -or $observed -ne $expected -or $threw -ne ($preference -and $expected -ne 0)) { throw 'Native 0/17 capture or error-preference behavior changed.' }
        }
        $global:LASTEXITCODE = 17
        $missing = Join-Path $env:RUNNER_TEMP ("ci-exit-missing-" + [guid]::NewGuid().ToString('N') + '.exe')
        $observed = $null; $launchFailed = $false
        try { Invoke-CiNativeWithExit -ExitCode ([ref]$observed) -Command { & $missing } }
        catch [Management.Automation.CommandNotFoundException] { $launchFailed = $true }
        if (-not $launchFailed -or $null -ne $observed) { throw 'Launch failure inherited or fabricated a native exit code.' }
    }
} finally {
    $PSNativeCommandUseErrorActionPreference = $priorPreference
    if ($null -eq $priorExit) { Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue } else { $global:LASTEXITCODE = $priorValue }
}
Write-Host 'CI host-exit self-tests passed: 5 accepted, 12 rejected, 6 trace-boundary and 6 native cases.'
