[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$complete = Join-Path $PSScriptRoot 'Complete-ContractJourneyRun.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('proof-finalization-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$before = Join-Path $root 'before.json'
$after = Join-Path $root 'after.json'
$trx = Join-Path $root 'proof.trx'
$caseIds = @('C01.NavigationPreservesPreferences','C04.PreferenceOffToOnSurvivesRestart','C04.PreferenceOnToOffSurvivesRestart',
    'C01.McpOnlyStartsWithoutGatewayCredential','C01.UnavailableGatewayStillOpensWorkspace',
    'C03.SendStreamsIntoSelectedConversation','C03.CancelStopsOutputAndAllowsNextSend',
    'C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer')
Add-Type -AssemblyName System.Drawing

function Write-ValidFixture([string]$Architecture = 'X64') {
    foreach ($caseId in $caseIds) {
        $runId = [Guid]::NewGuid().ToString('N')
        $runRoot = Join-Path $root $runId
        New-Item -ItemType Directory -Path $runRoot | Out-Null
        $images = if ($caseId -ceq 'C01.NavigationPreservesPreferences') { @('navigation-settings.png','navigation-restored.png') }
            elseif($caseId -ceq 'C01.McpOnlyStartsWithoutGatewayCredential'){@('mcp-only-startup.png')}
            elseif($caseId -ceq 'C01.UnavailableGatewayStillOpensWorkspace'){@('unavailable-gateway-startup.png')}
            elseif($caseId -ceq 'C03.SendStreamsIntoSelectedConversation'){@('chat-pending.png','chat-streaming.png','chat-final.png')}
            elseif($caseId -ceq 'C03.CancelStopsOutputAndAllowsNextSend'){@('chat-before-cancel.png','chat-cancelled.png','chat-after-cancel.png')}
            elseif($caseId -ceq 'C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer'){@('two-peer-before-switch.png','two-peer-after-switch.png')}
            else { @('preference-edited.png','preference-restarted.png') }
        $checkpoints = @($images | ForEach-Object {
            $image = Join-Path $runRoot $_
            $bitmap = [Drawing.Bitmap]::new(420,320)
            try { $bitmap.Save($image,[Drawing.Imaging.ImageFormat]::Png) }
            finally { $bitmap.Dispose() }
            $captured = [DateTimeOffset]::UtcNow
            @{RunId=$runId;sha256=(Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash;
                sampledContentColors=16;bounds=@{Width=420;Height=320};capturedAtUtc=$captured;
                assertions=@(@{AutomationId=if($_ -ceq 'navigation-settings.png') {'SettingsPageMarker'}
                    elseif($_ -in @('mcp-only-startup.png','unavailable-gateway-startup.png')){'WorkspaceNavigation'}
                    elseif($_ -like 'chat-*.png'){'ChatComposerPrimaryAction'}
                    elseif($_ -ceq 'two-peer-before-switch.png'){'SavedGatewayConnectAction'}
                    elseif($_ -ceq 'two-peer-after-switch.png'){'WorkspaceNavigation'}
                    else {'SettingsPageShowToolCalls'};
                    left=20;top=70;Width=40;Height=30})} | ConvertTo-Json -Depth 6 |
                Set-Content -LiteralPath ([IO.Path]::ChangeExtension($image,'.json')) -Encoding utf8
            @{Name="capture:$_";ObservedAtUtc=$captured.AddMilliseconds(1)}
        })
        @{CaseId=$caseId;BehaviorPassed=$true;IsolationUnchanged=$true;CleanupSucceeded=$true;
            Behavior='passed';Isolation='unchanged';Cleanup='passed';Qualification='passed';
            Reason=$null;Changes=@();Failures=@();RunId=$runId;Checkpoints=$checkpoints} | ConvertTo-Json -Depth 6 |
            Set-Content -LiteralPath (Join-Path $root "$caseId.json") -Encoding utf8
    }
    $rows = $caseIds | ForEach-Object { "<UnitTestResult testName='$_' outcome='Passed' />" }
    [IO.File]::WriteAllText($trx,"<TestRun><Results>$($rows -join '')</Results><ResultSummary outcome='Completed'><Counters total='$($caseIds.Count)' executed='$($caseIds.Count)' passed='$($caseIds.Count)' failed='0' error='0' aborted='0' notExecuted='0'/></ResultSummary></TestRun>")
    $identity = @{Head='synthetic-head';Dirty=@(@{Path='source.cs';Sha256='OLD'});AppIdentity='release';
        Architecture=$Architecture;OsVersion='synthetic';LvtVersion='0.5.0';Payloads=@(
            @{Role='app';Path='OpenClaw.Tray.WinUI.exe';Sha256='APP'},
            @{Role='app';Path='Pages\SettingsPage.xbf';Sha256='XBF'},
            @{Role='app';Path='resources.pri';Sha256='PRI'},
            @{Role='tests';Path='OpenClaw.Tray.UITests.dll';Sha256='TESTS'},
            @{Role='tests';Path='OpenClaw.TestSupport.dll';Sha256='SUPPORT'},
            @{Role='driver';Path='lvt.exe';Sha256='DRIVER'},
            @{Role='driver';Path="lvt_tap_$($Architecture.ToLowerInvariant()).dll";Sha256='TAP'})}
    foreach($entry in $identity.Payloads) {
        $entry.Sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($entry.Sha256)))
    }
    $identity | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $before -Encoding utf8
    $identity | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $after -Encoding utf8
}
function Invoke-Complete([string]$Failure = '') {
    & $complete -ReportsDirectory $root -ResultPath $trx -BeforeIdentityPath $before `
        -AfterIdentityPath $after -ArtifactsDirectory $root -RunnerFailure $Failure
}
function Assert-Blocked([string]$Scenario, [string]$Failure = '') {
    $rejected = $false
    try { Invoke-Complete $Failure | Out-Null } catch { $rejected=$true }
    if (-not $rejected) { throw "$Scenario unexpectedly qualified." }
    $summary = Get-Content -LiteralPath (Join-Path $root 'suite-summary.json') -Raw | ConvertFrom-Json
    if ($summary.Qualification -ceq 'passed' -or $summary.FinalGatePassed -ne $false -or
        @($summary.GateFailures).Count -eq 0) { throw "$Scenario retained a misleading passed summary." }
}
try {
    foreach ($architecture in @('X64', 'Arm64')) {
        Write-ValidFixture $architecture
        Invoke-Complete | Out-Null
        $summary=Get-Content -LiteralPath (Join-Path $root 'suite-summary.json') -Raw | ConvertFrom-Json
        if ($summary.Qualification -cne 'passed' -or $summary.FinalGatePassed -ne $true) {
            throw "Valid $architecture run did not finalize."
        }
    }
    foreach ($path in @('OpenClaw.Tray.WinUI.exe','Pages\SettingsPage.xbf','resources.pri',
        'OpenClaw.Tray.UITests.dll','OpenClaw.TestSupport.dll','lvt.exe','lvt_tap_x64.dll')) {
        Write-ValidFixture
        $identity=Get-Content -LiteralPath $after -Raw | ConvertFrom-Json
        ($identity.Payloads | Where-Object Path -ceq $path).Sha256='CHANGED'
        $identity | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $after -Encoding utf8
        Assert-Blocked "Payload drift: $path"
    }
    foreach($field in @('Head','AppIdentity','Architecture','OsVersion','LvtVersion')) {
        Write-ValidFixture
        $identity=Get-Content -LiteralPath $after -Raw | ConvertFrom-Json
        $identity.$field='changed'
        $identity | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $after -Encoding utf8
        Assert-Blocked "Identity drift: $field"
    }
    Write-ValidFixture
    $identity=Get-Content -LiteralPath $after -Raw | ConvertFrom-Json
    $identity.Dirty[0].Sha256='CHANGED'
    $identity | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $after -Encoding utf8
    Assert-Blocked 'Dirty source drift'
    Write-ValidFixture
    Assert-Blocked 'Nonzero test process' 'Synthetic testhost exited with code 1.'
    Write-ValidFixture
    Remove-Item -LiteralPath $trx
    Assert-Blocked 'Missing TRX despite passed cases'
    Write-ValidFixture
    Remove-Item -LiteralPath $after
    Assert-Blocked 'Missing final build identity'
    Write-ValidFixture
    $identity=Get-Content -LiteralPath $before -Raw | ConvertFrom-Json
    $identity.Payloads=@($identity.Payloads | Where-Object Role -cne 'tests')
    $identity | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $before -Encoding utf8
    $identity | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $after -Encoding utf8
    Assert-Blocked 'Missing test payload role'
    Write-ValidFixture
    $case=Get-Content -LiteralPath (Join-Path $root "$($caseIds[0]).json") -Raw | ConvertFrom-Json
    $image=Join-Path (Join-Path $root $case.RunId) 'navigation-settings.png'
    Remove-Item -LiteralPath $image
    Assert-Blocked 'Missing capture despite passing cases'
    foreach($name in @('two-peer-before-switch','two-peer-after-switch')) {
        Write-ValidFixture
        $case=Get-Content -LiteralPath (Join-Path $root 'C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer.json') -Raw | ConvertFrom-Json
        $image=Join-Path (Join-Path $root $case.RunId) "$name.png"
        Remove-Item -LiteralPath $image
        Assert-Blocked "Missing gateway-switch capture: $name"
        Write-ValidFixture
        $case=Get-Content -LiteralPath (Join-Path $root 'C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer.json') -Raw | ConvertFrom-Json
        $metadata=Join-Path (Join-Path $root $case.RunId) "$name.json"
        $proof=Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
        $proof.assertions[0].AutomationId='WrongControl'
        $proof | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadata -Encoding utf8
        Assert-Blocked "Wrong gateway-switch asserted control: $name"
    }
    Write-ValidFixture
    $case=Get-Content -LiteralPath (Join-Path $root "$($caseIds[0]).json") -Raw | ConvertFrom-Json
    $image=Join-Path (Join-Path $root $case.RunId) 'navigation-settings.png'
    [IO.File]::AppendAllText($image,'changed')
    Assert-Blocked 'Changed capture image'
    foreach($field in @('RunId','sampledContentColors','capturedAtUtc')) {
        Write-ValidFixture
        $case=Get-Content -LiteralPath (Join-Path $root "$($caseIds[0]).json") -Raw | ConvertFrom-Json
        $metadata=Join-Path (Join-Path $root $case.RunId) 'navigation-settings.json'
        $proof=Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
        if($field -ceq 'RunId'){$proof.RunId='wrong'}
        elseif($field -ceq 'sampledContentColors'){$proof.sampledContentColors=1}
        else{$proof.capturedAtUtc='2000-01-01T00:00:00Z'}
        $proof | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadata -Encoding utf8
        Assert-Blocked "Invalid capture $field"
    }
    Write-Host 'Proof finalization passed: 26 invalid final-gate conditions rejected with non-passing summaries.'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
