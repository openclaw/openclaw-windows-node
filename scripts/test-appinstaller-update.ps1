<#
.SYNOPSIS
  Simulates a non-Store .appinstaller upgrade by hosting two MSIX versions
  on a local HTTP server and walking the install vN -> publish vN+1 ->
  trigger upgrade flow end-to-end.

.DESCRIPTION
  The point of this script is to catch regressions in the .appinstaller
  XML and the PackageManager.AddPackageByAppInstallerFileAsync wiring
  *without* needing a real GitHub release / stable feed PR cycle. Run this
  before a release tag goes out; if it fails, the same failure will happen
  to every user that installs from the stable architecture-specific AppInstaller URL.

  Steps:
   1. Launch a tiny HTTP server (HttpListener) on localhost:8765 that serves
      the two MSIX files + a rendered .appinstaller pointing at vN+1.
   2. Render an "old" .appinstaller pointing at vN, install it (this records
      the source URL with Windows AppInstaller).
   3. Re-render the .appinstaller in place pointing at vN+1.
   4. Invoke PackageManager.AddPackageByAppInstallerFileAsync against the
      local URL — this is the same call the in-app "Check for updates"
      button makes.
   5. Assert Get-AppxPackage reports the new Version.
   6. Tear down.

.PARAMETER MsixVnPath
  Path to the "older" .msix (used as the seed install).

.PARAMETER MsixVn1Path
  Path to the "newer" .msix (used as the upgrade target).

.PARAMETER VnVersion
  4-part version of the older .msix (e.g. 0.5.3.0).

.PARAMETER Vn1Version
  4-part version of the newer .msix (e.g. 0.5.4.0).

.PARAMETER Publisher
  Publisher subject that must match BOTH MSIX manifests.

.EXAMPLE
  ./scripts/test-appinstaller-update.ps1 `
    -MsixVnPath  .\OpenClawCompanion-0.5.3-win-x64.msix -VnVersion  0.5.3.0 `
    -MsixVn1Path .\OpenClawCompanion-0.5.4-win-x64.msix -Vn1Version 0.5.4.0
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $MsixVnPath,
  [Parameter(Mandatory)] [string] $VnVersion,
  [Parameter(Mandatory)] [string] $MsixVn1Path,
  [Parameter(Mandatory)] [string] $Vn1Version,
  [string] $Publisher = 'CN=Scott Hanselman, O=Scott Hanselman, L=Forest Grove, S=Oregon, C=US',
  [int]    $Port = 8765
)

$ErrorActionPreference = 'Stop'

foreach ($p in @($MsixVnPath, $MsixVn1Path)) {
  if (-not (Test-Path $p)) { throw "MSIX not found: $p" }
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "openclaw-appinstaller-test-$(Get-Random)"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
  Copy-Item $MsixVnPath  (Join-Path $tmp 'vN.msix')
  Copy-Item $MsixVn1Path (Join-Path $tmp 'vNplus1.msix')

  $baseUri = "http://127.0.0.1:$Port"
  $repoRoot = Split-Path -Parent $PSScriptRoot

  function Render-AppInstaller {
    param([string]$Version, [string]$MsixFileName, [string]$OutputPath)
    & "$repoRoot\scripts\render-appinstaller.ps1" `
      -Version $Version `
      -Publisher $Publisher `
      -ProcessorArchitecture x64 `
      -MsixUri "$baseUri/$MsixFileName" `
      -AppInstallerUri "$baseUri/openclaw.appinstaller" `
      -OutputPath $OutputPath `
      -AllowHttpForLocalTest
  }

  Render-AppInstaller -Version $VnVersion  -MsixFileName 'vN.msix'      -OutputPath (Join-Path $tmp 'openclaw.appinstaller')

  # Spin up exactly one HttpListener in a background job. Binding the same
  # prefix in both parent and job makes the smoke test fail before AppInstaller
  # is exercised.
  $listenerJob = Start-Job -ScriptBlock {
    param($prefix, $root)
    $l = [System.Net.HttpListener]::new()
    $l.Prefixes.Add("$prefix/")
    $l.Start()
    while ($l.IsListening) {
      $ctx = $l.GetContext()
      $name = [System.IO.Path]::GetFileName($ctx.Request.Url.LocalPath)
      $path = Join-Path $root $name
      if (Test-Path $path) {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        $ctx.Response.ContentType = if ($name.EndsWith('.appinstaller')) { 'application/appinstaller' } else { 'application/octet-stream' }
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
      } else {
        $ctx.Response.StatusCode = 404
      }
      $ctx.Response.Close()
    }
  } -ArgumentList $baseUri, $tmp

  $listenerReady = $false
  for ($i = 0; $i -lt 20; $i++) {
    if ($listenerJob.State -eq 'Failed') {
      Receive-Job $listenerJob -Keep | Out-String | Write-Error
      throw "AppInstaller test HTTP listener failed to start."
    }

    try {
      Invoke-WebRequest "$baseUri/openclaw.appinstaller" -UseBasicParsing -TimeoutSec 2 | Out-Null
      $listenerReady = $true
      break
    } catch {
      Start-Sleep -Milliseconds 250
    }
  }
  if (-not $listenerReady) {
    throw "AppInstaller test HTTP listener did not serve $baseUri/openclaw.appinstaller."
  }
  Write-Host "Listening on $baseUri/" -ForegroundColor Cyan

  try {
    # Step 2: install vN via the .appinstaller URL.
    Write-Host "Installing vN via $baseUri/openclaw.appinstaller ..." -ForegroundColor Cyan
    Add-AppxPackage -AppInstallerFile "$baseUri/openclaw.appinstaller" -ForceApplicationShutdown
    $pkg = Get-AppxPackage -Name 'OpenClaw.Companion*' | Select-Object -First 1
    if ($pkg.Version -ne $VnVersion) {
      throw "Expected vN install to land version $VnVersion, got $($pkg.Version)"
    }
    Write-Host "  vN installed: $($pkg.Version)" -ForegroundColor Green

    # Step 3: re-render the .appinstaller in place pointing at vN+1.
    Render-AppInstaller -Version $Vn1Version -MsixFileName 'vNplus1.msix' -OutputPath (Join-Path $tmp 'openclaw.appinstaller')

    # Step 4: trigger the in-app update path via PackageManager.
    Write-Host "Triggering upgrade to vN+1 via PackageManager.AddPackageByAppInstallerFileAsync ..." -ForegroundColor Cyan
    Add-Type -AssemblyName 'Windows.Management.Deployment.PackageManager, ContentType=WindowsRuntime'
    $pm = [Windows.Management.Deployment.PackageManager,Windows.Management.Deployment,ContentType=WindowsRuntime]::new()
    $op = $pm.AddPackageByAppInstallerFileAsync(
        [Uri]"$baseUri/openclaw.appinstaller",
        [Windows.Management.Deployment.AddPackageByAppInstallerOptions]::None,
        $pm.GetDefaultPackageVolume())
    $result = $op.AsTask().GetAwaiter().GetResult()
    if (-not $result.IsRegistered) {
      throw "Upgrade failed: $($result.ErrorText) (HRESULT 0x$('{0:X8}' -f $result.ExtendedErrorCode.HResult))"
    }
    Write-Host "  PackageManager reported IsRegistered=$($result.IsRegistered)" -ForegroundColor Green

    # Step 5: assert.
    $pkg = Get-AppxPackage -Name 'OpenClaw.Companion*' | Select-Object -First 1
    if ($pkg.Version -ne $Vn1Version) {
      throw "Expected upgrade to land version $Vn1Version, got $($pkg.Version)"
    }
    Write-Host "vN+1 verified at $($pkg.Version)" -ForegroundColor Green

    Write-Host "`nAppInstaller upgrade simulation: PASS" -ForegroundColor Green
  }
  finally {
    if ($listenerJob) { Stop-Job $listenerJob -ErrorAction SilentlyContinue; Remove-Job $listenerJob -Force -ErrorAction SilentlyContinue }
  }
}
finally {
  if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}
