#requires -Version 5.1
<#
.SYNOPSIS
    Fixture-only regressions for Enable-StoreMigrationTest.ps1.
.DESCRIPTION
    Executes the actual helper in a hostless runspace with in-memory registry and
    executable metadata adapters, blocked process commands, and path-bounded file
    cmdlets. All environment roots and real files belong to one unique repository
    fixture directory. Even a regressed reparse/lock guard cannot touch real product
    state or enumerate/stop processes. No certificate, installer or app is used.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$targetScript = Join-Path $PSScriptRoot 'Enable-StoreMigrationTest.ps1'
$root = Join-Path (Split-Path $PSScriptRoot -Parent) ('.store-migration-fixtures-' + [guid]::NewGuid().ToString('N'))
$passed = 0
$cases = 0
$fixtureNumber = 0
$backupVersion = 'OpenClawMigrationTestOriginalDisplayVersion'
$backupName = 'OpenClawMigrationTestOriginalDisplayName'
$links = [Collections.Generic.List[string]]::new()

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}

function Test-Case([string] $Name, [scriptblock] $Action) {
    & $Action
    $script:cases++
    Write-Host "PASS $Name"
}

function New-Fixture([string] $Version = '2026.9.5', [switch] $State) {
    $script:fixtureNumber++
    $fixtureRoot = Join-Path $root "case-$fixtureNumber"
    $roaming = Join-Path $fixtureRoot 'roaming'
    $local = Join-Path $fixtureRoot 'local'
    $install = Join-Path $local 'OpenClawTray'
    $gateways = Join-Path $roaming 'OpenClawTray\gateways'
    $records = Join-Path $roaming 'OpenClawTray\store-migration'
    $null = [IO.Directory]::CreateDirectory($install)
    $null = [IO.Directory]::CreateDirectory($gateways)
    foreach ($name in @('OpenClaw.Tray.WinUI.exe', 'unins000.exe', 'Test-InnoMigration.ps1',
        'MigrationRecordCodec.cs', 'Uninstall-LocalGateway.ps1')) {
        [IO.File]::WriteAllText((Join-Path $install $name), 'inert fixture, never execute')
    }
    [IO.File]::WriteAllText((Join-Path $install 'app-identity.txt'), 'release')
    if ($State) {
        $null = [IO.Directory]::CreateDirectory($records)
        [IO.File]::WriteAllText((Join-Path $records 'prepare.lock'), 'persistent lock sentinel')
    }
    return @{
        Root = $fixtureRoot; Roaming = $roaming; Local = $local; Install = $install
        Gateways = $gateways; Records = $records; Exists = $true
        Key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{M0LTB0T-TRAY-4PP1-D3N7}_is1'
        Registration = @{
            InstallLocation = $install; DisplayVersion = $Version
            DisplayName = "OpenClaw Companion version $Version"; Publisher = 'OpenClaw Foundation'
            UninstallString = '"' + (Join-Path $install 'unins000.exe') + '"'
        }
        Binary = @{ FileVersion = '2026.9.5.0'; ProductVersion = '2026.9.5' }
        Writes = 0; Reads = 0; ProcessCalls = 0; FailWrite = ''; DriftOnRead = $false
        ScanOwnership = [Collections.Generic.List[bool]]::new()
        MutationOwnership = [Collections.Generic.List[bool]]::new()
    }
}

function Add-Records($Fixture) {
    $null = [IO.Directory]::CreateDirectory($Fixture.Records)
    foreach ($name in @('prepare.lock', 'consent.lock', 'intent.dpapi', 'consent.dpapi',
        'completed.dpapi', '.0123456789abcdef0123456789abcdef.tmp', 'unknown.keep')) {
        [IO.File]::WriteAllText((Join-Path $Fixture.Records $name), "fixture bytes: $name")
    }
}

function Get-Snapshot($Fixture) {
    $files = @(Get-ChildItem -LiteralPath $Fixture.Root -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Fixture.Root.Length)
        if ($_.PSIsContainer) { "directory:$relative" }
        else { "file:$relative" + ':' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName)) }
    })
    $labels = @($Fixture.Registration.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$($_.Value)" })
    return ($files + $labels) -join "`n"
}

# Adapters replace only external I/O, not version, inventory, lock, reset or
# relabel decisions. Registry adapters never fall back to a real provider.
$safeInvocation = {
    param($ScriptPath, $Fixture, $Arguments)
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    function Assert-FixturePath([string] $Path) {
        $full = [IO.Path]::GetFullPath($Path)
        if ($full -ine $Fixture.Root -and
            -not $full.StartsWith($Fixture.Root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "FIXTURE SAFETY: path outside owned fixture: $Path"
        }
    }
    function Test-LockOwned {
        $handle = $null
        try {
            $handle = [IO.FileStream]::new((Join-Path $Fixture.Records 'prepare.lock'),
                [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            return $false
        }
        catch [IO.IOException] { return ($_.Exception.GetBaseException().HResult -band 0xffff) -in @(32, 33) }
        finally { if ($null -ne $handle) { $handle.Dispose() } }
    }
    function Assert-FixtureKey([string] $Path) {
        if ($Path -cne $Fixture.Key) { throw "FIXTURE SAFETY: unexpected registry adapter key: $Path" }
    }
    function Get-Process { $Fixture.ProcessCalls++; throw 'FIXTURE SAFETY: process enumeration forbidden' }
    function Stop-Process { $Fixture.ProcessCalls++; throw 'FIXTURE SAFETY: process termination forbidden' }
    function Start-Process { $Fixture.ProcessCalls++; throw 'FIXTURE SAFETY: process launch forbidden' }
    function Test-Path {
        [CmdletBinding()]
        param([string]$LiteralPath, [string]$Path, [string]$PathType = 'Any')
        $p = if ($LiteralPath) { $LiteralPath } else { $Path }
        if ($p -match '^HK(CU|LM):') { return $Fixture.Exists -and $p -ceq $Fixture.Key }
        Assert-FixturePath $p
        Microsoft.PowerShell.Management\Test-Path -LiteralPath $p -PathType $PathType
    }
    function Get-ItemProperty {
        [CmdletBinding()]
        param([string]$LiteralPath, [string]$Path)
        Assert-FixtureKey $(if ($LiteralPath) { $LiteralPath } else { $Path })
        $Fixture.Reads++
        if ($Fixture.DriftOnRead -and $Fixture.Reads -eq 2) {
            $Fixture.Registration.DisplayVersion = '2026.9.6'
        }
        [pscustomobject]$Fixture.Registration.Clone()
    }
    function New-ItemProperty {
        [CmdletBinding(SupportsShouldProcess)]
        param([string]$LiteralPath, [string]$Path, [string]$Name, $Value, [string]$PropertyType, [switch]$Force)
        Assert-FixtureKey $(if ($LiteralPath) { $LiteralPath } else { $Path })
        $Fixture.MutationOwnership.Add((Test-LockOwned))
        if ($Fixture.Registration.ContainsKey($Name) -and -not $Force) { throw 'Fixture registry value already exists' }
        $Fixture.Registration[$Name] = $Value
        $Fixture.Writes++
    }
    function Set-ItemProperty {
        [CmdletBinding(SupportsShouldProcess)]
        param([string]$LiteralPath, [string]$Path, [string]$Name, $Value, [string]$Type)
        Assert-FixtureKey $(if ($LiteralPath) { $LiteralPath } else { $Path })
        if ($Fixture.FailWrite -ceq $Name) { throw "Injected registry write failure: $Name" }
        $Fixture.MutationOwnership.Add((Test-LockOwned))
        $Fixture.Registration[$Name] = $Value
        $Fixture.Writes++
    }
    function Remove-ItemProperty {
        [CmdletBinding(SupportsShouldProcess)]
        param([string]$LiteralPath, [string]$Path, [string]$Name)
        Assert-FixtureKey $(if ($LiteralPath) { $LiteralPath } else { $Path })
        $Fixture.MutationOwnership.Add((Test-LockOwned))
        $Fixture.Registration.Remove($Name)
        $Fixture.Writes++
    }
    function Get-Item {
        [CmdletBinding()]
        param([string]$LiteralPath, [switch]$Force)
        Assert-FixturePath $LiteralPath
        $item = Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath -Force:$Force
        if ($LiteralPath -ieq (Join-Path $Fixture.Install 'OpenClaw.Tray.WinUI.exe')) {
            return [pscustomobject]@{
                FullName = $item.FullName; Name = $item.Name; Attributes = $item.Attributes
                PSIsContainer = $item.PSIsContainer; VersionInfo = [pscustomobject]$Fixture.Binary
            }
        }
        $item
    }
    function Get-ChildItem {
        [CmdletBinding()]
        param([string]$LiteralPath, [switch]$Force, [switch]$File)
        Assert-FixturePath $LiteralPath
        if ($LiteralPath -ieq $Fixture.Records) { $Fixture.ScanOwnership.Add((Test-LockOwned)) }
        Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $LiteralPath -Force:$Force -File:$File
    }
    function Get-Content {
        [CmdletBinding()]
        param([string]$LiteralPath, [switch]$Raw)
        Assert-FixturePath $LiteralPath
        Microsoft.PowerShell.Management\Get-Content -LiteralPath $LiteralPath -Raw:$Raw
    }
    function New-Item {
        [CmdletBinding(SupportsShouldProcess)]
        param([string]$Path, [string]$ItemType, [switch]$Force)
        Assert-FixturePath $Path
        Microsoft.PowerShell.Management\New-Item -Path $Path -ItemType $ItemType -Force:$Force -Confirm:$false
    }
    function Remove-Item {
        [CmdletBinding(SupportsShouldProcess)]
        param([string]$LiteralPath, [switch]$Force)
        Assert-FixturePath $LiteralPath
        $Fixture.MutationOwnership.Add((Test-LockOwned))
        Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Force:$Force -Confirm:$false
    }
    function Move-Item {
        [CmdletBinding(SupportsShouldProcess)]
        param([string]$LiteralPath, [string]$Destination)
        Assert-FixturePath $LiteralPath
        Assert-FixturePath $Destination
        $Fixture.MutationOwnership.Add((Test-LockOwned))
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -Confirm:$false
    }
    $environment = @{
        APPDATA = $Fixture.Roaming; LOCALAPPDATA = $Fixture.Local
        OPENCLAW_TRAY_DATA_DIR = (Join-Path $Fixture.Roaming 'OpenClawTray')
        OPENCLAW_TRAY_APPDATA_DIR = $Fixture.Roaming; OPENCLAW_TRAY_LOCALAPPDATA_DIR = $Fixture.Local
        OPENCLAW_TRAY_LOCAL_DATA_DIR = (Join-Path $Fixture.Local 'OpenClawTray')
        OPENCLAW_STATE_DIR = (Join-Path $Fixture.Root 'state'); OPENCLAW_CONFIG_PATH = (Join-Path $Fixture.Root 'config.json')
    }
    $old = @{}
    $message = $null
    $output = [Collections.Generic.List[string]]::new()
    try {
        # Defense in depth: an accidental registry cmdlet cannot resolve HKCU/HKLM.
        Remove-PSDrive HKCU, HKLM -ErrorAction SilentlyContinue
        foreach ($name in $environment.Keys) {
            $old[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, $environment[$name])
        }
        & $ScriptPath @Arguments 6>&1 3>&1 | ForEach-Object { $output.Add("$_") }
    }
    catch { $message = $_.Exception.Message }
    finally {
        foreach ($name in $old.Keys) { [Environment]::SetEnvironmentVariable($name, $old[$name]) }
    }
    [pscustomobject]@{ Message = $message; Output = $output -join "`n" }
}

function Invoke-Helper($Fixture, [hashtable] $Arguments = @{}) {
    $pipeline = [PowerShell]::Create()
    try {
        $null = $pipeline.AddScript($safeInvocation.ToString()).AddArgument($targetScript).AddArgument($Fixture).AddArgument($Arguments)
        $result = @($pipeline.Invoke())
        if ($pipeline.Streams.Error.Count -gt 0) { throw ($pipeline.Streams.Error | Out-String) }
        if ($result.Count -ne 1) { throw "Unexpected fixture result count: $($result.Count)" }
        Assert-True ($Fixture.ProcessCalls -eq 0) 'Helper attempted to enumerate, launch or stop a process.'
        return $result[0]
    }
    finally { $pipeline.Dispose() }
}

function Assert-Success($Result) {
    Assert-True ([string]::IsNullOrEmpty($Result.Message)) "Unexpected helper failure: $($Result.Message)"
}
function Assert-Failure($Result, [string] $Text) {
    Assert-True ($null -ne $Result.Message -and $Result.Message.Contains($Text)) "Expected '$Text', received '$($Result.Message)'."
}
function New-FixtureJunction([string] $Link, [string] $Target) {
    $null = New-Item -ItemType Junction -Path $Link -Target $Target
    $script:links.Add($Link)
}

try {
    $null = [IO.Directory]::CreateDirectory($root)
    # Expected values are independently specified, not generated by the policy.
    $versions = @(
        @{ Label = '2026.9.5'; File = '2026.9.5.0'; Product = '2026.9.5'; Valid = $true },
        @{ Label = '2026.9.5.0'; File = '2026.9.5'; Product = '2026.9.5+commit.abc-1'; Valid = $true },
        @{ Label = '2026.9.5.7'; File = '2026.9.5.7'; Product = '2026.9.5.7+build.42'; Valid = $true },
        @{ Label = '2026.9.5'; File = '2026.9.5.0'; Product = ''; Valid = $true },
        @{ Label = '2026.9.5-2'; File = '2026.9.5.0'; Product = '2026.9.5-2+commit.abc'; Valid = $true },
        @{ Label = '2026.9.5-2'; File = '2026.9.5'; Product = '2026.9.5-2'; Valid = $true },
        @{ Label = '2026.9.5'; File = '2026.9.5.0'; Product = '2026.9.5-alpha.64'; Valid = $false },
        @{ Label = '2026.9.5-alpha.64'; File = '2026.9.5.0'; Product = '2026.9.5-alpha.64'; Valid = $false },
        @{ Label = '2026.9.5-2'; File = '2026.9.5.2'; Product = '2026.9.5-2'; Valid = $false },
        @{ Label = '2026.9.5-2'; File = '2026.9.5.0'; Product = '2026.9.5-3'; Valid = $false },
        @{ Label = '2026.9.5'; File = '2026.9.5.0'; Product = '2026.9.5-2'; Valid = $false },
        @{ Label = '2026.9.5-2'; File = '2026.9.5.0'; Product = ''; Valid = $false },
        @{ Label = '2026.9.5'; File = '2026.9.5.0'; Product = '2026.9.5+'; Valid = $false },
        @{ Label = '2026.9.5'; File = '2026.9.5.0'; Product = '2026.9.5+x..y'; Valid = $false },
        @{ Label = '2026.9.5'; File = '2026.9.5.0'; Product = '2026.9.5+x+y'; Valid = $false },
        @{ Label = '2026.9.5'; File = '2026.9.6.0'; Product = '2026.9.5'; Valid = $false },
        @{ Label = '2026.9'; File = '2026.9.5.0'; Product = '2026.9.5'; Valid = $false },
        @{ Label = 'v2026.9.5'; File = '2026.9.5.0'; Product = '2026.9.5'; Valid = $false },
        @{ Label = '2026.9.5.0.0'; File = '2026.9.5.0'; Product = '2026.9.5'; Valid = $false },
        @{ Label = '2026.9.5-0'; File = '2026.9.5.0'; Product = '2026.9.5-0'; Valid = $false },
        @{ Label = '2026.9.5-02'; File = '2026.9.5.0'; Product = '2026.9.5-02'; Valid = $false },
        @{ Label = '2026.9.5-2147483648'; File = '2026.9.5.0'; Product = '2026.9.5-2147483648'; Valid = $false },
        @{ Label = '2026.9.5.0-2'; File = '2026.9.5.0'; Product = '2026.9.5.0-2'; Valid = $false },
        @{ Label = '2026.9.5'; File = '2026.9.5.0 '; Product = '2026.9.5'; Valid = $false }
    )
    foreach ($row in $versions) {
        Test-Case "metadata $($row.Label) / $($row.File) / $($row.Product)" {
            $f = New-Fixture -Version $row.Label
            $f.Binary.FileVersion = $row.File; $f.Binary.ProductVersion = $row.Product
            $before = Get-Snapshot $f
            $result = Invoke-Helper $f
            if ($row.Valid) {
                Assert-Success $result
                Assert-True ($result.Output.Contains("No relabel necessary for '$($row.Label)'")) 'Default label changed or preflight was bypassed.'
                Assert-True ($f.Writes -eq 0) 'Already-eligible source was relabelled.'
                Assert-Success (Invoke-Helper $f @{ TargetVersion = $row.Label })
                Assert-True ($f.Writes -eq 0) 'Explicit compatible target unnecessarily changed labels.'
            }
            else {
                Assert-Failure $result 'incompatible with FileVersion'
                Assert-True ((Get-Snapshot $f) -ceq $before) 'Invalid metadata changed files or registry.'
                Assert-True (-not [IO.Directory]::Exists($f.Records)) 'Invalid metadata created a lock directory.'
            }
        }
    }

    Test-Case 'alpha rejection precedes quarantine and any mutation' {
        $f = New-Fixture
        $f.Binary.ProductVersion = '2026.9.5-alpha.64+commit.abc'
        [IO.File]::WriteAllText((Join-Path $f.Gateways 'unknown.txt'), 'keep')
        $before = Get-Snapshot $f
        $result = Invoke-Helper $f @{ TargetVersion = '2026.9.5.0'; MoveBlockingFiles = $true }
        Assert-Failure $result 'Current policy rejects alpha ProductVersion'
        Assert-True ((Get-Snapshot $f) -ceq $before) 'Alpha preflight had side effects.'
    }
    Test-Case 'explicit incompatible target cannot create backup or lock' {
        $f = New-Fixture
        $before = Get-Snapshot $f
        Assert-Failure (Invoke-Helper $f @{ TargetVersion = '2026.9.6' }) "Label '2026.9.6' is incompatible"
        Assert-True ((Get-Snapshot $f) -ceq $before) 'Bad target mutated source.'
    }
    foreach ($pair in @(@('2026.9.5', '2026.9.5.0'), @('2026.9.5.0', '2026.9.5'))) {
        Test-Case "equivalent explicit label $($pair[0]) -> $($pair[1]) and revert" {
            $f = New-Fixture -Version $pair[0] -State
            Assert-Success (Invoke-Helper $f @{ TargetVersion = $pair[1] })
            Assert-True ($f.Registration.DisplayVersion -ceq $pair[1]) 'Requested label was not applied.'
            Assert-True ($f.Registration[$backupVersion] -ceq $pair[0]) 'Original version backup missing.'
            Assert-True ($f.Registration[$backupName] -ceq "OpenClaw Companion version $($pair[0])") 'Original name backup missing.'
            Assert-Success (Invoke-Helper $f @{ Revert = $true })
            Assert-True ($f.Registration.DisplayVersion -ceq $pair[0] -and -not $f.Registration.ContainsKey($backupVersion)) 'Revert did not restore and clear backup.'
            Assert-True ($f.MutationOwnership.Count -eq 8 -and -not $f.MutationOwnership.Contains($false)) 'Registry mutation lacked exclusive ownership.'
        }
    }
    foreach ($defect in @('payload', 'identity', 'name', 'publisher', 'uninstaller')) {
        Test-Case "already-eligible label still checks $defect" {
            $f = New-Fixture
            $expected = switch ($defect) {
                'payload' { [IO.File]::Delete((Join-Path $f.Install 'MigrationRecordCodec.cs')); 'Required migration payload' }
                'identity' { [IO.File]::WriteAllText((Join-Path $f.Install 'app-identity.txt'), 'dev'); 'identity is not release' }
                'name' { $f.Registration.DisplayName = 'Unexpected'; 'publisher or DisplayName' }
                'publisher' { $f.Registration.Publisher = 'Unexpected'; 'publisher or DisplayName' }
                'uninstaller' { $f.Registration.UninstallString += ' /silent'; 'uninstall command' }
            }
            $before = Get-Snapshot $f
            Assert-Failure (Invoke-Helper $f) $expected
            Assert-True ((Get-Snapshot $f) -ceq $before) 'Failed preflight mutated state.'
        }
    }
    Test-Case 'native draft and exact atomic siblings survive; unknown files require opt-in' {
        $f = New-Fixture -State
        $allowed = @('native-setup-draft.json', '.native-setup-draft.json.0123456789abcdef0123456789abcdef.tmp')
        $unknown = @('unknown.txt', '.native-setup-draft.json.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.tmp',
            '.native-setup-draft.json.bad.tmp', '.native-setup-draft.json.0123456789abcdef0123456789abcdef.tmp.extra')
        foreach ($name in $allowed + $unknown) { [IO.File]::WriteAllText((Join-Path $f.Gateways $name), "bytes:$name") }
        $before = Get-Snapshot $f
        Assert-Failure (Invoke-Helper $f) 'Unknown gateway-root files'
        Assert-True ((Get-Snapshot $f) -ceq $before) 'Unknown files changed without opt-in.'
        Assert-Success (Invoke-Helper $f @{ MoveBlockingFiles = $true })
        foreach ($name in $allowed) {
            Assert-True ([IO.File]::ReadAllText((Join-Path $f.Gateways $name)) -ceq "bytes:$name") "Allowed native state moved: $name"
        }
        $quarantine = @(Get-ChildItem -LiteralPath (Split-Path $f.Gateways -Parent) -Directory -Filter 'gateways-blocking-*')
        Assert-True ($quarantine.Count -eq 1) 'Expected one unique quarantine.'
        foreach ($name in $unknown) {
            Assert-True (-not [IO.File]::Exists((Join-Path $f.Gateways $name)) -and
                [IO.File]::ReadAllText((Join-Path $quarantine[0].FullName $name)) -ceq "bytes:$name") "Unknown file lost: $name"
        }
        Assert-True (-not $f.MutationOwnership.Contains($false)) 'Quarantine lacked exclusive ownership.'
    }
    Test-Case 'default reset preserves receipt and operation lock while deleting all recognized records' {
        $f = New-Fixture -State; Add-Records $f
        $result = Invoke-Helper $f @{ ResetMigrationState = $true }
        Assert-Success $result
        $remaining = @(Get-ChildItem -LiteralPath $f.Records | Sort-Object Name | ForEach-Object Name) -join ','
        Assert-True ($remaining -ceq 'completed.dpapi,prepare.lock,unknown.keep') "Unexpected records remain: $remaining"
        foreach ($name in @('completed.dpapi', 'prepare.lock', 'unknown.keep')) {
            Assert-True ([IO.File]::ReadAllText((Join-Path $f.Records $name)) -ceq "fixture bytes: $name") "Preserved file changed: $name"
        }
        Assert-True ($f.ScanOwnership.Count -eq 3 -and $f.ScanOwnership[1] -and $f.ScanOwnership[2]) 'Authoritative scan did not retain exclusive lock.'
        Assert-True ($f.MutationOwnership.Count -eq 4 -and -not $f.MutationOwnership.Contains($false)) 'Reset did not own every deletion.'
    }
    Test-Case 'explicit receipt reset also preserves lock and unknown data' {
        $f = New-Fixture -State; Add-Records $f
        Assert-Success (Invoke-Helper $f @{ ResetMigrationState = $true; IncludeCompletionReceipt = $true })
        Assert-True (-not [IO.File]::Exists((Join-Path $f.Records 'completed.dpapi'))) 'Explicit receipt deletion did not occur.'
        Assert-True ([IO.File]::ReadAllText((Join-Path $f.Records 'prepare.lock')) -ceq 'fixture bytes: prepare.lock') 'Operation lock was replaced/deleted.'
        Assert-True ([IO.File]::Exists((Join-Path $f.Records 'unknown.keep'))) 'Unknown record was removed.'
    }
    foreach ($mode in @('reset', 'relabel', 'quarantine', 'revert')) {
        Test-Case "non-tray shared reader blocks $mode without changes" {
            $f = New-Fixture -State; Add-Records $f
            $arguments = switch ($mode) {
                'reset' { @{ ResetMigrationState = $true; IncludeCompletionReceipt = $true } }
                'relabel' { @{ TargetVersion = '2026.9.5.0' } }
                'quarantine' { [IO.File]::WriteAllText((Join-Path $f.Gateways 'unknown.txt'), 'keep'); @{ MoveBlockingFiles = $true } }
                'revert' { $f.Registration[$backupVersion] = '2026.9.5.0'; $f.Registration[$backupName] = 'OpenClaw Companion version 2026.9.5.0'; @{ Revert = $true } }
            }
            $before = Get-Snapshot $f
            $reader = [IO.FileStream]::new((Join-Path $f.Records 'prepare.lock'),
                [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try { $result = Invoke-Helper $f $arguments }
            finally { $reader.Dispose() }
            Assert-Failure $result 'Cannot acquire exclusive migration lock'
            Assert-True ((Get-Snapshot $f) -ceq $before) 'Busy operation changed records or labels.'
            Assert-True ($f.Writes -eq 0 -and $f.MutationOwnership.Count -eq 0) 'Busy operation attempted mutation.'
        }
    }
    foreach ($receiptOnly in @($false, $true)) {
        Test-Case "no-op reset without source (receipt only: $receiptOnly)" {
            $f = New-Fixture; $f.Exists = $false
            if ($receiptOnly) {
                $null = [IO.Directory]::CreateDirectory($f.Records)
                [IO.File]::WriteAllText((Join-Path $f.Records 'completed.dpapi'), 'keep')
            }
            $before = Get-Snapshot $f
            Assert-Success (Invoke-Helper $f @{ ResetMigrationState = $true })
            Assert-True ((Get-Snapshot $f) -ceq $before) 'No-op reset mutated state or created a lock.'
            Assert-True ($f.Reads -eq 0) 'Standalone reset read registration.'
        }
    }
    Test-Case 'receipt opt-in without reset rejects before any I/O' {
        $f = New-Fixture
        $before = Get-Snapshot $f
        Assert-Failure (Invoke-Helper $f @{ IncludeCompletionReceipt = $true }) 'only applies to -ResetMigrationState'
        Assert-True ($f.Reads -eq 0 -and (Get-Snapshot $f) -ceq $before) 'Invalid opt-in performed work.'
    }
    foreach ($linkAt in @('store-migration', 'OpenClawTray', 'prepare.lock', 'consent.dpapi')) {
        Test-Case "path-specific reset refusal at $linkAt" {
            $f = New-Fixture
            $outside = Join-Path $f.Root 'redirected'
            $recordHome = if ($linkAt -eq 'OpenClawTray') { Join-Path $outside 'store-migration' } else { $outside }
            $null = [IO.Directory]::CreateDirectory($recordHome)
            foreach ($name in @('completed.dpapi', 'consent.dpapi')) { [IO.File]::WriteAllText((Join-Path $recordHome $name), "keep:$name") }
            if ($linkAt -eq 'OpenClawTray') {
                [IO.Directory]::Delete((Split-Path $f.Gateways -Parent), $true)
                $link = Split-Path $f.Records -Parent
            }
            elseif ($linkAt -eq 'store-migration') { $link = $f.Records }
            else {
                $null = [IO.Directory]::CreateDirectory($f.Records)
                $link = Join-Path $f.Records $linkAt
                [IO.File]::WriteAllText((Join-Path $f.Records 'intent.dpapi'), 'keep')
            }
            New-FixtureJunction $link $outside
            $result = Invoke-Helper $f @{ ResetMigrationState = $true; IncludeCompletionReceipt = $true }
            Assert-Failure $result "'$link' is a reparse point"
            foreach ($name in @('completed.dpapi', 'consent.dpapi')) {
                Assert-True ([IO.File]::ReadAllText((Join-Path $recordHome $name)) -ceq "keep:$name") "Redirected $name changed."
            }
        }
    }
    Test-Case 'native draft reparse point is refused, never quarantined' {
        $f = New-Fixture -State
        $outside = Join-Path $f.Root 'draft-target'; $null = [IO.Directory]::CreateDirectory($outside)
        $link = Join-Path $f.Gateways 'native-setup-draft.json'
        New-FixtureJunction $link $outside
        Assert-Failure (Invoke-Helper $f @{ MoveBlockingFiles = $true }) "'$link' is a reparse point"
        Assert-True (@(Get-ChildItem -LiteralPath (Split-Path $f.Gateways -Parent) -Filter 'gateways-blocking-*').Count -eq 0) 'Reparse entry was quarantined.'
    }
    Test-Case 'allowed draft name must be a file, not a directory' {
        $f = New-Fixture -State
        $path = Join-Path $f.Gateways 'native-setup-draft.json'
        $null = [IO.Directory]::CreateDirectory($path)
        Assert-Failure (Invoke-Helper $f @{ MoveBlockingFiles = $true }) "'$path' is unexpectedly a directory"
        Assert-True ([IO.Directory]::Exists($path) -and $f.Writes -eq 0) 'Unexpected draft directory was moved or registration changed.'
    }
    Test-Case 'legacy alpha backup can be restored without authorizing migration' {
        $f = New-Fixture -State
        $f.Binary.ProductVersion = '2026.9.5-alpha.64+commit.abc'
        $f.Registration[$backupVersion] = '2026.9.5-alpha.64'
        $f.Registration[$backupName] = 'OpenClaw Companion version 2026.9.5-alpha.64'
        Assert-Success (Invoke-Helper $f @{ Revert = $true })
        Assert-True ($f.Registration.DisplayVersion -ceq '2026.9.5-alpha.64') 'Legacy label was not restored.'
        Assert-Failure (Invoke-Helper $f) 'Current policy rejects alpha ProductVersion'
    }
    foreach ($drift in @('receipt', 'version', 'name', 'saved', 'missing', 'binary')) {
        Test-Case "revert refuses $drift drift" {
            $f = New-Fixture -State
            $f.Registration[$backupVersion] = '2026.9.5.0'
            $f.Registration[$backupName] = 'OpenClaw Companion version 2026.9.5.0'
            $expected = switch ($drift) {
                'receipt' { [IO.File]::WriteAllText((Join-Path $f.Records 'completed.dpapi'), 'keep'); 'Completion receipt' }
                'version' { $f.Registration.DisplayVersion = '2026.9.6'; $f.Registration.DisplayName = 'OpenClaw Companion version 2026.9.6'; 'drifted' }
                'name' { $f.Registration.DisplayName = 'Unexpected'; 'publisher or DisplayName' }
                'saved' { $f.Registration[$backupName] = 'Unexpected'; 'coherent saved original label' }
                'missing' { $f.Registration.Remove($backupName); 'coherent saved original label' }
                'binary' { $f.Binary.ProductVersion = '2026.9.6'; 'drifted' }
            }
            $before = Get-Snapshot $f
            Assert-Failure (Invoke-Helper $f @{ Revert = $true }) $expected
            Assert-True ((Get-Snapshot $f) -ceq $before) 'Revert overwrote drifted state.'
        }
    }
    Test-Case 'registration drift between preflight and lock is refused' {
        $f = New-Fixture -State; $f.DriftOnRead = $true
        Assert-Failure (Invoke-Helper $f @{ TargetVersion = '2026.9.5.0' }) "Registration 'DisplayVersion' changed"
        Assert-True ($f.Writes -eq 0) 'Helper overwrote concurrent registry change.'
    }
    foreach ($mode in @('reset', 'relabel', 'quarantine', 'revert', 'missing-lock')) {
        Test-Case "WhatIf preserves all state for $mode" {
            $f = New-Fixture -State
            $arguments = switch ($mode) {
                'reset' { Add-Records $f; @{ ResetMigrationState = $true; IncludeCompletionReceipt = $true } }
                'relabel' { @{ TargetVersion = '2026.9.5.0' } }
                'quarantine' { [IO.File]::WriteAllText((Join-Path $f.Gateways 'unknown.txt'), 'keep'); @{ MoveBlockingFiles = $true } }
                'revert' { $f.Registration[$backupVersion] = '2026.9.5.0'; $f.Registration[$backupName] = 'OpenClaw Companion version 2026.9.5.0'; @{ Revert = $true } }
                'missing-lock' { [IO.Directory]::Delete($f.Records, $true); @{} }
            }
            $arguments.WhatIf = $true
            $before = Get-Snapshot $f
            Assert-Success (Invoke-Helper $f $arguments)
            Assert-True ((Get-Snapshot $f) -ceq $before) 'WhatIf mutated fixture.'
        }
    }
    Test-Case 'unavailable confirmation fails without mutation' {
        $f = New-Fixture -State; Add-Records $f
        $before = Get-Snapshot $f
        $result = Invoke-Helper $f @{ ResetMigrationState = $true; Confirm = $true }
        Assert-Failure $result 'ShouldProcess'
        Assert-True ((Get-Snapshot $f) -ceq $before) 'Failed confirmation deleted state.'
    }
    Test-Case 'registry write failure keeps backups and does not report success' {
        $f = New-Fixture -State; $f.FailWrite = 'DisplayVersion'
        $result = Invoke-Helper $f @{ TargetVersion = '2026.9.5.0' }
        Assert-Failure $result 'Injected registry write failure'
        Assert-True ($f.Registration.DisplayVersion -ceq '2026.9.5' -and $f.Registration[$backupVersion] -ceq '2026.9.5') 'Failed write lost original labels.'
        Assert-True (-not $result.Output.Contains('Relabelled')) 'Failed write reported success.'
        $f.FailWrite = ''
        Assert-Success (Invoke-Helper $f @{ Revert = $true })
    }
    Test-Case 'partial label write failure retains backups and refuses guessed revert' {
        $f = New-Fixture -State; $f.FailWrite = 'DisplayName'
        $result = Invoke-Helper $f @{ TargetVersion = '2026.9.5.0' }
        Assert-Failure $result 'Injected registry write failure'
        Assert-True ($f.Registration.DisplayVersion -ceq '2026.9.5.0' -and
            $f.Registration.DisplayName -ceq 'OpenClaw Companion version 2026.9.5') 'Fixture did not reach partial-label failure.'
        Assert-True ($f.Registration[$backupVersion] -ceq '2026.9.5' -and
            $f.Registration[$backupName] -ceq 'OpenClaw Companion version 2026.9.5') 'Partial write lost backup values.'
        Assert-True (-not $result.Output.Contains('Relabelled')) 'Partial write reported success.'
        $f.FailWrite = ''
        $before = Get-Snapshot $f
        Assert-Failure (Invoke-Helper $f @{ Revert = $true }) 'publisher or DisplayName'
        Assert-True ((Get-Snapshot $f) -ceq $before) 'Revert overwrote a partial/unknown label pair.'
    }
    Test-Case 'record deletion failure retains operation lock and reports failure' {
        $f = New-Fixture -State
        $record = Join-Path $f.Records 'consent.dpapi'
        [IO.File]::WriteAllText($record, 'keep')
        $reader = [IO.FileStream]::new($record, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { $result = Invoke-Helper $f @{ ResetMigrationState = $true } }
        finally { $reader.Dispose() }
        Assert-True (-not [string]::IsNullOrEmpty($result.Message)) 'Locked record deletion unexpectedly succeeded.'
        Assert-True ([IO.File]::ReadAllText($record) -ceq 'keep') 'Failed deletion changed record.'
        Assert-True ([IO.File]::ReadAllText((Join-Path $f.Records 'prepare.lock')) -ceq 'persistent lock sentinel') 'Failed operation deleted/replaced lock.'
        Assert-Success (Invoke-Helper $f @{ ResetMigrationState = $true })
    }
    Test-Case 'CI runs helper fixtures beside migration package tests on Windows PowerShell 7' {
        $workflow = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) '.github\workflows\ci.yml') -Raw
        $fastValidation = [regex]::Match($workflow, '(?ms)^  fast-validation:\r?\n(?<job>.*?)(?=^  [a-z0-9-]+:|\z)').Groups['job'].Value
        Assert-True ($fastValidation -match 'runs-on: windows-latest') 'Helper CI job lost Windows runner.'
        Assert-True ($fastValidation -match '(?s)name: Validate developer migration test package\s+shell: pwsh\s+run: \./scripts/test-migration-test-msix\.ps1\s+- name: Validate Store migration helper fixtures\s+shell: pwsh\s+run: \.\\scripts\\test-store-migration-test-script\.ps1') 'Migration fixture CI step is missing, moved or using the wrong shell.'
    }

    Write-Host "Store migration helper fixtures passed: $cases cases, $passed assertions."
}
finally {
    foreach ($link in $links) {
        if ([IO.Directory]::Exists($link)) { [IO.Directory]::Delete($link) }
    }
    if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $true) }
}
$global:LASTEXITCODE = 0
