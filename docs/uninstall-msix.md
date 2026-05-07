# Uninstalling OpenClaw Tray — MSIX Package

> **Date:** 2026-05-07  
> **Branch:** feat/wsl-gateway-uninstall

---

## MSIX Cannot Auto-Clean WSL State

**Feasibility verdict:** `runFullTrust` MSIX packages do **not** support a
`CustomInstall` / `CustomUninstall` extension that runs an arbitrary EXE
at uninstall time.  The supported extension points
(`windows.startupTask`, `windows.appExecutionAlias`, `com.extension`,
`windows.protocol`, etc.) do not include an uninstall hook.

Therefore, removing the MSIX package via **Settings → Apps → OpenClaw Tray
→ Uninstall** will silently leave behind:

- **WSL distro** — `OpenClawGateway` remains in `wsl --list`.
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

## Recommended: Run "Remove Local Gateway" Before Uninstalling MSIX

1. Open the tray icon.
2. Navigate to **Settings → Local Gateway**.
3. Click **"Remove Local Gateway"** (Mattingly's warning banner in commit 4
   surfaces this step for MSIX users).
4. Wait for the engine to complete — it stops keepalive processes, unregisters
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

# 5. Remove roaming app data (settings, device key — only if you want full purge)
#    NOTE: mcp-token.txt is intentionally preserved here; delete manually if needed.
Remove-Item "$env:APPDATA\OpenClawTray\setup-state.json" -Force -ErrorAction SilentlyContinue
```

Or use the validation script if it is available separately:

```powershell
.\validate-wsl-gateway-uninstall.ps1 -Mode Full -ConfirmDestructive
```
