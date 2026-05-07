# Kranz — Reviewer-gate verdict on Bug 1 (`fe2de09`) + Bug 2 (`4af2581`)

- **Date:** 2026-05-04
- **Reviewer:** Kranz (Lead / Architect / Reviewer Gate)
- **Worktree reviewed:** `..\openclaw-wsl-gateway-clean` @ `4af2581`
- **Prototype cross-check:** `..\openclaw-windows-node` @ `pr-241-feedback-fixes`
- **Tests re-run?** No — Aaron-15 and Mattingly-6 reports (Shared 1180/1180, Tray 493/493) inspected and accepted on diff evidence; PID 8240 guardrail prevents safe rebuild.

---

## Bug 1 — `fe2de09` (Aaron-15): bootstrap-token operator-pairing auto-approve

**Verdict: APPROVE.**

What I verified:

- The final root-cause theory in Aaron's doc (lines 9–36) — *operator pending pairings need explicit `openclaw devices approve`; `node-pairing-auto-approve.ts` short-circuits on `params.role !== "node"`* — is consistent with the actual implementation. The fix gates approval on `credential.IsBootstrapToken && LocalGatewayApprover.IsLocalGateway(state.GatewayUrl) && _pendingApprover != null` (`LocalGatewaySetup.cs:~1481-1497`), so non-bootstrap (revoked-deviceToken) and remote-gateway paths are untouched.
- Retry is bounded: a second `PairingRequired` after approval is surfaced as-is — no loop. `OperatorPairingApprovalTests.PairingRequiredTwice_DoesNotLoop` exercises exactly that (asserts `connector.ConnectCalls == 2`, `approver.ApproveCalls == 1`, `ErrorCode == "operator_pairing_required"`).
- Token hygiene is correct: `WslGatewayCliPendingDeviceApprover` reads `/var/lib/openclaw/gateway-token` via `"$(cat …)"` *inside* the bash heredoc (`LocalGatewaySetup.cs:~1685-1700`); the secret never reaches `wsl.exe` argv. `ShellQuoteScalar` properly quotes `state.GatewayUrl` and the CLI name.
- Factory wire-up at `LocalGatewaySetup.cs:~2256-2270` instantiates the WSL approver and threads it into `SettingsOperatorPairingService`. Default-null constructor parameter preserves the remote-gateway-only constructor semantics for any future caller.

Concerns (non-blocking):

1. **`--latest` semantic risk.** `openclaw devices approve --latest` assumes the newest pending entry is ours. True on a fresh-reset local gateway (Aaron's tested path); could approve the wrong device if the user re-runs setup with a stale `~/.openclaw/devices/pending.json` and an unrelated pending request raced in between. Acceptable for local-loopback but worth a follow-up to prefer `--device-id <state.DeviceId>` once the tray exposes its own deviceId at this layer.
2. **Doc drift.** Aaron's doc (line 50) names the record fields `Approved, ErrorCode, Message`; actual record is `(Success, ErrorCode, ErrorMessage)`. Cosmetic only — code is internally consistent.
3. **Real e2e not run.** Behavior against the live gateway after approval (does the gateway accept a re-connect with the *same* bootstrap token post-approval, or does it now demand the device-token path?) is asserted only by Aaron's investigation trace, not by this commit. Mike's fresh e2e re-verification covers this — already called out in Aaron's doc lines 111-115.

---

## Bug 2 — `4af2581` (Mattingly-6): `LocalSetupProgressPage` stage propagation

**Verdict: CONDITIONAL APPROVE — single closeable item: screenshot pass after PID 8240 release.**

What I verified:

- Root cause is concrete and correct. `FunctionalUI.cs:186-194` does `EqualityComparer<T>.Default.Equals(h.Value, next)`; for a non-record class this resolves to `ReferenceEquals`. Engine emits `StateChanged?.Invoke(state)` at `LocalGatewaySetup.cs:2077` with the same mutating `LocalGatewaySetupState` instance from `SaveAndPublishAsync`. The "first event renders, all subsequent events swallowed" symptom follows directly. Replacing the stored value with a `RenderSnapshot` record (value-equality on `Phase, Status, LastRunningPhase, UserMessage, FailureCode`) is the right primitive.
- `Capture()` runs on the engine thread *before* `dispatcher.TryEnqueue` (`LocalSetupProgressPage.cs:~123-135`). This is correct: `StateChanged` fires synchronously after each mutation in `SaveAndPublishAsync`, so capture and mutation cannot interleave; the snapshot freezes the post-mutation values for the dispatcher dequeue.
- Failure-pinning logic is sound. `Capture()` derives `LastRunningPhase` by walking `state.History` backwards skipping terminal phases, then overrides with `state.Phase` while still running. `LocalSetupProgressStageMap.ComputeStageState` then uses `lastRunningPhase` ordinal — not `Phase=Failed` — to pick the correct visible stage on `FailedRetryable`/`FailedTerminal`/`Phase==Failed`. The Aaron-14 scenario (`FailedRetryable_AtPairOperator_PinsFailureOnLastVisibleStage`) drives `(Failed, FailedRetryable, PairOperator)` and asserts stages 0–5 ✅, stage 6 ❌. That test would have failed under the pre-fix code.
- `VisibleStages` change folds `PairOperator / CheckWindowsNodeReadiness / PairWindowsTrayNode / VerifyEndToEnd / ElevationCheck` into existing visible stages. `EveryDeclaredEnginePhase_IsCoveredBySomeVisibleStageOrIsTerminal` is a real coverage guard — every running enum value is now reachable from a stage, future drift will fail loudly.
- Policy split (`MapStatusToNextButtonState(bool hasSnapshot, status)`) preserves the legacy `(LocalGatewaySetupState?, status)` overload so existing tests of that signature don't churn. Back-compat clean.
- **Prototype cross-check confirmed.** Mattingly's addendum (lines 120-154) is sound. I read `..\openclaw-windows-node\src\OpenClaw.Tray.WinUI\Onboarding\Pages\ConnectionPage.cs:415-432` directly: the same `setWslSetupState(state)` ref-equality bug is latent there, but the sibling `setWslSetupStatus(setupMessage)` and `setStatusMsg(setupMessage)` calls — strings derived from `BuildWslSetupStatusMessage(state)` that vary per phase — force re-renders every event and accidentally mask the binding bug. The prototype is not a pattern to port; `RenderSnapshot` is the durable fix.

Concerns:

1. **Capture() is not directly unit-tested.** It's a `private static` in the page, so the helper tests don't exercise the History-walk. The visual-test harness was patched (`StartPhase(MintBootstrapToken)` before `Block(...)`) so `LastRunningPhase` pins correctly in synthetic states, but that's smoke coverage. **Closeable** by lifting `Capture` into `LocalSetupProgressStageMap` (or a sibling pure helper) and adding 3-4 history-walk tests. Not a blocker for this commit; file as a punch-list item.
2. **Screenshot verification deferred.** Per repo's MANDATORY screenshot-verification rule for any UI change, this fix is unverified visually. Mattingly's doc lines 82-90 already calls this out and lines 92-101 supply the exact `OPENCLAW_VISUAL_TEST_LOCAL_SETUP=active:CreateWslInstance | active:MintBootstrapToken | retryable | terminal` sweep. **This is the single conditional-approval gate** — Mike must complete the screenshot pass after stopping PID 8240 before this commit ships.

---

## Recommended follow-ups (punch list)

1. **`UseState<TClass>` codebase sweep (Mattingly's "Lesson #1").** Concrete hit found: `src\OpenClaw.Tray.WinUI\Onboarding\Pages\PermissionsPage.cs:21` — `UseState<List<PermissionChecker.PermissionResult>?>(null)`. `List<T>` does not override `Equals`, so any path that mutates and re-emits the same list reference will swallow re-renders. The page may currently dodge this by always producing a new list, but it's fragile. **Action:** spawn a small ticket for an audit pass (clean-branch only — clean is the deliverable). One file, low scope.
2. **Lift `Capture()` out of `LocalSetupProgressPage` into `LocalSetupProgressStageMap`** so the History-walk has direct unit coverage. Estimated 30 lines + 4 tests.
3. **Bug 1 deviceId-targeted approval.** Replace `--latest` with `--device-id <state.DeviceId>` once the tray exposes its operator deviceId at the pairing-service layer. Eliminates the stale-pending race in #1 above.
4. **Engine-side smell (Mattingly's "Lesson #2").** `Block(...)` overwrites `Phase` with `LocalGatewaySetupPhase.Failed`, losing the failed-at phase. Stage map now reconstructs it from History; consider promoting `LastRunningPhase` to a first-class field on `LocalGatewaySetupState`. Out of scope for these commits — flag for a future engine-clarity pass.

---

## Final overall recommendation

Both commits are architecturally sound and the test coverage materially exercises the bug each one claims to fix. The branch is **ready for Mike to push after**:

1. Stop PID 8240, run `./build.ps1` to confirm the WinUI link step is clean (it was deferred in both fix sessions, not failed).
2. Re-run the e2e drive against a freshly-reset local gateway (clear `~/.openclaw/devices/pending.json` + `paired.json`) to confirm Bug 1's approval+retry path lands the operator pairing on the live gateway.
3. Run Mattingly's `OPENCLAW_VISUAL_TEST_LOCAL_SETUP` screenshot sweep (her doc lines 96-99) to satisfy the repo's mandatory screenshot-verification policy for Bug 2.

No code changes required from this review. Punch-list items above are follow-ups, not merge blockers.
