# MSIX End-to-End Test Runbook

Manual test matrix for an OpenClaw Companion MSIX release. Run on a fresh
Windows 11 24H2 VM and on a Windows-on-ARM device before promoting a tag to
`make_latest=true`.

The automated counterpart lives in:

- `scripts/test-msix-install.ps1` — install/launch/uninstall smoke test.
- `scripts/test-appinstaller-update.ps1` — `.appinstaller` upgrade simulation.
- `tests/OpenClaw.Tray.Tests/MsixManifestAssertionTests.cs` — manifest contract.
- `tests/OpenClaw.Tray.Tests/AppInstallerTemplateAssertionTests.cs` — template contract.

The runbook below covers the things automation cannot — OS consent dialogs,
multi-launch behaviour, real WSL distros, and the dirty-uninstall recovery.

## Pre-flight

- [ ] Clean Windows 11 24H2 VM. No prior `OpenClaw*` package, no `openclaw-*`
      WSL distros, no `%APPDATA%\OpenClawTray\` or `%LOCALAPPDATA%\OpenClawTray\`.
- [ ] `wsl --list` reports either no distros or only unrelated distros.
- [ ] Dev Mode **off** in Windows Settings (so sideload trust is exercised
      end-to-end through Trusted Signing, not bypassed).

## Scenarios

### 1. Clean install via signed MSIX

1. Open the GitHub release page in Edge.
2. Download the signed MSIX for the machine architecture:
   `OpenClawCompanion-X.Y.Z-win-x64.msix` or
   `OpenClawCompanion-X.Y.Z-win-arm64.msix`.
3. **Assert** Windows AppInstaller opens with:
   - Publisher: `CN=Scott Hanselman, O=Scott Hanselman, …` (no "untrusted")
   - DisplayName: `OpenClaw Companion`
   - Version: the tag version
4. Click **Install**.
5. **Assert** the tray icon appears in the notification area within 5 s.
6. **Assert** `Get-AppxPackage OpenClaw.Companion*` returns one row with the
   expected `Publisher` and a 4-part `Version`.
7. **Assert** `Package.GetAppInstallerInfo()` or an equivalent package query
   reports the embedded architecture-specific AppInstaller URL on Windows builds
   that support embedded App Installer metadata.

### 2. First-run permission consent (packaged path)

1. On first launch, open Settings → Onboarding → Permissions.
2. **Assert** each row reports a status pulled from the per-package consent
   API (the unpackaged code path would have said "denied" or "unknown" here
   for camera/mic/location).
3. Trigger an action that uses each capability and **assert** the OS
   consent prompt appears once, with **"OpenClaw Companion"** as the app
   name (not "Desktop apps", which would mean we accidentally fell back to
   the unpackaged DeviceAccessInformation surface).
   - Camera: click "Take photo" in the onboarding camera widget.
   - Microphone: click "Test microphone".
   - Location: click "Use location" in the onboarding wizard.
4. **Assert** Settings → Privacy → Camera (and Microphone, Location) lists
   "OpenClaw Companion" with a per-app toggle.

### 3. Permission revocation while running

1. With the tray running, open Settings → Privacy → Camera.
2. Turn the **OpenClaw Companion** toggle OFF.
3. **Assert** the tray's Permissions page (Settings → Permissions or the
   onboarding row strip) updates within ~1 s without restart — this proves
   the `AppCapability.AccessChanged` subscription wired up by
   `PermissionChecker.SubscribeToAccessChangesPackaged` is firing.
4. Toggle it back ON and **assert** the row returns to "Granted".

### 4. StartupTask (replaces the legacy HKCU\\…\\Run autostart)

1. Open Settings → Auto-start. Toggle **Launch when Windows starts** ON.
2. **Assert** Windows shows the one-time consent dialog for the
   `OpenClawCompanionStartup` task.
3. Sign out and back in (or reboot).
4. **Assert** the tray appears in the notification area shortly after sign-in.
5. **Assert** Task Manager → Startup apps lists "OpenClaw Companion" with
   status "Enabled".
6. **Assert** `Get-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name OpenClawTray -EA SilentlyContinue`
   is empty — i.e. we did NOT also write the legacy Run key.

### 5. Local-gateway install + in-app uninstall

1. Open Onboarding → Local gateway → "Install WSL gateway". Wait for the
   distro to register and the tray status to flip to "Connected".
2. **Assert** `wsl --list --quiet` shows `openclaw-local` (or the variant
   the install chose).
3. Open Settings → Local Gateway → **Remove Local Gateway**.
4. **Assert** the in-app status reports success; `wsl --list --quiet` no
   longer shows the distro; `%LOCALAPPDATA%\OpenClawTray\wsl-keepalive\`
   markers are gone.

### 6. Clean app uninstall

1. Run the in-app **Settings → "Reset & remove"** (when implemented per
   the Track 3 follow-up). Until that lands, run scenario 5 first, then:
2. Settings → Apps → OpenClaw Companion → Uninstall.
3. Reboot.
4. **Assert** `Get-AppxPackage OpenClaw.Companion*` returns nothing.
5. **Assert** the following are absent: `%APPDATA%\OpenClawTray\`,
   `%LOCALAPPDATA%\OpenClawTray\`, `HKCU:\Software\Classes\openclaw`,
   `HKCU:\Software\Microsoft\Windows\CurrentVersion\Run\OpenClawTray`,
   `openclaw-*` WSL distros.

### 7. Dirty uninstall + recovery (proves `--purge-wsl-orphans`)

This scenario deliberately skips the in-app cleanup so we can verify the
support recipe works.

1. Re-install per scenario 1 and re-do scenario 5 up to and including a
   working WSL gateway.
2. Without using Settings → Local Gateway, go straight to Settings → Apps
   and Uninstall.
3. **Assert** the WSL distro is still present (`wsl --list --quiet` shows
   `openclaw-local`) — this is the failure mode we need to recover from.
4. Run the published one-liner from `docs/uninstall-msix.md`:
   ```powershell
   openclaw-winnode --purge-wsl-orphans --json-output
   ```
5. **Assert** exit code 1 and the JSON report enumerates the orphan distro
   and any leftover folders.
6. Run with `--confirm-destructive`:
   ```powershell
   openclaw-winnode --purge-wsl-orphans --confirm-destructive --json-output
   ```
7. **Assert** exit code 0 and the `Removed` list contains everything from
   the earlier `Orphans` list. `wsl --list --quiet` no longer shows the
   distro; `%APPDATA%\OpenClawTray\` and `%LOCALAPPDATA%\OpenClawTray\` are
   gone.

### 8. `.appinstaller` auto-update (vN → vN+1)

1. Install vN via the signed MSIX on Windows 11 24H2 and via the hosted
   architecture-specific `.appinstaller` on a downlevel Windows target.
2. Publish vN+1 by tagging `vX.Y.Z+1` and re-uploading the rendered
   `openclaw-x64.appinstaller` / `openclaw-arm64.appinstaller` files to GitHub
   Pages (the release pipeline produces the files; the gh-pages publish is
   currently manual — see RELEASING.md).
3. **Trigger 1 (AutomaticBackgroundTask):** Leave the tray running and give
   Windows enough time to poll the stable URL. **Assert** no App Installer UI
   appears during normal launch.
4. **Trigger 2 (in-app, on demand):** From a fresh vN install, click tray menu
   → "Check for updates". **Assert** the tray is not force-closed by default and
   the in-app status surfaces `Ready` / "restart when convenient" or `Current`
   if vN+1 was not published.
5. **Trigger 3 (explicit restart):** If a manual "Update now" affordance is
   exposed, invoke it and assert Windows applies vN+1 after the explicit restart.

### 9. Sideload trust on a stock no-dev-mode box

1. Fresh Win11 VM, Dev Mode OFF, no developer keys imported.
2. Double-click the `.msix` directly (not the `.appinstaller`) downloaded
   from the release.
3. **Assert** the install succeeds with no "untrusted publisher" warning
   — the Azure Trusted Signing cert chain is what's being validated here.

### 10. ARM64

1. On a Windows-on-ARM device (Surface Pro X, Snapdragon X laptop), repeat
   scenarios 1, 2, 5, 7, 8 against the `-win-arm64.msix`.
2. **Assert** every consent dialog still shows the package name "OpenClaw
   Companion" (no name mangling on ARM64 manifests).
3. **Assert** scenario 8 step 3 also works — `.appinstaller` is
   architecture-aware and Windows picks the ARM64 MSIX from the same URL.

## Recording results

Record outcomes per scenario in the release tracking issue with:

- Build tag tested
- OS build (winver)
- Architecture (x64 / arm64)
- Pass / Fail / Skip
- Notes for any partial passes or unexpected dialogs

Promote `openclaw-x64.appinstaller` and `openclaw-arm64.appinstaller` to
GitHub Pages only after scenarios 1, 2, 5, 6, 7, 8 (triggers 1 and 2), 9, and
10 all pass on at least one VM.
