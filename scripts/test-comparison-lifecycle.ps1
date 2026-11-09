[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ComparisonLifecycleMetadata.ps1')

# Literal protocol fixture, not a call to the reader or production serializer.
$source = '3876d834ae5bbe2f75ef0a95b82120ddb55d6cfc'
$invocation = '11111111111111111111111111111111'
$ready = '{"schema":1,"sequence":1,"source":"ready","processId":42,"managedThreadId":7,"invocationId":"11111111111111111111111111111111","configuredSourceRevision":"3876d834ae5bbe2f75ef0a95b82120ddb55d6cfc","scopeId":0,"phase":"Idle","lastStartedDispatchId":0,"lastStartedDispatchState":"none","sourceDispatchId":0,"exceptionAvailable":false,"exceptionReadFailed":false,"exceptionType":null,"reportedHResult":null,"reportedHResultHex":null}'
$lf = [string][char]10
$script:checks = 0
function Assert-Policy([bool]$Value, [string]$Name) {
    if (-not $Value) { throw "Lifecycle self-test failed: $Name" }
}
function Convert-Fixture([string]$Text) {
    ConvertFrom-ComparisonLifecycleBytes -Bytes ([Text.Encoding]::UTF8.GetBytes($Text)) -ExpectedSource $source -InvocationId $invocation -HostProcessId 42
}
function Reject-Fixture([string]$Name, [byte[]]$Bytes) {
    $rejected = $false
    try { $null = ConvertFrom-ComparisonLifecycleBytes -Bytes $Bytes -ExpectedSource $source -InvocationId $invocation -HostProcessId 42 }
    catch [IO.InvalidDataException] {
        $rejected = $true
        Assert-Policy ($_.Exception.Message -ceq 'Lifecycle receipt rejected by its fixed schema or identity boundary.') "$Name fixed error"
        Assert-Policy ($null -eq $_.Exception.InnerException) "$Name no inner input payload"
    }
    Assert-Policy $rejected "$Name rejection"
    $script:checks++
}
function Change-Fixture([string]$Text, [hashtable]$Changes) {
    $row = ConvertFrom-Json -InputObject $Text -AsHashtable
    foreach ($key in $Changes.Keys) { $row[$key] = $Changes[$key] }
    ConvertTo-Json -InputObject $row -Compress -Depth 4
}

# The exit producer supplies typed Int32 values. Ambiguous/missing callbacks cannot bind a file.
$exitRecord = [pscustomobject]@{ hostProcessId = [int]42; reportedExitCode = [int]0 }
Assert-Policy ((Get-ComparisonLifecycleHost @($exitRecord, $exitRecord)) -eq 42) 'duplicate identical exit receipts bind one host'
$script:checks++
foreach ($inputRecords in @(
    @{name='missing-host'; rows=@()},
    @{name='different-hosts'; rows=@($exitRecord, [pscustomobject]@{hostProcessId=[int]43; reportedExitCode=[int]0})},
    @{name='contradictory-exits'; rows=@($exitRecord, [pscustomobject]@{hostProcessId=[int]42; reportedExitCode=[int]1})},
    @{name='untyped-host'; rows=@([pscustomobject]@{hostProcessId='42'; reportedExitCode=[int]0})},
    @{name='unavailable-host'; rows=@([pscustomobject]@{hostProcessId=[int]0; reportedExitCode=[int]0})})) {
    $rejected = $false
    try { $null = Get-ComparisonLifecycleHost -ExitMetadata $inputRecords.rows }
    catch { $rejected = $true }
    Assert-Policy $rejected $inputRecords.name
    $script:checks++
}

$body = Change-Fixture $ready @{ sequence=2; source='lifecycle'; scopeId=1; phase='ChatBody'; lastStartedDispatchId=5; lastStartedDispatchState='completed' }
$closed = Change-Fixture $body @{ sequence=3; source='closed'; phase='UiExited' }
$result = Convert-Fixture ($ready + $lf + $body + $lf + $closed + $lf)
Assert-Policy ($result.status -ceq 'validated' -and $result.recordCount -eq 3 -and $result.closed -and -not $result.overflowed) 'complete receipt'
Assert-Policy ($result.hostProcessId -eq 42 -and $result.sourceCommit -ceq $source -and $result.records[1].phase -ceq 'ChatBody') 'exact operational fields'
$script:checks++
$result = Convert-Fixture ($ready + $lf + $body + $lf)
Assert-Policy (-not $result.closed -and -not $result.overflowed -and $result.recordCount -eq 2) 'aborted receipt stays incomplete'
$script:checks++
$exception = Change-Fixture $body @{ source='WinUiUnhandled'; exceptionAvailable=$true; exceptionType='COMException'; reportedHResult=-2147467259; reportedHResultHex='0x80004005' }
$result = Convert-Fixture ($ready + $lf + $exception + $lf)
Assert-Policy ($result.records[1].reportedHResult -eq -2147467259 -and $result.records[1].reportedHResultHex -ceq '0x80004005') 'signed HRESULT'
$script:checks++
$getterFailed = Change-Fixture $body @{ source='ChatUnhandled'; exceptionReadFailed=$true }
$result = Convert-Fixture ($ready + $lf + $getterFailed + $lf)
Assert-Policy ($result.records[1].exceptionReadFailed -and -not $result.records[1].exceptionAvailable) 'failed projected getter remains unknown'
$script:checks++
$overflow = Change-Fixture $body @{ source='overflow' }
$result = Convert-Fixture ($ready + $lf + $overflow + $lf)
Assert-Policy ($result.overflowed -and -not $result.closed) 'overflow is explicit incomplete evidence'
$script:checks++

$bad = [ordered]@{
    'unknown-content-field' = $ready.Replace('"schema":1', '"secret":"SECRET-MUST-NOT-LEAK","schema":1') + $lf
    'duplicate-property' = $ready.Replace('"schema":1', '"schema":1,"schema":1') + $lf
    'escaped-duplicate-property' = $ready.Replace('"schema":1', '"schema":1,"sc\u0068ema":1') + $lf
    'case-alias' = $ready.Replace('"schema":1', '"Schema":1') + $lf
    'missing-property' = $ready.Replace('"scopeId":0,', '') + $lf
    'wrong-schema' = $ready.Replace('"schema":1', '"schema":2') + $lf
    'string-integer' = $ready.Replace('"schema":1', '"schema":"1"') + $lf
    'fractional-integer' = $ready.Replace('"schema":1', '"schema":1.5') + $lf
    'wrong-host' = $ready.Replace('"processId":42', '"processId":43') + $lf
    'zero-thread' = $ready.Replace('"managedThreadId":7', '"managedThreadId":0') + $lf
    'pipeline-not-product-sha' = $ready.Replace($source, '767354b54f0bd278c6f69c30ae43ee76aa9d67fe') + $lf
    'wrong-invocation' = $ready.Replace($invocation, '22222222222222222222222222222222') + $lf
    'later-host-change' = $ready + $lf + $body.Replace('"processId":42','"processId":43') + $lf
    'sequence-gap' = $ready + $lf + (Change-Fixture $body @{sequence=3}) + $lf
    'not-ready-first' = $body + $lf
    'second-ready' = $ready + $lf + (Change-Fixture $ready @{sequence=2}) + $lf
    'unknown-phase-content' = $ready + $lf + (Change-Fixture $body @{phase='SECRET-MUST-NOT-LEAK'}) + $lf
    'unknown-source' = $ready + $lf + (Change-Fixture $body @{source='UnknownSource'}) + $lf
    'string-boolean' = $ready.Replace('"exceptionAvailable":false','"exceptionAvailable":"false"') + $lf
    'negative-counter' = $ready + $lf + (Change-Fixture $body @{scopeId=-1}) + $lf
    'unsigned-hresult' = $ready + $lf + (Change-Fixture $exception @{reportedHResult=2147500037L}) + $lf
    'hresult-hex-mismatch' = $ready + $lf + (Change-Fixture $exception @{reportedHResultHex='0x80000000'}) + $lf
    'type-content' = $ready + $lf + (Change-Fixture $exception @{exceptionType='SECRET-MUST-NOT-LEAK / user data'}) + $lf
    'oversize-type' = $ready + $lf + (Change-Fixture $exception @{exceptionType=('A' * 129)}) + $lf
    'exception-without-type' = $ready + $lf + (Change-Fixture $exception @{exceptionType=$null}) + $lf
    'exception-getter-contradiction' = $ready + $lf + (Change-Fixture $exception @{exceptionReadFailed=$true}) + $lf
    'metadata-without-availability' = $ready + $lf + (Change-Fixture $exception @{exceptionAvailable=$false}) + $lf
    'lifecycle-exception-payload' = $ready + $lf + (Change-Fixture $exception @{source='lifecycle'}) + $lf
    'after-close' = $ready + $lf + $body + $lf + $closed + $lf + (Change-Fixture $body @{sequence=4}) + $lf
    'after-overflow' = $ready + $lf + $overflow + $lf + (Change-Fixture $body @{sequence=3}) + $lf
    'missing-final-newline' = $ready
    'blank-line' = $ready + $lf + $lf
    'malformed-json' = '{"secret":"SECRET-MUST-NOT-LEAK"' + $lf
    'array-root' = '[]' + $lf
    'nested-value' = $ready.Replace('"schema":1', '"schema":{"secret":{"nested":{"x":"SECRET-MUST-NOT-LEAK"}}}') + $lf
    'record-byte-cap' = (' ' * 1024) + $ready + $lf
}
foreach ($entry in $bad.GetEnumerator()) { Reject-Fixture $entry.Key ([Text.Encoding]::UTF8.GetBytes($entry.Value)) }
Reject-Fixture 'invalid-utf8' ([byte[]]@(255,10))
Reject-Fixture 'empty' ([byte[]]@())
Reject-Fixture 'file-byte-cap' ([byte[]]::new(2097153))
$many = [Text.StringBuilder]::new()
$null = $many.Append($ready).Append($lf)
for ($i = 2; $i -le 4096; $i++) { $null = $many.Append((Change-Fixture $body @{sequence=$i})).Append($lf) }
$result = Convert-Fixture ($many.ToString())
Assert-Policy ($result.recordCount -eq 4096 -and -not $result.closed) 'exact record cap is admitted without claiming completion'
$script:checks++
$null = $many.Append((Change-Fixture $body @{sequence=4097})).Append($lf)
Reject-Fixture 'record-count-cap' ([Text.Encoding]::UTF8.GetBytes($many.ToString()))

# Cheapest faithful file boundary: own temp file, positive read, then oversize/missing.
$tempParent = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$temp = Join-Path $tempParent ('comparison-lifecycle-selftest-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($temp)
try {
    $path = Join-Path $temp 'receipt.jsonl'
    [IO.File]::WriteAllText($path, $ready + $lf + $body + $lf, [Text.UTF8Encoding]::new($false))
    $result = Read-ComparisonLifecycle -Path $path -ExpectedSource $source -InvocationId $invocation -HostProcessId 42
    Assert-Policy ($result.recordCount -eq 2 -and -not $result.closed) 'actual bounded file reader'
    $script:checks++
    [IO.File]::WriteAllBytes($path, [byte[]]::new(2097153))
    $rejected = $false
    try { $null = Read-ComparisonLifecycle -Path $path -ExpectedSource $source -InvocationId $invocation -HostProcessId 42 }
    catch [IO.InvalidDataException] { $rejected = $true }
    Assert-Policy $rejected 'actual oversized file rejected before parse'
    $script:checks++
    [IO.File]::Delete($path)
    $rejected = $false
    try { $null = Read-ComparisonLifecycle -Path $path -ExpectedSource $source -InvocationId $invocation -HostProcessId 42 }
    catch { $rejected = $true }
    Assert-Policy $rejected 'missing receipt never admitted'
    $script:checks++
} finally { [IO.Directory]::Delete($temp, $true) }
if ($script:checks -ne 55) { throw 'Lifecycle self-test inventory did not execute exactly 55 checks' }
Write-Host "Lifecycle reader self-tests passed: $script:checks independent admission/privacy/file checks."
$global:LASTEXITCODE = 0
