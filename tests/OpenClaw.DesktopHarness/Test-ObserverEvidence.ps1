[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Compile native declarations, but never instantiate an observer or call a native API.
$sources = Get-ChildItem (Join-Path $PSScriptRoot 'Observation') -Filter '*.cs' |
    ForEach-Object FullName
Add-Type -Path @($sources; (Join-Path $PSScriptRoot '..\OpenClaw.DesktopHarness.Tests\ObserverEvidenceFake.cs'))
$checks = 0
function Assert-Evidence([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    $script:checks++
}
$native = [ObserverEvidenceFake]::new()
$native.OpenFailure = 5
$original = [ThreadOwnershipProbe]::Query($native, 824)
$native.OpenFailure = 87
$retry = [ThreadOwnershipProbe]::Query($native, 824)
Assert-Evidence ($original.OpenError -eq 5) 'Retry overwrote original access-denied error'
Assert-Evidence ($retry.OpenError -eq 87) 'Missing-thread retry lost its own error'
Assert-Evidence (!$original.Opened -and $original.OwnerPid -eq 0 -and $null -eq $original.CreatedFileTime) 'Denied open invented owner/lifetime'
Assert-Evidence ($original.Fields()['openError'] -eq 5 -and $retry.Fields()['openError'] -eq 87) 'Export merged original and retry errors'
$native.OpenFailure = 0
$native.Pid = 0
$native.OwnerFailure = 6
$native.TimesOk = $false
$native.TimesFailure = 299
$native.CloseFailure = 32
$attempt = [ThreadOwnershipProbe]::Query($native, 824)
Assert-Evidence ($attempt.OwnerError -eq 6) 'Timing/close clobbered original owner error'
Assert-Evidence ($attempt.TimesError -eq 299) 'Close clobbered timing error'
Assert-Evidence ($attempt.CloseError -eq 32) 'Close failure missing'
Assert-Evidence ($null -eq $attempt.CreatedFileTime) 'Failed timing query exposed invalid creation value'
$native.Pid = 456
$native.TimesOk = $true
$native.CloseFailure = 0
$success = [ThreadOwnershipProbe]::Query($native, 824)
Assert-Evidence ($success.OwnerPid -eq 456 -and $success.CreatedFileTime -eq 900) 'Retained handle did not preserve owner/lifetime'
Assert-Evidence ($null -eq $success.OpenError -and $null -eq $success.OwnerError -and $null -eq $success.TimesError) 'Success exported stale native errors'
Assert-Evidence ($original.OpenError -eq 5 -and $original.OwnerPid -eq 0) 'Later successful lookup retroactively resolved original failure'
$ledger = [QuietCaptureLedger]::new()
$show = $ledger.Callback(0x8002, [IntPtr]0x702B6, 0, 0, 824, 100, 101)
$destroy = $ledger.Callback(0x8001, [IntPtr]0x702B6, 0, 0, 824, 103, 104)
$reshow = $ledger.Callback(0x8002, [IntPtr]0x702B6, 0, 0, 999, 105, 106)
Assert-Evidence ($show['objectId'] -eq 0 -and $show['childId'] -eq 0 -and $show['idEventThread'] -eq 824) 'Raw callback operands lost'
Assert-Evidence ($destroy['pairedShowToDestroyMilliseconds'] -eq 3) 'SHOW/DESTROY interval lost'
Assert-Evidence ($reshow['priorDestroyMillisecondsSameHandleOnly'] -eq 103) 'Tombstone missing'
Assert-Evidence ($reshow['pairingIsSameHandleOnlyNotIdentityProof'] -eq $true) 'HWND reuse became identity proof'
$context = [ObserverCallbackContext]::new()
$outer = $context.Enter(4)
$nested = $context.Enter(5)
Assert-Evidence ($outer.Depth -eq 1 -and $null -eq $outer.ParentSequence) 'Outer callback acquired a false parent'
Assert-Evidence ($nested.Depth -eq 2 -and $nested.ParentSequence -eq 4) 'Reentrant callback lost its parent'
$outOfOrderRejected = $false
try { $outer.Dispose() } catch { $outOfOrderRejected = $true }
Assert-Evidence $outOfOrderRejected 'Out-of-order completion silently corrupted callback context'
$nested.Dispose()
$sibling = $context.Enter(6)
Assert-Evidence ($sibling.Depth -eq 2 -and $sibling.ParentSequence -eq 4) 'Nested completion lost the active outer callback'
$sibling.Dispose()
$outer.Dispose()
$outer.Dispose()
$next = $context.Enter(7)
Assert-Evidence ($next.Depth -eq 1 -and $null -eq $next.ParentSequence) 'Completed callback contaminated the next event'
$next.Dispose()
$script:tick = 100
$timed = [ThreadOwnershipProbe]::Query($native, 824, [Func[long]]{ $script:tick++; $script:tick })
Assert-Evidence ($timed.CallTicks['openBegin'] -eq 101 -and $timed.CallTicks['openEnd'] -eq 102) 'Original open interval lost'
Assert-Evidence ($timed.CallTicks['ownerBegin'] -eq 103 -and $timed.CallTicks['timesEnd'] -eq 106 -and $timed.CallTicks['closeEnd'] -eq 108) 'Retained-handle query intervals lost'
[pscustomobject]@{
    checks = $checks
    passed = $true
    nativeCallsExecuted = $false
    observerInstantiated = $false
    scope = 'per-call error preservation, retained lifetime evidence, callback/tombstone receipts'
} | ConvertTo-Json
