<#
.SYNOPSIS
  Validates hosted AppInstaller and MSIX URLs before promoting a release.

.DESCRIPTION
  Windows AppInstaller is strict about hosted metadata and package assets. This
  script checks the stable .appinstaller URL, parses its MainPackage URI when
  -MsixUri is not provided, then validates the MSIX endpoint. It is intended for
  release operators before copying openclaw-x64.appinstaller or
  openclaw-arm64.appinstaller to the stable hosting branch/location.

.PARAMETER AppInstallerUri
  Stable hosted .appinstaller URL, e.g.
  https://openclaw.github.io/openclaw-windows-node/openclaw-x64.appinstaller.

.PARAMETER MsixUri
  Optional MSIX URL. When omitted, the script fetches AppInstallerUri and reads
  the MainPackage Uri attribute.

.EXAMPLE
  ./scripts/validate-appinstaller-hosting.ps1 `
    -AppInstallerUri https://openclaw.github.io/openclaw-windows-node/openclaw-x64.appinstaller
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory)] [Uri] $AppInstallerUri,
  [Uri] $MsixUri
)

$ErrorActionPreference = 'Stop'

function Get-HeaderValue {
  param(
    [Parameter(Mandatory)] $Response,
    [Parameter(Mandatory)] [string] $Name
  )

  $value = $Response.Headers[$Name]
  if ($value -is [array]) { return $value[0] }
  return $value
}

function Invoke-Head {
  param([Parameter(Mandatory)] [Uri] $Uri)

  try {
    return Invoke-WebRequest -Uri $Uri -Method Head -MaximumRedirection 5 -UseBasicParsing
  }
  catch {
    throw "HEAD $Uri failed: $($_.Exception.Message)"
  }
}

function Assert-HttpsUri {
  param(
    [Parameter(Mandatory)] [Uri] $Uri,
    [Parameter(Mandatory)] [string] $Description
  )

  if ($Uri.Scheme -ne 'https') {
    throw "$Description must use https: $Uri"
  }
}

function Assert-ContentType {
  param(
    [Parameter(Mandatory)] $Response,
    [Parameter(Mandatory)] [Uri] $Uri,
    [Parameter(Mandatory)] [string] $Expected
  )

  $contentType = Get-HeaderValue -Response $Response -Name 'Content-Type'
  if ([string]::IsNullOrWhiteSpace($contentType) -or
      -not $contentType.StartsWith($Expected, [StringComparison]::OrdinalIgnoreCase)) {
    throw "$Uri returned Content-Type '$contentType'; expected '$Expected'."
  }
  Write-Host "  Content-Type OK: $contentType"
}

function Assert-ContentLength {
  param(
    [Parameter(Mandatory)] $Response,
    [Parameter(Mandatory)] [Uri] $Uri
  )

  $contentLength = Get-HeaderValue -Response $Response -Name 'Content-Length'
  if ([string]::IsNullOrWhiteSpace($contentLength)) {
    throw "$Uri did not return Content-Length."
  }
  $parsedContentLength = 0L
  if (-not [long]::TryParse($contentLength, [ref]$parsedContentLength)) {
    throw "$Uri returned non-numeric Content-Length '$contentLength'."
  }
  Write-Host "  Content-Length OK: $contentLength"
}

function Assert-MsixRangeRequest {
  param([Parameter(Mandatory)] [Uri] $Uri)

  try {
    $response = Invoke-WebRequest -Uri $Uri `
      -Method Get `
      -Headers @{ Range = 'bytes=0-0' } `
      -MaximumRedirection 5 `
      -UseBasicParsing
  }
  catch {
    throw "Range GET $Uri failed: $($_.Exception.Message)"
  }

  if ($response.StatusCode -ne 206) {
    throw "$Uri did not honor range request. Expected HTTP 206, got HTTP $($response.StatusCode)."
  }

  $contentRange = Get-HeaderValue -Response $response -Name 'Content-Range'
  if ([string]::IsNullOrWhiteSpace($contentRange)) {
    throw "$Uri returned HTTP 206 but omitted Content-Range."
  }
  Write-Host "  Range request OK: $contentRange"
}

Write-Host "Validating AppInstaller hosting: $AppInstallerUri"
Assert-HttpsUri -Uri $AppInstallerUri -Description 'AppInstallerUri'
$appInstallerHead = Invoke-Head -Uri $AppInstallerUri
Assert-ContentType -Response $appInstallerHead -Uri $AppInstallerUri -Expected 'application/appinstaller'
Assert-ContentLength -Response $appInstallerHead -Uri $AppInstallerUri

if ($null -eq $MsixUri) {
  $appInstallerBody = Invoke-WebRequest -Uri $AppInstallerUri -Method Get -MaximumRedirection 5 -UseBasicParsing
  [xml]$appInstallerXml = $appInstallerBody.Content
  $namespaceManager = [System.Xml.XmlNamespaceManager]::new($appInstallerXml.NameTable)
  $namespaceManager.AddNamespace('ai', 'http://schemas.microsoft.com/appx/appinstaller/2018')
  $mainPackage = $appInstallerXml.SelectSingleNode('/ai:AppInstaller/ai:MainPackage', $namespaceManager)
  if ($null -eq $mainPackage) {
    $mainPackage = $appInstallerXml.SelectSingleNode('/AppInstaller/MainPackage')
  }

  $mainPackageUri = if ($null -eq $mainPackage) { $null } else { $mainPackage.GetAttribute('Uri') }
  if ([string]::IsNullOrWhiteSpace($mainPackageUri)) {
    throw "$AppInstallerUri does not contain a MainPackage Uri."
  }
  $MsixUri = [Uri]$mainPackageUri
  Write-Host "Discovered MSIX URI from AppInstaller: $MsixUri"
}

Write-Host "Validating MSIX hosting: $MsixUri"
Assert-HttpsUri -Uri $MsixUri -Description 'MsixUri'
$msixHead = Invoke-Head -Uri $MsixUri
Assert-ContentType -Response $msixHead -Uri $MsixUri -Expected 'application/msix'
Assert-ContentLength -Response $msixHead -Uri $MsixUri
Assert-MsixRangeRequest -Uri $MsixUri

Write-Host "AppInstaller hosting validation passed." -ForegroundColor Green
