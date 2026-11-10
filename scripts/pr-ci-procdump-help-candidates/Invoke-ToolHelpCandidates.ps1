# ToolHelpCandidates: one pinned no-target ProcDump help observation. No fixture, CDB or capture path.
[CmdletBinding()]param([Parameter(Mandatory)][string]$ProductRoot,[Parameter(Mandatory)][string]$PipelineRoot,[switch]$CleanupOnly)
Set-StrictMode -Version Latest;$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$here=$PSScriptRoot;$shared=Join-Path (Split-Path $here) 'pr-ci-observe-only';$prep=Join-Path (Split-Path $here) 'pr-ci-capture-preparation'
try{$pins=Get-Content -LiteralPath (Join-Path $here 'identity.json') -Raw|ConvertFrom-Json -AsHashtable}catch{Write-Host 'help-candidates-identity-load-failed';exit 1}
$ProductCommit=$pins.productCommit;$ProductTree=$pins.productTree
if(-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64' -or $env:GITHUB_RUN_ID -cnotmatch '\A[1-9][0-9]*\z' -or $env:GITHUB_RUN_ATTEMPT -cne '1'){throw 'help-candidates-runner'}
$private=Join-Path $env:RUNNER_TEMP "openclaw-help-candidates-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$public=Join-Path $env:GITHUB_WORKSPACE 'tool-help-candidates-safe'
. (Join-Path $shared 'ProbeAdmission.ps1');. (Join-Path $shared 'ApplicationResolution.ps1');. (Join-Path $prep 'PrivateTools.ps1')
try{Add-Type -Path @((Join-Path $shared 'OwnedCommand.cs'),(Join-Path $shared 'BarrierGate.cs')) -ErrorAction Stop *> $null}catch{Write-Host 'help-candidates-owner-compile-failed';exit 1}
function Canonical([string]$Path) {
    $p = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'path-unbound' }
    for ($q = $p; $q; $q = [IO.Path]::GetDirectoryName($q)) {
        if ([IO.File]::GetAttributes($q).HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'path-reparse' }
    }
    return $p
}
function Sha([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function ReadBounded([string]$Path, [int]$Cap = 1048576) {
    if ((Get-Item -LiteralPath $Path).Length -gt $Cap) { throw 'input-oversize' }
    return [IO.File]::ReadAllText($Path)
}
function PrivateAcl([string]$Path) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($sid); $acl.SetAccessRuleProtection($true, $false)
    foreach ($who in @($sid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($who,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    $actual = Get-Acl -LiteralPath $Path
    $rules = @($actual.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    if (-not $actual.AreAccessRulesProtected -or $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $sid.Value -or $rules.Count -ne 2) { throw 'private-acl' }
    foreach ($rule in $rules) {
        if ($rule.IdentityReference.Value -notin @($sid.Value,'S-1-5-18') -or $rule.AccessControlType -ne 'Allow' -or $rule.FileSystemRights -ne 'FullControl') { throw 'private-acl' }
    }
}
function Binary([string]$Path,[string]$Role,[string]$Binding) {
    $path = Canonical $Path
    $v = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    return @{ role=$Role; sha256=(Sha $path); version="$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart).$($v.FilePrivatePart)"; binding=$Binding }
}
function MicrosoftSigned([string]$Path) {
    $s=Get-AuthenticodeSignature -LiteralPath $Path
    return $s.Status -eq 'Valid' -and $null -ne $s.SignerCertificate -and $s.SignerCertificate.Subject -match '(^|, )O=Microsoft Corporation(,|$)'
}
function IsX64([string]$Path) {
    $f=[IO.File]::OpenRead($Path)
    try { $b=[IO.BinaryReader]::new($f); if ($b.ReadUInt16() -ne 0x5a4d) { return $false }; $f.Position=0x3c; $offset=$b.ReadInt32(); if ($offset -lt 0 -or $offset -gt $f.Length-6) { return $false }; $f.Position=$offset; return $b.ReadUInt32() -eq 0x4550 -and $b.ReadUInt16() -eq 0x8664 } finally { $f.Dispose() }
}
function Set-ObserveSourceStep {
    param([ValidateSet('None','GitResolve','ProductHead','PipelineHead','ProductClean','PipelineClean','ProductTree','DotnetResolve','DotnetIdentity','DotnetVersion')][string]$Step)
    $script:sourceStep=$Step
    $script:commandWitness=[OpenClawObserve.CommandWitness]::new()
}
function Run([string]$Exe,[string[]]$Arguments,[int]$Seconds=900,[switch]$AllowFailure) {
    # A launch with no observed exit must not inherit the previous command's result.
    $key=if($AllowFailure){'probe'}else{'setup'}
    $script:nativeOutcomes[$key]=$null
    $script:commandWitness=[OpenClawObserve.CommandWitness]::new()
    $r=[OpenClawObserve.OwnedCommand]::Run($Exe,$Arguments,$ProductRoot,$script:child,$private,$Seconds,$script:commandWitness)
    return Receive-ObserveCommandResult -Result $r -Outcomes $script:nativeOutcomes -Probe:$AllowFailure
}
function CleanupPrivate {
    if(-not (Test-Path -LiteralPath $private)) { return $true }
    $null=Canonical $private
    $marker=(ReadBounded (Join-Path $private 'owner.json') 4096) | ConvertFrom-Json
    if($marker.productCommit -cne $ProductCommit -or $marker.run -cne $env:GITHUB_RUN_ID -or $marker.attempt -cne $env:GITHUB_RUN_ATTEMPT) { return $false }
    $jobs=@(Get-ChildItem -LiteralPath (Join-Path $private 'jobs') -Filter '*.job' -ErrorAction SilentlyContinue)
    if($jobs.Count -gt 40) { return $false }
    foreach($j in $jobs) {
        if($j.BaseName -notmatch '^[a-f0-9]{32}$') { return $false }
        $name=ReadBounded $j.FullName 128
        if($name -cne "Local\OpenClawObserve-$($j.BaseName)") { return $false }
        $joinedPath=[IO.Path]::ChangeExtension($j.FullName,'.joined')
        if(-not (Test-Path -LiteralPath $joinedPath) -or (ReadBounded $joinedPath 16) -cne 'joined') {
            if(-not [OpenClawObserve.OwnedCommand]::ExistingJobEmpty($name)) { return $false }
        }
    }
    Remove-Item -LiteralPath $private -Recurse -Force
    return -not (Test-Path -LiteralPath $private)
}
if($CleanupOnly){try{if(-not (CleanupPrivate)){throw 'cleanup-incomplete'};exit 0}catch{Write-Host 'help-candidates-cleanup-incomplete';exit 1}}
function New-CandidateObservation {
    return ('{"schema":1,"transportApproved":false,"behaviorVerified":false,"baseline":{"schema":1,"semantic":{"schema":1,"status":"not-observed","reason":"none","grammarId":null,"scope":"documented-help-forms","behaviorVerified":false,"inputSha256":null,"inputBytes":null,"encoding":"unknown","helpMajor":null,"helpMinor":null,"versionAgreement":null,"capabilities":{"helpNoTarget":false,"unhandledException":false,"nativeDebugger":false,"fullMemory":false,"dumpCount":false,"captureTimeout":false,"cancelGraceful":false,"acceptEula":false},"clauses":{"helpNoTarget":"unobserved","unhandledException":"unobserved","nativeDebugger":"unobserved","fullMemory":"unobserved","dumpCount":"unobserved","captureTimeout":"unobserved","cancelGraceful":"unobserved","acceptEula":"unobserved"}},"format":{"state":"unobserved","reason":"none","bom":"unobserved","kinds":{"NUL":false,"ESC":false,"FF":false,"otherCc":false,"Cf":false,"Cs":false,"Co":false,"Cn":false},"occurrences":"unobserved"},"stderr":{"state":"unobserved","presence":"unobserved"},"responseComparison":"unobserved"},"stdout":{"state":"unobserved","inputSha256":null,"inputBytes":null,"bom":"unobserved","candidates":[{"id":"utf8","state":"unobserved","bomPolicy":"unobserved","decode":"unobserved","roundTrip":"unobserved","kinds":{"NUL":false,"ESC":false,"FF":false,"otherCc":false,"Cf":false,"Cs":false,"Co":false,"Cn":false},"occurrences":"unobserved","limits":"unobserved","phases":{"versionCount":"unobserved","versionState":"unobserved","major":null,"minor":null,"build":null,"revision":null,"versionAgreement":null,"usageCount":"unobserved","usageForm":"unobserved","clauses":{"helpNoTarget":"unobserved","unhandledException":"unobserved","nativeDebugger":"unobserved","fullMemory":"unobserved","dumpCount":"unobserved","captureTimeout":"unobserved","cancelGraceful":"unobserved","acceptEula":"unobserved"}},"analysis":{"scope":"derived-analysis-input","encoding":"utf8","inputSha256":null,"inputBytes":null,"status":"unobserved","reason":"none"},"conformant":false},{"id":"utf16le","state":"unobserved","bomPolicy":"unobserved","decode":"unobserved","roundTrip":"unobserved","kinds":{"NUL":false,"ESC":false,"FF":false,"otherCc":false,"Cf":false,"Cs":false,"Co":false,"Cn":false},"occurrences":"unobserved","limits":"unobserved","phases":{"versionCount":"unobserved","versionState":"unobserved","major":null,"minor":null,"build":null,"revision":null,"versionAgreement":null,"usageCount":"unobserved","usageForm":"unobserved","clauses":{"helpNoTarget":"unobserved","unhandledException":"unobserved","nativeDebugger":"unobserved","fullMemory":"unobserved","dumpCount":"unobserved","captureTimeout":"unobserved","cancelGraceful":"unobserved","acceptEula":"unobserved"}},"analysis":{"scope":"derived-analysis-input","encoding":"utf8","inputSha256":null,"inputBytes":null,"status":"unobserved","reason":"none"},"conformant":false},{"id":"utf16be","state":"unobserved","bomPolicy":"unobserved","decode":"unobserved","roundTrip":"unobserved","kinds":{"NUL":false,"ESC":false,"FF":false,"otherCc":false,"Cf":false,"Cs":false,"Co":false,"Cn":false},"occurrences":"unobserved","limits":"unobserved","phases":{"versionCount":"unobserved","versionState":"unobserved","major":null,"minor":null,"build":null,"revision":null,"versionAgreement":null,"usageCount":"unobserved","usageForm":"unobserved","clauses":{"helpNoTarget":"unobserved","unhandledException":"unobserved","nativeDebugger":"unobserved","fullMemory":"unobserved","dumpCount":"unobserved","captureTimeout":"unobserved","cancelGraceful":"unobserved","acceptEula":"unobserved"}},"analysis":{"scope":"derived-analysis-input","encoding":"utf8","inputSha256":null,"inputBytes":null,"status":"unobserved","reason":"none"},"conformant":false}],"conformance":{"count":"none","id":null}},"stderr":{"state":"unobserved","inputSha256":null,"inputBytes":null,"bom":"unobserved","candidates":[{"id":"utf8","state":"unobserved","bomPolicy":"unobserved","decode":"unobserved","roundTrip":"unobserved","kinds":{"NUL":false,"ESC":false,"FF":false,"otherCc":false,"Cf":false,"Cs":false,"Co":false,"Cn":false},"occurrences":"unobserved","limits":"unobserved","phases":{"versionCount":"unobserved","versionState":"unobserved","major":null,"minor":null,"build":null,"revision":null,"versionAgreement":null,"usageCount":"unobserved","usageForm":"unobserved","clauses":{"helpNoTarget":"unobserved","unhandledException":"unobserved","nativeDebugger":"unobserved","fullMemory":"unobserved","dumpCount":"unobserved","captureTimeout":"unobserved","cancelGraceful":"unobserved","acceptEula":"unobserved"}},"analysis":{"scope":"derived-analysis-input","encoding":"utf8","inputSha256":null,"inputBytes":null,"status":"unobserved","reason":"none"},"conformant":false},{"id":"utf16le","state":"unobserved","bomPolicy":"unobserved","decode":"unobserved","roundTrip":"unobserved","kinds":{"NUL":false,"ESC":false,"FF":false,"otherCc":false,"Cf":false,"Cs":false,"Co":false,"Cn":false},"occurrences":"unobserved","limits":"unobserved","phases":{"versionCount":"unobserved","versionState":"unobserved","major":null,"minor":null,"build":null,"revision":null,"versionAgreement":null,"usageCount":"unobserved","usageForm":"unobserved","clauses":{"helpNoTarget":"unobserved","unhandledException":"unobserved","nativeDebugger":"unobserved","fullMemory":"unobserved","dumpCount":"unobserved","captureTimeout":"unobserved","cancelGraceful":"unobserved","acceptEula":"unobserved"}},"analysis":{"scope":"derived-analysis-input","encoding":"utf8","inputSha256":null,"inputBytes":null,"status":"unobserved","reason":"none"},"conformant":false},{"id":"utf16be","state":"unobserved","bomPolicy":"unobserved","decode":"unobserved","roundTrip":"unobserved","kinds":{"NUL":false,"ESC":false,"FF":false,"otherCc":false,"Cf":false,"Cs":false,"Co":false,"Cn":false},"occurrences":"unobserved","limits":"unobserved","phases":{"versionCount":"unobserved","versionState":"unobserved","major":null,"minor":null,"build":null,"revision":null,"versionAgreement":null,"usageCount":"unobserved","usageForm":"unobserved","clauses":{"helpNoTarget":"unobserved","unhandledException":"unobserved","nativeDebugger":"unobserved","fullMemory":"unobserved","dumpCount":"unobserved","captureTimeout":"unobserved","cancelGraceful":"unobserved","acceptEula":"unobserved"}},"analysis":{"scope":"derived-analysis-input","encoding":"utf8","inputSha256":null,"inputBytes":null,"status":"unobserved","reason":"none"},"conformant":false}],"conformance":{"count":"none","id":null}}}' | ConvertFrom-Json -AsHashtable)
}
function Get-RequestedWrapperExit([AllowNull()][Nullable[int]]$NativeExit,[AllowNull()][Nullable[int]]$SetupExit){
    if($null -ne $NativeExit -and $NativeExit -ne 0){return $NativeExit}
    if($null -ne $SetupExit -and $SetupExit -ne 0){return $SetupExit}
    return 1 # This mode never grants qualification, even when the observation is complete.
}
$tool=@{attempted=$false;identityState='unobserved';identityStable=$false;archiveSha256=$null;sha256=$null;version=$null;signatureVerified=$false;architectureMatched=$false;nativeExitCode=$null;lifecycleSucceeded=$false;stdoutSha256=$null;stderrSha256=$null}
$report=@{schema=1;mode='ToolHelpCandidates';captureEnabled=$false;captureAdmission='not-reviewed';qualificationPassed=$false;transportApproved=$false;formatEvidence=@{kind='reference';runId='42757425327';receiptSha256='2f487e663ecad79e3314af3fd90834eb5e4768d6a04d041de83ff0e7999945e4'};priorEvidence=@{kind='reference';runId=$pins.priorRunId;receiptSha256=$pins.priorReceiptSha256};productCommit=$ProductCommit;productTree=$ProductTree;pipelineCommit=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID;runAttempt=$env:GITHUB_RUN_ATTEMPT;stage='initialization';failure='observation-incomplete';observationComplete=$false;tool=$tool;observation=(New-CandidateObservation);setupExitCode=$null;requestedWrapperExitCode=1;failureWitness=$null;privacy=@{privateAclVerified=$false;outsideArtifactRoot=$false;explicitUploadOnly=$true};cleanup=@{ownedProcessesJoined=$false;privateRootDeleted=$false};dumpFiles=$null}
$nativeOutcomes=@{setup=$null;probe=$null};$observed=$false;$activeTool=$null;$firstUnexpectedExit=$null
$canonicalFunction=(Get-Item Function:Canonical).ScriptBlock;Set-ObserveSourceStep 'None'
try {
    $ProductRoot=Canonical $ProductRoot;$PipelineRoot=Canonical $PipelineRoot
    if((Canonical $here) -ine (Canonical (Join-Path $PipelineRoot 'scripts/pr-ci-procdump-help-candidates')) -or $pins.schema -ne 1 -or $pins.kind -cne 'observational-identity-pins'){throw 'pipeline-script-binding'}
    if($env:GITHUB_SHA -cnotmatch '\A[a-f0-9]{40}\z' -or (Test-Path -LiteralPath $private)){throw 'help-candidates-root'}
    $temp=Canonical $env:RUNNER_TEMP;$workspace=Canonical $env:GITHUB_WORKSPACE
    if(-not $private.StartsWith($temp+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $private.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'private-root'}
    $null=New-Item -ItemType Directory -Path $private;PrivateAcl $private;$report.privacy.privateAclVerified=$true;$report.privacy.outsideArtifactRoot=$true
    @{productCommit=$ProductCommit;run=$env:GITHUB_RUN_ID;attempt=$env:GITHUB_RUN_ATTEMPT}|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $private 'owner.json')
    foreach($name in @('raw','jobs','profile')){$null=New-Item -ItemType Directory -Path (Join-Path $private $name)}
    $child=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($e in [Environment]::GetEnvironmentVariables().GetEnumerator()){
        if($e.Key -match '^(VSTEST_DUMP|DOTNET_Dbg|COMPlus_Dbg|CORECLR_.*PROFIL|COR_.*PROFIL|DOTNET_STARTUP_HOOKS|VSTEST_HOST_DEBUG|VSTEST_DATACOLLECTOR_DEBUG)' -and $e.Value){throw 'inherited-instrumentation'}
        if($e.Key -notmatch '^OPENCLAW_' -and $e.Key -notmatch '^='){$child[$e.Key]=[string]$e.Value}
    }
    $child['DOTNET_DbgEnableMiniDump']='0';$child['COMPlus_DbgEnableMiniDump']='0';$child['OPENCLAW_REPO_ROOT']=$ProductRoot;$child['OPENCLAW_RELEASE_SOURCE_SHA']=$ProductCommit;$child['OPENCLAW_CI_COLLECT_COVERAGE']='false'
    foreach($name in @('OPENCLAW_TRAY_DATA_DIR','OPENCLAW_TRAY_APPDATA_DIR','OPENCLAW_TRAY_LOCALAPPDATA_DIR','OPENCLAW_TRAY_LOCAL_DATA_DIR')){$child[$name]=Join-Path $private "profile/$name"}
    $profile=Get-Content -LiteralPath (Join-Path $prep 'procdump-contract.json') -Raw|ConvertFrom-Json -AsHashtable
    if($profile.reviewed -isnot [bool] -or $profile.reviewed -or $null -ne $profile.version -or $null -ne $profile.sha256 -or $null -ne $profile.helpSha256 -or $null -ne $profile.helpExitCode){throw 'human-profile-state'}
    $report.stage='source';Set-ObserveSourceStep 'GitResolve'
    $git=Select-ObserveApplicationSource -Candidates @(Get-Command git -CommandType Application -TotalCount 1 -ErrorAction Stop) -CanonicalPath $canonicalFunction
    foreach($pair in @(@($ProductRoot,$ProductCommit,'ProductHead'),@($PipelineRoot,$env:GITHUB_SHA,'PipelineHead'))){
        Set-ObserveSourceStep $pair[2]
        $r=Run $git @('-C',$pair[0],'rev-parse','HEAD') 30
        if((ReadBounded $r.Stdout 256).Trim() -cne $pair[1]){throw 'source-mismatch'}
    }
    Set-ObserveSourceStep 'ProductTree';$r=Run $git @('-C',$ProductRoot,'rev-parse','HEAD^{tree}') 30;if((ReadBounded $r.Stdout 256).Trim() -cne $ProductTree){throw 'product-tree'}
    Set-ObserveSourceStep 'ProductClean';$null=Run $git @('-C',$ProductRoot,'diff','--exit-code','HEAD','--') 30
    Set-ObserveSourceStep 'PipelineClean';$null=Run $git @('-C',$PipelineRoot,'diff','--exit-code','HEAD','--') 30;Set-ObserveSourceStep 'None'
    $python=Select-ObserveApplicationSource -Candidates @(Get-Command python -CommandType Application -TotalCount 1 -ErrorAction Stop) -CanonicalPath $canonicalFunction
    $report.stage='acquire';$package=Get-PrivateProcDump (Join-Path $private 'procdump');$tool.archiveSha256=$package.ArchiveSha256
    if($tool.archiveSha256 -cne $pins.archiveSha256){$tool.identityState='archive-mismatch';$report.failure='identity-refused';throw 'archive-pin'}
    $report.stage='identity';$proc=Canonical $package.Path;$binary=Binary $proc 'procdump' 'file-inspected';$tool.sha256=$binary.sha256;$tool.version=$binary.version
    if($tool.sha256 -cne $pins.executableSha256){$tool.identityState='binary-mismatch';$report.failure='identity-refused';throw 'binary-pin'}
    if($tool.version -cne $pins.fileVersion){$tool.identityState='version-mismatch';$report.failure='identity-refused';throw 'version-pin'}
    $tool.signatureVerified=MicrosoftSigned $proc;$tool.architectureMatched=IsX64 $proc
    if(-not $tool.signatureVerified -or -not $tool.architectureMatched){$tool.identityState='untrusted';$report.failure='identity-refused';throw 'binary-trust'}
    $tool.identityState='matched';$report.stage='tool';$tool.attempted=$true;$activeTool=$tool
    $r=Run $proc (Get-NoTargetArguments 'procdump') $PreparationToolSeconds -AllowFailure
    $tool.nativeExitCode=$r.ExitCode;$tool.lifecycleSucceeded=$r.LifecycleSucceeded;$tool.stdoutSha256=Sha $r.Stdout;$tool.stderrSha256=Sha $r.Stderr
    if((Sha $proc) -cne $tool.sha256){throw 'binary-changed'};$tool.identityStable=$true
    $report.stage='project';$projection=Join-Path $private 'candidate-observation.json'
    $null=Run $python @((Join-Path $here 'project_candidates.py'),$r.Stdout,$r.Stderr,$tool.version,$projection) $PreparationToolSeconds
    $report.observation=(ReadBounded $projection 32768)|ConvertFrom-Json -AsHashtable
    if($null -ne $report.observation.baseline.semantic.inputSha256 -and $report.observation.baseline.semantic.inputSha256 -cne $tool.stdoutSha256){
        $empty=New-CandidateObservation;$report.observation.baseline.format=$empty.baseline.format;$report.observation.baseline.format.state='read-failed';$report.observation.baseline.format.reason='input-changed';$report.observation.baseline.responseComparison='unobserved'
        $report.observation.stdout=$empty.stdout;$report.observation.stdout.state='input-changed';foreach($c in $report.observation.stdout.candidates){$c.state='input-changed'}
        throw 'baseline-digest'
    }
    foreach($name in @('stdout','stderr')){
        if($report.observation[$name].state -ceq 'observed' -and $report.observation[$name].inputSha256 -cne $tool[$name+'Sha256']){
            $empty=New-CandidateObservation;$report.observation[$name]=$empty[$name];$report.observation[$name].state='input-changed';foreach($c in $report.observation[$name].candidates){$c.state='input-changed'}
            throw 'projection-digest'
        }
    }
    $activeTool=$null
    $null=Run $git @('-C',$ProductRoot,'diff','--exit-code','HEAD','--') 30
    $observed=$report.observation.stdout.state -ceq 'observed' -and $report.observation.stderr.state -ceq 'observed' -and $report.observation.baseline.responseComparison -cne 'unobserved'
    $report.stage='complete';$report.failure=if($observed){'qualification-unreviewed'}else{'observation-incomplete'}
} catch {
    $primaryError=$_.Exception
    $firstUnexpectedExit=Save-PreparationToolFailure -Tool $activeTool -Outcomes $nativeOutcomes -FirstExit $firstUnexpectedExit
    if($report.failure -cne 'identity-refused'){$report.failure='observation-incomplete'}
    try{$commandWitness.RecordManaged($primaryError);$report.failureWitness=@{sourceStep=$sourceStep;nativePhase=$commandWitness.Phase.ToString();category=$commandWitness.Category.ToString();win32ErrorAvailable=$commandWitness.Win32ErrorAvailable;win32Error=$commandWitness.Win32Error}}catch{}
} finally {
    if($tool.attempted){$tool.nativeExitCode=$nativeOutcomes.probe} # Sole tool result, never the later helper's setup exit.
    $report.setupExitCode=$nativeOutcomes.setup
    try{if(Test-Path -LiteralPath $private){$report.dumpFiles=@(Get-ChildItem -LiteralPath $private -Filter '*.dmp' -Recurse -File).Count;$report.cleanup.ownedProcessesJoined=CleanupPrivate;$report.cleanup.privateRootDeleted=$report.cleanup.ownedProcessesJoined}}catch{$report.cleanup.ownedProcessesJoined=$false;$report.cleanup.privateRootDeleted=$false}
    $report.observationComplete=[bool]($observed -and $tool.identityStable -and $tool.lifecycleSucceeded -and $report.failureWitness -eq $null -and $report.dumpFiles -eq 0 -and $report.cleanup.ownedProcessesJoined -and $report.cleanup.privateRootDeleted)
    $report.requestedWrapperExitCode=Get-RequestedWrapperExit -NativeExit $tool.nativeExitCode -SetupExit $report.setupExitCode
    try{$null=New-Item -ItemType Directory -Path $public -Force;$pending=Join-Path $public 'safe-report.pending';$report|ConvertTo-Json -Depth 16 -Compress|Set-Content -LiteralPath $pending
        & python (Join-Path $here 'validate_candidates.py') $pending (Join-Path $public 'safe-report.json');if($LASTEXITCODE -ne 0){$report.observationComplete=$false};Remove-Item -LiteralPath $pending -Force
    }catch{$report.observationComplete=$false}
}
exit $report.requestedWrapperExitCode
