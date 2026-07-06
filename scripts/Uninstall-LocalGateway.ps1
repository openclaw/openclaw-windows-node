<#
.SYNOPSIS
    Removes the OpenClaw local WSL gateway during app uninstall.

.DESCRIPTION
    This helper is launched by the Inno uninstaller after the user chooses to
    remove the local gateway. It deliberately calls WSL directly instead of
    launching OpenClaw binaries from the install directory, so the app payload is
    not kept loaded while Inno removes installed files.
#>

[CmdletBinding()]
param(
    [string]$AppRoot = $PSScriptRoot,
    [string]$DataDirectoryName = 'OpenClawTray',
    [string]$AutoStartName = 'OpenClawTray',
    [string]$StartupTaskName = 'OpenClaw Companion',
    [string]$DistroName = 'OpenClawGateway'
)

$ErrorActionPreference = 'Stop'

$resultPath = Join-Path $AppRoot 'uninstall-gateway-result.json'
$errorPath = Join-Path $AppRoot 'uninstall-gateway-error.log'
$wslLogPath = Join-Path $AppRoot 'uninstall-gateway-wsl.log'
$cleanupWarnings = New-Object 'System.Collections.Generic.List[string]'

if ($DataDirectoryName -notmatch '^[A-Za-z0-9._-]+$') {
    throw "Invalid data directory name '$DataDirectoryName'."
}
if ($DistroName -notmatch '^[A-Za-z0-9._-]+$') {
    throw "Invalid WSL distro name '$DistroName'."
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
            $host = $uri.Host.ToLowerInvariant()
            return $host -eq 'localhost' -or $host -eq '127.0.0.1' -or $host -eq '::1' -or $host -eq '[::1]'
        } catch {
            return $false
        }
    }

    function Test-SetupManagedLocalRecord {
        param([object]$Record)

        $isLocal = [bool](Get-JsonPropertyValue $Record 'isLocal')
        $sshTunnel = Get-JsonPropertyValue $Record 'sshTunnel'
        if (-not $isLocal -or $null -ne $sshTunnel) {
            return $false
        }

        $setupManagedDistroName = [string](Get-JsonPropertyValue $Record 'setupManagedDistroName')
        if ([string]::Equals($setupManagedDistroName, $DistroName, [StringComparison]::Ordinal)) {
            return $true
        }

        if (-not [string]::IsNullOrWhiteSpace($setupManagedDistroName)) {
            return $false
        }

        $friendlyName = [string](Get-JsonPropertyValue $Record 'friendlyName')
        $url = [string](Get-JsonPropertyValue $Record 'url')
        return [string]::Equals($friendlyName, "Local ($DistroName)", [StringComparison]::Ordinal) -and (Test-LocalGatewayUrl $url)
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
        param([string]$DataDir)

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
            if (Test-SetupManagedLocalRecord $record) {
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

    function Remove-WindowsGatewayArtifacts {
        $dataDir = Resolve-AppDataDir
        $localDataDir = Resolve-LocalDataDir

        Write-GatewayLog "Cleaning Windows-side local gateway artifacts. AppData='$dataDir'; LocalData='$localDataDir'."

        Remove-AutostartRegistryValue
        Remove-ScheduledStartupTask
        Remove-FileIfExists -Path (Join-Path $dataDir 'setup-state.json') -Label 'legacy setup-state.json'
        Remove-FileIfExists -Path (Join-Path $localDataDir 'setup-state.json') -Label 'setup-state.json'
        Remove-FileIfExists -Path (Join-Path $localDataDir 'run.marker') -Label 'run.marker'
        Remove-FileIfExists -Path (Join-Path $dataDir 'exec-policy.json') -Label 'exec-policy.json'
        Remove-KeepaliveMarker -LocalDataDir $localDataDir

        $registryCleanup = Remove-SetupManagedGatewayRecords -DataDir $dataDir
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
        Write-Host "OpenClaw local WSL gateway removed successfully."
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

function Test-DistroListed {
    param([string]$Output)

    if ([string]::IsNullOrWhiteSpace($Output)) {
        return $false
    }

    $distros = ($Output -replace "`0", '') -split '\r?\n' | ForEach-Object { $_.Trim() }
    return $distros -contains $DistroName
}

function Remove-GatewayDirectory {
    $gatewayDirectory = Join-Path $AppRoot "wsl\$DistroName"

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

    $script:WslPath = Get-WslExePath
    if (-not $script:WslPath) {
        Write-GatewayLog 'wsl.exe was not found; removing stale gateway directory if present.'
        Remove-GatewayDirectory
        Complete-GatewayCleanup -Message 'wsl.exe was not found; no registered WSL gateway can be removed.'
    }

    $listResult = Invoke-Wsl -Arguments @('--list', '--quiet')
    if ($listResult.ExitCode -eq 0 -and -not (Test-DistroListed $listResult.Output)) {
        Write-GatewayLog "WSL distro '$DistroName' is not registered; removing stale gateway directory if present."
        Remove-GatewayDirectory
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

    Remove-GatewayDirectory

    Complete-GatewayCleanup -Message "Local WSL gateway '$DistroName' removed."
} catch {
    $message = $_.Exception.Message
    Write-GatewayLog "Local gateway cleanup failed: $message"
    try { "[$(Get-Date -Format 'o')] $message" | Out-File -LiteralPath $errorPath -Encoding UTF8 -Force } catch {}
    Write-GatewayResult -Succeeded $false -ExitCode 1 -Message $message
    Write-Warning $message
    exit 1
}
