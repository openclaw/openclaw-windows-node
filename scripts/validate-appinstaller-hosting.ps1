<#
.SYNOPSIS
  Validates hosted AppInstaller and MSIX URLs before promoting a release.

.DESCRIPTION
  Windows AppInstaller is strict about hosted metadata and package assets. This
  script checks the stable .appinstaller URL, parses its MainPackage URI when
  -MsixUri is not provided, then validates the MSIX endpoint. It is intended for
  release operators before promoting openclaw-x64.appinstaller or
  openclaw-arm64.appinstaller to the stable feed location.

.PARAMETER AppInstallerUri
  Stable hosted .appinstaller URL, e.g.
  https://raw.githubusercontent.com/openclaw/openclaw-windows-node/master/installer/appinstaller/openclaw-x64.appinstaller.

.PARAMETER MsixUri
  Optional MSIX URL. When omitted, the script fetches AppInstallerUri and reads
  the MainPackage Uri attribute.

.PARAMETER AppInstallerPath
  Optional local .appinstaller file to parse instead of fetching AppInstallerUri.
  This is used by the feed-update PR workflow before the rendered file has been
  merged into the stable raw GitHub location.

.PARAMETER AllowGitHubContentTypes
  Candidate-mode compatibility switch for GitHub-hosted release assets. GitHub
  release downloads currently serve MSIX files as application/octet-stream. This
  switch keeps strict validation as the default while allowing two-version E2E
  testing to prove whether Windows AppInstaller accepts GitHub's headers.

.EXAMPLE
  ./scripts/validate-appinstaller-hosting.ps1 `
    -AppInstallerUri https://raw.githubusercontent.com/openclaw/openclaw-windows-node/master/installer/appinstaller/openclaw-x64.appinstaller
#>

[CmdletBinding()]
param(
  [Uri] $AppInstallerUri,
  [string] $AppInstallerPath,
  [Uri] $MsixUri,
  [switch] $AllowGitHubContentTypes
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
    [Parameter(Mandatory)] [string] $Expected,
    [string[]] $AlsoAllowed = @()
  )

  $contentType = Get-HeaderValue -Response $Response -Name 'Content-Type'
  $allowed = @($Expected) + $AlsoAllowed
  foreach ($candidate in $allowed) {
    if (-not [string]::IsNullOrWhiteSpace($contentType) -and
        $contentType.StartsWith($candidate, [StringComparison]::OrdinalIgnoreCase)) {
      Write-Host "  Content-Type OK: $contentType"
      return
    }
  }

  if ([string]::IsNullOrWhiteSpace($contentType) -or
      $allowed.Count -eq 1) {
    throw "$Uri returned Content-Type '$contentType'; expected '$Expected'."
  }
  throw "$Uri returned Content-Type '$contentType'; expected one of: $($allowed -join ', ')."
}

function Get-MainPackageUri {
  param([Parameter(Mandatory)] [xml] $AppInstallerXml)

  $namespaceManager = [System.Xml.XmlNamespaceManager]::new($AppInstallerXml.NameTable)
  $namespaceManager.AddNamespace('ai', 'http://schemas.microsoft.com/appx/appinstaller/2018')
  $mainPackage = $AppInstallerXml.SelectSingleNode('/ai:AppInstaller/ai:MainPackage', $namespaceManager)
  if ($null -eq $mainPackage) {
    $mainPackage = $AppInstallerXml.SelectSingleNode('/AppInstaller/MainPackage')
  }

  $mainPackageUri = if ($null -eq $mainPackage) { $null } else { $mainPackage.GetAttribute('Uri') }
  if ([string]::IsNullOrWhiteSpace($mainPackageUri)) {
    throw "AppInstaller XML does not contain a MainPackage Uri."
  }

  return [Uri]$mainPackageUri
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

if ([string]::IsNullOrWhiteSpace($AppInstallerPath) -and $null -eq $AppInstallerUri) {
  throw "Provide either -AppInstallerUri or -AppInstallerPath."
}

if (-not [string]::IsNullOrWhiteSpace($AppInstallerPath)) {
  if (-not (Test-Path $AppInstallerPath)) {
    throw "AppInstallerPath not found: $AppInstallerPath"
  }

  Write-Host "Validating local AppInstaller XML: $AppInstallerPath"
  [xml]$appInstallerXml = Get-Content -Path $AppInstallerPath -Raw
  if ($null -eq $MsixUri) {
    $MsixUri = Get-MainPackageUri -AppInstallerXml $appInstallerXml
    Write-Host "Discovered MSIX URI from local AppInstaller: $MsixUri"
  }
}
else {
  Write-Host "Validating AppInstaller hosting: $AppInstallerUri"
  Assert-HttpsUri -Uri $AppInstallerUri -Description 'AppInstallerUri'
  $appInstallerHead = Invoke-Head -Uri $AppInstallerUri
  $allowedAppInstallerTypes = if ($AllowGitHubContentTypes -and
      $AppInstallerUri.Host.Equals('raw.githubusercontent.com', [StringComparison]::OrdinalIgnoreCase)) {
    @('text/plain')
  } else {
    @()
  }
  Assert-ContentType -Response $appInstallerHead -Uri $AppInstallerUri -Expected 'application/appinstaller' -AlsoAllowed $allowedAppInstallerTypes
  if (-not ($AllowGitHubContentTypes -and
      $AppInstallerUri.Host.Equals('raw.githubusercontent.com', [StringComparison]::OrdinalIgnoreCase))) {
    Assert-ContentLength -Response $appInstallerHead -Uri $AppInstallerUri
  }

  if ($null -eq $MsixUri) {
    $appInstallerBody = Invoke-WebRequest -Uri $AppInstallerUri -Method Get -MaximumRedirection 5 -UseBasicParsing
    [xml]$appInstallerXml = $appInstallerBody.Content
    $MsixUri = Get-MainPackageUri -AppInstallerXml $appInstallerXml
    Write-Host "Discovered MSIX URI from AppInstaller: $MsixUri"
  }
}

Write-Host "Validating MSIX hosting: $MsixUri"
Assert-HttpsUri -Uri $MsixUri -Description 'MsixUri'
$msixHead = Invoke-Head -Uri $MsixUri
$allowedMsixTypes = if ($AllowGitHubContentTypes -and
    $MsixUri.Host.Equals('github.com', [StringComparison]::OrdinalIgnoreCase)) {
  @('application/octet-stream')
} else {
  @()
}
Assert-ContentType -Response $msixHead -Uri $MsixUri -Expected 'application/msix' -AlsoAllowed $allowedMsixTypes
Assert-ContentLength -Response $msixHead -Uri $MsixUri
Assert-MsixRangeRequest -Uri $MsixUri

Write-Host "AppInstaller hosting validation passed." -ForegroundColor Green
