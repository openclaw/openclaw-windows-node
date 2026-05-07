# Commit 5 Delivery — WSL Gateway Uninstall CLI + Inno Hook + Docs + Gap Closures

**SHA:** 0a78b0d  
**Branch:** feat/wsl-gateway-uninstall  
**Author:** Aaron (Backend/Infrastructure)

---

## What Landed

### CLI `--uninstall` Flag (App.xaml.cs)

Early intercept in `OnLaunched` BEFORE the single-instance mutex check so Inno can call it while tray is running. Attaches parent console via `AttachConsole(-1)` for stdout visibility.

**Full contract:**
```
OpenClaw.Tray.WinUI.exe --uninstall --confirm-destructive [--dry-run] --json-output <path>
```

- `--confirm-destructive` — required; prevents accidental invocation
- `--dry-run` — optional; reports what would happen, no mutations
- `--json-output <path>` — required; file path for JSON result output
- Exit 0 = success, non-zero = failure
- All access tokens redacted via `CliRedact()` before writing JSON and stdout
- No `--json-output` → exit code 2 (usage error, no output)
- No `--confirm-destructive` → exit code 2

### Inno [UninstallRun] Hook

- `installer.iss` adds `[UninstallRun]` entry pointing at `scripts\Uninstall-LocalGateway.ps1`
- Ordering: runs BEFORE `{app}\` directory deletion (EXE is guaranteed present)
- `scripts/Uninstall-LocalGateway.ps1` always exits 0 so Inno never aborts on failure
- Searches EXE at `{app}\OpenClaw.Tray.WinUI.exe`

### Docs

- `docs/uninstall-portable.md` — portable ZIP: in-tray button + CLI flag paths
- `docs/uninstall-msix.md` — MSIX verdict: NOT feasible for hook; in-tray button only

### Gap Closures

- **run.marker:** Step 8a idempotent delete. Written only by `App.xaml.cs` constructor; no setup writer.
- **VHD parent-dir:** Step 5a deletes `%LOCALAPPDATA%\OpenClawTray\wsl\<DistroName>` after distro unregister.
- **VhdDirAbsent postcondition** added to engine + validate script.

### Validate Script Updates

- `-NoCli` / `-ExePath` params
- Full mode delegates to CLI; `-NoCli` forces inline PS fallback for diagnostics
- `vhd_dir_absent` added to `Get-Postconditions` and `Get-Verdict` required keys

---

## For Bostick — Commit 7 Integration Notes

The CLI flag is live. When wiring UI → CLI:

```
OpenClaw.Tray.WinUI.exe --uninstall --confirm-destructive --json-output C:\path\to\result.json
```

For dry-run preview in UI:
```
OpenClaw.Tray.WinUI.exe --uninstall --confirm-destructive --dry-run --json-output C:\path\to\result.json
```

JSON output schema mirrors `LocalGatewayUninstallResult` (same as engine result). Access tokens are redacted (`[REDACTED]`) in the file.

**Potential integration wrinkle:** `OnLaunched` calls `Environment.Exit()` after the CLI path completes. This fires `ProcessExit` → `MarkRunEnded()` which deletes the run.marker. No cleanup needed by caller.
