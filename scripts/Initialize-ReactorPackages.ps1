[CmdletBinding()]
param(
    [string]$DestinationDirectory = (Join-Path $PSScriptRoot '..\artifacts\reactor-nuget-packages')
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
    # Microsoft Authenticode-signs the NuGet.org builds of these assemblies.
    $feed = 'https://www.nuget.org/api/v2/package'
    $version = '0.1.0-preview.18'
    foreach ($package in @(
        @{ Id = 'Microsoft.UI.Reactor'; Sha256 = 'A983B1BD4C4C5ADBBAF8EB93C8012A0A48CC32D5160FC24EEE2B9678B5D61F28' },
        @{ Id = 'Microsoft.UI.Reactor.Advanced'; Sha256 = '79879F02247D2AB436F6D4FA73E6C098393C9BA568F711E72E299D92B56B4008' }
    )) {
        $name = "$($package.Id).$version.nupkg"
        Save-VerifiedReactorPackage -Uri "$feed/$($package.Id)/$version" -Sha256 $package.Sha256 `
            -Destination (Join-Path $Directory $name)
    }
    Write-Host "Official Reactor $version NuGet packages verified."
}

if ($MyInvocation.InvocationName -ne '.') {
    Initialize-ReactorPackages -Directory $DestinationDirectory
}
