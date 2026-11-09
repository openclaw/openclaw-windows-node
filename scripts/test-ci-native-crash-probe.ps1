# Tests the public native-crash summary allowlist, without launching or downloading a debugger.
# Usage: .\scripts\test-ci-native-crash-probe.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-CiNativeCrashProbe.ps1')
$summary = ConvertTo-CiNativeCrashSummary -Lines @(
    '',
    'private output that must not be published',
    '00 00000000`00000001 00000000`00000002 secret!BeforeCapture',
    'OPENCLAW_CRASH_BEGIN',
    '',
    'ExceptionCode: c0000005 (Access violation)',
    'ExceptionInformation[1]: 0000000000000040',
    '00 00000000`00000001 00000000`00000002 Microsoft_UI_Xaml!DirectUI::Page::~Page+0x71',
    '   3 Id: 2a8c.60b8 Suspend: 1 Teb: 00000000`00000000 Unfrozen',
    '01 00000000`00000001 00000000`00000002 coreclr!FinalizerThread::FinalizerThreadStart+0x51 [private-source @ 10]',
    'private value: must-not-be-published',
    'OPENCLAW_CRASH_END',
    '00 00000000`00000001 00000000`00000002 secret!AfterCapture'
)
if (-not $summary.crashCaptured -or $summary.exceptionCodes.Count -ne 1 -or $summary.exceptionCodes[0] -cne 'c0000005') {
    throw 'The exception code was not retained.'
}
if ($summary.frames.Count -ne 2 -or $summary.frames[0].thread -cne 'exception' -or
    $summary.frames[0].symbol -cne 'Microsoft_UI_Xaml!DirectUI::Page::~Page+0x71' -or
    $summary.frames[1].thread -cne '60b8' -or
    $summary.frames[1].symbol -cne 'coreclr!FinalizerThread::FinalizerThreadStart+0x51') {
    throw 'Expected native function frames with thread attribution.'
}
$json = $summary | ConvertTo-Json -Depth 5
if ($json -match 'private|secret|ExceptionInformation|00000000') { throw 'Private data escaped the summary allowlist.' }
$none = ConvertTo-CiNativeCrashSummary -Lines @('normal test output')
if ($none.crashCaptured -or $none.frames.Count -or $none.exceptionCodes.Count) { throw 'A normal exit was classified as a crash.' }
$many = @('OPENCLAW_CRASH_BEGIN') + @(1..300 | ForEach-Object { '00 00000001 00000002 ntdll!Synthetic+0x1' }) + @('OPENCLAW_CRASH_END')
if ((ConvertTo-CiNativeCrashSummary -Lines $many).frames.Count -ne 256) { throw 'Frame output is not bounded.' }
$oldTemp = $env:RUNNER_TEMP
try {
    $env:RUNNER_TEMP = Join-Path (Get-Location).Path 'TestResults'
    $rejected = $false
    try {
        Invoke-CiNativeCrashProbe -TestArguments @('test') -ResultsDirectory $env:RUNNER_TEMP
    } catch {
        if ($_.Exception.Message -ne 'Private native diagnostics must be outside the checkout and artifact directory.') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'A public-artifact dump location was accepted.' }
} finally { $env:RUNNER_TEMP = $oldTemp }
Write-Host 'Native crash summary allowlist tests passed.'
