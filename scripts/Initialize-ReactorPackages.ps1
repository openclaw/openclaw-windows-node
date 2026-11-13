[CmdletBinding()]
param(
    [string]$DestinationDirectory = (Join-Path $PSScriptRoot '..\artifacts\reactor-packages')
)

$ErrorActionPreference = 'Stop'

function Save-VerifiedReactorPackage {
    param([string]$Uri, [string]$Sha256, [string]$Destination)

    if (Test-Path -LiteralPath $Destination) {
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) {
            throw "Cached Reactor package digest mismatch: $Destination"
        }
        return
    }

    $temporary = $Destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Invoke-WebRequest -Uri $Uri -OutFile $temporary -TimeoutSec 120 -UseBasicParsing
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $Sha256) {
            throw "Downloaded Reactor package digest mismatch: $Uri"
        }
        # A concurrent initializer may have completed the same verified download.
        if (Test-Path -LiteralPath $Destination) {
            if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Sha256) {
                throw "Concurrent Reactor package digest mismatch: $Destination"
            }
        } else {
            [IO.File]::Move($temporary, $Destination)
        }
    } finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Initialize-ReactorPackages {
    param([string]$Directory)

    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    $release = 'https://github.com/microsoft/microsoft-ui-reactor/releases/download/v0.1.0-preview.18'
    foreach ($package in @(
        @{ Name = 'Microsoft.UI.Reactor.0.1.0-preview.18.nupkg'; Sha256 = '4E7EE4646EFF4E6F69F2C827A481F190F9DEAA1C5022055BD7F5E7532094D588' },
        @{ Name = 'Microsoft.UI.Reactor.Advanced.0.1.0-preview.18.nupkg'; Sha256 = 'F9E6CC4030768B1FAE8DBC35C1DDEFF8BA0E2992C83C5AA211D12518E8E3EF6D' }
    )) {
        Save-VerifiedReactorPackage -Uri "$release/$($package.Name)" -Sha256 $package.Sha256 `
            -Destination (Join-Path $Directory $package.Name)
    }
    Write-Host 'Official Reactor preview.18 packages verified.'
}

if ($MyInvocation.InvocationName -ne '.') {
    Initialize-ReactorPackages -Directory $DestinationDirectory
}
