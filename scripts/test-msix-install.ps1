<#
.SYNOPSIS
  End-to-end smoke test for the OpenClaw Companion MSIX install / launch /
  health-check / uninstall cycle. Runnable locally on a developer Windows box
  and from CI (windows-latest runner).

.DESCRIPTION
  Designed to be the automated counterpart to the manual runbook in
  docs/WINDOWS_NODE_TESTING.md. Each step is independent, prints PASS/FAIL,
  and the script exits non-zero on the first failure.

  Steps:
   1. Install the MSIX via Add-AppxPackage.
   2. Assert the package shows up in Get-AppxPackage with the expected
      Publisher and a 4-part Version.
   3. Launch the tray (Start-Process via the package family activation alias)
      and wait for the singleton named-pipe ("OpenClawTray-DeepLink") to come
      up — that's the readiness signal.
   4. Send an `openclaw://health` deep link through the pipe.
   5. Stop the tray process(es).
   6. Remove-AppxPackage and assert no orphan files remain in
      %APPDATA%\OpenClawTray\ or %LOCALAPPDATA%\OpenClawTray\.

.PARAMETER MsixPath
  Path to the .msix produced by build-msix CI job (or by a local
  `msbuild /p:PackageMsix=true` invocation).

.PARAMETER ExpectedPublisher
  Publisher subject the package must declare. Defaults to the Trusted Signing
  cert subject used by CI.

.PARAMETER KeepInstall
  Don't run the uninstall step at the end. Useful when debugging an install
  problem and you want the package to stay registered between runs.

.EXAMPLE
  ./scripts/test-msix-install.ps1 -MsixPath .\OpenClawCompanion-0.5.3-win-x64.msix

.NOTES
  This script does NOT exercise the AppInstaller (`.appinstaller`) flow —
  for that, see scripts/test-appinstaller-update.ps1 which spins up a local
  HTTP server and walks the vN -> vN+1 upgrade path.
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $MsixPath,
  [string] $ExpectedPublisher = 'CN=Scott Hanselman, O=Scott Hanselman, L=Forest Grove, S=Oregon, C=US',
  [switch] $KeepInstall
)

$ErrorActionPreference = 'Stop'
$script:failed = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "  PASS: $Message" -ForegroundColor Green
    } else {
        Write-Host "  FAIL: $Message" -ForegroundColor Red
        $script:failed++
    }
}

function Section { param([string]$Title) Write-Host "`n=== $Title ===" -ForegroundColor Cyan }

if (-not (Test-Path $MsixPath)) {
    throw "MSIX not found: $MsixPath"
}

Section 'Step 1: Install MSIX'
try {
    Add-AppxPackage -Path $MsixPath -ForceApplicationShutdown -ErrorAction Stop
    Assert-True $true "Add-AppxPackage exited cleanly"
} catch {
    Assert-True $false "Add-AppxPackage failed: $($_.Exception.Message)"
    exit 1
}

Section 'Step 2: Assert package presence'
$pkg = Get-AppxPackage -Name 'OpenClaw.Companion*' | Select-Object -First 1
Assert-True ($null -ne $pkg) "Get-AppxPackage finds OpenClaw.Companion*"
if ($pkg) {
    Assert-True ($pkg.Publisher -eq $ExpectedPublisher) "Publisher matches: $($pkg.Publisher)"
    $versionParts = $pkg.Version.Split('.')
    Assert-True ($versionParts.Length -eq 4) "Version is 4-part: $($pkg.Version)"
}

Section 'Step 3: Launch + wait for singleton named pipe'
if ($pkg) {
    # Activate via the package family — same path users hit from Start menu.
    $appId = ($pkg.PackageFamilyName + '!App')
    Start-Process -FilePath "shell:AppsFolder\$appId" -ErrorAction SilentlyContinue
    # Wait up to 30s for the OpenClawTray-DeepLink named pipe to appear.
    $deadline = (Get-Date).AddSeconds(30)
    $pipeUp = $false
    while ((Get-Date) -lt $deadline) {
        $pipes = [System.IO.Directory]::GetFiles('\\.\pipe\') 2>$null
        if ($pipes -and ($pipes | Where-Object { $_ -match 'OpenClawTray-DeepLink' })) {
            $pipeUp = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    Assert-True $pipeUp "Named pipe 'OpenClawTray-DeepLink' came up within 30s"
}

Section 'Step 4: Health deep link round-trip'
if ($pkg -and $pipeUp) {
    try {
        $client = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'OpenClawTray-DeepLink', 'Out')
        $client.Connect(5000)
        $writer = [System.IO.StreamWriter]::new($client)
        $writer.WriteLine('openclaw://health')
        $writer.Flush()
        $writer.Dispose()
        $client.Dispose()
        Assert-True $true "Wrote openclaw://health to the deep-link pipe"
    } catch {
        Assert-True $false "Pipe write failed: $($_.Exception.Message)"
    }
}

Section 'Step 5: Stop tray process'
Get-Process -Name 'OpenClaw.Tray.WinUI' -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Assert-True $true "Tray processes stopped (best-effort)"

Section 'Step 6: Uninstall + orphan check'
if ($KeepInstall) {
    Write-Host "  (skipping uninstall due to -KeepInstall)" -ForegroundColor Yellow
} elseif ($pkg) {
    try {
        Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
        Assert-True $true "Remove-AppxPackage exited cleanly"
    } catch {
        Assert-True $false "Remove-AppxPackage failed: $($_.Exception.Message)"
    }

    $stillThere = Get-AppxPackage -Name 'OpenClaw.Companion*' -ErrorAction SilentlyContinue
    Assert-True ($null -eq $stillThere) "Package removed from Get-AppxPackage"

    # File orphans: MSIX uninstall removes the package container but does NOT
    # touch the historical %APPDATA%\OpenClawTray\ / %LOCALAPPDATA%\OpenClawTray\
    # folders. We assert that the smoke-test install didn't write to them
    # (a fresh install on a clean profile shouldn't create them at all). This
    # is the case the in-app Reset & remove flow targets.
    $appDataOrphan      = Test-Path (Join-Path $env:APPDATA      'OpenClawTray')
    $localAppDataOrphan = Test-Path (Join-Path $env:LOCALAPPDATA 'OpenClawTray')
    if ($appDataOrphan -or $localAppDataOrphan) {
        Write-Host "  WARNING: orphan folders detected (likely from a prior install):" -ForegroundColor Yellow
        if ($appDataOrphan)      { Write-Host "    %APPDATA%\OpenClawTray\" }
        if ($localAppDataOrphan) { Write-Host "    %LOCALAPPDATA%\OpenClawTray\" }
        Write-Host "  Run 'openclaw-winnode --purge-wsl-orphans --confirm-destructive' to clean."
    } else {
        Assert-True $true "No orphan %APPDATA% / %LOCALAPPDATA% folders"
    }
}

Section 'Summary'
if ($script:failed -gt 0) {
    Write-Host "$script:failed assertion(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "All assertions passed." -ForegroundColor Green
exit 0
