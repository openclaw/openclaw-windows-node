[CmdletBinding()]
param([Parameter(Mandatory)][string]$ResultPath, [string]$ReportsDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expected = @(
    'C01.NavigationPreservesPreferences',
    'C04.PreferenceOffToOnSurvivesRestart',
    'C04.PreferenceOnToOffSurvivesRestart',
    'C01.McpOnlyStartsWithoutGatewayCredential',
    'C01.UnavailableGatewayStillOpensWorkspace',
    'C03.SendStreamsIntoSelectedConversation',
    'C03.CancelStopsOutputAndAllowsNextSend',
    'C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer'
)
if (-not [string]::IsNullOrWhiteSpace($ReportsDirectory)) {
    foreach ($caseId in $expected) {
        $path = Join-Path $ReportsDirectory "$caseId.json"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required case evidence is missing: $caseId" }
        $case = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($case.CaseId -cne $caseId) { throw 'Case report identity does not match its required manifest entry.' }
        if ($case.Behavior -cne 'passed' -or $case.Isolation -cne 'unchanged' -or
            $case.Cleanup -cne 'passed' -or $case.Qualification -cne 'passed') {
            throw "Contract proof is not qualified: $caseId. Behavior=$($case.Behavior); isolation=$($case.Isolation); cleanup=$($case.Cleanup)."
        }
        foreach ($field in @('BehaviorPassed', 'IsolationUnchanged', 'CleanupSucceeded')) {
            if ($case.$field -isnot [bool] -or $case.$field -ne $true) {
                throw "Case evidence has inconsistent $field state: $caseId"
            }
        }
        if ($null -ne $case.Reason -or @($case.Changes).Count -ne 0) {
            throw "Case evidence contains a failure or isolation changes: $caseId"
        }
        if ($case.PSObject.Properties.Name -notcontains 'Failures' -or
            $null -eq $case.Failures -or @($case.Failures).Count -ne 0) {
            throw "Case evidence must contain an empty failure-category list: $caseId"
        }
    }
}
if (-not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) { throw 'Contract journey report is missing.' }
[xml]$report = Get-Content -LiteralPath $ResultPath -Raw
$results = @($report.SelectNodes("//*[local-name()='UnitTestResult']"))
if ($results.Count -ne $expected.Count) { throw 'Contract journey case count does not match the required manifest.' }
$names = @($results | ForEach-Object { $_.GetAttribute('testName') })
if (@($names | Sort-Object -Unique).Count -ne $names.Count -or (Compare-Object $expected $names)) {
    throw 'Contract journey case IDs are missing, duplicated, or unexpected.'
}
foreach ($result in $results) {
    if ($result.GetAttribute('outcome') -cne 'Passed') {
        throw "Required contract journey '$($result.GetAttribute('testName'))' did not pass."
    }
}
$summary = $report.SelectSingleNode("//*[local-name()='ResultSummary']")
if ($null -eq $summary -or $summary.GetAttribute('outcome') -cne 'Completed') {
    throw 'Contract journey run did not complete successfully.'
}
$counters = $summary.SelectSingleNode("*[local-name()='Counters']")
if ($null -eq $counters) { throw 'Contract journey summary counters are missing.' }
foreach ($field in @('total', 'executed', 'passed', 'failed', 'error', 'aborted', 'notExecuted')) {
    [int]$count = 0
    $required = if ($field -in @('total', 'executed', 'passed')) { $expected.Count } else { 0 }
    if (-not [int]::TryParse($counters.GetAttribute($field), [ref]$count) -or $count -ne $required) {
        throw "Contract journey summary '$field' does not match the required result set."
    }
}
Write-Host "Case/TRX admission passed for $($expected.Count) required cases. Final qualification is still pending."
