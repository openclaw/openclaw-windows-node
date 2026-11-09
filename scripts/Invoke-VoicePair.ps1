<#
.SYNOPSIS
Temporary two-arm, one-fact diagnostic. Not a replacement CI lane or a retry.
.EXAMPLE
./scripts/Invoke-VoicePair.ps1 -Mode Run -ExpectedPipelineSha <dispatched commit>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Run', 'Cleanup')][string]$Mode,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedPipelineSha
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$pipeline = Split-Path $PSScriptRoot -Parent
$workspace = Split-Path $pipeline -Parent
$private = Join-Path $env:RUNNER_TEMP "voice-pair-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$public = Join-Path $pipeline 'TestResults/FocusedVoice'
$fact = 'OpenClaw.Tray.UITests.GatewayFixtureUiTests.HistoricalFractionalVoiceConversationRendersWithoutLoadingPlaceholder'
$pins = @{
    main = @('1c223f14f6b66cf0928eeb8686f34208b5ea4d51', '807162139a790fd5fb88c29217688db3f7448ab9')
    approval = @('1e60d1135ae50d3a37525c7cdc3433b81123b4f3', '22ba737e3122d589787701a16ee2e2bbd234bdb0')
}
function Assert-Source([string]$Root, [string]$Sha, [string]$Tree) {
    $actual = & git -C $Root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $actual.Trim() -cne $Sha) { throw 'Pinned source mismatch.' }
    $actualTree = & git -C $Root rev-parse 'HEAD^{tree}'
    if ($LASTEXITCODE -ne 0 -or $actualTree.Trim() -cne $Tree) { throw 'Pinned tree mismatch.' }
    $dirty = & git -C $Root status --porcelain --untracked-files=no
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Tracked product source changed.' }
}
function Write-Json($Value, [string]$Path) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding utf8
}
function Assert-Exited([int]$ProcessId, [string]$StartedUtc) {
    if ($ProcessId -le 0) { throw 'Missing owned process identity.' }
    $process = $null
    try { $process = [Diagnostics.Process]::GetProcessById($ProcessId) }
    catch [ArgumentException] { return }
    try {
        if (-not $process.HasExited -and $process.StartTime.ToUniversalTime() -eq [datetime]::Parse($StartedUtc).ToUniversalTime()) {
            throw 'Source-owned app or fixture worker is still alive.'
        }
        # A different start identity is PID reuse, not permission to terminate it.
    } finally { $process.Dispose() }
}
function Save-Invocation($Record) {
    # This fixed field list contains no executable path, arguments or exception text.
    $safe = [ordered]@{}
    foreach ($key in @('arm','sourceCommit','sourceTree','pipelineCommit','platform','processId','processStartUtc',
        'startedUtc','finishedUtc','exitCode','timedOut','appSha256','appAssemblySha256','testAssemblySha256')) {
        $safe[$key] = $Record[$key]
    }
    Write-Json $safe (Join-Path $public ($Record.arm + '.invocation.json'))
}
function Get-OnlyArmOutcome([object[]]$Output) {
    if ($Output.Count -ne 1 -or $Output[0] -isnot [string] -or $Output[0] -cnotin @('Passed', 'Failed')) {
        throw 'Arm output must contain exactly one Passed or Failed string.'
    }
    return $Output[0]
}
function Get-FixtureTarget([string]$Source, [string]$Arm) {
    # The same real MSBuild environment properties used by the child wrapper.
    $oldPlatform = $env:Platform; $oldDevBuild = $env:DevBuild
    try {
        $env:Platform = 'x64'; $env:DevBuild = 'false'
        $query = Join-Path $private ($Arm + '-target.private.json')
        if ($Mode -eq 'Prepare') {
            Enter-VoicePreparation 'target-query' $Arm
            Invoke-VoicePreparationNative {
                & dotnet msbuild (Join-Path $Source 'tests/OpenClaw.Tray.UITests/OpenClaw.Tray.UITests.csproj') -nologo -p:Configuration=Debug -p:RuntimeIdentifier=win-x64 '-getProperty:TargetPath,Platform,DevBuild,Configuration,RuntimeIdentifier' > $query 2> (Join-Path $private ($Arm + '-target.private.err'))
            }
        } else {
            & dotnet msbuild (Join-Path $Source 'tests/OpenClaw.Tray.UITests/OpenClaw.Tray.UITests.csproj') -nologo -p:Configuration=Debug -p:RuntimeIdentifier=win-x64 '-getProperty:TargetPath,Platform,DevBuild,Configuration,RuntimeIdentifier' > $query 2> (Join-Path $private ($Arm + '-target.private.err'))
        }
        if ($Mode -ne 'Prepare' -and $LASTEXITCODE -ne 0) { throw 'Fixture TargetPath evaluation failed.' }
        if ($Mode -eq 'Prepare') { Enter-VoicePreparation 'target-json' $Arm 'target-query-json' }
        $properties = (Get-Content -LiteralPath $query -Raw | ConvertFrom-Json).Properties
        if ($Mode -eq 'Prepare') { Complete-VoicePreparation; Enter-VoicePreparation 'target-contract' $Arm 'target-query-contract' }
        if ($properties.Platform -cne 'x64' -or $properties.DevBuild -cne 'false' -or
            $properties.Configuration -cne 'Debug' -or $properties.RuntimeIdentifier -cne 'win-x64') {
            throw 'Fixture MSBuild property binding differs from the original fixture lane.'
        }
        $expected = Join-Path $Source 'tests/OpenClaw.Tray.UITests/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/OpenClaw.Tray.UITests.dll'
        if ([IO.Path]::GetFullPath($properties.TargetPath) -ine [IO.Path]::GetFullPath($expected)) {
            throw 'Fixture TargetPath differs from the source-bound x64 fixture output.'
        }
        if ($Mode -eq 'Prepare') { Complete-VoicePreparation }
        return [IO.Path]::GetFullPath($properties.TargetPath)
    } finally { $env:Platform = $oldPlatform; $env:DevBuild = $oldDevBuild }
}
function Build-Arm([string]$Arm) {
    $source = Join-Path $workspace $Arm
    Enter-VoicePreparation 'source-before' $Arm 'source-guard'
    Assert-Source $source $pins[$Arm][0] $pins[$Arm][1]
    Complete-VoicePreparation
    $env:OPENCLAW_REPO_ROOT = $source
    Push-Location $source
    $oldPlatform = $env:Platform; $oldDevBuild = $env:DevBuild
    try {
        # Ordinary CI first builds the bin/Debug app used by the later fixture.
        $env:Platform = $null; $env:DevBuild = 'false'
        Enter-VoicePreparation 'default-restore' $Arm
        Invoke-VoicePreparationNative {
            & dotnet restore tests/OpenClaw.Tray.UITests -r win-x64 -p:DevBuild=false *> (Join-Path $private "$Arm-restore.log")
        }
        Enter-VoicePreparation 'default-build' $Arm
        Invoke-VoicePreparationNative {
            & dotnet build tests/OpenClaw.Tray.UITests -c Debug -r win-x64 --no-restore -p:DevBuild=false *> (Join-Path $private "$Arm-build.log")
        }
        # The original fixture then builds/runs the x64-platform test assembly.
        # Keep the previously built standalone app at bin/Debug, as its AppPath specifies.
        Enter-VoicePreparation 'fixture-restore' $Arm
        Invoke-VoicePreparationNative {
            & dotnet restore tests/OpenClaw.Tray.UITests -r win-x64 -p:Platform=x64 -p:DevBuild=false *> (Join-Path $private "$Arm-fixture-restore.log")
        }
        Enter-VoicePreparation 'fixture-build' $Arm
        Invoke-VoicePreparationNative {
            & dotnet build tests/OpenClaw.Tray.UITests -c Debug -r win-x64 --no-restore -p:Platform=x64 -p:DevBuild=false *> (Join-Path $private "$Arm-fixture-build.log")
        }
        $target = Get-FixtureTarget $source $Arm
        Enter-VoicePreparation 'required-files' $Arm 'file-io'
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Queried fixture test assembly is missing.' }
        $standaloneApp = Join-Path $source 'src/OpenClaw.Tray.WinUI/bin/Debug/net10.0-windows10.0.22621.0/win-x64/OpenClaw.Tray.WinUI.exe'
        if (-not (Test-Path -LiteralPath $standaloneApp -PathType Leaf)) { throw 'Original fixture AppPath was not built.' }
        Complete-VoicePreparation
        Enter-VoicePreparation 'app-sdk' $Arm 'dependency-contract'
        [xml]$props = Get-Content Directory.Build.props
        if ($props.SelectSingleNode('/Project/PropertyGroup/MicrosoftWindowsAppSDKVersion').InnerText -cne '2.6.5') {
            throw 'Pinned runtime dependency differs.'
        }
        Complete-VoicePreparation
        Enter-VoicePreparation 'source-after' $Arm 'source-guard'
        Assert-Source $source $pins[$Arm][0] $pins[$Arm][1]
        Complete-VoicePreparation
    } finally { $env:Platform = $oldPlatform; $env:DevBuild = $oldDevBuild; Pop-Location }
}
function Invoke-Arm([string]$Arm) {
    $source = Join-Path $workspace $Arm
    Assert-Source $source $pins[$Arm][0] $pins[$Arm][1]
    $root = Join-Path $private $Arm
    New-Item -ItemType Directory -Path $root -ErrorAction Stop | Out-Null
    foreach ($folder in @('temp', 'fixture', 'results', 'data', 'roaming', 'local')) {
        New-Item -ItemType Directory -Path (Join-Path $root $folder) -ErrorAction Stop | Out-Null
    }
    $app = Join-Path $source 'src/OpenClaw.Tray.WinUI/bin/Debug/net10.0-windows10.0.22621.0/win-x64/OpenClaw.Tray.WinUI.exe'
    $assembly = [IO.Path]::ChangeExtension($app, '.dll')
    $testAssembly = Get-FixtureTarget $source $Arm
    $record = [ordered]@{
        arm = $Arm; sourceCommit = $pins[$Arm][0]; sourceTree = $pins[$Arm][1]; pipelineCommit = $ExpectedPipelineSha
        platform = 'x64'; appPath = $app
        appSha256 = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
        appAssemblySha256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
        testAssemblySha256 = (Get-FileHash -LiteralPath $testAssembly -Algorithm SHA256).Hash
        processId = $null; processStartUtc = $null; startedUtc = [datetime]::UtcNow.ToString('o')
        finishedUtc = $null; exitCode = $null; timedOut = $false
    }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $source
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($arg in @('-NoLogo', '-NoProfile', '-File', (Join-Path $source 'scripts/Invoke-CiTest.ps1'),
        '-Project', 'tests/OpenClaw.Tray.UITests', '-ResultsDirectory', (Join-Path $root 'results'),
        '-TrxFileName', 'voice.trx', '-Runtime', 'win-x64', '-Filter', "FullyQualifiedName=$fact", '-RequireAllTestsPassed')) {
        $start.ArgumentList.Add($arg)
    }
    # Exact fixture opt-in without inherited proof/debug switches. No CaptureHostExit,
    # dumps, animation override, keyboard input or screenshots are requested.
    foreach ($key in @($start.Environment.Keys)) {
        if ($key.StartsWith('OPENCLAW_', [StringComparison]::OrdinalIgnoreCase)) { $start.Environment.Remove($key) | Out-Null }
    }
    $values = @{
        OPENCLAW_REPO_ROOT = $source; OPENCLAW_CI_COLLECT_COVERAGE = 'false'
        OPENCLAW_RUN_GATEWAY_FIXTURE_UI = '1'; OPENCLAW_RUN_KEYBOARD_PROOF = '0'
        OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS = '0'; OPENCLAW_GATEWAY_FIXTURE_APP = $app
        OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS = (Join-Path $root 'fixture')
        OPENCLAW_TRAY_DATA_DIR = (Join-Path $root 'data'); OPENCLAW_TRAY_APPDATA_DIR = (Join-Path $root 'roaming')
        OPENCLAW_TRAY_LOCALAPPDATA_DIR = (Join-Path $root 'local')
        TEMP = (Join-Path $root 'temp'); TMP = (Join-Path $root 'temp')
        Platform = 'x64'; DevBuild = 'false'
        GitVersion_NoNormalizeEnabled = 'true'; GitVersion_NoFetchEnabled = 'true'
    }
    foreach ($key in $values.Keys) { $start.Environment[$key] = $values[$key] }
    $stdout = [IO.File]::Create((Join-Path $root 'stdout.private'))
    $stderr = [IO.File]::Create((Join-Path $root 'stderr.private'))
    $process = $null
    $invocationError = $false; $recordOrStreamError = $false
    Set-Content -LiteralPath (Join-Path $root 'active.flag') -Value 'owned invocation not yet qualified'
    try {
        $process = [Diagnostics.Process]::Start($start)
        $record.processId = $process.Id; $record.processStartUtc = $process.StartTime.ToUniversalTime().ToString('o')
        Write-Json $record (Join-Path $root 'invocation.json')
        Save-Invocation $record
        $outDrain = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errDrain = $process.StandardError.BaseStream.CopyToAsync($stderr)
        if (-not $process.WaitForExit(900000)) {
            $record.timedOut = $true
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw 'Owned invocation termination unconfirmed.' }
        }
        $record.exitCode = $process.ExitCode
        [void][Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($outDrain, $errDrain)).WaitAsync([timespan]::FromSeconds(10)).GetAwaiter().GetResult()
    } catch {
        $invocationError = $true # Never emit ErrorRecord contents from the private invocation.
    } finally {
        $record.finishedUtc = [datetime]::UtcNow.ToString('o')
        try { Write-Json $record (Join-Path $root 'invocation.json') } catch { $recordOrStreamError = $true }
        try { Save-Invocation $record } catch { $recordOrStreamError = $true }
        try { $stdout.Dispose() } catch { $recordOrStreamError = $true }
        try { $stderr.Dispose() } catch { $recordOrStreamError = $true }
        if ($null -ne $process) { try { $process.Dispose() } catch { $recordOrStreamError = $true } }
    }
    if ($invocationError -or $recordOrStreamError) { throw 'Invocation or secondary recording failed; recorded native exit is not overwritten.' }
    if ($record.timedOut) { throw 'Invocation exceeded the unchanged 900-second fixture ceiling; comparison stopped.' }
    Assert-Source $source $pins[$Arm][0] $pins[$Arm][1]
    # Failed reports remain FAILURE evidence, never fed to the success reader as exit zero.
    if ($record.exitCode -eq 0) {
        . (Join-Path $source 'scripts/TestResultContract.ps1')
        $reports = @(Get-ChildItem -LiteralPath (Join-Path $root 'results') -Filter voice.trx -Recurse -File)
        if ($reports.Count -ne 1) { throw 'One invocation TRX is required.' }
        $result = Read-TestResultContract -Path $reports[0].FullName -StartedUtc $record.startedUtc -ExitCode $record.exitCode
        if ($result.discovered -ne 1 -or $result.passed -ne 1) { throw 'Exact one-case execution required.' }
    }
    & python (Join-Path $PSScriptRoot 'voice_pair_evidence.py') $root
    if ($LASTEXITCODE -ne 0) { throw 'Evidence admission failed; next arm is prohibited.' }
    $ownership = Get-Content -LiteralPath (Join-Path $root 'ownership.json') -Raw | ConvertFrom-Json
    Assert-Exited $ownership.appProcessId $ownership.appProcessStartUtc
    Assert-Exited $ownership.fixtureProcessId $ownership.fixtureProcessStartUtc
    if (Test-Path -LiteralPath $ownership.profileRoot) { throw 'Existing fixture owner did not remove its profile.' }
    $safe = Get-Content -LiteralPath (Join-Path $root 'candidate.json') -Raw | ConvertFrom-Json
    $safe.cleanupQualified = $true
    Write-Json $safe (Join-Path $public "$Arm.json")
    Copy-Item -LiteralPath (Join-Path $root 'sanitized.trx') -Destination (Join-Path $public "$Arm.sanitized.trx")
    Remove-Item -LiteralPath (Join-Path $root 'active.flag')
    return $safe.result.outcome
}

try {
    if ($Mode -eq 'Prepare') {
        . (Join-Path $PSScriptRoot 'VoicePreparationWitness.ps1')
        Start-VoicePreparation $private
    }
    if (-not $IsWindows -or $env:GITHUB_REPOSITORY -cne 'openclaw/openclaw-windows-node' -or
        $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
        $env:GITHUB_REF -cne 'refs/heads/validation/inbox-stack-ab3cbe7b-20261108' -or
        $env:GITHUB_SHA -cne $ExpectedPipelineSha -or $env:GITHUB_RUN_ATTEMPT -cne '1' -or
        $env:GITHUB_RUN_ID -cnotmatch '^\d+$' -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne 'X64') {
        throw 'Disposable one-attempt pipeline identity refused.'
    }
    $actual = & git -C $pipeline rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $actual.Trim() -cne $ExpectedPipelineSha) { throw 'Pipeline checkout mismatch.' }
    if ($Mode -eq 'Cleanup') {
        if (Test-Path -LiteralPath $private) {
            if (@(Get-ChildItem -LiteralPath $private -Filter active.flag -Recurse -File).Count -ne 0) {
                throw 'Unqualified lifetime: keep private state for disposable-VM destruction, never run another arm.'
            }
            if (@(Get-ChildItem -LiteralPath $private -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -ne 0) {
                throw 'Private cleanup refused a reparse point.'
            }
            Remove-Item -LiteralPath $private -Recurse -Force
        }
        return
    }
    if ($Mode -eq 'Prepare') {
        Complete-VoicePreparation
        Enter-VoicePreparation 'directories' 'none' 'file-io'
        New-Item -ItemType Directory -Path $private -ErrorAction Stop | Out-Null
        New-Item -ItemType Directory -Path $public -ErrorAction Stop | Out-Null
        Complete-VoicePreparation
        Enter-VoicePreparation 'witness-controls' 'none' 'controls'
        & (Join-Path $PSScriptRoot 'test-voice-preparation-witness.ps1')
        Complete-VoicePreparation
        Enter-VoicePreparation 'python-controls' 'none' 'controls'
        Get-Command python -ErrorAction Stop | Out-Null
        Invoke-VoicePreparationNative { & python (Join-Path $PSScriptRoot 'test_voice_pair_evidence.py') }
        Enter-VoicePreparation 'output-controls' 'none' 'controls'
        & (Join-Path $PSScriptRoot 'test-voice-pair-output.ps1')
        Complete-VoicePreparation
        Build-Arm 'main'
        Build-Arm 'approval'
        # Same version-derived x64 installer contract as ci.yml ui-tests.
        Enter-VoicePreparation 'runtime-version' 'none' 'dependency-contract'
        [xml]$props = Get-Content (Join-Path $workspace 'main/Directory.Build.props')
        $version = $props.SelectSingleNode('/Project/PropertyGroup/MicrosoftWindowsAppSDKVersion').InnerText.Trim()
        $channel = [regex]::Match($version, '^\d+\.\d+').Value
        $installer = Join-Path $private 'WindowsAppRuntimeInstall.exe'
        Complete-VoicePreparation
        Enter-VoicePreparation 'runtime-download' 'none' 'runtime-download'
        Invoke-WebRequest -Uri "https://aka.ms/windowsappsdk/$channel/$version/windowsappruntimeinstall-x64.exe" -OutFile $installer
        Complete-VoicePreparation
        Enter-VoicePreparation 'runtime-install'
        Invoke-VoicePreparationNative { & $installer --quiet *> (Join-Path $private 'runtime-install.log') }
        Enter-VoicePreparation 'desktop' 'none' 'desktop-contract'
        & (Join-Path $workspace 'main/scripts/Set-CiDesktopResolution.ps1') *> (Join-Path $private 'desktop.log')
        Complete-VoicePreparation
        Enter-VoicePreparation 'sdk-query'
        $sdk = (Invoke-VoicePreparationNative { & dotnet --version }).Trim()
        Enter-VoicePreparation 'sdk-contract' 'none' 'dependency-contract'
        if ($sdk -cnotmatch '^10[.][0-9.]+$') { throw 'Unknown SDK identity.' }
        Complete-VoicePreparation
        Enter-VoicePreparation 'image-contract' 'none' 'image-contract'
        if ($env:ImageOS -cnotmatch '^[A-Za-z0-9.-]{1,64}$' -or $env:ImageVersion -cnotmatch '^[A-Za-z0-9.-]{1,64}$') { throw 'Unknown hosted image identity.' }
        Complete-VoicePreparation
        Enter-VoicePreparation 'manifest' 'none' 'file-io'
        Write-Json ([ordered]@{ schema = 'focused-voice-pair-manifest-v1'; pipelineCommit = $ExpectedPipelineSha
            mainCommit = $pins.main[0]; mainTree = $pins.main[1]; approvalCommit = $pins.approval[0]; approvalTree = $pins.approval[1]
            runId = $env:GITHUB_RUN_ID; runAttempt = 1; architecture = 'x64'; platform = 'x64'; configuration = 'Debug'; devBuild = $false
            sdk = $sdk; osVersion = [Environment]::OSVersion.Version.ToString(); image = $env:ImageOS; imageVersion = $env:ImageVersion
            appSdk = $version; runtimeInstallerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
            filter = "FullyQualifiedName=$fact"; screenshots = $false; keyboardProof = $false; coverage = $false
            bodyDeadlineSeconds = 150; nativeWaitSeconds = 20; invocationDeadlineSeconds = 900
            testInvocation = 'source scripts/Invoke-CiTest.ps1 -Runtime win-x64 -RequireAllTestsPassed, exact FQN'
            trxExport = 'sanitized one-case derivative; raw stdout/errors/attachments removed'
            testOutput = 'bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64, queried with the child MSBuild environment'
            standaloneAppOutput = 'src/OpenClaw.Tray.WinUI/bin/Debug/net10.0-windows10.0.22621.0/win-x64'
            isolationDifference = 'cold single cases, separate checkouts/build outputs, private per-arm TEMP/three roots, screenshots disabled'
        }) (Join-Path $public 'manifest.json')
        Complete-VoicePreparation
        Write-Host 'Focused voice pair prepared; no app test has run.'
        return
    }
    $mainOutcome = Get-OnlyArmOutcome @(Invoke-Arm 'main')
    # Continue after a failed assertion ONLY with qualified source-owned cleanup.
    $approvalOutcome = Get-OnlyArmOutcome @(Invoke-Arm 'approval')
    $summary = [ordered]@{ schema = 'focused-voice-pair-v1'; pipelineCommit = $ExpectedPipelineSha
        arms = 2; main = $mainOutcome; approval = $approvalOutcome; comparisonComplete = $true
        bothPassed = ($mainOutcome -ceq 'Passed' -and $approvalOutcome -ceq 'Passed'); causalConclusion = 'none' }
    Write-Json $summary (Join-Path $public 'summary.json')
    if (-not $summary.bothPassed) { exit 1 }
} catch {
    if (Test-Path -LiteralPath $public) {
        $receipt = [ordered]@{ schema = 'focused-voice-pair-v1'; comparisonComplete = $false; bothPassed = $false; causalConclusion = 'none'; rejectedAt = $Mode }
        if ($Mode -eq 'Prepare' -and (Test-Path 'Function:Save-VoicePreparationFailure')) {
            Save-VoicePreparationFailure $receipt { param($safeReceipt) Write-Json $safeReceipt (Join-Path $public 'incomplete.json') }
        } else {
            try { Write-Json $receipt (Join-Path $public 'incomplete.json') } catch { }
        }
    }
    Write-Host 'Focused voice comparison incomplete or cleanup unqualified. No raw diagnostics exported; no further arm authorized.'
    exit 2
}
