# Mattingly — Bugs #1 + #2 Implementation Report

**Author:** Mattingly (Frontend / Onboarding UX)
**Date:** 2026-05-05T07:35 PT
**Branch:** `feat/wsl-gateway-clean` (worktree: `openclaw-wsl-gateway-clean`)
**Status:** Implementation complete. Awaiting RubberDucky CONDITIONAL → AGREE re-review.
**Commits (chronological):**

| Bug | SHA | Subject |
|---|---|---|
| #1 | `545d95e9d8f60f694b6704032d040d6ed04f26e6` | `fix(onboarding): keep Wizard in route for Local autopair + seed GatewayClient (Bug #1 from manual test)` |
| #2 | `d4e6f32bcd6761e973c9f66a7ff1c3d6810ef8e7` | `fix(onboarding): suppress Pending toast during Phase 14 auto-approve (Bug #2 from manual test)` |

Both commits include the `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` trailer.
**No push performed** (per Mike's directive).

---

## Bug #1 — Post-pair nav lands on Permissions instead of Wizard

### Production diff

| File | LOC | Purpose |
|---|---|---|
| `src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs` | +9 / −5 | Carve out `SetupPath.Local` exception to the `EnableNodeMode` Wizard-skip guard at L126. |
| `src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs` | +21 / −0 (1 using added) | Sister fix: at LocalSetupProgress completion (just before `RequestAdvance`), eagerly (re)initialize `App.GatewayClient` and copy the reference into `Props.GatewayClient` so WizardPage's `App.GatewayClient ?? Props.GatewayClient` poll finds a connected client instead of timing out into "offline". |

### Tests added (`tests/OpenClaw.Tray.Tests/OnboardingStateTests.cs`)

| Test | Purpose |
|---|---|
| `GetPageOrder_LocalPath_NodeMode_KeepsWizardAndChat` | **Replaces** old `..._SkipsWizardAndChat` — asserts the post-Bug-#1 expected route shape. |
| `GetPageOrder_LocalPath_NodeMode_NoChat_KeepsWizard` | New — `ShowChat=false` variant must still keep Wizard between LocalSetupProgress and Permissions. |
| `NextRouteAfterLocalSetupProgress_LocalNodeMode_IsWizard` | New **integration assertion** that RubberDucky specifically called out — uses the same `Array.IndexOf(pages, current)+1` lookup that `OnboardingApp.GoNext()` performs (OnboardingApp.cs:36-46), so it would fail if the auto-advance landed on Permissions. |
| `GetPageOrder_AdvancedPath_NodeMode_UsesConnectionPage` | Pre-existing — explicitly verified unchanged. |

### RubberDucky closure-condition verification (Bug #1)

> **"Production diff MUST include BOTH the route fix AND gateway-client seeding for the Wizard hop."**
>
> ✅ Route fix at `OnboardingState.cs:127` (the new `&& path != Onboarding.Services.SetupPath.Local` guard).
> ✅ Gateway-client seeding at `LocalSetupProgressPage.cs:142-152` — calls `appForSeed.ReinitializeGatewayClient()` if not already connected and writes `advanceRef.GatewayClient = appForSeed.GatewayClient` so both `App.GatewayClient` and `Props.GatewayClient` are populated before `RequestAdvance()` fires.

> **"The fix to `OnboardingState.GetPageOrder()` must keep the `Local` setup path including the Wizard hop … `path == SetupPath.Local` exception to the node-mode skip OR reorder so explicit Local is handled before node mode."**
>
> ✅ Took RubberDucky's preferred shape #1 (single-line `&&` exception). See `OnboardingState.cs:127`:
> ```csharp
> if (Settings.EnableNodeMode && path != Onboarding.Services.SetupPath.Local)
> {
>     return [...Permissions-only...];
> }
> ```

> **Integration assertion was missing from the original test plan.**
>
> ✅ Added `NextRouteAfterLocalSetupProgress_LocalNodeMode_IsWizard` (OnboardingStateTests.cs ~L116). The test mirrors `OnboardingApp.GoNext()`'s `pages[pageIndex + 1]` lookup, so a regression to "lands on Permissions" would fail this test.

---

## Bug #2 — "Copy pairing command" toast fires during Phase 14 auto-approve

### Production diff

| File | LOC | Purpose |
|---|---|---|
| `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs` | +43 / −1 | Added `LocalGatewaySetupEngine.IsAutoPairingWindowsNode` property + `ShouldSuppressPairingPendingNotification(engine, status)` static decision helper. Wrapped the Phase 14 `_windowsTrayNode.PairAsync` action delegate (L2401) with `Interlocked.Exchange`-based try/finally bracket. |
| `src/OpenClaw.Tray.WinUI/App.xaml.cs` | +27 / −1 | Added `_localSetupEngine` field; cached the engine in `CreateLocalGatewaySetupEngine()`; `OnPairingStatusChanged` Pending branch now consults the static decision helper and early-returns with an Info log when suppression is active. Paired/Rejected branches untouched. |

### Tests added

| File | Test | Purpose |
|---|---|---|
| New: `tests/OpenClaw.Tray.Tests/LocalGatewaySetupAutoPairFlagTests.cs` | `IsAutoPairingWindowsNode_DefaultsToFalse` | Pre-run baseline. |
| | `IsAutoPairingWindowsNode_TrueOnlyDuringPhase14PairAsync` | Captures the engine flag inside both the Phase 12 and Phase 14 provisioner callbacks; asserts false during Phase 12, true during Phase 14, false after run. **Proves the bracket is exactly Phase 14, not the whole engine run** (RubberDucky condition). |
| | `IsAutoPairingWindowsNode_ResetEvenIfPhase14Throws` | Race-safety: finally must reset the flag so a later unrelated Pending event isn't silently swallowed. |
| | `ShouldSuppressPairingPendingNotification_OnlyForPendingDuringAutoPair` (Theory, 6 cases) | All combinations of `(PairingStatus, autopair-flag)` — only `(Pending, true)` suppresses. Captures the decision inside the Phase 14 callback so the engine flag is observably true. |
| | `ShouldSuppressPairingPendingNotification_NullEngine_NeverSuppresses` | Manual ConnectionPage path: App may have no cached engine — helper must tolerate null and never suppress. |
| Modified: `tests/OpenClaw.Tray.Tests/LocalGatewaySetupTests.cs` | (no test changes) | Promoted 8 helper classes from `private sealed` → `internal sealed` so the new test file can construct an engine. No assertion changes. |

### RubberDucky closure-condition verification (Bug #2)

> **"Suppression bracket MUST wrap the actual Phase 14 node-role `PairAsync` call (the one that triggers the role-upgrade Pending blip), NOT the whole local setup run, and NOT a nonexistent engine-level `PairAsync`."**
>
> ✅ Bracket lives at `LocalGatewaySetup.cs:2403-2415` — wraps **only** the `_windowsTrayNode.PairAsync(...)` await inside the Phase 14 `RunProvisioningPhaseAsync` action delegate. The Phase 12 `_operatorPairing.PairAsync` (L2396) is **not** bracketed; the rest of `RunLocalOnlyAsync` is **not** bracketed. The flag is on the engine (a real type that exists), not a fictional `engine.PairAsync`.
>
> ✅ Empirical proof: `IsAutoPairingWindowsNode_TrueOnlyDuringPhase14PairAsync` asserts `FlagDuringOperatorPair == false` AND `FlagDuringWindowsNodePair == true` AND `engine.IsAutoPairingWindowsNode == false` after run. If the bracket leaked outside Phase 14, this test would fail.

> **"The manual ConnectionPage caller path (App.xaml.cs ~line 366 per your earlier diagnosis) must remain unaffected — verify with a test or explicit code path trace."**
>
> ✅ Code-path trace: `ConnectionPage.cs:366` calls `app.ShowPairingPendingNotification(deviceId, cmd)` **directly**, bypassing `OnPairingStatusChanged` entirely. The suppression gate I added lives only inside `OnPairingStatusChanged` (App.xaml.cs:1206-1217), so the manual path cannot be suppressed by it.
>
> ✅ Test cover: `ShouldSuppressPairingPendingNotification_NullEngine_NeverSuppresses` confirms the helper returns `false` when no engine is cached, which is the expected App state for any non-Local-easy-setup flow (including manual Advanced ConnectionPage). The Theory case `(Pending, autopair=false)` further confirms that even with a cached engine, suppression only applies during the Phase 14 window.
>
> ✅ Race-safety: `WindowsNodeClient.PairingStatusChanged` is invoked synchronously from `HandleRequestError` (RubberDucky's note 3 in the review). The bracket is `try { await _windowsTrayNode.PairAsync(...) } finally { reset }`, so the flag is guaranteed live for the duration of the await — including the synchronous event raise. `Interlocked.Exchange` provides memory barrier semantics for the App-thread reader.

---

## Validation results (all green)

| Step | Result |
|---|---|
| `./build.ps1` | ✅ Shared / Cli / WinNodeCli / WinUI all built successfully. |
| `dotnet test tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore` | ✅ **Passed: 1158, Skipped: 22, Total: 1180** (matches baseline; one infra test requires `OPENCLAW_REPO_ROOT` env var — same pre-existing condition as before my changes). |
| `dotnet test tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore` | ✅ **Passed: 536, Failed: 0, Total: 536** (524 baseline + 12 new tests = 536; Localization tests likewise need `OPENCLAW_REPO_ROOT`). |
| `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore -v q` | ✅ 0 Errors, 20 Warnings (pre-existing XAML warnings, unchanged). |
| **DLL freshness** | `src\OpenClaw.Tray.WinUI\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\OpenClaw.Tray.WinUI.dll` → `5/5/2026 7:33:27 AM` (post-edit). |

(Both Shared and Tray test failures observed without `OPENCLAW_REPO_ROOT` are unrelated localization/readme infra tests; with the env var set both suites are 100% green. None of those tests are touched by my changes.)

---

## Notes for RubberDucky's re-review

**Where to look first:**

1. `src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs:120-138` — the rewritten guard. I went with the smaller `&& path != SetupPath.Local` shape (your option A) plus a comment block citing the `LocalGatewaySetup.cs:2147` mutation as root cause.
2. `src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs:140-156` — the GatewayClient seeding block. Note: I read App.GatewayClient via `(App)Application.Current` and call `ReinitializeGatewayClient()` only if not already connected; idempotent across back/forward navigation that re-fires `Status==Complete`.
3. `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:2235-2270` — the new `IsAutoPairingWindowsNode` property and `ShouldSuppressPairingPendingNotification` static helper. The static helper is the testability seam you asked for.
4. `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:2403-2415` — the Phase 14 bracket. Just `_windowsTrayNode.PairAsync` is wrapped; the surrounding `RunProvisioningPhaseAsync` and the Phase 12 `_operatorPairing.PairAsync` (L2396) are not.
5. `src/OpenClaw.Tray.WinUI/App.xaml.cs:1207-1219` — the new suppression branch in `OnPairingStatusChanged`. Logs the suppression for traceability.

**Things I wasn't 100% sure about (please weigh in):**

- The seeding call uses `app.ReinitializeGatewayClient()` (no bootstrap-handoff arg). The operator credentials in `_settings.Token` were saved by the Phase 12 `_operatorPairing.PairAsync` — same path the manual ConnectionPage relies on. I deliberately did **not** pass `useBootstrapHandoffAuth: true` because by Phase 16 the bootstrap token has already been consumed; the persistent operator token is what we want. If you prefer a more defensive shape (e.g., gate seeding on `_settings.EnableNodeMode==true` before re-init to avoid double-init in the `EnableNodeMode==false` case), let me know.
- I left the existing `App.xaml.cs:385` startup branch alone — it still picks `InitializeNodeService` when `EnableNodeMode==true`. The new seeding block in `LocalSetupProgressPage` runs **only** during the post-onboarding LocalSetupProgress completion path, so it doesn't conflict with normal startup. After onboarding finishes and the tray reboots, the regular startup logic will run and the dual operator+node connections will be re-established as designed.
- I exposed `LocalGatewaySetupEngine.ShouldSuppressPairingPendingNotification` as `public static`. If you'd prefer `internal static` plus an `InternalsVisibleTo` for the test project, happy to swap — I went with `public` because there's no existing `InternalsVisibleTo` in the WinUI project and adding one felt like more surface change than warranted.
- I promoted 8 helpers in `LocalGatewaySetupTests.cs` from `private sealed` → `internal sealed` so the new test file could reuse them. No production-code change; no behavioral change to existing tests.

**Hard guardrails honored:**

- ✅ Did not push to remote.
- ✅ Did not modify code outside the scope of these two bugs.
- ✅ Did not regress any existing tests (524 → 536 Tray, 1180 → 1180 Shared with same skip count).
- ✅ Did not touch the `OpenClawGateway` distro or any of the 17 prototype distros.
- ✅ All test artifacts created in repo paths (no `/tmp` writes).

**Live-tray note:** PID 53736 was no longer running when I started (`Get-Process -Id 53736` returned nothing) — Mike must have stopped it between Mattingly-25's diagnosis and this implementation pass. So the build proceeded without any lock contention. If Mike wants to re-launch and verify end-to-end, the freshly-built x64 DLL is at the path above (timestamp `5/5/2026 7:33:27 AM`).

— Mattingly
