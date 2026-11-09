# Pure parser/projection policy tests only. No native desktop call or process launch.
$checks = 0
function Confirm([bool]$Condition) { if (-not $Condition) { throw 'inventory-self-test-failed' }; $script:checks++ }
function New-CompleteQuery([string]$Kind, [string]$Text) {
    $q = New-SkippedQuery $Kind
    $q.outcome = 'completed'; $q.rootStarted = $true; $q.handleRetained = $true
    $q.rootExitObserved = $true; $q.exitCode = 0; $q.privateOut = $Text; $q.privateError = ''
    return $q
}
$SourceRevision = '0123456789abcdef0123456789abcdef01234567'; $HostSelector = 'rtx-2'; $ExpectedArchitecture = 'arm64'
Confirm (Test-SourceRevision $SourceRevision)
Confirm (-not (Test-SourceRevision ($SourceRevision + [Environment]::NewLine)))
Confirm (-not (Test-SourceRevision ('A' * 40)))
Confirm ((Convert-SafeVersion '10.0.28000') -ceq '10.0.28000')
Confirm ($null -eq (Convert-SafeVersion 'PRIVATE_VERSION'))
Confirm ($null -eq (Convert-SafeBuild '28000-PRIVATE'))
$base = New-InventoryReport '10.0.28000' '28000' 1 'Arm64' 'Arm64' $true $true $true
$baseJson = Get-SafeJson $base
$roundTrip = ConvertFrom-Json -InputObject $baseJson -AsHashtable
Confirm ($roundTrip.schema -eq 3 -and $roundTrip.os.build -eq 28000 -and $roundTrip.os.productType -eq 1)
Confirm ($roundTrip.landingQualified -is [bool] -and $roundTrip.landingQualified -eq $false -and $roundTrip.mxcRuntime -ceq 'unknown')
$nl = [Environment]::NewLine
$v = New-CompleteQuery version 'WSL version: 2.4.4.0'
$s = New-CompleteQuery status ('Default Distribution: PRIVATE_DISTRO' + $nl + 'Default Version: 2')
$l = New-CompleteQuery list ('  NAME  STATE  VERSION' + $nl + '* PRIVATE_ONE  Running  2' + $nl + '  PRIVATE_TWO  Stopped  1')
$ready = Get-WslReadiness $v $s $l
$expected = '{"kind":"Ready","version":"2.4.4.0","defaultVersion":2,"distributions":{"parse":"recognized","count":2,"wsl1":1,"wsl2":1,"running":1,"stopped":1}}'
Confirm ((Get-SafeJson $ready) -ceq $expected)
Confirm ((Get-SafeJson $ready) -cnotmatch 'PRIVATE')
Confirm ((Read-WslVersion 'Version de WSL : 2.6.3') -ceq '2.6.3')
Confirm ($null -eq (Read-WslVersion ('WSL version: 2.4.4' + $nl + 'WSL version: 2.5.1')))
Confirm ($null -eq (Read-WslVersion 'WSLg version: 2.4.4'))
Confirm ($null -eq (Read-WslVersion 'WSL version: 2.4.4.5.6'))
Confirm ((Get-WslReadiness (New-CompleteQuery version 'WSL version: 2.4.3') $s $l).kind -ceq 'UpdateRequired')
Confirm ((Get-WslReadiness (New-CompleteQuery version 'WSL version: 2.4.4') $s $l).kind -ceq 'Ready')
Confirm ((Get-WslReadiness (New-CompleteQuery version ('WSL version: 2.6.3' + $nl + 'Press any key to install')) $s $l).kind -ceq 'PromptOrConsentRequired')
Confirm ((Read-StatusReadiness 'Default Version: 1' '').kind -ceq 'DefaultVersionNot2')
Confirm ((Read-StatusReadiness ('Default Version: 2' + $nl + 'Default Version: 2') '').kind -ceq 'InspectionFailed')
Confirm ((Read-StatusReadiness 'Version par defaut: 2' '').kind -ceq 'InspectionFailed')
Confirm ((Read-StatusReadiness ('Default Version: 2' + $nl + '0x80370102') '').kind -ceq 'EnvironmentBlocked')
Confirm ((Read-StatusReadiness ('Default Version: 2' + $nl + 'WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED') '').kind -ceq 'Installable')
Confirm ((Read-StatusReadiness ('Default Version: 2' + $nl + 'UNKNOWN_ADVICE') '').kind -ceq 'InspectionFailed')
Confirm ((Read-StatusReadiness 'Default Version: 2' 'PRIVATE_STDERR').kind -ceq 'InspectionFailed')
Confirm ((Read-StatusReadiness ('Default Distribution: not installed PRIVATE_NAME' + $nl + 'Default Version: 2') '').kind -ceq 'Ready')
Confirm ((Read-ListSummary 'NAME STATE VERSION').count -eq 0)
Confirm ((Read-ListSummary '').parse -ceq 'unknown')
Confirm ((Read-ListSummary ('NAME STATE VERSION' + $nl + 'PRIVATE  Unknown  2')).parse -ceq 'unknown')
Confirm ((Read-ListSummary ('NAME STATE VERSION' + $nl + 'PRIVATE  Running  3')).parse -ceq 'unknown')
Confirm ((Read-ListSummary ('NAME STATE VERSION' + $nl + ((1..129 | ForEach-Object { 'PRIVATE  Running  2' }) -join $nl))).parse -ceq 'unknown')
$nonzero = New-CompleteQuery version 'WSL is not installed'; $nonzero.outcome = 'nonzero'; $nonzero.exitCode = 17
Confirm ((Get-WslReadiness $nonzero $s $l).kind -ceq 'Installable')
Confirm ((Get-PublicQuery $nonzero).exitCode -eq 17 -and (Get-SafeJson (Get-PublicQuery $nonzero)) -cnotmatch 'not installed|privateOut|privateError')
$unsettled = New-CompleteQuery version 'WSL version: 2.6.3'; $unsettled.settled = $false; $unsettled.outcome = 'cleanup-unconfirmed'; $unsettled.rootExitObserved = $null; $unsettled.exitCode = $null
Confirm ((Get-WslReadiness $unsettled $s $l).kind -ceq 'InspectionFailed')
Confirm ($null -eq (Get-PublicQuery $unsettled).rootExitObserved -and $null -eq (Get-PublicQuery $unsettled).exitCode)
Confirm ((Get-WslReadiness $v $s (New-SkippedQuery list)).kind -ceq 'InspectionFailed')
Confirm ((Convert-QueryText ([Text.Encoding]::UTF8.GetBytes('Default Version: 2'))) -ceq 'Default Version: 2')
Confirm ((Convert-QueryText ([Text.Encoding]::Unicode.GetBytes('Default Version: 2'))) -ceq 'Default Version: 2')
Confirm ($null -eq (Convert-QueryText ([byte[]]@(0xC3, 0x28))))
Confirm ($null -eq (Convert-QueryText ([byte[]]::new($MaxQueryBytes + 1))))
$ambient = Get-AmbientPresence @('PATH', 'UNRELATED_PRIVATE_NAME')
Confirm (Test-AmbientClear $ambient)
$unsafe = Get-AmbientPresence @('openclaw_setup_PRIVATE_KEY', 'OPENCLAW_STATE_DIR', 'OPENCLAW_E2E_TRAY_EXE', 'OPENCLAW_E2E_GATEWAY_VERSION', 'OPENCLAW_E2E_GATEWAY_PACKAGE_TGZ', 'OPENCLAW_E2E_PROTOCOL_MISMATCH_RESULT')
Confirm ($unsafe.setupOverrides -and $unsafe.stateDirectoryOverride -and $unsafe.trayExecutableOverride -and $unsafe.gatewayVersionOverride -and $unsafe.gatewayPackageOverride -and $unsafe.protocolResultOverride)
Confirm (-not (Test-AmbientClear $unsafe) -and (Get-SafeJson $unsafe) -cnotmatch 'PRIVATE_KEY|OPENCLAW_')
$desktop = @{ accessible = $true; handleClosed = $true }
Confirm (Test-ReadOnlyQualification $base $desktop $ambient $ready)
Confirm (-not (Test-ReadOnlyQualification $base @{ accessible = $false; handleClosed = $null } $ambient $ready))
Confirm (-not (Test-ReadOnlyQualification $base @{ accessible = $null; handleClosed = $null } $ambient $ready))
Confirm (-not (Test-ReadOnlyQualification $base @{ accessible = $true; handleClosed = $false } $ambient $ready))
Confirm (-not (Test-ReadOnlyQualification $base $desktop $unsafe $ready))
$unknownAmbient = Get-AmbientPresence @(); $unknownAmbient.observed = $false
Confirm (-not (Test-ReadOnlyQualification $base $desktop $unknownAmbient $ready))
$base.session.userInteractive = $false
Confirm (-not (Test-ReadOnlyQualification $base $desktop $ambient $ready))
$limitRejected = $false
try { $null = Get-SafeJson @{ text = ('x' * ($MaxJsonBytes + 1)) } } catch { $limitRejected = $true }
Confirm $limitRejected
# Exercise the exact normal-completion statement without starting a query process.
function Test-ExitWaitSuppression([string]$Source) {
    $waits = [regex]::Matches($Source, '(?m)^[ \t]*(?:\$null[ \t]*=[ \t]*)?\$exitTask\.GetAwaiter\(\)\.GetResult\(\)[ \t]*\r?$')
    return $waits.Count -eq 1 -and $waits[0].Value.Trim() -ceq '$null = $exitTask.GetAwaiter().GetResult()'
}
$querySource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'InventoryWsl.ps1') -Raw
Confirm (Test-ExitWaitSuppression $querySource)
$regressedSource = $querySource.Replace('$null = $exitTask.GetAwaiter().GetResult()', '$exitTask.GetAwaiter().GetResult()')
Confirm (-not (Test-ExitWaitSuppression $regressedSource))
$waitStatement = [regex]::Match($querySource, '(?m)^[ \t]*\$null = \$exitTask\.GetAwaiter\(\)\.GetResult\(\)[ \t]*\r?$').Value.Trim()
# Pure completed Task fixture: no process, Win32, WSL, network, delay or filesystem I/O.
Add-Type -TypeDefinition @'
using System.Threading.Tasks;
public static class InventoryCompletedAsyncFixtureV4
{
    public static async Task Complete()
    {
        await Task.CompletedTask;
    }
}
'@ -ErrorAction Stop
$completionTask = [InventoryCompletedAsyncFixtureV4]::Complete()
Confirm $completionTask.IsCompletedSuccessfully
$queryRecord = New-CompleteQuery version 'WSL version: 2.4.4'
$completionBody = [scriptblock]::Create('param($exitTask, $queryRecord)' + [Environment]::NewLine + $waitStatement + [Environment]::NewLine + 'return $queryRecord')
$completionOutput = @(& $completionBody $completionTask $queryRecord)
Confirm ($completionOutput.Count -eq 1)
Confirm ([object]::ReferenceEquals($completionOutput[0], $queryRecord))
Confirm ($completionOutput[0] -is [hashtable] -and $completionOutput[0].outcome -ceq 'completed' -and $completionOutput[0].exitCode -eq 0)
$singleRecord = ConvertFrom-Json -InputObject (Get-SafeJson (Get-PublicQuery $completionOutput[0])) -AsHashtable
Confirm ($singleRecord.Count -eq 9 -and $singleRecord.kind -ceq 'version' -and $singleRecord.exitCode -eq 0 -and $singleRecord.rootExitObserved -is [bool] -and $singleRecord.rootExitObserved)
Write-Output (Get-SafeJson @{ selfTest = 'passed'; checks = $checks })
