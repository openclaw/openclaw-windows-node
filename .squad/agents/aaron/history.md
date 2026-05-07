# Project Context

- **Project:** openclaw-windows-node (Windows tray app + WSL gateway)
- **Created:** 2026-05-04
- **User:** Mike Harsh

## Core Context

Clean WSL gateway rebuild on sibling worktree ..\openclaw-wsl-gateway-clean (branch eat/wsl-gateway-clean from upstream/master 871b959). Prototype worktree openclaw-windows-node is reference-only.

Read on first spawn: .squad/identity/now.md, .squad/prototype-reference.md.

## Recent Updates

📌 Team hired 2026-05-04. Universe: Apollo 13.

## Executive Summary — Key Patterns & Decisions

### Phases 1–8 COMPLETE [Landed 16 commits since 871b959]

**Architectural Phases:**
- **P1 DeviceIdentity:** operator/node-token accessors; strict role-based whitelist
- **P2 Gateway/Node clients:** bootstrap setup-code auth + stored-token reconnect; role-specific hello-ok
- **P3 LocalGatewaySetup:** Full WSL state machine (15 stages); loopback-only networking
- **P4 App wiring:** IdentityDataPath setup; removed PreserveWorkerData
- **P6–P8:** Validation script, reset script, documentation

**Key empirical finding:** wsl --install Ubuntu-24.04 10/10 vs winget install 0/10. Production path locked.

### Bug Fixes & Learnings (Phases 1–8 to present)

**Bug #1 — PairOperator handshake (2026-05-04):**  
Root: Fresh loopback gateway auto-registers bootstrap-token connect as *pending* operator pairing, rejects same connect with device-auth-invalid. Fix in LocalGatewaySetup.cs: new IPendingDeviceApprover seam → wsl invokes openclaw devices approve --latest. Gated on loopback + bootstrap + no existing pairing. Tests: 10 new (OperatorPairingApprovalTests.cs). Tray 493/493.

**Bug #2 — PairOperator handshake + FunctionalUI RadioButtons (2026-05-06):**  
Stale-build lesson: check DLL LastWriteTime, not commit date. Three genuine wizard behavioral defects: (1) wizard.start on transient disconnect creates parallel session (should wizard.status first); (2) RadioButtons re-bind on every render → visual flash + apparent double-click required (fix: cache options in UseState); (3) LocalSetupProgressPage phase updates stuck at stage 0 (symptom: UI ProgressRing spinning but not advancing).

**Bug #3 — PR #274 P0 Tray Init Regression (2026-05-07):**  
Root: App.OnLaunched is sync void (fire-and-forget). InitializeTrayIcon deferred past RequiresSetup branch → tray icon ctor throws while wizard displayed → no tray chrome. Core principle: **Tray is application chrome, must outlive any wizard failure.** Fix: Reorder InitializeTrayIcon BEFORE RequiresSetup. Wrap ShowOnboardingAsync in try/catch + Logger.Error. Commit 3e4c217. Defensive pattern: async-void lifecycle methods need critical object initialization FIRST, before conditional branches. Always wrap wizard/setup flows in try/catch.

### Active Workstreams

**WSL Gateway Uninstall — Commit 3 COMPLETE (2026-05-08, SHA bc08f11):**  
`LocalGatewayUninstall` core engine + unit tests.  
- NEW `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewayUninstall.cs` (~460 lines): 13-step idempotent uninstall sequence; DryRun=true safety default; `Build()` factory; registry helpers OS-guarded with `OperatingSystem.IsWindows()` (CA1416 compliance on net10.0).  
- MOD `src/OpenClaw.Shared/DeviceIdentity.cs`: Added `TryClearDeviceToken(string dataPath, IOpenClawLogger?)` — nulls DeviceToken + DeviceTokenScopes, preserves file + mcp-token.txt unconditionally (v3 §F).  
- MOD `tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj`: added `<Compile>` link for `LocalGatewayUninstall.cs`.  
- NEW `tests/OpenClaw.Tray.Tests/LocalGatewayUninstallTests.cs` (~380 lines): 20 `[WindowsFact]` tests.  
Results: Build PASS, Tray 632/640 pass (8 pre-existing localization fails).

**WSL Gateway Uninstall (feat/wsl-gateway-uninstall) — Re-baseline onto PR #274 COMPLETE (2026-05-07):**  
Executed re-baseline from origin/master to PR #274 head (`3e4c217`). Preserved 148 artifacts (MSIX validation script, .squad/ decision inbox/agent histories). Hard reset answered 'n' to all worktree .squad untracked deletion prompts. 3 clean commits landed:
- `cd1a83b` — refactor(setup): remove OPENCLAW_WSL_INSTALL_LOCATION env-var binding (LocalGatewaySetup.cs, LocalGatewaySetupTests.cs, validate-wsl-gateway.ps1)
- `83eadcf` — refactor(scripts): extract shared uninstall helpers into _uninstall-helpers.ps1
- `22bda40` — chore(squad): restore session artifacts (MSIX script + .squad/ files)  
Key structural diff on PR #274 base: `LocalGatewaySetupRuntimeConfiguration` already cleaned of TrustedSigningKeyId/TrustedSigningPublicKeyPath/RootfsManifestPath fields. App.xaml.cs already does not pass instanceInstallLocation. Commit 1 scope: 5 removals only (record field, constant, FromEnvironment line, factory param, factory assignment). Pre-existing failures: 8 LocalizationValidationTests failing with `OPENCLAW_REPO_ROOT` env not set (worktree environment issue, NOT introduced by this work). Build: PASS. Tray.Tests: 609/617 pass (8 pre-existing localization fails). Shared.Tests: pass (exit 0, ~853 test methods, 2 skipped). Commits 3–7 cleared to start.

**WSL Gateway Uninstall (feat/wsl-gateway-uninstall) — Commits 1+2 COMPLETE (2026-05-08, OLD BASELINE):**  
[Superseded by re-baseline above. Prior base was origin/master with pr-241-feedback-fixes merge — UNAUTHORIZED scope expansion. Reset and redone on PR #274 head per Mike's Path A decision.]

## Test Results (Latest)

- **Shared Tests:** passing (exit 0, ~853 test methods, 2 with Skip; 2026-05-07)
- **Tray Tests:** 632/640 pass — 8 pre-existing LocalizationValidationTests fails (OPENCLAW_REPO_ROOT not set in worktree) — updated after Commit 3
- **Build:** PASS (2026-05-07, feat/wsl-gateway-uninstall re-baseline onto PR #274)

## Deferred & Open

- Underlying tray ctor exception (env-specific to fresh box) — defensive try/catch will surface in openclaw-tray.log
- Uninstall feature: 8 open design Qs for Mike (see Active Workstreams)


### WSL Gateway Uninstall — Commit 5 COMPLETE (2026-05-08, SHA 0a78b0d)

**Files touched:**
- `src/OpenClaw.Tray.WinUI/App.xaml.cs` — `--uninstall` CLI interception in OnLaunched (early, before mutex), `AttachConsole` P/Invoke, `RunCliUninstallAsync()`, `CliRedact()`
- `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewayUninstall.cs` — Step 5a VHD parent-dir cleanup, Step 8a run.marker cleanup, `VhdDirAbsent` postcondition
- `installer.iss` — `[Files]` entry for `scripts\Uninstall-LocalGateway.ps1`, `[UninstallRun]` entry
- `scripts/Uninstall-LocalGateway.ps1` (NEW) — Inno helper; thin CLI wrapper, always exits 0
- `scripts/validate-wsl-gateway-uninstall.ps1` — `-NoCli`/`-ExePath` params, `Get-TrayExePath`, CLI delegation in Full mode, `vhd_dir_absent` postcondition
- `docs/uninstall-portable.md` (NEW) — portable ZIP uninstall documentation
- `docs/uninstall-msix.md` (NEW) — MSIX uninstall feasibility verdict (NOT feasible)
- `tests/OpenClaw.Tray.Tests/LocalGatewayUninstallTests.cs` — Tests 21–25 (VHD dir cleanup × 3, run.marker × 2)

**Test results:** 645 total (+5 new), 636 passed, 8 pre-existing localization fails, 1 pre-existing flaky `SettingsManagerIsolation` (passes in isolation).

**Key empirical finding — run.marker:** Written ONLY in `App.xaml.cs` constructor (`MarkRunStarted`); no setup-side writer. Stale markers possible after crash. Step 8a handles idempotently (skip-if-absent).

**MSIX verdict:** `runFullTrust` MSIX has NO supported `CustomUninstall` extension point. In-tray button is the only safe uninstall path for MSIX installs. Documented in `docs/uninstall-msix.md`.

**CLI flag contract for Commit 7 (Bostick):**
```
OpenClaw.Tray.WinUI.exe --uninstall --confirm-destructive [--dry-run] --json-output <path>
```
Exit 0 = success, non-zero = failure. Output JSON written to `<path>`; any access tokens redacted. Stdout also printed (attached console).

### WSL Gateway Uninstall — Commit 6 COMPLETE (2026-05-07, SHA 1cfc1ee)
scripts/validate-wsl-gateway-uninstall.ps1 — 1000 lines.

**Smoke-test results (this machine, OpenClawGateway IS registered):**
- -Mode PreflightOnly → verdict PreflightOnly, exit 0. Distro found registered; pre-state.json written. ✅
- -Mode Full -DryRun → verdict DryRunComplete, exit 0. All 13 steps recorded as DryRun; no state mutated. ✅
- -Mode PostconditionOnly → verdict PARTIAL, exit 1. Correct: distro registered + setup-state.json present = 2 failing postconditions. ✅
- -Help → usage printed cleanly, exit 0. ✅
- -Mode Full (no -ConfirmDestructive) → exit 2. ✅
- -DistroName Ubuntu → exit 2. ✅
- PSScriptAnalyzer not installed; parse check clean (Get-Command no errors).
