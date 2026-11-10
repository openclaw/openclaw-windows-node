# Temporary non-memory probe only. Raw output never goes to the Actions console or upload path.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProductRoot, [Parameter(Mandatory)][string]$PipelineRoot, [switch]$CleanupOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ProductCommit = 'e56258fd0a835969b417f8d0236baf86760e3139'
$ProductTree = '22ba737e3122d589787701a16ee2e2bbd234bdb0'
$Fact = 'OpenClaw.Tray.UITests.AccessibilityFixtureIsolationTests.FixtureLaunchPathsAllUseTheIsolationHelper'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64' -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64' -or $env:GITHUB_RUN_ID -notmatch '^\d+$' -or $env:GITHUB_RUN_ATTEMPT -cne '1') { throw 'observe-runner-unbound' }
$private = Join-Path $env:RUNNER_TEMP "openclaw-observe-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$public = Join-Path $env:GITHUB_WORKSPACE 'observe-safe'
$here = $PSScriptRoot
. (Join-Path $here 'ProbeAdmission.ps1')
. (Join-Path $here 'ObserverReceipt.ps1')
. (Join-Path $here 'ApplicationResolution.ps1')
try { Add-Type -Path @((Join-Path $here 'OwnedCommand.cs'),(Join-Path $here 'BarrierGate.cs')) -ErrorAction Stop *> $null } catch { Write-Host 'observe-launcher-compile-failed'; exit 1 }
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
if($CleanupOnly) {
    try { if(-not (CleanupPrivate)) { throw 'cleanup-incomplete' }; exit 0 } catch { Write-Host 'observe-only-cleanup-incomplete';exit 1 }
}
$report=@{schema=4;collector=(New-ObserveCollectorStatus);failureWitness=$null;phase='ObserveOnly';stage='initialization';captureEnabled=$false;captureAdmission='not-reviewed';productCommit=$ProductCommit;productTree=$ProductTree;pipelineCommit=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID;runAttempt=$env:GITHUB_RUN_ATTEMPT;sdkVersion=$null;producerContract=$null;targetFrameworkMatched=$false;architecture='x64';binaries=@();tools=@();probe=@{attempts=0;executed=0;passed=0;skipped=0;nativeExitCode=$null;hosts=0;cases=0;sourceHashMatched=$false;handleRetained=$false;jobMatched=$false};privacy=@{privateAclVerified=$false;outsideArtifactRoot=$false;explicitUploadOnly=$true};cleanup=@{ownedProcessesJoined=$false;privateRootDeleted=$false};dumpFiles=$null;observePassed=$false;failure='initialization';setupExitCode=$null;limits=@('capture-not-reviewed','host-coreclr-not-remotely-observed')}
$nativeOutcomes=@{probe=$null;setup=$null};$probeExit=$null;$ready=$false
$observerRoot=$null;$invocation=$null;$probeWritersJoined=$false
Set-ObserveSourceStep 'None'
try {
    $ProductRoot=Canonical $ProductRoot;$PipelineRoot=Canonical $PipelineRoot
    if($env:GITHUB_SHA -notmatch '^[a-f0-9]{40}$' -or (Test-Path -LiteralPath $private)) { throw 'source-or-root' }
    $temp=Canonical $env:RUNNER_TEMP;$workspace=Canonical $env:GITHUB_WORKSPACE
    if(-not $private.StartsWith($temp+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $private.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'private-root' }
    $null=New-Item -ItemType Directory -Path $private
    PrivateAcl $private;$report.privacy.privateAclVerified=$true;$report.privacy.outsideArtifactRoot=$true
    @{productCommit=$ProductCommit;run=$env:GITHUB_RUN_ID;attempt=$env:GITHUB_RUN_ATTEMPT}|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $private 'owner.json')
    foreach($n in @('raw','jobs','observer','build','packages','profile')) {$null=New-Item -ItemType Directory -Path (Join-Path $private $n)}
    $child=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($e in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
        if($e.Key -match '^(VSTEST_DUMP|DOTNET_Dbg|COMPlus_Dbg|CORECLR_.*PROFIL|COR_.*PROFIL|DOTNET_STARTUP_HOOKS|VSTEST_HOST_DEBUG|VSTEST_DATACOLLECTOR_DEBUG)') { if($e.Value) { throw 'inherited-instrumentation' } }
        if($e.Key -notmatch '^OPENCLAW_' -and $e.Key -notmatch '^=') { $child[$e.Key]=[string]$e.Value }
    }
    $child['OPENCLAW_REPO_ROOT']=$ProductRoot;$child['OPENCLAW_RELEASE_SOURCE_SHA']=$ProductCommit;$child['OPENCLAW_CI_COLLECT_COVERAGE']='false'
    foreach($n in @('OPENCLAW_TRAY_DATA_DIR','OPENCLAW_TRAY_APPDATA_DIR','OPENCLAW_TRAY_LOCALAPPDATA_DIR','OPENCLAW_TRAY_LOCAL_DATA_DIR')) {$child[$n]=Join-Path $private "profile/$n"}
    $child['NUGET_PACKAGES']=Join-Path $private 'packages';$child['DOTNET_CLI_TELEMETRY_OPTOUT']='1';$child['MSBUILDDISABLENODEREUSE']='1';$child['DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER']='1';$child['GitVersion_NoNormalizeEnabled']='true';$child['GitVersion_NoFetchEnabled']='true'
    # Pure source-bound resolver controls run in this process, before any owned command or WinApp discovery.
    $resolverSelftest=@(& (Join-Path $here 'Test-ApplicationResolution.ps1') -PrivateRoot (Join-Path $private 'resolver-selftest') -ProductRoot $ProductRoot -EntrypointPath (Join-Path $here 'Invoke-ObserveOnly.ps1') -CanonicalPath ${function:Canonical})
    if($resolverSelftest.Count -ne 1 -or $resolverSelftest[0].schema -ne 1 -or $resolverSelftest[0].cases -ne 26 -or $resolverSelftest[0].passed -isnot [bool] -or -not $resolverSelftest[0].passed -or $resolverSelftest[0].nativeProcessesOpened -ne 0) {throw 'resolver-selftest'}
    Write-Host 'observe-resolver-controls-passed:26'
    # WinApp FIRST: metadata/help only on the verified file. Never run an application or DevTools.
    $report.stage='winapp'
    $win=DiscoverTool 'winapp' @('winapp') @();$report.tools+=,$win.safe
    if($win.safe.status -eq 'verified-file') {
        foreach($arg in @(@('--version'),@('--cli-schema'),@('--help'),@('run','--help'))) {
            $r=Run $win.path $arg 30
            if($arg[0] -eq '--cli-schema') { $null=(ReadBounded $r.Stdout 2097152)|ConvertFrom-Json;$win.safe.cliSchemaSha256=Sha $r.Stdout }
        }
        # Discovery does not assert that a newer release has a reviewed attach capability.
        if($win.safe.version -match '^0\.8\.') {$win.safe.existingTesthostBoundary='unsupported-by-reviewed-contract'}
    } else { $report.limits+='winapp-unavailable-or-untrusted' }
    $report.stage='source-binding'
    Set-ObserveSourceStep 'GitResolve'
    $git=Select-ObserveApplicationSource -Candidates @(Get-Command git -CommandType Application -TotalCount 1 -ErrorAction Stop) -CanonicalPath ${function:Canonical}
    foreach($pair in @(@($ProductRoot,$ProductCommit,'ProductHead'),@($PipelineRoot,$env:GITHUB_SHA,'PipelineHead'))) {
        Set-ObserveSourceStep $pair[2]
        $r=Run $git @('-C',$pair[0],'rev-parse','HEAD') 30
        if((ReadBounded $r.Stdout 256).Trim() -cne $pair[1]) { throw 'source-mismatch' }
    }
    Set-ObserveSourceStep 'ProductClean'
    $null=Run $git @('-C',$ProductRoot,'diff','--exit-code','HEAD','--') 30
    Set-ObserveSourceStep 'PipelineClean'
    $null=Run $git @('-C',$PipelineRoot,'diff','--exit-code','HEAD','--') 30
    Set-ObserveSourceStep 'ProductTree'
    $r=Run $git @('-C',$ProductRoot,'rev-parse','HEAD^{tree}') 30
    if((ReadBounded $r.Stdout 256).Trim() -cne $ProductTree) { throw 'tree-mismatch' }
    Set-ObserveSourceStep 'DotnetResolve'
    $dotnet=Select-ObserveApplicationSource -Candidates @(Get-Command dotnet -CommandType Application -TotalCount 1 -ErrorAction Stop) -CanonicalPath ${function:Canonical}
    Set-ObserveSourceStep 'DotnetIdentity'
    if(-not (MicrosoftSigned $dotnet) -or -not (IsX64 $dotnet)) {throw 'dotnet-identity'}
    Set-ObserveSourceStep 'DotnetVersion'
    $r=Run $dotnet @('--version') 30;$sdk=(ReadBounded $r.Stdout 256).Trim()
    if($sdk -notmatch '^10\.0\.\d{3}$') { throw 'sdk-contract' };$report.sdkVersion=$sdk
    Set-ObserveSourceStep 'None'
    $report.stage='ui-build'
    $ui=Join-Path $ProductRoot 'tests/OpenClaw.Tray.UITests/OpenClaw.Tray.UITests.csproj'
    $null=Run $dotnet @('restore',$ui,'-r','win-x64','--disable-parallel')
    $null=Run $dotnet @('build',$ui,'-c','Debug','-r','win-x64','--no-restore','-p:UseSharedCompilation=false','-nodeReuse:false')
    $report.stage='sdk-evaluation'
    $r=Run $dotnet @('msbuild',$ui,'-nologo','-p:Configuration=Debug','-p:RuntimeIdentifier=win-x64','-getProperty:VSTestConsolePath,VSTestTaskAssemblyFile,TargetFramework,TargetFrameworks,TargetPath,MSBuildToolsPath') 60
    $properties=((ReadBounded $r.Stdout 32768)|ConvertFrom-Json).Properties
    if($properties.TargetFramework -cne 'net10.0-windows10.0.22621.0' -or $properties.TargetFrameworks) { throw 'target-framework' };$report.targetFrameworkMatched=$true
    $assembly=Canonical $properties.TargetPath
    $expected=Join-Path $ProductRoot 'tests/OpenClaw.Tray.UITests/bin/Debug/net10.0-windows10.0.22621.0/win-x64/OpenClaw.Tray.UITests.dll'
    if($assembly -ine (Canonical $expected)) { throw 'target-path' }
    $console=Canonical $properties.VSTestConsolePath;$sdkDir=Canonical $properties.MSBuildToolsPath
    if([IO.Path]::GetDirectoryName($console) -ine $sdkDir) { throw 'console-override' }
    $version=[Diagnostics.FileVersionInfo]::GetVersionInfo($console).ProductVersion
    $producerMatch=[regex]::Match($version,'^18\.(6|7)\.\d+([+\- ].*)?$')
    if(-not $producerMatch.Success) { throw 'producer-version' }
    $report.producerContract='18.'+$producerMatch.Groups[1].Value
    $model=Canonical (Join-Path $sdkDir 'Microsoft.VisualStudio.TestPlatform.ObjectModel.dll')
    $blame=Canonical (Join-Path $sdkDir 'Extensions/Microsoft.TestPlatform.Extensions.BlameDataCollector.dll')
    $taskPath=$properties.VSTestTaskAssemblyFile
    if(-not [IO.Path]::IsPathFullyQualified($taskPath)) {$taskPath=Join-Path $sdkDir $taskPath}
    $report.binaries+=,(Binary $console 'console' 'evaluated')
    $report.binaries+=,(Binary $taskPath 'build-task' 'evaluated')
    $report.binaries+=,(Binary $assembly 'ui-assembly' 'evaluated')
    $report.binaries+=,(Binary (Join-Path ([IO.Path]::GetDirectoryName($assembly)) 'testhost.dll') 'testhost-assembly' 'evaluated')
    # Standard source-selected WindowsAppRuntime prerequisite, not analysis-tool acquisition.
    $report.stage='runtime-prerequisite'
    [xml]$props=Get-Content -LiteralPath (Join-Path $ProductRoot 'Directory.Build.props') -Raw
    $runtimeVersion=$props.SelectSingleNode('/Project/PropertyGroup/MicrosoftWindowsAppSDKVersion').InnerText.Trim()
    if($runtimeVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'runtime-version' }
    $channel=[regex]::Match($runtimeVersion,'^\d+\.\d+').Value
    $runtimeExe=Join-Path $private 'runtime-install.exe'
    Invoke-WebRequest -Uri "https://aka.ms/windowsappsdk/$channel/$runtimeVersion/windowsappruntimeinstall-x64.exe" -OutFile $runtimeExe
    if((Get-Item $runtimeExe).Length -gt 268435456 -or -not (MicrosoftSigned $runtimeExe)) { throw 'runtime-installer' }
    $null=Run $runtimeExe @('--quiet')
    $report.stage='analysis-tool-files'
    $cdbPaths=@((Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits/10/Debuggers/x64/cdb.exe'))
    foreach($t in @((DiscoverTool 'cdb' @('cdb.exe') $cdbPaths),(DiscoverTool 'procdump' @('procdump64.exe') @()))) {
        $report.tools+=,$t.safe;if($t.safe.status -ne 'verified-file') {$report.limits+=($t.safe.role+'-preparation-required')}
    }
    $report.stage='collector-build'
    $build=Join-Path $private 'build';Copy-Item -LiteralPath (Join-Path $here 'Observer') -Destination $build -Recurse
    Copy-Item -LiteralPath (Join-Path $here 'SelfTest') -Destination $build -Recurse
    Copy-Item -LiteralPath (Join-Path $here 'OwnedCommand.cs') -Destination $build
    Copy-Item -LiteralPath (Join-Path $ProductRoot 'NuGet.Config') -Destination $build
    Copy-Item -LiteralPath (Join-Path $ProductRoot 'global.json') -Destination $build
    $observerProject=Join-Path $build 'Observer/Observer.csproj';$selfProject=Join-Path $build 'SelfTest/SelfTest.csproj'
    $null=Run $dotnet @('build',$observerProject,'-c','Release',"-p:ObjectModelPath=$model",'-p:UseSharedCompilation=false','-nodeReuse:false')
    $null=Run $dotnet @('build',$selfProject,'-c','Release','-p:UseSharedCompilation=false','-nodeReuse:false')
    $report.stage='binding-selftest'
    $r=Run $dotnet @((Join-Path $build 'SelfTest/bin/Release/net10.0/SelfTest.dll'),(Join-Path $private 'observer-contract-selftest')) 30
    $self=(ReadBounded $r.Stdout 4096)|ConvertFrom-Json
    if($self.checks -ne 93 -or $self.passed -ne $true -or $self.nativeProcessesOpened -ne 0) {throw 'selftest'}
    if($self.configurationCases -ne 24 -or $self.custodyCases -ne 12) {throw 'observer-contract-selftest'}
    if($self.blameIdentityCases -ne 30) {throw 'blame-identity-selftest'}
    $r=Run ([Environment]::ProcessPath) @('-NoProfile','-NonInteractive','-File',(Join-Path $here 'Test-ProbeAdmission.ps1'),'-ProductRoot',$ProductRoot,'-PrivateRoot',(Join-Path $private 'admission-selftest')) 30
    $admissionSelftest=(ReadBounded $r.Stdout 4096)|ConvertFrom-Json
    if($admissionSelftest.policyCases -ne 5 -or $admissionSelftest.trxCases -ne 14 -or $admissionSelftest.passed -ne $true -or $admissionSelftest.nativeProcessesOpened -ne 0) {throw 'admission-selftest'}
    $statusSelftest=@(& (Join-Path $here 'Test-ObserverReceipt.ps1') -PrivateRoot (Join-Path $private 'observer-status-selftest'))
    if($statusSelftest.Count -ne 1 -or $statusSelftest[0].cases -ne 18 -or $statusSelftest[0].passed -isnot [bool] -or -not $statusSelftest[0].passed -or $statusSelftest[0].nativeProcessesOpened -ne 0) {throw 'observer-status-selftest'}
    Write-Host 'observe-collector-controls-passed:24/12/18'
    if($statusSelftest[0].blameCases -ne 6) {throw 'blame-status-selftest'}
    Write-Host 'observe-blame-identity-controls-passed:30/6'
    $collector=Canonical (Join-Path $build 'Observer/bin/Release/net8.0/OpenClaw.ObserveOnly.Collector.dll')
    $observerRoot=Join-Path $private 'observer';$invocation=[guid]::NewGuid().ToString('N')
    $hostImage=Join-Path ([IO.Path]::GetDirectoryName($assembly)) 'testhost.exe'
    $hostDigest=if(Test-Path $hostImage){Sha $hostImage}else{Sha $dotnet}
    $xml=[xml]'<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount><TargetPlatform>x64</TargetPlatform><TargetFrameworkVersion>.NETCoreApp,Version=v10.0</TargetFrameworkVersion></RunConfiguration><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName="OpenClawObserveOnly" uri="datacollector://openclaw/observe-only/1"><Configuration /></DataCollector></DataCollectors></DataCollectionRunSettings></RunSettings>'
    $values=@{Root=$observerRoot;Invocation=$invocation;ExpectedSource=$assembly;ExpectedSourceSha256=(Sha $assembly);ExpectedObjectModelSha256=(Sha $model);ExpectedCollectorSha256=(Sha $collector);ExpectedHostImageSha256=$hostDigest;ExpectedDotnetSha256=(Sha $dotnet);ExpectedBlameSha256=(Sha $blame)}
    foreach($k in $values.Keys){$n=$xml.CreateElement($k);$n.InnerText=$values[$k];$null=$xml.SelectSingleNode('//Configuration').AppendChild($n)}
    $settings=Join-Path $private 'observe.runsettings';$xml.Save($settings)
    $report.stage='probe'
    $raw=Join-Path $private 'raw';$report.probe.attempts=1
    $probeStartedUtc=[DateTime]::UtcNow
    $r=Run $dotnet @('exec',$console,$assembly,"/Settings:$settings","/TestAdapterPath:$([IO.Path]::GetDirectoryName($collector))","/TestCaseFilter:FullyQualifiedName=$Fact","/Logger:trx;LogFileName=probe.trx","/ResultsDirectory:$raw","/Diag:$(Join-Path $raw 'vstest.log')",'/Blame:CollectHangDump;TestTimeout=300s;HangDumpType=None') 900 -AllowFailure
    $probeExit=$r.ExitCode;$report.probe.nativeExitCode=$probeExit;$probeWritersJoined=$r.LifecycleSucceeded
    ($report.binaries | Where-Object role -eq 'console').binding='invocation-bound'
    $report.stage='receipt'
    $counts=Read-ObserveProbeResult -ProductRoot $ProductRoot -Path (Join-Path $raw 'probe.trx') -StartedUtc $probeStartedUtc -ExitCode $probeExit -ExpectedFact $Fact
    $report.probe.executed=$counts.executed;$report.probe.passed=$counts.passed;$report.probe.skipped=$counts.skipped
    $snapshot=Get-ObserveCollectorSnapshot -Root $observerRoot -Attempted $true -WritersJoined $probeWritersJoined -ExpectedInvocation $invocation
    $report.collector=$snapshot.Status
    if($snapshot.Status.receiptReadStatus -cne 'validated') {throw 'observer-receipt-unavailable'}
    $observed=$snapshot.Receipt
    $report.binaries+=@($observed.binaries)
    $report.probe.hosts=$observed.hosts;$report.probe.cases=$observed.cases;$report.probe.sourceHashMatched=$observed.sourceMatched;$report.probe.handleRetained=$observed.processRetained;$report.probe.jobMatched=$observed.jobMatched
    if(Test-Path (Join-Path $observerRoot 'observer-invalid')) {throw 'collector-invalid'}
    if($observed.schema -ne 2 -or $observed.mode -cne 'ObserveOnly' -or $observed.failure -cne 'none' -or $observed.ends -ne 1 -or $observed.sessionEnded -ne $true) {throw 'collector-receipt'}
    if($observed.invocation -cne $invocation -or $observed.jobName -cne $r.JobName -or $observed.captureEnabled -ne $false -or -not $observed.bound -or $observed.hosts -ne 1 -or $observed.cases -ne 1 -or -not $observed.processRetained -or -not $observed.jobMatched) {throw 'collector-binding'}
    $readyRecord=(ReadBounded (Join-Path $observerRoot 'observer-ready.json') 4096)|ConvertFrom-Json
    if($readyRecord.invocation -cne $invocation -or $readyRecord.captureEnabled -ne $false){throw 'collector-ready'}
    $loadedModel=@($observed.binaries|Where-Object role -eq 'object-model');$loadedSelf=@($observed.binaries|Where-Object role -eq 'observer')
    if($loadedModel.Count -ne 1 -or $loadedModel[0].sha256 -cne (Sha $model) -or $loadedSelf.Count -ne 1 -or $loadedSelf[0].sha256 -cne (Sha $collector)) {throw 'loaded-identity'}
    $report.probe.hosts=1;$report.probe.cases=1;$report.probe.sourceHashMatched=$true;$report.probe.handleRetained=$true;$report.probe.jobMatched=$true
    $null=Run $git @('-C',$ProductRoot,'diff','--exit-code','HEAD','--') 30
    $report.failure='none';$report.stage='complete';$ready=$true
 } catch {
    $report.failure='preflight-incomplete'
    # Fixed enums and a copied error number only. Never export the exception or its payload.
    try {
        $commandWitness.RecordManaged($_.Exception)
        $report.failureWitness=@{sourceStep=$sourceStep;nativePhase=$commandWitness.Phase.ToString();category=$commandWitness.Category.ToString();win32ErrorAvailable=$commandWitness.Win32ErrorAvailable;win32Error=$commandWitness.Win32Error}
    } catch { } # Evidence failure must not replace the existing native/primary outcome.
} finally {
    $report.probe.nativeExitCode=$nativeOutcomes.probe
    $report.setupExitCode=$nativeOutcomes.setup
    Save-ObserveCollectorSnapshot -Report $report -ReadSnapshot { Get-ObserveCollectorSnapshot -Root $observerRoot -Attempted ($report.probe.attempts -eq 1) -WritersJoined $probeWritersJoined -ExpectedInvocation $invocation }
    try {
        if(Test-Path -LiteralPath $private) {
            $report.dumpFiles=@(Get-ChildItem -LiteralPath $private -Filter '*.dmp' -Recurse -File).Count
            $report.cleanup.ownedProcessesJoined=CleanupPrivate
            $report.cleanup.privateRootDeleted=$report.cleanup.ownedProcessesJoined
        }
    } catch { $report.cleanup.ownedProcessesJoined=$false;$report.cleanup.privateRootDeleted=$false }
    $collectorReady=Test-ObserveCollectorStatusReady $report.collector
    if($ready -and -not $collectorReady -and $report.failure -eq 'none') {$report.failure='preflight-incomplete'}
    $report.observePassed=[bool]($ready -and $collectorReady -and $report.failure -eq 'none' -and $report.dumpFiles -eq 0 -and $report.cleanup.ownedProcessesJoined -and $report.cleanup.privateRootDeleted)
    try {
        $null=New-Item -ItemType Directory -Path $public -Force
        $pending=Join-Path $public 'safe-report.pending';$report|ConvertTo-Json -Depth 12 -Compress|Set-Content -LiteralPath $pending
        & python (Join-Path $here 'validate_report.py') $pending (Join-Path $public 'safe-report.json')
        if($LASTEXITCODE -ne 0){$report.observePassed=$false}
        Remove-Item -LiteralPath $pending -Force
    } catch { $report.observePassed=$false }
}
exit (Get-ObserveExitCode -Outcomes $nativeOutcomes -AdmissionPassed $report.observePassed)
