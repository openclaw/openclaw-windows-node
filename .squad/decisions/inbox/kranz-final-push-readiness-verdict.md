# Kranz — Final push-readiness verdict for `feat/wsl-gateway-clean`

- **Date:** 2026-05-05
- **Reviewer:** Kranz (Lead / Architect / Final Reviewer Gate)
- **Worktree:** `..\openclaw-wsl-gateway-clean` @ `6e532f7`
- **Scope reviewed:** 25 commits against `871b959`, 8 of them bug fixes (Bug 1 ×6, Bug 2 ×1, Bug 3 ×1)
- **Tests re-run?** No. Validation cited from agent reports per AGENTS.md note (running tray executable would conflict; Bostick's Round 6 cited Tray 524/524, Shared 1180/1180 from Aaron-22, against the rebuilt DLL that drove Round 6 GREEN).

---

## Per-bug verdict

### Bug 1 — Operator pairing auto-approve (Phase 12) — **APPROVE** ✅

Final shape (`LocalGatewaySetup.cs:1657-2098`) is internally coherent. I traced each layered fix and confirmed it is retained correctly with no vestigial logic:

- **Aaron-16 / `fe2de09`** — bootstrap-token wire-format consistency + the `IPendingDeviceApprover` seam. Seam is the right primitive: factory at `:2666` constructs ONE approver and threads it into BOTH the operator-pairing service (`:2680`) and the windows-tray-node provisioner (`:2681`). Reuse pattern is clean.
- **Aaron-17 / `3927451`** — drop `--url` to bypass `ensureExplicitGatewayAuth`. Confirmed: `BuildPreviewScript`/`BuildCommitScript` (`:1966-1992`) emit no `--url` flag.
- **Aaron-18 / `6942a81`** — two-stage approve (preview discover → explicit-requestId commit). `ApproveLatestAsync` body is exactly that two-stage shape (`:1738-1786`).
- **Aaron-19 / `05f7be0`** — 750ms retry of stage 1 on failure + stderr surfacing. Retry lives in `RunStage1WithRetryAsync` (`:1789-1825`); critically, the retry is now correctly gated behind the part-6 success check so the happy path does NOT burn the 750ms delay (`:1800` "treat exit-0 OR a parseable preview JSON as stage-1 success").
- **Aaron-20 / `f2dec42`** — C# pre-read of the gateway token + single-quoted shell-literal interpolation + STDOUT (not just stderr) surfacing. `ReadGatewayTokenAsync` (`:1833-1861`) does the separate `cat`; `IsSafeTokenForSingleQuoteInterpolation` (`:1947-1956`) guards interpolation; `BuildStage1Failure`/`BuildStage2Failure` (`:1863-1927`) surface both streams from both attempts with intelligent de-duplication.
- **Aaron-21 / `4d36dcd`** — the actual fix: parse JSON before exit-code gate. `ApproveLatestAsync` (`:1758-1766`) calls `ParsePreviewJson(stage1.Result.StandardOutput)` FIRST and only falls through to `BuildStage1Failure` when no usable preview shape can be extracted AND exit was non-zero. The class-level comment block (`:1747-1757`) explains the CLI v2026.5.3-1 deterministic exit-1 oddity that motivated this gate flip — clear, maintainable, and references the smoking-gun Round-4 capture.

End-to-end correctness is anchored by Bostick's Round 6 timeline: Phase 12 OK, gateway `paired.json` got the engine-written operator entry, no manual approval needed.

Concerns (non-blocking, all carry forward):

1. `--latest` semantic risk (carried from my earlier review) — still applies. Acceptable for fresh-reset local-loopback. Future: prefer `--device-id <state.DeviceId>`.
2. Stage-1 STDOUT surfacing in `BuildStage1Failure` is permanent; per Bostick's recommendation, this is intentional — keep it as a diagnostic aid even now that Bug 1 is closed.

### Bug 2 — `LocalSetupProgressPage` `RenderSnapshot` — **APPROVE** ✅

(Already conditionally approved in my Round 15 verdict; conditional gate was the screenshot pass.) Bostick's Round 6 captured `page-00..03.png` against the real engine across the entire onboarding flow; UI rendered every phase transition correctly and auto-navigated past `LocalSetupProgress` on `Status=Complete`. The conditional is **closed**. No regression observed across all six rounds.

### Bug 3 — Phase 14 role-upgrade auto-approve — **APPROVE** ✅

`SettingsWindowsTrayNodeProvisioner.PairAsync` (`LocalGatewaySetup.cs:2139-2199`) mirrors the Phase 12 pattern exactly: try connect → on failure, gate on `_pendingApprover != null && LocalGatewayApprover.IsLocalGateway(state.GatewayUrl)` → invoke `ApproveLatestAsync` → retry connect once. Reuse of the **same approver instance** is the right call — no new shell logic, no copy/paste of stage-1/2 reasoning, single source of truth for the gateway-CLI handshake. Code quality of the reuse: clean.

Test coverage (`WindowsTrayNodePairingApprovalTests.cs`, 8 tests) is meaningful and exhaustive — happy path, approver failure, no-pending-entries, retry-still-fails, remote-skip, fast-path no-approve, no-approver legacy passthrough, `OperationCanceled` passthrough. None of these are shape-pinning; each asserts a distinct semantic.

End-to-end anchor: Round 6 showed gateway `paired.json` with `6518435058e5e9c3` (windows tray, role=`node`, operator scopes) — the engine-written role-upgrade outcome. Tray reached the post-onboarding "Grant Permissions" page, which is the original Round-1 success criterion.

### Spot-checks on test quality (Aaron-21 + Aaron-22)

- **Aaron-21 gate-flip tests** — 5 distinct semantics: exit-1+valid-JSON proceeds-to-stage-2, exit-0+valid-JSON proceeds-to-stage-2, exit-1+empty-stdout fails-with-diagnostics, exit-1+malformed-JSON fails-with-diagnostics, exit-1+valid-JSON does NOT retry (this last one was the perf-regression risk Bug 1 part 6 created — explicitly pinned). These exercise the exact gate flip. **Real coverage, not pinning.**
- **Aaron-22 Phase 14 tests** — see list above. The `RemoteGateway_ConnectFails_DoesNotApprove` and `NoApproverWired_PreservesLegacyFailureCode` tests in particular guard against future regressions of the local-gate and back-compat surfaces. **Real coverage, not pinning.**

---

## Final overall push-readiness verdict — **GREEN** ✅

**The branch is ready to push as-is.** No code changes required from this gate. All three bugs are closed end-to-end with empirical evidence, the final shape of the approver is coherent and well-commented, the seam reuse for Bug 3 is the right pattern, and test coverage is meaningful at every layer.

---

## Recommended push approach

- **Do NOT squash.** The 6-commit Bug 1 sequence is now part of the diagnostic record — every commit message names the CLI version, the failure mode it ruled out, and the next hypothesis. Squashing would erase a forensic trail that is going to matter the next time CLI v2026.5.4 ships and one of these gates flips again. Push the 25 commits as-is.
- **PR title:** `feat: WSL gateway clean port (forked onboarding + local-loopback gateway + auto-approve)`
- **PR description outline:**
  1. Scope: Phases 1–14 of the clean-branch plan; forked onboarding UX; app-owned `OpenClawGateway` Ubuntu instance; localhost-only.
  2. End-to-end status: GREEN against CLI v2026.5.3-1 — Round 6 e2e drive (`bostick-bug1-reverify.md` "Path B re-drive — Round 6"), `Status=Complete`, post-onboarding Grant Permissions page reached.
  3. Bug-fix arc: link Bug 1 (6 commits — explain the v2026.5.3-1 preview-mode-exit-1 finding briefly and link to `aaron-bug1-final-gate-fix.md`), Bug 2 (`4af2581` — `RenderSnapshot`), Bug 3 (`6e532f7` — Phase 14 reuse).
  4. Test deltas: Tray 447 → 524 (+77), Shared 1172 → 1180 (+8). All green.
  5. Punch list: explicit "follow-up PR" list (below).
  6. Out of scope: WSL prototype-litter cleanup, Aaron-15 uninstall plan.

---

## Ship-in-this-PR vs follow-up PR

**Ship in this PR (already in the 25 commits):**
- Bug 1 fix (all 6 commits, including the permanent stage-1 STDOUT diagnostic surface — keep, per Bostick).
- Bug 2 `RenderSnapshot` fix.
- Bug 3 Phase 14 wiring + 8 new tests.
- Phase 1–14 clean port (everything before `fe2de09`).

**Follow-up PRs (do NOT block push):**

1. **`UseState<TClass>` codebase sweep** (Mattingly Lesson #1 / my earlier punch-list item). `PermissionsPage.cs:21` `UseState<List<PermissionResult>?>` is the candidate I flagged. Empirically it does NOT manifest today (Round 6 page-03.png shows the page rendering all four permission states correctly — `PermissionChecker.CheckAllAsync` returns a fresh list reference per call, so `EqualityComparer<T>.Default.Equals` always sees inequality and re-renders). It is a latent footgun, not an active bug. Sweep can wait. **Defer.**
2. **Lift `Capture()` out of `LocalSetupProgressPage` into `LocalSetupProgressStageMap`** for direct unit coverage of the History-walk. (~30 lines + 4 tests.)
3. **Bug 1 deviceId-targeted approval** — replace `--latest` with `--device-id <state.DeviceId>`. Eliminates the stale-pending race.
4. **Stale `Token` field in `settings.json`** — empty even at terminal happy state (Bostick observation). One-line cleanup or deprecation comment. Confirmed not on the critical happy-path read path.
5. **Document `OPENCLAW_FORCE_ONBOARDING` + `OPENCLAW_ONBOARDING_START_ROUTE=LocalSetupProgress` as the known-working e2e harness** in a CONTRIBUTING-style note.
6. **Aaron-15's uninstall plan** (22KB, 8 open Mike-questions) — already flagged as follow-up PR by Aaron.
7. **Mattingly-3's 5 low-confidence translation strings** — follow-up loc PR.
8. **DEFECT-WSL-PROTOTYPE-LITTER** (17 leftover distros from earlier phases) — explicitly out of scope per Mike's standing rule.
9. **Engine-side `LastRunningPhase` first-class field** (Mattingly Lesson #2 / my earlier punch list #4) — engine-clarity pass.

---

## Code-quality observations on the final approver shape

`WslGatewayCliPendingDeviceApprover` (`LocalGatewaySetup.cs:1657-2098`) read end-to-end. It is **coherent and free of vestigial logic.** Specifically:

- The class-level comment block is a forensic-grade explanation of why each mitigation exists. Future readers staring at "why are we reading the token in C# instead of `$(cat …)`?" get the answer in-line. **Keep.**
- The retry gate at `:1800` (`first.Success || ParsePreviewJson(first.StandardOutput).Success`) is the single-line "fix the perf regression Aaron-21 introduced" — it correctly avoids wasting 750ms on the deterministic-exit-1 happy path. Without this line, every successful call would burn the retry delay. Critical, easy-to-miss, well-placed.
- `BuildStage1Failure` de-duplicates `attempt2` stderr/stdout against `attempt1` (`:1891-1898`) — small but right; failure messages don't double-print on a deterministic-failure mode.
- `BuildStage2Failure` keeps a backwards-compatible bare-stderr `ErrorMessage` shape when only stderr is present (`:1922-1925`) — preserves consumers of the old failure shape.
- `ShellQuoteScalar` (`:2097`) is the standard POSIX `'\''` escape; correct.
- `IsSafeTokenForSingleQuoteInterpolation` and `IsSafeRequestId` are defense-in-depth that complements the single-quoted interpolation. Belt-and-suspenders is appropriate for a security-relevant shell construction; **keep.**

**No dead code from intermediate fixes.** The `--url` removal (Aaron-17) is invisible in the final source — it was a deletion, no ghost flag remains. The two-stage shape (Aaron-18) is the only shape present. The C# token pre-read (Aaron-20) replaced the embedded `$(...)` substitution wholesale — no fallback remains. The JSON-first gate (Aaron-21) is the only `if (!stage1.Result.Success)` branch reached. Clean.

**Bug 2 / Bug 3 wiring** — verified via `git grep`: the `IPendingDeviceApprover` seam is consumed in exactly two places (`SettingsOperatorPairingService:1487` and `SettingsWindowsTrayNodeProvisioner:2173`) and constructed exactly once (`:2666`), shared between both consumers. **Correct seam, correct reuse, no parallel implementations.**

---

## Lessons learned from the 6-round Bug 1 cycle (worth canonicalizing in `decisions.md`)

When a CLI invocation works manually but fails inside the engine's invocation context, the failure is almost never in our script — it is in the layered argv encoding (.NET `ProcessStartInfo.ArgumentList` → `wsl.exe` → `bash -lc`) or in the CLI's own protocol (e.g. v2026.5.3-1's deterministic exit-1 in preview mode). Round 6 of Bug 1 fell out of two disciplined moves: (a) **diagnose by surfacing both stdout AND stderr, not just stderr** — the smoking-gun JSON was on stdout the whole time, and three rounds were burned on "empty stderr means the script never ran" before we instrumented stdout; (b) **read tokens and other secrets in C# via a separate simple `cat`, then interpolate as single-quoted shell literals** — this eliminates `$(...)` substitutions and embedded double quotes that .NET argv encoding can mangle. Future canonical rule for the clean branch: *any* CLI invocation that goes `.NET → wsl.exe → bash -lc` should (1) surface both streams in the failure path as a permanent diagnostic, and (2) treat the CLI's stdout JSON as the primary success signal where one is documented, with exit code as the secondary discriminator only. Keeping the stage-1 STDOUT surface in production after Bug 1 closes is exactly this rule applied.

---

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
