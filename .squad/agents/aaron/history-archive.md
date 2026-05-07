# Project Context

- **Project:** openclaw-windows-node (Windows tray app + WSL gateway)
- **Created:** 2026-05-04
- **User:** Mike Harsh

## Core Context

Clean WSL gateway rebuild on sibling worktree `..\openclaw-wsl-gateway-clean` (branch `feat/wsl-gateway-clean` from upstream/master `871b959`). Prototype worktree `openclaw-windows-node` is reference-only.

Read on first spawn: `.squad/identity/now.md`, `.squad/prototype-reference.md`.

## Recent Updates

📌 Team hired 2026-05-04. Universe: Apollo 13.

## Learnings

### Summary — Phases 1–8 PLAN COMPLETE [Scribe-compacted 2026-05-04T19:35-07:00 / round 13]

**Phases 1–8 all landed and APPROVED on `feat/wsl-gateway-clean` (16 commits since baseline `871b959`):**

- **P1 DeviceIdentity** `95911b8`+`3ae03d3`: operator/node-token accessors with strict `DeviceTokenRole` whitelist.
- **P2 Gateway/Node clients** `b20b5ce`+`b69202d`: bootstrap setup-code (`auth.bootstrapToken`), stored-token reconnect (`auth.deviceToken`), role-specific hello-ok handoff. No WebBridge.
- **P3 LocalGatewaySetup** `98bdf77`: ported full WSL setup state machine (NotStarted→Preflight→ElevationCheck→EnsureWslEnabled→CreateWslInstance→ConfigureWslInstance→InstallOpenClawCli→PrepareGatewayConfig→InstallGatewayService→StartGateway→WaitForGateway→MintBootstrapToken→PairOperator→CheckWindowsNodeReadiness→PairWindowsTrayNode→VerifyEndToEnd→Complete/Failed/Cancelled). Removed: rootfs/import/worker/LocalOnlyComplete. Loopback-only networking. No `\\wsl$`, no `--shutdown`, no `gateway.bind`.
- **P4 App wiring** `4ab1ec6`+`8cc32c6`: removed PreserveWorkerData; gated distro override behind DEBUG/TRAY_TESTS; added `App.IdentityDataPath` (`%APPDATA%\OpenClawTray`).
- **P6 Validation script** `8060ae9`: `scripts/validate-wsl-gateway.ps1` (~620 lines). Scenarios: PreflightOnly/UpstreamInstall/FreshMachine/Recreate. `Recreate` uses `--unregister` only.
- **P7 Reset script** `dbd7708`: `scripts/reset-openclaw-wsl-validation-state.ps1` (388 lines). Distro hardcoded `OpenClawGateway`. Backup-before-remove. APPROVED Kranz, Bostick SHA256-verified.
- **P8 docs** `1300981`: `docs/wsl-owner-validation.md` + `docs/wsl-owner-open-issues.md` (Craig's Q&A inlined). Omitted rootfs doc per Mike. APPROVED.

**Empirical research (round 6/7):** `wsl --install Ubuntu-24.04 --no-launch --name OpenClawGateway --location <appdata> --version 2` 10/10 vs `winget install Canonical.Ubuntu.2404 --silent` 0/10 (APPX is launcher-only; never registers distro on silent). Deeper H1-H6 sweep confirmed: only viable winget fallback is winget+`ubuntu2404.exe install --root` (3/3) but cannot pass `--name`/`--location`. `winget install Microsoft.WSL` returns `0x8A15006B` UPDATE_NOT_APPLICABLE on already-current host — must treat as success-equivalent. **Recommendation locked:** `wsl --install` is the production path.

**Round 11 PR-prep:**
- aaron-13: discarded 6 stale unstaged files (`LocalSetupProgressPage.cs` + 5 resw locales) after pre-snapshot to `artifacts/stale-files-discarded-2026-05-04/`. HEAD unchanged at `1300981`; tests match Phase-8 anchor (1180/1180, 434/434). Files confirmed superseded by `32cbeae`.
- mattingly-3 closed i18n blocker at `ce89251` (85 entries × 5 locales).
- coordinator (autopilot) recorded Next-button defaults on LocalSetupProgressPage.

**Round 12 PR-prep:**
- aaron-15 wrote 22 KB uninstall robustness plan (`.squad/decisions/inbox/aaron-uninstall-plan.md`, now merged to decisions.md). 8 open Qs for Mike. Recommends shipping as **follow-up PR** after WSL gateway clean PR merges (uninstall depends on packaging decisions Q1/Q2).
- mattingly-5 implemented Next-button policy at `73767c5` (+13 tests → Tray 447/447).
## 2026-05-04T19:30-07:00 — Aaron-14: E2E install drive (FAILED at PairOperator)

**Worktree:** openclaw-wsl-gateway-clean | **HEAD:** 73767c5 | **Task:** drive auto-WSL install end-to-end up to gateway wizard step 1.

**Pre-flight:** baseline 18 distros snapshotted to rtifacts/e2e-drive-2026-05-04/before-distros.txt. OpenClawGateway present from prior prototype — cleaned via scripts\reset-openclaw-wsl-validation-state.ps1 -ConfirmDestructiveClean. Backup at rtifacts\reset-backups\20260504190728\ (438 files / 19.3 GB pre-unregister). Build PASS.

**Launch:** PID 8240 at 19:11:35. SetupWarningPage rendered ✓.

**Click:** computer-use MCP failed with Bun is not defined. Fell back to PowerShell UIA — OnboardingSetupLocal button found by AutomationId, InvokePattern.Invoke() at 19:12:35.

**Engine progress (inferred from filesystem + gateway logs, NOT page UI which froze):**
- ✅ EnsureWslEnabled → CreateWslInstance → ConfigureWslInstance → InstallOpenClawCli (/opt/openclaw/{bin,tools} populated, node-v22.22.0 staged)
- ✅ PrepareGatewayConfig → InstallGatewayService (user systemd openclaw-gateway.service Active running since 02:14:21 UTC)
- ✅ StartGateway / WaitForGateway (HTTP 494ms succeeded; 18789 listening loopback v4+v6)
- ✅ MintBootstrapToken (Windows-side BootstrapToken populated [REDACTED]; gateway-side pending.json written)
- ❌ **PairOperator FAILED** — gateway log: 2× cause:device-auth-invalid handshake:failed (1062ms, 2018ms) then cause:pairing-required handshake:failed durationMs:31. Last gateway log activity 02:14:35 UTC; engine did not retry after.
- ⛔ CheckWindowsNodeReadiness, PairWindowsTrayNode, VerifyEndToEnd: not reached.

**Final UI state:** LocalSetupProgressPage stuck rendering only first stage (• Checking system) with ProgressRing "Busy" spinning — even though engine reached PairOperator and failed. Next disabled, Back enabled. No FailedRetryable/Terminal transition. **Separate UI state-sync defect from the engine pairing defect.**

**App still running PID 8240; Mike has control.** App NOT killed per task. Wizard step 1 NOT reached so nothing to "stop at". Two defects to triage: (1) PairOperator handshake — bootstrap token from Windows tray rejected as device-auth-invalid by gateway holding the same token in pending.json (likely client framing or scope mismatch); (2) LocalSetupProgressPage doesn't propagate phase updates past stage 0.

**Decision:** .squad/decisions/inbox/aaron-e2e-drive.md (full timeline + redaction confirmation).

**Redaction:** BootstrapToken observed in settings.json during diagnostic dump — redacted from report + history. No tokens/setup-codes/private-keys/ed25519 material in artifacts.


## 2026-05-04T19:00-07:00 — Aaron-15: Uninstall robustness plan (planning only)

22 KB design doc covering 8 sections + 8 open questions for Mike (per-user vs per-machine, MSIX vs MSI, keep-WSL-data option, tray menu, wsl --export pre-backup, telemetry, script location, backup retention). Recommends shipping as **follow-up PR** after WSL gateway clean PR merges. Read-only investigation. Decision: aaron-uninstall-plan.md. SUCCESS.

## 2026-05-04T19:35-07:00 — Aaron-16 (in flight)

Investigating + fixing **Bug 1** from aaron-14 E2E drive: PairOperator handshake — Windows tray's uth.bootstrapToken rejected by gateway as device-auth-invalid despite pending.json containing the same token. Likely framing/scope mismatch in OpenClawGatewayClient bootstrap-token round-trip. Mattingly-6 in parallel on Bug 2 (UI phase propagation).
## 2026-05-04T19:35Z — Aaron-15: Bug 1 fix landed (commit e2de09) — operator-pending auto-approve

Root cause: MintBootstrapToken/PairOperator round-trip is correct, but on a fresh local-loopback gateway the upstream registers the bootstrap-token connect as a *pending* operator pairing request (logged in `~/.openclaw/devices/pending.json`) and rejects the same connect with `cause:device-auth-invalid reason:device-signature` then `cause:pairing-required reason:not-paired`. The upstream auto-approve path (`gateway.nodes.pairing.autoApproveCidrs`, `node-pairing-auto-approve.ts`) gates on `role === 'node'` so it does not fire for `operator` pairings; the canonical mechanism is explicit `openclaw devices approve`. On loopback the tray user IS the operator, so the engine drives that approval automatically.

Fix in `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs`: new `IPendingDeviceApprover` seam + `WslGatewayCliPendingDeviceApprover` invokes `openclaw devices approve --latest --json --url <state.GatewayUrl> --token "$(cat /var/lib/openclaw/gateway-token)"` inside the distro (token read in shell — never on argv). `SettingsOperatorPairingService.PairAsync` retries the bootstrap connect once after approval succeeds. Approval is gated on `credential.IsBootstrapToken && LocalGatewayApprover.IsLocalGateway(state.GatewayUrl) && _pendingApprover != null` so remote gateways and previously-paired devices keep their existing PairingRequired surface unchanged. Wired in `Build()`.

Tests: `tests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs` — 10 new (round-trip approve+retry, double-PairingRequired bounded, approval-failure error path, remote-gateway opt-out, non-bootstrap opt-out, first-connect happy path, 4 `ParseApproveJson` cases). Tray 493/493, Shared 1180/1180 with `OPENCLAW_RUN_INTEGRATION=1` + `OPENCLAW_REPO_ROOT`. Build.ps1 WinUI step blocked by PID 8240 .exe lock (expected: task forbids killing it; source compilation is clean per `dotnet build` of WinUI which only fails at the post-link copy). All redaction enforced — token VALUES never appear in code/tests/decision file. No e2e re-run by Aaron-15 (running app must remain at broken state for Mike's inspection).

## Learnings

### 2026-05-06T09:37:38-07:00 — Aaron wizard 3-bug deep debug (investigation only)

**Build-verification-vs-running-binary lesson:**  
Always check both the commit timestamp AND the DLL LastWriteTime before concluding "fix not in binary". Commit `2487aef` was at 08:07; DLL was built at 08:30 — fix WAS present. The bugs were genuine behavioral issues, not a stale-build artifact. The stale-build trap from decisions.md (Aaron-21) applies: after source edits, always explicitly rebuild WinUI with `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q` and verify DLL timestamp.

**Upstream wizard contract (openclaw/openclaw, ref bc97182d):**  
`src/wizard/session.ts`: `WizardSession` is pure in-memory; NOT persisted across gateway process restart. `wizard.start` always creates a NEW session from step 0. `wizard.status` returns the current pending step of an EXISTING session. When a transient disconnect occurs mid-wizard (gateway process stayed up), the `answerDeferred` for the in-flight step is still live on the gateway — `wizard.status` will return that same step. `wizard.start` in this case creates a parallel new session, which is wrong. The correct recovery protocol after transient disconnect is `wizard.status` first, then `wizard.start` fallback only if session is gone.

**FunctionalUI RadioButtons binding behavior:**  
`ConfigureRadioButtons` (FunctionalUI.cs:678–697) sets `control.ItemsSource = element.Items`. WinUI3 RadioButtons resets internal selection whenever `ItemsSource` is assigned to a new object reference, even if the content is identical. Since `WizardPage.Render()` calls `labels.ToArray()` on every render cycle, EVERY render replaces `ItemsSource` with a new array → visual flash (brief deselect during layout pass) and apparent "two-click to select" behavior. Fix: cache the options array in `UseState` and only replace it when the step changes (inside `ApplyStep`), so re-renders from heartbeat/channel-health state changes reuse the same object reference.

**Log pattern for wizard issues:**  
`[Wizard]` prefix in tray log captures WizardPage lifecycle. Key events: "WizardPage constructed" = new instance (either mount or recovery restart); "Sending wizard.start frame" vs "Sending wizard.next" distinguishes fresh vs. advance. A second "WizardPage constructed" immediately after an ERROR line is the smoking gun for unwanted recovery restart (Symptom 3). Absence of "wizard.status" in the log means the recovery path never tried to resume — it went straight to wizard.start.
## 2026-05-06T22:00:00Z — Aaron PR #274: Merge with master (graft pattern documentation)

**Context:** PR #274 merged to origin/master via merge commit 37745b2. Aaron orchestrated final graft + merge sequence (aaron-pr274-graft agent).

**Pattern: Feature-branch graft with conditional re-dispatch**
- **Goal:** Preserve feature branch's design (tray-menu redesign) while adopting master's new entry pattern (Setup Guide / Reconfigure).
- **Method:** Master's `OnboardingExistingConfigGuard` + dispatch case `"setup"` are existing, stable patterns. Graft inserts the Setup/Reconfigure action between QuickSend and Exit in the master tray menu structure using the same guard + dispatch.
- **Implementation:** No new case added to dispatcher; reused case `"setup"` (which already existed on master for onboarding-gate flows). Conditional `OnboardingExistingConfigGuard` ensures Setup Guide shows only for unconfigured tray, Reconfigure for configured tray — both dispatch via existing `"setup"` case.
- **Side-fixes applied during merge:**
  - **DeviceIdentity.cs:** Kept feature branch's multi-method operator/node-token dispatch; merged in master's empty-token guard (new condition to `StoreDeviceTokenCore` + `StoreNodeDeviceTokenCore`).
  - **SetupCodeDecoder.cs:** Adopted master's strict version pattern; dropped feature branch's bootstrap_token/token field-name fallbacks.
  - **Architectural decision:** Multi-method dispatch + single empty-token guard is more maintainable than feature branch's single-method with fallback logic — each token type has its own path; guard applies uniformly.
- **Lost in merge** (file separately if needed): Activity Stream flyout, Support/Debug flyouts, AutoStart entry, RestartSshTunnel entry — feature branch had these; master does not. Redesign trade-off.
- **ARM64 messaging:** Softened from "not supported" → "unvalidated" (bostick-pr274-arm64-wording agent). Install scripts already auto-detect arch; wording now matches capability.
- **Build/test result:** PASS (build.ps1 + tray tests 447/447 + shared tests 1180/1180).

**Future merge pattern:** When grafting conditional UI/dispatch changes, anchor to existing guard + case structure on target branch to minimize merge conflicts and preserve dispatch stability.

## 2026-05-06
- Security plan + implementation


## 2026-05-07 — Aaron: WSL Gateway Uninstall Plan v2 (feat/wsl-gateway-uninstall)

**Task:** Refined planning following Mike's D1–D4 decisions. Investigation-only; no code changes.

### New Findings

**Installer Flavors (confirmed from GH release v0.5.0 + ci.yml + installer.iss):**
- **Inno Setup** (non-MSIX installer): `OpenClawTray-Setup-{x64,arm64}.exe`. Script: `installer.iss` in repo root. `DefaultDirName={localappdata}\OpenClawTray`. Current `[UninstallRun]` only removes CommandPalette Appx — no WSL cleanup. WSL cleanup hook via new `[UninstallRun]` entry calling a dropped helper script. Inno does NOT auto-clean runtime app state.
- **Portable ZIP**: `OpenClawTray-{ver}-win-{rid}.zip`. No OS uninstall hook. Only path: in-tray "Remove Local Gateway" button.
- **MSIX (sideloaded)**: `OpenClawTray-{ver}-win-{rid}.msix`. `Package.appxmanifest` declares `runFullTrust` capability → VFS redirection does NOT apply → app writes to REAL `%APPDATA%\OpenClawTray\` and `%LOCALAPPDATA%\OpenClawTray\`, not MSIX package container. MSIX removal only cleans `%LOCALAPPDATA%\Packages\OpenClaw.Tray_<hash>\` — all runtime app state (WSL distro, VHD, credentials) remains after MSIX removal. **No standard uninstall hook available for runFullTrust MSIX.** Critical risk: orphaned WSL distro after MSIX removal.

**mcp-token.txt (D3 investigation):**
- File: `%APPDATA%\OpenClawTray\mcp-token.txt` (via `NodeService.McpTokenPath` = `SettingsManager.SettingsDirectoryPath + "mcp-token.txt"`).
- Created lazily when user enables Local MCP Server (`McpAuthToken.LoadOrCreate(McpTokenPath)` in `NodeService.StartMcpServerAsync`).
- Bearer token for local MCP HTTP server (loopback-only). Read by external MCP clients (Claude Desktop, VS Code, `openclaw-windows-node` CLI).
- **Completely independent of WSL gateway** — not created or used by the WSL gateway install path.
- **Decision: PRESERVE unconditionally.** Deleting it silently invalidates all user MCP client registrations. `KeepMcpToken` option removed from uninstall API.

**device-key-ed25519.json schema (D2 investigation):**
- Today: single-entry flat JSON `{ PrivateKeyBase64, PublicKeyBase64, DeviceId, DeviceToken, Algorithm, CreatedAt }`.
- NOT a list/multi-entry structure. Mike's D2 multi-gateway assumption does NOT match current schema.
- Ed25519 keypair is global device identity (used for all gateways). `DeviceToken` is the most-recently-stored pairing credential.
- **v1 uninstall approach:** Null out `DeviceToken` field in place. Preserve keypair. `HasStoredDeviceToken` returns false → `RequiresSetup` triggers correctly.
- **Schema v2 proposal:** `{ SchemaVersion:2, DeviceId, Algorithm, ..., GatewayEntries: [{GatewayUrl, DeviceToken, PairedAt}] }` for full per-gateway edit-vs-delete. Filed as separate work item / prerequisite for full D2 compliance.


**Task:** Planning-only uninstall design following PR #274 merge.  
**Worktree:** `openclaw-uninstall` | **Branch:** `feat/wsl-gateway-uninstall`

### Artifact Catalog (key facts)

- **Two separate Windows data roots:** `%APPDATA%\OpenClawTray` (roaming — settings.json, device-key-ed25519.json, mcp-token.txt) vs `%LOCALAPPDATA%\OpenClawTray` (local — setup-state.json, Logs/, wsl/OpenClawGateway/, crash.log, run.marker, exec-policy.json).
- **WSL VHD location:** `%LOCALAPPDATA%\OpenClawTray\wsl\OpenClawGateway\ext4.vhdx` (from `WslStoreInstanceInstaller.ResolveInstallLocation`).
- **`wsl --unregister` deletes the VHD** — no separate file delete needed for the VHD, but the parent directory may linger.
- **DistroName is stored in `setup-state.json`** (`LocalGatewaySetupState.DistroName`) — uninstall should read it from state file rather than hardcoding `OpenClawGateway`, to handle env-var overrides.
- **Keepalive process** `WslDistroKeepAlive` launches `sleep 2147483647` inside the distro; it dies when `wsl --terminate` runs.
- **`StartupSetupState.RequiresSetup` gate:** returns `true` (requires setup) when `Token` is empty AND NOT (`EnableNodeMode` AND (bootstrap token or device key file)). Uninstall must clear `Token`, `BootstrapToken`, `EnableNodeMode=false`, and delete `device-key-ed25519.json`. If `EnableMcpServer=true`, `RequiresSetup` returns `false` regardless — open Q7.
- **AutoStartManager:** registry at `HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\OpenClawTray`. No settings flag sync needed unless Q8 decided.
- **MCP token:** `%APPDATA%\OpenClawTray\mcp-token.txt` — credential for local MCP HTTP server. Deleting breaks external MCP registrations. Open Q4.

### Decisions Made

- **API surface:** New `LocalGatewayUninstall.cs` alongside `LocalGatewaySetup.cs`.
- **UI entry point:** Settings page "Remove Local Gateway" button; existing tray "Reconfigure" dispatch unchanged.
- **Confirmation gate:** `ConfirmDestructive=true` required; dry-run mode when false.
- **Order:** service stop → terminate → unregister → VHD dir cleanup → settings clear → device key delete → setup-state delete → autostart remove → optional mcp/logs/exec-policy cleanup.
- **Idempotency:** every step skip-on-absent; distro unregister is a no-op if not registered.

### Open Questions for Mike (8 total)

Q1 Packaging scope (MSIX/MSI hook vs. in-tray settings button)  
Q2 Per-user install only? (no elevation needed — confirm)  
Q3 wsl --export backup before unregister?  
Q4 Delete mcp-token.txt on uninstall?  
Q5 Delete logs on uninstall?  
Q6 Delete exec-policy.json on uninstall?  
Q7 Set EnableMcpServer=false on uninstall? (affects RequiresSetup gate)  
Q8 Set settings.AutoStart=false in addition to removing registry entry?

**Decision file:** `.squad/decisions/inbox/aaron-uninstall-plan.md`  
**Status:** PLAN COMPLETE — awaiting Mike's answers to Q1-Q8 before coding begins.

## 2026-05-07 — PR #274 P0 Tray Init Regression: async-void OnLaunched Ordering (aaron-pr274-tray-init-fix)

**Critical pattern for future merges:**

**Root Cause:** `App.OnLaunched` is `async void` (fire-and-forget). During PR #274 merge, `InitializeTrayIcon()` was deferred until AFTER the `RequiresSetup` branch. On fresh-box test, the sequence becomes:
1. OnLaunched async-void starts (no await)
2. RequiresSetup branch shows onboarding wizard
3. async InitializeTrayIcon eventually runs, tray icon ctor throws
4. Exception swallowed by async-void; tray icon never created
5. User has setup wizard but no tray icon

**Fix:** Reorder `InitializeTrayIcon()` BEFORE the `RequiresSetup` branch (Commit 3e4c217).  
Wrap `ShowOnboardingAsync()` in try/catch with `Logger.Error()` so ANY wizard failure surfaces in openclaw-tray.log rather than crashing silently.

**Core principle: Tray is application chrome, must outlive any wizard failure.**  
UI wizards (setup, reconfigure) are disposable flows; tray icon is foundational. Never defer tray init past conditional branches. Always wrap wizard invocations in try/catch.

**Tests:** Shared 1252/1274, Tray 617/617. Build PASS. Underlying constructor exception still unidentified (env-specific to fresh contributor box — defensive log will surface it on next repro).

**Pattern for future codebases:**
- async-void lifecycle methods (OnLaunched, OnSuspending) execute as fire-and-forget
- Initialization order matters: critical foundational objects before conditional branches
- Wrap branch-flow initiators (wizard.Show, setup.Start) in try/catch with Logger.Error


