# Temporary RTX2 read-only qualification. No product suite, installer, or distro launch.
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$SourceRevision,
    [string]$HostSelector,
    [string]$ExpectedArchitecture
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$CimTimeoutSeconds = 10
$MaxJsonBytes = 8192
. (Join-Path $PSScriptRoot 'InventoryWsl.ps1')

function Test-SourceRevision([string]$Value) {
    return $Value -cmatch '\A[a-f0-9]{40}\z'
}
function Convert-SafeVersion([object]$Value) {
    if ($Value -is [string] -and $Value -cmatch '\A[0-9]{1,5}(\.[0-9]{1,5}){1,3}\z') { return $Value }
    return $null
}
function Convert-SafeBuild([object]$Value) {
    if ($Value -is [string] -and $Value -cmatch '\A[0-9]{1,7}\z') { return [int]$Value }
    return $null
}
function Convert-SafeArchitecture([string]$Value) {
    $value = $Value.ToLowerInvariant()
    if ($value -cin @('x64', 'arm64', 'x86')) { return $value }
    return 'unknown'
}
function Convert-SafeBoolean([object]$Value) {
    if ($Value -is [bool]) { return $Value }
    return $null
}
function New-InventoryReport($Version, $Build, $ProductType, [string]$NativeArchitecture,
    [string]$ProcessArchitecture, $UserInteractive, $NonzeroSession, $WslFilePresent) {
    if (-not (Test-SourceRevision $SourceRevision) -or
        $HostSelector -cne 'rtx-2' -or
        $ExpectedArchitecture -cne 'arm64') { throw 'invalid-input' }
    $versionValue = Convert-SafeVersion $Version
    $buildValue = Convert-SafeBuild $Build
    $typeValue = $null
    if (($ProductType -is [uint32] -or $ProductType -is [int]) -and $ProductType -in @(1, 2, 3)) { $typeValue = [int]$ProductType }
    $native = Convert-SafeArchitecture $NativeArchitecture
    $process = Convert-SafeArchitecture $ProcessArchitecture
    $matches = $native -ceq $process -and $native -ceq $ExpectedArchitecture
    $observed = $null -ne $versionValue -and $null -ne $buildValue -and $null -ne $typeValue
    return [ordered]@{
        schema = 3; sourceRevision = $SourceRevision; hostSelector = $HostSelector
        outcome = $(if ($observed -and $matches) { 'observed-with-limits' } else { 'incomplete' })
        os = [ordered]@{ observed = $observed; version = $versionValue; build = $buildValue; productType = $typeValue; architecture = $native; nativeProcessMatches = $matches }
        session = [ordered]@{ userInteractive = Convert-SafeBoolean $UserInteractive; nonzeroSession = Convert-SafeBoolean $NonzeroSession; unlocked = 'unknown'; restorableOwnedState = 'unknown' }
        wsl = [ordered]@{ systemExecutableExists = Convert-SafeBoolean $WslFilePresent; wsl2 = 'unknown'; distributions = 'unknown'; runningState = 'unknown' }
        mxcRuntime = 'unknown'; gatewayPath = 'unknown'; landingQualified = $false
    }
}
function Get-SafeJson($Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 4 -Compress
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt $MaxJsonBytes) { throw 'report-limit' }
    return $json
}

if ($SelfTest) {
    try {
        . (Join-Path $PSScriptRoot 'Test-ManagedHostInventory.ps1')
        exit 0
    } catch {
        Write-Output (Get-SafeJson @{ selfTest = 'failed' })
        exit 1
    }
}

try {
    # Reject before any host query. The workflow separately fences this SHA to its ref.
    if (-not (Test-SourceRevision $SourceRevision) -or
        $HostSelector -cne 'rtx-2' -or
        $ExpectedArchitecture -cne 'arm64') { throw 'invalid-input' }
    $native = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    $process = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    $version = $null; $build = $null; $type = $null
    $interactive = $null; $nonzero = $null; $wslExists = $null
    if ($IsWindows) {
        try {
            $os = @(Get-CimInstance Win32_OperatingSystem -Property Version, BuildNumber, ProductType -OperationTimeoutSec $CimTimeoutSeconds)
            if ($os.Count -eq 1) { $version = $os[0].Version; $build = $os[0].BuildNumber; $type = $os[0].ProductType }
        } catch { } # No arbitrary CIM object, path, or exception is emitted.
        $current = [Diagnostics.Process]::GetCurrentProcess()
        try { $interactive = [Environment]::UserInteractive; $nonzero = $current.SessionId -gt 0 }
        catch { } finally { $current.Dispose() }
        if ($native -ceq $process -and $native.ToLowerInvariant() -ceq $ExpectedArchitecture) {
            try {
                $system = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
                if ([string]::IsNullOrEmpty($system)) { throw 'unknown-system-directory' }
                $attributes = [IO.File]::GetAttributes((Join-Path $system 'wsl.exe'))
                $wslExists = ($attributes -band [IO.FileAttributes]::Directory) -eq 0
            } catch [IO.FileNotFoundException] { $wslExists = $false }
              catch [IO.DirectoryNotFoundException] { $wslExists = $false }
              catch { $wslExists = $null }
        }
    }
    $report = New-InventoryReport $version $build $type $native $process $interactive $nonzero $wslExists
    $desktop = @{ accessible = $null; handleClosed = $null; ownership = 'unknown'; unlocked = 'unknown' }
    $ambient = @{ observed = $false; stateDirectoryOverride = $null; setupOverrides = $null; trayExecutableOverride = $null; gatewayVersionOverride = $null; gatewayPackageOverride = $null; protocolResultOverride = $null }
    try { $ambient = Get-AmbientPresence @(Get-ChildItem -LiteralPath Env: -Name) } catch { }
    if ($IsWindows -and $report.os.nativeProcessMatches) {
        try {
            Add-Type -Path (Join-Path $PSScriptRoot 'InventoryInputDesktop.cs') -ErrorAction Stop
            $observation = [InventoryInputDesktop]::Read()
            $desktop.accessible = $observation.Accessible
            $desktop.handleClosed = $observation.HandleClosed
        } catch { }
    }
    $versionQuery = New-SkippedQuery version; $statusQuery = New-SkippedQuery status; $listQuery = New-SkippedQuery list
    if ($report.os.observed -and $report.os.productType -eq 1 -and $report.os.nativeProcessMatches -and
        $wslExists -eq $true -and $desktop.accessible -eq $true -and $desktop.handleClosed -eq $true) {
        $wslPath = Join-Path $system 'wsl.exe'
        $versionQuery = Invoke-WslRead $wslPath version
        if ($versionQuery.settled -and $versionQuery.outcome -ceq 'completed') {
            $versionValue = Read-WslVersion $versionQuery.privateOut
            $versionDiagnostic = Get-WslDiagnosticKind ($versionQuery.privateOut + [Environment]::NewLine + $versionQuery.privateError)
            if ($null -ne $versionValue -and [version]$versionValue -ge [version]'2.4.4' -and
                [string]::IsNullOrWhiteSpace($versionQuery.privateError) -and $versionDiagnostic -ceq 'None') {
                $statusQuery = Invoke-WslRead $wslPath status
                if ($statusQuery.settled -and $statusQuery.outcome -ceq 'completed' -and
                    (Read-StatusReadiness $statusQuery.privateOut $statusQuery.privateError).kind -ceq 'Ready') {
                    $listQuery = Invoke-WslRead $wslPath list
                }
            }
        }
    }
    $readiness = Get-WslReadiness $versionQuery $statusQuery $listQuery
    $report['desktop'] = $desktop
    $report['ambient'] = $ambient
    $report['wsl'] = $readiness
    $report.wsl['systemExecutableExists'] = Convert-SafeBoolean $wslExists
    $report['queries'] = @((Get-PublicQuery $versionQuery), (Get-PublicQuery $statusQuery), (Get-PublicQuery $listQuery))
    $report['readOnlyChecksReady'] = Test-ReadOnlyQualification $report $desktop $ambient $readiness
    $report.outcome = if ($report.readOnlyChecksReady) { 'qualification-observed' } else { 'not-ready' }
    Write-Output (Get-SafeJson $report)
    if (-not $report.readOnlyChecksReady) { exit 1 }
} catch {
    Write-Output (Get-SafeJson @{ schema = 3; outcome = 'inventory-failed'; landingQualified = $false })
    exit 1
}
