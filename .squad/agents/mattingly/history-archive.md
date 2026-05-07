# mattingly History Archive - 2026-05-06
Entries consolidated from full history.

## Summary
- Long-running project
- Multiple work streams
- See current history.md for recent activity

# Project Context

- **Project:** openclaw-windows-node (Windows tray app + WSL gateway)
- **Created:** 2026-05-04
- **User:** Mike Harsh

## Core Context

Clean WSL gateway rebuild on sibling worktree `..\openclaw-wsl-gateway-clean` (branch `feat/wsl-gateway-clean` from upstream/master `871b959`). Onboarding UX is Mattingly's primary scope. Read `.squad/identity/now.md` and `.squad/prototype-reference.md` on first spawn.

## Recent Updates

📌 Team hired 2026-05-04. Universe: Apollo 13.

## Learnings

### UX constraints (locked)

- **Grid for left+right rows** — never HStack for alignment.
- **TextBlock for read-only display** — never editable TextBox (clear-button leak).
- **Screenshot verification mandatory** before declaring any UI page done. Use `OPENCLAW_VISUAL_TEST=1` harness; `windows-computer-use` MCP currently fails with `Bun is not defined` on this machine.

### Summary — Phase 5 onboarding UX [Scribe-compacted 2026-05-04T19:35-07:00 / round 13]

**Phase 5 landed and APPROVED (commits `43035ca` → `99f5107` → `32cbeae` → `ce89251` → `73767c5`):**

- Added `SetupWarning` + `LocalSetupProgress` routes + `SetupPath` enum + `AdvanceRequested` event on `OnboardingState`.
- `SetupWarningPage.cs`: Grid Auto/1*/Auto/Auto, MaxWidth 460, accent "Set up locally" + hyperlink "Advanced setup", folded ⚠️ security notice. AutomationIds: `OnboardingSetupLocal` / `OnboardingSetupAdvanced`.
- `LocalSetupProgressPage.cs`: drives engine via `App.CreateLocalGatewaySetupEngine()`. **7 visible stages:** Checking system / Installing Ubuntu / Configuring instance / Installing OpenClaw / Preparing gateway / Starting gateway / Generating setup code. (Skipped phases not in `s_visibleStages[]`: `EnsureWsl`, `InstallService`, `Complete`.) Error/retry row; 1s auto-advance on Complete; static engine fields survive page nav.
- `WelcomePage.cs` deleted; `GetPageOrder()` branches on `SetupPath` (null defaults to Local for indicator stability).
- **Fast-follow `32cbeae`:** subtitle time-estimate dropped; 45 orphan `Onboarding_Welcome_*` resw entries removed (9 keys × 5 locales) via `XmlDocument` (`PreserveWhitespace=true`) + XPath.
- **i18n `ce89251` (mattingly-3, round 11):** 17 new keys × 5 locales = 85 entries; `OPENCLAW_TEST_LOCALE` env hook in `OnboardingWindow`. fr-fr screenshot verified. 5 low-confidence translations flagged `?` for Mike (nl-nl Title; zh `正在` prefix; fr-fr nbsp-before-colon; zh quote style; nl-nl Advanced).
- **Visual pass `ce89251` (mattingly-4, round 12):** all 5 required states + WelcomePage removal verified shippable. Captures: `visual-test-output/full-pass-2026-05-04/`.
- **Next-button policy `73767c5` (mattingly-5, round 12):** new `OnboardingNextButtonState` enum + `SetNextButtonState()` + `NavBarStateChanged` event on `OnboardingState`. New pure helper `LocalSetupProgressPolicy.MapStatusToNextButtonState()` (no WinUI deps). `OnboardingApp` consults state **only** when `currentRoute == LocalSetupProgress`. Bonus fix: 1s auto-advance on Complete now checks `CurrentRoute == LocalSetupProgress` to prevent over-advance. **Tests +13 → Tray 447/447**, Shared 1180/1180. All 4 active states screenshot-verified at `visual-test-output/next-button-impl-2026-05-04/`.

**Net delta from baseline `871b959` across 17 commits:** Tray 407 → 447 (+40), Shared 1172 → 1180 (+8), zero regressions.
## 2026-05-04T18:35-07:00 — Mattingly-4: Full visual pass (round 12)

Visually verified 5 onboarding states + WelcomePage removal on eat/wsl-gateway-clean@ce89251: SetupWarning en-us, LocalSetupProgress idle (Preflight), LocalSetupProgress active (InstallOpenClawCli), ConnectionPage Advanced, SetupWarning fr-fr. All layout contracts hold (MaxWidth 460/520, NavigationHost 680). No truncation, no English fallback in fr-fr, no prototype residue. Verdict: **visually ship-ready**. windows-computer-use MCP returned Bun is not defined — fell back to OPENCLAW_VISUAL_TEST=1 harness. Captures under isual-test-output/full-pass-2026-05-04/. Tray 434/434, Shared 1180/1180 (no code change). Decision: mattingly-full-visual-pass.md.

## 2026-05-04T18:55-07:00 — Mattingly-5: Phase 5 final — Next/Back-button policy (commit 73767c5)

Implemented coordinator's autopilot Next-button defaults. New OnboardingNextButtonState enum + SetNextButtonState() + NavBarStateChanged event on OnboardingState; new pure helper LocalSetupProgressPolicy.MapStatusToNextButtonState(); OnboardingApp consults state **only** when currentRoute == LocalSetupProgress. Bonus fix: 1s auto-advance on Complete now checks current route to prevent over-advance. Tests +13 → **Tray 447/447**; Shared 1180/1180. All 4 active states screenshot-verified at isual-test-output/next-button-impl-2026-05-04/. Net delta from baseline 871b959: Tray +40, Shared +8 across 17 commits. Decision: mattingly-next-button-policy.md.

## 2026-05-04T19:35-07:00 — Mattingly-6 (in flight)

Investigating + fixing **Bug 2** from aaron-14 E2E drive: LocalSetupProgressPage never propagates engine phase updates past stage 0 (• Checking system stayed spinning while engine progressed through all phases to PairOperator), and never transitions to FailedRetryable/FailedTerminal on engine failure. Aaron-16 in parallel on Bug 1 (bootstrap-token handshake).
## Round 13 — 2026-05-04T19:35:00-07:00 — Bug 2 fix: LocalSetupProgressPage stage propagation + FailedRetryable rendering (mattingly-6)

Fixes the page-binding bug Aaron's e2e drive surfaced (.squad/decisions/inbox/aaron-e2e-drive.md § 4): UI stayed on stage 1 spinner the entire 12-minute run even though the engine advanced through 9+ phases and ultimately failed at PairOperator. Commit 4af2581 on eat/wsl-gateway-clean (parent 73767c5).

**Root cause (concrete):** Reference-equality in Component.UseState. EqualityComparer<T>.Default.Equals for a class without an Equals override falls through to ReferenceEquals. The page held `UseState<LocalGatewaySetupState?>` and the engine raises StateChanged?.Invoke(state) with the same mutating instance every call (LocalGatewaySetup.cs:1964). First null→state transition rendered once; every subsequent state→state event was deemed "no change" and the framework swallowed the re-render request. Stage list never advanced. FailedRetryable never rendered.

**Files modified:**
- `src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs` — introduced `private sealed record RenderSnapshot(Phase, Status, LastRunningPhase, UserMessage, FailureCode)` + `Capture(LocalGatewaySetupState)` static helper; switched to `UseState<RenderSnapshot?>`; `Capture()` runs OFF the dispatcher (before `TryEnqueue`) so the snapshot reflects the state at event-fire time, not whatever the engine has mutated to by the time the dispatcher dequeues; deleted inline `s_visibleStages` / `ComputeStageState` / `StageState` (moved to helper); `TryReadVisualTestState` now does `StartPhase(MintBootstrapToken)` before `Block(...)` so `LastRunningPhase` pins the failure marker on the correct stage in retryable/terminal visual-test scenarios.
- `src/OpenClaw.Tray.WinUI/Onboarding/Services/LocalSetupProgressStageMap.cs` *(new, +119 lines)* — pure helper hosting `StageState` enum, `VisibleStages` array, `ComputeStageState`, `IndexOfStageForPhase`, `ShouldShowErrorRow`, `ShouldShowRetryButton`. `VisibleStages` now folds `PairOperator`/`CheckWindowsNodeReadiness`/`PairWindowsTrayNode`/`VerifyEndToEnd` (previously hidden) into the MintToken stage so a PairOperator failure (the actual e2e-drive bug) pins on a visible stage instead of being unrepresentable.
- `src/OpenClaw.Tray.WinUI/Onboarding/Services/LocalSetupProgressPolicy.cs` — added `MapStatusToNextButtonState(bool hasSnapshot, status)` overload used by the page; existing `(LocalGatewaySetupState?, status)` overload preserved for back-compat with `LocalSetupProgressPageNextButtonTests`.
- `tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj` — `<Compile Include>` for the new helper.

**Tests added** (`LocalSetupProgressStageMapTests`, +36 net new; some are Theory rows expanding from 15 InlineData):
- Stage advancement: every running engine phase resolves to the expected visible-stage index — 15 InlineData covering `Preflight`/`EnsureWslEnabled`/`ElevationCheck` → 0; `CreateWslInstance` → 1; `ConfigureWslInstance` → 2; `InstallOpenClawCli` → 3; `PrepareGatewayConfig`/`InstallGatewayService` → 4; `StartGateway`/`WaitForGateway` → 5; `MintBootstrapToken`/`PairOperator`/`CheckWindowsNodeReadiness`/`PairWindowsTrayNode`/`VerifyEndToEnd` → 6.
- `NotStarted_RendersAllStagesPending`, `Complete_RendersAllStagesComplete`.
- `EveryDeclaredEnginePhase_IsCoveredBySomeVisibleStageOrIsTerminal` — coverage guard against future enum additions silently dropping off the page.
- `FailedRetryable_AtPairOperator_PinsFailureOnLastVisibleStage` — concretely the Aaron-14 scenario; stages 0–5 Complete, stage 6 Failed.
- `FailedRetryable_AtCreateWslInstance_PinsFailureOnSecondStage`.
- `FailedTerminal_AtPreflight_PinsFailureOnFirstStage`.
- `ShouldShowErrorRow`/`ShouldShowRetryButton` truth tables (9 + 5 InlineData).
- `IndexOfStageForPhase_ReturnsMinusOne_ForUncoveredPhases` (NotStarted/Complete/Failed/Cancelled).

**Validation (per AGENTS.md reporting standard):**
- Env: `OPENCLAW_REPO_ROOT=<worktree>`, `OPENCLAW_RUN_INTEGRATION=1`.
- `dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj` — **Passed: 1180, Failed: 0, Skipped: 0** (anchor 1180/1180; no change).
- `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj` — **Passed: 493, Failed: 0, Skipped: 0** (was 447/447 at `73767c5`; **+46 net new tests**).
- `./build.ps1` — Shared/Cli/WinNodeCli ✅; **WinUI build BLOCKED** by running tray app PID 8240 holding write-locks on `src\OpenClaw.Tray.WinUI\bin\x64\Debug\...` (lock contention error: `MSB3026 Could not copy ... OpenClaw.Shared.dll`). Per the e2e-drive guardrail (`DO NOT touch the running tray app at PID 8240 — Mike is looking at the broken state`) PID 8240 was NOT terminated. Side-output build (`-p:BaseOutputPath=bin-verify\`) failed with duplicate-AssemblyInfo errors because the obj/ + obj-verify/ dirs both fed into compilation.
- **Screenshot verification BLOCKED** for the same reason (cannot launch a fresh WinUI build to drive the visual harness while PID 8240 holds the lock). Mike (or a follow-up agent after Mike releases PID 8240) should:
  1. `Stop-Process -Id 8240`
  2. `dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -p:Platform=x64 --no-restore`
  3. Launch with `OPENCLAW_VISUAL_TEST=1` + `OPENCLAW_FORCE_ONBOARDING=1` + `OPENCLAW_ONBOARDING_START_ROUTE=LocalSetupProgress` and these scenarios:
     - `OPENCLAW_VISUAL_TEST_LOCAL_SETUP=active:CreateWslInstance` — confirm stage 1 shows spinner, stage 0 shows ✅.
     - `OPENCLAW_VISUAL_TEST_LOCAL_SETUP=active:MintBootstrapToken` — confirm stages 0–5 ✅, stage 6 spinner.
     - `OPENCLAW_VISUAL_TEST_LOCAL_SETUP=retryable:device-auth-invalid` — confirm stage 6 ❌ red, error row + Try Again button.
     - `OPENCLAW_VISUAL_TEST_LOCAL_SETUP=terminal:Setup cannot continue` — confirm error row + diagnostics hint, no retry button.

**Confidence:** High that the unit-tested mapping is correct (every phase covered + theory-driven). High that the reference-equality fix is correct (record value-equality is well-defined; first-render still works because `null → RenderSnapshot` is a value-difference; subsequent transitions differ in at least `Phase` or `Status`). Visual verification deferred — surfaced explicitly above.

**Commit:** `4af2581239f7544df3fc5da92788b3a458ca9042` on `feat/wsl-gateway-clean` (parent `73767c5`). Branch is now 17 commits since baseline `871b959`.

## 2026-05-06T09:28:55-07:00 — Mattingly-7: PR #274 existing-config gate — plan (plan-only turn)

Produced `mattingly-pr274-existing-config-gate-plan.md` for RubberDucky review. No code edited.

### Learnings

- **`IdentityDataPath`** lives at `%APPDATA%\OpenClawTray` (env override `OPENCLAW_TRAY_APPDATA_DIR`), distinct from **`DataPath`** (`%LOCALAPPDATA%\OpenClawTray\...`). The split matters: `DeviceIdentity` uses `IdentityDataPath`; `LocalGatewaySetupStateStore` and `SettingsManager` use `DataPath`. When constructing `OnboardingExistingConfigGuard`, both paths are needed — get `IdentityDataPath` from `App.IdentityDataPath` (static field, already used by `StartupSetupState`).

- **FunctionalUI pages cannot use `async void` click handlers safely.** ContentDialog.ShowAsync() is the natural WinUI3 confirm pattern but requires awaiting from an async context. The FunctionalUI click callback is synchronous `void`. The correct pattern for modal confirmation in FunctionalUI is **inline `UseState<bool>` flag** — flip a boolean on click, re-render a warning section in-place. `SetupWarningPage` already follows this shape (single-frame render with no async plumbing).

- **`OnboardingWindow` currently takes only `SettingsManager`** — when wiring in the guard, the cleanest pass-through is `identityDataPath` as a second constructor param, supplied by `App.ShowOnboardingAsync()`. Do not add the guard to `App.xaml.cs` statics; keep guard construction co-located with `OnboardingState` construction in `OnboardingWindow`.

- **Engine fail-closed via `CreateLocalOnly`** is the right seam — not `RunLocalOnlyAsync`. The factory is the only external-API surface; `RunLocalOnlyAsync` is engine-internal. Throwing `InvalidOperationException` with a structured error-code prefix (`existing_config_replacement_not_confirmed: ...`) lets the `LocalSetupProgressPage` catch block surface the message as a terminal failure.

- **`settings.Token`** is the sufficient proxy for "has existing config" at the engine level. It is non-empty iff a prior setup completed (operator pair sets it at `LocalGatewaySetup.cs:1562`) or the user manually configured a remote gateway — both data-loss scenarios. `BootstrapToken` and DeviceIdentity checks are enrichments for the summary display; not needed in the engine guard.

- **Mobile returning-user UX (iOS/Android):** both clients default returning users to reconnect/re-pair, never to reinstall. The Windows equivalent is defaulting `SetupPath=Advanced` so the nav-bar Next button lands on ConnectionPage. This aligns with platform precedent and uses existing plumbing with ~12 LOC.

### Addendum — prototype cross-check (mattingly-6)

Per Mike's mid-flight reminder, verified the fix against prototype `openclaw-windows-node` branch `pr-241-feedback-fixes` `ConnectionPage.cs:415-432`. The prototype's apparent "working" engine binding was **accidental masking**, not a robust pattern: it updated three sibling `UseState`s per event (`setWslSetupState(state)` + `setWslSetupStatus(setupMessage)` + `setStatusMsg(setupMessage)`). The `LocalGatewaySetupState`-typed setter had the same reference-equality bug latent — but `BuildWslSetupStatusMessage(state)` produced a per-phase unique string, so the companion `UseState<string>.Set` calls forced re-renders the state-typed setter silently swallowed. When mattingly-1 forked into `LocalSetupProgressPage` and dropped the free-form status-text companion (the forked design uses a stage list instead of running text), the masking went away and the bug surfaced. `RenderSnapshot` (record value-equality) is the correct durable fix; it doesn't regress if a future page drops companion text-state. Cross-check **strengthens** — does not change — the fix shipped at `4af2581`.

## 2026-05-06T15:31:47-07:00 — Mattingly-8: Wizard loopback Symptom 3 fix (commit b3275a8)

Owned the 4th-round recovery artifact for Symptom 3 (loopback to step 0 after channels page disconnect). Aaron's plan was rejected (wrong protocol: wizard.status). Hockney's plan shipped (commit 04c46df, wizard.next resume) but loopback still occurred.

**Root cause found from live log:**
`TryResumeWithSessionAsync` (WizardFlowController.cs:167) guards the wizard.next branch with `client?.IsConnectedToGateway == true`. Recovery fires at disconnect time — `connected=False` at that exact moment. The guard fails, control falls immediately to fallback `wizard.start`, which has its own 30-second reconnect polling loop. After reconnect (~27s in Mike's repro), wizard.start creates a NEW session at step 0. The gateway's live in-memory `WizardSession` was never queried.

**Fix:** Added `WaitForConnectionAsync(IWizardGateway?, int maxPollCount, Func<Task>? delayAsync)` to `WizardFlowController` — polls `IsConnectedToGateway` up to 30 times (injectable for test speed). Called from the recovery lambda in `WizardPage.cs` BEFORE `TryResumeWithSessionAsync`, so the IsConnectedToGateway guard is true when the resume path is evaluated.

**Key learnings:**
- `TryResumeWithSessionAsync` was correct in its design but the caller assumption (connected on entry) was violated — recovery fires synchronously at disconnect, not after reconnect.
- `StartWizardAsync` had its own 30s polling loop that silently "fixed" the delay for wizard.start — the asymmetry is what caused the bug (start waited, resume did not).
- `WizardSession.answerDeferred` survives client WebSocket disconnect as long as the Node.js process is alive (confirmed by RubberDucky / Hockney plan). wizard.next on the live session should return the channels step, not step 0.
- Delay must be injectable (`Func<Task>? delayAsync`) for unit tests to run instantly.

**Validation:** build=pass, shared-tests=1184/1206 skipped=22, tray-tests=611/611 (3 new WaitForConnectionAsync tests). Commit b3275a8 on feat/wsl-gateway-clean. Tray PID 48836.

## 2026-05-06
- Wizard plans + impls


