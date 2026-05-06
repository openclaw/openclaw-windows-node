---
name: "dev-reset-rebuild-loop"
description: "Canonical script for the OpenClaw tray dev-loop: kill processes → backup/wipe state → optionally wipe WSL distro → build x64 → optionally launch with visual capture"
domain: "dev-tooling"
confidence: "medium"
source: "observed (6+ manual iterations of the same step sequence during wsl-gateway-clean development, 2026-05-06)"
---

## Context

During active OpenClaw tray development, agents and developers repeatedly perform
the same step sequence:

1. Kill `OpenClaw*` processes by PID
2. Backup or wipe `%APPDATA%\OpenClawTray` and `%LOCALAPPDATA%\OpenClawTray`
3. Optionally unregister the `OpenClawGateway` WSL distro
4. `dotnet build` the x64 tray project
5. Optionally launch the tray with optional visual-test capture env vars

Use the canonical script at `scripts\dev-reset-rebuild-launch.ps1` instead of
hand-rolling these commands each time. The script is idempotent, supports
`-WhatIf`, and enforces the correct ordering (kill → backup → distro → build → launch).

## Patterns

### Use the canonical script

```powershell
# From worktree root:
.\scripts\dev-reset-rebuild-launch.ps1
```

### Kill processes by PID — always

Get a list of processes by name pattern, then stop each one by `-Id`.
`Stop-Process -Name` is forbidden in this repo.

```powershell
# Correct
$procs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "OpenClaw*" }
foreach ($p in $procs) { Stop-Process -Id $p.Id -Force }

# Wrong -- forbidden
Stop-Process -Name "OpenClaw*" -Force
```

### WSL file ops use `wsl bash -c`

Never access the WSL filesystem via `\\wsl$\` — the 9P protocol triggers
Windows permission prompts.

```powershell
# Correct
wsl bash -c 'cat ~/.openclaw-dev/devices/pending.json'
wsl bash -c 'echo "{}" > ~/.openclaw-dev/devices/paired.json'

# Wrong -- triggers UAC/permission dialogs
Get-Content "\\wsl$\Ubuntu\home\user\.openclaw-dev\devices\pending.json"
Set-Content "\\wsl$\Ubuntu\home\user\.openclaw-dev\devices\paired.json" "{}"
```

### Kill before touching state dirs

The tray holds file locks on `%APPDATA%\OpenClawTray` and `%LOCALAPPDATA%\OpenClawTray`.
Kill the process first; backup/delete second.

### Set env vars before launch when screenshots are needed

The tray auto-captures screenshots only when both env vars are set:

```powershell
$env:OPENCLAW_VISUAL_TEST     = "1"
$env:OPENCLAW_VISUAL_TEST_DIR = "C:\path\to\capture-dir"
```

Use `-CaptureDir` on the script to have it set these automatically.

## Examples

```powershell
# Standard reset + rebuild + launch (no WSL wipe, no capture)
.\scripts\dev-reset-rebuild-launch.ps1

# Full clean slate: also unregister the OpenClawGateway WSL distro
.\scripts\dev-reset-rebuild-launch.ps1 -WipeWslDistro

# Reset + build, but don't launch (useful before testing manually)
.\scripts\dev-reset-rebuild-launch.ps1 -DontLaunch

# Reset + build + launch with visual-test screenshot capture
.\scripts\dev-reset-rebuild-launch.ps1 -CaptureDir .\visual-test-output\my-test

# Dry-run: see what would happen without touching anything
.\scripts\dev-reset-rebuild-launch.ps1 -WhatIf -DontLaunch -SkipBuild

# Fast wipe (no backup) + skip build, just clear state
.\scripts\dev-reset-rebuild-launch.ps1 -NoBackup -SkipBuild -DontLaunch
```

## Anti-Patterns

- **Don't `Stop-Process -Name "OpenClaw*"`** — name-based kills are forbidden in
  this repo. Use `Get-Process` then `Stop-Process -Id <PID>`.

- **Don't use `\\wsl$\` or `\\wsl.localhost\` paths** for WSL file operations —
  triggers Windows permission prompts via the 9P protocol; use `wsl bash -c` instead.

- **Don't backup/delete state dirs before killing the tray** — the tray holds
  file locks; the kill step must precede the backup/wipe step.

- **Don't skip `OPENCLAW_VISUAL_TEST` env vars** when UI screenshots are needed
  — the tray only auto-captures when both `OPENCLAW_VISUAL_TEST=1` and
  `OPENCLAW_VISUAL_TEST_DIR` are set before launch.

- **Don't hand-roll this sequence** — use the canonical script to ensure
  idempotency, correct step ordering, and `-WhatIf` support.

## Citations

- Canonical script: `scripts\dev-reset-rebuild-launch.ps1`
