[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReportsDirectory,
    [bool]$FinalGatePassed = $false,
    [string[]]$GateFailures = @()
)
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
$cases = @($expected | ForEach-Object {
    $caseId = $_
    $path = Join-Path $ReportsDirectory "$caseId.json"
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        try { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
        catch {
            [pscustomobject]@{ CaseId = $caseId; Behavior = 'unknown'; Isolation = 'unknown';
                Cleanup = 'unknown'; Qualification = 'blocked'; Reason = 'Case evidence is malformed.'; Changes = @() }
        }
    } else {
        [pscustomobject]@{ CaseId = $_; Behavior = 'unknown'; Isolation = 'unknown';
            Cleanup = 'unknown'; Qualification = 'blocked'; Reason = 'Case evidence is missing.'; Changes = @() }
    }
})
$qualification = if ($FinalGatePassed -and $GateFailures.Count -eq 0 -and
    @($cases | Where-Object Qualification -cne 'passed').Count -eq 0) { 'passed' }
    elseif (@($cases | Where-Object Behavior -ceq 'failed').Count -gt 0) { 'failed' } else { 'blocked' }
@{ Qualification = $qualification; FinalGatePassed = $FinalGatePassed;
    GateFailures = $GateFailures; Cases = $cases } | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $ReportsDirectory 'suite-summary.json') -Encoding utf8

Write-Host "`nContract proof summary (separate from test-runner failure counts):"
$cases | Select-Object CaseId,Behavior,Isolation,Cleanup,Qualification | Format-Table -AutoSize |
    Out-String | Write-Host
foreach ($case in $cases) {
    if ($case.Reason) { Write-Host "$($case.CaseId): $($case.Reason)" }
    if ($case.PSObject.Properties.Name -contains 'Failures') {
        foreach ($failure in $case.Failures) { Write-Host "  $($failure.Category): $($failure.Detail)" }
    }
    foreach ($change in $case.Changes) {
        $before = if ($null -eq $change.Before) { 'absent' } else { "$($change.Before.Size) bytes; $($change.Before.LastWriteTimeUtc)" }
        $after = if ($null -eq $change.After) { 'absent' } else { "$($change.After.Size) bytes; $($change.After.LastWriteTimeUtc)" }
        Write-Host "  $($change.Kind): $($change.Path). Before: $before. After: $after."
    }
}
foreach ($failure in $GateFailures) { Write-Host "Final gate: $failure" }
Write-Host "Suite qualification: $qualification. Metadata changes do not identify the process that wrote them."
