[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot\StartupProof.ps1"
. "$PSScriptRoot\StartupProof.Native.ps1"
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
    actorSid = 'S-1-5-21-1-2-3-1000'; actorSession = 1
    dotnet = $dotnet; dotnetVersion = '10.0.402'
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
            executionBlocked = $false; attributionLimitationCount = if ($script:rawFailed) { 2 } else { 0 } }
    }
}
function New-FixtureTrace([StartupTraceMode] $Mode) {
    [StartupTraceAdapter]::new($Mode, {
        param($ctx,$mode)
        $script:calls.Add("start:$mode")
        @{ ready = $true; traceStartsCounter = [int]($mode -eq [StartupTraceMode]::Record) }
    }, {
        param($ctx,$mode)
        $script:calls.Add("stop:$mode")
        @{ stopped = $true; instanceAbsent = $true
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
    $missingReceipt = Join-Path $root 'not-created-yet\caller-ready.json'
    $waitError = $null
    try { Wait-StartupReceipt $missingReceipt 0 } catch { $waitError = $_.Exception.Message }
    Assert-Proof ($waitError -ceq 'Bounded receipt wait expired: caller-ready.json') 'Recorder must admit a not-yet-created scenario directory and retain the bounded receipt timeout'
    $existingReceipt = Join-Path $root 'ready.json'
    '{"completed":true}' | Set-Content $existingReceipt
    Assert-Proof ((Wait-StartupReceipt $existingReceipt 1).completed -ceq $true) 'Receipt reader must return the actual parsed final receipt'
    $dry = Invoke-Fixture 'dry'
    Assert-Proof ($dry.completed -and $dry.functionalContractsPassed -and $dry.traceStartsCounter -eq 0) "Full disabled adapter must execute all actual wrapper stages, without recording: $($dry.errors -join '; ')"
    Assert-Proof (($script:calls -join ',') -ceq 'preflight,enter,start:Disabled,managed,sdk-four-controls,ownership-timeout-tree,cleanup,stop:Disabled') 'Dry-run must qualify the observer before recorder admission, execute all three functional owners and finalize the real receipts'
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
    $badReceipt = $dry | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    $badReceipt.completed = 'false'
    $malformed = Invoke-Fixture 'malformed-dry-status' 'Record' $badReceipt
    Assert-Proof (!$malformed.completed -and !$script:calls.Contains('start:Record')) 'String false cannot qualify a supposedly completed dry-run'
    $record = Invoke-Fixture 'record' 'Record' $dry
    Assert-Proof ($record.completed -and $record.functionalContractsPassed -and $record.traceStartsCounter -eq 1) 'Passed full receipt admits exactly the same wrapper through the recording adapter'
    Assert-Proof (($script:calls -join ',') -ceq 'preflight,enter,start:Record,managed,sdk-four-controls,ownership-timeout-tree,cleanup,stop:Record') 'Recording and dry-run must execute identical qualification, functional, cleanup and final-receipt paths'
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
    "PASS: $checks native-free startup-wrapper regressions."
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
