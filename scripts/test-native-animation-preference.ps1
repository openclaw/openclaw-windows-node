# Tests the native-preference transaction without changing the Windows preference.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeAnimationPreference.ps1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$cases = @(
    @{ name = 'enable'; initial = $false; desired = $true; expectedWrites = 'True,False' },
    @{ name = 'already-enabled'; initial = $true; desired = $true; expectedWrites = '' },
    @{ name = 'disable'; initial = $true; desired = $false; expectedWrites = 'False,True' },
    @{ name = 'read-fails'; initial = $false; desired = $true; readFails = $true; error = 'read failed'; expectedWrites = '' },
    @{ name = 'setter-partially-fails'; initial = $false; desired = $true; setFails = $true; error = 'set failed'; expectedWrites = 'True,False' },
    @{ name = 'setter-has-no-effect'; initial = $false; desired = $true; noEffect = $true; error = 'Could not establish'; expectedWrites = 'True,False' },
    @{ name = 'body-fails'; initial = $false; desired = $true; bodyFails = $true; error = 'body failed'; expectedWrites = 'True,False' },
    @{ name = 'restore-fails'; initial = $false; desired = $true; restoreFails = $true; error = 'Could not restore'; expectedWrites = 'True,False' },
    @{ name = 'restore-readback-fails'; initial = $false; desired = $true; restoreNoEffect = $true; error = 'Could not restore'; expectedWrites = 'True,False' },
    @{ name = 'body-and-restore-fail'; initial = $false; desired = $true; bodyFails = $true; restoreFails = $true; error = 'both failed'; expectedWrites = 'True,False' }
)
$previous = $env:OPENCLAW_TEST_ANIMATION_PREFERENCE
try {
    $env:OPENCLAW_TEST_ANIMATION_PREFERENCE = 'original-marker'
    foreach ($case in $cases) {
        $state = [pscustomobject]@{
            value = $case.initial
            writes = [Collections.Generic.List[bool]]::new()
            bodyCalls = 0
        }
        $read = {
            if ($case.readFails) { throw 'read failed' }
            return $state.value
        }.GetNewClosure()
        $write = {
            param($value)
            $state.writes.Add($value)
            if ($state.writes.Count -eq 1) {
                if (-not $case.noEffect) { $state.value = $value }
                if ($case.setFails) { throw 'set failed' }
            } else {
                if ($case.restoreFails) { throw 'restore failed' }
                if (-not $case.restoreNoEffect) { $state.value = $value }
            }
        }.GetNewClosure()
        $body = {
            $state.bodyCalls++
            $expected = if ($case.desired) { 'Enabled' } else { 'Disabled' }
            if ($env:OPENCLAW_TEST_ANIMATION_PREFERENCE -ne $expected) {
                throw 'Child environment was not bound to the verified preference.'
            }
            if ($case.bodyFails) { throw 'body failed' }
        }.GetNewClosure()
        $failure = $null
        try {
            Invoke-WithNativeAnimationPreference -Enabled $case.desired -ReadPreference $read -WritePreference $write -Action $body
        } catch { $failure = $_.Exception.ToString() }
        if ($case.error) {
            Assert-True ($failure -like "*$($case.error)*") "$($case.name): expected '$($case.error)', got '$failure'."
        } else {
            Assert-True ($null -eq $failure) "$($case.name): unexpected failure '$failure'."
        }
        Assert-True (($state.writes -join ',') -ceq $case.expectedWrites) "$($case.name): incorrect mutation/restoration order."
        if (-not $case.restoreFails -and -not $case.restoreNoEffect) {
            Assert-True ($state.value -eq $case.initial) "$($case.name): original state was not restored."
        }
        if ($case.readFails -or $case.setFails -or $case.noEffect) {
            Assert-True ($state.bodyCalls -eq 0) "$($case.name): tests started without a verified prerequisite."
        } else {
            Assert-True ($state.bodyCalls -eq 1) "$($case.name): test body did not run exactly once."
        }
        Assert-True ($env:OPENCLAW_TEST_ANIMATION_PREFERENCE -eq 'original-marker') 'Parent environment was not restored.'
    }

    $preserved = [pscustomobject]@{ calls = 0 }
    Invoke-WithNativeAnimationPreference -PreservePreference `
        -ReadPreference { throw 'Preserve must not read the preference.' } `
        -WritePreference { throw 'Preserve must not write the preference.' } `
        -Action { $preserved.calls++ }
    Assert-True ($preserved.calls -eq 1) 'Preserve did not run its body.'
    $preserveFailed = $false
    try {
        Invoke-WithNativeAnimationPreference -PreservePreference -Action { throw 'preserve body failed' }
    } catch {
        if ($_.Exception.Message -notlike '*preserve body failed*') { throw }
        $preserveFailed = $true
    }
    Assert-True $preserveFailed 'Preserve swallowed the child operation failure.'

    # Competing mutating and ordinary UI processes must fail before running tests or touching User32.
    $mutex = [Threading.Mutex]::new($false, [OpenClaw.Testing.NativeAnimationPreference]::MutexName())
    Assert-True ($mutex.WaitOne(0)) 'Could not acquire the contention-test lock.'
    try {
        $helper = (Join-Path $PSScriptRoot 'NativeAnimationPreference.ps1').Replace("'", "''")
        $command = @"
. '$helper'
try {
    Invoke-WithNativeAnimationPreference -Enabled `$true -Action { throw 'body must not run' }
    exit 2
} catch {
    if (`$_.Exception.Message -like '*Another test process owns*') { exit 0 }
    throw
}
"@
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -EncodedCommand $encoded
        Assert-True ($LASTEXITCODE -eq 0) 'The competing process did not fail closed at the ownership lock.'
        $runnerPath = (Join-Path $PSScriptRoot 'Invoke-CiTest.ps1').Replace("'", "''")
        $repoRoot = Split-Path -Parent $PSScriptRoot
        $resultDirectory = Join-Path ([IO.Path]::GetTempPath()) ("OpenClaw.AnimationLock." + [guid]::NewGuid().ToString('N'))
        try {
            foreach ($case in @(
                @{ project = 'tests\OpenClaw.Tray.UITests'; directory = $repoRoot },
                @{ project = 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj'; directory = $repoRoot },
                @{ project = '.\tests\OpenClaw.Tray.UITests\.'; directory = $repoRoot },
                @{ project = '.'; directory = (Join-Path $repoRoot 'tests\OpenClaw.Tray.UITests') }
            )) {
                $command = @"
Set-Location '$($case.directory.Replace("'", "''"))'
try {
    & '$runnerPath' -Project '$($case.project)' -ResultsDirectory '$($resultDirectory.Replace("'", "''"))' -TrxFileName blocked.trx
    exit 2
} catch {
    if (`$_.Exception.Message -like '*Another test process owns*') { exit 0 }
    throw
}
"@
                $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
                & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -EncodedCommand $encoded
                Assert-True ($LASTEXITCODE -eq 0) "Ordinary UI wrapper '$($case.project)' bypassed animation ownership."
            }
            $command = @"
Set-Location '$($repoRoot.Replace("'", "''"))'
function dotnet { `$global:LASTEXITCODE = 73 }
& '$runnerPath' -Project tests\OpenClaw.Shared.Tests -ResultsDirectory '$($resultDirectory.Replace("'", "''"))' -TrxFileName managed.trx
if (`$LASTEXITCODE -eq 73) { exit 0 }
exit 2
"@
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
            & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -EncodedCommand $encoded
            Assert-True ($LASTEXITCODE -eq 0) 'Unrelated managed tests must dispatch without acquiring native animation ownership.'
        } finally {
            if (Test-Path -LiteralPath $resultDirectory) { [IO.Directory]::Delete($resultDirectory, $true) }
        }
    } finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }

    $actions = $env:GITHUB_ACTIONS
    $runner = $env:RUNNER_ENVIRONMENT
    try {
        $env:GITHUB_ACTIONS = 'false'
        $env:RUNNER_ENVIRONMENT = 'self-hosted'
        $refused = $false
        try {
            & (Join-Path $PSScriptRoot 'Invoke-CiTest.ps1') -Project 'not-run' -ResultsDirectory 'not-created' `
                -TrxFileName 'not-run.trx' -AnimationPreference Enabled
        } catch {
            if ($_.Exception.Message -notlike '*explicit -AllowUserAnimationPreferenceChange*') { throw }
            $refused = $true
        }
        Assert-True $refused 'The runner must require explicit local authorization before a native preference change.'
    } finally {
        $env:GITHUB_ACTIONS = $actions
        $env:RUNNER_ENVIRONMENT = $runner
    }
} finally {
    $env:OPENCLAW_TEST_ANIMATION_PREFERENCE = $previous
}
Write-Host 'Native animation prerequisite tests passed: 10 transaction cases, 2 preserve cases, 5 cross-process exclusions, managed nonserialization, local authorization. No Windows preference changed.'
