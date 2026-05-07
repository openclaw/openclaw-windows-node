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

**WSL Gateway Uninstall (feat/wsl-gateway-uninstall) — Commits 1+2 COMPLETE (2026-05-08):**  
Executed Kranz's uninstall plan v3 commits 1 and 2. Baseline merge of pr-241-feedback-fixes into worktree required manual copy of 7 untracked files (LocalGatewaySetup.cs, LocalGatewayLifecycle.cs, scripts, tests, RootfsArtifactManifest.cs, WslGatewayContracts.cs) and resolution of 4 API incompatibilities. Commit 1 removes OPENCLAW_WSL_INSTALL_LOCATION env-var from LocalGatewaySetupRuntimeConfiguration while retaining InstanceInstallLocation on options as test seam. Commit 2 creates scripts/_uninstall-helpers.ps1 with Test-IsOpenClawOwnedDistroName (moved from reset script), plus new Invoke-WslCommand, Stop-OpenClawProcessByPid, Assert-DryRunGate, Add-Step helpers. Fixed 12 pre-existing test failures from baseline merge (localization duplicates, test-code mismatches, env-isolation). All 447 tray tests pass. Next: commits 3+ (uninstall implementation).  
Previous: Planning-complete (2026-05-07). Two Windows data roots identified: roaming (%APPDATA%\OpenClawTray) + local (%LOCALAPPDATA%\OpenClawTray). Uninstall order: service stop → terminate → unregister → cleanup. Awaiting Mike's answers to 8 design questions (packaging scope, per-user install, wsl --export backup, mcp-token.txt delete, logs/exec-policy cleanup, EnableMcpServer flag, AutoStart removal).

## Test Results (Latest)

- **Shared Tests:** passing (all)
- **Tray Tests:** 447/447
- **Build:** PASS (2026-05-08, feat/wsl-gateway-uninstall after commits 1+2)

## Deferred & Open

- Underlying tray ctor exception (env-specific to fresh box) — defensive try/catch will surface in openclaw-tray.log
- Uninstall feature: 8 open design Qs for Mike (see Active Workstreams)
