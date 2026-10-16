[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'Assert-ContractJourneyResults.ps1'
$directory = Join-Path ([IO.Path]::GetTempPath()) ("contract-result-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$path = Join-Path $directory 'synthetic.trx'
$reports = Join-Path $directory 'cases'
New-Item -ItemType Directory -Path $reports | Out-Null
$cases = @('C01.NavigationPreservesPreferences', 'C04.PreferenceOffToOnSurvivesRestart', 'C04.PreferenceOnToOffSurvivesRestart',
    'C01.McpOnlyStartsWithoutGatewayCredential', 'C01.UnavailableGatewayStillOpensWorkspace',
    'C03.SendStreamsIntoSelectedConversation', 'C03.CancelStopsOutputAndAllowsNextSend',
    'C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer')

function Write-Report([string[]]$Names, [string]$Outcome = 'Passed', [string]$Summary = 'Completed') {
    $rows = $Names | ForEach-Object { "<UnitTestResult testName='$_' outcome='$Outcome' />" }
    [IO.File]::WriteAllText($path, "<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'><Results>$($rows -join '')</Results><ResultSummary outcome='$Summary'><Counters total='$($Names.Count)' executed='$($Names.Count)' passed='$($Names.Count)' failed='0' error='0' aborted='0' notExecuted='0'/></ResultSummary></TestRun>")
}
function Assert-Rejected([string]$Scenario, [switch]$PhaseEvidence) {
    $failed = $false
    try {
        if ($PhaseEvidence) { & $validator -ResultPath $path -ReportsDirectory $reports }
        else { & $validator -ResultPath $path }
    }
    catch { $failed = $true }
    if (-not $failed) { throw "$Scenario was incorrectly accepted." }
}
function Write-PhaseReports {
    foreach ($caseId in $cases) {
        @{ CaseId = $caseId; BehaviorPassed = $true; IsolationUnchanged = $true; CleanupSucceeded = $true;
            Behavior = 'passed'; Isolation = 'unchanged'; Cleanup = 'passed'; Qualification = 'passed';
            Reason = $null; Changes = @(); Failures = @() } | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $reports "$caseId.json") -Encoding utf8
    }
}
try {
    Write-Report $cases
    & $validator -ResultPath $path
    Write-Report @()
    Assert-Rejected 'Zero cases'
    Write-Report $cases[0..1]
    Assert-Rejected 'Missing required case'
    Write-Report @($cases | Where-Object { $_ -cne 'C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer' })
    Assert-Rejected 'Previous seven-case suite omits required gateway switching'
    $duplicate = $cases.Clone()
    $duplicate[1] = $duplicate[0]
    Write-Report $duplicate
    Assert-Rejected 'Duplicate case'
    $unexpected = $cases.Clone()
    $unexpected[2] = 'Unexpected.Case'
    Write-Report $unexpected
    Assert-Rejected 'Unexpected case'
    foreach ($outcome in @('NotExecuted', 'Skipped', 'Failed', 'Aborted', 'passed', '')) {
        Write-Report $cases -Outcome $outcome
        Assert-Rejected "Outcome $outcome"
    }
    Write-Report $cases -Summary 'Aborted'
    Assert-Rejected 'Incomplete run'
    Write-Report $cases
    [xml]$report = Get-Content -LiteralPath $path -Raw
    $report.TestRun.ResultSummary.Counters.passed = [string]($cases.Count + 1)
    $report.Save($path)
    Assert-Rejected 'Inconsistent counters'
    Write-Report $cases
    [xml]$report = Get-Content -LiteralPath $path -Raw
    $null = $report.TestRun.ResultSummary.RemoveChild($report.TestRun.ResultSummary.Counters)
    $report.Save($path)
    Assert-Rejected 'Missing counters'
    Remove-Item -LiteralPath $path
    Assert-Rejected 'Missing report'
    Write-Report $cases
    Write-PhaseReports
    & $validator -ResultPath $path -ReportsDirectory $reports
    foreach ($field in @('BehaviorPassed', 'IsolationUnchanged', 'CleanupSucceeded')) {
        Write-PhaseReports
        $phasePath = Join-Path $reports "$($cases[0]).json"
        $case = Get-Content -LiteralPath $phasePath -Raw | ConvertFrom-Json
        $case.$field = $false
        $case | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $phasePath -Encoding utf8
        Assert-Rejected "False $field hidden by passed strings" -PhaseEvidence
        Write-PhaseReports
        $case = Get-Content -LiteralPath $phasePath -Raw | ConvertFrom-Json
        $case.$field = 'true'
        $case | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $phasePath -Encoding utf8
        Assert-Rejected "String $field hidden by passed strings" -PhaseEvidence
    }
    Write-PhaseReports
    $phasePath = Join-Path $reports "$($cases[0]).json"
    $case = Get-Content -LiteralPath $phasePath -Raw | ConvertFrom-Json
    $case.Isolation = 'changed'
    $case.Qualification = 'blocked'
    $case.IsolationUnchanged = $false
    $case.Reason = 'Shared profile metadata changed.'
    $case | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $phasePath -Encoding utf8
    Assert-Rejected 'Passed behavior with failed isolation' -PhaseEvidence
    & (Join-Path $PSScriptRoot 'Write-ContractJourneySummary.ps1') -ReportsDirectory $reports
    $summary = Get-Content -LiteralPath (Join-Path $reports 'suite-summary.json') -Raw | ConvertFrom-Json
    if ($summary.Qualification -cne 'blocked' -or $summary.Cases[0].Behavior -cne 'passed') {
        throw 'Blocked proof incorrectly changed the observed behavior outcome.'
    }
    foreach ($field in @('Reason', 'Changes', 'Failures')) {
        Write-PhaseReports
        $case = Get-Content -LiteralPath $phasePath -Raw | ConvertFrom-Json
        if ($field -eq 'Reason') { $case.Reason = 'failure' }
        elseif ($field -eq 'Changes') { $case.Changes = @(@{ Path = 'profile\synthetic'; Kind = 'added'; Before = $null; After = @{Size=1;LastWriteTimeUtc='2000-01-01'} }) }
        else { $case.Failures = @(@{ Category = 'capture'; Detail = 'missing screenshot' }) }
        $case | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $phasePath -Encoding utf8
        Assert-Rejected "Failure evidence in $field" -PhaseEvidence
    }
    Write-PhaseReports
    $case = Get-Content -LiteralPath $phasePath -Raw | ConvertFrom-Json
    $case.CaseId = 'Unexpected.Case'
    $case | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $phasePath -Encoding utf8
    Assert-Rejected 'Wrong phase report identity' -PhaseEvidence
    Write-PhaseReports
    $case = Get-Content -LiteralPath $phasePath -Raw | ConvertFrom-Json
    $case.Behavior = 'not-run'
    $case.BehaviorPassed = $null
    $case.Qualification = 'blocked'
    $case | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $phasePath -Encoding utf8
    Assert-Rejected 'Not-run phase disguised by passing TRX' -PhaseEvidence
    Write-PhaseReports
    Remove-Item -LiteralPath $phasePath
    Assert-Rejected 'Missing phase evidence' -PhaseEvidence
    Write-PhaseReports
    Remove-Item -LiteralPath (Join-Path $reports 'C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer.json')
    Assert-Rejected 'Missing gateway-switch phase evidence' -PhaseEvidence
    Write-Host 'Contract journey result tests passed: 15 invalid TRX shapes, 14 invalid phase reports, and blocked-summary classification.'
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}
