# Synthetic private files only. No testhost, collector, debugger or other process is opened.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PrivateRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$cases=0;$nonce='a'*32
function Check-Receipt([bool]$Value) { if(-not $Value){throw 'observer-status-selftest'} }
function Case-Receipt([scriptblock]$Action) { & $Action;$script:cases++ }
function Fresh-Receipt([string]$Name) { $p=Join-Path $PrivateRoot $Name;$null=New-Item -ItemType Directory -Path $p;return $p }
function Good-Receipt {
    return [ordered]@{schema=1;invocation=$nonce;mode='ObserveOnly';captureEnabled=$false;hosts=1;cases=1;ends=1;sourceMatched=$true;probePassed=$true;sessionEnded=$true;bound=$true;failure='none';processRetained=$true;jobMatched=$true;hostPid=42;hostCreation=[long]123;jobName=('Local\OpenClawObserve-'+('b'*32));binaries=@(foreach($role in @('object-model','observer','collector-corelib','collector-coreclr','host-image','blame')){[ordered]@{role=$role;sha256=('c'*64);version='18.7.0.0';binding=$(if($role -ceq 'host-image'){'framework-query'}else{'loaded'})}})}
}
function Put-Receipt([string]$Root,$Value) { [IO.File]::WriteAllText((Join-Path $Root 'observer.json'),($Value|ConvertTo-Json -Depth 6 -Compress),[Text.UTF8Encoding]::new($false)) }
function Snapshot([string]$Root,[bool]$Joined=$true) { return Get-ObserveCollectorSnapshot -Root $Root -Attempted $true -WritersJoined $Joined -ExpectedInvocation $nonce }
if(Test-Path -LiteralPath $PrivateRoot){throw 'observer-status-selftest-root'}
$null=New-Item -ItemType Directory -Path $PrivateRoot
try {
    Case-Receipt {
        $r=Get-ObserveCollectorSnapshot -Root 'must-not-be-read' -Attempted $false -WritersJoined $true -ExpectedInvocation $null
        Check-Receipt ($r.Status.claim -ceq 'unobserved' -and $r.Status.receiptReadStatus -ceq 'not-inspected' -and -not $r.Status.writersJoined -and $null -eq $r.Receipt)
    }
    Case-Receipt { $r=Snapshot (Fresh-Receipt 'missing');Check-Receipt ($r.Status.final -ceq 'absent' -and $r.Status.receiptReadStatus -ceq 'missing' -and $null -eq $r.Status.failureReason) }
    Case-Receipt {
        $p=Fresh-Receipt 'active';Put-Receipt $p (Good-Receipt);$r=Snapshot $p $false
        Check-Receipt ($r.Status.final -ceq 'present' -and $r.Status.receiptReadStatus -ceq 'not-inspected' -and $null -eq $r.Receipt -and $null -eq $r.Status.failureReason)
        Check-Receipt (-not (Test-ObserveCollectorStatusReady $r.Status))
    }
    Case-Receipt {
        $p=Fresh-Receipt 'valid';Put-Receipt $p (Good-Receipt)
        [IO.File]::WriteAllText((Join-Path $p 'collector.claim'),'');[IO.File]::WriteAllText((Join-Path $p 'observer-ready.json'),'synthetic-presence')
        $r=Snapshot $p
        Check-Receipt (Test-ObserveCollectorStatusReady $r.Status)
        Check-Receipt ($r.Receipt.hosts -eq 1 -and $r.Status.failureReason -ceq 'none')
    }
    Case-Receipt {
        $p=Fresh-Receipt 'failed-init';$v=Good-Receipt;$v.bound=$false;$v.failure='initialization';$v.hosts=0;$v.cases=0;$v.ends=0;$v.hostPid=0;$v.hostCreation=0;$v.jobName='';$v.binaries=@();$v.processRetained=$false;$v.jobMatched=$false;$v.sourceMatched=$false;$v.sessionEnded=$false;$v.probePassed=$false
        Put-Receipt $p $v;[IO.File]::WriteAllText((Join-Path $p 'observer-invalid'),'invalid');$r=Snapshot $p
        Check-Receipt ($r.Status.receiptReadStatus -ceq 'validated' -and $r.Status.failureReason -ceq 'initialization' -and -not (Test-ObserveCollectorStatusReady $r.Status))
    }
    Case-Receipt { $p=Fresh-Receipt 'invalid-json';[IO.File]::WriteAllText((Join-Path $p 'observer.json'),'PRIVATE invalid json');$r=Snapshot $p;Check-Receipt ($r.Status.receiptReadStatus -ceq 'invalid-json' -and $null -eq $r.Status.failureReason) }
    Case-Receipt { $p=Fresh-Receipt 'oversize';[IO.File]::WriteAllText((Join-Path $p 'observer.json'),('x'*16385));$r=Snapshot $p;Check-Receipt ($r.Status.receiptReadStatus -ceq 'oversize' -and $null -eq $r.Receipt) }
    Case-Receipt { $p=Fresh-Receipt 'unknown-key';$v=Good-Receipt;$v['privatePath']='PRIVATE';Put-Receipt $p $v;$r=Snapshot $p;Check-Receipt ($r.Status.receiptReadStatus -ceq 'invalid-shape' -and $null -eq $r.Status.failureReason) }
    Case-Receipt { $p=Fresh-Receipt 'unknown-reason';$v=Good-Receipt;$v.bound=$false;$v.failure='PRIVATE';Put-Receipt $p $v;$r=Snapshot $p;Check-Receipt ($r.Status.receiptReadStatus -ceq 'invalid-shape' -and $null -eq $r.Status.failureReason) }
    Case-Receipt { $p=Fresh-Receipt 'wrong-nonce';$v=Good-Receipt;$v.invocation='d'*32;Put-Receipt $p $v;$r=Snapshot $p;Check-Receipt ($r.Status.receiptReadStatus -ceq 'invalid-shape') }
    Case-Receipt { $p=Fresh-Receipt 'bad-count';$v=Good-Receipt;$v.hosts=$true;Put-Receipt $p $v;$r=Snapshot $p;Check-Receipt ($r.Status.receiptReadStatus -ceq 'invalid-shape') }
    Case-Receipt {
        $p=Fresh-Receipt 'duplicate';$json=(Good-Receipt)|ConvertTo-Json -Depth 6 -Compress
        [IO.File]::WriteAllText((Join-Path $p 'observer.json'),$json.Replace('"schema":1','"schema":1,"schema":1'))
        Check-Receipt ((Snapshot $p).Status.receiptReadStatus -ceq 'invalid-shape')
    }
    Case-Receipt { $p=Fresh-Receipt 'directory';$null=New-Item -ItemType Directory -Path (Join-Path $p 'observer.json');$r=Snapshot $p;Check-Receipt ($r.Status.final -ceq 'unobserved' -and $r.Status.receiptReadStatus -ceq 'unreadable') }
    Case-Receipt {
        $p=Fresh-Receipt 'locked';Put-Receipt $p (Good-Receipt);$held=[IO.FileStream]::new((Join-Path $p 'observer.json'),[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
        try { $r=Snapshot $p;Check-Receipt ($r.Status.receiptReadStatus -ceq 'unreadable' -and $null -eq $r.Receipt) } finally { $held.Dispose() }
    }
    foreach($code in @(17,0,$null)) {
        Case-Receipt {
            $outcomes=@{probe=$code;setup=0};$witness=@{category='synthetic-primary'};$report=@{collector=(New-ObserveCollectorStatus);failure='preflight-incomplete';failureWitness=$witness}
            Save-ObserveCollectorSnapshot -Report $report -ReadSnapshot {throw 'PRIVATE secondary read failure'}
            Check-Receipt ([object]::ReferenceEquals($report.failureWitness,$witness) -and $report.failure -ceq 'preflight-incomplete' -and $outcomes.probe -eq $code)
            Check-Receipt (-not (Test-ObserveCollectorStatusReady $report.collector))
            $expected=if($code -eq 17){17}else{1}
            Check-Receipt ((Get-ObserveExitCode -Outcomes $outcomes -AdmissionPassed $false) -eq $expected)
        }
    }
    Case-Receipt {
        $report=@{collector=(New-ObserveCollectorStatus)}
        Save-ObserveCollectorSnapshot -Report $report -ReadSnapshot { [pscustomobject]@{Status=(New-ObserveCollectorStatus)};[pscustomobject]@{Status=(New-ObserveCollectorStatus)} }
        Check-Receipt ($report.collector.receiptReadStatus -ceq 'not-inspected')
    }
} finally { Remove-Item -LiteralPath $PrivateRoot -Recurse -Force }
[ordered]@{schema=1;cases=$cases;passed=$true;nativeProcessesOpened=0}
