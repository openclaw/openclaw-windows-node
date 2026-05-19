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

.PARAMETER MsixX64Uri
  Absolute https:// URL of the x64 .msix release asset.

.PARAMETER MsixArm64Uri
  Absolute https:// URL of the arm64 .msix release asset.

.PARAMETER AppInstallerUri
  Absolute https:// URL of THIS rendered .appinstaller file on the stable
  channel (e.g. https://openclaw.github.io/openclaw-windows-node/latest.appinstaller).
  Embedded inside the AppInstaller so Windows AppInstaller knows where to poll.

.PARAMETER OutputPath
  Destination path for the rendered .appinstaller file.

.EXAMPLE
  ./scripts/render-appinstaller.ps1 `
    -Version 0.5.3.0 `
    -Publisher 'CN=Scott Hanselman, O=Scott Hanselman, L=Forest Grove, S=Oregon, C=US' `
    -MsixX64Uri https://github.com/.../v0.5.3/OpenClawCompanion-0.5.3-win-x64.msix `
    -MsixArm64Uri https://github.com/.../v0.5.3/OpenClawCompanion-0.5.3-win-arm64.msix `
    -AppInstallerUri https://openclaw.github.io/openclaw-windows-node/latest.appinstaller `
    -OutputPath OpenClawCompanion-0.5.3.appinstaller
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $Publisher,
  [Parameter(Mandatory)] [string] $MsixX64Uri,
  [Parameter(Mandatory)] [string] $MsixArm64Uri,
  [Parameter(Mandatory)] [string] $AppInstallerUri,
  [Parameter(Mandatory)] [string] $OutputPath
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

# Validate URIs are absolute https://. AppInstaller refuses to poll http:// in
# Windows 11 23H2+ (security policy) and a relative URL crashes the renderer.
foreach ($pair in @(
    @{ Name = 'MsixX64Uri';      Value = $MsixX64Uri },
    @{ Name = 'MsixArm64Uri';    Value = $MsixArm64Uri },
    @{ Name = 'AppInstallerUri'; Value = $AppInstallerUri }
  )) {
  $u = $null
  if (-not [Uri]::TryCreate($pair.Value, 'Absolute', [ref]$u) -or $u.Scheme -ne 'https') {
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
$rendered = $rendered.Replace('{{VERSION}}',          $Version)
$rendered = $rendered.Replace('{{PUBLISHER}}',        $Publisher)
$rendered = $rendered.Replace('{{MSIX_X64_URI}}',     $MsixX64Uri)
$rendered = $rendered.Replace('{{MSIX_ARM64_URI}}',   $MsixArm64Uri)
$rendered = $rendered.Replace('{{APPINSTALLER_URI}}', $AppInstallerUri)

# Validate the rendered XML parses. A bad template / bad substitution surfaces
# here instead of at deploy time when Windows refuses to install.
[xml]$xml = $rendered
if ($xml.AppInstaller.Version -ne $Version) {
  throw "Rendered XML has Version '$($xml.AppInstaller.Version)' but expected '$Version'. Substitution failure."
}
if ($xml.AppInstaller.MainBundle.Publisher -ne $Publisher) {
  throw "Rendered XML has Publisher '$($xml.AppInstaller.MainBundle.Publisher)' but expected '$Publisher'."
}

$outDir = Split-Path -Parent $OutputPath
if ($outDir -and -not (Test-Path $outDir)) {
  New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
Set-Content -Path $OutputPath -Value $rendered -Encoding UTF8

Write-Host "Rendered AppInstaller: $OutputPath"
Write-Host "  Version:         $Version"
Write-Host "  Publisher:       $Publisher"
Write-Host "  MSIX x64 URI:    $MsixX64Uri"
Write-Host "  MSIX ARM64 URI:  $MsixArm64Uri"
Write-Host "  AppInstaller URI: $AppInstallerUri"
