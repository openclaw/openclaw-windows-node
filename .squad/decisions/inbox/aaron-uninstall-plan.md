# Robust End-User Uninstall Plan — OpenClaw Windows + WSL Gateway

**Author:** Aaron (Backend / Infra) — planning only, no code in this doc
**Date:** 2026-05-04T19:05:00-07:00
**Worktree:** `..\openclaw-wsl-gateway-clean` @ `feat/wsl-gateway-clean` (16 commits since `871b959`)
**Requested by:** Mike Harsh — quote: *"We need to have a robust uninstall plan."*

> Phase 7 reset script (`scripts/reset-openclaw-wsl-validation-state.ps1`) is a
> **dev/test** cleanup that nukes `OpenClawGateway` for re-validation. This
> document designs the **end-user-facing** uninstall: what happens when a user
> installs OpenClaw on Windows and later wants it gone cleanly.

---

## 1. Scope — Tiers of "uninstall"

Recommend supporting **two tiers** with a single user choice point:

| Tier | What it removes | Default? |
|------|-----------------|----------|
| **App-only (soft)** | Tray app binaries + Start Menu + Add/Remove Programs entry + autostart registry value. **Leaves** WSL distro + identity dirs intact, so a reinstall picks up where the user left off. | No — opt-in for "I'm reinstalling" |
| **Full uninstall** | App-only **plus** `OpenClawGateway` WSL distro **plus** `%APPDATA%\OpenClawTray` and `%LOCALAPPDATA%\OpenClawTray`. | **Yes — default** |

Explicitly **out of scope** ("nuclear"):

- Uninstalling the WSL platform itself (`Microsoft.WSL` / `wsl --uninstall`). Other distros may depend on it.
- Removing Ubuntu-24.04 from Microsoft Store (`Canonical.Ubuntu.2404` APPX). It's a launcher; harmless to leave.
- Touching `%USERPROFILE%\.wslconfig` (per Craig — never written by us; never removed by us).
- Per-machine state for *other* users (we are per-user, see §6).

**UX recommendation:** the Windows Add/Remove Programs flow always defaults to **Full**, and presents one checkbox: *"Keep my OpenClaw WSL data and identity in case I reinstall"* (unchecked by default). Power users / IT can pass a flag to the script for unattended app-only.

---

## 2. Inventory — everything we install or touch

Cited from the clean worktree. All paths assume **per-user** install (recommended — see §5 / open question Q1).

### 2.1 Windows components

| Artifact | Path / key | Created by |
|---|---|---|
| Tray app binaries | TBD by packaging — likely `%LOCALAPPDATA%\Programs\OpenClawTray\` (per-user MSIX/MSI) | Installer |
| Add/Remove Programs entry | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenClawTray` (or MSIX package registration) | Installer |
| Start Menu shortcut | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\OpenClaw\` | Installer |
| Per-user data dir (`DataPath`) | `%LOCALAPPDATA%\OpenClawTray\` — settings, logs, run.marker, crash.log, exec approvals, diagnostics jsonl | `App.xaml.cs:151-164` |
| Per-user identity dir (`IdentityDataPath`) | `%APPDATA%\OpenClawTray\` — operator + node `DeviceIdentity` (tokens), policy | `App.xaml.cs:159-162` |
| WSL instance VHD location | `%LOCALAPPDATA%\OpenClawTray\wsl\OpenClawGateway\` (default from `LocalGatewaySetup.ResolveInstallLocation`, line 644) | `wsl --install ... --location` |
| Autostart entry | `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\OpenClawTray` (REG_SZ → exe path) | `Services/AutoStartManager.cs:11-12` (only if user enabled it) |
| Single-instance mutex | Process-scoped, dies with process — no cleanup needed |
| Firewall rules | **None** — gateway is loopback-only (port 18789, Craig-confirmed) |
| Scheduled tasks | **None** in current design |
| `HKLM` entries | **None** in per-user model |

### 2.2 WSL distro components

| Artifact | What | How removed |
|---|---|---|
| Distro registration | `OpenClawGateway` in `wsl --list` | `wsl --unregister OpenClawGateway` |
| VHD on disk | `ext4.vhdx` under `%LOCALAPPDATA%\OpenClawTray\wsl\OpenClawGateway\` | `wsl --unregister` deletes it; we then verify the dir is empty and remove the empty parent dirs |
| `~\.wslconfig` global | **NEVER touched** by install — never touched by uninstall (Craig) |

### 2.3 In-distro Linux components

All disappear with `wsl --unregister OpenClawGateway` — no separate cleanup needed. For the record (so support can reason about state pre-uninstall):

- `/opt/openclaw/...` — upstream Linux installer payload
- `/etc/systemd/user/openclaw-gateway.service` — gateway unit (`LocalGatewaySetup.cs:864`)
- `loginctl enable-linger openclaw` — set on `openclaw` user (`LocalGatewaySetup.cs:722`)
- `/etc/wsl.conf` — `systemd=true / interop=false / appendWindowsPath=false / default=openclaw` (`LocalGatewaySetup.cs:704, 761-764`)
- `/etc/wsl-distribution.conf` — shortcut/terminal disabled (`LocalGatewaySetup.cs:718`)
- `/home/openclaw/...` — gateway config, pairing store, logs

### 2.4 Backup-worthy state (pre-delete)

Mirroring Phase 7's `Backup-Directory` model — copy *before* remove:

- `%APPDATA%\OpenClawTray\` (operator + node tokens, identity)
- `%LOCALAPPDATA%\OpenClawTray\` (settings, logs)
- **NOT** the WSL VHD — too large, and `wsl --unregister` is the only safe way to release it; users wanting that backup should `wsl --export OpenClawGateway <path.tar>` themselves before uninstalling. (We may surface this as an optional pre-step in the script — see §5.)

Tokens are not durable across reinstalls anyway: a fresh install re-pairs from a fresh setup-code, so the backup is mainly for support diagnostics, not for "restore in place."

---

## 3. Order of operations (safe sequence)

```
1.  Detect & stop tray process       (release file locks on DataPath)
       Stop-Process -Id <openclaw-tray-pid>   # exact PID, never -Name
       Wait for exit (<= 10s); if still alive → escalate to terminate.

2.  Stop in-distro service first     (clean shutdown; release port 18789)
       wsl -d OpenClawGateway -u openclaw -- systemctl --user stop openclaw-gateway.service
       (best-effort; ignore failure — terminate handles hangs)

3.  Terminate the distro             (release VHD lock, NEVER --shutdown)
       wsl --terminate OpenClawGateway

4.  Backup identity + data           (Phase-7-style copy before remove)
       Copy %APPDATA%\OpenClawTray\        → %TEMP%\OpenClawUninstallBackup-<ts>\appdata-OpenClawTray\
       Copy %LOCALAPPDATA%\OpenClawTray\   → %TEMP%\OpenClawUninstallBackup-<ts>\localappdata-OpenClawTray\
       Print backup path to user / installer log.

5.  Unregister the distro            (deletes the VHD)
       wsl --unregister OpenClawGateway
       Verify: wsl --list --quiet does NOT contain OpenClawGateway.

6.  Remove identity + data dirs      (after process is dead)
       Remove-Item -LiteralPath %APPDATA%\OpenClawTray\        -Recurse -Force
       Remove-Item -LiteralPath %LOCALAPPDATA%\OpenClawTray\   -Recurse -Force
       (LocalAppData includes the now-empty wsl\OpenClawGateway\ instance dir.)

7.  Remove autostart                 (registry value only — exact key, not delete-tree)
       Reg DELETE HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run /v OpenClawTray /f
       (See AutoStartManager.cs:11-12. Per-user only.)

8.  Remove Start Menu shortcut       (single dir under %APPDATA%\...\Programs\OpenClaw\)

9.  Uninstall the package LAST       (so the running uninstaller, if invoked
                                      from MSIX/MSI, doesn't yank itself first)
       MSIX: Remove-AppxPackage <PackageFullName>
       MSI:  msiexec /x <ProductCode> /qn
       This removes Add/Remove Programs entry implicitly.
```

Key dependency reasons:

- **Step 1 before 6:** tray process holds file locks on `DataPath\diagnostics-*.jsonl` and `run.marker`.
- **Step 2 before 3:** systemd stop is graceful; terminate is forced. Even though terminate handles hangs, a clean stop avoids a "service died" entry in the last log.
- **Step 3 before 5:** `wsl --unregister` will fail if the distro is still mounted (rare, but possible).
- **Step 4 before 5/6:** backup *first*. If anything fails partway through, the user has recovery material.
- **Step 9 last:** if the package uninstaller is the orchestrator, it must finish bookkeeping after the per-user state is gone.

---

## 4. Safety gates (mirroring Phase 7 model)

These are non-negotiable. Lifted from `scripts/reset-openclaw-wsl-validation-state.ps1` (lines 36, 151-158, 287, 348):

1. **Hard-locked distro name.** `$script:OpenClawDistroName = "OpenClawGateway"`. No flag, no env var, no override path. `Assert-DestructiveTargetIsAllowed` runs first; refuses if the constant ever drifts. **The uninstaller must never accept a `-DistroName` argument** — same hard-lock as Phase 7.
2. **Backup before remove.** Every directory deletion is preceded by `Copy-Item -Recurse` to `%TEMP%\OpenClawUninstallBackup-<yyyyMMddHHmmss>\` (or `$BackupRoot` override). Print the path on stdout / installer UI. Backup retention: user's responsibility (we don't auto-purge; we don't write `%TEMP%\` cleanup hooks).
3. **No `wsl --shutdown`, ever.** Only `wsl --terminate OpenClawGateway` (Craig-confirmed in `.squad/decisions.md`). Shutdown would impact every other distro on the box.
4. **No `\\wsl$\` / `\\wsl.localhost\` for file ops.** All in-distro reads/writes route through `wsl bash -c ...` (per global Copilot rule + Phase 7 line 15). For uninstall the simplest path is "let `wsl --unregister` delete everything" — we never touch the distro filesystem during uninstall.
5. **Postcondition assertions** (Phase 7 lines 242-282 model). On exit (non-dry-run), throw if:
   - any `OpenClaw*` process is still running
   - `wsl --list --quiet` still contains `OpenClawGateway`
   - `%APPDATA%\OpenClawTray\` exists
   - `%LOCALAPPDATA%\OpenClawTray\` exists
   - autostart registry value still present
   - **Diff of `wsl --list --verbose` before vs after must show only `OpenClawGateway` removed.** Any other distro count change → throw.
6. **Dry-run by default for the standalone script.** `-ConfirmDestructive` flag required to actually remove (mirrors `-ConfirmDestructiveClean` in Phase 7 line 25). The MSIX/MSI uninstaller path skips dry-run because the package manager already gated user consent.
7. **Token / private-key redaction in logs.** Reuse `SecretRedactor.Redact` from `LocalGatewaySetup.cs:665`. Specifically: any uninstall log written to `%TEMP%\OpenClawUninstallBackup-<ts>\uninstall.log` must run identity dir contents through redaction *before* logging filenames or contents (filenames are usually fine; full-file echo is forbidden — we never echo identity files).
8. **No `Stop-Process -Name`.** Always exact PID via `Get-Process | Where-Object { $_.ProcessName -like 'OpenClaw*' }` then `Stop-Process -Id`. Same rule as the global Copilot config.

**Where uninstall differs from Phase 7 reset:**

| Aspect | Phase 7 reset | End-user uninstall |
|---|---|---|
| Goal | Re-validate from clean slate | Permanent removal |
| Default | Dry-run | Confirmed (Add/Remove Programs has already confirmed) |
| Removes app binaries? | No (they stay; only state cleared) | **Yes** |
| Removes autostart reg key? | No | **Yes** |
| Removes Start Menu? | No | **Yes** |
| Backup? | Yes, to `artifacts\reset-backups\<ts>` | Yes, to `%TEMP%\OpenClawUninstallBackup-<ts>` |
| Touches WSL platform? | No | No (same — explicitly out of scope) |

---

## 5. UX surfaces — how does the user trigger it?

Recommendation for **first PR**: ship surface (a) and (c). Defer (b) and (d).

| # | Surface | Ship in first PR? | Notes |
|---|---|---|---|
| (a) | **Windows Add/Remove Programs** (Settings → Apps → OpenClawTray → Uninstall) | **Yes** | Standard. The package manager invokes our uninstaller. This is what 95% of users will use. |
| (b) | Tray menu "Uninstall OpenClaw…" | **No — follow-up** | Convenient but redundant with (a) and (c). Adds a code path that has to handle "uninstall from inside the running app" (self-deletion). |
| (c) | Standalone script `scripts\uninstall-openclaw.ps1` | **Yes** | Power users / IT / repair. Same primitive the package uninstaller calls. Defaults to dry-run. |
| (d) | Group Policy / MDM unattended (`/quiet` MSI flag, MSIX `Remove-AppxPackage` for all users) | **No — follow-up** | Needed for enterprise. Depends on packaging decision (see Q2). |

Confirmation prompt model for surface (c) (script):

```
.\scripts\uninstall-openclaw.ps1                                  # dry-run, prints plan
.\scripts\uninstall-openclaw.ps1 -KeepWslData                     # app-only tier, dry-run
.\scripts\uninstall-openclaw.ps1 -ConfirmDestructive              # full uninstall
.\scripts\uninstall-openclaw.ps1 -ConfirmDestructive -KeepWslData # app-only, real
.\scripts\uninstall-openclaw.ps1 -ConfirmDestructive -ExportDistroTo C:\path\OpenClawGateway.tar
                                                                  # optional pre-backup of distro
```

The MSIX/MSI uninstaller hook calls the same script with `-ConfirmDestructive` (and `-KeepWslData` if the user checked the "keep my data" box).

---

## 6. Edge cases

| # | Case | Mitigation |
|---|---|---|
| E1 | WSL platform uninstalled / disabled out from under us | `wsl --list --quiet` returns non-zero or empty. Skip the distro steps; log "wsl unavailable, skipping distro removal." Continue with Windows-side cleanup. Postcondition for distro = "not present" (which is satisfied by absence). |
| E2 | User renamed `OpenClawGateway` to something else manually | We do **not** chase. The hard-lock is the safety property (§4.1). Surface a warning: "No distro named `OpenClawGateway` found — if you renamed it, unregister it manually with `wsl --unregister <name>`." |
| E3 | Two parallel installs (somehow) created shared state | Per-user model means each user's `%LOCALAPPDATA%\OpenClawTray\` is independent. Distro is per-machine but only one `OpenClawGateway` can exist at a time. Not really achievable in practice. |
| E4 | Uninstall runs mid-pairing (in-flight bootstrap token) | Stop tray process first (step 1) — bootstrap token in memory dies with the process. Backup captures the on-disk identity files (which may not yet contain the new token, harmless). |
| E5 | Another tool is using `wsl.exe` when we unregister | `wsl --unregister` will fail with `WSL_E_DISTRO_NOT_RUNNING` or busy. Retry once after 2s. If still failing, surface error with `aka.ms/wsllogs` link, and **stop** — do NOT proceed to delete identity dirs (user may want to retry without losing tokens). |
| E6 | systemd stop hangs in distro | `wsl --terminate OpenClawGateway` resolves it (kills the VM). We use a 5s timeout on the systemd stop call before falling through to terminate. |
| E7 | Per-user vs per-machine | **Recommend per-user.** Per-machine would require admin elevation and HKLM cleanup, and would force *every* user on the box to repair WSL state. See open question Q1. If we go per-machine: HKLM uninstall key + run uninstaller as SYSTEM, and **explicitly skip** per-user `%APPDATA%`/`%LOCALAPPDATA%` cleanup (we cannot reach other profiles safely). |
| E8 | User reinstalls after app-only uninstall | Tray app starts up, finds `IdentityDataPath` populated and distro registered → skips setup, reconnects via `auth.deviceToken` (per `.squad/decisions.md` "Store role-specific credentials"). This is the *whole point* of supporting tier 1. |
| E9 | Uninstall runs while gateway is still pairing a node | Same as E4 — stop tray first, kill systemd service, terminate distro. Pairing is idempotent: a fresh install re-pairs. |
| E10 | `%LOCALAPPDATA%\OpenClawTray\wsl\OpenClawGateway\` contains the VHD that's still locked | We never `Remove-Item` that subtree directly. `wsl --unregister` is the only deleter. After unregister, `Remove-Item %LOCALAPPDATA%\OpenClawTray\` is safe (the wsl subdir is empty). |
| E11 | User's `%TEMP%` is on a constrained drive (not enough space for backup) | Allow `-BackupRoot <path>` override on the script (mirrors Phase 7's `$BackupRoot` parameter at line 20-22). If backup fails, **abort before destructive steps** — never delete without backup unless user passed `-NoBackup` (advanced flag, undocumented in default UX). |

---

## 7. Testing strategy

Two kinds of tests:

### 7.1 Unit / integration (in `tests/OpenClaw.Tray.Tests/`)

- New filter `LocalUninstallTests`: pure logic for the order-of-operations planner (no shell calls). Mocks `IFileSystem` / `IWslRunner` / `IRegistry`. Verifies:
  - distro name is exactly `OpenClawGateway` and never accepts override
  - backup happens before remove for every directory
  - postcondition checks throw on each missing condition
  - `KeepWslData` skips steps 2-5 but keeps 1, 6-9
- Reuse `SecretRedactor` tests for log-redaction surface.

### 7.2 Validation script (analog to `validate-wsl-gateway.ps1`)

Either:

- **(a) New scenario** in `scripts\validate-wsl-gateway.ps1`: `-Scenario Uninstall`. Existing scenarios: `PreflightOnly` / `UpstreamInstall` / `FreshMachine` / `Recreate`. Add `Uninstall` that runs after `UpstreamInstall` succeeds and asserts postconditions.
- **(b) Separate script** `scripts\test-uninstall.ps1`. Cleaner separation, but duplicates harness boilerplate.

**Recommend (a)** — keeps one harness, one report format, one set of redaction rules.

Postconditions to verify (subset already enforced inside the uninstaller — re-checked in the test from the outside):

- `wsl --list --quiet` does NOT contain `OpenClawGateway`
- `Test-Path %APPDATA%\OpenClawTray` → False
- `Test-Path %LOCALAPPDATA%\OpenClawTray` → False
- `(Get-Process | Where-Object ProcessName -like 'OpenClaw*').Count -eq 0`
- `Get-ItemProperty HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run -Name OpenClawTray` errors
- Add/Remove Programs query returns no `OpenClawTray` entry
- **Diff of `wsl --list --verbose` before-install vs after-uninstall is empty** (every other distro untouched — primary safety property)
- Backup dir exists at `%TEMP%\OpenClawUninstallBackup-<ts>\` and contains both `appdata-OpenClawTray\` and `localappdata-OpenClawTray\` subtrees

---

## 8. Implementation scope — which PR?

**Recommend (B): immediate follow-up PR, not on `feat/wsl-gateway-clean`.**

Reasoning:

- `feat/wsl-gateway-clean` is **plan-complete and reviewed** (Phase 8 APPROVED, 16 commits, +35 tests, 0 regressions). Mike's three blockers are closed. It is in PR-ready state. Adding uninstall now risks gating that PR on net-new design, packaging decisions (see open questions), and a fresh Kranz/Bostick round.
- Uninstall depends on **packaging decisions Mike has not made yet** (Q1, Q2 below). The shape of `scripts\uninstall-openclaw.ps1` differs materially between MSIX and MSI; the `Add/Remove Programs` hook differs even more. Building before deciding wastes work.
- The clean PR only adds the *install* path. Shipping uninstall as PR #2 mirrors how the team already works (Phase 7 reset shipped *after* Phase 6 validation).

**However**, two things should land in PR #1 as a forward bridge:

1. A short note in `docs/wsl-owner-validation.md` (or new `docs/wsl-owner-uninstall.md`) saying: "End-user uninstall is tracked as a follow-up PR; for now, validators use `scripts/reset-openclaw-wsl-validation-state.ps1` to clear state."
2. A `// TODO(uninstall): see .squad/decisions/inbox/aaron-uninstall-plan.md` comment in `LocalGatewaySetup.cs` near `ResolveInstallLocation` so we don't lose the design when it's time to implement.

If Mike disagrees and wants (A), the new commits on `feat/wsl-gateway-clean` would be:

- `feat(scripts): port uninstall-openclaw.ps1` (new, ~400 LOC mirroring reset script structure)
- `feat(tray): UninstallPlanner + IWslRunner shim` (new tests)
- `feat(scripts): add Uninstall scenario to validate-wsl-gateway.ps1`
- `docs(wsl): add wsl-owner-uninstall.md`

That's 4 new commits and ~50 new tests. Doable, but bumps the PR from "ship now" to "ship in 1-2 days."

---

## Open questions for Mike

Please answer before implementation kicks off:

| # | Question | Why it matters |
|---|---|---|
| Q1 | **Per-user or per-machine install?** Recommend per-user (HKCU, `%LOCALAPPDATA%`, no admin needed for install or uninstall). | Drives whether HKLM keys exist and whether uninstall needs elevation. Affects edge-case E7. |
| Q2 | **MSIX, MSI, or NSIS/Inno setup?** I assume MSIX (modern, per-user friendly, integrates with Add/Remove Programs and Microsoft Store path). | Drives the `Remove-AppxPackage` vs `msiexec /x` shape and whether we get free Add/Remove Programs registration. |
| Q3 | **Does uninstall offer "keep my WSL data for reinstall"?** Recommend **yes**, unchecked by default. | Tier 1 vs Tier 2. Affects UX dialog and script flag. |
| Q4 | **Tray menu "Uninstall…" item in v1?** Recommend **no** (defer). | Avoids self-uninstall complexity in first cut. |
| Q5 | **`wsl --export OpenClawGateway` pre-backup option?** Recommend **yes, opt-in via `-ExportDistroTo <path>`** for power users. | Lets advanced users keep a portable distro snapshot before uninstall. Default off (large file, slow). |
| Q6 | **Telemetry on uninstall?** (e.g., a single ping "uninstall completed" before the binaries go away.) | Privacy / product question. Default off unless Mike says otherwise. |
| Q7 | **Where does the standalone script live post-install?** I assume `%LOCALAPPDATA%\Programs\OpenClawTray\uninstall.ps1` or invoked via `OpenClawTray.exe --uninstall`. | Affects discoverability and how IT scripts call it. |
| Q8 | **Backup retention?** I assume we never auto-purge `%TEMP%\OpenClawUninstallBackup-*\` (Windows handles `%TEMP%` cleanup). | Confirm — alternative is a 30-day self-clean. |

---

## Cross-references

- Inspiration: `scripts/reset-openclaw-wsl-validation-state.ps1` (Phase 7, commit `dbd7708`) — safety gate model, backup-before-remove, hard-locked distro constant.
- Decisions baseline: `.squad/decisions.md` — "Dedicated Ubuntu WSL instance", "Craig Loewen's WSL Answers", "winget Research Consolidated."
- Data path constants: `src/OpenClaw.Tray.WinUI/App.xaml.cs:151-164`.
- Autostart key: `src/OpenClaw.Tray.WinUI/Services/AutoStartManager.cs:11-12`.
- Distro install location: `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:639-649`.
- Service / linger: `LocalGatewaySetup.cs:704, 718, 722, 864`.
- Redactor: `LocalGatewaySetup.cs:665` (uses `SecretRedactor.Redact`).
