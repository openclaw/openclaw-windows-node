# Squad Decisions Archive

Archived 2026-05-04 | Entries from Phase 0 planning and early team decisions that were resolved, superseded, or preserved for historical reference.

## Phase 0 Planning (2026-05-04) — Now Resolved

### Early Architectural Assumptions

Kranz filed initial porting plan with 5 open questions for Mike. All answered by 2026-05-04T10:15-07:00:

1. **Clean worktree remote** → Resolved: `origin` (no `upstream` exists)
2. **Craig confirmation status** → Resolved: ✅ Craig answered all questions (see coordinator-craig-wsl-answers decision)
3. **WSL worker requirement** → Resolved: Windows tray node only (Mike decision)
4. **Offline/from-file fallback** → Resolved: Not needed per Craig (modern WSL distributions not via Store)
5. **wsl-gateway-rootfs.md disposition** → Resolved: Omit from clean PR (historical reference only)

### Initial Conditional Architecture Statements

Decisions.md lines 5–39 contained conditional language ("Assume Craig confirms...") for WSL direction, networking, and lifecycle. All conditions were satisfied by Craig's verbatim answers and Mike's Phase-0 decisions. Conditional language removed; see `coordinator-craig-wsl-answers` and `kranz-phase3-revised-craig-answers` for authoritative final specs.

### Mattingly's Onboarding Layout Contract (2026-05-04)

Mattingly filed full pre-XAML layout contract for SetupWarning and LocalSetupProgress pages with 6 open questions (OQ-1 through OQ-6). OQ-1 answered by Mike (fold security notice, delete WelcomePage). OQ-2–OQ-6 remain open for Phase 5 scope. Layout structural contract (Grid rows/cols, localization keys, phase mapping) is now baked into kranz-phase3-revised-craig-answers final phase surfacing spec.

### Bostick Phase-0 Baseline (2026-05-04 10:00 UTC-7)

Clean worktree baseline captured:
- Build: ✅ 57.14s, no errors
- Shared.Tests: 1172 total (1151p, 1f [pre-existing ReadmeValidationTests], 20s)
- Tray.Tests: 407p, 0f

Baseline locked for Phase 1+ regression detection. Pre-existing failure in Shared.Tests (ReadmeValidationTests) is not a blocker.

---

**Archive decision:** All Phase 0 planning entries have been resolved by final verdicts and Michael's decisions. Preserved here for historical context; canonical reference should use kranz-phase3-revised-craig-answers, coordinator-craig-wsl-answers, and Phase 2 closures from decisions.md.

---

## Phase 1 + Phase 2 Closure (2026-05-04, archived 2026-05-04 round 6)

Archived because superseded by Phase 3 completion and Phase 4 landing.

### Phase 1 Reviewer Verdict (Kranz @ 95911b8) — CONDITIONAL APPROVE
- Role-specific operator/node token storage and persistence present.
- DeviceIdentity integration tests 17/17 with `OPENCLAW_RUN_INTEGRATION=1`.
- Punch-list deferred to Phase 2 closure (non-empty node-scope persistence + unknown-role handling) — **closed in commit 3ae03d3**.

### Phase 1 Independent Verification (Bostick @ 95911b8)
- Phase 1 approval stood. Surfaced env-var dependency that became the Reporting Standard (still canonical).
- LocalizationValidationTests + ReadmeValidationTests failures identified as environmental (`OPENCLAW_REPO_ROOT` discovery), not code defects.

### Phase 1 Punch-List Closure (Aaron, 3ae03d3)
- Implemented Option B: role-string APIs convert via private `DeviceTokenRole` enum (case-sensitive `"operator"` / `"node"` whitelist).
- Added non-empty node-scope persistence + invalid-role exception coverage.

### Phase 2.1 GatewayClient Port (Aaron, b20b5ce)
- Bootstrap setup-code consumption via `auth.bootstrapToken`; stored operator reconnect via `auth.deviceToken`.
- Role-specific token handoff from `hello-ok.auth` (incl. node-token).
- `_operatorReadScopeUnavailable` fallback ported as-is (compatibility uncertain — flagged for revisit if needed).
- Not ported: WebBridge relay, prototype UI-automation hooks.

### Phase 2.2 WindowsNodeClient Port (Aaron, b69202d)
- Node reconnect via stored `NodeDeviceToken` → `auth.deviceToken`.
- Node-token storage via `StoreDeviceTokenForRole("node", ...)`; startup credential resolution prefers stored node token, then gateway token, then bootstrap token.
- Public API: kept `HasStoredNodeDeviceToken(...)`.

### Phase 2 Reviewer Verdict (Kranz @ b69202d) — APPROVE
- Bootstrap, stored-token reconnect, role-specific handoff, redaction all verified.
- No `\\wsl$` / `\\wsl.localhost` paths.
- Phase 3 unlocked.

### Phase 2 Verification (Bostick @ b69202d)
- Build PASS; Shared.Tests 1179/1180 (1 pre-existing failure: `ReadmeAllowCommandsJsonExample_IsValid`); Tray.Tests 407/407 with `OPENCLAW_REPO_ROOT` set.
- Locale flap diagnosed as env-var dependency (test `GetRepositoryRoot()` walks from `AppContext.BaseDirectory`); fix is to set `OPENCLAW_REPO_ROOT` in build/CI.

---

## Phase 3 (2026-05-04, archived 2026-05-04 round 6)

Archived because Phase 3 commit landed, was reviewed (CONDITIONAL APPROVE), independently verified, and the conditions (`PreserveWorkerData` removal + distro-name override gating) were closed in Phase 4 commit `4ab1ec6`.

### Phase 3 Plan — Revised Against Craig's Authoritative Answers (Kranz)
Authoritative deltas now embedded in code:
- Install: `wsl --install Ubuntu-24.04 --name OpenClawGateway --location <appdata>\OpenClawTray\wsl --no-launch --version 2`. No `--web-download` / `--from-file` / offline fallback.
- Networking: loopback ONLY; resolver returns `http://localhost:{port}`.
- Trust `wsl --install` exit code (no postcondition-on-hang fallback).
- Config: `/etc/wsl.conf` (automount/interop/appendWindowsPath = false) + `/etc/wsl-distribution.conf` (systemd, default user openclaw).
- Repair: `wsl --terminate OpenClawGateway` only; never global `wsl --shutdown`.
- Diagnostics: `aka.ms/wsllogs` link; no internal log scraping.
- Lifecycle: `loginctl enable-linger openclaw` + tray-owned keepalive.
- Worker phases removed (Mike: Windows tray node only).

Phase enum kept (19): NotStarted, Preflight, ElevationCheck, EnsureWslEnabled, CreateWslInstance, ConfigureWslInstance, InstallOpenClawCli, PrepareGatewayConfig, InstallGatewayService, StartGateway, WaitForGateway, MintBootstrapToken, PairOperator, CheckWindowsNodeReadiness, PairWindowsTrayNode, VerifyEndToEnd, Complete, Failed, Cancelled. Dropped: VerifyRootfsArtifact, ImportDistro, VerifyDistro, StartWorker, PairWorker, LocalOnlyComplete.

Progress UI mapping (for Mattingly Phase 5):
| UI Stage | Internal Phase(s) |
|---|---|
| Checking system | Preflight, ElevationCheck, EnsureWslEnabled |
| Installing Ubuntu | CreateWslInstance |
| Configuring instance | ConfigureWslInstance |
| Installing OpenClaw | InstallOpenClawCli |
| Preparing gateway | PrepareGatewayConfig, InstallGatewayService |
| Starting gateway | StartGateway, WaitForGateway |
| Generating setup code | MintBootstrapToken |
| Connecting operator | PairOperator |
| Connecting node | CheckWindowsNodeReadiness, PairWindowsTrayNode |
| Verifying | VerifyEndToEnd |

### Phase 3 LocalGatewaySetup Port (Aaron, 98bdf77)
- Created `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs`; moved `SetupCodeDecoder.cs` (kept `OpenClawTray.Onboarding.Services` namespace); created `tests/OpenClaw.Tray.Tests/LocalGatewaySetupTests.cs`.
- Loopback-only resolver; removed WSL-IP fallback, `gateway.bind` writes, LAN/auto bind, worker phases, post-clone repairs.
- Repair primitive: `wsl --terminate OpenClawGateway` (no global shutdown).
- Runtime env bindings retained: `OPENCLAW_WSL_DISTRO_NAME`, `OPENCLAW_WSL_INSTALL_LOCATION`, `OPENCLAW_WSL_ALLOW_EXISTING_DISTRO`.
- Validation (env: `OPENCLAW_RUN_INTEGRATION=1`, `OPENCLAW_REPO_ROOT=...openclaw-wsl-gateway-clean`): build PASS; filter 33/33; Tray 426/426; Shared 1180/1180.

### Phase 3 Reviewer Verdict (Kranz @ 98bdf77) — CONDITIONAL APPROVE
All architectural guardrails verified (install command, loopback-only, wsl.conf/wsl-distribution.conf, instance-scoped terminate, no UNC WSL paths, redaction, `aka.ms/wsllogs`, lifecycle preserves linger + keepalive). Two punch-list items left (BOTH closed in Phase 4 commit `4ab1ec6`):
1. Remove `PreserveWorkerData` / `worker_data_preserved` worker vocabulary from lifecycle removal API.
2. Gate distro-name override (`OPENCLAW_WSL_DISTRO_NAME`) to test/dev only; lock shipping path to `OpenClawGateway`.

### Phase 3 Independent Verification (Bostick @ 98bdf77) — CONFIRMED
Aaron's claim fully confirmed. Filter 33/33, Tray 426/426, Shared 1180/1180, build PASS — all with required env vars set. LocalizationValidationTests hypothesis re-confirmed (env-var dependency, not Phase 3 regression).


## Round-9 Archive (2026-05-04) — Superseded Phase Author + Verdict Entries

The following entries were consolidated to `decisions.md` round-9 condensed forms or fully superseded by later phases. Preserved here for audit.

### Mike's Three Phase-0 Decisions (now baked into Active Canonical)

**Date:** 2026-05-04 — **By:** Mike Harsh

- Craig confirmation: ✅ Craig answered full `wsl-owner-open-issues.md` set. Phase 3 unblocked.
- WSL worker requirement: Windows tray node ONLY. Do NOT port `StartWorker`/`PairWorker`.
- Welcome page disposition: REMOVE existing `WelcomePage`. Fold security notice into `SetupWarningPage` body. Fork page is page 0 of onboarding.

### Literature winget vs `wsl --install` (Aaron, 2026-05-04T12:41:45)

Literature-only research. Recommendation: stay with `wsl --install`. **Superseded by:** Aaron-8 empirical (Phase 6 entry, decisions.md) and Aaron-9 deeper hypothesis test (decisions.md). Original key finding preserved: `Canonical.Ubuntu.2404` APPX has no `--name`/`--location` semantics; `wsl --install` is the only single primitive for app-owned named instances.

### Phase 4 — App Wiring + Phase 3 Punch-List Closure (Aaron, 2026-05-04T13:10:27)

Commits `4ab1ec6` (punch list) + `8cc32c6` (Phase 4 wiring) on `feat/wsl-gateway-clean`.

**Task A — Phase 3 punch list:** `PreserveWorkerData` / `worker_data_preserved` / `workerData` vocabulary fully deleted (`LocalGatewayRemoveRequest` parameter removed; `LocalGatewayLifecycleManager.RemoveAsync` step write removed). Distro-name override (`OPENCLAW_WSL_DISTRO_NAME`) gated behind `#if DEBUG || OPENCLAW_TRAY_TESTS`; Release returns constant `"OpenClawGateway"` regardless of caller input or env.

**Task B — Phase 4 wiring:** `App.CreateLocalGatewaySetupEngine()` factory in `App.xaml.cs:55-62`. `IdentityDataPath` (`%APPDATA%\OpenClawTray`, override via `OPENCLAW_TRAY_APPDATA_DIR`) at `App.xaml.cs:152-161`. `NodeService` constructor accepts optional `identityDataPath` (`NodeService.cs:151,157`); falls back to `dataPath`. `WindowsNodeClient` (`NodeService.cs:179`) and `StartupSetupState` callsites (`App.xaml.cs:1082, 1167`) switched to `IdentityDataPath` — closes prototype operator/node identity divergence. `DataPath` (`%LOCALAPPDATA%\OpenClawTray`) preserved for crash logs / run markers / exec-approval policy / diagnostics.

Stripped: prototype env-var rootfs/manifest overrides, dev-shim auto-accept, worker-in-WSL wiring.

Validation (`OPENCLAW_REPO_ROOT` + `OPENCLAW_RUN_INTEGRATION=1`): `./build.ps1` PASS, Tray.Tests 426/426/0/0, Shared.Tests 1180/1180/0/0.

Diff vs `98bdf77..HEAD`: `LocalGatewaySetup.cs` +16/-5; `App.xaml.cs` +62/-4; `NodeService.cs` +12/-1.

### Phase 4 Reviewer Gate — APPROVE (Kranz) + Independent Verification (Bostick) — 2026-05-04T13:25:00

Kranz APPROVE on `4ab1ec6` + `8cc32c6`. No punch list. Phase 5 unblocked unconditionally. Worker vocabulary strip clean (0 hits). Distro override gated at three independent gates (`LocalGatewaySetupRuntimeConfiguration.FromEnvironment`, `LocalGatewaySetupEngineFactory.ResolveDistroName`, `OPENCLAW_TRAY_TESTS` defined only in tests csproj). Prohibited additions all empty: `OPENCLAW_WSL_ROOTFS_*`, `TrustedSigningKeyId`, `RootfsArtifactManifest`, `WslRootfsOverlay`, `\\wsl$`, `\\wsl.localhost`, dev-shim auto-accept, `StartWorker`, `PairWorker`. Diff minimal/surgical. Bostick verification (env vars set): build PASS 31.29s, filter 17/17/0/0, Tray 426/426/0/0, Shared 1180/1180/0/0 — matches Aaron exactly.

### Phase 5 — Onboarding UX (SetupWarning + LocalSetupProgress) — Mattingly — 2026-05-04

Commits `43035ca`..`99f5107` over Phase 4 tip `8cc32c6`. Created `Onboarding/Pages/SetupWarningPage.cs` and `Onboarding/Pages/LocalSetupProgressPage.cs`. Modified `OnboardingState.cs`, `OnboardingApp.cs`, `OnboardingWindow.cs`, `OnboardingStateTests.cs`, `ConnectionPageTopologyTests.cs`. Deleted `WelcomePage.cs`.

State: `OnboardingRoute` removed `Welcome`, added `SetupWarning` and `LocalSetupProgress`. New `SetupPath { Local, Advanced }`. `OnboardingState.SetupPath` (`SetupPath?`), `event AdvanceRequested`, `RequestAdvance()`. Default `CurrentRoute = SetupWarning`. `GetPageOrder()` branches on `SetupPath`; null defaults to Local for indicator stability; Next disabled until SetupPath set.

Layout: SetupWarning MaxWidth=460 (lobster, centered title, body with folded ⚠️ security notice + Advanced-setup pointer, accent "Set up locally" button MinWidth=200 Height=44, hyperlink "Advanced setup"). LocalSetupProgress MaxWidth=520 (lobster, title, subtitle, 7-stage list ✓/spinner/○, error/retry row).

Visible 7 phases: Checking system, Installing Ubuntu, Configuring instance, Installing OpenClaw, Preparing gateway, Starting gateway, Generating setup code. Hidden subtitle-only: ElevationCheck, PairOperator, CheckWindowsNodeReadiness, PairWindowsTrayNode, VerifyEndToEnd. Auto-advance on `Complete` after 1s. Retry on FailedRetryable; `aka.ms/wsllogs` hint on FailedTerminal.

Visual-test hooks: `OPENCLAW_ONBOARDING_START_SETUP_PATH=Local|Advanced`; `OPENCLAW_ONBOARDING_START_ROUTE=LocalSetupProgress` auto-sets `SetupPath=Local`; `OPENCLAW_VISUAL_TEST_LOCAL_SETUP` (only with `OPENCLAW_VISUAL_TEST=1`) renders synthetic engine state.

Validation: build PASS, Tray 434/434 (+8 vs Phase 4's 426), Shared 1180/1180. Screenshots `phase5-warning/page-02.png` and `phase5-progress-active/page-02.png` verified.

### Phase 5 Reviewer Gate — CONDITIONAL APPROVE (Kranz) + Verification (Bostick) — 2026-05-04T13:55:00

HEAD `99f5107`. Punch list (fast-follow, NOT Phase 6 blocker): (1) trim subtitle "This usually takes a few minutes."; (2) Mike question — Next button mid-install policy; (3) step-indicator default-7 acceptable; (4) static `s_engine`/`s_runTask`/`s_advanceFiredForCompletion` need reset comment. Orphan `Onboarding_Welcome_*` resw entries in 5 locales (~45 lines): fast-follow. Hard-coded English copy: post-PR i18n landing.

**Punch list items 1 + 2 closed by Mattingly Phase 5 fast-follow @ `32cbeae` (decisions.md round-9). Items 3 + 4 stand as-is.**

Bostick verification (env vars set): build PASS 28.0s; Tray 434/434/0/0, Shared 1180/1180/0/0; onboarding-filter `OnboardingState|SetupWarning|LocalSetupProgress` 32/32/0/0. Both screenshots viewed and confirmed match contract.

## Round-9–10 Phase Cycles (archived 2026-05-04T22:15Z) — Phases 5 fast-follow / 6 / 7

Round-by-round phase landings on `feat/wsl-gateway-clean`. All approved by Kranz (round-9 & round-10) and independently verified by Bostick. Final round-10 PLAN COMPLETE entry remains in active `decisions.md`.

### Phase 6 — `validate-wsl-gateway.ps1` port (Aaron, HEAD `8060ae9`)

`scripts/validate-wsl-gateway.ps1` (~620 lines, vs prototype 1537). Scenarios kept (4): `PreflightOnly` / `UpstreamInstall` / `FreshMachine` / `Recreate`. Stripped: `BuildRootfs`, `InstallOnly`, `Smoke`, `Full`, `Loop` scenarios; `-BuildDevRootfs`, `-BaseRootfsPath`, `-GatewayPackagePath`, `-UseExistingManifest`, `-RootfsPath`, `-AllowUnsignedDevArtifact`, `-SigningKeyId`, `-PublicKeyPath`, `-AllowNonStandardDistroNameForDestructiveClean`, `-NetworkingMode`, `-LoopMode`, `-RequireWorkerPairing`, `-CleanOpenClawState`, `-GoSkillProofCommand`, `-RequireGoSkillProof`. Networking: loopback only `:18789`. UI hook: drives `OnboardingSetupLocal`; polls `setup-state.json`. Diagnostics surface `aka.ms/wsllogs`. Redaction at all token-emitting sinks. Build PASS · Tray 434/434 · Shared 1180/1180. **Kranz APPROVE; Bostick verified.**

### Phase 7 — `reset-openclaw-wsl-validation-state.ps1` port (Aaron, HEAD `dbd7708`)

388 lines new file. `-AllowNonStandardDistroNameForDestructiveClean`, `-CleanOpenClawState`, `-DistroName` all stripped. Distro hard-coded `$script:OpenClawDistroName = "OpenClawGateway"`. Dry-run default; `Backup-Directory` runs Copy-then-Remove. Lifecycle uses `wsl --terminate` then `wsl --unregister` only. Build PASS · Tray 434/434 · Shared 1180/1180. **Kranz APPROVE, no fast-follow; Bostick dry-run hard-confirmed: WSL state SHA256 identical before/after, escape hatch refused for `-Force` / `-AllowNonStandardDistroNameForDestructiveClean` / `-DistroName Foo`.**

### Phase 5 fast-follow — time-estimate drop + orphan Welcome resw cleanup (Mattingly, HEAD `32cbeae`)

Closes Phase-5 verdict punch-list items 1 & 2 (items 3 i18n + 4 Next-button policy deferred). `LocalSetupProgressPage.cs:127` fallback subtitle trimmed to `"Setting up your local OpenClaw gateway."` (no time estimate). 9 `Onboarding_Welcome_*` resw entries removed × 5 locales = 45 total. Diffstat: 6 files, +50/-185. Build PASS · Tray 434/434 · Shared 1180/1180; `LocalizationValidationTests` parity preserved. Screenshot: `visual-test-output/phase5-followup/page-02.png`.
