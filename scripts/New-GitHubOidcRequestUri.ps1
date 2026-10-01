[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [uri]$RequestUri,

    [Parameter(Mandatory)]
    [string]$Audience
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RequestUri.IsAbsoluteUri) {
    throw 'The GitHub OIDC request URI must be absolute.'
}
if ([string]::IsNullOrWhiteSpace($Audience)) {
    throw 'The GitHub OIDC audience must not be empty.'
}

$requestText = $RequestUri.AbsoluteUri
$separator = if ($requestText.Contains('?')) {
    if ($requestText.EndsWith('?') -or $requestText.EndsWith('&')) { '' } else { '&' }
}
else {
    '?'
}

"${requestText}${separator}audience=$([Uri]::EscapeDataString($Audience))"
