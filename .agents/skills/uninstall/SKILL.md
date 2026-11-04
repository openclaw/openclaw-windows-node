---
name: uninstall
description: Uninstall or hard-clean OpenClaw Companion, native Gateway/MXC, WSL Gateway, and llama.cpp/Local AI state for a clean retest. Inventory exact targets first and confirm before deleting.
---

# Uninstall and clean retest

## Choose the cleanup scope first

Creating or updating cleanup tooling is not permission to uninstall anything.
Inventory first, show the exact proposed targets and preserved resources, then
ask for confirmation before stopping processes or changing installed state.

| User intent | Procedure |
| --- | --- |
| Exercise the dev build's WSL uninstall engine | **Dev-build engine uninstall** below. |
| Clean reinstall, hard wipe, remove native Gateway/MXC or all managed llama.cpp state | Read [HARD-CLEAN.md](HARD-CLEAN.md) before acting. The dev engine path alone is not a native hard clean. |
| Demo/test device without Copilot | Copy `scripts\clean-uninstall.ps1` to the device. Run with Windows PowerShell 5.1; default is preview, `-ConfirmDestructive` applies. See [standalone usage](HARD-CLEAN.md#standalone-script-no-copilot-required). |
| Ordinary installed-app removal | Settings > Apps > Installed apps. See `docs\SETUP.md`, Uninstalling. Retained state is intentional. |

The standalone script's flags, including `-All`, `-ExcludeCachedModels`, and
`-RemoveIsolatedProfilePath`, are listed in the
[HARD-CLEAN option table](HARD-CLEAN.md#standalone-script-no-copilot-required).
`-All` never implies `-ConfirmDestructive` or discovers custom profile paths.
Review and confirm the expanded scope first.

Existing helpers are narrower than a hard clean:

- `scripts\dev-reset-rebuild-launch.ps1` resets production tray state and can wipe
  the production WSL distro. It mutates by default; use `-WhatIf` to preview.
- `scripts\reset-openclaw-wsl-validation-state.ps1` is a production-WSL validation
  reset with backups and a `-ConfirmDestructiveClean` gate. It is not a native
  Gateway/MXC package teardown.
- `scripts\_uninstall-helpers.ps1` supplies utilities, not a complete uninstaller.

Do not chain these helpers together as an "all" wipe. Their identities, backup
behavior, process selection, and WSL scope differ.

## Dev-build engine uninstall

`OpenClaw.Tray.WinUI.exe` embeds the SetupEngine and accepts `--uninstall`
directly, so a local `dotnet build` output is enough. It runs a real uninstall
of dev/test state: WSL gateway distro, `openclaw` CLI inside WSL, gateway
systemd service, Tailscale state, and the tray's AppData files.

This is destructive. Confirm with the user before the final `--confirm-destructive`
run, and call out anything it will touch that isn't disposable dev state (e.g. a WSL
distro or Tailscale session actually in use).

### Procedure

1. **Build with the dev identity, for the matching architecture.** `.\build.ps1`
   alone defaults to release identity even in a Debug configuration, and a
   release-identity exe would unregister the real `OpenClawGateway` distro and mutate
   `%APPDATA%\OpenClawTray` instead of the `-Dev` copies below (the output path is
   identical either way, so nothing later in this procedure would catch that
   mistake). Always build with `-DevBuild`, and verify the marker before proceeding:

   ```powershell
   .\build.ps1 -DevBuild   # skip only if you already built with -DevBuild

   $arm64 = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
       [System.Runtime.InteropServices.Architecture]::Arm64
   $arch = if ($arm64) { 'win-arm64' } else { 'win-x64' }
   $outDir = "src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\$arch"
   $exe = "$outDir\OpenClaw.Tray.WinUI.exe"

   $identity = (Get-Content "$outDir\app-identity.txt" -Raw).Trim()
   if ($identity -ne 'dev') { throw "Build output identity is '$identity', not 'dev' - rebuild with -DevBuild." }

   $pathOverrides = 'OPENCLAW_TRAY_DATA_DIR',
       'OPENCLAW_TRAY_APPDATA_DIR',
       'OPENCLAW_TRAY_LOCALAPPDATA_DIR',
       'OPENCLAW_TRAY_LOCAL_DATA_DIR',
       'OPENCLAW_STATE_DIR'
   $activeOverrides = $pathOverrides | Where-Object {
       -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
   }
   if ($activeOverrides) {
       throw "Refusing destructive uninstall while path overrides are set: $($activeOverrides -join ', ')"
   }
   ```

2. **Stop only the dev instance, by PID.** A background `OpenClaw.Tray.WinUI.exe`
   (e.g. the tray icon) holds its own log/journal files open, which makes later steps
   fail to delete the AppData Logs directory. A release-identity tray can share the
   same process name, so match on the resolved `$exe` path and confirm before
   stopping; this repo kills processes by PID only, never by name
   (see `scripts/dev-reset-rebuild-launch.ps1`):

   ```powershell
   $exePath = (Resolve-Path $exe).Path
   $devProcs = Get-Process OpenClaw.Tray.WinUI -ErrorAction SilentlyContinue |
       Where-Object { $_.Path -eq $exePath }
   $devProcs | Format-Table Id, Path -AutoSize   # confirm these are the dev instance before stopping
   $devProcs | ForEach-Object { Stop-Process -Id $_.Id -Force }
   ```

3. **Dry-run first**, capturing output and exit code (a bare shell invocation can swallow
   the exit code for a WinUI apphost; use `Start-Process -Wait -PassThru`):

   ```powershell
   $p = Start-Process -FilePath $exe -ArgumentList '--uninstall','--dry-run' -PassThru -Wait `
       -RedirectStandardOutput out.log -RedirectStandardError err.log
   $p.ExitCode
   Get-Content out.log
   ```

   Review the "Would rollback: ..." line for each of the 34 setup steps. Note the
   `--data-dir` / `--distro-name` printed at the top; dev-branch builds use
   `OpenClawTray-Dev` / `OpenClawGateway-Dev`, separate from a real install's
   `OpenClawTray` / `OpenClawGateway`. Confirm that the printed data directories
   are exactly `%APPDATA%\OpenClawTray-Dev` and
   `%LOCALAPPDATA%\OpenClawTray-Dev`. Stop if they point anywhere else.

4. **Check for real state the dry-run doesn't surface**, e.g. a live WSL distro
   and the Dev startup task:

   ```powershell
   wsl.exe --list --verbose
   Get-ScheduledTask -TaskName 'OpenClaw Companion (Dev)' -ErrorAction SilentlyContinue
   ```

   If the distro named in step 3's output is present, flag to the user that it will be
   unregistered (`wsl --unregister`), permanently deleting its filesystem.

   The dry-run only logs rollback step IDs, not the concrete files each step would
   touch. If it matters to the user's situation, inspect them before consenting:
   `%APPDATA%\OpenClawTray-Dev` holds the Gateway registry and settings, and
   `%LOCALAPPDATA%\OpenClawTray-Dev` holds `windows-node-context.json` and
   Local AI state.

5. **Confirm with the user**, then run the real uninstall the same way:

   ```powershell
   $p = Start-Process -FilePath $exe -ArgumentList '--uninstall','--confirm-destructive' -PassThru -Wait `
       -RedirectStandardOutput out2.log -RedirectStandardError err2.log
   $p.ExitCode
   Get-Content out2.log
   ```

6. **Verify** the specific artifacts each rollback step is supposed to clean up: not the
   AppData roots themselves. `TrayArtifactCleanup` (registry/files/settings only, no WSL
   calls) and the WSL/gateway rollback steps between them reset or remove selected files
   and records in place; neither deletes `%APPDATA%\OpenClawTray[-Dev]` or
   `%LOCALAPPDATA%\OpenClawTray[-Dev]`, so asserting those roots are gone will report a
   successful run as failed:

   ```powershell
   wsl.exe --list --verbose   # target distro should no longer be listed
   # The current run's own log/journal remain under Logs\Setup (they are open during
   # cleanup); success = that directory holds only uninstall-engine-<this-run> files
   Get-ChildItem "$env:APPDATA\OpenClawTray-Dev\Logs\Setup"   # only this run's uninstall-engine-* files expected
   Test-Path "$env:LOCALAPPDATA\OpenClawTray-Dev\Logs"   # should be False
   Test-Path "$env:LOCALAPPDATA\OpenClawTray-Dev\run.marker"  # should be False
   Test-Path "$env:APPDATA\OpenClawTray-Dev\exec-approvals.json"  # should be False
   Test-Path "$env:LOCALAPPDATA\OpenClawTray-Dev\windows-node-context.json"  # should be False, if it existed
   # settings.json: GatewayUrl removed (key absent); EnableNodeMode/AutoStart reset to false
   # unless other gateway records remain
   # Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name OpenClawTray-Dev -ErrorAction SilentlyContinue
   # should error/be absent (autostart registry key removed)
   Get-ScheduledTask -TaskName 'OpenClaw Companion (Dev)' -ErrorAction SilentlyContinue   # should return nothing
   ```

   The current run's own active log and journal under `%APPDATA%\OpenClawTray-Dev\Logs\Setup`
   are expected to remain: the Setup Engine still holds them open when
   `TrayArtifactCleanup` runs, so the `Failed to delete AppData Logs directory` warning
   is normal. A `Failed to delete...` warning naming any other file, or leftover files
   from earlier runs, means a process is still holding them open (see step 2).

### Notes

- Log and journal for each run are printed at the end of stdout, under
  `%APPDATA%\OpenClawTray[-Dev]\Logs\Setup\uninstall-engine-<timestamp>.jsonl`.
- For a native or full clean retest, use [HARD-CLEAN.md](HARD-CLEAN.md).
