<#
.SYNOPSIS
  Renders installer/openclaw-companion.appinstaller.template into a release-ready
  AppInstaller XML by substituting the {{TOKEN}} placeholders.

.DESCRIPTION
  Used by .github/workflows/ci.yml during the release job. Also runnable
  locally to validate template renders before tagging a release.

  The rendered AppInstaller XML must validate against the AppInstaller schema
  (https://schemas.microsoft.com/appx/appinstaller/2018). We assert via XML
  load rather than schema validation because the schema isn't shipped with the
  Windows SDK on most runners.

.PARAMETER Version
  4-part version string (e.g. "0.5.3.0"). Must match the MSIX <Identity Version=…>.

.PARAMETER Publisher
  Publisher subject from the MSIX manifest, with quoting preserved. Example:
  "CN=Scott Hanselman, O=Scott Hanselman, L=Forest Grove, S=Oregon, C=US"

.PARAMETER ProcessorArchitecture
  MSIX processor architecture for this AppInstaller file. Must be x64 or arm64.

.PARAMETER IdentityName
  MSIX package identity for the MainPackage element. Stable releases use
  OpenClaw.Companion. Channel-specific releases must pass the patched package
  identity so AppInstaller never crosses channels.

.PARAMETER MsixUri
  Absolute https:// URL of the matching architecture .msix release asset.

.PARAMETER WindowsAppRuntimeUri
  Absolute https:// URL of the matching architecture Microsoft.WindowsAppRuntime.2
  framework .msix release asset. AppInstaller installs this dependency when it
  is missing, avoiding a launch-time runtime acquisition prompt.

.PARAMETER AppInstallerUri
  Absolute https:// URL of THIS rendered .appinstaller file on the stable
  channel (e.g. https://raw.githubusercontent.com/openclaw/openclaw-windows-node/master/installer/appinstaller/openclaw-x64.appinstaller).
  Embedded inside the AppInstaller so Windows AppInstaller knows where to poll.

.PARAMETER OutputPath
  Destination path for the rendered .appinstaller file.

.PARAMETER AllowHttpForLocalTest
  Allows http:// loopback URIs for local AppInstaller smoke tests. Production
  release rendering must omit this switch and use https:// URLs.

.EXAMPLE
  ./scripts/render-appinstaller.ps1 `
    -Version 0.5.3.0 `
    -Publisher 'CN=Scott Hanselman, O=Scott Hanselman, L=Forest Grove, S=Oregon, C=US' `
    -IdentityName OpenClaw.Companion `
    -ProcessorArchitecture x64 `
    -MsixUri https://github.com/.../v0.5.3/OpenClawCompanion-0.5.3-win-x64.msix `
    -WindowsAppRuntimeUri https://github.com/.../v0.5.3/Microsoft.WindowsAppRuntime.2-2.0.1.0-win-x64.msix `
    -AppInstallerUri https://raw.githubusercontent.com/openclaw/openclaw-windows-node/master/installer/appinstaller/openclaw-x64.appinstaller `
    -OutputPath OpenClawCompanion-0.5.3-win-x64.appinstaller
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $Publisher,
  [string] $IdentityName = 'OpenClaw.Companion',
  [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string] $ProcessorArchitecture,
  [Parameter(Mandatory)] [string] $MsixUri,
  [Parameter(Mandatory)] [string] $WindowsAppRuntimeUri,
  [Parameter(Mandatory)] [string] $AppInstallerUri,
  [Parameter(Mandatory)] [string] $OutputPath,
  [switch] $AllowHttpForLocalTest
)

$ErrorActionPreference = 'Stop'

# Validate version is 4-part. AppInstaller silently accepts 1-3 part versions
# but Windows AppInstaller's update detector compares them as 4-part, so a
# 3-part value produces "no update available" forever.
$parts = $Version.Split('.')
if ($parts.Length -ne 4) {
  throw "Version must be 4-part (X.Y.Z.W). Got: '$Version'"
}
foreach ($p in $parts) {
  if (-not [int]::TryParse($p, [ref]([int]0))) {
    throw "Version segment '$p' is not an integer."
  }
}

if ([string]::IsNullOrWhiteSpace($IdentityName)) {
  throw "IdentityName must not be empty."
}

# Validate URIs are absolute https:// for production. Local smoke tests may use
# http://127.0.0.1 with -AllowHttpForLocalTest.
foreach ($pair in @(
    @{ Name = 'MsixUri';         Value = $MsixUri },
    @{ Name = 'WindowsAppRuntimeUri'; Value = $WindowsAppRuntimeUri },
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
# regex-metacharacter surprises from values like a publisher subject that
# contains literal commas/quotes or a URI with a percent-encoded character.
$rendered = $template
$rendered = $rendered.Replace('{{VERSION}}',                $Version)
$rendered = $rendered.Replace('{{PUBLISHER}}',              $Publisher)
$rendered = $rendered.Replace('{{IDENTITY_NAME}}',          $IdentityName)
$rendered = $rendered.Replace('{{PROCESSOR_ARCHITECTURE}}', $ProcessorArchitecture)
$rendered = $rendered.Replace('{{MSIX_URI}}',               $MsixUri)
$rendered = $rendered.Replace('{{WINDOWS_APP_RUNTIME_URI}}', $WindowsAppRuntimeUri)
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
$dependency = $xml.AppInstaller.Dependencies.Package | Where-Object { $_.Name -eq 'Microsoft.WindowsAppRuntime.2' } | Select-Object -First 1
if ($null -eq $dependency) {
  throw "Rendered XML must contain a Microsoft.WindowsAppRuntime.2 dependency package."
}
if ($dependency.Publisher -ne 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US') {
  throw "Rendered XML has Windows App Runtime Publisher '$($dependency.Publisher)'."
}
if ($dependency.Version -ne '2.0.1.0') {
  throw "Rendered XML has Windows App Runtime Version '$($dependency.Version)' but expected '2.0.1.0'."
}
if ($dependency.ProcessorArchitecture -ne $ProcessorArchitecture) {
  throw "Rendered XML has Windows App Runtime ProcessorArchitecture '$($dependency.ProcessorArchitecture)' but expected '$ProcessorArchitecture'."
}
if ($dependency.Uri -ne $WindowsAppRuntimeUri) {
  throw "Rendered XML has Windows App Runtime Uri '$($dependency.Uri)' but expected '$WindowsAppRuntimeUri'."
}

$outDir = Split-Path -Parent $OutputPath
if ($outDir -and -not (Test-Path $outDir)) {
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
Set-Content -Path $OutputPath -Value $rendered -Encoding UTF8

Write-Host "Rendered AppInstaller: $OutputPath"
Write-Host "  Version:         $Version"
Write-Host "  Publisher:       $Publisher"
Write-Host "  Identity:        $IdentityName"
Write-Host "  Architecture:    $ProcessorArchitecture"
Write-Host "  MSIX URI:        $MsixUri"
Write-Host "  Windows App Runtime URI: $WindowsAppRuntimeUri"
Write-Host "  AppInstaller URI: $AppInstallerUri"
