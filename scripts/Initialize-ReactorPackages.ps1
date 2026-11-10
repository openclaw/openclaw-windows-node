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
    $release = 'https://github.com/microsoft/microsoft-ui-reactor/releases/download/v0.1.0-preview.17'
    foreach ($package in @(
        @{ Name = 'Microsoft.UI.Reactor.0.1.0-preview.17.nupkg'; Sha256 = '7A2BC9C416671F0F8A6B0E397FC6784A3DED7D605D7AD6AA52D14228BDE55A74' },
        @{ Name = 'Microsoft.UI.Reactor.Advanced.0.1.0-preview.17.nupkg'; Sha256 = '0DD50EB6F7D4F54EFF0C8A5372137BC0B6D0338615CFF5FA45103946B37715E3' }
    )) {
        Save-VerifiedReactorPackage -Uri "$release/$($package.Name)" -Sha256 $package.Sha256 `
            -Destination (Join-Path $Directory $package.Name)
    }
    Write-Host 'Official Reactor preview.17 packages verified.'
}

if ($MyInvocation.InvocationName -ne '.') {
    Initialize-ReactorPackages -Directory $DestinationDirectory
}
