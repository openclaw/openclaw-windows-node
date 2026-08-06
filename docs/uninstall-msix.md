# Uninstalling the Current OpenClaw Companion MSIX Package

This document describes the optional MSIX package currently present in the
repository.  It does not define the future product lifecycle proposed in the
[MSIX lifecycle plan](MSIX_LIFECYCLE_PLAN.md).

## Current Package Behavior

The current package manifest does not declare an install, repair, or uninstall
action, so Windows does not run OpenClaw cleanup code when that package is
removed from Settings or with `Remove-AppxPackage`.

Windows documents a restricted
[`windows.customInstall`](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-custominstall)
extension that can declare install, repair, and uninstall actions.  It requires
the restricted `customInstallActions` capability and is intended for a narrow
class of applications.  OpenClaw eligibility, Store acceptance where
applicable, production App Installer behavior, managed deployment behavior,
target-user execution, and failure recovery have not been established.  The
extension is therefore a validation candidate, not a capability of the current
package or a supported cleanup path.

The package identity varies by build channel: `OpenClaw.Companion` for Release,
`OpenClaw.Companion.Alpha` for an Alpha prerelease package build, and
`OpenClaw.Companion.Dev` for an installed Dev build.  The installed identity and
package family can be found with:

```powershell
Get-AppxPackage *OpenClaw* | Select-Object Name, PackageFamilyName
```

With the current manifest, removing the matching companion package via
**Settings > Apps > OpenClaw Companion > Uninstall** leaves behind external
state including:

- **WSL distro:** `OpenClawGateway` remains in `wsl --list`
  (`OpenClawGateway-Dev` for Dev).
- **Roaming app data** under `%APPDATA%\OpenClawTray\` (device key, settings,
  mcp-token), or `%APPDATA%\OpenClawTray-Dev\` for Dev.
- **Local app data** under `%LOCALAPPDATA%\OpenClawTray\` (setup state, logs,
  VHD parent directory), or `%LOCALAPPDATA%\OpenClawTray-Dev\` for Dev.

> **Note:** If the tray was installed with MSIX and the data landed in the
> package-virtualized path under `%LOCALAPPDATA%\Packages\<PackageFamilyName>\`
> instead of real `%APPDATA%`, those directories are removed automatically by
> MSIX on uninstall.  Use
> [`validate-msix-storage-paths.ps1`](../scripts/validate-msix-storage-paths.ps1)
> to determine which layout applies.

---

## Recommended: Run "Remove Local Gateway" Before Uninstalling MSIX

1. Open the tray icon.
2. Navigate to **Settings > Local Gateway**.
3. Click **"Remove Local Gateway"**.
4. Wait for the engine to complete.  It stops keepalive processes, unregisters
   the WSL distro, nulls the device token, removes autostart, and cleans up app
   data.
5. Uninstall the MSIX package via **Settings > Apps**.

---

## Manual Recovery (After MSIX Removed Without In-Tray Cleanup)

If the MSIX was already removed and the WSL distro / app data remains, use the
commands below.

They target the current Release and Alpha builds, which share the
`OpenClawTray` data and startup names.  For an installed Dev build, substitute
`OpenClawTray-Dev`, `OpenClaw Companion (Dev)`, and `OpenClawGateway-Dev` where
shown.

```powershell
# 1. Unregister the distro (removes .vhdx from wsl's internal store)
wsl --unregister OpenClawGateway

# 2. Remove VHD parent directory (wsl --unregister may leave the folder)
Remove-Item "$env:LOCALAPPDATA\OpenClawTray\wsl\OpenClawGateway" `
    -Recurse -Force -ErrorAction SilentlyContinue

# 3. Remove autostart scheduled task and fallback registry entry
Unregister-ScheduledTask `
    -TaskName "OpenClaw Companion" -Confirm:$false -ErrorAction SilentlyContinue
Remove-ItemProperty `
    -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name "OpenClawTray" -ErrorAction SilentlyContinue

# 4. Remove local app data (setup state, logs)
Remove-Item "$env:LOCALAPPDATA\OpenClawTray" -Recurse -Force -ErrorAction SilentlyContinue

# 5. Remove roaming app data only for a full purge. This deletes settings,
#    gateway records and tokens, per-gateway identity files, and the MCP token.
#    Omit this step when preserving state for a reinstall.
Remove-Item "$env:APPDATA\OpenClawTray" -Recurse -Force -ErrorAction SilentlyContinue
```

Or use the repository's
[`validate-wsl-gateway-uninstall.ps1`](../scripts/validate-wsl-gateway-uninstall.ps1)
script from the repository root:

```powershell
.\scripts\validate-wsl-gateway-uninstall.ps1 -Mode Full -ConfirmDestructive
```
