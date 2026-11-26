param(
    [Parameter(Mandatory)][string]$Family,
    [string]$CleanupHostPath,
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
$preservationPendingPath = Join-Path $root 'gateway-preservation.pending'
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
function Invoke-NativeTeardownCommand([string]$alias) {
    # Keep process exit and redirected output inside one budget, below the worker's 180 s caller limit.
    $timeoutMilliseconds = 120000
    $configuredTimeout = 0
    if ([int]::TryParse($env:OPENCLAW_GATEWAY_CLEANUP_TEARDOWN_TIMEOUT_MS, [ref]$configuredTimeout) -and
        $configuredTimeout -gt 0 -and $configuredTimeout -le $timeoutMilliseconds) {
        $timeoutMilliseconds = $configuredTimeout
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $alias
    $start.Arguments = 'teardown --force --json'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $remaining = [Math]::Max(0, $timeoutMilliseconds - [int]$elapsed.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remaining)) {
            try { $process.Kill() } catch { }
            throw 'Native Gateway teardown timed out; its outcome is unconfirmed. Cleanup remains pending.'
        }
        $remaining = [Math]::Max(0, $timeoutMilliseconds - [int]$elapsed.ElapsedMilliseconds)
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), $remaining)) {
            throw 'Native Gateway teardown output is incomplete. Cleanup remains pending.'
        }
        # Do not persist raw CLI output, which can contain account/profile details.
        [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout.Result }
    } finally { $process.Dispose() }
}
function Invoke-NativeGatewayTeardown($package) {
    $alias = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Microsoft\WindowsApps\$($package.PackageFamilyName)\clawctl.exe"
    if (-not (Test-Path -LiteralPath $alias -PathType Leaf)) {
        throw 'The owned Gateway teardown CLI is unavailable. Cleanup remains pending.'
    }
    $command = Invoke-NativeTeardownCommand $alias
    try { $result = ConvertFrom-Json -InputObject $command.Stdout -ErrorAction Stop }
    catch { throw 'Native Gateway teardown returned an invalid result. Cleanup remains pending.' }
    # Released clawctl teardown confirms recovery removal before reporting these terminal states.
    if ($command.ExitCode -ne 0 -or $result -isnot [pscustomobject] -or
        $result.ok -isnot [bool] -or -not $result.ok -or $result.command -cne 'teardown' -or
        $result.gateway.state -cne 'records-removed' -or $result.session.state -cnotin @('removed', 'not-configured')) {
        throw 'Native Gateway teardown did not confirm success. Cleanup remains pending.'
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
    if (-not $Register -and -not $Preserve -and -not (Test-Path $recordPath)) { exit 0 }
    # Keep the worker fail-closed while Companion finalizes an explicit preservation choice.
    if (-not $Register -and -not $Preserve -and
        (Test-Path -LiteralPath $preservationPendingPath)) { exit 0 }
    Assert-ValidRemovalReceipt
    $record = if (Test-Path $recordPath) { Get-Content $recordPath -Raw | ConvertFrom-Json } else {
        [pscustomobject]@{ DistroName = $null; InstallPath = $null; OwnNativePackage = $false; InstallationId = $null; RegisteredUtc = $null }
    }
    foreach ($property in @('RegistrationId', 'NativeFamily', 'NativeInstallationId')) {
        if ($property -notin $record.PSObject.Properties.Name) { $record | Add-Member NoteProperty $property $null }
    }
    if ($Preserve) {
        Record-Removal $record.DistroName $record.InstallPath $record.NativeFamily
    } elseif ($Register) {
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
        foreach ($pendingPath in @($preservationPendingPath, (Join-Path $root 'cleanup-task-removal.pending'))) {
            if (Test-Path -LiteralPath $pendingPath) { Remove-Item -LiteralPath $pendingPath -Force -ErrorAction Stop }
        }
        $record | ConvertTo-Json | Set-Content $recordPath -Encoding UTF8
        try { Register-CleanupTask }
        catch {
            [Console]::Error.WriteLine("OPENCLAW_CLEANUP_TASK_UNAVAILABLE: " + $_.Exception.Message)
            exit 12
        }
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
        $record | ConvertTo-Json | Set-Content $recordPath -Encoding UTF8
    }
    if ($record.OwnNativePackage) {
        $nativeMarker = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Packages\$($record.NativeFamily)\LocalState\companion-cleanup-owner.txt"
        if ($record.NativeInstallationId -and (Test-Path $nativeMarker) -and
            (Get-Content $nativeMarker -Raw).Trim() -eq $record.NativeInstallationId) {
            # A shared Store package remains until the other installed Companion consumers leave.
            if (Get-AppxPackage -PackageTypeFilter Main | Where-Object {
                $_.PackageFamilyName -ne $Family -and $_.Name -in @('OpenClawFoundation.OpenClaw', 'OpenClawFoundation.OpenClaw.Dev')
            }) { exit 0 }
            $native = @(Get-AppxPackage -Name OpenClawFoundation.OpenClawGateway -PackageTypeFilter Main |
                Where-Object { $_.Publisher -eq 'CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC' -and $_.PackageFamilyName -eq $record.NativeFamily })
            if ($native.Count -ne 1) { throw 'The owned Gateway package could not be confirmed. Cleanup remains pending.' }
            Invoke-NativeGatewayTeardown $native[0]
            # Teardown can take time. Do not remove a replaced package or a newly shared installation.
            $current = @(Get-AppxPackage -Name OpenClawFoundation.OpenClawGateway -PackageTypeFilter Main |
                Where-Object { $_.Publisher -eq $native[0].Publisher -and $_.PackageFullName -eq $native[0].PackageFullName })
            if ($current.Count -ne 1 -or -not (Test-Path $nativeMarker) -or
                (Get-Content $nativeMarker -Raw).Trim() -ne $record.NativeInstallationId) {
                throw 'Native Gateway ownership changed during teardown. Cleanup remains pending.'
            }
            if (Get-AppxPackage -PackageTypeFilter Main | Where-Object {
                $_.PackageFamilyName -ne $Family -and $_.Name -in @('OpenClawFoundation.OpenClaw', 'OpenClawFoundation.OpenClaw.Dev')
            }) { exit 0 }
            Remove-AppxPackage -Package $native[0].PackageFullName -ErrorAction Stop
            Record-Removal $null $null $record.NativeFamily
        }
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
