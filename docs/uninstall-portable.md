# Uninstalling OpenClaw Tray - Portable ZIP

> **Date:** 2026-05-07
> **Branch:** feat/wsl-gateway-uninstall

Portable (ZIP) installations have **no automatic uninstall hook**.
Simply deleting the folder leaves the WSL distro, app data, and autostart
entry behind.  Follow one of the two paths below for a clean removal.

---

## Recommended: In-Tray Removal (Requires the Tray Running)

1. Open the tray icon.
2. Navigate to **Settings → Local Gateway**.
3. Click **"Remove Local Gateway"**.
4. The engine stops keepalive processes, unregisters the WSL distro, nulls
   the device token, removes autostart, and cleans up app data.
5. After the operation completes, delete the portable folder.

---

## CLI: Headless Removal (No Tray UI Required)

Run from the portable folder:

```powershell
# Destructive - removes the local WSL gateway cleanly, then print result to stdout
.\OpenClaw.Tray.WinUI.exe --uninstall --confirm-destructive

# With JSON output for programmatic consumption (tokens redacted in output):
.\OpenClaw.Tray.WinUI.exe --uninstall --confirm-destructive --json-output .\uninstall-result.json

# Dry-run - records what would happen without any destruction:
.\OpenClaw.Tray.WinUI.exe --uninstall --dry-run
```

**Exit codes:**

| Code | Meaning |
|------|---------|
| 0 | Success - all steps completed, postconditions satisfied |
| 1 | Partial failure - one or more steps failed (see JSON output or stderr) |
| 2 | Bad arguments - `--confirm-destructive` or `--dry-run` missing |

After the CLI command exits 0, delete the portable folder.

---

## WARNING: Deleting the Folder Without Running Uninstall

Deleting the portable folder **without** running the uninstall first leaves:

- **WSL distro orphaned** - `OpenClawGateway` remains in `wsl --list`.
  Manual cleanup: `wsl --unregister OpenClawGateway`

- **App data** remains under:
  - `%APPDATA%\OpenClawTray\` - device key, settings, mcp-token
  - `%LOCALAPPDATA%\OpenClawTray\` - setup state, logs, exec policy, VHD parent dir

- **Autostart entry** may remain in
  `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\OpenClawTray`

Manual WSL + registry cleanup:

```powershell
wsl --unregister OpenClawGateway
Remove-ItemProperty -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" `
    -Name "OpenClawTray" -ErrorAction SilentlyContinue
Remove-Item "$env:LOCALAPPDATA\OpenClawTray\wsl\OpenClawGateway" -Recurse -Force -ErrorAction SilentlyContinue
```

---

## Installer Uninstall Preserved the Local Gateway

The installer's uninstaller can report that it could not confirm whether your
data was migrated to the Store app, and that it left the local WSL gateway in
place. That is deliberate. When migration state cannot be validated, the
uninstaller preserves the gateway rather than risk destroying data that a
completed migration still depends on. No override is offered, because at that
point nothing on the machine can tell a broken check apart from a real
migration.

The app is still uninstalled. Only the WSL distro and its generated state are
kept.

To remove them:

- **Before uninstalling**, open the tray and choose
  **Settings -> Local Gateway -> Remove Local Gateway**.
- **After uninstalling**, run the manual cleanup above. The distro is
  `OpenClawGateway` and its VHD parent directory is
  `%LOCALAPPDATA%\OpenClawTray\wsl\OpenClawGateway`.

Silent uninstalls (`/VERYSILENT`) skip the message. The same outcome is written
to the Inno uninstall log, naming the distro and the preserved directory.
