# Bug 2 fix: LocalSetupProgressPage stage propagation + FailedRetryable rendering

**Author:** mattingly (Frontend / Onboarding UX)
**Date:** 2026-05-04
**Branch:** `feat/wsl-gateway-clean`
**Commit:** `4af2581239f7544df3fc5da92788b3a458ca9042`
**Parent:** `73767c5` (aaron's e2e drive checkpoint)
**Closes:** Bug 2 from `.squad/decisions/inbox/aaron-e2e-drive.md` § 4

## Summary

The `LocalSetupProgressPage` UI did not advance past its first stage and never
transitioned to `FailedRetryable`, despite the underlying engine progressing
through 9+ phases and ultimately failing at `PairOperator` with
`device-auth-invalid` during Aaron's 2026-05-04 e2e drive. Fixed by replacing
the page's `UseState<LocalGatewaySetupState?>` with a value-equal
`RenderSnapshot` record and extracting all stage-list math into a pure,
unit-testable helper.

## Root cause (concrete)

Reference-equality short-circuit in `OpenClawTray.FunctionalUI.Component.UseState<T>.Set`:

```csharp
// FunctionalUI.cs:186, 194
var changed = !EqualityComparer<T>.Default.Equals(h.Value, next);
```

For a class that does not override `Equals`, `EqualityComparer<T>.Default`
falls through to `ReferenceEquals`. The engine
(`Services/LocalGatewaySetup/LocalGatewaySetup.cs:1964`) emits
`StateChanged?.Invoke(state)` with the **same mutating instance** every phase
transition. Result: the first `null → state` event passed the equality check
and rendered Preflight; every subsequent `state → state` event was identified
as "no change" and the framework swallowed `_requestRender`. The page stayed
on stage 1 with the spinner for the full 12-minute run.

A second, smaller bug compounded the visible failure: `Block(...)` sets
`Phase = LocalGatewaySetupPhase.Failed` (the highest enum ordinal), losing the
position of the last-running phase. Even if re-renders had fired, the page
had no way to pin the ❌ on `MintBootstrapToken`/`PairOperator`.

## Fix

1. **Page (`LocalSetupProgressPage.cs`):**
   - Introduced ``private sealed record RenderSnapshot(LocalGatewaySetupPhase Phase, LocalGatewaySetupStatus Status, LocalGatewaySetupPhase LastRunningPhase, string UserMessage, string FailureCode)``.
   - Switched from `UseState<LocalGatewaySetupState?>` to `UseState<RenderSnapshot?>` — record value equality means each engine event yields a snapshot that compares non-equal to the previous one, reliably triggering `_requestRender`.
   - Added `Capture(LocalGatewaySetupState)` static helper that derives `LastRunningPhase` by walking `state.History` backwards for the last non-terminal phase (or `state.Phase` if currently running).
   - **Capture runs OFF the dispatcher** (immediately in the engine's StateChanged callback, before `dispatcher.TryEnqueue(...)`) so the snapshot freezes the engine's state at event-fire time, not whatever it has mutated to by the time the dispatcher dequeues.
   - Visual-test `retryable`/`terminal` paths in `TryReadVisualTestState` now do `state.StartPhase(MintBootstrapToken)` before `Block(...)` so synthetic test states surface a non-trivial History (and thus a usable `LastRunningPhase`) for the harness.

2. **Helper (`LocalSetupProgressStageMap.cs`, new):**
   - Extracted `VisibleStages`, `StageState`, `ComputeStageState`, `IndexOfStageForPhase`, `ShouldShowErrorRow`, `ShouldShowRetryButton` out of the page into a pure helper (no WinUI deps).
   - `VisibleStages` now also folds `PairOperator` / `CheckWindowsNodeReadiness` / `PairWindowsTrayNode` / `VerifyEndToEnd` into the MintToken stage so the actual e2e-drive failure phase pins on a visible stage instead of being unrepresentable.

3. **Policy (`LocalSetupProgressPolicy.cs`):**
   - Added `MapStatusToNextButtonState(bool hasSnapshot, LocalGatewaySetupStatus status)` overload (called by the page); existing `(LocalGatewaySetupState?, status)` overload preserved for back-compat.

## Tests

`tests/OpenClaw.Tray.Tests/LocalSetupProgressStageMapTests.cs` (+36 net new):

- 15 `[Theory]` rows covering every running engine phase → expected stage index.
- `NotStarted` → all stages Pending.
- `Complete` → all stages Complete.
- **Coverage guard** (`EveryDeclaredEnginePhase_IsCoveredBySomeVisibleStageOrIsTerminal`) — locks down future enum drift.
- **The Aaron-14 scenario** (`FailedRetryable_AtPairOperator_PinsFailureOnLastVisibleStage`) — stages 0–5 ✅, stage 6 ❌.
- `FailedRetryable_AtCreateWslInstance_PinsFailureOnSecondStage`.
- `FailedTerminal_AtPreflight_PinsFailureOnFirstStage`.
- `ShouldShowErrorRow` (9 InlineData) + `ShouldShowRetryButton` (5 InlineData).
- `IndexOfStageForPhase_ReturnsMinusOne_ForUncoveredPhases`.

## Validation

| Suite | Baseline (`73767c5`) | Now (`4af2581`) |
|---|---|---|
| `tests/OpenClaw.Shared.Tests` | 1180 / 1180 | **1180 / 1180** |
| `tests/OpenClaw.Tray.Tests` | 447 / 447 | **493 / 493** (+46) |

Env: `OPENCLAW_REPO_ROOT=<worktree>`, `OPENCLAW_RUN_INTEGRATION=1`.

## ⚠️ Deferred verification

- **Full `./build.ps1` (WinUI side):** BLOCKED. Tray app **PID 8240** is running and holds write-locks on `src\OpenClaw.Tray.WinUI\bin\x64\Debug\...\OpenClaw.Shared.dll`, causing `MSB3026 Could not copy ...` during the WinUI assembly step. Per the e2e-drive guardrail (*"DO NOT touch the running tray app at PID 8240 — Mike is looking at the broken state"*), PID 8240 was **not** terminated. Side-output build attempts (`-p:BaseOutputPath=bin-verify\`) failed with duplicate-AssemblyInfo errors because both `obj\` and `obj-verify\` fed into compilation; abandoned and cleaned up.
- **Screenshot verification:** BLOCKED for the same reason. The mandatory screenshot-verification protocol (per project instructions) cannot run while PID 8240 holds the lock.

The page itself is now a thin declarative wrapper over the pure helper; the
unit tests carry strong coverage of the behavioral fix. Verifying the
remaining glue (UseState change-detection, dispatcher marshalling, Lottie /
text bindings) requires a fresh launch.

**Follow-up for Mike (or next mattingly round):**
1. `Stop-Process -Id 8240`
2. `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore`
3. Launch with `OPENCLAW_VISUAL_TEST=1` + `OPENCLAW_FORCE_ONBOARDING=1` + `OPENCLAW_ONBOARDING_START_ROUTE=LocalSetupProgress` and these scenarios:
   - `OPENCLAW_VISUAL_TEST_LOCAL_SETUP=active:CreateWslInstance` — stage 1 spinner, stage 0 ✅.
   - `OPENCLAW_VISUAL_TEST_LOCAL_SETUP=active:MintBootstrapToken` — stages 0–5 ✅, stage 6 spinner.
   - `OPENCLAW_VISUAL_TEST_LOCAL_SETUP=retryable:device-auth-invalid` — stage 6 ❌, error row + Try Again button.
   - `OPENCLAW_VISUAL_TEST_LOCAL_SETUP=terminal:Setup cannot continue` — error row + diagnostics hint, no retry button.
4. Live e2e drive (no visual-test override) to confirm stages advance as the
   real engine progresses through Preflight → … → MintBootstrapToken.

## Files

**New:**
- `src/OpenClaw.Tray.WinUI/Onboarding/Services/LocalSetupProgressStageMap.cs`
- `tests/OpenClaw.Tray.Tests/LocalSetupProgressStageMapTests.cs`

**Modified:**
- `src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs`
- `src/OpenClaw.Tray.WinUI/Onboarding/Services/LocalSetupProgressPolicy.cs`
- `tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj`

## Lessons / generalizable signals

1. **`UseState<TClass>` with reference-equality is a footgun anywhere in this codebase.** Any other page binding directly to a mutating engine instance has the same bug latent. Worth a sweep for `UseState<...>` calls whose type argument is a non-record class without an `Equals` override. Candidates: any onboarding page that subscribes to a stateful service.
2. **Block() losing phase information** is a smell on the engine side too. Consider preserving `LastRunningPhase` on `LocalGatewaySetupState` directly (instead of inferring from History). Out of scope for this fix; flagging.
3. **Pure-helper extraction pattern.** Moving the stage-list math out of the page made it unit-testable without WinUI. Recommend the same shape for any other onboarding page that has non-trivial display-state derivation.

## Addendum — prototype cross-check (per Mike's mid-flight reminder)

Verified against the prototype worktree (`openclaw-windows-node` branch
`pr-241-feedback-fixes`), `src/OpenClaw.Tray.WinUI/Onboarding/Pages/ConnectionPage.cs:415-432`:

```csharp
engine.StateChanged += state =>
{
    var setupMessage = BuildWslSetupStatusMessage(state);
    dispatcher.TryEnqueue(() =>
    {
        setWslSetupState(state);          // <-- same ref-equality bug latent here
        setWslSetupStatus(setupMessage);  // <-- string varies per phase, forces re-render
        setStatusMsg(setupMessage);       // <-- same
    });
};
```

The prototype "worked" not because it had a correct binding pattern, but
because it updated three sibling `UseState`s per event. The
`LocalGatewaySetupState`-typed one had the **same reference-equality bug**
my fix targets — but the companion `string` states varied per phase
(`BuildWslSetupStatusMessage` produces unique text per phase), so *those*
`UseState<string>.Set` calls triggered the re-render the page-state setter
silently swallowed.

When mattingly-1 forked into `LocalSetupProgressPage` and dropped the
free-form status-text companion (since the new design uses a stage list
rather than running text), the accidental masking went away and the bug
became visible. The prototype is **not** a pattern worth porting forward —
it's a bug behind a load-bearing accident.

`RenderSnapshot` (record value-equality) is the correct durable fix and
does not regress if a future page chooses to drop companion text-state.
This addendum strengthens — does not change — the fix shipped at `4af2581`.
