# Uninstalling OpenClaw Tray - MSIX Package

> **Date:** 2026-05-07
> **Branch:** feat/wsl-gateway-uninstall

---

## Automatic cleanup for managed Gateways

**Feasibility verdict:** `runFullTrust` MSIX packages do **not** support a
`CustomInstall` / `CustomUninstall` extension that runs an arbitrary EXE
at uninstall time.  The supported extension points
(`windows.startupTask`, `windows.appExecutionAlias`, `com.extension`,
`windows.protocol`, etc.) do not include an uninstall hook.

Companion registers a user-scoped cleanup helper outside its MSIX payload when it
creates a managed WSL Gateway or acquires a missing native Store Gateway. The helper
removes the captured WSL registration and disk directory, or the native package it
acquired, after Companion removal. WSL ownership requires its creation marker and
matching registered disk path and registration identity. Replacements, unknown directories
and unreadable inventories are preserved. Native ownership is recorded only after verified
acquisition and bound to the Gateway's LocalState marker; other installed Companion variants
defer removal of the shared package. Unverified failed or cancelled acquisitions remain unclaimed.
The package-local ownership marker and successful Windows Remove receipt
let first launch settle cleanup after a fast reinstall; normal updates retain the
marker. Legacy WSL installations without a creation-bound cleanup registration are
preserved. A name/path marker cannot distinguish an original distro from a replacement
installed before the first upgraded launch, so startup never adopts it for unattended
removal. Use the explicit in-app removal flow for these installations. A pending conflict offers
preservation and continuation. Startup retires only removed managed connections and their
credentials, preserving unrelated Gateways, settings and local MCP state.

Malformed cleanup receipts stop the worker before removal. Malformed receipts or saved
settings also stop startup with a visible recovery error instead of entering the
preserve/disown path. Reconciliation validates settings before retiring credentials and
keeps a failed receipt for repair. Restore valid `cleanup-result.json` and `settings.json`
from a backup using the paths in the error, then reopen Companion. Invalid receipt recovery
does not discard the ownership record.

Older installations without verified ownership may still leave behind:

- **WSL distro** - `OpenClawGateway` remains in `wsl --list`.
- **Roaming app data** under `%APPDATA%\OpenClawTray\` (device key, settings,
  mcp-token).
- **Local app data** under `%LOCALAPPDATA%\OpenClawTray\` (setup state, logs,
  VHD parent directory).

> **Note:** If the tray was installed with MSIX and the data landed in the
> package-virtualized path (`%LOCALAPPDATA%\Packages\OpenClaw.Tray_<hash>\...`)
> instead of real `%APPDATA%`, those directories are removed automatically by
> MSIX on uninstall.  Bostick's commit 7 validation test (Path A vs Path B)
> confirms which layout applies.

---

## Remove a Gateway while keeping Companion, or clean up an older installation

1. Open the tray icon.
2. Navigate to **Settings → Local Gateway**.
3. Click **"Remove Local Gateway"** (Mattingly's warning banner in commit 4
   surfaces this step for MSIX users).
4. Wait for the engine to complete - it stops keepalive processes, unregisters
   the WSL distro, nulls the device token, removes autostart, and cleans up app
   data.
5. Uninstall the MSIX package via **Settings → Apps**.

---

## Manual Recovery (After MSIX Removed Without In-Tray Cleanup)

If the MSIX was already removed and the WSL distro / app data remains:

```powershell
# 1. Unregister the distro (removes .vhdx from wsl's internal store)
wsl --unregister OpenClawGateway

# 2. Remove VHD parent directory (wsl --unregister may leave the folder)
Remove-Item "$env:LOCALAPPDATA\OpenClawTray\wsl\OpenClawGateway" `
    -Recurse -Force -ErrorAction SilentlyContinue

# 3. Remove autostart registry entry
Remove-ItemProperty `
    -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name "OpenClawTray" -ErrorAction SilentlyContinue

# 4. Remove local app data (setup state, logs)
Remove-Item "$env:LOCALAPPDATA\OpenClawTray" -Recurse -Force -ErrorAction SilentlyContinue

# 5. Remove roaming app data (settings, device key - only if you want full purge)
#    NOTE: mcp-token.txt is intentionally preserved here; delete manually if needed.
Remove-Item "$env:APPDATA\OpenClawTray\setup-state.json" -Force -ErrorAction SilentlyContinue
```

Or use the validation script if it is available separately:

```powershell
.\validate-wsl-gateway-uninstall.ps1 -Mode Full -ConfirmDestructive
```
