[CmdletBinding()]param([Parameter(Mandatory)][string]$PrivateRoot)
Set-StrictMode -Version Latest;$ErrorActionPreference='Stop';$cases=0;$helpCases=0
function Check([bool]$value){if(-not $value){throw 'preparation-policy-selftest'}}
function Case([scriptblock]$body){&$body;$script:cases++}
function TestReviewedProfile {return @{documentationCommit='d46b5bfa4b1151a3dbcf568152cb56a4a9f7c032';schema=1;reviewed=$true;version='12.2.0.0';sha256=('a'*64);helpSha256=('b'*64);helpExitCode=0}}
function TestHelpObservation {
    $capabilities=@{};$clauses=@{}
    foreach($key in @('helpNoTarget','unhandledException','nativeDebugger','fullMemory','dumpCount','captureTimeout','cancelGraceful','acceptEula')){$capabilities[$key]=$true;$clauses[$key]='recognized'}
    return @{version='12.2.0.0';sha256=('a'*64);helpSha256=('b'*64);nativeExitCode=0;helpProjection=@{schema=1;status='recognized';reason='none';grammarId='procdump-help-12.02-d46b5bfa';scope='documented-help-forms';behaviorVerified=$false;inputSha256=('b'*64);inputBytes=2108;encoding='utf8';helpMajor=12;helpMinor=2;versionAgreement=$true;capabilities=$capabilities;clauses=$clauses}}
}
function ArchiveCase([string[]]$Names,[bool]$Accept,[int]$Attributes=0){
    $memory=[IO.MemoryStream]::new();$archive=[IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
    try{foreach($name in $Names){$e=$archive.CreateEntry($name);$e.ExternalAttributes=$Attributes;$s=$e.Open();try{$s.WriteByte(1)}finally{$s.Dispose()}}}finally{$archive.Dispose()}
    $memory.Position=0;$archive=[IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Read)
    try{$rejected=$false;try{$plan=Get-PreparationArchivePlan $archive}catch{$rejected=$true};Check ($rejected -ne $Accept);if($Accept){Check ($plan.Count -eq 2)}}finally{$archive.Dispose();$memory.Dispose()}
}
Case {ArchiveCase @('procdump64.exe','Eula.txt','procdump.exe','procdump64a.exe') $true}
Case {ArchiveCase @('../procdump64.exe','Eula.txt') $false}
Case {ArchiveCase @('/procdump64.exe','Eula.txt') $false}
Case {ArchiveCase @('C:\procdump64.exe','Eula.txt') $false}
Case {ArchiveCase @('procdump64.exe','Eula.txt','PROCDUMP64.EXE') $false}
Case {ArchiveCase @('procdump64.exe','Eula.txt','unknown.exe') $false}
Case {ArchiveCase @('procdump.exe','Eula.txt') $false}
Case {ArchiveCase @('procdump64.exe/','Eula.txt') $false}
Case {Check (((Get-NoTargetArguments 'procdump') -join '|') -ceq '-accepteula|-?')}
Case {Check (((Get-NoTargetArguments 'cdb') -join '|') -ceq '-version')}
Case {$failed=$false;try{$null=Get-NoTargetArguments 'PRIVATE'}catch{$failed=$true};Check $failed}
Case {$p=TestReviewedProfile;$v=TestHelpObservation;Check (Test-ReviewedProcDumpContract $p $v);$p.reviewed=$false;Check (-not (Test-ReviewedProcDumpContract $p $v))}
Case {ArchiveCase @('procdump64.exe','Eula.txt') $false 1024}
Case {ArchiveCase @('procdump64.exe','Eula.txt') $false -1610612736}
Case {ArchiveCase @('procdump64.exe','Eula.txt') $false 16}
Case {$p=@{documentationCommit='d46b5bfa4b1151a3dbcf568152cb56a4a9f7c032';schema=1;reviewed=$true;version='12.2.0.0';sha256=('a'*64);helpSha256=('b'*64);helpExitCode=0};$v=TestHelpObservation;Check (Test-ReviewedProcDumpContract $p $v);$v.sha256='c'*64;Check (-not (Test-ReviewedProcDumpContract $p $v))}
Case {$p=@{documentationCommit='d46b5bfa4b1151a3dbcf568152cb56a4a9f7c032';schema=1;reviewed=$true;version=$null;sha256=$null;helpSha256=$null;helpExitCode=0};Check (-not (Test-ReviewedProcDumpContract $p @{version=$null;sha256=$null;helpSha256=$null;nativeExitCode=0}))}
Case {$p=TestReviewedProfile;$v=TestHelpObservation;$p.helpExitCode=1;$v.nativeExitCode=1;Check (Test-ReviewedProcDumpContract $p $v);$p.helpExitCode=$true;Check (-not (Test-ReviewedProcDumpContract $p $v))}
Case {ArchiveCase @('procdump64.exe','Eula.txt','x1','x2','x3','x4','x5','x6','x7') $false}
Case {
    $memory=[IO.MemoryStream]::new();$a=[IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {$e=$a.CreateEntry('procdump64.exe');$s=$e.Open();try{$bytes=[byte[]]::new(16777217);$s.Write($bytes,0,$bytes.Length)}finally{$s.Dispose()};$e=$a.CreateEntry('Eula.txt');$s=$e.Open();try{$s.WriteByte(1)}finally{$s.Dispose()}}finally{$a.Dispose()}
    $memory.Position=0;$a=[IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Read)
    try {$failed=$false;try{$null=Get-PreparationArchivePlan $a}catch{$failed=$true};Check $failed}finally{$a.Dispose();$memory.Dispose()}
}
Case {
    $p=@{documentationCommit='d46b5bfa4b1151a3dbcf568152cb56a4a9f7c032';schema=1;reviewed=$true;version='12.2.0.0';sha256=('a'*64);helpSha256=('b'*64);helpExitCode=0}|ConvertTo-Json|ConvertFrom-Json -AsHashtable
    Check (Test-ReviewedProcDumpContract $p (TestHelpObservation))
    $p['unknown']='PRIVATE';Check (-not (Test-ReviewedProcDumpContract $p (TestHelpObservation)))
}
Case {$tool=@{nativeExitCode=$null};$exit=Save-PreparationToolFailure $tool @{probe=17} $null;Check ($exit -eq 17 -and $tool.nativeExitCode -eq 17)}
Case {$tool=@{nativeExitCode=$null};$exit=Save-PreparationToolFailure $tool @{probe=0} $null;Check ($null -eq $exit -and $tool.nativeExitCode -eq 0)}
Case {$tool=@{nativeExitCode=$null};$exit=Save-PreparationToolFailure $tool @{probe=$null} $null;Check ($null -eq $exit -and $null -eq $tool.nativeExitCode)}
Case {$tool=@{nativeExitCode=$null};$exit=Save-PreparationToolFailure $tool @{probe=17} 19;Check ($exit -eq 19 -and $tool.nativeExitCode -eq 17)}
Case {
    $data=[Collections.Generic.Dictionary[string,object]]::new();$data['nativeExitCode']=$null
    $tool=[Collections.ObjectModel.ReadOnlyDictionary[string,object]]::new($data)
    $exit=Save-PreparationToolFailure $tool @{probe=17} $null;Check ($exit -eq 17)
}
function HelpCase([scriptblock]$body){&$body;$script:helpCases++}
foreach($key in @('helpNoTarget','unhandledException','nativeDebugger','fullMemory','dumpCount','captureTimeout','cancelGraceful','acceptEula')){
    HelpCase {$p=TestReviewedProfile;$v=TestHelpObservation;Check (Test-ReviewedProcDumpContract $p $v);$v.helpProjection.capabilities[$key]=$false;Check (-not (Test-ReviewedProcDumpContract $p $v))}
}
HelpCase {$p=TestReviewedProfile;$v=TestHelpObservation;Check (Test-ReviewedProcDumpContract $p $v);$v.helpProjection.status='unrecognized';$v.helpProjection.reason='unrecognized-clause';$v.helpProjection.capabilities.nativeDebugger=$false;$v.helpProjection.clauses.nativeDebugger='unrecognized';Check (-not (Test-ReviewedProcDumpContract $p $v))}
HelpCase {$p=TestReviewedProfile;$v=TestHelpObservation;Check (Test-ReviewedProcDumpContract $p $v);$p.version='13.0.0.0';$v.version='13.0.0.0';$v.helpProjection.status='unsupported';$v.helpProjection.reason='unsupported-version';$v.helpProjection.grammarId=$null;$v.helpProjection.helpMajor=13;$v.helpProjection.helpMinor=0;foreach($name in @($v.helpProjection.capabilities.Keys)){$v.helpProjection.capabilities[$name]=$false;$v.helpProjection.clauses[$name]='unobserved'};Check (-not (Test-ReviewedProcDumpContract $p $v))}
HelpCase {$p=TestReviewedProfile;$v=TestHelpObservation;Check (Test-ReviewedProcDumpContract $p $v);$v.helpProjection.behaviorVerified=$true;Check (-not (Test-ReviewedProcDumpContract $p $v))}
HelpCase {$p=TestReviewedProfile;$v=TestHelpObservation;Check (Test-ReviewedProcDumpContract $p $v);$v.helpProjection.inputSha256='c'*64;Check (-not (Test-ReviewedProcDumpContract $p $v))}
[ordered]@{schema=1;cases=$cases;helpPolicyCases=$helpCases;passed=$true;nativeProcessesOpened=0}
