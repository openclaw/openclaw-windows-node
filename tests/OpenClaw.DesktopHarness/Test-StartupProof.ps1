[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\StartupProof.ps1"
. "$PSScriptRoot\StartupProof.Native.ps1"
. "$PSScriptRoot\HarnessPreparation.ps1"
$root = [IO.Path]::GetFullPath((Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N'))))
$capsule = Join-Path $root 'capsule'
$null = New-Item -ItemType Directory -Path (Join-Path $capsule 'observer-final')
$observer = Join-Path $capsule 'observer-final\QuietLaneCaptureObserver.dll'
$dotnet = Join-Path $root 'sdk\dotnet.exe'
$null = New-Item -ItemType Directory (Split-Path $dotnet)
Set-Content $observer 'fixture observer bytes, never loaded'
Set-Content $dotnet 'fixture SDK bytes, never executed'
$config = [pscustomobject]@{
    sdkVersion = '1.0.0'; sourceHead = ('a' * 40)
    leaseId = 'cbx_0123456789ab'
    actorSid = 'S-1-5-21-1-2-3-1000'; actorSession = 1
    dotnet = $dotnet; dotnetVersion = '10.0.402'
    traceStartsFile = Join-Path $root 'trace-starts.json'
    dryRunReceipt = Join-Path $root 'DryRunReceipt.json'
    capsuleRoot = $capsule; observerSource = $observer; requiredPaths = @()
    files = @(
        @{ path = $observer; sha256 = (Get-FileHash $observer).Hash },
        @{ path = $dotnet; sha256 = (Get-FileHash $dotnet).Hash }
    )
}
$startUtc = [DateTime]::SpecifyKind([DateTime]::new(2026,10,23,17,0,0),[DateTimeKind]::Utc)
$script:caller = [pscustomobject]@{
    completed = $true; pid = 123; creationUtc = '2026-10-23T17:00:00.0000000Z'
    actualProcess = [pscustomobject]@{ Id = 123; StartTime = $startUtc; SessionId = 1 }
    sourceHead = ('a' * 40); sid = 'S-1-5-21-1-2-3-1000'; session = 1
    integritySid = 'S-1-16-8192'; threadHasNoToken = $true; nativeLibraryPrimed = $true
    dotnetRoot = Split-Path $dotnet; dotnetRootX64 = Split-Path $dotnet
    resolvedDotnet = $dotnet; dotnetVersion = '10.0.402'; dotnetExitCode = 0
    path = "$(Split-Path $dotnet);C:\Windows"; cwd = $root
}
$script:calls = [Collections.Generic.List[string]]::new()
$script:rawFailed = $false; $script:cleanupOk = $true; $script:badStage = ''
$script:badPreparation = $false
$script:cleanupBlocked = $false
$script:traceStopExit = 0; $script:traceBytes = 1024; $script:traceHash = 'c' * 64
$ops = @{
    Preflight = { param($ctx) $script:calls.Add('preflight'); $script:caller }
    Enter = { param($ctx) $script:calls.Add('enter') }
    Stage = {
        param($ctx,$name)
        $script:calls.Add($name)
        [pscustomobject]@{ name = $name; contractVerified = ($name -cne $script:badStage)
            resourcesReaped = $true; executionBlocked = $false }
    }
    Cleanup = {
        param($ctx)
        $script:calls.Add('cleanup')
        @{ resourcesReaped = $script:cleanupOk; observerFailed = $script:rawFailed
            executionBlocked = $script:cleanupBlocked; attributionLimitationCount = if ($script:rawFailed) { 2 } else { 0 } }
    }
}
function New-FixtureTrace([StartupTraceMode] $Mode) {
    [StartupTraceAdapter]::new($Mode, {
        param($ctx,$mode)
        $script:calls.Add("prepare:$mode")
        @{ prepared = !$script:badPreparation; bindingHash = $ctx.bindingHash; leaseId = $ctx.config.leaseId }
    }, {
        param($ctx,$mode)
        $script:calls.Add("start:$mode")
        @{ ready = $true; traceStartsCounter = [int]($mode -eq [StartupTraceMode]::Record) }
    }, {
        param($ctx,$mode)
        $script:calls.Add("stop:$mode")
        @{ stopped = $true; instanceAbsent = $true
            stopExitCode = $script:traceStopExit; etlBytes = $script:traceBytes; etlSha256 = $script:traceHash
            traceStartsCounter = [int]($mode -eq [StartupTraceMode]::Record -and $script:calls.Contains('start:Record')) }
    })
}
$checks = 0
function Assert-Proof([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }; $script:checks++
}
function Invoke-Fixture([string] $Name, [StartupTraceMode] $Mode = 'Disabled', $Receipt = $null) {
    $script:calls.Clear()
    Invoke-StartupProofRun -Config $config -Trace (New-FixtureTrace $Mode) -Operations $ops `
        -RunDirectory (Join-Path $root $Name) -DryRunReceipt $Receipt
}
try {
    $instance = 'OpenClaw1791-' + ('b' * 32)
    $profilePath = 'C:\proof inputs\startup.wprp'
    $tracePath = 'C:\private proof\startup.etl'
    $recorderCases = @(
        @{operation='Status';expected=@('-status','-instancename',$instance)},
        @{operation='Start';expected=@('-start','C:\proof inputs\startup.wprp!OpenClaw1791.Light','-instancename',$instance)},
        @{operation='Stop';expected=@('-stop','C:\private proof\startup.etl','OpenClaw1791 full startup scenario','-skipPdbGen','-instancename',$instance)},
        @{operation='Cancel';expected=@('-cancel','-instancename',$instance)}
    )
    foreach ($case in $recorderCases) {
        $info = New-StartupRecorderStartInfo -Wpr 'C:\Windows\System32\wpr.exe' `
            -Operation $case.operation -InstanceName $instance -Profile $profilePath -TracePath $tracePath
        $actual = [string]::Join([char]0,[string[]]$info.ArgumentList)
        $expected = [string]::Join([char]0,[string[]]$case.expected)
        Assert-Proof ($actual -ceq $expected -and !$info.UseShellExecute -and $info.CreateNoWindow) `
            "Actual $($case.operation) process launch must retain every operand and its exact owned instance"
    }
    $rejected = $false
    try { New-StartupRecorderStartInfo -Wpr 'wpr.exe' -Operation Cancel -InstanceName 'unowned' | Out-Null }
    catch { $rejected = $true }
    Assert-Proof $rejected 'Recorder commands must reject an unowned instance before any process launch'
    $qualifiedStatus = "Windows Performance Recorder is not recording.`nInstance Name: $instance"
    $qualifiedPattern = [regex]::Escape($qualifiedStatus)
    $nativeStatus = $qualifiedStatus -replace "`n","`r`n"
    Assert-Proof (Test-StartupRecorderStatusAbsent -ExitCode 0 -Text $nativeStatus -ExpectedExitCode 0 -ExpectedPattern $qualifiedPattern) `
        'Native CRLF and captured help-line LF must preserve the same qualified absence signature'
    Assert-Proof (!(Test-StartupRecorderStatusAbsent -ExitCode 1 -Text $nativeStatus -ExpectedExitCode 0 -ExpectedPattern $qualifiedPattern)) `
        'Matching absence text must not hide an unexpected tool exit code'
    Assert-Proof (!(Test-StartupRecorderStatusAbsent -ExitCode 0 -Text 'Recorder is active' -ExpectedExitCode 0 -ExpectedPattern $qualifiedPattern)) `
        'A successful status command must not classify an active or unknown recorder as absent'
    $missingReceipt = Join-Path $root 'not-created-yet\caller-ready.json'
    $waitError = $null
    try { Wait-StartupReceipt $missingReceipt 0 } catch { $waitError = $_.Exception.Message }
    Assert-Proof ($waitError -ceq 'Bounded receipt wait expired: caller-ready.json') 'Recorder must admit a not-yet-created scenario directory and retain the bounded receipt timeout'
    $existingReceipt = Join-Path $root 'ready.json'
    '{"completed":true}' | Set-Content $existingReceipt
    Assert-Proof ((Wait-StartupReceipt $existingReceipt 1).completed -ceq $true) 'Receipt reader must return the actual parsed final receipt'
    $dry = Invoke-Fixture 'dry'
    Assert-Proof ($dry.completed -and $dry.functionalContractsPassed -and $dry.traceStartsCounter -eq 0) "Full disabled adapter must execute all actual wrapper stages, without recording: $($dry.errors -join '; ')"
    Assert-Proof (($script:calls -join ',') -ceq 'preflight,prepare:Disabled,enter,start:Disabled,managed,sdk-four-controls,ownership-timeout-tree,cleanup,stop:Disabled') 'Both recorder processes must prepare before measured observation; actual observer admission still precedes all functional owners'
    Assert-Proof ($null -eq $dry.runUrl -and $dry.stages.Count -eq 3 -and $dry.noDialogVerified) 'Missing optional runUrl must survive the real final JSON formatter'
    $firstBinding = Get-StartupBinding $config $script:caller
    $config | Add-Member -NotePropertyName runUrl -NotePropertyValue $null
    Assert-Proof ((Get-StartupBinding $config $script:caller).digest -ceq $firstBinding.digest) 'Omitted and explicit null runUrl must have identical semantics'
    $nullUrl = Invoke-Fixture 'null-url'
    Assert-Proof ($nullUrl.completed -and $null -eq $nullUrl.runUrl) 'Explicit JSON null must not break completion'
    $identityDate = [DateTimeOffset]::new(2026,10,23,10,0,0,[TimeSpan]::FromHours(-7))
    Assert-Proof (Test-StartupProcessIdentity 123 $identityDate $script:caller.actualProcess) 'UTC ticks, not local/UTC formatted strings, bind live process identity'
    Assert-Proof (!(Test-StartupProcessIdentity 123 $identityDate.AddTicks(1) $script:caller.actualProcess)) 'A reused PID or one-tick lifetime mismatch must not match'
    $script:caller.actualProcess.StartTime = 'invalid'
    $badTime = Invoke-Fixture 'bad-time'
    Assert-Proof (!$badTime.completed -and $badTime.traceStartsCounter -eq 0 -and !$script:calls.Contains('enter')) 'Bad native DateTime evidence must fail before adapter start and preserve failed end receipt'
    $script:caller.actualProcess.StartTime = $startUtc
    $script:caller.completed = 'false'
    $stringStatus = Invoke-Fixture 'string-status'
    Assert-Proof (!$stringStatus.completed -and !$script:calls.Contains('start:Disabled')) 'String false is not a typed successful readiness status'
    $script:caller.completed = $true
    Remove-Item $observer
    $missingDll = Invoke-Fixture 'missing-observer'
    Assert-Proof (!$missingDll.completed -and $missingDll.traceStartsCounter -eq 0 -and !$script:calls.Contains('start:Disabled')) 'Missing actual observer-final DLL must fail the full wrapper before any trace'
    Set-Content $observer 'fixture observer bytes, never loaded'
    $script:caller.path = 'C:\Windows'
    $missingPath = Invoke-Fixture 'missing-sdk-path'
    Assert-Proof (!$missingPath.completed -and $missingPath.traceStartsCounter -eq 0 -and !$script:calls.Contains('enter')) 'SDK executable existing elsewhere cannot hide an unusable actual caller PATH'
    $script:caller.path = "$(Split-Path $dotnet);C:\Windows"
    $script:rawFailed = $true
    $counted = Invoke-Fixture 'raw-two'
    Assert-Proof ($counted.completed -and $counted.functionalContractsPassed -and $counted.attributionLimitationCount -eq 2 -and !$counted.noDialogVerified -and $counted.observerFailed) 'N attribution failures remain raw failures without truncating verified independent functional contracts'
    Assert-Proof ($counted.stages.Count -eq 3) 'Second eligible raw unknown cannot silently drop SDK or ownership outcomes'
    $script:rawFailed = $false
    $script:badStage = 'sdk-four-controls'
    $failedSdk = Invoke-Fixture 'failed-sdk'
    Assert-Proof (!$failedSdk.functionalContractsPassed -and !$failedSdk.noDialogVerified -and $failedSdk.stages.Count -eq 3) 'SDK contract failure remains a failure even when later cleanup-safe ownership executes'
    $script:badStage = ''
    $script:cleanupOk = $false
    $cleanupFailure = Invoke-Fixture 'cleanup-failure'
    Assert-Proof (!$cleanupFailure.completed -and !$cleanupFailure.resourcesReaped -and $script:calls.Contains('stop:Disabled')) 'Cleanup failure cannot qualify a dry-run and must still finalize tracing'
    $script:cleanupOk = $true
    $unauthorizedRecord = Invoke-Fixture 'no-dry-receipt' 'Record'
    Assert-Proof (!$unauthorizedRecord.completed -and $unauthorizedRecord.traceStartsCounter -eq 0 -and !$script:calls.Contains('start:Record')) 'No full dry-run receipt means no recording, not a fabricated trace count of one'
    $dry | Add-Member -NotePropertyName controllerResourcesReaped -NotePropertyValue $false
    $unreaped = Invoke-Fixture 'unreaped-controller' 'Record' $dry
    Assert-Proof (!$unreaped.completed -and !$script:calls.Contains('start:Record')) 'Native resource cleanup alone cannot hide a live task, recorder or medium controller'
    $dry.controllerResourcesReaped = $true
    $script:cleanupBlocked = $true
    $script:rawFailed = $true
    foreach ($mode in @('Disabled','Record')) {
        $blocked = Invoke-Fixture "cleanup-blocked-$mode" $mode $dry
        Assert-Proof (!$blocked.completed -and !$blocked.functionalContractsPassed -and
            $blocked.resourcesReaped -and $blocked.traceInstanceAbsent -and $blocked.stages.Count -eq 3) `
            "A blocking observation first found while draining cleanup must reject $mode despite three passing stages"
    }
    $script:cleanupBlocked = $false
    $script:rawFailed = $false
    $badReceipt = $dry | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $badReceipt.completed = 'false'
    $malformed = Invoke-Fixture 'malformed-dry-status' 'Record' $badReceipt
    Assert-Proof (!$malformed.completed -and !$script:calls.Contains('start:Record')) 'String false cannot qualify a supposedly completed dry-run'
    $record = Invoke-Fixture 'record' 'Record' $dry
    Assert-Proof ($record.completed -and $record.functionalContractsPassed -and $record.traceStartsCounter -eq 1) 'Passed full receipt admits exactly the same wrapper through the recording adapter'
    Assert-Proof (($script:calls -join ',') -ceq 'preflight,prepare:Record,enter,start:Record,managed,sdk-four-controls,ownership-timeout-tree,cleanup,stop:Record') 'Recording and dry-run must execute identical qualification, functional, cleanup and final-receipt paths'
    $script:caller.path += ';C:\Changed'
    $changedEnv = Invoke-Fixture 'changed-environment' 'Record' $dry
    Assert-Proof (!$changedEnv.completed -and !$script:calls.Contains('start:Record')) 'Changing even a valid environment invalidates passed dry-run binding'
    $script:caller.path = "$(Split-Path $dotnet);C:\Windows"
    Set-Content $observer 'replacement bytes'
    $changedArtifact = Invoke-Fixture 'changed-artifact' 'Record' $dry
    Assert-Proof (!$changedArtifact.completed -and !$script:calls.Contains('start:Record')) 'Changed artifact bytes fail before recording'
    $config.files[0].sha256 = (Get-FileHash $observer).Hash
    $repinnedArtifact = Invoke-Fixture 'repinned-artifact' 'Record' $dry
    Assert-Proof (!$repinnedArtifact.completed -and !$script:calls.Contains('start:Record')) 'Repinning changed bytes cannot reuse an old passed dry-run'
    Set-Content $observer 'fixture observer bytes, never loaded'
    $config.files[0].sha256 = (Get-FileHash $observer).Hash
    Set-Content (Join-Path $capsule 'unexpected.ps1') 'unexpected'
    $unboundLayout = Invoke-Fixture 'layout-change' 'Record' $dry
    Assert-Proof (!$unboundLayout.completed -and !$script:calls.Contains('start:Record')) 'An extra unpinned capsule entry invalidates the exact directory layout'
    Remove-Item (Join-Path $capsule 'unexpected.ps1')
    $rejected = $false
    try { Assert-StartupDryRun $dry $dry.bindingHash 1 } catch { $rejected = $true }
    Assert-Proof $rejected 'A previous trace start prevents a second recording even with a passed dry-run'
    $rejected = $false
    try { Invoke-Fixture 'dry' } catch { $rejected = $true }
    Assert-Proof $rejected 'Existing dry-run output and failures remain immutable'
    $probeFixture = Join-Path $root 'probe-fixture.ps1'
    @'
param($LeaseId,$OutputDirectory,[switch]$GuestAuthorized)
[pscustomobject]@{ lease = $LeaseId; directory = $OutputDirectory; authorized = [bool]$GuestAuthorized; starts = 0 }
'@ | Set-Content $probeFixture
    $generated = Join-Path $root 'first-command.ps1'
    $null = & "$PSScriptRoot\New-StartupGuestProbe.ps1" -LeaseId cbx_0123ab -Destination $generated -EntryPoint $probeFixture
    $invoked = & $generated
    Assert-Proof ($invoked.lease -ceq 'cbx_0123ab' -and $invoked.authorized -and $invoked.starts -eq 0) 'The no-stdin/no-argument transport script must actually invoke its exact bound qualification entrypoint'
    Assert-Proof ($invoked.directory -ceq 'C:\crabbox\hub-proof1791\qualification-cbx_0123ab') 'First probe output must be lease-owned, not dependent on unknown transport working directory'
    $rejected = $false
    try { & "$PSScriptRoot\New-StartupGuestProbe.ps1" -LeaseId cbx_0123ab -Destination $generated -EntryPoint $probeFixture } catch { $rejected = $true }
    Assert-Proof $rejected 'A staged first probe must not be silently replaced'
    $rejected = $false
    try { & "$PSScriptRoot\New-StartupGuestProbe.ps1" -LeaseId invalid -Destination (Join-Path $root 'bad-probe.ps1') -EntryPoint $probeFixture } catch { $rejected = $true }
    Assert-Proof $rejected 'An unresolved lease must not produce an executable authorized probe'
    $e2e = Invoke-Fixture 'controller-e2e'
    $driver = Complete-StartupProofController -Config $config -Mode Disabled `
        -RunRoot (Join-Path $root 'controller-e2e') -Run $e2e `
        -TaskAbsent $true -MediumProcessExited $true -RecorderProcessExited $true
    $frozen = Get-Content $config.dryRunReceipt -Raw | ConvertFrom-Json
    Assert-Proof ($driver.completed -and $frozen.completed -and $frozen.controllerResourcesReaped -and
        $frozen.traceStartsCounter -eq 0 -and $null -eq $frozen.runUrl) 'Common engine, actual controller formatter, receipt parse and input freeze must finish end to end with tracing disabled'
    Assert-Proof ($frozen.bindingHash -ceq (Get-StartupBinding $config $script:caller).digest) 'Successful controller completion must freeze the actual unchanged runtime binding'
    $e2eRecord = Invoke-Fixture 'controller-record' Record $frozen
    Set-Content $config.traceStartsFile '{"attempt":1}'
    $recordDriver = Complete-StartupProofController -Config $config -Mode Record `
        -RunRoot (Join-Path $root 'controller-record') -Run $e2eRecord `
        -TaskAbsent $true -MediumProcessExited $true -RecorderProcessExited $true
    Assert-Proof ($recordDriver.completed -and $recordDriver.traceStartsCounter -eq 1) 'Only the trace adapter changes after the fully finalized dry-run; the real controller finalizer also completes recording'
    foreach ($field in @('observerExecutionBlocked','recordingOutputVerified')) {
        $unsafeRecord = $e2eRecord | ConvertTo-Json -Depth 30 | ConvertFrom-Json
        $unsafeRecord.$field = $field -ceq 'observerExecutionBlocked'
        $unsafeRoot = Join-Path $root "controller-reject-$field"
        $null = New-Item -ItemType Directory $unsafeRoot
        $rejected = $false
        try {
            Complete-StartupProofController -Config $config -Mode Record -RunRoot $unsafeRoot `
                -Run $unsafeRecord -TaskAbsent $true -MediumProcessExited $true `
                -RecorderProcessExited $true | Out-Null
        } catch { $rejected = $true }
        $unsafeDriver = Get-Content (Join-Path $unsafeRoot 'driver.json') -Raw | ConvertFrom-Json
        Assert-Proof ($rejected -and !$unsafeDriver.completed -and $unsafeDriver.controllerResourcesReaped) `
            "Controller must independently reject $field even when the run claims completion"
    }
    foreach ($fault in @('stop-failed','missing-etl','empty-etl','missing-hash')) {
        $script:traceStopExit = if ($fault -ceq 'stop-failed') { 1 } else { 0 }
        $script:traceBytes = if ($fault -in @('missing-etl','empty-etl')) { 0 } else { 1024 }
        $script:traceHash = if ($fault -in @('missing-etl','missing-hash')) { $null } else { 'c' * 64 }
        $failedRecording = Invoke-Fixture "recording-$fault" Record $frozen
        Assert-Proof (!$failedRecording.completed -and $failedRecording.resourcesReaped -and
            $failedRecording.traceInstanceAbsent -and $failedRecording.traceStartsCounter -eq 1) `
            "Successful cancellation/cleanup cannot qualify a recording with $fault"
        $rejected = $false
        try {
            Complete-StartupProofController -Config $config -Mode Record `
                -RunRoot (Join-Path $root "recording-$fault") -Run $failedRecording `
                -TaskAbsent $true -MediumProcessExited $true -RecorderProcessExited $true | Out-Null
        } catch { $rejected = $true }
        $failedDriver = Get-Content (Join-Path $root "recording-$fault\driver.json") -Raw | ConvertFrom-Json
        Assert-Proof ($rejected -and !$failedDriver.completed -and $failedDriver.controllerResourcesReaped) `
            "Controller finalization must retain cleanup success but reject $fault"
    }
    $script:traceStopExit = 0; $script:traceBytes = 1024; $script:traceHash = 'c' * 64
    Remove-Item $config.traceStartsFile
    $frozenHash = (Get-FileHash $config.dryRunReceipt).Hash
    foreach ($missing in @('task','medium','recorder')) {
        $unsafe = Invoke-Fixture "controller-unreaped-$missing"
        $rejected = $false
        try {
            Complete-StartupProofController -Config $config -Mode Disabled `
                -RunRoot (Join-Path $root "controller-unreaped-$missing") -Run $unsafe `
                -TaskAbsent ($missing -cne 'task') -MediumProcessExited ($missing -cne 'medium') `
                -RecorderProcessExited ($missing -cne 'recorder') | Out-Null
        } catch { $rejected = $true }
        $failedDriver = Get-Content (Join-Path $root "controller-unreaped-$missing\driver.json") -Raw | ConvertFrom-Json
        Assert-Proof ($rejected -and !$failedDriver.completed -and !$failedDriver.controllerResourcesReaped -and
            (Get-FileHash $config.dryRunReceipt).Hash -ceq $frozenHash) "A live $missing must fail the actual final controller and cannot replace the frozen dry-run"
    }
    Remove-Item $config.dryRunReceipt
    $incomplete = Invoke-Fixture 'controller-malformed-stage'
    $incomplete.stages[1].contractVerified = 'true'
    $rejected = $false
    try {
        Complete-StartupProofController -Config $config -Mode Disabled `
            -RunRoot (Join-Path $root 'controller-malformed-stage') -Run $incomplete `
            -TaskAbsent $true -MediumProcessExited $true -RecorderProcessExited $true | Out-Null
    } catch { $rejected = $true }
    $failedDriver = Get-Content (Join-Path $root 'controller-malformed-stage\driver.json') -Raw | ConvertFrom-Json
    Assert-Proof ($rejected -and !$failedDriver.completed -and !(Test-Path $config.dryRunReceipt)) 'All controllers exiting cannot qualify a malformed functional receipt or write a successful final driver'
    $profile = Join-Path $root 'fixture.wprp'
    Set-Content $profile '<fixture />'
    @{computer=$env:COMPUTERNAME;traceStarted=$false;profileSha256=(Get-FileHash $profile).Hash
        instanceName=('OpenClaw1791-' + ('a' * 32))}|ConvertTo-Json|Set-Content "$profile.receipt.json"
    $wprFixture = Join-Path $root 'fixture-wpr.ps1'
    @'
$Verb=$args[0]
$global:LASTEXITCODE=0
switch($Verb) {
    '-help' {
        if ($args.Count -gt 1 -and $args[1] -ceq 'advanced') {
            'wpr <command> -instancename <instance name>'
            'Can be applied to any commands that manipulates the logging session'
        } else {
            '-help advanced - for advanced trace commands'
        }
    }
    '-profiledetails' { 'Fixture profile parsed without recording' }
    '-status' { 'Windows Performance Recorder is not recording' }
    default { throw 'No recording operation is permitted by this native-free fixture' }
}
'@ | Set-Content $wprFixture
    $manifestPath = Join-Path $root 'fixture-manifest.json'
    @{pr=1791;sdkVersion='1.0.0';leaseId='cbx_0123ab';disposableComputerName=$env:COMPUTERNAME
        expectedHead=$config.sourceHead;repository=$root;dotnet=$dotnet;powerShell=$dotnet
        jobSource=$dotnet;scopeApprovalReceipt=$dotnet
        stages=@(@{name='managed';script=$dotnet},@{name='sdk-four-controls';script=$dotnet},@{name='ownership-timeout-tree';script=$dotnet})
        supportScripts=@(@{path=$dotnet});artifacts=@(@{path=$dotnet})
        inputs=@{nativeRuntime=$dotnet;nativeProbe=$dotnet;nativeInitFailure=$dotnet}}|
        ConvertTo-Json -Depth 8|Set-Content $manifestPath
    $generatedConfigPath = Join-Path $root 'generated-config.json'
    $proofRoot = Join-Path $root 'generated-proof'
    $configReceipt = & "$PSScriptRoot\New-StartupProofConfig.ps1" -Manifest $manifestPath `
        -LeaseId cbx_0123ab -CapsuleRoot $capsule -Harness $dotnet -Profile $profile -Wpr $wprFixture `
        -ActorSid $script:caller.sid -ActorSession $script:caller.session -CallerPath $script:caller.path `
        -Destination $generatedConfigPath -ProofRoot $proofRoot -DotnetVersion '10.0.402'
    $generatedConfig = Get-Content $generatedConfigPath -Raw|ConvertFrom-Json
    Assert-Proof ($configReceipt.completed -and $configReceipt.traceStartsCounter -eq 0 -and
        $null -eq $generatedConfig.runUrl -and $generatedConfig.observerSource -ceq $observer) 'The actual configuration builder must qualify its tool boundary, pin the exact observer layout and serialize nullable runUrl without recording'
    $generatedRunRoot = Join-Path $root 'generated-config-run'
    $generatedRun = Invoke-StartupProofRun -Config $generatedConfig -Trace (New-FixtureTrace Disabled) `
        -Operations $ops -RunDirectory $generatedRunRoot
    $generatedDriver = Complete-StartupProofController -Config $generatedConfig -Mode Disabled `
        -RunRoot $generatedRunRoot -Run $generatedRun -TaskAbsent $true `
        -MediumProcessExited $true -RecorderProcessExited $true
    $generatedFrozen = Get-Content $generatedConfig.dryRunReceipt -Raw|ConvertFrom-Json
    Assert-Proof ($generatedDriver.completed -and $generatedFrozen.traceStartsCounter -eq 0 -and
        $generatedFrozen.bindingHash -ceq (Get-StartupBinding $generatedConfig $script:caller).digest) 'Actual config generation, identical engine, controller formatter and parsed frozen receipt must complete through native-free boundaries'
    $generatedConfig.computer = 'NOT-THE-AUTHORIZED-GUEST'
    $generatedConfig|ConvertTo-Json -Depth 40|Set-Content $generatedConfigPath
    foreach($entry in @('Invoke-StartupProof.ps1','Invoke-StartupProofGuest.ps1','Invoke-StartupRecorder.ps1')) {
        $deniedRoot = Join-Path $root "$entry-denied"
        $rejected = $false
        try {
            & "$PSScriptRoot\$entry" -Config $generatedConfigPath -LeaseId cbx_0123ab `
                -RunRoot $deniedRoot -Mode Disabled | Out-Null
        } catch { $rejected = $true }
        Assert-Proof ($rejected -and !(Test-Path $deniedRoot)) "$entry must reject a nonmatching guest before any process, task, observer or recorder can be started"
    }
    $deniedRoot = Join-Path $root 'unauthorized-qualification'
    $rejected = $false
    try {
        & "$PSScriptRoot\Get-StartupGuestQualification.ps1" -LeaseId cbx_0123ab -OutputDirectory $deniedRoot | Out-Null
    } catch { $rejected = $true }
    Assert-Proof ($rejected -and !(Test-Path $deniedRoot)) 'Actual qualification entrypoint must reject missing guest authority without touching native tooling or creating a receipt'
    $savedPreflight = $ops.Preflight
    $ops.Preflight = { param($ctx) 'unexpected preparation output'; $script:caller }
    $mixedOutput = Invoke-Fixture 'mixed-preflight-output'
    Assert-Proof (!$mixedOutput.completed -and !$script:calls.Contains('enter') -and
        $mixedOutput.traceStartsCounter -eq 0) 'Stray readiness output must fail before observer or recorder admission, not be mistaken for a caller receipt'
    $ops.Preflight = $savedPreflight
    $savedEnter = $ops.Enter
    $ops.Enter = { param($ctx) $script:calls.Add('enter'); throw 'Fixture observer readiness failure' }
    $failedEnter = Invoke-Fixture 'observer-start-failure'
    Assert-Proof (!$failedEnter.completed -and $script:calls.Contains('cleanup') -and
        $script:calls.Contains('stop:Disabled') -and !$script:calls.Contains('start:Disabled')) 'Observer startup failure must still finalize resources and the disabled adapter without admitting any scenario'
    $ops.Enter = $savedEnter
    $savedStage = $ops.Stage
    foreach($failure in @('unreaped','blocked','malformed')) {
        $script:stageFailure = $failure
        $ops.Stage = {
            param($ctx,$name)
            $script:calls.Add($name)
            @{name=$name;contractVerified=$true;resourcesReaped=($script:stageFailure -cne 'unreaped')
                executionBlocked=if($script:stageFailure -ceq 'malformed'){'false'}else{$script:stageFailure -ceq 'blocked'}}
        }
        $unsafeStage = Invoke-Fixture "unsafe-stage-$failure"
        Assert-Proof (!$unsafeStage.completed -and !$script:calls.Contains('sdk-four-controls') -and
            $script:calls.Contains('cleanup') -and $script:calls.Contains('stop:Disabled')) "A $failure managed stage must stop later SDK/ownership execution while finalizing the real orchestration"
    }
    $ops.Stage = $savedStage
    $script:calls.Clear()
    $rollback = [StartupTraceAdapter]::new('Record', {
        param($ctx,$mode)
        @{prepared=$true;bindingHash=$ctx.bindingHash;leaseId=$ctx.config.leaseId}
    }, {
        param($ctx,$mode)
        @{ready=$true;traceStartsCounter=1}
    }, {
        param($ctx,$mode)
        @{stopped=$true;instanceAbsent=$true;traceStartsCounter=0}
    })
    $badStop = Invoke-StartupProofRun -Config $config -Trace $rollback -Operations $ops `
        -RunDirectory (Join-Path $root 'counter-rollback') -DryRunReceipt $frozen
    Assert-Proof (!$badStop.completed -and $badStop.traceStartsCounter -eq 1 -and
        !$badStop.traceInstanceAbsent) 'Malformed recorder finalization cannot erase an actual attempt or permit a second trace'
    $script:badPreparation = $true
    $unprepared = Invoke-Fixture 'unprepared-recorder'
    Assert-Proof (!$unprepared.completed -and $unprepared.traceStartsCounter -eq 0 -and
        $unprepared.stages.Count -eq 0 -and !$script:calls.Contains('enter') -and
        !$script:calls.Contains('start:Disabled')) 'Failed real recorder preparation must refuse observation, functional execution and trace admission'
    $script:badPreparation = $false
    foreach ($partialReceipt in @('', ' ', 'null', '[]', 'true', '{"ready":')) {
        $rejectedReceipt = $false
        try { $null = ConvertFrom-StartupReceiptJson $partialReceipt } catch { $rejectedReceipt = $true }
        Assert-Proof $rejectedReceipt 'File creation, incomplete writes and non-object JSON must not finalize a receipt'
    }
    $readyReceipt = ConvertFrom-StartupReceiptJson '{"ready":true,"traceStartsCounter":0}'
    Assert-Proof ($readyReceipt.ready -is [bool] -and $readyReceipt.ready -and
        $readyReceipt.traceStartsCounter -eq 0) 'A complete actual receipt must retain its typed completion and zero-start fields'
    $preparedExecutable = Join-Path $root 'prepared-harness.exe'
    [IO.File]::WriteAllText($preparedExecutable, 'frozen artifact', [Text.UTF8Encoding]::new($false))
    $expectedHarnessHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes('frozen artifact')))
    $script:harnessBuildCalls = 0
    $unexpectedBuild = { $script:harnessBuildCalls++; throw 'Frozen execution must never compile.' }
    $resolved = Resolve-DesktopHarnessExecutable $preparedExecutable $expectedHarnessHash $unexpectedBuild
    Assert-Proof ($resolved -ceq $preparedExecutable -and $script:harnessBuildCalls -eq 0) 'Frozen execution must use the prepared artifact without compiling'
    [IO.File]::WriteAllText($preparedExecutable, 'changed artifact', [Text.UTF8Encoding]::new($false))
    $rejected = $false
    try { Resolve-DesktopHarnessExecutable $preparedExecutable $expectedHarnessHash $unexpectedBuild | Out-Null } catch { $rejected = $true }
    Assert-Proof ($rejected -and $script:harnessBuildCalls -eq 0) 'A changed frozen artifact must fail without rebuilding inside observation'
    Remove-Item -LiteralPath $preparedExecutable
    $rejected = $false
    try { Resolve-DesktopHarnessExecutable $preparedExecutable $expectedHarnessHash $unexpectedBuild | Out-Null } catch { $rejected = $true }
    Assert-Proof ($rejected -and $script:harnessBuildCalls -eq 0) 'A missing frozen artifact must fail without an implicit compilation fallback'
    $rejected = $false
    try { Resolve-DesktopHarnessExecutable $preparedExecutable 'invalid' $unexpectedBuild | Out-Null } catch { $rejected = $true }
    Assert-Proof ($rejected -and $script:harnessBuildCalls -eq 0) 'An invalid frozen digest must not enter the ordinary build path'
    $resolved = Resolve-DesktopHarnessExecutable -Executable $preparedExecutable -Build {
        $script:harnessBuildCalls++
        [IO.File]::WriteAllText($preparedExecutable, 'ordinary build output', [Text.UTF8Encoding]::new($false))
    }
    Assert-Proof ($resolved -ceq $preparedExecutable -and $script:harnessBuildCalls -eq 1) 'Ordinary regression invocation must preserve its explicit build operation'
    "PASS: $checks native-free startup-wrapper regressions."
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
