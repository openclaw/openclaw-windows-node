<#
.SYNOPSIS
  Renders installer/openclaw-companion.appinstaller.template into a release-ready
  AppInstaller XML by substituting the {{TOKEN}} placeholders.

.DESCRIPTION
  Used by .github/workflows/appinstaller-feed-pr.yml after a stable release tag
  to regenerate installer/appinstaller/openclaw-{x64,arm64}.appinstaller from
  the signed MSIX release assets. Also runnable locally to validate template
  renders before tagging a release.

  The rendered AppInstaller XML must validate against the AppInstaller schema
  (http://schemas.microsoft.com/appx/appinstaller/2018). We assert via XML
  load rather than schema validation because the schema isn't shipped with the
  Windows SDK on most runners.

  The OpenClaw Companion MSIX is built with WindowsAppSDKSelfContained=true,
  so the rendered AppInstaller intentionally has NO <Dependencies> block.
  Windows does not need to fetch a separate WindowsAppRuntime package.

.PARAMETER Version
  4-part version string (e.g. "0.5.3.0"). Must match the MSIX <Identity Version=…>.
  Windows AppInstaller's update detector compares versions as 4-part values, so
  a 1-3 part value produces "no update available" forever even though it parses.

.PARAMETER Publisher
  Publisher subject from the MSIX manifest. Must match the signing cert
  Subject DN exactly. Example:
    "CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US"

.PARAMETER ProcessorArchitecture
  MSIX processor architecture for this AppInstaller file. Must be x64 or arm64.

.PARAMETER IdentityName
  MSIX package identity for the MainPackage element. Stable releases use
  OpenClaw.Companion.

.PARAMETER MsixUri
  Absolute https:// URL of the matching architecture .msix release asset.

.PARAMETER AppInstallerUri
  Absolute https:// URL of THIS rendered .appinstaller file on the stable
  channel (e.g.
  https://raw.githubusercontent.com/openclaw/openclaw-windows-node/main/installer/appinstaller/openclaw-x64.appinstaller).
  Embedded inside the AppInstaller so Windows AppInstaller knows where to poll
  for future updates.

.PARAMETER OutputPath
  Destination path for the rendered .appinstaller file.

.PARAMETER AllowHttpForLocalTest
  Allows http:// loopback URIs for local AppInstaller smoke tests. Production
  release rendering must omit this switch and use https:// URLs.

.EXAMPLE
  ./scripts/render-appinstaller.ps1 `
    -Version 0.5.3.0 `
    -Publisher 'CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US' `
    -IdentityName OpenClaw.Companion `
    -ProcessorArchitecture x64 `
    -MsixUri https://github.com/openclaw/openclaw-windows-node/releases/download/v0.5.3/OpenClawCompanion-0.5.3-win-x64.msix `
    -AppInstallerUri https://raw.githubusercontent.com/openclaw/openclaw-windows-node/main/installer/appinstaller/openclaw-x64.appinstaller `
    -OutputPath installer/appinstaller/openclaw-x64.appinstaller
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $Publisher,
  [string] $IdentityName = 'OpenClaw.Companion',
  [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string] $ProcessorArchitecture,
  [Parameter(Mandatory)] [string] $MsixUri,
  [Parameter(Mandatory)] [string] $AppInstallerUri,
  [Parameter(Mandatory)] [string] $OutputPath,
  [switch] $AllowHttpForLocalTest
)

$ErrorActionPreference = 'Stop'

$parts = $Version.Split('.')
if ($parts.Length -ne 4) {
  throw "Version must be 4-part (X.Y.Z.W). Got: '$Version'"
}
foreach ($p in $parts) {
  $parsed = 0
  if (-not [int]::TryParse($p, [ref]$parsed)) {
    throw "Version segment '$p' is not an integer."
  }
}

if ([string]::IsNullOrWhiteSpace($IdentityName)) {
  throw "IdentityName must not be empty."
}

foreach ($pair in @(
    @{ Name = 'MsixUri';         Value = $MsixUri },
    @{ Name = 'AppInstallerUri'; Value = $AppInstallerUri }
  )) {
  $u = $null
  if (-not [Uri]::TryCreate($pair.Value, 'Absolute', [ref]$u)) {
    throw "$($pair.Name) must be an absolute URL. Got: '$($pair.Value)'"
  }

  $isAllowedHttpLoopback = $AllowHttpForLocalTest -and $u.Scheme -eq 'http' -and $u.IsLoopback
  if ($u.Scheme -ne 'https' -and -not $isAllowedHttpLoopback) {
    throw "$($pair.Name) must be an absolute https:// URL. Got: '$($pair.Value)'"
  }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$templatePath = Join-Path $repoRoot 'installer\openclaw-companion.appinstaller.template'
if (-not (Test-Path $templatePath)) {
  throw "Template not found: $templatePath"
}

$template = Get-Content $templatePath -Raw

# Simple string substitution — inputs are not regex patterns and we don't want
# regex-metacharacter surprises from values like a publisher subject with
# literal commas/quotes or a URI with percent-encoded characters.
$rendered = $template
$rendered = $rendered.Replace('{{VERSION}}',                $Version)
$rendered = $rendered.Replace('{{PUBLISHER}}',              $Publisher)
$rendered = $rendered.Replace('{{IDENTITY_NAME}}',          $IdentityName)
$rendered = $rendered.Replace('{{PROCESSOR_ARCHITECTURE}}', $ProcessorArchitecture)
$rendered = $rendered.Replace('{{MSIX_URI}}',               $MsixUri)
$rendered = $rendered.Replace('{{APPINSTALLER_URI}}',       $AppInstallerUri)
if ($rendered -match '\{\{[A-Z0-9_]+\}\}') {
  throw "Rendered XML still contains unresolved template token(s): $($Matches[0])"
}

# Validate the rendered XML parses. A bad template / bad substitution surfaces
# here instead of at deploy time when Windows refuses to install.
[xml]$xml = $rendered
if ($xml.AppInstaller.Version -ne $Version) {
  throw "Rendered XML has Version '$($xml.AppInstaller.Version)' but expected '$Version'. Substitution failure."
}
$mainPackage = $xml.AppInstaller.MainPackage
if ($null -eq $mainPackage) {
  throw "Rendered XML must contain exactly one MainPackage element."
}
if ($mainPackage.Publisher -ne $Publisher) {
  throw "Rendered XML has Publisher '$($mainPackage.Publisher)' but expected '$Publisher'."
}
if ($mainPackage.Name -ne $IdentityName) {
  throw "Rendered XML has MainPackage Name '$($mainPackage.Name)' but expected '$IdentityName'."
}
if ($mainPackage.Version -ne $Version) {
  throw "Rendered XML has MainPackage Version '$($mainPackage.Version)' but expected '$Version'."
}
if ($mainPackage.ProcessorArchitecture -ne $ProcessorArchitecture) {
  throw "Rendered XML has ProcessorArchitecture '$($mainPackage.ProcessorArchitecture)' but expected '$ProcessorArchitecture'."
}
if ($mainPackage.Uri -ne $MsixUri) {
  throw "Rendered XML has package Uri '$($mainPackage.Uri)' but expected '$MsixUri'."
}

# We do not emit a <Dependencies> block (WindowsAppSDKSelfContained=true), so
# guard against accidentally re-introducing one in the template.
if ($null -ne $xml.AppInstaller.Dependencies) {
  throw "Rendered XML must not contain a <Dependencies> block (MSIX is self-contained)."
}

$outDir = Split-Path -Parent $OutputPath
if ($outDir -and -not (Test-Path $outDir)) {
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
Set-Content -Path $OutputPath -Value $rendered -Encoding UTF8

Write-Host "Rendered AppInstaller: $OutputPath"
Write-Host "  Version:          $Version"
Write-Host "  Publisher:        $Publisher"
Write-Host "  Identity:         $IdentityName"
Write-Host "  Architecture:     $ProcessorArchitecture"
Write-Host "  MSIX URI:         $MsixUri"
Write-Host "  AppInstaller URI: $AppInstallerUri"
