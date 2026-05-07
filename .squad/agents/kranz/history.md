# Project Context

- **Project:** openclaw-windows-node (Windows tray app + WSL gateway)
- **Created:** 2026-05-04
- **User:** Mike Harsh

## Core Context

Clean WSL gateway rebuild. Prototype lives in worktree `openclaw-windows-node` (branch `pr-241-feedback-fixes`) and is intentionally dirty / reference-only. Final implementation goes in sibling worktree `..\openclaw-wsl-gateway-clean` (branch `feat/wsl-gateway-clean` from upstream/master).

Read these on first spawn:
- `.squad/identity/now.md` — current focus and immediate next todo.
- `.squad/prototype-reference.md` — file-by-file porting inventory.

## Recent Updates

📌 Team hired 2026-05-04. Universe: Apollo 13.

## Learnings

### Summary — Rounds 1–5 (planning + Phase 1/2/3 reviewer gates) [Scribe-compacted 2026-05-04T21:15Z]

- **Sequenced porting plan** (round 1): 8 ordered phases. Strip rootfs/custom-distro path; Windows tray node only (no WSL worker); Phase 5 UX is Mattingly-owned and gated on Phase 4 app wiring; Phases 5/6 can parallelize after Phase 4. Craig confirmation hard-gated Phase 3.
- **Phase 3 revised against Craig's answers**: loopback only (drop `lan`/`auto`/WSL-IP fallback); trust `wsl --install` exit code (drop postcondition-on-hang); install spec locked `wsl --install Ubuntu-24.04 --name OpenClawGateway --location <path> --no-launch --version 2`; no worker in WSL (`StartWorker`/`PairWorker` dropped); repair = `wsl --terminate OpenClawGateway` only — global `wsl --shutdown` banned in product paths; aka.ms/wsllogs surfaced on failure; WelcomePage removed (security notice folds into SetupWarning); port-with-pruning over rewrite (~130 lines contamination in 2600-line file).
- **Phase 1 verdict — CONDITIONAL APPROVE** on `95911b8`: role-specific operator/node token storage present; no rootfs/trusted-signing leak. Punch list before Phase-2 final: non-empty node-scope persistence coverage + unknown-role handling lockdown.
- **Phase 2 verdict — APPROVE** through `b69202d`: Phase 1 punch list closed; GatewayClient preserves `auth.bootstrapToken` / stored `auth.deviceToken` / role-specific `hello-ok.auth`; WindowsNodeClient reconnect uses `auth.deviceToken` with role-aware `DeviceIdentity` APIs; no WebBridge / WSL UNC / token leak. Validation needed `OPENCLAW_REPO_ROOT`.
- **Phase 3 verdict — CONDITIONAL APPROVE** on `98bdf77` (Aaron's LocalGatewaySetup port): all Craig deltas present; loopback resolver, WSL config writes, `wsl --terminate` repair, aka.ms/wsllogs, `loginctl enable-linger`, tray keepalive. No product `wsl --shutdown` / `--web-download` / `--from-file` / rootfs / WSL-IP fallback / UNC I/O / StartWorker/PairWorker. Validation: build PASS, Tray 426/426, Shared 1180/1180, LocalGatewaySetupTests 33/33. Punch list before merge: strip `PreserveWorkerData`/`worker_data_preserved`; gate distro-name override as test/dev-only.

Five open questions filed for Mike before Phase 1 starts (clean worktree remote, Craig status, WSL worker requirement, offline fallback scope, rootfs doc disposition).

**2026-05-04 17:00:00Z — Team Update**
Clean worktree exists and porting plan is canonical. Aaron ready for Phase 1 upon Mike approval; Mattingly ready for Phase 5 layout work.

### Summary — Rounds 5–6 team updates (Phase 4 verdict + lead-up to Phase 5) [Scribe-compacted 2026-05-04T21:35Z]

- **Round 5:** kranz-4 issued **CONDITIONAL APPROVE** on Phase 3 (`98bdf77`); Phase 4 unlocked. Aaron switched to opus-4.7 (aaron-7/8). Punch list owners: Aaron (worker-vocab strip, distro-override gating).
- **Phase 4 reviewer gate (`4ab1ec6` + `8cc32c6`) — APPROVE.** Worker vocabulary fully purged; distro override hard-locked behind `#if DEBUG || OPENCLAW_TRAY_TESTS` (Release returns constant `OpenClawGateway`). `App.CreateLocalGatewaySetupEngine()` factory wires Phase 1+2+3 with lazy `NodeService`; `App.IdentityDataPath` (`%APPDATA%\OpenClawTray`, `OPENCLAW_TRAY_APPDATA_DIR` override) is shared operator+node DeviceIdentity store. No rootfs/UNC/worker leakage. Validation: build PASS, Shared 1180/1180, Tray 426/426 (env: `OPENCLAW_REPO_ROOT`, `OPENCLAW_RUN_INTEGRATION=1`). Decision: `kranz-phase4-verdict.md`. Phase 5 unlocked unconditionally.
- **Round 6 team update:** Phase 5 in flight (Mattingly: SetupWarning + LocalSetupProgress XAML + screenshots); aaron-8 empirical 20-iter winget harness running. `decisions.md` compacted 33.6 KB → 10.8 KB; Phase 1/2/3 archived to `decisions-archive.md`.

### Summary — Phase 5/6/7 reviewer gates + interim team updates [Scribe-compacted 2026-05-04T22:00Z]

Three reviewer gates at `HEAD` on `feat/wsl-gateway-clean` with `OPENCLAW_REPO_ROOT` + `OPENCLAW_RUN_INTEGRATION=1`:

- **Phase 5 @ `99f5107` (2026-05-04T13:55) — CONDITIONAL APPROVE** (Mattingly `43035ca`..`99f5107`). `SetupWarningPage` matches contract (Grid Auto/1*/Auto/Auto, MaxWidth 460, accent CTA verb-phrase, `TextBlockButtonStyle` hyperlink, folded ⚠️ notice, no HStack/no TextBox). `LocalSetupProgressPage` matches (Grid Auto/Auto/1*/Auto, MaxWidth 520, per-stage Auto/1*/Auto, error-row collapsed unless Failed*). Welcome route removed; `SetupPath` enum + `AdvanceRequested` event wired; `GetPageOrder()` forks Local vs Advanced. Nav Next disabled until path picked; 1s auto-advance on Complete (gated by `s_advanceFiredForCompletion`); FailedTerminal → aka.ms/wsllogs hint. Tests: `OnboardingStateTests` rewritten with full forked matrix; `ConnectionPageTopologyTests` updated for Advanced-fork-only WSL/SSH. `./build.ps1` PASS, Tray **434/434** (+8 vs 426), Shared **1180/1180**. No `\\wsl$`/`\\wsl.localhost`. Both screenshots viewed and matched contract. Punch list (fast-follow, NOT Phase 6 blocker): (1) trim subtitle time-estimate; (2) Mike question on Next-button mid-install; (3) clean orphan `Onboarding_Welcome_*` resw across 5 locales; (4) i18n post-merge. **Mattingly fast-follow @ `32cbeae` closed items 1+2 round-9.**
- **Phase 6 @ `8060ae9` (2026-05-04T14:15) — APPROVE** (Aaron `validate-wsl-gateway.ps1`, +940/-0). Scenarios reduced to `PreflightOnly | UpstreamInstall | FreshMachine | Recreate` (line 35). Stripped-parameter grep (rootfs/manifest/signing-key/public-key/gateway-package/allow-unsigned-dev-artifact/allow-non-standard-distro-name) all empty. Loopback-only networking confirmed: `wslIp|wsl-ip|GetWslIp|FallbackBind|AutoBind|gateway\.bind` empty; endpoint check on `127.0.0.1:18789` only. `Recreate` uses `wsl.exe --unregister` (line 782); sole `--shutdown` is prohibition comment (line 781). UI clicks `OnboardingSetupLocal` only (single click; relies on `LocalSetupProgressPage` self-start). `aka.ms/wsllogs` surfaced in setup-failure / setup-timeout / gateway-health-failure throws + `Save-DiagnosticsSnapshot` + `summary.md` failure footer + final host print. Redaction covers stdout/settings/device-key/setup-state/relay-probe/journal/openclaw-cli probe; `Token|GatewayToken|BootstrapToken|NodeToken`; `setupCode|PrivateKeyBase64|PublicKeyBase64`. `StartWorker|PairWorker|WorkerPairing` empty. PreflightOnly run PASS, parser zero errors, `./build.ps1` PASS, Shared 1180/1180, Tray 434/434. Non-blocking fast-follows: ordinal-based `Convert-SetupPhase` (recommend property-name once engine emits names); script relies on default `SetupWarning` route. None gate Phase 7.
- **Phase 7 @ `dbd7708` (2026-05-04T14:35) — APPROVE** (Aaron `reset-openclaw-wsl-validation-state.ps1`, +388/-0). No fast-follow punch list. Dry-run default (`.dryRun = -not `); every destructive branch emits `DryRun` step. Distro hard-locked (` = "OpenClawGateway"`); `param()` has no `-DistroName`/`-AllowNonStandardDistroNameForDestructiveClean`/`-CleanOpenClawState`. Backup-before-destruction order recovery-preserving (Copy then Remove); default `artifacts\reset-backups\<yyyyMMddHHmmss>\`. Stripped surface (rootfs/manifest, worker-data, `wsl --shutdown`, `\\wsl$`/`\\wsl.localhost`) absent in code; sole hits are prohibition comments. Lifecycle: `wsl --terminate OpenClawGateway` then `wsl --unregister OpenClawGateway` only. Token redaction not applicable (script writes no token payloads). `./build.ps1` PASS, Shared 1180/1180, Tray 434/434, parser clean, dry-run smoke exit 0.

**Interim team updates (rounds 7/8):** Phase 4 APPROVED (`8cc32c6`); Phase 5 onboarding UX landed (`43035ca`..`99f5107`). Aaron-8 empirical 20-iter winget result (`wsl --install` 10/10 vs `winget Canonical.Ubuntu.2404` 0/10 — APPX only stages launcher). Phase 6 `8060ae9` landed (4 scenarios, loopback-only, `wsl --unregister` for Recreate). Phase 7 `dbd7708` landed (hard-locked `OpenClawGateway`, dry-run default).
### Summary — Phase 8 verdict + Plan Complete [Scribe-compacted 2026-05-04T18:35-07:00 / round 11]

- **Phase 8 reviewer gate @ `1300981` (2026-05-04T15:00-07:00) — APPROVE** (Aaron's docs port +744/-0, FINAL phase). Two new docs present (validation ~17.5 KB, open-issues ~16.5 KB); rootfs doc correctly absent. Install command canonical across diagram/empirical/UpstreamInstall. `wsl --terminate` documented as repair primitive (6 hits); all 10 `wsl --shutdown` mentions are prohibitions; aka.ms/wsllogs 11×. 4 validation scenarios match Phase 6 exactly. Forbidden-as-design grep (rootfs/`--web-download`/`--from-file`/wsl-ip/lan-bind/StartWorker/BuildRootfs) all in negation or removed-context framing; UNC grep all prohibitions. 19 questions all ✅ Answered. `.squad/` not in worktree, `artifacts/` gitignored, `scripts/experiments/` absent — no `.gitignore` update. AGENTS.md gate satisfied by Aaron's Phase-8 run.
- **PLAN COMPLETE — 2026-05-04T22:15Z (round 10).** 15 commits on `feat/wsl-gateway-clean` since baseline `871b959`. Build PASS, Shared 1180/1180/0/0, Tray 434/434/0/0. Net delta from anchor: **+35 new tests across 8 phases, zero regressions.**
- **Mike's 3 PR-prep blockers documented round-10:** (1) 6 stale unstaged files revert, (2) Next-button mid-install policy, (3) i18n strategy. **All three closed in round 11** (see team update below).

---
## Team update — Round 11 (2026-05-04T18:35-07:00) [Scribe]

- **Aaron-13** discarded the 6 stale unstaged worktree files after pre-snapshotting diffs to `artifacts/stale-files-discarded-2026-05-04/`. Build PASS, Shared 1180/1180, Tray 434/434 — files confirmed stale, no restore needed. **PR-prep blocker #1 CLOSED.** HEAD unchanged at `1300981`.
- **Mattingly-3** landed Phase-5 i18n at commit `ce89251` (parent `1300981`): 17 new keys × 5 locales = 85 entries, `OPENCLAW_TEST_LOCALE` env hook in `OnboardingWindow`, fr-fr screenshot verified, 5 low-confidence translations flagged `?` for Mike. Build PASS, Tray 434/434, Shared 1180/1180. **PR-prep blocker #3 CLOSED.**
- **Coordinator** (autopilot) recorded Next-button defaults on `LocalSetupProgressPage` (Mike was offline): industry-standard onboarding-progress behavior — Idle hidden, Running visible+disabled, Success visible+enabled briefly before auto-advance, Failed states visible+disabled with Back enabled. **PR-prep blocker #2 CLOSED with autopilot defaults**, Mike-override-on-PR-review. See `decisions.md` round-11 entry.
- **configure-copilot** enabled `windows-computer-use-mcp` v0.1.1 (18 desktop automation tools) in user MCP config — replaces brittle visual-test env-var pipeline.
- **mattingly-4** in flight running full visual pass via computer-use MCP for final PR evidence.
- Branch `feat/wsl-gateway-clean` now at 16 commits since baseline `871b959`. Working tree clean modulo mattingly-4. Next: mattingly-4 returns → Mike pushes → PR opens.


## 2026-05-04T19:35-07:00 — Team update (round 13)

Aaron-14 E2E drive on 73767c5 reached PairOperator and surfaced **2 real bugs**: (1) bootstrap-token handshake rejected as device-auth-invalid; (2) LocalSetupProgressPage doesn't propagate phase updates past stage 0. **Aaron-16** + **Mattingly-6** in flight fixing in parallel. Tray 447/447, Shared 1180/1180 (mattingly-5 added Next-button policy at 73767c5). PR push deferred until both bugs resolved.
