# PrepareAndBarrier only. No debugger target, dump or ordinary UI run is reachable.
[CmdletBinding()]param([Parameter(Mandatory)][string]$ProductRoot,[Parameter(Mandatory)][string]$PipelineRoot,[switch]$CleanupOnly)
Set-StrictMode -Version Latest;$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue'
$ProductCommit='e56258fd0a835969b417f8d0236baf86760e3139';$ProductTree='22ba737e3122d589787701a16ee2e2bbd234bdb0'
$Fact='OpenClaw.Barrier.Fixture.BarrierFact.BodyReportsActualAckVisibility'
$BarrierCommandSeconds=120 # Three bounded fixtures, not the ordinary UI suite's 900-second invocation.
if(-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64' -or $env:GITHUB_RUN_ID -cnotmatch '\A[1-9][0-9]*\z' -or $env:GITHUB_RUN_ATTEMPT -cne '1'){throw 'preparation-runner'}
$private=Join-Path $env:RUNNER_TEMP "openclaw-preparation-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$public=Join-Path $env:GITHUB_WORKSPACE 'preparation-safe';$here=$PSScriptRoot
$shared=Join-Path (Split-Path $here) 'pr-ci-observe-only'
. (Join-Path $shared 'ProbeAdmission.ps1');. (Join-Path $shared 'ObserverReceipt.ps1');. (Join-Path $shared 'ApplicationResolution.ps1');. (Join-Path $here 'PrivateTools.ps1')
try {Add-Type -Path @((Join-Path $shared 'OwnedCommand.cs'),(Join-Path $shared 'BarrierGate.cs')) -ErrorAction Stop *> $null}catch{Write-Host 'preparation-owner-compile-failed';exit 1}
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
function DiscoverTool([string]$Role,[string[]]$Names,[string[]]$KnownPaths) {
    $found=@(foreach($n in $Names) { Get-Command $n -All -ErrorAction SilentlyContinue })
    $paths=@($found | Where-Object CommandType -eq Application | ForEach-Object Source)
    $paths+=@($KnownPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    $paths=@($paths | Sort-Object -Unique)
    $entry=@{role=$Role;status='missing';sha256=$null;version=$null;microsoftSignatureValid=$false;architectureMatched=$false;cliSchemaSha256=$null;existingTesthostBoundary='unverified'}
    if($paths.Count -eq 0) { return @{safe=$entry;path=$null} }
    if($paths.Count -ne 1 -or @($found | Where-Object CommandType -ne Application).Count -gt 0) { $entry.status='ambiguous';return @{safe=$entry;path=$null} }
    try {
        $p=Canonical $paths[0];$b=Binary $p $Role 'file-inspected'
        $entry.sha256=$b.sha256;$entry.version=$b.version;$entry.microsoftSignatureValid=MicrosoftSigned $p;$entry.architectureMatched=IsX64 $p
        $entry.status=if($entry.microsoftSignatureValid -and $entry.architectureMatched){'verified-file'}else{'untrusted'}
        return @{safe=$entry;path=$p}
    } catch { $entry.status='untrusted';return @{safe=$entry;path=$null} }
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
if($CleanupOnly){try{if(-not (CleanupPrivate)){throw 'cleanup-incomplete'};exit 0}catch{Write-Host 'preparation-cleanup-incomplete';exit 1}}
function CaseRecord([string]$Name){return @{mode=$Name;attempted=$false;sourceBound=$false;bodySawAck=$null;callbackHeld=$false;expectedAbort=$false;nativeExitCode=$null;joined=$false;executed=$null;passedCount=$null;skipped=$null;passed=$false}}
$report=@{schema=2;mode='PrepareAndBarrier';captureEnabled=$false;captureAdmission='not-reviewed';productCommit=$ProductCommit;productTree=$ProductTree;pipelineCommit=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID;runAttempt=$env:GITHUB_RUN_ATTEMPT;stage='initialization';failure='preparation-incomplete';preparePassed=$false;sdkVersion=$null;producerContract=$null;fixtureSha256=$null;observerSha256=$null;adapterSha256=$null;adapterVersion=$null;tools=@();winapp='unobserved';cases=@((CaseRecord 'async'),(CaseRecord 'sync'),(CaseRecord 'deny'));controls=@{baselineManaged=$false;baselinePowerShell=$false;barrierPolicy=$false;archivePolicy=$false};privacy=@{privateAclVerified=$false;outsideArtifactRoot=$false;explicitUploadOnly=$true};cleanup=@{ownedProcessesJoined=$false;privateRootDeleted=$false};dumpFiles=$null;setupExitCode=$null;failureWitness=$null}
$nativeOutcomes=@{setup=$null;probe=$null};$firstUnexpectedExit=$null;$allCases=$false;$toolContract=$false;$cdbStarted=$false;$activeTool=$null
$canonicalFunction=(Get-Item Function:Canonical).ScriptBlock
Set-ObserveSourceStep 'None'
try {
    $ProductRoot=Canonical $ProductRoot;$PipelineRoot=Canonical $PipelineRoot
    if((Canonical $here) -ine (Canonical (Join-Path $PipelineRoot 'scripts/pr-ci-capture-preparation'))){throw 'pipeline-script-binding'}
    if($env:GITHUB_SHA -cnotmatch '\A[a-f0-9]{40}\z' -or (Test-Path -LiteralPath $private)){throw 'preparation-source-root'}
    $temp=Canonical $env:RUNNER_TEMP;$workspace=Canonical $env:GITHUB_WORKSPACE
    if(-not $private.StartsWith($temp+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $private.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'private-root'}
    $null=New-Item -ItemType Directory -Path $private;PrivateAcl $private;$report.privacy.privateAclVerified=$true;$report.privacy.outsideArtifactRoot=$true
    @{productCommit=$ProductCommit;run=$env:GITHUB_RUN_ID;attempt=$env:GITHUB_RUN_ATTEMPT}|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $private 'owner.json')
    foreach($name in @('raw','jobs','build','packages','profile')){$null=New-Item -ItemType Directory -Path (Join-Path $private $name)}
    $child=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($e in [Environment]::GetEnvironmentVariables().GetEnumerator()){
        if($e.Key -match '^(VSTEST_DUMP|DOTNET_Dbg|COMPlus_Dbg|CORECLR_.*PROFIL|COR_.*PROFIL|DOTNET_STARTUP_HOOKS|VSTEST_HOST_DEBUG|VSTEST_DATACOLLECTOR_DEBUG)' -and $e.Value){throw 'inherited-instrumentation'}
        if($e.Key -notmatch '^OPENCLAW_' -and $e.Key -notmatch '^='){$child[$e.Key]=[string]$e.Value}
    }
    $child['DOTNET_DbgEnableMiniDump']='0';$child['COMPlus_DbgEnableMiniDump']='0';$child['OPENCLAW_REPO_ROOT']=$ProductRoot;$child['OPENCLAW_RELEASE_SOURCE_SHA']=$ProductCommit;$child['OPENCLAW_CI_COLLECT_COVERAGE']='false'
    foreach($name in @('OPENCLAW_TRAY_DATA_DIR','OPENCLAW_TRAY_APPDATA_DIR','OPENCLAW_TRAY_LOCALAPPDATA_DIR','OPENCLAW_TRAY_LOCAL_DATA_DIR')){$child[$name]=Join-Path $private "profile/$name"}
    $child['NUGET_PACKAGES']=Join-Path $private 'packages';$child['DOTNET_CLI_TELEMETRY_OPTOUT']='1';$child['MSBUILDDISABLENODEREUSE']='1';$child['DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER']='1';$child['GitVersion_NoNormalizeEnabled']='true';$child['GitVersion_NoFetchEnabled']='true'
    $resolver=@(& (Join-Path $shared 'Test-ApplicationResolution.ps1') -PrivateRoot (Join-Path $private 'resolver-selftest') -ProductRoot $ProductRoot -EntrypointPath $PSCommandPath -CanonicalPath $canonicalFunction)
    if($resolver.Count -ne 1 -or $resolver[0].cases -ne 26 -or $resolver[0].passed -ne $true){throw 'resolver-selftest'}
    Write-Host 'preparation-resolver-controls-passed:26'
    $report.stage='winapp';$win=DiscoverTool 'winapp' @('winapp') @();$report.winapp=$win.safe.status
    if($win.safe.status -eq 'verified-file'){foreach($arg in @(@('--version'),@('--cli-schema'),@('--help'),@('run','--help'))){$r=Run $win.path $arg 30;if($arg[0] -eq '--cli-schema'){$null=(ReadBounded $r.Stdout 2097152)|ConvertFrom-Json}}}
    $report.stage='source';Set-ObserveSourceStep 'GitResolve'
    $git=Select-ObserveApplicationSource -Candidates @(Get-Command git -CommandType Application -TotalCount 1 -ErrorAction Stop) -CanonicalPath $canonicalFunction
    foreach($pair in @(@($ProductRoot,$ProductCommit,'ProductHead'),@($PipelineRoot,$env:GITHUB_SHA,'PipelineHead'))){
        Set-ObserveSourceStep $pair[2]
        $r=Run $git @('-C',$pair[0],'rev-parse','HEAD') 30
        if((ReadBounded $r.Stdout 256).Trim() -cne $pair[1]){throw 'source-mismatch'}
    }
    Set-ObserveSourceStep 'ProductTree';$r=Run $git @('-C',$ProductRoot,'rev-parse','HEAD^{tree}') 30;if((ReadBounded $r.Stdout 256).Trim() -cne $ProductTree){throw 'product-tree'}
    Set-ObserveSourceStep 'ProductClean';$null=Run $git @('-C',$ProductRoot,'diff','--exit-code','HEAD','--') 30
    Set-ObserveSourceStep 'PipelineClean';$null=Run $git @('-C',$PipelineRoot,'diff','--exit-code','HEAD','--') 30
    Set-ObserveSourceStep 'DotnetResolve';$dotnet=Select-ObserveApplicationSource -Candidates @(Get-Command dotnet -CommandType Application -TotalCount 1 -ErrorAction Stop) -CanonicalPath $canonicalFunction
    if(-not (MicrosoftSigned $dotnet) -or -not (IsX64 $dotnet)){throw 'dotnet-identity'}
    Set-ObserveSourceStep 'DotnetVersion';$r=Run $dotnet @('--version') 30;$report.sdkVersion=(ReadBounded $r.Stdout 256).Trim();if($report.sdkVersion -cne '10.0.403'){throw 'sdk-unreviewed'};Set-ObserveSourceStep 'None'
    [xml]$props=Get-Content -LiteralPath (Join-Path $ProductRoot 'tests/Directory.Build.props') -Raw
    foreach($pair in @(@('Microsoft.NET.Test.Sdk','18.6.0'),@('xunit','2.9.3'),@('xunit.runner.visualstudio','3.1.5'))){$nodes=@($props.SelectNodes("/Project/ItemGroup/PackageReference[@Include='$($pair[0])']"));if($nodes.Count -ne 1 -or $nodes[0].Version -cne $pair[1]){throw 'fixture-package-source'}}
    $report.stage='policies';$archiveControls=@(& (Join-Path $here 'Test-PreparationPolicies.ps1') -PrivateRoot (Join-Path $private 'archive-selftest'))
    if($archiveControls.Count -ne 1 -or $archiveControls[0].cases -ne 26 -or $archiveControls[0].passed -ne $true){throw 'archive-selftest'};if($archiveControls[0].helpPolicyCases -ne 12){throw 'help-policy-selftest'};$report.controls.archivePolicy=$true
    $build=Join-Path $private 'build';Copy-Item -LiteralPath $shared -Destination $build -Recurse;Copy-Item -LiteralPath $here -Destination $build -Recurse
    Copy-Item -LiteralPath (Join-Path $ProductRoot 'global.json') -Destination $build;Copy-Item -LiteralPath (Join-Path $ProductRoot 'NuGet.Config') -Destination $build
    $sharedBuild=Join-Path $build 'pr-ci-observe-only';$prepBuild=Join-Path $build 'pr-ci-capture-preparation';$fixtureProject=Join-Path $prepBuild 'Fixture/Fixture.csproj';$selfProject=Join-Path $sharedBuild 'SelfTest/SelfTest.csproj'
    $report.stage='build';$null=Run $dotnet @('restore',$fixtureProject,'-r','win-x64','--disable-parallel');$null=Run $dotnet @('build',$fixtureProject,'-c','Release','-r','win-x64','--no-restore','-p:UseSharedCompilation=false','-nodeReuse:false')
    $r=Run $dotnet @('msbuild',$fixtureProject,'-nologo','-p:Configuration=Release','-p:RuntimeIdentifier=win-x64','-getProperty:VSTestConsolePath,MSBuildToolsPath,TargetPath,TargetFramework') 60
    $properties=((ReadBounded $r.Stdout 32768)|ConvertFrom-Json).Properties
    if($properties.TargetFramework -cne 'net10.0-windows10.0.22621.0'){throw 'fixture-framework'}
    $assembly=Canonical $properties.TargetPath;$sdkDir=Canonical $properties.MSBuildToolsPath;$console=Canonical $properties.VSTestConsolePath
    if([IO.Path]::GetDirectoryName($console) -ine $sdkDir -or [IO.Path]::GetFileName($assembly) -cne 'OpenClaw.Barrier.Fixture.dll'){throw 'fixture-or-console-path'}
    if([Diagnostics.FileVersionInfo]::GetVersionInfo($console).ProductVersion -notmatch '^18\.7\.\d+([+\- ].*)?$'){throw 'producer-unreviewed'};$report.producerContract='18.7'
    $model=Canonical (Join-Path $sdkDir 'Microsoft.VisualStudio.TestPlatform.ObjectModel.dll');$blame=Canonical (Join-Path $sdkDir 'Extensions/Microsoft.TestPlatform.Extensions.BlameDataCollector.dll')
    $expectedAssembly=Canonical (Join-Path $prepBuild 'Fixture/bin/Release/net10.0-windows10.0.22621.0/win-x64/OpenClaw.Barrier.Fixture.dll');if($assembly -ine $expectedAssembly){throw 'fixture-path'}
    $adapters=@(foreach($name in @('xunit.runner.visualstudio.testadapter.dll','xunit.runner.visualstudio.dotnetcore.testadapter.dll')){$path=Join-Path ([IO.Path]::GetDirectoryName($assembly)) $name;if(Test-Path -LiteralPath $path -PathType Leaf){Canonical $path}})
    if($adapters.Count -ne 1){throw 'adapter-ambiguity'};$adapterInfo=Binary $adapters[0] 'adapter' 'evaluated';$report.adapterSha256=$adapterInfo.sha256;$report.adapterVersion=$adapterInfo.version
    if($report.adapterVersion -cnotmatch '\A3\.1\.5\.[0-9]{1,10}\z'){throw 'adapter-version'}
    $report.fixtureSha256=Sha $assembly
    $null=Run $dotnet @('build',(Join-Path $prepBuild 'Collector/Collector.csproj'),'-c','Release',"-p:ObjectModelPath=$model",'-p:UseSharedCompilation=false','-nodeReuse:false')
    $null=Run $dotnet @('build',$selfProject,'-c','Release','-p:UseSharedCompilation=false','-nodeReuse:false')
    $r=Run $dotnet @((Join-Path $sharedBuild 'SelfTest/bin/Release/net10.0/SelfTest.dll'),(Join-Path $private 'contract-selftest')) 30
    $self=(ReadBounded $r.Stdout 4096)|ConvertFrom-Json
    if($self.checks -ne 93 -or $self.configurationCases -ne 24 -or $self.custodyCases -ne 12 -or $self.blameIdentityCases -ne 30 -or $self.nativeProcessesOpened -ne 0 -or $self.passed -ne $true){throw 'baseline-managed-controls'};$report.controls.baselineManaged=$true
    if($self.barrierPolicyCases -ne 19){throw 'barrier-policy-controls'};if($self.barrierDeadlineCases -ne 14){throw 'barrier-deadline-controls'};$report.controls.barrierPolicy=$true
    $r=Run ([Environment]::ProcessPath) @('-NoProfile','-NonInteractive','-File',(Join-Path $shared 'Test-ProbeAdmission.ps1'),'-ProductRoot',$ProductRoot,'-PrivateRoot',(Join-Path $private 'admission-selftest')) 30
    $control=(ReadBounded $r.Stdout 4096)|ConvertFrom-Json;if($control.policyCases -ne 5 -or $control.trxCases -ne 14 -or $control.passed -ne $true){throw 'baseline-admission-controls'}
    $control=@(& (Join-Path $shared 'Test-ObserverReceipt.ps1') -PrivateRoot (Join-Path $private 'status-selftest'));if($control.Count -ne 1 -or $control[0].cases -ne 18 -or $control[0].blameCases -ne 6 -or $control[0].passed -ne $true){throw 'baseline-status-controls'};$report.controls.baselinePowerShell=$true
    Write-Host 'preparation-controls-passed:93/24/12/30/19/26/18/6+19/26+14/12'
    $report.stage='tools';$package=Get-PrivateProcDump (Join-Path $private 'procdump');$proc=Canonical $package.Path
    $procInfo=Binary $proc 'procdump' 'file-inspected';if(-not (MicrosoftSigned $proc) -or -not (IsX64 $proc)){throw 'procdump-trust'}
    if($procInfo.version -cnotmatch '\A[0-9]{1,10}(\.[0-9]{1,10}){3}\z'){throw 'procdump-version'}
    $tool=@{role='procdump';status='unreviewed-contract';sha256=$procInfo.sha256;version=$procInfo.version;archiveSha256=$package.ArchiveSha256;helpSha256=$null;nativeExitCode=$null;signatureVerified=$true;architectureMatched=$true;helpProjection=(New-UnobservedProcDumpHelp)}
    $report.tools+=,$tool
    $activeTool=$tool
    $r=Run $proc (Get-NoTargetArguments 'procdump') $PreparationToolSeconds -AllowFailure;$tool.nativeExitCode=$r.ExitCode;$tool.helpSha256=Sha $r.Stdout
    if((Sha $proc) -cne $tool.sha256){throw 'procdump-changed'}
    $python=Select-ObserveApplicationSource -Candidates @(Get-Command python -CommandType Application -TotalCount 1 -ErrorAction Stop) -CanonicalPath $canonicalFunction
    $helpPath=Join-Path $private 'procdump-help-projection.json'
    $null=Run $python @((Join-Path $here 'project_procdump_help.py'),$r.Stdout,$tool.version,$helpPath) $PreparationToolSeconds
    $tool.helpProjection=(ReadBounded $helpPath 8192)|ConvertFrom-Json -AsHashtable
    if($null -ne $tool.helpProjection.inputSha256 -and $tool.helpProjection.inputSha256 -cne $tool.helpSha256){throw 'help-projection-digest'}
    $null=ReadBounded $r.Stderr $PreparationToolOutputBytes
    $profile=Get-Content -LiteralPath (Join-Path $here 'procdump-contract.json') -Raw|ConvertFrom-Json -AsHashtable
    $toolContract=Test-ReviewedProcDumpContract $profile $tool;if($toolContract){$tool.status='contract-reviewed'}elseif($r.ExitCode -ne 0){$firstUnexpectedExit=$r.ExitCode}
    $activeTool=$null
    $cdb=DiscoverTool 'cdb' @('cdb.exe') @((Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits/10/Debuggers/x64/cdb.exe'))
    $tool=@{role='cdb';status=$cdb.safe.status;sha256=$cdb.safe.sha256;version=$cdb.safe.version;archiveSha256=$null;helpSha256=$null;nativeExitCode=$null;signatureVerified=$cdb.safe.microsoftSignatureValid;architectureMatched=$cdb.safe.architectureMatched;helpProjection=$null};$report.tools+=,$tool
    if($cdb.safe.status -eq 'verified-file'){$activeTool=$tool;$r=Run $cdb.path (Get-NoTargetArguments 'cdb') $PreparationToolSeconds -AllowFailure;$tool.nativeExitCode=$r.ExitCode;$text=ReadBounded $r.Stdout $PreparationToolOutputBytes;$null=ReadBounded $r.Stderr $PreparationToolOutputBytes;$tool.helpSha256=Sha $r.Stdout;if((Sha $cdb.path) -cne $tool.sha256){throw 'cdb-changed'};$cdbStarted=$r.ExitCode -eq 0 -and $text -match [regex]::Escape($tool.version);$tool.status=if($cdbStarted){'cli-verified'}else{'unrecognized-output'};if($r.ExitCode -ne 0 -and $null -eq $firstUnexpectedExit){$firstUnexpectedExit=$r.ExitCode}}
    $activeTool=$null
    $collector=Canonical (Join-Path $prepBuild 'Collector/bin/Release/net8.0/OpenClaw.Barrier.Collector.dll');$report.observerSha256=Sha $collector
    $hostImage=Join-Path ([IO.Path]::GetDirectoryName($assembly)) 'testhost.exe';$hostHash=if(Test-Path $hostImage){Sha $hostImage}else{Sha $dotnet}
    foreach($case in $report.cases){
        $report.stage='barrier-'+$case.mode;$root=Join-Path $private ('barrier-'+$case.mode);$null=New-Item -ItemType Directory -Path $root;$nonce=[guid]::NewGuid().ToString('N')
        $child['OPENCLAW_BARRIER_ROOT']=$root;$child['OPENCLAW_BARRIER_NONCE']=$nonce;$child['OPENCLAW_BARRIER_MODE']=$case.mode
        [xml]$xml='<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount><TargetPlatform>x64</TargetPlatform><TargetFrameworkVersion>.NETCoreApp,Version=v10.0</TargetFrameworkVersion></RunConfiguration><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName="OpenClawBarrierProof" uri="datacollector://openclaw/barrier-proof/1"><Configuration /></DataCollector></DataCollectors></DataCollectionRunSettings></RunSettings>'
        if($case.mode -ne 'async'){$x=$xml.CreateElement('xUnit');$n=$xml.CreateElement('ParallelizeTestCollections');$n.InnerText='false';$null=$x.AppendChild($n);$null=$xml.DocumentElement.AppendChild($x)}
        $values=@{Root=$root;Invocation=$nonce;ExpectedSource=$assembly;ExpectedSourceSha256=(Sha $assembly);ExpectedObjectModelSha256=(Sha $model);ExpectedCollectorSha256=(Sha $collector);ExpectedHostImageSha256=$hostHash;ExpectedDotnetSha256=(Sha $dotnet);ExpectedBlameSha256=(Sha $blame)}
        foreach($key in $values.Keys){$n=$xml.CreateElement($key);$n.InnerText=$values[$key];$null=$xml.SelectSingleNode('//Configuration').AppendChild($n)}
        $settings=Join-Path $root 'barrier.runsettings';$xml.Save($settings);$raw=Join-Path $private ('raw/'+$case.mode);$started=[DateTime]::UtcNow
        $gate=[OpenClawObserve.BarrierGate]::new($root,$nonce,(Sha $assembly),(Sha $model),(Sha $collector),(Sha $blame),$case.mode)
        $case.attempted=$true;$commandWitness=[OpenClawObserve.CommandWitness]::new()
        $r=[OpenClawObserve.OwnedCommand]::RunBarrier($dotnet,@('exec',$console,$assembly,"/Settings:$settings","/TestAdapterPath:$([IO.Path]::GetDirectoryName($collector))","/TestCaseFilter:FullyQualifiedName=$Fact","/Logger:trx;LogFileName=barrier.trx","/ResultsDirectory:$raw",'/Blame:CollectHangDump;TestTimeout=300s;HangDumpType=None'),$ProductRoot,$child,$private,$BarrierCommandSeconds,$gate,$commandWitness)
        $case.nativeExitCode=$r.ExitCode;$case.joined=$r.Joined;$case.sourceBound=$gate.Entered;$case.bodySawAck=$gate.BodySawAck;$case.callbackHeld=$gate.HoldAcknowledged;$case.expectedAbort=$r.BarrierExpectedAbort
        $valid=$gate.ValidateAfterJoin($r.ExitCode,$r.LifecycleSucceeded,$r.BarrierExpectedAbort)
        $case.sourceBound=$gate.Entered;$case.bodySawAck=$gate.BodySawAck;$case.callbackHeld=$gate.HoldAcknowledged
        if(-not $valid){if($null -ne $r.ExitCode -and $r.ExitCode -ne 0 -and $null -eq $firstUnexpectedExit){$firstUnexpectedExit=$r.ExitCode};throw 'barrier-proof-failed'}
        if($case.mode -ne 'deny'){
            $counts=Read-ObserveProbeResult -ProductRoot $ProductRoot -Path (Join-Path $raw 'barrier.trx') -StartedUtc $started -ExitCode $r.ExitCode -ExpectedFact $Fact
            $case.executed=$counts.executed;$case.passedCount=$counts.passed;$case.skipped=$counts.skipped
            $snapshot=Get-ObserveCollectorSnapshot -Root $root -Attempted $true -WritersJoined $r.LifecycleSucceeded -ExpectedInvocation $nonce
            if(-not (Test-ObserveCollectorStatusReady $snapshot.Status) -or -not $snapshot.Receipt.bound -or $snapshot.Receipt.jobName -cne $r.JobName){throw 'barrier-receipt'}
        }
        $case.passed=$true
    }
    $null=Run $git @('-C',$ProductRoot,'diff','--exit-code','HEAD','--') 30
    $allCases=$true;$report.stage='complete';$report.failure=if(-not $toolContract){'tool-contract-unreviewed'}elseif(-not $cdbStarted){'cdb-unavailable'}else{'none'}
} catch {
    $primaryError=$_.Exception
    $firstUnexpectedExit=Save-PreparationToolFailure -Tool $activeTool -Outcomes $nativeOutcomes -FirstExit $firstUnexpectedExit
    $report.failure='preparation-incomplete'
    try{$commandWitness.RecordManaged($primaryError);$report.failureWitness=@{sourceStep=$sourceStep;nativePhase=$commandWitness.Phase.ToString();category=$commandWitness.Category.ToString();win32ErrorAvailable=$commandWitness.Win32ErrorAvailable;win32Error=$commandWitness.Win32Error}}catch{}
} finally {
    $report.setupExitCode=$nativeOutcomes.setup
    try{if(Test-Path -LiteralPath $private){$report.dumpFiles=@(Get-ChildItem -LiteralPath $private -Filter '*.dmp' -Recurse -File).Count;$report.cleanup.ownedProcessesJoined=CleanupPrivate;$report.cleanup.privateRootDeleted=$report.cleanup.ownedProcessesJoined}}catch{$report.cleanup.ownedProcessesJoined=$false;$report.cleanup.privateRootDeleted=$false}
    $report.preparePassed=[bool]($allCases -and $toolContract -and $cdbStarted -and $report.failure -eq 'none' -and $report.dumpFiles -eq 0 -and $report.cleanup.ownedProcessesJoined -and $report.cleanup.privateRootDeleted)
    try{$null=New-Item -ItemType Directory -Path $public -Force;$pending=Join-Path $public 'safe-report.pending';$report|ConvertTo-Json -Depth 12 -Compress|Set-Content -LiteralPath $pending
        & python (Join-Path $here 'validate_preparation.py') $pending (Join-Path $public 'safe-report.json');if($LASTEXITCODE -ne 0){$report.preparePassed=$false};Remove-Item -LiteralPath $pending -Force
    }catch{$report.preparePassed=$false}
}
if($null -ne $firstUnexpectedExit){exit $firstUnexpectedExit}
if($null -ne $nativeOutcomes.setup -and $nativeOutcomes.setup -ne 0){exit $nativeOutcomes.setup}
if($report.preparePassed){exit 0};exit 1
