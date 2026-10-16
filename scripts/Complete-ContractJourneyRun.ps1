[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReportsDirectory,
    [Parameter(Mandatory)][string]$ResultPath,
    [Parameter(Mandatory)][string]$BeforeIdentityPath,
    [Parameter(Mandatory)][string]$AfterIdentityPath,
    [Parameter(Mandatory)][string]$ArtifactsDirectory,
    [string]$RunnerFailure
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$errors = [Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($RunnerFailure)) { $errors.Add($RunnerFailure) }
try {
    & (Join-Path $PSScriptRoot 'Assert-ContractJourneyResults.ps1') -ResultPath $ResultPath -ReportsDirectory $ReportsDirectory
} catch { $errors.Add($_.Exception.Message) }
try {
    & (Join-Path $PSScriptRoot 'Assert-ContractCaptureEvidence.ps1') -ReportsDirectory $ReportsDirectory `
        -ArtifactsDirectory $ArtifactsDirectory
} catch { $errors.Add($_.Exception.Message) }
try {
    $before = Get-Content -LiteralPath $BeforeIdentityPath -Raw | ConvertFrom-Json
    $after = Get-Content -LiteralPath $AfterIdentityPath -Raw | ConvertFrom-Json
    if ($before.Head -cne $after.Head -or
        ($before.Dirty | ConvertTo-Json -Depth 5 -Compress) -cne ($after.Dirty | ConvertTo-Json -Depth 5 -Compress) -or
        ($before.Payloads | ConvertTo-Json -Depth 5 -Compress) -cne ($after.Payloads | ConvertTo-Json -Depth 5 -Compress) -or
        $before.AppIdentity -cne $after.AppIdentity -or $before.Architecture -cne $after.Architecture -or
        $before.OsVersion -cne $after.OsVersion -or $before.LvtVersion -cne $after.LvtVersion) {
        throw 'Source or complete app/test/driver payload changed during proof. Evidence is stale.'
    }
    if (@($before.Payloads | Where-Object Role -ceq 'app').Count -eq 0 -or
        @($before.Payloads | Where-Object Role -ceq 'tests').Count -eq 0 -or
        @($before.Payloads | Where-Object Role -ceq 'driver').Count -eq 0) {
        throw 'Proof identity is missing a required payload role.'
    }
    foreach ($role in @('app','tests','driver')) {
        $payload = @($before.Payloads | Where-Object Role -ceq $role)
        $names = @($payload | ForEach-Object Path)
        if (@($names | Sort-Object -Unique).Count -ne $names.Count) { throw 'Proof payload contains duplicate paths.' }
        foreach ($entry in $payload) {
            if ($entry.Sha256 -notmatch '^[A-F0-9]{64}$' -or
                [IO.Path]::IsPathRooted($entry.Path) -or $entry.Path -match '(^|[\\/])\.\.([\\/]|$)') {
                throw 'Proof payload contains invalid hashes or escaping paths.'
            }
        }
    }
} catch { $errors.Add($_.Exception.Message) }
& (Join-Path $PSScriptRoot 'Write-ContractJourneySummary.ps1') -ReportsDirectory $ReportsDirectory `
    -FinalGatePassed ($errors.Count -eq 0) -GateFailures $errors.ToArray()
if ($errors.Count -gt 0) {
    throw "Contract qualification did not pass. See suite-summary.json. $($errors -join ' | ')"
}
