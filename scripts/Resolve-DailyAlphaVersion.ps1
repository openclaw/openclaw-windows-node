<#
.SYNOPSIS
    Selects the Windows daily alpha version from GitVersion and the current
    Gateway stable release line.

.DESCRIPTION
    Keeps GitVersion's canonical alpha while Windows is on the same or a newer
    release line. When the Gateway latest stable release is newer, starts that
    Windows alpha train at alpha.1. The selector never moves Windows backwards.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GitVersionSemVer,

    [Parameter(Mandatory)]
    [string]$GatewayTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-ReleaseLine {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Pattern
    )

    $match = [regex]::Match($Value, $Pattern)
    if (-not $match.Success) {
        throw "$Label '$Value' has an unsupported format."
    }

    return @(
        [long]$match.Groups["year"].Value,
        [long]$match.Groups["month"].Value,
        [long]$match.Groups["patch"].Value
    )
}

function Compare-ReleaseLine {
    param(
        [Parameter(Mandatory)][object[]]$Left,
        [Parameter(Mandatory)][object[]]$Right
    )

    for ($index = 0; $index -lt 3; $index++) {
        if ($Left[$index] -lt $Right[$index]) { return -1 }
        if ($Left[$index] -gt $Right[$index]) { return 1 }
    }

    return 0
}

$windowsLine = ConvertTo-ReleaseLine `
    -Value $GitVersionSemVer `
    -Label "GitVersion alpha" `
    -Pattern '^(?<year>0|[1-9]\d*)\.(?<month>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)-alpha\.(?:0|[1-9]\d*)$'
$gatewayLine = ConvertTo-ReleaseLine `
    -Value $GatewayTag `
    -Label "Gateway release tag" `
    -Pattern '^v(?<year>0|[1-9]\d*)\.(?<month>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-[1-9]\d*)?$'

if ((Compare-ReleaseLine -Left $gatewayLine -Right $windowsLine) -le 0) {
    return $GitVersionSemVer
}

return "$($gatewayLine[0]).$($gatewayLine[1]).$($gatewayLine[2])-alpha.1"
