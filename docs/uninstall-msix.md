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

If the MSIX was already removed and the WSL distro / app data remains, the
**supported recovery path** is the dedicated CLI flag:

```powershell
# Detect orphans (dry-run; exits 1 if any found)
openclaw-winnode --purge-wsl-orphans --json-output

# Apply the deletions
openclaw-winnode --purge-wsl-orphans --confirm-destructive --json-output
```

The CLI detects and removes:

| Kind                  | Where                                                                                |
|-----------------------|--------------------------------------------------------------------------------------|
| `wsl-distro`          | Known app-owned WSL distro names such as `OpenClawGateway` and `openclaw-local`      |
| `appdata-folder`      | `%APPDATA%\OpenClawTray\`                                                            |
| `localappdata-folder` | `%LOCALAPPDATA%\OpenClawTray\`                                                       |
| `registry-uri-scheme` | `HKCU\Software\Classes\openclaw` (legacy unpackaged URI scheme)                      |
| `registry-run-key`    | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\OpenClawTray` (legacy autostart) |

When `--confirm-destructive` is used, the CLI refuses to delete anything if it
can still see the OpenClaw Companion MSIX registered for the current user or the
tray mutex is present. Use the in-app cleanup first. The
`--force-even-if-installed` override exists only for support cases where you
have independently verified the installed app is gone but Windows' package
registration check is stale or unavailable.

If the CLI is not available (e.g., the package was uninstalled before this
fallback was published), the equivalent PowerShell one-liners are:

```powershell
# 1. Unregister the WSL distro(s)
wsl --list --quiet |
    Where-Object { $_ -in @('OpenClawGateway', 'openclaw-local', 'openclaw-staging') } |
    ForEach-Object { wsl --unregister $_ }

# 2. Remove autostart registry entry (legacy)
Remove-ItemProperty `
    -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name "OpenClawTray" -ErrorAction SilentlyContinue

# 3. Remove openclaw:// URI scheme registration (legacy)
Remove-Item "HKCU:\SOFTWARE\Classes\openclaw" -Recurse -Force -ErrorAction SilentlyContinue

# 4. Remove app data
Remove-Item "$env:LOCALAPPDATA\OpenClawTray" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:APPDATA\OpenClawTray"      -Recurse -Force -ErrorAction SilentlyContinue
```

