# Pure synthetic receipt tests against the ACTUAL pinned product contract. Starts no process or app.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProductRoot, [Parameter(Mandatory)][string]$PrivateRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ProbeAdmission.ps1')
function Check([bool]$Value) { if(-not $Value){throw 'admission-selftest-failed'} }
$policyCases=0
foreach($case in @(
    @{probe=$true;code=17;lifecycle=$false;expected=17},
    @{probe=$true;code=0;lifecycle=$false;expected=1},
    @{probe=$false;code=17;lifecycle=$false;expected=17},
    @{probe=$true;code=$null;lifecycle=$false;expected=1},
    @{probe=$true;code=0;lifecycle=$true;expected=0}
)) {
    $outcomes=@{probe=$null;setup=$null};$rejected=$false
    try { $null=Receive-ObserveCommandResult -Result ([pscustomobject]@{ExitCode=$case.code;LifecycleSucceeded=$case.lifecycle}) -Outcomes $outcomes -Probe:$case.probe }
    catch { Check ($_.Exception.Message -ceq 'owned-lifecycle');$rejected=$true }
    Check ($rejected -eq (-not $case.lifecycle))
    $key=if($case.probe){'probe'}else{'setup'}
    Check ($outcomes[$key] -eq $case.code)
    Check ((Get-ObserveExitCode -Outcomes $outcomes -AdmissionPassed $case.lifecycle) -eq $case.expected)
    if($null -eq $case.code) { Check ($null -eq $outcomes.probe);Check ((Get-ObserveExitCode -Outcomes $outcomes -AdmissionPassed $true) -eq 1) }
    if($case.probe -and $case.code -eq 17) { $outcomes.setup=5;Check ((Get-ObserveExitCode -Outcomes $outcomes -AdmissionPassed $false) -eq 17) }
    $policyCases++
}

$namespace='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
$fact='Synthetic.ExpectedFact'
function Node([xml]$Doc,[string]$XPath) {
    $ns=[Xml.XmlNamespaceManager]::new($Doc.NameTable);$ns.AddNamespace('t',$namespace)
    return $Doc.SelectSingleNode($XPath,$ns)
}
$cases=@(
    @{name='valid';change={param($d,$start) $d};prefix=$null;exit=0},
    @{name='wrong-root';change={param($d,$start) [xml]$d.OuterXml.Replace('<TestRun','<NotATestRun').Replace('</TestRun>','</NotATestRun>')};prefix='Malformed TRX root or namespace.';exit=0},
    @{name='wrong-namespace';change={param($d,$start) [xml]$d.OuterXml.Replace('http://microsoft.com/schemas/VisualStudio/TeamTest/2010','urn:wrong')};prefix='Malformed TRX root or namespace.';exit=0},
    @{name='nonzero-skips';change={param($d,$start) (Node $d '/t:TestRun/t:ResultSummary/t:Counters').SetAttribute('notExecuted','99');$d};prefix='TRX leaf results and counters disagree.';exit=0},
    @{name='nonzero-timeout';change={param($d,$start) (Node $d '/t:TestRun/t:ResultSummary/t:Counters').SetAttribute('timeout','1');$d};prefix='TRX has a nonzero or invalid auxiliary counter: timeout.';exit=0},
    @{name='nonzero-error';change={param($d,$start) (Node $d '/t:TestRun/t:ResultSummary/t:Counters').SetAttribute('error','1');$d};prefix='TRX has a nonzero or invalid auxiliary counter: error.';exit=0},
    @{name='stale-start';change={param($d,$start) (Node $d '/t:TestRun/t:Times').SetAttribute('start',$start.AddSeconds(-5).ToString('o'));$d};prefix='TRX start time is missing, invalid, or stale.';exit=0},
    @{name='future-finish';change={param($d,$start) (Node $d '/t:TestRun/t:Times').SetAttribute('finish',[DateTime]::UtcNow.AddDays(1).ToString('o'));$d};prefix='TRX finish time is missing, invalid, or stale.';exit=0},
    @{name='reversed-times';change={param($d,$start) $t=Node $d '/t:TestRun/t:Times';$now=[DateTime]::UtcNow;$t.SetAttribute('start',$now.AddMilliseconds(300).ToString('o'));$t.SetAttribute('finish',$now.ToString('o'));$d};prefix='Malformed TRX: finish precedes start.';exit=0},
    @{name='missing-execution-id';change={param($d,$start) (Node $d '/t:TestRun/t:Results/t:UnitTestResult').RemoveAttribute('executionId');$d};prefix='TRX result execution identities are missing or duplicated.';exit=0},
    @{name='actual-skip';change={param($d,$start) $counts=Node $d '/t:TestRun/t:ResultSummary/t:Counters';$counts.SetAttribute('total','2');$counts.SetAttribute('notExecuted','1');$row=(Node $d '/t:TestRun/t:Results/t:UnitTestResult').CloneNode($true);$row.SetAttribute('executionId','execution-2');$row.SetAttribute('outcome','NotExecuted');$null=(Node $d '/t:TestRun/t:Results').AppendChild($row);$d};prefix='Every selected proof test must pass. Skips are not complete proof.';exit=0},
    @{name='wrong-fact';change={param($d,$start) (Node $d '/t:TestRun/t:Results/t:UnitTestResult').SetAttribute('testName','Synthetic.OtherFact');$d};prefix='probe-fact-mismatch';exit=0},
    @{name='native-nonzero';change={param($d,$start) $d};prefix='Test command failed with exit code 17.';exit=17},
    @{name='native-unknown';change={param($d,$start) $d};prefix='native-exit-unknown';exit=$null}
)
if(Test-Path -LiteralPath $PrivateRoot){throw 'admission-selftest-root-exists'}
$null=New-Item -ItemType Directory -Path $PrivateRoot
$trxCases=0
try {
    foreach($case in $cases) {
        $started=[DateTime]::UtcNow;$stamp=$started.ToString('o')
        [xml]$doc="<TestRun xmlns='$namespace'><Times start='$stamp' finish='$stamp'/><Results><UnitTestResult executionId='execution-1' testName='$fact' outcome='Passed'/></Results><ResultSummary outcome='Completed'><Counters total='1' executed='1' passed='1' failed='0' notExecuted='0' error='0' timeout='0'/></ResultSummary></TestRun>"
        $doc=& $case.change $doc $started
        $path=Join-Path $PrivateRoot "$trxCases.trx";$doc.Save($path)
        $rejected=$false
        try {
            $result=Read-ObserveProbeResult -ProductRoot $ProductRoot -Path $path -StartedUtc $started -ExitCode $case.exit -ExpectedFact $fact
            Check ($result.discovered -eq 1 -and $result.passed -eq 1 -and $result.executed -eq 1 -and $result.skipped -eq 0)
        } catch {
            Check ($null -ne $case.prefix -and $_.Exception.Message -ceq $case.prefix)
            $rejected=$true
        }
        Check ($rejected -eq ($null -ne $case.prefix))
        $trxCases++
    }
} finally { Remove-Item -LiteralPath $PrivateRoot -Recurse -Force }
[ordered]@{schema=1;policyCases=$policyCases;trxCases=$trxCases;passed=$true;nativeProcessesOpened=0}|ConvertTo-Json -Compress
