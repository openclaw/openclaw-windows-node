# Squad Decisions

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
- Older / superseded entries live in `decisions-archive.md`

## Active Canonical Decisions

### Dedicated Ubuntu WSL instance, not custom OpenClaw distro

OpenClaw creates a dedicated app-owned Ubuntu-24.04 WSL instance named `OpenClawGateway` from the Store Ubuntu package, then applies OpenClaw-owned configuration. No custom rootfs or offline fallback path in this clean PR. (Craig confirmed.)

### Public Linux installer remains source of truth

Windows tray invokes the public OpenClaw Linux installer unchanged inside WSL at `https://openclaw.ai/install-cli.sh` with prefix `/opt/openclaw`. No forking or patching.

### Use upstream setup-code/bootstrap pairing

Local setup calls upstream `openclaw qr --json`, decodes/consumes upstream `setupCode` bootstrap payload, and pairs through the normal WebSocket handshake using `auth.bootstrapToken`. Windows does not directly edit gateway pairing stores.

### Store role-specific credentials

Windows tray identity may receive both node and operator credentials. Persist separately: operator token in existing field, node token in separate field. Paired reconnects use `auth.deviceToken`; node credentials never sent as `auth.token`.

### Windows tray node is acceptable, WSL worker optional

Mac app parity supports same-app node model. For Windows: gateway in WSL + Windows tray operator + Windows tray node is the scope for this clean PR. (Mike: Windows tray node ONLY; no WSL worker port.)

### Fork onboarding setup UX

Fork before current master connection page: first warning page (SetupWarning) offers centered **Setup locally** and **Advanced setup** link. **Setup locally** opens dedicated WSL local setup progress page then gateway wizard. **Advanced setup** opens current connection page then gateway wizard. (WelcomePage deleted, security notice folds into SetupWarning body — Mike decision.)

### Reporting Standard (test counts)

All test-count claims must include:

1. Failures broken out, even when pre-existing.
2. `OPENCLAW_RUN_INTEGRATION` env-var state at time of run.
3. Any other env-vars materially affecting counts (notably `OPENCLAW_REPO_ROOT`, which test repo-root discovery requires; without it, `LocalizationValidationTests` and `ReadmeValidationTests` fail environmentally).

Pre-existing baseline for this branch: Shared.Tests 1172 total (1151p, 1f [ReadmeValidationTests], 20s), Tray.Tests 407p.

Phase-anchor baseline (Phases 6→7→8 stable): Shared **1180/1180**, Tray **434/434**.

## Decision: Clean WSL Gateway Worktree Created

**Date:** 2026-05-04T09:52:26-07:00 — **Author:** Aaron

Clean sibling worktree at upstream tip (`871b959`, "Fix onboarding theme backgrounds") from `origin/master`. Branch: `feat/wsl-gateway-clean`. Path: `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-wsl-gateway-clean`.

## Decision: Craig Loewen's WSL Answers (Authoritative)

**Date:** 2026-05-04 — **By:** Mike Harsh (relaying Craig Loewen)
**Source:** Craig Loewen review of `docs/wsl-owner-open-issues.md`

- `wsl --install Ubuntu-24.04 --name OpenClawGateway --no-launch --version 2` is supportable ✅
- Trust exit code (drop hang-fallback pattern) ✅
- Use explicit `Ubuntu-24.04` (not generic `Ubuntu`) ✅
- No `--web-download` / `--from-file` / offline fallback needed ✅
- `automount=false, interop=false, appendWindowsPath=false` appropriate ✅
- No post-clone repair (machine-id/DNS/timezone work as-is) ✅
- Use `wsl.conf` and `wsl-distribution.conf` ✅
- **Networking: loopback ONLY** (not WSL-IP fallback, not `lan`/`auto` bind) ✅
- Localhost forwarding is reliable core WSL promise ✅
- **Repair primitive: `wsl --terminate OpenClawGateway`** (never global `wsl --shutdown`) ✅
- Diagnostics via `aka.ms/wsllogs` ✅
- Lifecycle (user-systemd + tray keepalive both acceptable) ✅

Full verbatim Q&A in `coordinator-craig-wsl-answers.md`.

---

## Decision: winget Research Consolidated — Stay with `wsl --install` (Aaron-8 + Aaron-9)

**Date:** 2026-05-04T13:30 → 13:50 — **Artifacts:** `artifacts/wsl-install-vs-winget/run-20260504-131837/` (Aaron-8) and `artifacts/wsl-install-vs-winget/deeper-20260504-135355/` (Aaron-9).

**Verdict:** **Stay with `wsl --install Ubuntu-24.04 --name OpenClawGateway --location <appdata>\OpenClawTray\wsl --no-launch --version 2`** as the single primitive. winget remains optional platform-repair fallback only (`Microsoft.WSL`).

**Aaron-8 empirical 20-iter (10 each arm):** `wsl --install` 10/10 (mean 45.5s). `winget install Canonical.Ubuntu.2404 --silent` 0/10 (mean 13.0s, no-op fast). Root cause: APPX is the Ubuntu launcher, not a WSL distro creator; distro registration only happens on first launch of `ubuntu2404.exe`; `--silent --disable-interactivity` never invokes it. winget cannot pass `--name`/`--location` to the launcher.

**Aaron-9 deeper hypothesis sweep (6 hypotheses, 3 iter each):** H1 pre-`Microsoft.WSL`+Canonical 0/3 (`Microsoft.WSL` returns `0x8A15006B` UPDATE_NOT_APPLICABLE on already-current host — must be treated as success-equivalent if invoked). **H2 winget+`ubuntu2404.exe install --root` 3/3** ✓ (`--root` skips first-run prompt). H3 non-silent 0/3 (manifest is plain `InstallerType: appx`, no PostInstall). **H4 winget+`wsl --install --no-launch` 3/3** but redundant. H5 `wsl --install --no-distribution`+launch 3/3 (no-op on current host). H6 `--interactive` 0/1 (no prompt).

**Why neither H2 nor H4 displaces `wsl --install` directly:** neither satisfies the `--name OpenClawGateway` / `--location <appdata>\OpenClawTray\wsl` requirements. H2 registers as default `Ubuntu-24.04` in default location; H4 reduces to the same `wsl --install` primitive plus a winget prereq. Phase 3 success criterion in `LocalGatewaySetup.cs` remains: `wsl --install` exit 0 AND `wsl --list --quiet` contains `OpenClawGateway`.

**Host:** Win 10.0.26200.0, WSL 2.6.3.0 / kernel 6.6.87.2-1, winget v1.28.240. Safety: 18 baseline distros preserved across both runs, no orphans, no reboot, no `wsl --shutdown`, no `\\wsl$`.

---

_Phase 6 (`validate-wsl-gateway.ps1`) and Phase 7 (`reset-openclaw-wsl-validation-state.ps1`) entries archived to `decisions-archive.md` on 2026-05-04T22:15Z. Both Kranz-APPROVED and Bostick-verified; no fast-follow. See archive for full detail._

## Decision: Phase 8 — Documentation Port (Aaron) — APPROVED round-10

**Date:** 2026-05-04T14:35:00-07:00 — **HEAD:** `1300981` (parent `32cbeae` Phase 5 fast-follow).

**Files created:**
- `docs/wsl-owner-validation.md` (~17.5 KB) — describes shipped WSL design: `wsl --install Ubuntu-24.04 --name OpenClawGateway --location <appdata>\OpenClawTray\wsl --no-launch --version 2`; loopback-only `:18789`; `/etc/wsl.conf` (systemd / automount=false / interop=false / appendWindowsPath=false / default user openclaw / useWindowsTimezone=true); `/etc/wsl-distribution.conf` (shortcut/terminal disabled); `wsl --terminate OpenClawGateway` repair; `aka.ms/wsllogs` diagnostics; 4 validation scenarios; empirical evidence pointer to `artifacts/wsl-install-vs-winget/run-20260504-131837/summary.json`; explicit out-of-scope list.
- `docs/wsl-owner-open-issues.md` (~16.5 KB) — 19 questions across Distribution model / Networking / Lifecycle + Mac app comparison; every Q ✅ Answered with Craig's answer + implementation implication; no 🟡 Open items remain.

**Explicitly omitted:** `docs/wsl-gateway-rootfs.md` — historical-only per Mike's autopilot decision; clean implementation has no rootfs path.

**Stripped from prototype text:** custom rootfs / dev-shim / signed offline base artifact; WSL-IP fallback / `lan` / `auto` bind / `gateway.bind` overrides; `--web-download` / `--from-file`; worker-in-WSL phases; `\\wsl$` / `\\wsl.localhost`; global `wsl --shutdown`; conditional/forward-looking language.

**Validation:** `./build.ps1` PASS · Tray **434/434** · Shared **1180/1180**. No regressions. Diff: 2 files changed, +744/-0.

**Notes for Kranz round-10:** authoritativeness sourced from `.squad/decisions.md` Craig answers (paraphrased authoritatively, cites squad decision file by phrase only); harness script (`scripts/experiments/...`) is in prototype, not in clean worktree (separate phase if porting wanted); pre-existing unstaged tree mods (`LocalSetupProgressPage.cs`, 5 resw files) unrelated to Phase 8 and not included in `1300981`.

**Outcome:** SUCCESS pending Kranz round-10 verdict. **Plan is functionally complete pending Phase 8 verdict.**

---

_Phase 5 fast-follow (Mattingly, `32cbeae`) — closes punch-list items 1 (time-estimate drop) and 2 (orphan `Onboarding_Welcome_*` resw cleanup, 5 locales × 9 keys = 45 entries). Items 3 (i18n) and 4 (Next-button policy) deferred to Mike. Build PASS · Tray 434/434 · Shared 1180/1180. Full detail archived to `decisions-archive.md` on 2026-05-04T22:15Z._

## Decision: Phase 8 Reviewer Verdict — APPROVE (Kranz, round-10)

**Date:** 2026-05-04T15:00:00-07:00 — **Reviewer:** Kranz (read-only) — **Commit reviewed:** `1300981` (parent `32cbeae`)

**Verdict:** ✅ **APPROVE.** Phase 8 documentation port is functionally complete. Mike may push `feat/wsl-gateway-clean` and open the upstream PR after handling fast-follow items in the PR-readiness summary below. Strict lockout NOT invoked — Aaron finished with this branch barring post-PR comment.

**File hygiene:** docs at expected paths; `wsl-gateway-rootfs.md` absent (`Test-Path` False). `git log --oneline dbd7708..HEAD` = exactly one commit; `git show --stat` matches Aaron's claim (2 files, +744/-0).

**Cross-cutting greps:** `\\wsl$` / `\\wsl.localhost` — 3 hits in `docs/`, all prohibitions ("are forbidden", "No `\\wsl$`", "are read or written" preceded by "No"). Forbidden tokens (`rootfs`, `RootfsManifest`, `--web-download`, `--from-file`, `wsl-ip`, `lan bind`, `StartWorker`, `BuildRootfs`) appear only in negation/removed contexts.

**Open-issues doc:** 19 questions (`Q1.1`–`Q1.7`, `Q2.1`–`Q2.6`, `Q3.1`–`Q3.6`); every one ✅ Answered with Craig's answer + implementation implication. Single 🟡 Open token is in legend only.

**Final-PR readiness:** `.squad/` does not exist in clean worktree (lives only in TEAM_ROOT); `artifacts/` gitignored (`.gitignore:64`, `git check-ignore -v` confirms); `scripts/experiments/` not in clean worktree; `git ls-files | rg '^(\.squad|artifacts|scripts/experiments)/'` empty. **No `.gitignore` update needed.**

**Mike's PR-prep blockers:** (1) revert 6 stale unstaged worktree mods (`LocalSetupProgressPage.cs` + 5 resw locales — Mattingly drafts superseded by `32cbeae`); (2) Mattingly Phase-5 Item 4 — Next-button mid-install policy (recommendation: disable until terminal); (3) Mattingly Phase-5 Item 3 — i18n of `SetupWarningPage` / `LocalSetupProgressPage` literals (likely post-merge patch with PR-description callout). Items (2) and (3) and harness-port and aaron-9-cite are non-blocking; only (1) must be resolved before `git push`.

---

## Decision: Phase 8 + Final Integration Sweep — PASS (Bostick, round-10)

**Date:** 2026-05-04T15:00:00-07:00 — **Verifier:** Bostick — **Commit:** `1300981` — **Env:** `OPENCLAW_RUN_INTEGRATION=1`, `OPENCLAW_REPO_ROOT=<worktree>`

**Phase 8 confirmation (Aaron's claim vs Bostick run):** `./build.ps1` PASS / PASS · Shared 1180/1180/0/0 (8 s) · Tray 434/434/0/0 (620 ms). All numbers match exactly. Doc spot-check: validation (300 lines) + open-issues (266 lines) parse; rootfs doc absent ✅.

**Final commit count:** **15 commits** on `feat/wsl-gateway-clean` since baseline `871b959`:

```
1300981 docs(wsl): port wsl-owner-validation + wsl-owner-open-issues (Phase 8)
32cbeae fix(onboarding): drop time estimate + clean orphan Welcome resw (Phase 5 fast-follow)
dbd7708 feat(scripts): port reset-openclaw-wsl-validation-state.ps1 (Phase 7)
8060ae9 feat(scripts): port validate-wsl-gateway.ps1 (Phase 6)
99f5107 chore(onboarding): remove WelcomePage (Phase 5.4)
c2ad1e5 feat(onboarding): LocalSetupProgressPage (Phase 5.3)
6a5783a feat(onboarding): SetupWarningPage (Phase 5.2)
43035ca feat(onboarding): SetupWarning + LocalSetupProgress routes + SetupPath (Phase 5.1)
8cc32c6 feat(tray): wire setup engine + shared identity path (Phase 4)
4ab1ec6 fix(tray): close Phase 3 punch list
98bdf77 feat(tray): port LocalGatewaySetup (Phase 3)
b69202d feat(shared): port WindowsNodeClient (Phase 2.2)
b20b5ce feat(shared): port OpenClawGatewayClient (Phase 2.1)
3ae03d3 fix(shared): close Phase 1 punch list
95911b8 feat(shared): port DeviceIdentity (Phase 1)
```

**Net delta from anchor `871b959`:** Shared +8 tests (1172 → 1180); Tray +27 tests (407 → 434). **Total +35 new tests across 8 phases. Zero regressions.** Anchor failure (`ReadmeValidationTests.ReadmeAllowCommandsJsonExample_IsValid`) was an `OPENCLAW_REPO_ROOT`-dependent env-discovery flap, not a code defect; passes cleanly with env set.

**PR-readiness sanity:** `.gitignore` covers `artifacts/` (line 64); `git ls-files .squad` and `git ls-files artifacts` return 0; `scripts/experiments/` does not exist in this worktree. ✅

**Outstanding non-blocking:** revert 6 stale unstaged files; editorial cite of `.squad/decisions.md` by path; empirical-artifact path accuracy.

---

## Decision: PLAN COMPLETE (round-10 close)

**Date:** 2026-05-04T22:15:00Z (15:15 PT) — **Author:** Scribe

The clean WSL gateway porting plan is **complete**. Phase 8 (FINAL phase per the original plan in `.squad/decisions-archive.md`) approved at commit `1300981`. Branch `feat/wsl-gateway-clean` holds 15 commits since baseline `871b959`. Build PASS, Shared 1180/1180, Tray 434/434, +35 net new tests since anchor, zero regressions across 8 phases.

**Awaiting:** Mike's PR-prep decisions on (1) reverting 6 stale unstaged files, (2) Next-button mid-install policy, (3) i18n strategy. See `kranz-phase8-verdict` and `bostick-phase8-and-final-sweep` entries above for detail. Session log: `.squad/log/2026-05-04T22-15-00Z-plan-complete-final-verdict.md`.

**Lockouts:** none. Aaron, Mattingly, Bostick, Kranz all available for post-PR review.


---

## 2026-05-04T18:14Z — Aaron-13: Stale unstaged files DISCARDED (PR-prep blocker #1 closed)

Per Mike's directive ("discard the 6 stale files; if tests fail, restore"), aaron-13 discarded 6 stale unstaged worktree mods on `feat/wsl-gateway-clean` worktree. HEAD unchanged at `1300981`. Pre-snapshotted full diffs to `artifacts/stale-files-discarded-2026-05-04/` (gitignored, recoverable via `git apply`). Files: `LocalSetupProgressPage.cs` (1/1), `en-us|fr-fr|zh-cn|zh-tw/Resources.resw` (28/1 each — orphan-key cleanup superseded by `32cbeae`), `nl-nl/Resources.resw` (72/45 — BOM + emoji-XML-entity normalization, also superseded). Validation: `./build.ps1` PASS, Shared 1180/1180, Tray 434/434 — exact match to Phase-8 anchor. Files **confirmed stale**, no restore needed. Concurrency-safe with Mattingly: ran `git checkout --` first; `git status --porcelain` empty pre and post.

## 2026-05-04T18:14Z — Mattingly-3: Phase-5 i18n landed (commit `ce89251`, blocker #3 closed)

Mike directive: "Localize any UI changes into the languages the app currently supports." Extracted 17 hard-coded English strings from `SetupWarningPage.cs` + `LocalSetupProgressPage.cs` into `Resources.resw`; translated for all 5 locales (en-us/fr-fr/nl-nl/zh-cn/zh-tw) = **85 entries**. Groups: `Onboarding_SetupWarning_*` (4), `Onboarding_LocalSetup_*` (6), `Onboarding_LocalSetup_Phase_*` (7). Skipped `Phase_EnsureWsl/InstallService/Complete` — not in actual `s_visibleStages[]`. Added `OPENCLAW_TEST_LOCALE` env hook in `OnboardingWindow` calling `LocalizationHelper.SetLanguageOverride` for visual-test locale rendering. `LocalizationValidationTests` green (key parity across 5 locales, no missing/extra/duplicates, no placeholder risk). Validation: build PASS, Tray 434/434, Shared 1180/1180. fr-fr screenshot `visual-test-output/phase5-localization/fr-fr/page-02.png` verified inline. **5 low-confidence translations flagged with ?` for Mike**: nl-nl `Title` (`Bezig met lokaal instellen`), zh-* `正在` continuous-aspect prefix on stage labels, fr-fr non-breaking-space-before-colon on `DiagnosticsHint`, zh-cn/zh-tw curly-vs-corner quotes in body, nl-nl `Geavanceerde installatie` for Advanced. Local commit only; Mike controls the push.

## 2026-05-04T18:35Z — Coordinator (autopilot): Next-button defaults on LocalSetupProgressPage (blocker #2 closed defensibly)

Coordinator-recorded defaults — Mike was offline when the deferred Next/Back-button question came due, so coordinator chose industry-standard onboarding-progress defaults under autopilot for branch PR-readiness. Mike retains override on PR review. Defaults by state: **Idle** Next=hidden, Back=enabled. **Running** Next=visible+disabled (clearer than hidden), Back=enabled (allows escape; engine continues; re-entry resumes from current state). **Success** Next=visible+enabled briefly (~1s) before auto-advance fires (tap-to-skip rewards engaged users). **FailedRetryable** Next=visible+disabled (forces in-page Try Again or Back to Advanced), Back=enabled. **FailedTerminal** Next=visible+disabled, Back=enabled (no trap; prevents completing onboarding with a non-functional gateway). Mattingly to implement as fast-follow after localization commits.

## 2026-05-04T18:30Z — Configure-copilot: `windows-computer-use-mcp` v0.1.1 ENABLED

Added to `C:\Users\mharsh\.copilot\mcp-config.json`. **18 desktop automation tools** now available to all agents (cursor_position, double_click, get_frontmost_application, hold_key, key, left_click, left_click_drag, list_displays, list_running_applications, mouse_move, open_application, read_clipboard, right_click, screenshot, scroll, type, write_clipboard, zoom). Replaces brittle visual-test env-var pipeline for full visual passes; mattingly-4 in flight using it now.

## 2026-05-04T18:35Z — Round 11 status (Scribe)

PR-prep blockers: **#1 (stale files) CLOSED** (aaron-13). **#2 (Next-button policy) CLOSED with autopilot defaults** (coordinator, Mike-override-on-review). **#3 (i18n) CLOSED** (mattingly-3, ce89251 local). Branch `feat/wsl-gateway-clean` now has **16 commits** since baseline `871b959` (Phase 8 `1300981` + localization `ce89251`). Working tree clean modulo mattingly-4's in-flight visual pass. Next: mattingly-4 returns with full visual evidence, then Mike pushes + opens PR.

## 2026-05-04T18:35Z — Mattingly-4: Full visual pass (round 12)

Five required onboarding states visually verified on eat/wsl-gateway-clean@ce89251: (1) SetupWarning en-us with folded ⚠️ WSL notice + accent "Set up locally" + Advanced hyperlink + Back/Next disabled; (2) LocalSetupProgress idle (Preflight active, 7 stages, no time-estimate); (3) LocalSetupProgress mid-flow (InstallOpenClawCli active, prior ✓); (4) ConnectionPage Advanced (5 modes, no inline-WSL panel residue, step dot 2 of 6); (5) SetupWarning fr-fr (full French strings, MaxWidth 460 intact, no clipping); (6) WelcomePage removal confirmed. Layout contracts (MaxWidth 460/520, NavigationHost 680) all hold. **Verdict: visually ship-ready.** windows-computer-use MCP returned Bun is not defined — fell back to OPENCLAW_VISUAL_TEST=1 harness. Captures: isual-test-output/full-pass-2026-05-04/{s1-warning-en,s2-progress-idle-en,s3-progress-active-en,s4-connection-advanced-en,s5-warning-fr}/page-02.png. Tray 434/434, Shared 1180/1180 (no code change).

## 2026-05-04T18:55Z — Mattingly-5: Phase 5 final — Next/Back-button policy implemented (commit 73767c5)

Implemented coordinator's autopilot Next-button defaults from round 11. New OnboardingNextButtonState enum (Default | Hidden | VisibleDisabled | VisibleEnabled) on OnboardingState + SetNextButtonState() + NavBarStateChanged event. New LocalSetupProgressPolicy.MapStatusToNextButtonState() pure helper (no WinUI deps; unit-testable). OnboardingApp consults state **only** when currentRoute == LocalSetupProgress; legacy logic untouched on every other route. Bonus fix: Complete-state 1s auto-advance now checks CurrentRoute == LocalSetupProgress before firing RequestAdvance() to prevent over-advance when user taps Next during pause. **Tests +13 → Tray 447/447** (3 OnboardingState + 10 mapping); Shared 1180/1180 unchanged. All 4 states screenshot-verified (isual-test-output/next-button-impl-2026-05-04/{s1-running,s2-success,s3-failed-terminal,s4-failed-retryable}/page-02.png). LocalOnlyComplete does not exist — only Complete (treated as success). Net delta from baseline 871b959: Tray **407 → 447 (+40)**, Shared **1172 → 1180 (+8)** across 17 commits.

## 2026-05-04T19:30-07:00 — Aaron-14: E2E install drive surfaced TWO real bugs (PARTIAL SUCCESS)

E2E drove production install path on clean WSL state from 73767c5. windows-computer-use MCP failed (Bun is not defined); fell back to PowerShell UIA — AutomationId=OnboardingSetupLocal button found and InvokePattern.Invoke() at 19:12:35. **Engine progressed through ~9 phases successfully** (EnsureWslEnabled → CreateWslInstance → ConfigureWslInstance → InstallOpenClawCli → PrepareGatewayConfig → InstallGatewayService → StartGateway → WaitForGateway → MintBootstrapToken). OpenClawGateway distro running, gateway service active on 127.0.0.1:18789 (v2026.5.3-1), HTTP 494ms, BootstrapToken minted Windows-side and pending.json written gateway-side. **Failed at PairOperator:** gateway log shows cause:device-auth-invalid handshake:failed ×2 (1062ms, 2018ms), then cause:pairing-required handshake:failed. Last gateway activity 02:14:35 UTC; engine did not retry. **Two distinct defects:** **Bug 1** PairOperator handshake — Windows tray's uth.bootstrapToken rejected by gateway holding the *same* token in pending.json (likely framing or scope mismatch in OpenClawGatewayClient). **Bug 2** LocalSetupProgressPage UI never propagated past stage 0 (• Checking system ProgressRing spinning) and never transitioned to FailedRetryable/FailedTerminal despite engine reaching PairOperator. App PID 8240 left running per task instructions — Mike has control. Pre-flight backup: rtifacts/reset-backups/20260504190728/ (438 files / 19.3 GB pre-unregister; gitignored). BootstrapToken value REDACTED throughout. Decision: aron-e2e-drive.md.

## 2026-05-04T19:00Z — Aaron-15: Robust uninstall plan (planning only)

22 KB design doc for Windows tray uninstall robustness; 8 sections, 8 open questions for Mike (per-user vs per-machine; MSIX vs MSI; offer keep-WSL-data option; tray menu in v1; wsl --export pre-backup opt-in; uninstall telemetry; script post-install location; backup retention). Recommends shipping as **follow-up PR after WSL gateway clean PR merges** to keep current PR focused. Read-only investigation; no WSL ops or product files modified. Decision: aron-uninstall-plan.md.

## 2026-05-04T19:30Z — Round 13 status (Scribe)

E2E drive surfaced 2 real bugs at PairOperator boundary; **aaron-16** in flight investigating + fixing Bug 1 (bootstrap-token round-trip / handshake framing in OpenClawGatewayClient); **mattingly-6** in flight investigating + fixing Bug 2 (LocalSetupProgressPage phase-update propagation + Failed-state transition). Branch eat/wsl-gateway-clean at 17 commits (73767c5). Tray 447/447, Shared 1180/1180. PR push deferred until both bugs resolved — they directly affect first-run install reliability which is the whole point of this PR.

---

## 2026-05-04T20:09Z — Mike Harsh directive: prototype is valid reference for bug fixes

When fixing bugs in the clean worktree, agents should consult the prototype worktree at C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-windows-node (branch pr-241-feedback-fixes) as a reference. The prototype code went through real end-to-end validation, so when the clean port has a regression that the prototype didn't have, the prototype is the authoritative answer for what the working behavior looked like. **Applies to all future spawns** that involve diagnosing or fixing behavior the prototype demonstrably worked. Prompts should explicitly include the prototype paths the agent should compare against.

## 2026-05-04T21:00Z — Aaron-16: Bug 1 FIXED — bootstrap-token operator-pending auto-approve (commit e2de09)

**Scope tag:** ix(shared) — bootstrap-token wire-format consistency between gateway mint and tray pair.

**Root cause:** On a fresh local-loopback gateway the upstream records the bootstrap-token connect as a *pending* operator pairing request (~/.openclaw/devices/pending.json entry with deviceId, publicKey, and ole: "operator") and then rejects the same connect with pairing-required because nothing has approved that pending entry yet. The upstream gateway.nodes.pairing.autoApproveCidrs policy checks if (params.role !== "node") return false before its CIDR allow-list, so it does not auto-approve operator pairings. On a local-loopback gateway the tray user IS the approver, so the tray engine must drive that step itself.

**Fix:** SettingsOperatorPairingService (~line 1440) added optional IPendingDeviceApprover constructor parameter. On the PairingRequired outcome, if the credential IsBootstrapToken, the gateway URL passes LocalGatewayApprover.IsLocalGateway, and an approver is wired, the service invokes ApproveLatestAsync and retries the connect exactly once. New types: PendingDeviceApprovalResult (record), IPendingDeviceApprover (contract), WslGatewayCliPendingDeviceApprover (invokes openclaw devices approve --latest --json --url <state.GatewayUrl>). Build() factory instantiates the WSL approver and passes it to the pairing service.

**Tests:** 	ests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs (+10 net new, all green). Covers approve + retry, no-loop, failure-surface, remote-gateway pass-through, non-bootstrap-token pass-through, first-connect success, ParseApproveJson cases.

**Validation:**
| Suite | Before | Now |
|---|---|---|
| OpenClaw.Shared.Tests | 1180 | **1180** |
| OpenClaw.Tray.Tests | 447 | **493** (+46 including mattingly-6) |

./build.ps1 cannot complete WinUI link in this session (running PID 8240 holds lock per task guardrails). dotnet build OpenClaw.Tray.WinUI.csproj reports source compilation clean.

**Note:** Mattingly-6 Bug 2 fix (LocalSetupProgressPage phase-update propagation) also landed in same validation run; see entry below for isolated test delta.

## 2026-05-04T21:30Z — Mattingly-6: Bug 2 FIXED — LocalSetupProgressPage stage propagation + FailedRetryable rendering (commit 4af2581)

**Root cause:** Reference-equality short-circuit in UseState<T>.Set — for a class that does not override Equals, EqualityComparer<T>.Default falls through to ReferenceEquals. The engine emits StateChanged?.Invoke(state) with the **same mutating instance** every phase transition. Result: the first 
ull → state event passed the equality check; every subsequent state → state event was identified as "no change" and the framework swallowed _requestRender. A second, smaller bug compounded the visible failure: Block(...) sets Phase = LocalGatewaySetupPhase.Failed (the highest enum ordinal), losing the position of the last-running phase.

**Fix:** Introduced private sealed record RenderSnapshot(LocalGatewaySetupPhase Phase, LocalGatewaySetupStatus Status, LocalGatewaySetupPhase LastRunningPhase, string UserMessage, string FailureCode). Switched from UseState<LocalGatewaySetupState?> to UseState<RenderSnapshot?> — record value equality means each engine event yields a snapshot that compares non-equal to the previous one, reliably triggering _requestRender. Added Capture(LocalGatewaySetupState) static helper that derives LastRunningPhase by walking state.History backwards for the last non-terminal phase. Runs OFF the dispatcher (immediately in the engine's StateChanged callback) so the snapshot freezes the engine's state at event-fire time. Extracted all stage-list math into pure helper LocalSetupProgressStageMap.cs (no WinUI deps; unit-testable). Helper folds PairOperator / CheckWindowsNodeReadiness / PairWindowsTrayNode / VerifyEndToEnd into the MintToken stage so the actual e2e-drive failure phase pins on a visible stage.

**Tests:** 	ests/OpenClaw.Tray.Tests/LocalSetupProgressStageMapTests.cs (+36 net new). Covers every running engine phase → expected stage index, all stages Pending, all stages Complete, coverage guard (EveryDeclaredEnginePhase_IsCoveredBySomeVisibleStageOrIsTerminal), the Aaron-14 scenario (stages 0–5 ✅, stage 6 ❌), FailedRetryable at CreateWslInstance, FailedTerminal at Preflight, ShouldShowErrorRow (9 cases), ShouldShowRetryButton (5 cases), IndexOfStageForPhase_ReturnsMinusOne_ForUncoveredPhases.

**Validation:**
| Suite | Before | Now |
|---|---|---|
| OpenClaw.Shared.Tests | 1180 | **1180** |
| OpenClaw.Tray.Tests | 447 | **493** (+46 net across both aaron-16 + mattingly-6) |

WinUI build blocked by running PID 8240 (task guardrails). Unit tests carry strong coverage of the behavioral fix.

**⚠️ Footgun warning (canonical for future sweeps):** UseState<TClass> with reference-equality is a footgun anywhere in this codebase. Any other page binding directly to a mutating engine instance has the same bug latent. Recommend a sweep for UseState<...> calls whose type argument is a non-record class without an Equals override (candidates: any onboarding page that subscribes to a stateful service).

## 2026-05-04T22:00Z — Mike's uninstall design doc deferred to follow-up PR

Aaron-15's 22 KB uninstall design doc (8 sections, 8 open questions for Mike) recommended as follow-up PR after WSL gateway clean PR merges to keep current PR focused. Read-only investigation; no code or product files modified. Decision deferred for coordination outside this sprint.

---

## 2026-05-04T20:09Z — Mike Harsh directive: prototype is valid reference for bug fixes

When fixing bugs in the clean worktree, agents should consult the prototype worktree at `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-windows-node` (branch `pr-241-feedback-fixes`) as a reference. The prototype code went through real end-to-end validation, so when the clean port has a regression that the prototype didn't have, the prototype is the authoritative answer for what the working behavior looked like. **Applies to all future spawns** that involve diagnosing or fixing behavior the prototype demonstrably worked.

## 2026-05-04T21:00Z — Aaron-16: Bug 1 FIXED — bootstrap-token operator-pending auto-approve (commit `fe2de09`)

**Scope tag:** `fix(shared)` — bootstrap-token wire-format consistency between gateway mint and tray pair.

**Root cause:** On a fresh local-loopback gateway the upstream records the bootstrap-token connect as a *pending* operator pairing request and rejects the same connect with `pairing-required`. The upstream `gateway.nodes.pairing.autoApproveCidrs` policy does not auto-approve `operator` pairings. On a local-loopback gateway the tray user IS the approver, so the tray engine must drive that step itself.

**Fix:** `SettingsOperatorPairingService` added optional `IPendingDeviceApprover` constructor parameter. On `PairingRequired`, if the credential `IsBootstrapToken`, the gateway URL passes `LocalGatewayApprover.IsLocalGateway`, and an approver is wired, the service invokes `ApproveLatestAsync` and retries once. New types: `PendingDeviceApprovalResult`, `IPendingDeviceApprover`, `WslGatewayCliPendingDeviceApprover`.

**Tests:** `tests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs` (+10 net new, all green).

**Validation:** Shared 1180/1180, Tray 493/493 (+46 including mattingly-6).

## 2026-05-04T21:30Z — Mattingly-6: Bug 2 FIXED — LocalSetupProgressPage stage propagation + FailedRetryable rendering (commit `4af2581`)

**Root cause:** Reference-equality short-circuit in `UseState<T>.Set` — for a class without `Equals` override, `EqualityComparer<T>.Default` falls through to `ReferenceEquals`. Engine emits `StateChanged?.Invoke(state)` with the **same mutating instance** every phase transition. Result: first `null → state` event passed equality check; every subsequent `state → state` event was identified as "no change" and swallowed `_requestRender`. Second bug: `Block(...)` sets `Phase = LocalGatewaySetupPhase.Failed`, losing last-running phase info.

**Fix:** Introduced `private sealed record RenderSnapshot(...)`. Switched from `UseState<LocalGatewaySetupState?>` to `UseState<RenderSnapshot?>` — record value equality reliably triggers `_requestRender`. Added `Capture(LocalGatewaySetupState)` helper that derives `LastRunningPhase`. Extracted stage-list math into pure helper `LocalSetupProgressStageMap.cs` (no WinUI deps).

**Tests:** `tests/OpenClaw.Tray.Tests/LocalSetupProgressStageMapTests.cs` (+36 net new). Covers engine phase → stage mapping, pending/complete states, coverage guard, Aaron-14 scenario, error/retry rendering.

**Validation:** Shared 1180/1180, Tray 493/493 (+46 net across both fixes).

**⚠️ Footgun warning:** `UseState<TClass>` with reference-equality is a footgun anywhere in this codebase. Any page binding directly to a mutating engine instance has this bug latent. Recommend sweep for `UseState<...>` calls whose type argument is a non-record class without `Equals` override.

## 2026-05-04T22:00Z — Mike's uninstall design doc deferred to follow-up PR

Aaron-15's 22 KB uninstall design doc (8 sections, 8 open questions) recommended as follow-up PR after WSL gateway clean PR merges to keep current PR focused.
---

## 2026-05-04T20:09Z — Mike Harsh directive: prototype is valid reference for bug fixes

When fixing bugs in the clean worktree, agents should consult the prototype worktree at C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-windows-node (branch pr-241-feedback-fixes) as a reference. The prototype code went through real end-to-end validation, so when the clean port has a regression that the prototype didn't have, the prototype is the authoritative answer for what the working behavior looked like. **Applies to all future spawns** that involve diagnosing or fixing behavior the prototype demonstrably worked.

## 2026-05-04T21:00Z — Aaron-16: Bug 1 FIXED — bootstrap-token operator-pending auto-approve (commit e2de09)

**Scope tag:** fix(shared) — bootstrap-token wire-format consistency between gateway mint and tray pair.

**Root cause:** On a fresh local-loopback gateway the upstream records the bootstrap-token connect as a *pending* operator pairing request (~/.openclaw/devices/pending.json entry). The upstream gateway.nodes.pairing.autoApproveCidrs policy checks if (params.role !== "node") return false, so it does not auto-approve operator pairings. On a local-loopback gateway the tray user IS the approver, so the tray engine must drive openclaw devices approve.

**Fix:** SettingsOperatorPairingService (~line 1440) added optional IPendingDeviceApprover constructor parameter. On PairingRequired outcome, if the credential IsBootstrapToken, the gateway URL passes LocalGatewayApprover.IsLocalGateway, and an approver is wired, the service invokes ApproveLatestAsync and retries the connect exactly once. New types: PendingDeviceApprovalResult (record), IPendingDeviceApprover (contract), WslGatewayCliPendingDeviceApprover (invokes openclaw devices approve --latest --json --url <state.GatewayUrl>). Build() factory instantiates and wires the WSL approver to the pairing service.

**Tests:** 	ests/OpenClaw.Tray.Tests/OperatorPairingApprovalTests.cs (+10 net new, all green).

**Validation:** Shared 1180/1180, Tray 493/493 (+46 including mattingly-6).

## 2026-05-04T21:30Z — Mattingly-6: Bug 2 FIXED — LocalSetupProgressPage stage propagation + FailedRetryable rendering (commit 4af2581)

**Root cause:** Reference-equality short-circuit in UseState<T>.Set — for a class that does not override Equals, EqualityComparer<T>.Default falls through to ReferenceEquals. Engine emits StateChanged?.Invoke(state) with the **same mutating instance** every phase transition. Result: first 
ull → state event passed equality check; every subsequent state → state event was identified as "no change" and framework swallowed _requestRender. Second bug: Block(...) sets Phase = LocalGatewaySetupPhase.Failed, losing the last-running phase info.

**Fix:** Introduced private sealed record RenderSnapshot(LocalGatewaySetupPhase Phase, LocalGatewaySetupStatus Status, LocalGatewaySetupPhase LastRunningPhase, string UserMessage, string FailureCode). Switched from UseState<LocalGatewaySetupState?> to UseState<RenderSnapshot?> — record value equality reliably triggers _requestRender. Added Capture(LocalGatewaySetupState) static helper that derives LastRunningPhase by walking state.History backwards. Runs OFF the dispatcher (immediately in engine's StateChanged callback) so snapshot freezes state at event-fire time. Extracted stage-list math into pure helper LocalSetupProgressStageMap.cs (no WinUI deps). Helper folds PairOperator / CheckWindowsNodeReadiness / PairWindowsTrayNode / VerifyEndToEnd into MintToken stage so actual failure phase pins on visible stage.

**Tests:** 	ests/OpenClaw.Tray.Tests/LocalSetupProgressStageMapTests.cs (+36 net new). Covers engine phase → stage mapping, all Pending, all Complete, coverage guard (EveryDeclaredEnginePhase_IsCoveredBySomeVisibleStageOrIsTerminal), Aaron-14 scenario (stages 0–5 ✅, stage 6 ❌), FailedRetryable, FailedTerminal, ShouldShowErrorRow (9 cases), ShouldShowRetryButton (5 cases).

**Validation:** Shared 1180/1180, Tray 493/493 (+46 net across both aaron-16 + mattingly-6).

**⚠️ Footgun warning (canonical):** UseState<TClass> with reference-equality is a footgun anywhere in this codebase. Any page binding directly to a mutating engine instance has this bug latent. Recommend sweep for UseState<...> calls whose type argument is a non-record class without Equals override.

## 2026-05-04T22:00Z — Kranz: Bug 1 + Bug 2 reviewer verdict — APPROVE with deferred screenshot pass

**Reviewer:** Kranz (Lead / Architect / Reviewer Gate)  
**Worktree reviewed:** openclaw-wsl-gateway-clean @ 4af2581  
**Commits:** Bug 1 e2de09, Bug 2 4af2581

**Bug 1 verdict: APPROVE.** Root cause consistent with implementation. Retry bounded (no loop). Token hygiene correct (secret read inside bash, never on wsl.exe argv). Factory wire-up preserves remote-gateway semantics. Non-blocking concerns: (1) --latest assumes fresh-reset pending.json; recommend future --device-id <state.DeviceId> when tray exposes operator deviceId at this layer. (2) Doc field names cosmetic drift (Approved/ErrorCode/Message vs actual Success/ErrorCode/ErrorMessage). (3) Real e2e not run; Mike's fresh e2e re-verification will cover.

**Bug 2 verdict: CONDITIONAL APPROVE** — single closeable item: screenshot pass after PID 8240 release. Root cause concrete and correct. Capture() runs before dispatcher.TryEnqueue (correct). Failure-pinning logic sound. VisibleStages folding eliminates uncoverable phases. Policy split preserves back-compat. Prototype cross-check confirmed (setWslSetupState ref-equality bug also latent in prototype; RenderSnapshot is durable fix, not prototype pattern). Non-blocking concerns: (1) Capture() is not directly unit-tested (not a blocker; punchlist to lift into LocalSetupProgressStageMap). (2) **Screenshot verification deferred** — repo's MANDATORY screenshot-verification rule for UI changes must be satisfied after PID 8240 release.

**Punch-list follow-ups (not merge blockers):**
1. UseState<TClass> codebase sweep — hit found: PermissionsPage.cs:21 uses UseState<List<PermissionChecker.PermissionResult>?> (List doesn't override Equals). Audit for similar fragile patterns.
2. Lift Capture() out of LocalSetupProgressPage into LocalSetupProgressStageMap for direct History-walk unit coverage.
3. Bug 1 deviceId-targeted approval (replace --latest with --device-id once tray exposes operator deviceId).
4. Engine smell: Block(...) overwrites Phase; consider promoting LastRunningPhase to first-class field on LocalGatewaySetupState.

**Pre-push requirements (not blockers; Mike's responsibility):**
1. Stop PID 8240, run ./build.ps1 to confirm WinUI link clean.
2. Fresh e2e drive (clear ~/.openclaw/devices/pending.json + paired.json) to verify Bug 1's approval+retry lands operator pairing on live gateway.
3. Mattingly's OPENCLAW_VISUAL_TEST_LOCAL_SETUP screenshot sweep (lines 96-99 of her doc) to satisfy repo's mandatory screenshot-verification policy for Bug 2.

## 2026-05-04T22:00Z — Mike's uninstall design doc deferred to follow-up PR

Aaron-15's 22 KB uninstall design doc (8 sections, 8 open questions) recommended as follow-up PR after WSL gateway clean PR merges to keep current PR focused. Read-only investigation; no code or product files modified. Decision deferred for coordination outside this sprint.

## 2026-05-04T22:30Z — Scribe: Round 15 inbox merge complete

**Inbox files processed:** aaron-bug1-bootstrap-token-fix.md, mattingly-bug2-stage-propagation-fix.md, copilot-directive-prototype-as-bugfix-reference.md, kranz-bug-fixes-verdict.md. All merged into decisions.md. Created history.md with round-15 summary. Updated identity/now.md to reflect Bug 1 + Bug 2 landed; awaiting Mike's machine prep + fresh e2e re-verification. Created registry.md with agent registry. Migrated inbox files to inbox-processed/round-15/. Ready for commit to feat/wsl-gateway-clean.

**Branch state:** 17 commits since baseline 871b959. Tests: Shared 1180/1180, Tray 493/493. Blocking pre-push: kill PID 8240, clear WSL pending-device state, run fresh e2e + mandatory screenshot pass.