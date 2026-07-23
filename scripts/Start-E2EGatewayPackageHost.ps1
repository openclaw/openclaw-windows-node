<#
.SYNOPSIS
    Hosts an immutable OpenClaw package inside a disposable WSL distro.

.DESCRIPTION
    Verifies the package SHA-256, creates a named Ubuntu distro, copies the
    package into that distro, and starts a loopback-free HTTP server reachable
    by the separate app-owned distro provisioned by the Windows E2E fixture.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][ValidatePattern("^[a-fA-F0-9]{64}$")][string]$ExpectedSha256,
    [Parameter(Mandatory = $true)][ValidatePattern("^OpenClawE2EPackageHost-[A-Za-z0-9-]+$")][string]$DistroName,
    [Parameter(Mandatory = $true)][string]$InstallLocation,
    [Parameter(Mandatory = $true)][ValidatePattern("^[A-Za-z0-9._-]+$")][string]$OwnershipToken,
    [Parameter(Mandatory = $true)][string]$GitHubOutput,
    [ValidateRange(1024, 65535)][int]$Port = 38677
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

function Write-OwnershipMarker {
    param(
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][bool]$RegistrationProven,
        [AllowNull()][string]$RegisteredBasePath
    )

    $temporaryMarkerPath = "$MarkerPath.tmp"
    [ordered]@{
        ownership_token = $Token
        registration_proven = $RegistrationProven
        registered_base_path = $RegisteredBasePath
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath $temporaryMarkerPath -NoNewline
    Move-Item -LiteralPath $temporaryMarkerPath -Destination $MarkerPath -Force
}

function Read-OwnershipMarker {
    param(
        [Parameter(Mandatory = $true)][string]$MarkerPath,
        [Parameter(Mandatory = $true)][string]$Token
    )

    if (-not (Test-Path -LiteralPath $MarkerPath)) {
        throw "Ownership marker is missing: '$MarkerPath'."
    }
    $marker = Get-Content -LiteralPath $MarkerPath -Raw | ConvertFrom-Json
    if ($marker.PSObject.Properties.Name -notcontains "ownership_token" -or
        $marker.PSObject.Properties.Name -notcontains "registration_proven" -or
        $marker.PSObject.Properties.Name -notcontains "registered_base_path") {
        throw "Ownership marker is invalid: '$MarkerPath'."
    }
    if ([string]$marker.ownership_token -ne $Token) {
        throw "Ownership marker belongs to a different workflow attempt."
    }
    return $marker
}

$package = Get-Item -LiteralPath $PackagePath
$actualSha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $ExpectedSha256.ToLowerInvariant()) {
    throw "Gateway composed package SHA-256 mismatch: expected $ExpectedSha256, got $actualSha256."
}

$existingDistros = @(& wsl.exe --list --quiet) -replace "`0", "" | ForEach-Object { $_.Trim() }
if ($LASTEXITCODE -ne 0) {
    throw "Failed to enumerate existing WSL distros (exit $LASTEXITCODE)."
}
if ($existingDistros -contains $DistroName) {
    throw "Refusing to reuse existing WSL distro '$DistroName'."
}

$installLocationFull = [System.IO.Path]::GetFullPath($InstallLocation)
if ((Split-Path -Leaf $installLocationFull) -ne $DistroName) {
    throw "Install location leaf must match the disposable distro name '$DistroName'."
}
$installParent = Split-Path -Parent $installLocationFull
if (-not (Test-Path -LiteralPath $installParent -PathType Container)) {
    throw "Install location parent does not exist: '$installParent'."
}
if (Test-Path -LiteralPath $installLocationFull) {
    throw "Refusing to reuse existing install location '$installLocationFull'."
}
$preRegistrationMarkerPath = Join-Path $installParent ".$DistroName.openclaw-e2e-owner.json"
if (Test-Path -LiteralPath $preRegistrationMarkerPath) {
    throw "Refusing to reuse existing pre-registration marker '$preRegistrationMarkerPath'."
}

$preRegistrationMarkerCreated = $false
$installAttempted = $false
$installSucceeded = $false
try {
    Write-OwnershipMarker -MarkerPath $preRegistrationMarkerPath -Token $OwnershipToken -RegistrationProven $false -RegisteredBasePath $null
    $preRegistrationMarkerCreated = $true
    $installAttempted = $true
    & wsl.exe --install --distribution Ubuntu-24.04 --name $DistroName --location $installLocationFull --no-launch --web-download
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create package-host distro '$DistroName' (exit $LASTEXITCODE)."
    }
    $registeredBasePath = Get-RegisteredWslBasePath -DistributionName $DistroName
    if (-not [string]::Equals($registeredBasePath, $installLocationFull.TrimEnd("\"), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Registered base path for '$DistroName' does not match its requested install location."
    }
    $markerPath = Join-Path $installLocationFull ".openclaw-e2e-owner.json"
    Write-OwnershipMarker -MarkerPath $markerPath -Token $OwnershipToken -RegistrationProven $true -RegisteredBasePath $registeredBasePath
    Remove-Item -LiteralPath $preRegistrationMarkerPath -Force
    $preRegistrationMarkerCreated = $false
    $installSucceeded = $true

    $drive = $package.FullName.Substring(0, 1).ToLowerInvariant()
    $tail = $package.FullName.Substring(2).Replace("\", "/")
    $mountedPackagePath = "/mnt/$drive$tail"
    $hostDirectory = "/tmp/openclaw-e2e-package-host"
    $hostPackageName = "openclaw-composed-$actualSha256.tgz"
    $hostPackagePath = "$hostDirectory/$hostPackageName"

    & wsl.exe -d $DistroName -u root -- mkdir -p $hostDirectory
    if ($LASTEXITCODE -ne 0) { throw "Failed to create package-host directory." }
    & wsl.exe -d $DistroName -u root -- cp -- $mountedPackagePath $hostPackagePath
    if ($LASTEXITCODE -ne 0) { throw "Failed to copy the composed gateway package into the package-host distro." }
    $hostHashOutput = [string](& wsl.exe -d $DistroName -u root -- sha256sum -- $hostPackagePath)
    if ($LASTEXITCODE -ne 0 -or $hostHashOutput -notmatch "^([a-fA-F0-9]{64})\s") {
        throw "Failed to verify the copied composed gateway package inside the package-host distro."
    }
    $hostSha256 = $Matches[1].ToLowerInvariant()
    if ($hostSha256 -ne $actualSha256) {
        throw "Copied composed gateway package SHA-256 mismatch: expected $actualSha256, got $hostSha256."
    }
    & wsl.exe -d $DistroName -u root -- chmod 0444 $hostPackagePath
    if ($LASTEXITCODE -ne 0) { throw "Failed to make the hosted composed gateway package read-only." }
    & wsl.exe -d $DistroName -u root -- chmod 0555 $hostDirectory
    if ($LASTEXITCODE -ne 0) { throw "Failed to make the composed gateway package host directory read-only." }

    $hostAddresses = @(
        ((& wsl.exe -d $DistroName -u root -- hostname -I) -split "\s+") |
            Where-Object {
                $address = $null
                [System.Net.IPAddress]::TryParse($_, [ref]$address) -and
                    $address.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork
            }
    )
    $hostAddress = $hostAddresses[0]
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($hostAddress)) {
        throw "Failed to resolve package-host distro address."
    }

    $serverCommand = "setsid -f python3 -m http.server $Port --bind 0.0.0.0 --directory $hostDirectory >/tmp/openclaw-e2e-package-host.log 2>&1"
    & wsl.exe -d $DistroName -u root -- bash -lc $serverCommand
    if ($LASTEXITCODE -ne 0) { throw "Failed to start the package-host HTTP server." }

    $packageSpec = "http://${hostAddress}:$Port/$hostPackageName"
    $readinessDeadline = [DateTime]::UtcNow.AddSeconds(30)
    $lastReadinessError = $null
    do {
        try {
            $response = Invoke-WebRequest -Uri $packageSpec -Method Head -TimeoutSec 5 -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                $lastReadinessError = $null
                break
            }
            $lastReadinessError = "HTTP $($response.StatusCode)"
        } catch {
            $lastReadinessError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $readinessDeadline)
    if ($null -ne $lastReadinessError) {
        throw "Package-host readiness probe failed for the advertised URL: $lastReadinessError"
    }

    "package_spec=$packageSpec" | Out-File -FilePath $GitHubOutput -Encoding utf8 -Append
    "package_sha256=$actualSha256" | Out-File -FilePath $GitHubOutput -Encoding utf8 -Append
    "distro_name=$DistroName" | Out-File -FilePath $GitHubOutput -Encoding utf8 -Append
    Write-Host "Gateway E2E composed-package host ready: distro=$DistroName sha256=$actualSha256"
} catch {
    $startupError = $_
    $cleanupComplete = $false
    if ($installAttempted -and -not $installSucceeded) {
        try {
            $registeredDistros = @(& wsl.exe --list --quiet) -replace "`0", "" | ForEach-Object { $_.Trim() }
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to enumerate WSL state after the install failure."
            }
            $preRegistrationMarker = Read-OwnershipMarker -MarkerPath $preRegistrationMarkerPath -Token $OwnershipToken
            if ([bool]$preRegistrationMarker.registration_proven) {
                throw "Pre-registration marker unexpectedly claims completed registration."
            }
            if ($registeredDistros -notcontains $DistroName) {
                if (Test-Path -LiteralPath $installLocationFull) {
                    Remove-Item -LiteralPath $installLocationFull -Recurse -Force
                }
                Remove-Item -LiteralPath $preRegistrationMarkerPath -Force
                $preRegistrationMarkerCreated = $false
                $cleanupComplete = $true
            } else {
                $registeredBasePath = Get-RegisteredWslBasePath -DistributionName $DistroName
                if (-not [string]::Equals($registeredBasePath, $installLocationFull.TrimEnd("\"), [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Registered base path after the failed install does not match the owned install location."
                }
                $markerPath = Join-Path $installLocationFull ".openclaw-e2e-owner.json"
                Write-OwnershipMarker -MarkerPath $markerPath -Token $OwnershipToken -RegistrationProven $true -RegisteredBasePath $registeredBasePath
                Remove-Item -LiteralPath $preRegistrationMarkerPath -Force
                $preRegistrationMarkerCreated = $false
            }
        } catch {
            Write-Warning "Could not prove that pre-registration cleanup was safe: $($_.Exception.Message)"
        }
    }
    if ($installAttempted -and -not $cleanupComplete) {
        try {
            & (Join-Path $PSScriptRoot "Stop-E2EGatewayPackageHost.ps1") `
                -DistroName $DistroName `
                -InstallLocation $installLocationFull `
                -OwnershipToken $OwnershipToken
        } catch {
            Write-Warning "Package-host cleanup after failed startup also failed: $($_.Exception.Message)"
        }
    } elseif ($preRegistrationMarkerCreated) {
        Write-Warning "Preserving pre-registration marker because cleanup ownership could not be proven: '$preRegistrationMarkerPath'."
    }
    throw $startupError
}
