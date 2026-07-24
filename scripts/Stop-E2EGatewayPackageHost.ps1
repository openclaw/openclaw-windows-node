<#
.SYNOPSIS
    Removes the exact disposable WSL package-host distro created for E2E.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern("^OpenClawE2EPackageHost-[A-Za-z0-9-]+$")][string]$DistroName,
    [Parameter(Mandatory = $true)][string]$InstallLocation,
    [Parameter(Mandatory = $true)][ValidatePattern("^[A-Za-z0-9._-]+$")][string]$OwnershipToken
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-RegisteredWslBasePath {
    param([Parameter(Mandatory = $true)][string]$DistributionName)

    $registrations = @(
        Get-ChildItem -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss" -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath } |
            Where-Object { $_.DistributionName -eq $DistributionName }
    )
    if ($registrations.Count -ne 1 -or [string]::IsNullOrWhiteSpace($registrations[0].BasePath)) {
        throw "Could not prove the registered base path for WSL distro '$DistributionName'."
    }

    $basePath = [Environment]::ExpandEnvironmentVariables([string]$registrations[0].BasePath)
    if ($basePath.StartsWith("\\?\", [StringComparison]::Ordinal)) {
        $basePath = $basePath.Substring(4)
    }
    return [System.IO.Path]::GetFullPath($basePath).TrimEnd("\")
}

$installLocationFull = [System.IO.Path]::GetFullPath($InstallLocation)
if ((Split-Path -Leaf $installLocationFull) -ne $DistroName) {
    throw "Install location leaf must match the disposable distro name '$DistroName'."
}

$existingDistros = @(& wsl.exe --list --quiet) -replace "`0", "" | ForEach-Object { $_.Trim() }
if ($LASTEXITCODE -ne 0) {
    throw "Failed to enumerate existing WSL distros (exit $LASTEXITCODE); no cleanup was attempted."
}
$isRegistered = $existingDistros -contains $DistroName
if (-not (Test-Path -LiteralPath $installLocationFull)) {
    if ($isRegistered) {
        throw "Refusing to remove registered distro '$DistroName' without its ownership marker."
    }
    Write-Host "Gateway E2E package-host state already absent: $DistroName"
    exit 0
}

$markerPath = Join-Path $installLocationFull ".openclaw-e2e-owner.json"
if (-not (Test-Path -LiteralPath $markerPath)) {
    throw "Refusing to clean install location without ownership marker: '$installLocationFull'."
}
$marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($marker.PSObject.Properties.Name -notcontains "ownership_token" -or
    $marker.PSObject.Properties.Name -notcontains "registration_proven" -or
    $marker.PSObject.Properties.Name -notcontains "registered_base_path") {
    throw "Refusing to clean install location with an invalid ownership marker."
}
if ([string]$marker.ownership_token -ne $OwnershipToken) {
    throw "Refusing to clean install location owned by a different workflow attempt."
}

if ($isRegistered) {
    $registeredBasePath = Get-RegisteredWslBasePath -DistributionName $DistroName
    if (-not [string]::Equals($registeredBasePath, $installLocationFull.TrimEnd("\"), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to unregister '$DistroName': its registered base path does not match the owned install location."
    }
    & wsl.exe --terminate $DistroName
    if ($LASTEXITCODE -ne 0) { throw "Failed to terminate package-host distro '$DistroName'." }
    & wsl.exe --unregister $DistroName
    if ($LASTEXITCODE -ne 0) { throw "Failed to unregister package-host distro '$DistroName'." }

    $remainingDistros = @(& wsl.exe --list --quiet) -replace "`0", "" | ForEach-Object { $_.Trim() }
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to verify WSL state after unregistering '$DistroName'; install storage was preserved."
    }
    if ($remainingDistros -contains $DistroName) {
        throw "WSL distro '$DistroName' is still registered; install storage was preserved."
    }
} else {
    $recordedBasePath = if ($null -eq $marker.registered_base_path) {
        ""
    } else {
        [System.IO.Path]::GetFullPath([string]$marker.registered_base_path).TrimEnd("\")
    }
    if (-not [bool]$marker.registration_proven -or
        -not [string]::Equals($recordedBasePath, $installLocationFull.TrimEnd("\"), [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning "Preserving unregistered install location because no durable BasePath proof exists: '$installLocationFull'."
        exit 0
    }
}

Remove-Item -LiteralPath $installLocationFull -Recurse -Force
Write-Host "Gateway E2E package-host state removed: $DistroName"
