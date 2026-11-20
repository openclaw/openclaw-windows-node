param(
    [Parameter(Mandatory)][string]$Family,
    [string]$CleanupHostPath,
    [string]$PhysicalProfile,
    [switch]$Register,
    [switch]$Preserve,
    [switch]$CreatedDistro,
    [string]$DistroName,
    [string]$InstallPath,
    [switch]$OwnNativePackage,
    [string]$OwnedSinceUtc
)
$ErrorActionPreference = 'Stop'
function Normalize-Path([string]$path) {
    if ($path.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) { $path = '\\' + $path.Substring(8) }
    elseif ($path.StartsWith('\\?\')) { $path = $path.Substring(4) }
    [IO.Path]::GetFullPath($path).TrimEnd('\')
}
function Assert-ManagedPath([string]$path) {
    for ($item = (Normalize-Path $path); $item; $item = [IO.Path]::GetDirectoryName($item)) {
        if ((Test-Path -LiteralPath $item) -and
            (Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Managed WSL install path contains a reparse point. Cleanup remains pending.'
        }
    }
}
if ($Family -notmatch '^[A-Za-z0-9._-]+$' -or ($DistroName -and $DistroName -notmatch '^[A-Za-z0-9._-]+$')) {
    throw 'Invalid managed Gateway identity.'
}
$root = Join-Path $env:USERPROFILE ".openclaw-companion\cleanup\$Family"
$recordPath = Join-Path $root 'owned.json'
$taskName = "OpenClaw Gateway cleanup - $([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) - $Family"
$sentinel = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Packages\$Family\LocalState\gateway-cleanup-owner.txt"
if (-not $Register -and -not $Preserve -and -not (Test-Path $recordPath)) { exit 0 }
function Read-WslRegistrations {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
    if (Test-Path -LiteralPath $key -ErrorAction Stop) {
        Get-ChildItem -LiteralPath $key -ErrorAction Stop | Get-ItemProperty -ErrorAction Stop
    }
}
function Assert-ValidRemovalReceipt {
    $resultPath = Join-Path $root 'cleanup-result.json'
    if (-not (Test-Path -LiteralPath $resultPath)) { return }
    $json = Get-Content -LiteralPath $resultPath -Raw
    if ([string]::IsNullOrWhiteSpace($json) -or -not $json.TrimStart().StartsWith('[')) {
        throw 'OPENCLAW_CLEANUP_RECEIPT_INVALID: The cleanup receipt must contain an array.'
    }
    try { $results = ConvertFrom-Json -InputObject $json -ErrorAction Stop }
    catch [System.ArgumentException] { throw 'OPENCLAW_CLEANUP_RECEIPT_INVALID: Restore a valid cleanup-result.json before retrying.' }
    foreach ($result in $results) {
        if ($null -eq $result -or $result -isnot [pscustomobject]) {
            throw 'OPENCLAW_CLEANUP_RECEIPT_INVALID: The cleanup receipt contains an invalid removal.'
        }
        foreach ($name in @('DistroName', 'InstallPath', 'NativeFamily')) {
            if ($null -ne $result.$name -and $result.$name -isnot [string]) {
                throw 'OPENCLAW_CLEANUP_RECEIPT_INVALID: A removal identity is not a string.'
            }
        }
    }
}
function Record-Removal([string]$distro, [string]$path, [string]$native) {
    $resultPath = Join-Path $root 'cleanup-result.json'
    $results = if (Test-Path $resultPath) { @(Get-Content $resultPath -Raw | ConvertFrom-Json) } else { @() }
    $results += [pscustomobject]@{ DistroName = $distro; InstallPath = $path; NativeFamily = $native }
    ConvertTo-Json -InputObject @($results) | Set-Content "$resultPath.tmp" -Encoding UTF8
    Move-Item -LiteralPath "$resultPath.tmp" -Destination $resultPath -Force
}
function Test-PackageRemovedSince([string]$since) {
    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-AppXDeploymentServer/Operational'; Id = 400;
        StartTime = [DateTimeOffset]::Parse($since).LocalDateTime } -ErrorAction SilentlyContinue
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    foreach ($event in $events) {
        $xml = [xml]$event.ToXml()
        $operation = ($xml.Event.EventData.Data | Where-Object Name -eq 'DeploymentOperation').'#text'
        $fullName = ($xml.Event.EventData.Data | Where-Object Name -eq 'PackageFullName').'#text'
        $parts = $fullName -split '_'
        if ($operation -eq '2' -and $xml.Event.System.Security.UserID -eq $sid -and $parts.Count -ge 5 -and
            ($parts[0] + '_' + $parts[-1]) -eq $Family -and $parts[3] -in @('', '~')) { return $true }
    }
    return $false
}
function Register-CleanupTask {
    & (Join-Path $PSScriptRoot 'cleanup-task.ps1') -TaskName $taskName -HostPath $CleanupHostPath -Family $Family
}
function Get-CleanupHash([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-', '') }
    finally { $sha.Dispose() }
}
function Enter-CleanupWriterLease([string]$path, [string]$prefix) {
    # These names and normalization match PersistenceFileLease and DeviceIdentity.
    $hash = Get-CleanupHash ([IO.Path]::GetFullPath($path).ToUpperInvariant())
    $mutex = [Threading.Mutex]::new($false, $prefix + $hash)
    try {
        try { $acquired = $mutex.WaitOne([TimeSpan]::FromSeconds(15)) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'The saved Gateway writer is busy. Cleanup remains pending.' }
        return $mutex
    } catch { $mutex.Dispose(); throw }
}
function Write-CleanupJson([string]$path, $value) {
    $temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporary -Encoding UTF8
        if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temporary, $path, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $path) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
function Test-CleanupEndpoint([string]$left, [string]$right) {
    if ([string]::IsNullOrWhiteSpace($left) -or [string]::IsNullOrWhiteSpace($right)) { return $false }
    if ($left -ceq $right) { return $true }
    try {
        $a = [Uri]$left; $b = [Uri]$right
        return $a.IsAbsoluteUri -and $b.IsAbsoluteUri -and $a.IsLoopback -and $b.IsLoopback -and
            $a.Scheme -eq $b.Scheme -and $a.Port -eq $b.Port -and $a.PathAndQuery -ceq $b.PathAndQuery -and
            $a.Fragment -ceq $b.Fragment -and $a.UserInfo -ceq $b.UserInfo
    } catch { return $false }
}
function Get-CleanupLegacyHash($settings) {
    Get-CleanupHash (ConvertTo-Json -Compress -InputObject ([ordered]@{
        GatewayUrl = $settings.GatewayUrl; Token = $settings.Token
        BootstrapToken = $settings.BootstrapToken; UseSshTunnel = $settings.UseSshTunnel
    }))
}
function Remove-NativeGatewayData([string]$dataDir, [scriptblock]$removePackage) {
    # Called only after native installation ownership and surviving consumers have been checked.
    # Keep the existing registry writer lease across capture, package removal and retirement.
    # A durable, credential-free snapshot in owned.json fences retries against replacement records.
    $registryPath = Join-Path $dataDir 'gateways.json'
    $settingsPath = Join-Path $dataDir 'settings.json'
    Assert-ManagedPath $registryPath
    Assert-ManagedPath $settingsPath
    $registryLease = Enter-CleanupWriterLease $registryPath 'Global\OpenClaw.Persistence.'
    $settingsLease = $null
    $identityLeases = [Collections.Generic.List[Threading.Mutex]]::new()
    try {
        $settingsLease = Enter-CleanupWriterLease $settingsPath 'Global\OpenClaw.Persistence.'
        try {
            $registry = if (Test-Path -LiteralPath $registryPath) {
                Get-Content -LiteralPath $registryPath -Raw | ConvertFrom-Json
            } else { [pscustomobject]@{ gateways = @(); activeId = $null } }
            $settings = if (Test-Path -LiteralPath $settingsPath) {
                Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            } else { $null }
        } catch { throw 'The saved Gateway registry or settings are invalid. Cleanup remains pending.' }
        if ((Test-Path -LiteralPath $settingsPath) -and ($settings -isnot [pscustomobject] -or
            ($null -ne $settings.PreferredGatewayId -and $settings.PreferredGatewayId -isnot [string]) -or
            ($null -ne $settings.GatewayUrl -and $settings.GatewayUrl -isnot [string]) -or
            ($null -ne $settings.UseSshTunnel -and $settings.UseSshTunnel -isnot [bool]))) {
            throw 'The saved Gateway settings are invalid. Cleanup remains pending.'
        }
        if ($registry -isnot [pscustomobject] -or $null -eq $registry.gateways -or
            $registry.gateways -isnot [Array] -or
            ($null -ne $registry.activeId -and $registry.activeId -isnot [string])) {
            throw 'The saved Gateway registry is invalid. Cleanup remains pending.'
        }
        $ids = @{}
        foreach ($gateway in $registry.gateways) {
            if ($gateway -isnot [pscustomobject] -or $gateway.id -isnot [string] -or
                [string]::IsNullOrEmpty($gateway.id) -or $ids.ContainsKey($gateway.id)) {
                throw 'The saved Gateway registry contains an invalid identity. Cleanup remains pending.'
            }
            $ids[$gateway.id] = $gateway
        }
        $plan = $record.NativeDataRemoval
        if ($null -eq $plan) {
            $targets = @($registry.gateways | Where-Object {
                $_.isLocal -is [bool] -and $_.isLocal -and $null -eq $_.sshTunnel -and
                $null -eq $_.setupManagedDistroName -and $_.nativePackageFamilyName -ceq $record.NativeFamily
            } | ForEach-Object {
                [pscustomobject]@{ Id = $_.id; RecordHash = (Get-CleanupHash (ConvertTo-Json -InputObject $_ -Depth 100 -Compress)); KeyHash = $null }
            })
            $legacyHash = $null
            if ($settings.UseSshTunnel -ne $true -and @($targets | Where-Object {
                Test-CleanupEndpoint $settings.GatewayUrl $ids[$_.Id].url
            }).Count -gt 0) { $legacyHash = Get-CleanupLegacyHash $settings }
            $plan = [pscustomobject]@{
                InstallationId = $record.NativeInstallationId; DataDirectory = [IO.Path]::GetFullPath($dataDir)
                Targets = $targets; LegacySettingsHash = $legacyHash
            }
        } elseif ($plan -isnot [pscustomobject] -or $plan.InstallationId -cne $record.NativeInstallationId -or
            $plan.DataDirectory -ine [IO.Path]::GetFullPath($dataDir) -or
            $null -eq $plan.Targets -or $plan.Targets -isnot [Array]) {
            throw 'The pending native Gateway cleanup identity is invalid.'
        }
        $clearLegacy = $false
        if ($null -ne $plan.LegacySettingsHash) {
            if ($plan.LegacySettingsHash -isnot [string] -or $plan.LegacySettingsHash -notmatch '\A[A-F0-9]{64}\z') {
                throw 'The pending native Gateway settings binding is invalid.'
            }
            if ((Get-CleanupLegacyHash $settings) -ceq $plan.LegacySettingsHash) { $clearLegacy = $true }
            elseif (-not [string]::IsNullOrEmpty($settings.GatewayUrl) -or
                $null -ne $settings.Token -or $null -ne $settings.BootstrapToken) {
                throw 'The saved Gateway settings changed after cleanup started. They were preserved.'
            }
        }
        $seen = @{}
        foreach ($target in $plan.Targets) {
            if ($target -isnot [pscustomobject] -or $target.Id -isnot [string] -or
                $target.Id -notmatch '\A[A-Za-z0-9][A-Za-z0-9_-]{0,127}\z' -or
                $target.Id -match '\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z' -or
                $target.RecordHash -isnot [string] -or $target.RecordHash -notmatch '\A[A-F0-9]{64}\z' -or
                ($null -ne $target.KeyHash -and ($target.KeyHash -isnot [string] -or $target.KeyHash -notmatch '\A[A-F0-9]{64}\z')) -or
                $seen.ContainsKey($target.Id)) {
                throw 'The pending native Gateway cleanup target is invalid.'
            }
            $seen[$target.Id] = $true
            if ($ids.ContainsKey($target.Id) -and
                (Get-CleanupHash (ConvertTo-Json -InputObject $ids[$target.Id] -Depth 100 -Compress)) -cne $target.RecordHash) {
                throw 'A saved Gateway changed after cleanup started. Its credentials were preserved.'
            }
            $identityDir = Join-Path (Join-Path $dataDir 'gateways') $target.Id
            $keyPath = Join-Path $identityDir 'device-key-ed25519.json'
            Assert-ManagedPath $keyPath
            $identityLeases.Add((Enter-CleanupWriterLease $keyPath 'Local\OpenClaw.DeviceIdentity.'))
            $keyHash = if (Test-Path -LiteralPath $keyPath) { Get-CleanupHash ([IO.File]::ReadAllText($keyPath)) } else { $null }
            if ($null -eq $record.NativeDataRemoval) { $target.KeyHash = $keyHash }
            elseif ($null -ne $keyHash -and $keyHash -cne $target.KeyHash) {
                throw 'A Gateway device identity changed after cleanup started. It was preserved.'
            }
        }
        if ($null -eq $record.NativeDataRemoval) {
            $record | Add-Member NoteProperty NativeDataRemoval $plan -Force
            Write-CleanupJson $recordPath $record
        }
        & $removePackage
        # The callback must confirm package absence, including after an interrupted prior removal.
        # Delete only the captured key, never recursively delete an identity directory.
        foreach ($target in $plan.Targets) {
            $identityDir = Join-Path (Join-Path $dataDir 'gateways') $target.Id
            $keyPath = Join-Path $identityDir 'device-key-ed25519.json'
            Assert-ManagedPath $keyPath
            if (Test-Path -LiteralPath $keyPath) {
                if ((Get-CleanupHash ([IO.File]::ReadAllText($keyPath))) -cne $target.KeyHash) {
                    throw 'A Gateway device identity changed during cleanup. It was preserved.'
                }
                [IO.File]::Delete($keyPath)
            }
            if ((Test-Path -LiteralPath $identityDir) -and
                -not @(Get-ChildItem -LiteralPath $identityDir -Force).Count) { [IO.Directory]::Delete($identityDir) }
        }
        $clearPreferred = $null -ne $settings.PreferredGatewayId -and $seen.ContainsKey($settings.PreferredGatewayId)
        if ($clearLegacy) {
            $settings.GatewayUrl = ''
            $settings.PSObject.Properties.Remove('Token')
            $settings.PSObject.Properties.Remove('BootstrapToken')
        }
        if ($clearPreferred) {
            $settings.PreferredGatewayId = $null
        }
        if ($clearLegacy -or $clearPreferred) {
            Write-CleanupJson $settingsPath $settings
        }
        if ($plan.Targets.Count -gt 0) {
            $registry.gateways = @($registry.gateways | Where-Object { -not $seen.ContainsKey($_.id) })
            if ($null -ne $registry.activeId -and $seen.ContainsKey($registry.activeId)) { $registry.activeId = $null }
            Assert-ManagedPath $registryPath
            Write-CleanupJson $registryPath $registry
        }
        # Persist completion before the worker unregisters itself. No broad NativeFamily receipt
        # is emitted: a later startup must not reinterpret it against a replacement installation.
        $record.OwnNativePackage = $false
        $record.PSObject.Properties.Remove('NativeDataRemoval')
        Write-CleanupJson $recordPath $record
    } finally {
        for ($index = $identityLeases.Count - 1; $index -ge 0; $index--) {
            $identityLeases[$index].ReleaseMutex(); $identityLeases[$index].Dispose()
        }
        if ($null -ne $settingsLease) { $settingsLease.ReleaseMutex(); $settingsLease.Dispose() }
        $registryLease.ReleaseMutex(); $registryLease.Dispose()
    }
}
function Get-OwnedProfilePath([string]$kind) {
    $dataName = switch -Regex ($Family) {
        '\AOpenClawFoundation\.OpenClaw_[A-Za-z0-9]+\z' { 'OpenClawTray'; break }
        '\AOpenClawFoundation\.OpenClaw\.Dev_[A-Za-z0-9]+\z' { 'OpenClawTray-Dev'; break }
        default { throw 'The Companion data profile could not be identified. Cleanup remains pending.' }
    }
    switch ($kind) {
        'Roaming' { return Join-Path ([Environment]::GetFolderPath('ApplicationData')) $dataName }
        'Package' { return Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Packages\$Family\LocalCache\Roaming\$dataName" }
        default { throw 'Native Gateway cleanup has no confirmed Companion profile. Credentials were preserved.' }
    }
}
function Remove-OwnedNativeGateway([string]$dataDir, [string]$nativeMarker) {
    if (-not $nativeMarker) {
        $nativeMarker = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Packages\$($record.NativeFamily)\LocalState\companion-cleanup-owner.txt"
    }
    $ownsInstallation = $record.NativeInstallationId -and (Test-Path -LiteralPath $nativeMarker) -and
        (Get-Content -LiteralPath $nativeMarker -Raw).Trim() -ceq $record.NativeInstallationId
    if (-not $ownsInstallation -and $null -eq $record.NativeDataRemoval) { return }
    # A shared Store package remains until the other installed Companion consumers leave.
    if (Get-AppxPackage -PackageTypeFilter Main | Where-Object {
        $_.Name -in @('OpenClawFoundation.OpenClaw', 'OpenClawFoundation.OpenClaw.Dev')
    }) { throw 'Another Companion still uses the native Gateway. Cleanup remains pending.' }
    if (-not $dataDir) { $dataDir = Get-OwnedProfilePath $record.NativeProfile }
    Remove-NativeGatewayData $dataDir {
        $native = @(Get-AppxPackage -Name OpenClawFoundation.OpenClawGateway -PackageTypeFilter Main |
            Where-Object { $_.Publisher -eq 'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC' -and $_.PackageFamilyName -eq $record.NativeFamily })
        if ($native.Count -gt 0) {
            if (-not $ownsInstallation -or -not (Test-Path -LiteralPath $nativeMarker) -or
                (Get-Content -LiteralPath $nativeMarker -Raw).Trim() -cne $record.NativeInstallationId) {
                throw 'Native Gateway ownership changed. Its credentials were preserved.'
            }
            $native | Remove-AppxPackage -ErrorAction Stop
        }
        if (Get-AppxPackage -PackageTypeFilter Main | Where-Object PackageFamilyName -eq $record.NativeFamily) {
            throw 'Native Gateway removal is not confirmed. Cleanup remains pending.'
        }
    }
}
New-Item -ItemType Directory -Path $root -Force | Out-Null
$lock = $null
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(150)
while (-not $lock) {
    try { $lock = [IO.File]::Open((Join-Path $root 'cleanup.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
        Start-Sleep -Milliseconds 200
    }
}
try {
    Assert-ValidRemovalReceipt
    $record = if (Test-Path $recordPath) { Get-Content $recordPath -Raw | ConvertFrom-Json } else {
        [pscustomobject]@{ DistroName = $null; InstallPath = $null; OwnNativePackage = $false; InstallationId = $null; RegisteredUtc = $null }
    }
    foreach ($property in @('RegistrationId', 'NativeFamily', 'NativeInstallationId')) {
        if ($property -notin $record.PSObject.Properties.Name) { $record | Add-Member NoteProperty $property $null }
    }
    if ($Preserve) {
        # Preserving/disowning a native installation is not evidence of its removal.
        Record-Removal $record.DistroName $record.InstallPath $null
    } elseif ($Register) {
        if ($null -ne $record.NativeDataRemoval) {
            throw 'Native Gateway credential cleanup remains pending. Its ownership cannot be refreshed or rebound.'
        }
        if (-not (Test-Path $sentinel)) {
            New-Item -ItemType Directory -Path (Split-Path $sentinel) -Force | Out-Null
            [Guid]::NewGuid().ToString('N') | Set-Content $sentinel
        }
        $record.InstallationId = (Get-Content $sentinel -Raw).Trim()
        $record.RegisteredUtc = [DateTimeOffset]::UtcNow.ToString('o')
        if ($DistroName) {
            if (-not [IO.Path]::IsPathRooted($InstallPath)) { throw 'The managed Gateway install path must be absolute.' }
            $registrations = @(Read-WslRegistrations | Where-Object DistributionName -eq $DistroName)
            if ($registrations.Count -ne 1 -or (Normalize-Path $registrations[0].BasePath) -ne (Normalize-Path $InstallPath) -or
                (-not $CreatedDistro -and (-not $record.DistroName -or $record.DistroName -ne $DistroName -or
                    -not $record.RegistrationId -or $record.RegistrationId -ne $registrations[0].PSChildName -or
                    (Normalize-Path $record.InstallPath) -ne (Normalize-Path $InstallPath)))) {
                throw 'Managed WSL registration could not be confirmed. Preserve existing Gateways or retry cleanup.'
            }
            $record.DistroName = $DistroName
            $record.InstallPath = Normalize-Path $InstallPath
            $record.RegistrationId = $registrations[0].PSChildName
        }
        if ($OwnNativePackage -or ($record.OwnNativePackage -and -not $record.NativeProfile)) {
            $kind = @('Package', 'Roaming') | Where-Object { (Get-OwnedProfilePath $_) -ieq $PhysicalProfile }
            if (@($kind).Count -ne 1) { throw 'Cannot bind native cleanup to this Companion profile.' }
            $record | Add-Member NoteProperty NativeProfile $kind -Force
        }
        if ($OwnNativePackage) {
            $native = Get-AppxPackage -Name OpenClawFoundation.OpenClawGateway -PackageTypeFilter Main |
                Where-Object Publisher -eq 'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC'
            if (@($native).Count -ne 1) { throw 'A completed Store Gateway deployment is required before claiming ownership.' }
            $record.NativeFamily = $native.PackageFamilyName
            $record.NativeInstallationId = [Guid]::NewGuid().ToString('N')
            $nativeMarker = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Packages\$($record.NativeFamily)\LocalState\companion-cleanup-owner.txt"
            New-Item -ItemType Directory -Path (Split-Path $nativeMarker) -Force | Out-Null
            $record.NativeInstallationId | Set-Content $nativeMarker
            $record.OwnNativePackage = $true
        }
        Write-CleanupJson $recordPath $record
        Register-CleanupTask
        exit 0
    } else {
    $missingPath = Join-Path $root 'package-missing.txt'
    $registered = [bool](Get-AppxPackage -PackageTypeFilter Main | Where-Object PackageFamilyName -eq $Family)
    $sameInstallation = (Test-Path $sentinel) -and (Get-Content $sentinel -Raw).Trim() -eq $record.InstallationId
    if ($sameInstallation) {
        Remove-Item $missingPath -Force -ErrorAction SilentlyContinue
        exit 0
    }
    # Updates preserve LocalState. A successful Remove receipt plus a lost owner marker
    # identifies uninstall, even if the same family was reinstalled before this check.
    $removed = $false
    if (-not $sameInstallation -and $record.RegisteredUtc) {
        $removed = Test-PackageRemovedSince $record.RegisteredUtc
    }
    if (-not $removed) {
        if ($registered) { exit 0 }
        if (-not (Test-Path $missingPath)) {
            [DateTimeOffset]::UtcNow.ToString('o') | Set-Content $missingPath
            exit 0
        }
        if (([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse((Get-Content $missingPath -Raw))).TotalSeconds -lt 30) { exit 0 }
        if (Get-AppxPackage -PackageTypeFilter Main | Where-Object PackageFamilyName -eq $Family) { exit 0 }
    }
    if ($record.DistroName) {
        Assert-ManagedPath $record.InstallPath
        $registrations = @(Read-WslRegistrations | Where-Object DistributionName -eq $record.DistroName)
        if ($registrations.Count -gt 1) { throw 'Duplicate managed WSL registrations. Cleanup was not started.' }
        if ($registrations.Count -eq 1) {
            $registeredPath = Normalize-Path $registrations[0].BasePath
            if (-not $record.RegistrationId -or $registrations[0].PSChildName -ne $record.RegistrationId -or
                $registeredPath -ne (Normalize-Path $record.InstallPath)) { throw 'Managed WSL ownership changed. Cleanup was not started.' }
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = Join-Path $env:SystemRoot 'System32\wsl.exe'
            $start.Arguments = '--unregister ' + $record.DistroName
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $process = [Diagnostics.Process]::Start($start)
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(120000)) {
                try { $process.Kill() } catch { }
                throw 'Managed WSL removal timed out. Cleanup remains pending.'
            }
            if ($process.ExitCode -ne 0) { throw 'Managed WSL removal failed. Cleanup remains pending.' }
        }
        if ($registrations.Count -eq 1 -and (Test-Path -LiteralPath $record.InstallPath)) {
            Assert-ManagedPath $record.InstallPath
            # WSL removes its disk. Never recursively delete unknown files left at this path.
            if (-not @(Get-ChildItem -LiteralPath $record.InstallPath -Force -ErrorAction Stop).Count) {
                Remove-Item -LiteralPath $record.InstallPath -Force
            }
        }
        Record-Removal $record.DistroName $record.InstallPath $null
        $record.DistroName = $null
        Write-CleanupJson $recordPath $record
    }
    if ($record.OwnNativePackage) {
        Remove-OwnedNativeGateway
    }
    }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    if (Test-Path $recordPath) { Remove-Item $recordPath -Force }
    'complete' | Set-Content (Join-Path $root 'cleanup-result.txt')
} catch {
    $_.Exception.Message | Set-Content (Join-Path $root 'cleanup-error.txt')
    throw
} finally { $lock.Dispose() }
