<#
.SYNOPSIS
    Removes the OpenClaw local native Windows or WSL gateway during app uninstall.

.DESCRIPTION
    This helper is launched by the Inno uninstaller after the user chooses to
    remove the local gateway. It removes the native gateway Scheduled Task via
    the user-level OpenClaw CLI (with a direct task cleanup fallback), or directly
    unregisters the app-owned WSL distro. It does not load app payload binaries
    while Inno removes installed files.
#>

[CmdletBinding()]
param(
    [string]$AppRoot = $PSScriptRoot,
    [string]$DataDirectoryName = 'OpenClawTray',
    [string]$AutoStartName = 'OpenClawTray',
    [string]$StartupTaskName = 'OpenClaw Companion',
    [string]$GatewayTaskName = 'OpenClaw Gateway (OpenClawGateway)',
    [int]$GatewayPort = 18789,
    [string]$DistroName = 'OpenClawGateway'
)

$ErrorActionPreference = 'Stop'

$resultPath = Join-Path $AppRoot 'uninstall-gateway-result.json'
$errorPath = Join-Path $AppRoot 'uninstall-gateway-error.log'
$wslLogPath = Join-Path $AppRoot 'uninstall-gateway-wsl.log'
$cleanupWarnings = New-Object 'System.Collections.Generic.List[string]'
$script:installedGatewayMode = 'Wsl'

if ($DataDirectoryName -notmatch '^[A-Za-z0-9._-]+$') {
    throw "Invalid data directory name '$DataDirectoryName'."
}
if ($DistroName -notmatch '^[A-Za-z0-9._-]+$') {
    throw "Invalid WSL distro name '$DistroName'."
}
if ($GatewayPort -le 0 -or $GatewayPort -gt 65535) {
    throw "Invalid gateway port '$GatewayPort'."
}

function Ensure-AppRoot {
    if (-not [string]::IsNullOrWhiteSpace($AppRoot) -and -not (Test-Path -LiteralPath $AppRoot)) {
        New-Item -ItemType Directory -Path $AppRoot -Force | Out-Null
    }
}

function Write-GatewayLog {
    param([string]$Message)

    try {
        Ensure-AppRoot
        "[$(Get-Date -Format 'o')] $Message" | Out-File -LiteralPath $wslLogPath -Encoding UTF8 -Append -Force
    } catch {
        Write-Verbose "Failed to write gateway uninstall log: $($_.Exception.Message)"
    }
}

function Add-CleanupWarning {
    param([string]$Message)

    $script:cleanupWarnings.Add($Message)
    Write-GatewayLog "Windows artifact cleanup warning: $Message"
}

function Write-GatewayResult {
    param(
        [bool]$Succeeded,
        [int]$ExitCode,
        [string]$Message,
        [object]$Details = $null
    )

    try {
        Ensure-AppRoot
        [ordered]@{
            timestamp = (Get-Date).ToString('o')
            succeeded = $Succeeded
            exitCode = $ExitCode
            message = $Message
            details = $Details
        } | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $resultPath -Encoding UTF8 -Force
    } catch {
        $fallback = "[$(Get-Date -Format 'o')] Failed to write gateway uninstall result: $($_.Exception.Message)"
        try { $fallback | Out-File -LiteralPath $errorPath -Encoding UTF8 -Force } catch {}
    }
}

function Resolve-AppDataDir {
        if ($env:OPENCLAW_TRAY_DATA_DIR) {
            return $env:OPENCLAW_TRAY_DATA_DIR
        }

        return Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) $DataDirectoryName
    }

    function Resolve-LocalDataDir {
        if ($env:OPENCLAW_TRAY_LOCALAPPDATA_DIR) {
            return Join-Path $env:OPENCLAW_TRAY_LOCALAPPDATA_DIR $DataDirectoryName
        }

        if ($env:OPENCLAW_TRAY_LOCAL_DATA_DIR) {
            return $env:OPENCLAW_TRAY_LOCAL_DATA_DIR
        }

        return Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) $DataDirectoryName
    }

    function Get-JsonPropertyValue {
        param(
            [object]$Object,
            [string]$Name
        )

        if ($null -eq $Object) {
            return $null
        }

        $property = $Object.PSObject.Properties[$Name]
        if ($null -eq $property) {
            return $null
        }

        return $property.Value
    }

    function Read-JsonFile {
        param([string]$Path)

        if (-not (Test-Path -LiteralPath $Path)) {
            return $null
        }

        try {
            return Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        } catch {
            Add-CleanupWarning "Failed to read JSON file '$Path': $($_.Exception.Message)"
            return $null
        }
    }

    function Write-JsonFileAtomic {
        param(
            [string]$Path,
            [object]$Value
        )

        $directory = Split-Path -Parent $Path
        if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
        }

        $tempPath = Join-Path $directory ('.' + (Split-Path -Leaf $Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            $Value | ConvertTo-Json -Depth 50 | Out-File -LiteralPath $tempPath -Encoding UTF8 -Force
            Move-Item -LiteralPath $tempPath -Destination $Path -Force
        } catch {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
            throw
        }
    }

    function Test-LocalGatewayUrl {
        param([string]$Url)

        if ([string]::IsNullOrWhiteSpace($Url)) {
            return $false
        }

        try {
            $uri = [Uri]$Url
            $uriHost = $uri.Host.ToLowerInvariant()
            return $uriHost -eq 'localhost' -or $uriHost -eq '127.0.0.1' -or $uriHost -eq '::1' -or $uriHost -eq '[::1]'
        } catch {
            return $false
        }
    }

    function Test-SetupManagedLocalRecord {
        param(
            [object]$Record,
            [string]$InstallMode,
            [string]$OwnedNativeRecordId
        )

        $isLocal = [bool](Get-JsonPropertyValue $Record 'isLocal')
        $sshTunnel = Get-JsonPropertyValue $Record 'sshTunnel'
        if (-not $isLocal -or $null -ne $sshTunnel) {
            return $false
        }

        $setupManagedDistroName = [string](Get-JsonPropertyValue $Record 'setupManagedDistroName')
        $friendlyName = [string](Get-JsonPropertyValue $Record 'friendlyName')
        $recordId = [string](Get-JsonPropertyValue $Record 'id')
        $url = [string](Get-JsonPropertyValue $Record 'url')
        $isLocalUrl = Test-LocalGatewayUrl $url
        $isManagedWsl =
            [string]::Equals($setupManagedDistroName, $DistroName, [StringComparison]::Ordinal) -or
            ([string]::IsNullOrWhiteSpace($setupManagedDistroName) -and
                [string]::Equals($friendlyName, "Local ($DistroName)", [StringComparison]::Ordinal) -and
                $isLocalUrl)
        $isManagedNative =
            -not [string]::IsNullOrWhiteSpace($OwnedNativeRecordId) -and
            [string]::Equals($recordId, $OwnedNativeRecordId, [StringComparison]::Ordinal) -and
            [string]::IsNullOrWhiteSpace($setupManagedDistroName) -and
            $isLocalUrl

        if ([string]::Equals($InstallMode, 'NativeWindows', [StringComparison]::OrdinalIgnoreCase)) {
            return $isManagedNative
        }
        if ([string]::Equals($InstallMode, 'Wsl', [StringComparison]::OrdinalIgnoreCase)) {
            return $isManagedWsl
        }

        return $isManagedWsl -or $isManagedNative
    }

    function Test-ExternalGatewayRecord {
        param([object]$Record)

        $isLocal = [bool](Get-JsonPropertyValue $Record 'isLocal')
        $sshTunnel = Get-JsonPropertyValue $Record 'sshTunnel'
        $url = [string](Get-JsonPropertyValue $Record 'url')
        return (-not $isLocal) -and -not ($null -eq $sshTunnel -and (Test-LocalGatewayUrl $url))
    }

    function Remove-FileIfExists {
        param(
            [string]$Path,
            [string]$Label
        )

        try {
            if (Test-Path -LiteralPath $Path -PathType Leaf) {
                Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
                Write-GatewayLog "Deleted $Label."
            } else {
                Write-GatewayLog "$Label already absent."
            }
        } catch {
            Add-CleanupWarning "Failed to delete $Label '$Path': $($_.Exception.Message)"
        }
    }

    function Remove-DirectoryIfExists {
        param(
            [string]$Path,
            [string]$Label
        )

        try {
            if (Test-Path -LiteralPath $Path -PathType Container) {
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
                Write-GatewayLog "Deleted $Label directory."
            }
        } catch {
            Add-CleanupWarning "Failed to delete $Label directory '$Path': $($_.Exception.Message)"
        }
    }

    function Remove-OwnedDirectoryStrict {
        param(
            [string]$Path,
            [string]$Label
        )

        if (-not (Test-Path -LiteralPath $Path)) {
            Write-GatewayLog "$Label directory already absent."
            return
        }

        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively delete $Label reparse point '$Path'."
        }
        if (-not $item.PSIsContainer) {
            throw "Expected $Label to be a directory: '$Path'."
        }

        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $Path) {
            throw "Failed to verify removal of $Label directory '$Path'."
        }
        Write-GatewayLog "Deleted $Label directory."
    }

    function Remove-AutostartRegistryValue {
        $runKey = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
        try {
            $value = Get-ItemProperty -LiteralPath $runKey -Name $AutoStartName -ErrorAction SilentlyContinue
            if ($null -ne $value) {
                Remove-ItemProperty -LiteralPath $runKey -Name $AutoStartName -ErrorAction Stop
                Write-GatewayLog "Removed $AutoStartName autostart registry value."
            } else {
                Write-GatewayLog "$AutoStartName autostart registry value already absent."
            }
        } catch {
            Add-CleanupWarning "Failed to remove $AutoStartName autostart registry value: $($_.Exception.Message)"
        }
    }

    function Remove-ScheduledStartupTask {
        $schtasks = Join-Path $env:WINDIR 'System32\schtasks.exe'
        if (-not (Test-Path -LiteralPath $schtasks)) {
            Add-CleanupWarning "schtasks.exe was not found; could not remove startup task '$StartupTaskName'."
            return
        }

        try {
            $output = & $schtasks /Delete /TN $StartupTaskName /F 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-GatewayLog "Removed startup task '$StartupTaskName'."
            } else {
                Write-GatewayLog "Startup task '$StartupTaskName' was absent or could not be removed: $output"
            }
        } catch {
            Add-CleanupWarning "Failed to remove startup task '$StartupTaskName': $($_.Exception.Message)"
        }
    }

    function Remove-SetupManagedGatewayRecords {
        param(
            [string]$DataDir,
            [string]$InstallMode,
            [string]$OwnedNativeRecordId
        )

        $gatewaysPath = Join-Path $DataDir 'gateways.json'
        $registry = Read-JsonFile $gatewaysPath
        if ($null -eq $registry) {
            return [pscustomobject]@{
                RemainingCount = 0
                HasExternalGateways = $false
            }
        }

        $gatewayProperty = $registry.PSObject.Properties['gateways']
        $records = @()
        if ($null -ne $gatewayProperty -and $null -ne $gatewayProperty.Value) {
            $records = @($gatewayProperty.Value)
        }

        $remaining = New-Object System.Collections.ArrayList
        $removed = New-Object System.Collections.ArrayList
        foreach ($record in $records) {
            if (Test-SetupManagedLocalRecord -Record $record -InstallMode $InstallMode -OwnedNativeRecordId $OwnedNativeRecordId) {
                [void]$removed.Add($record)
            } else {
                [void]$remaining.Add($record)
            }
        }

        foreach ($record in $removed) {
            $id = [string](Get-JsonPropertyValue $record 'id')
            if ([string]::IsNullOrWhiteSpace($id)) {
                continue
            }

            $identityDir = Join-Path (Join-Path $DataDir 'gateways') $id
            try {
                if (Test-Path -LiteralPath $identityDir -PathType Container) {
                    Remove-Item -LiteralPath $identityDir -Recurse -Force -ErrorAction Stop
                    Write-GatewayLog "Deleted identity directory for local gateway record $id."
                }
            } catch {
                Add-CleanupWarning "Failed to delete identity directory '$identityDir': $($_.Exception.Message)"
            }
        }

        if ($removed.Count -gt 0) {
            try {
                $registry.gateways = @($remaining.ToArray())
                $activeId = [string](Get-JsonPropertyValue $registry 'activeId')
                if ($removed | Where-Object { [string](Get-JsonPropertyValue $_ 'id') -eq $activeId }) {
                    $registry.activeId = $null
                }

                Write-JsonFileAtomic -Path $gatewaysPath -Value $registry
                Write-GatewayLog "Removed $($removed.Count) setup-managed local gateway record(s)."
            } catch {
                Add-CleanupWarning "Failed to update gateways.json: $($_.Exception.Message)"
            }
        } else {
            Write-GatewayLog 'No setup-managed local gateway records found.'
        }

        $hasExternalGateways = $false
        foreach ($record in @($remaining.ToArray())) {
            if (Test-ExternalGatewayRecord $record) {
                $hasExternalGateways = $true
                break
            }
        }

        return [pscustomobject]@{
            RemainingCount = $remaining.Count
            HasExternalGateways = $hasExternalGateways
        }
    }

    function Clear-RootDeviceTokenForRole {
        param(
            [string]$DataDir,
            [string]$Role
        )

        $keyPath = Join-Path $DataDir 'device-key-ed25519.json'
        $keyData = Read-JsonFile $keyPath
        if ($null -eq $keyData) {
            Write-GatewayLog "Root device identity file absent or unreadable for $Role token cleanup."
            return
        }

        $tokenPropertyName = if ($Role -eq 'node') { 'NodeDeviceToken' } else { 'DeviceToken' }
        $scopesPropertyName = if ($Role -eq 'node') { 'NodeDeviceTokenScopes' } else { 'DeviceTokenScopes' }
        $tokenProperty = $keyData.PSObject.Properties[$tokenPropertyName]

        if ($null -eq $tokenProperty -or [string]::IsNullOrEmpty([string]$tokenProperty.Value)) {
            Write-GatewayLog "Root $Role device token already absent."
            return
        }

        try {
            $tokenProperty.Value = $null
            $scopesProperty = $keyData.PSObject.Properties[$scopesPropertyName]
            if ($null -ne $scopesProperty) {
                $scopesProperty.Value = $null
            }

            Write-JsonFileAtomic -Path $keyPath -Value $keyData
            Write-GatewayLog "Cleared root $Role device token."
        } catch {
            Add-CleanupWarning "Failed to clear root $Role device token: $($_.Exception.Message)"
        }
    }

    function Reset-OnboardingSettings {
        param(
            [string]$DataDir,
            [bool]$PreserveNodeSettings
        )

        $settingsPath = Join-Path $DataDir 'settings.json'
        $settings = Read-JsonFile $settingsPath
        if ($null -eq $settings) {
            Write-GatewayLog 'settings.json absent or unreadable; onboarding settings not reset.'
            return
        }

        $changed = $false
        if ($settings.PSObject.Properties['GatewayUrl']) {
            $settings.PSObject.Properties.Remove('GatewayUrl')
            $changed = $true
        }

        if (-not $PreserveNodeSettings -and $settings.PSObject.Properties['EnableNodeMode']) {
            $settings.EnableNodeMode = $false
            $changed = $true
        }

        if (-not $PreserveNodeSettings -and $settings.PSObject.Properties['AutoStart']) {
            $settings.AutoStart = $false
            $changed = $true
        }

        if (-not $changed) {
            Write-GatewayLog 'No onboarding settings needed reset.'
            return
        }

        try {
            Write-JsonFileAtomic -Path $settingsPath -Value $settings
            Write-GatewayLog 'Reset onboarding settings.'
        } catch {
            Add-CleanupWarning "Failed to reset onboarding settings: $($_.Exception.Message)"
        }
    }

    function Remove-KeepaliveMarker {
        param([string]$LocalDataDir)

        $markerDir = Join-Path $LocalDataDir 'wsl-keepalive'
        $markerPath = Join-Path $markerDir "$DistroName.json"
        Remove-FileIfExists -Path $markerPath -Label 'keepalive marker'

        try {
            if ((Test-Path -LiteralPath $markerDir -PathType Container) -and -not (Get-ChildItem -LiteralPath $markerDir -Force -ErrorAction Stop | Select-Object -First 1)) {
                Remove-Item -LiteralPath $markerDir -Force -ErrorAction Stop
                Write-GatewayLog 'Deleted empty wsl-keepalive directory.'
            }
        } catch {
            Add-CleanupWarning "Failed to remove empty wsl-keepalive directory '$markerDir': $($_.Exception.Message)"
        }
    }

    function Get-InstalledGatewayMode {
        param(
            [string]$DataDir,
            [string]$LocalDataDir
        )

        # Written before native config/service mutation, so this marker is the
        # newest setup intent after an interrupted native mode switch.
        $nativeOwnershipPath = Join-Path $LocalDataDir 'native-gateway-install.json'
        if (Test-Path -LiteralPath $nativeOwnershipPath -PathType Leaf) {
            $nativeOwnership = Read-JsonFile $nativeOwnershipPath
            if (Test-NativeOwnershipMatches -Ownership $nativeOwnership) {
                return 'NativeWindows'
            }
            throw 'Native ownership marker belongs to a different profile or Scheduled Task; refusing destructive cleanup.'
        }

        $profileOwnershipPath = Join-Path $LocalDataDir 'native-gateway-profile-owner.json'
        if (Test-Path -LiteralPath $profileOwnershipPath -PathType Leaf) {
            $profileOwnership = Read-JsonFile $profileOwnershipPath
            if (Test-NativeOwnershipMatches -Ownership $profileOwnership) {
                return 'NativeWindows'
            }
            throw 'Native profile ownership marker belongs to a different profile or Scheduled Task; refusing destructive cleanup.'
        }

        $state = Read-JsonFile (Join-Path $LocalDataDir 'setup-state.json')
        $persistedMode = [string](Get-JsonPropertyValue $state 'InstallMode')
        if ([string]::Equals($persistedMode, 'NativeWindows', [StringComparison]::OrdinalIgnoreCase)) {
            return 'NativeWindows'
        }
        if ([string]::Equals($persistedMode, 'Wsl', [StringComparison]::OrdinalIgnoreCase)) {
            return 'Wsl'
        }

        return 'Wsl'
    }

    function Get-NativeGatewayRecordId {
        param([string]$LocalDataDir)

        foreach ($fileName in @('native-gateway-install.json', 'native-gateway-profile-owner.json')) {
            $ownership = Read-JsonFile (Join-Path $LocalDataDir $fileName)
            if (-not (Test-NativeOwnershipMatches -Ownership $ownership)) {
                continue
            }
            $recordId = [string](Get-JsonPropertyValue $ownership 'GatewayRecordId')
            if ($recordId -match '^[A-Za-z0-9_-]{1,128}$') {
                return $recordId
            }
        }

        return $null
    }

    function Test-NativeOwnershipMatches {
        param([object]$Ownership)

        if ($null -eq $Ownership) {
            return $false
        }

        $profileName = [string](Get-JsonPropertyValue $Ownership 'ProfileName')
        $taskName = [string](Get-JsonPropertyValue $Ownership 'TaskName')
        return [string]::Equals($profileName, (Get-NativeGatewayProfile), [StringComparison]::Ordinal) -and
            [string]::Equals($taskName, $GatewayTaskName, [StringComparison]::Ordinal)
    }

    function Resolve-NativeOpenClawCli {
        $managedPrefix = Join-Path (Resolve-LocalDataDir) 'native-cli'
        foreach ($name in @('openclaw.ps1', 'openclaw.cmd')) {
            $managedCandidate = Join-Path $managedPrefix $name
            if (Test-Path -LiteralPath $managedCandidate -PathType Leaf) {
                return $managedCandidate
            }
        }

        foreach ($name in @('openclaw.ps1', 'openclaw.cmd')) {
            $command = Get-Command $name -ErrorAction SilentlyContinue
            if ($command) {
                return $command.Source
            }
        }

        $prefixes = New-Object System.Collections.Generic.List[string]
        if (-not [string]::IsNullOrWhiteSpace($env:APPDATA)) {
            $prefixes.Add((Join-Path $env:APPDATA 'npm'))
        }
        $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
        if ($npm) {
            try {
                $npmPrefix = (& $npm.Source config get prefix 2>$null | Select-Object -First 1).Trim()
                if (-not [string]::IsNullOrWhiteSpace($npmPrefix)) {
                    $prefixes.Add($npmPrefix)
                }
            } catch {
                Write-GatewayLog "Could not query npm prefix while locating the native CLI: $($_.Exception.Message)"
            }
        }

        foreach ($prefix in $prefixes | Select-Object -Unique) {
            foreach ($name in @('openclaw.ps1', 'openclaw.cmd')) {
                $candidate = Join-Path $prefix $name
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    return $candidate
                }
            }
        }

        return $null
    }

    function Invoke-NativeOpenClawCli {
        param(
            [string]$CliPath,
            [string[]]$Arguments
        )

        $managedHome = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        $managedProfile = Get-NativeGatewayProfile
        $managedStateDir = Get-NativeGatewayStateDir
        $managedSelectors = [ordered]@{
            OPENCLAW_PROFILE = $managedProfile
            OPENCLAW_STATE_DIR = $managedStateDir
            OPENCLAW_CONFIG_PATH = Join-Path $managedStateDir 'openclaw.json'
            OPENCLAW_HOME = $managedHome
            OPENCLAW_WINDOWS_TASK_NAME = $GatewayTaskName
            OPENCLAW_GATEWAY_PORT = ''
            OPENCLAW_GATEWAY_URL = ''
            OPENCLAW_WRAPPER = ''
        }
        $savedSelectors = @{}
        foreach ($name in $managedSelectors.Keys) {
            $savedSelectors[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $managedSelectors[$name], 'Process')
        }

        try {
            $output = @(& $CliPath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
            return [pscustomobject]@{
                ExitCode = [int]$LASTEXITCODE
                Output = ($output -join [Environment]::NewLine)
            }
        } catch {
            return [pscustomobject]@{
                ExitCode = -1
                Output = "Native OpenClaw CLI invocation failed: $($_.Exception.Message)"
            }
        } finally {
            foreach ($name in $managedSelectors.Keys) {
                [Environment]::SetEnvironmentVariable($name, $savedSelectors[$name], 'Process')
            }
        }
    }

    function Get-NativeGatewayProfile {
        $profile = ($DistroName -replace '[^A-Za-z0-9._-]', '-').Trim('-')
        if ([string]::IsNullOrWhiteSpace($profile) -or
            [string]::Equals($profile, 'default', [StringComparison]::OrdinalIgnoreCase)) {
            return "companion-$GatewayPort"
        }
        return $profile
    }

    function Get-NativeGatewayStateDir {
        $userHome = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
        $profile = Get-NativeGatewayProfile
        return Join-Path $userHome ".openclaw-$profile"
    }

    function Test-GatewayScheduledTask {
        $schtasks = Join-Path $env:WINDIR 'System32\schtasks.exe'
        if (-not (Test-Path -LiteralPath $schtasks)) {
            return $false
        }

        & $schtasks /Query /TN $GatewayTaskName *> $null
        return $LASTEXITCODE -eq 0
    }

    function Get-NativeGatewayPort {
        param(
            [string]$DataDir,
            [string]$LocalDataDir
        )

        $ownership = Read-JsonFile (Join-Path $LocalDataDir 'native-gateway-install.json')
        $ownedPort = Get-JsonPropertyValue $ownership 'GatewayPort'
        $parsedOwnedPort = 0
        if ($null -ne $ownedPort -and [int]::TryParse([string]$ownedPort, [ref]$parsedOwnedPort)) {
            return $parsedOwnedPort
        }

        $configPath = Join-Path (Get-NativeGatewayStateDir) 'openclaw.json'
        $config = Read-JsonFile $configPath
        $gateway = Get-JsonPropertyValue $config 'gateway'
        $configuredPort = Get-JsonPropertyValue $gateway 'port'
        $parsedPort = 0
        if ($null -ne $configuredPort -and [int]::TryParse([string]$configuredPort, [ref]$parsedPort)) {
            return $parsedPort
        }

        # setup-state can describe the previous mode after an interrupted switch,
        # so it is only a legacy fallback behind native ownership/config state.
        $state = Read-JsonFile (Join-Path $LocalDataDir 'setup-state.json')
        $gatewayUrl = [string](Get-JsonPropertyValue $state 'GatewayUrl')
        if (-not [string]::IsNullOrWhiteSpace($gatewayUrl)) {
            try {
                $uri = [Uri]$gatewayUrl
                if ($uri.Port -gt 0) {
                    return [int]$uri.Port
                }
            } catch {
                Write-GatewayLog "Could not parse native gateway URL '$gatewayUrl': $($_.Exception.Message)"
            }
        }

        return $GatewayPort
    }

    function Get-NativeGatewayServiceFiles {
        $safeTaskName = $GatewayTaskName -replace '[<>:"/\\|?*\x00-\x1F]', '_'
        $startupDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
        $stateDir = Get-NativeGatewayStateDir
        return @(
            (Join-Path $startupDir "$safeTaskName.cmd"),
            (Join-Path $startupDir "$safeTaskName.vbs"),
            (Join-Path $stateDir 'gateway.cmd'),
            (Join-Path $stateDir 'gateway.vbs')
        )
    }

    function Get-NativeGatewayProcesses {
        param([int]$GatewayPort)

        if (-not (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
            throw 'Get-NetTCPConnection is unavailable; cannot verify native gateway shutdown.'
        }

        $connections = @(Get-NetTCPConnection -LocalPort $GatewayPort -State Listen -ErrorAction SilentlyContinue)
        $processes = New-Object System.Collections.ArrayList
        foreach ($connection in $connections) {
            $processId = [int]$connection.OwningProcess
            if ($processId -le 0) {
                continue
            }

            $process = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction SilentlyContinue
            $commandLine = [string](Get-JsonPropertyValue $process 'CommandLine')
            if ($commandLine -match '(?i)(openclaw.*gateway|gateway.*openclaw)') {
                [void]$processes.Add($process)
            }
        }

        return @($processes.ToArray())
    }

    function Assert-NativeGatewayRuntimeStopped {
        param([int]$GatewayPort)

        for ($attempt = 1; $attempt -le 10; $attempt++) {
            if (@(Get-NativeGatewayProcesses -GatewayPort $GatewayPort).Count -eq 0) {
                return
            }
            Start-Sleep -Milliseconds 250
        }

        throw "An OpenClaw gateway is still listening on port $GatewayPort, but process ownership cannot be proven. It was left untouched."
    }

    function Remove-NativeGatewayService {
        param([int]$GatewayPort)

        $cliUninstallSucceeded = $false
        $nativeCli = Resolve-NativeOpenClawCli
        if ($nativeCli) {
            $stop = Invoke-NativeOpenClawCli -CliPath $nativeCli -Arguments @('gateway', 'stop')
            Write-GatewayLog "Native gateway stop exited $($stop.ExitCode): $($stop.Output)"

            $uninstall = Invoke-NativeOpenClawCli -CliPath $nativeCli -Arguments @('gateway', 'uninstall')
            Write-GatewayLog "Native gateway uninstall exited $($uninstall.ExitCode): $($uninstall.Output)"
            $cliUninstallSucceeded = $uninstall.ExitCode -eq 0
        } else {
            Write-GatewayLog 'Native OpenClaw CLI was not found; using verified direct service cleanup.'
        }

        if (Test-GatewayScheduledTask) {
            $schtasks = Join-Path $env:WINDIR 'System32\schtasks.exe'
            & $schtasks /End /TN $GatewayTaskName *> $null
            $deleteOutput = & $schtasks /Delete /TN $GatewayTaskName /F 2>&1
            if ($LASTEXITCODE -ne 0 -and (Test-GatewayScheduledTask)) {
                throw "Failed to remove native gateway task '$GatewayTaskName': $deleteOutput"
            }
            Write-GatewayLog "Removed native gateway task '$GatewayTaskName'."
        } else {
            Write-GatewayLog "Native gateway task '$GatewayTaskName' is absent."
        }

        foreach ($path in @(Get-NativeGatewayServiceFiles)) {
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Remove-Item -LiteralPath $path -Force -ErrorAction Stop
                Write-GatewayLog "Removed native gateway service file '$path'."
            }
            if (Test-Path -LiteralPath $path) {
                throw "Failed to remove native gateway service file '$path'."
            }
        }

        Assert-NativeGatewayRuntimeStopped -GatewayPort $GatewayPort
        if (Test-GatewayScheduledTask) {
            throw "Native gateway task '$GatewayTaskName' still exists after cleanup."
        }
        if (-not $cliUninstallSucceeded) {
            Write-GatewayLog 'Native CLI uninstall was unavailable or failed; verified direct cleanup completed.'
        }
    }

    function Remove-JsonPath {
        param(
            [object]$Root,
            [string]$Path
        )

        $segments = $Path -split '\.'
        $current = $Root
        for ($index = 0; $index -lt $segments.Length - 1; $index++) {
            $property = $current.PSObject.Properties | Where-Object { $_.Name -ieq $segments[$index] } | Select-Object -First 1
            if ($null -eq $property -or $null -eq $property.Value) {
                return $false
            }
            $current = $property.Value
        }

        $leaf = $current.PSObject.Properties | Where-Object { $_.Name -ieq $segments[-1] } | Select-Object -First 1
        if ($null -eq $leaf) {
            return $false
        }

        $current.PSObject.Properties.Remove($leaf.Name)
        return $true
    }

    function Remove-NativeGatewayConfig {
        param([string]$LocalDataDir)

        $configPath = Join-Path (Get-NativeGatewayStateDir) 'openclaw.json'
        $managedPaths = @()
        $ownershipPath = Join-Path $LocalDataDir 'native-gateway-install.json'
        if (Test-Path -LiteralPath $ownershipPath -PathType Leaf) {
            $ownership = Read-JsonFile $ownershipPath
            if ($null -eq $ownership) {
                throw "Native gateway ownership marker exists but could not be read: $ownershipPath"
            }

            $persistedPaths = Get-JsonPropertyValue $ownership 'ManagedConfigPaths'
            if ($null -eq $persistedPaths) {
                throw "Native gateway ownership marker has no ManagedConfigPaths: $ownershipPath"
            }

            foreach ($path in @($persistedPaths)) {
                $pathText = [string]$path
                if ($pathText -notmatch '^[A-Za-z0-9._-]+$') {
                    throw "Native gateway ownership marker contains an invalid config path."
                }
                $managedPaths += $pathText
            }
        } else {
            # Legacy native installs predate the ownership marker and own this fixed set.
            $managedPaths = @(
                'gateway.mode',
                'gateway.port',
                'gateway.bind',
                'gateway.auth.mode',
                'gateway.auth.token',
                'gateway.reload.mode',
                'gateway.nodes.allowCommands',
                'plugins.entries.device-pair.enabled',
                'plugins.entries.device-pair.config.publicUrl'
            )
        }

        $nativeCli = Resolve-NativeOpenClawCli
        if ($nativeCli) {
            $cliCleanupSucceeded = $true
            foreach ($path in @($managedPaths | Select-Object -Unique)) {
                $unset = Invoke-NativeOpenClawCli -CliPath $nativeCli -Arguments @('config', 'unset', $path)
                $pathWasMissing = $unset.Output -match '(?i)(Config path not found|Nothing was changed)'
                if ($unset.ExitCode -ne 0 -and -not $pathWasMissing) {
                    $cliCleanupSucceeded = $false
                    Write-GatewayLog "Native config unset failed for '$path': $($unset.Output)"
                    break
                }
            }
            if ($cliCleanupSucceeded) {
                Write-GatewayLog 'Removed setup-managed native gateway configuration through the OpenClaw JSON5 writer.'
                return
            }
        }

        $profileOwnerPath = Join-Path $LocalDataDir 'native-gateway-profile-owner.json'
        $profileOwnership = Read-JsonFile $profileOwnerPath
        if (Test-NativeOwnershipMatches -Ownership $profileOwnership) {
            # Installer uninstall deletes this whole isolated profile below. Avoid
            # parsing OpenClaw's JSON5 config with PowerShell's strict JSON parser.
            Write-GatewayLog 'Native config cleanup deferred to app-owned profile removal.'
            return
        }

        $config = Read-JsonFile $configPath
        if ($null -eq $config) {
            if (Test-Path -LiteralPath $configPath -PathType Leaf) {
                throw "Native OpenClaw config exists but could not be read after CLI cleanup failed: $configPath"
            }
            Write-GatewayLog 'Native OpenClaw config was already absent.'
            return
        }

        $changed = $false
        foreach ($path in @($managedPaths | Select-Object -Unique)) {
            if (Remove-JsonPath -Root $config -Path $path) {
                $changed = $true
            }
        }

        if ($changed) {
            Write-JsonFileAtomic -Path $configPath -Value $config
            Write-GatewayLog 'Removed setup-managed native gateway configuration and credential.'
        } else {
            Write-GatewayLog 'No setup-managed native gateway configuration remained.'
        }
    }

    function Remove-WindowsGatewayArtifacts {
        $dataDir = Resolve-AppDataDir
        $localDataDir = Resolve-LocalDataDir
        $ownedNativeRecordId = Get-NativeGatewayRecordId -LocalDataDir $localDataDir

        Write-GatewayLog "Cleaning Windows-side local gateway artifacts. AppData='$dataDir'; LocalData='$localDataDir'."

        Remove-AutostartRegistryValue
        Remove-ScheduledStartupTask
        Remove-FileIfExists -Path (Join-Path $dataDir 'setup-state.json') -Label 'legacy setup-state.json'
        Remove-FileIfExists -Path (Join-Path $localDataDir 'setup-state.json') -Label 'setup-state.json'
        Remove-FileIfExists -Path (Join-Path $localDataDir 'run.marker') -Label 'run.marker'
        Remove-FileIfExists -Path (Join-Path $dataDir 'exec-policy.json') -Label 'exec-policy.json'
        Remove-KeepaliveMarker -LocalDataDir $localDataDir

        $registryCleanup = Remove-SetupManagedGatewayRecords `
            -DataDir $dataDir `
            -InstallMode $script:installedGatewayMode `
            -OwnedNativeRecordId $ownedNativeRecordId
        $profileOwnerPath = Join-Path $localDataDir 'native-gateway-profile-owner.json'
        $profileOwnership = Read-JsonFile $profileOwnerPath
        if (Test-NativeOwnershipMatches -Ownership $profileOwnership) {
            Remove-OwnedDirectoryStrict -Path (Get-NativeGatewayStateDir) -Label 'app-owned native gateway profile'
        }
        Remove-OwnedDirectoryStrict -Path (Join-Path $localDataDir 'native-cli') -Label 'app-owned native CLI'
        Remove-FileIfExists -Path (Join-Path $localDataDir 'native-gateway-install.json') -Label 'native gateway ownership marker'
        Remove-FileIfExists -Path $profileOwnerPath -Label 'native gateway profile ownership marker'
        if ($registryCleanup.HasExternalGateways) {
            Write-GatewayLog 'External gateway records remain; preserving root device tokens.'
        } else {
            Clear-RootDeviceTokenForRole -DataDir $dataDir -Role 'operator'
            Clear-RootDeviceTokenForRole -DataDir $dataDir -Role 'node'
        }

        Reset-OnboardingSettings -DataDir $dataDir -PreserveNodeSettings:($registryCleanup.RemainingCount -gt 0)
        Remove-DirectoryIfExists -Path (Join-Path $dataDir 'Logs') -Label 'AppData Logs'
        Remove-DirectoryIfExists -Path (Join-Path $localDataDir 'Logs') -Label 'LocalAppData Logs'
    }

    function Complete-GatewayCleanup {
        param([string]$Message)

        Remove-WindowsGatewayArtifacts
        Write-GatewayResult `
            -Succeeded $true `
            -ExitCode 0 `
            -Message $Message `
            -Details ([ordered]@{ artifactWarnings = @($script:cleanupWarnings) })
        Write-Host "OpenClaw local gateway removed successfully."
        exit 0
}

function Get-WslExePath {
    $candidates = @(
        (Join-Path $env:WINDIR 'Sysnative\wsl.exe'),
        (Join-Path $env:WINDIR 'System32\wsl.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command wsl.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

function Format-Arguments {
    param([string[]]$Arguments)

    return ($Arguments | ForEach-Object {
        if ($_ -match '\s') {
            '"' + ($_ -replace '"', '\"') + '"'
        } else {
            $_
        }
    }) -join ' '
}

function Invoke-Wsl {
    param([string[]]$Arguments)

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()

    try {
        $process = Start-Process `
            -FilePath $script:WslPath `
            -ArgumentList $Arguments `
            -WindowStyle Hidden `
            -Wait `
            -PassThru `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath -Raw -ErrorAction SilentlyContinue } else { '' }
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue } else { '' }
        $output = (($stdout, $stderr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine

        Write-GatewayLog ("wsl.exe {0} exited {1}.{2}{3}" -f (Format-Arguments $Arguments), $process.ExitCode, [Environment]::NewLine, $output)

        return [pscustomobject]@{
            ExitCode = [int]$process.ExitCode
            Output = $output
        }
    } finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Test-DistroNotFound {
    param([string]$Output)

    if ([string]::IsNullOrWhiteSpace($Output)) {
        return $false
    }

    return $Output -match 'WSL_E_DISTRO_NOT_FOUND' -or
        $Output -match 'There is no distribution with the supplied name' -or
        $Output -match 'The specified distribution.*(could not be found|not found)' -or
        $Output -match 'distribution.*not.*found'
}

function Test-WslUnavailable {
    param([string]$Output)

    if ([string]::IsNullOrWhiteSpace($Output)) {
        return $false
    }

    return $Output -match 'WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED' -or
        $Output -match '0x8007019e' -or
        $Output -match 'optional component is not enabled' -or
        $Output -match 'Windows Subsystem for Linux has not been enabled'
}

function Test-DistroListed {
    param([string]$Output)

    if ([string]::IsNullOrWhiteSpace($Output)) {
        return $false
    }

    $distros = ($Output -replace "`0", '') -split '\r?\n' | ForEach-Object { $_.Trim() }
    return $distros -contains $DistroName
}

function Remove-GatewayDirectory {
    param([Parameter(Mandatory = $true)][string]$LocalDataDir)

    $gatewayDirectory = Join-Path $LocalDataDir "wsl\$DistroName"

    if (-not (Test-Path -LiteralPath $gatewayDirectory)) {
        Write-GatewayLog "Gateway directory does not exist: $gatewayDirectory"
        return
    }

    $gatewayItem = Get-Item -LiteralPath $gatewayDirectory -Force -ErrorAction Stop
    if (($gatewayItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to recursively delete reparse point '$gatewayDirectory'."
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try {
            Remove-Item -LiteralPath $gatewayDirectory -Recurse -Force -ErrorAction Stop
            if (-not (Test-Path -LiteralPath $gatewayDirectory)) {
                Write-GatewayLog "Removed gateway directory: $gatewayDirectory"
                return
            }
        } catch {
            $lastError = $_.Exception.Message
            Write-GatewayLog "Attempt $attempt failed to remove gateway directory '$gatewayDirectory': $lastError"
        }

        Start-Sleep -Seconds 1
    }

    throw "Failed to remove gateway directory '$gatewayDirectory': $lastError"
}

try {
    Ensure-AppRoot
    Write-GatewayLog "Starting local gateway cleanup for $DistroName."

    $dataDir = Resolve-AppDataDir
    $localDataDir = Resolve-LocalDataDir
    $installMode = Get-InstalledGatewayMode -DataDir $dataDir -LocalDataDir $localDataDir
    # Installer uninstall is destructive for every app-owned local runtime, even
    # when a mode switch preserved the inactive runtime for later reuse.
    $script:installedGatewayMode = 'All'
    Write-GatewayLog "Detected installed gateway mode: $installMode."
    if ($installMode -eq 'NativeWindows') {
        $nativeGatewayPort = Get-NativeGatewayPort -DataDir $dataDir -LocalDataDir $localDataDir
        Remove-NativeGatewayService -GatewayPort $nativeGatewayPort
        Remove-NativeGatewayConfig -LocalDataDir $localDataDir
        Write-GatewayLog 'Local native Windows gateway removed; checking for preserved app-owned WSL data.'
    }

    $script:WslPath = Get-WslExePath
    if (-not $script:WslPath) {
        Write-GatewayLog 'wsl.exe was not found; removing stale gateway directory if present.'
        Remove-GatewayDirectory -LocalDataDir $localDataDir
        Complete-GatewayCleanup -Message 'wsl.exe was not found; no registered WSL gateway can be removed.'
    }

    $listResult = Invoke-Wsl -Arguments @('--list', '--quiet')
    if ($listResult.ExitCode -ne 0) {
        if ($installMode -eq 'NativeWindows' -and (Test-WslUnavailable $listResult.Output)) {
            Write-GatewayLog 'WSL is unavailable; no preserved app-owned distro can be registered.'
            Remove-GatewayDirectory -LocalDataDir $localDataDir
            Complete-GatewayCleanup -Message 'Local native Windows gateway removed.'
        }

        throw "Failed to list WSL distributions: $($listResult.Output)"
    }
    if ($listResult.ExitCode -eq 0 -and -not (Test-DistroListed $listResult.Output)) {
        Write-GatewayLog "WSL distro '$DistroName' is not registered; removing stale gateway directory if present."
        Remove-GatewayDirectory -LocalDataDir $localDataDir
        Complete-GatewayCleanup -Message "Local WSL gateway '$DistroName' was already unregistered."
    }

    $terminateResult = Invoke-Wsl -Arguments @('--terminate', $DistroName)
    if ($terminateResult.ExitCode -ne 0) {
        Write-GatewayLog "Ignoring terminate exit code $($terminateResult.ExitCode); unregister handles stopped or missing distros."
    }

    Start-Sleep -Seconds 2

    $unregisterResult = Invoke-Wsl -Arguments @('--unregister', $DistroName)
    if ($unregisterResult.ExitCode -ne 0 -and -not (Test-DistroNotFound $unregisterResult.Output)) {
        Write-GatewayResult `
            -Succeeded $false `
            -ExitCode $unregisterResult.ExitCode `
            -Message "Failed to unregister WSL distro '$DistroName'." `
            -Details $unregisterResult.Output
        exit $unregisterResult.ExitCode
    }

    if ($unregisterResult.ExitCode -ne 0) {
        Write-GatewayLog "Treating missing distro '$DistroName' as already removed."
    }

    Remove-GatewayDirectory -LocalDataDir $localDataDir

    Complete-GatewayCleanup -Message "Local WSL gateway '$DistroName' removed."
} catch {
    $message = $_.Exception.Message
    Write-GatewayLog "Local gateway cleanup failed: $message"
    try { "[$(Get-Date -Format 'o')] $message" | Out-File -LiteralPath $errorPath -Encoding UTF8 -Force } catch {}
    Write-GatewayResult -Succeeded $false -ExitCode 1 -Message $message
    Write-Warning $message
    exit 1
}
