# CI failure backlog

Open `Build and Test` failures seen on `main` push runs that no change has fixed yet. Check an item off, and link the fixing PR, once its fix lands and CI passes on a rerun. Delete an item if a later investigation shows it doesn't reproduce.

#1597 (fix(ci): Build and Test flakes when Workspace keyboard input or console tail polling runs late) fixes two other failures, so they are not listed here: the `WizardConsoleTailTests` cursor race and the `Missing native control 'WorkspaceNavHome'` mode of the Workspace sidebar test.

Run links point to `https://github.com/openclaw/openclaw-windows-node/actions/runs/<id>`.

## UI, functional, and accessibility tests

- [ ] **`WorkspaceWindowProofTests.SidebarSessions_SelectOriginalKeys_AndKeepCompanionDraft`: pressing Space on `WorkspaceTogglePane` does not hide the pane.**
  - Symptom: `Native navigation did not reach the expected state.` from the `WorkspaceReopenPane` wait right after `SendKeys.SendWait(" ")`. Fails in either theme.
  - Runs: [36958321581](https://github.com/openclaw/openclaw-windows-node/actions/runs/36958321581), [36943555246](https://github.com/openclaw/openclaw-windows-node/actions/runs/36943555246), [36910516055](https://github.com/openclaw/openclaw-windows-node/actions/runs/36910516055), both themes on #1597 head `3ee08eee` ([36975087646](https://github.com/openclaw/openclaw-windows-node/actions/runs/36975087646)), and Light on head `142e7d42` ([36979192680](https://github.com/openclaw/openclaw-windows-node/actions/runs/36979192680)).
  - Known: on #1597 the test checks just before the key that the Hub window is in the foreground and that `AutomationElement.FocusedElement` is `WorkspaceTogglePane`. That check passes, but does not prove key delivery or rule out a subsequent focus change. In the same job, `NativePagesAndOwnerLinks_StayInOneWorkspaceWindow` presses Space on the same toggle successfully. Locally the test passes 20/20.
  - Suspect: an earlier step in the sidebar test, such as the Settings round-trip, `RefocusWorkspaceAsync`, or the `WorkspaceWindow.RefreshSidebar` item rebuild after a session is selected, leaves the toggle ignoring keyboard activation. Treat this as a possible product bug until proven otherwise.
  - Mitigation in #1597: the session/draft-preservation test now presses the toggle through UI Automation (`InvokePattern`). `NativePagesAndOwnerLinks_StayInOneWorkspaceWindow` retains mandatory Space activation and focus-transfer assertions after a Settings round-trip. No retries or fallback activation were added to the keyboard test.
  - Still unverified: why Space intermittently fails in the session-selected state. Separating the preservation test from synthetic keyboard delivery does not establish a product fix for that state.
  - Local follow-up: the dedicated keyboard test also timed out after Space on `WorkspaceReopenPane` in Light theme. Its reopen-button Space and Tab steps now use the same foreground-and-focus helper as the collapse-button step, rather than checking control focus without verifying the foreground window.
  - CI follow-up: [36986094081](https://github.com/openclaw/openclaw-windows-node/actions/runs/36986094081) passed the keyboard test but failed the Dark session-preservation test with a COM exception at the new toggle invocation. The foreground-and-focus barrier removed with `SendKeys` is restored before invoking. `RefocusWorkspaceAsync` alone only finds an already-existing marker, so it does not establish native activation readiness. This readiness correction needs a new CI run; no COM exceptions are suppressed or retried.
- [ ] **`ReactorChatLayoutProofTests` reasoning-picker body: silent testhost crash with the flyout open.**
  - Symptom: `Test host process crashed` while a `ReasoningPicker_*` test is mid-body with the reasoning flyout open; the last `chat-proof:` marker is `body start` with no `teardown begin`. Teardown hygiene is verified good: in the same run, 74 `body start` markers, 73 `window closed`, zero `popup close timed out`, and no teardown-phase crash. Two crashes on the same branch were both in ReasoningPicker bodies (`ReasoningPicker_PreservesCurrentValueWithoutInventingSupportedOptions`, `ReasoningPicker_UsesOnlyAdvertisedChoices`); a pristine-HEAD local run crashed in the same family (`ReasoningPicker_PreservesCurrentValueWithoutInventingSupportedOptions`).
  - Runs: [41536455039](https://github.com/larroy/openclaw-windows-node/actions/runs/41536455039) (fork `fix/ci-ui-flakes`), [41532119336](https://github.com/larroy/openclaw-windows-node/actions/runs/41532119336) (fork `fix/ci-ui-flakes`; hosted fixture startup, see the `UIThreadFixture` entry below) with local pristine run `TestResults/TrayUI-pristine1`. The pristine local reasoning-picker crash has no captured run link; its evidence is the local `TestResults/TrayUI-pristine1` artifacts.
  - Known: distinct from the teardown-window-closing crash (mode E) that the flyout/teardown hygiene fixed; the residual fault occurs while the flyout is open and Reactor re-renders the composer around a thinking-level change.
  - Next: capture the crash address on the next occurrence via the existing markers plus a Windows Error Reporting report from the runner, and consider closing the flyout before asserting re-render-sensitive state in a follow-up test-only change if the WER stack confirms the flyout child.
- [ ] **`UIThreadFixture` startup hangs on hosted runners: window creation never completes.**
  - Symptom: fixture markers show `Windows App SDK bootstrap initialized`, then `Application.Start: callback` and `entering window constructor` (after a ~37 s gap from bootstrap), and then nothing: the hidden test window constructor never completes, the fixture's own 90 s `_ready` wait fails with `UIThreadFixture failed to initialize within 90s; phase='creating hidden test window'`, and every remaining UI-collection test fails fast instead of the historical 5-minute inactivity blame abort (mode A's freeze inside `Bootstrap.Initialize`).
  - Runs: [41532119336](https://github.com/larroy/openclaw-windows-node/actions/runs/41532119336) (fork `fix/ci-ui-flakes`, 22:05:28 bootstrap → 22:06:10 window constructor → 22:06:58 timeout).
  - Known: same UI-thread-stall family as mode A, one phase later. The 90 s fail-fast wait, the phase-bearing initialization message, and the bootstrap/window-creation markers are existing fixture diagnostics (already on the merge base); the underlying window-creation stall is unresolved. New in this change: assembly-level test serialization and the early DPI lock that keep unrelated collections from starting while the fixture initializes.
  - Next: capture a dump of the stuck UI thread on the next occurrence (dumps stay disabled for secret safety, so first weigh the redaction plan), or test whether serializing the two launched-app collections away from fixture startup removes the stall.
- [ ] **`WorkspaceWindowProofTests` / `GatewayFixtureUiTests`: launched-app window management fails on this workstation.**
  - Symptom: `Win32Exception : The operation completed successfully.`, `Win32Exception : The handle is invalid.`, `Could not foreground the Hub window for keyboard input.`, and `NativeWindowHandle` asserts expecting a nonzero HWND but reading 0. The same profile reproduces on pristine `main` head (`TestResults/pristine-ws-1`: 10/16 failed), so it is not caused by test changes.
  - Runs: local only on the ARM64 workstation; CI does not show this mode.
  - Known: app launches and MCP connections work (fixture smoke passes), but foregrounding and HWND capture are unreliable in the current desktop session.
  - Next: retry in a fresh interactive session or rely on hosted CI for real-app proof.
- [ ] **`SharedRootDialog_DefersNavigationAndRejectsNativeBinding(destination: Chat, settingsOrigin: True)`: the retained composer draft is lost after the shared-root dialog round trip.**
  - Symptom: the composer is empty (`Value ""`) when the test returns to chat after navigating through Settings and the dialog; the read after the "retained chat" wait is immediate, so a transient re-render cannot be distinguished from a permanent loss without the new watch.
  - Runs: [40954902194](https://github.com/openclaw/openclaw-windows-node/actions/runs/40954902194) (`failure.png`: empty composer with the research session selected) and [41553144667](https://github.com/larroy/openclaw-windows-node/actions/runs/41553144667) (fork `fix/ci-ui-flakes`; the only UI-job failure in that run, `Timed out waiting for retained chat draft after the Settings round trip`). Reproduced locally with the draft watch: the wait timed out after 20 s (no transient recovery), and `failure.png` shows the chat page with an empty composer (`Message Assistant (Enter to send)` placeholder) while the research session stays selected. The composer runtime id is unchanged, so the element was never re-created; its value was cleared.
  - Known: this is a permanent draft loss in the same-window retention contract delivered by #1911, not a test-timing artifact. The test stays strict (polling wait, no masking); product code is unchanged here.
  - Next: fix the draft retention in the chat composer session binding (product), then close this entry with a green CI run.
- [ ] **`GatewayFixtureUiTests` chat-origin cases: `NullReferenceException` at `WorkspaceAssistantSelector` and fixture-tail timeouts.**
  - Symptom: `FindById(run, "WorkspaceAssistantSelector")!` returns null when the chat-origin dialog case starts (4/10 local fixture runs), and `Timed out waiting for natural tail at message 240` (3/10). One `IOException` in `GatewayFixtureRun.WaitForMcpAsync` on a fixture log file.
  - Runs: local only (`TestResults/loop-fixture-1..10`); not yet observed in CI with this attribution.
  - Known: the host's app-process window/session behavior is degraded for launched-app tests on this ARM64 workstation (see the WorkspaceWindowProofTests entry below), so these rates are not comparable to CI.
  - Next: retry on a healthy desktop session or on hosted CI before treating any of these as product defects.
- [ ] **`NativeSharedFlowTests.NativeFallback_GatewayMoreRejectsAnOverlappingContinue`: the wizard renders the gateway `__more` select step without the More expansion button.**
  - Symptom: `Timed out waiting for Gateway More button loaded. field=null, page.IsLoaded=True`. Reflection shows `_stepType=select`, `_errorState=False`, `_options=[skip|__more]`, `SelectOptions.Items.Count=1` ("Skip for now"), but the StepCard StackPanel has only its 12 base XAML children: no dynamically inserted `More ▾` button (`WizardPage.BuildOptions` should insert one whenever a gateway step carries a `__more` option). The request log shows exactly one `wizard.start` and no further wizard RPCs, so the state is stable, not transient.
  - Runs: no CI occurrence with this attribution yet (CI mode D was the 10 s `CompositionTarget.Rendering`-only wait, now widened by the poll timer). Reproduced locally on ARM64 `main` head `4cbbe51c`: 3/3 pristine-head full-class runs (`TestResults/pristine-class-1..3`), and with test-side diagnostics the state is stable from the first observation.
  - Known: single-test runs pass consistently (25/25 locally); the failure appears only in full-class runs on this host. The rendered select step is missing its required expansion control, which points at a product race in `BuildOptions` (options list rendered without the `__more` control), not at the test wait.
  - Next: capture the `_options` content at `BuildOptions` entry (test-side hook or temporary product trace) to determine whether the button insert was skipped because the parsed options lacked `__more` at render time, or the insert itself was skipped. Do not mask it by deriving the button from the visual tree; a missing More button is a user-visible wizard defect.
- [ ] **`A2UISvgTests.PathologicalGeometry_BoundedByRenderTimeout`: `SvgImageSource.SetSourceAsync` blocks the UI thread past the 8 s `SvgRenderTimeout`.**
  - Symptom: `pathological-geometry decode took 00:00:10.8466632 (>00:00:08)` against the `SvgRenderTimeout + 2 s` bound. `BillionLaughs_DoctypeStripped_ReturnsPromptly` passes in the same job (5 ms).
  - Runs: [41737007496](https://github.com/openclaw/openclaw-windows-node/actions/runs/41737007496) (10.85 s on a `main` control) and one other PR run (11.10 s, run link not captured). Reproduced locally on the x64 workstation in 3 of 5 runs of `A2UISvgTests` plus `NativeFallback_GatewayMore*` (13.9 s, 14.1 s, 15.1 s); the same test alone passes at 8.04 s.
  - Known: with a throwaway UI-dispatcher heartbeat, the failing runs logged `SVG decode timed out` at 8.07 s (the cap fires on time on the thread pool) while a dispatcher callback enqueued at 0.1 s was not serviced until 14 s, so the UI thread was blocked for the whole decode. The passing runs showed 50 ms dispatcher latency and return at 8.04 s. Queue wait is excluded: the single-load tests assert the decode slots are idle first. Why the decode runs synchronously on the UI thread only in some runs is unattributed.
  - Product impact: a pathological A2UI SVG can freeze the UI thread for longer than `SvgRenderTimeout`, so the cap does not bound user-visible wall time. The test stays at `SvgRenderTimeout + 2 s` (`OPENCLAW_UI_SVG_DECODE_SLACK_SECONDS`); widening it to the queue-plus-decode 18 s would hide this. Failure messages now carry the resolver warnings with offsets, and `Load_QueuedBehindBusyDecodeSlots_CompletesOnceASlotFrees` covers queue wait plus decode separately.
  - Next: find what makes `SetSourceAsync` run its parse on the UI thread in some runs (decode off the UI thread, or a size or complexity guard before decode), then close this entry with a green CI run.
- [ ] **`UIThreadFixture.Dispose` finalizer drain: native access violation (0xC0000005) in `ComWrappers+NativeObjectWrapper.Finalize`.**
  - Symptom: `Fatal error. 0xC0000005` inside `ComWrappers+NativeObjectWrapper.Finalize`, killing the Tray UI testhost about 0.5 s after the last UI-collection test. No managed stack.
  - Runs: [40233630640](https://github.com/openclaw/openclaw-windows-node/actions/runs/40233630640), [39998468597](https://github.com/openclaw/openclaw-windows-node/actions/runs/39998468597) (Oct 23 and 25; none since).
  - Known: it occurs during the fixture's deliberate `GC.Collect(); GC.WaitForPendingFinalizers();` drain, which releases projected XAML objects while their dispatcher and native reference trackers still exist. Mechanism unproven.
  - Next: the drain is now bracketed by `UIThreadFixture: dispose finalizer drain begin/end` console markers; use them to confirm attribution on the next occurrence before investigating.
- [ ] **`OnboardingNativeProof` assertion in `LocalAiHardwareProfileTests.Onboarding_OffersOnlyFittingProfiles`: unattributed WinRT `TaskCanceledException`.**
  - Symptom: `TaskCanceledException` from `OnboardingNativeProof.AssertLabelsAsync` with no inner error identifying the cancelled WinRT call.
  - Runs: [39851922781](https://github.com/openclaw/openclaw-windows-node/actions/runs/39851922781) (1 branch).
  - Known: no mechanism identified; a retry would be blind.
  - Next: identify which WinRT call cancels (instrument the proof helper to log each label-read step) before deciding on a fix.
- [ ] **`OnboardingAiPageTests.LocalAi_PreStartTargetRejectionRestoresChoicesInsteadOfVerificationOnly`: the Local AI card is not invokable.**
  - Symptom: `Assert.True() Failure` at `TestSupport.InvokeSettingsCardAction` (`TestSupport.cs` line 29: `card.IsLoaded && card.IsEnabled && card.IsClickEnabled`), reached from `InvokeLocalAiAsync` (`OnboardingAiPageTests.cs` line 1405).
  - Runs: [36977345825](https://github.com/openclaw/openclaw-windows-node/actions/runs/36977345825) (`da8f98ac`, #1585).
  - Next: check whether the card is still loading or disabled when the test invokes it. If so, wait for the ready state before invoking instead of asserting it.

## Core and CLI tests

- [ ] **`PiperVoiceExtractionTests.ExtractTarBz2Async_CancellationIsBoundedAndKillsExtractor`: extractor survives cancellation.**
  - Symptom: `Piper extractor process <pid> is still running.`
  - Runs: [36919488536](https://github.com/openclaw/openclaw-windows-node/actions/runs/36919488536).
  - Next: decide whether the kill is asynchronous and the test checks too early, or whether cancellation really leaves the extractor alive.
- [ ] **Shared tests with wall-clock limits fail on an overloaded runner.** All of these failed in the same run, so they share a likely cause:
  - `BoundedProcessWaitTests.WaitAsync_TimesOutAndKillsProcess`: `Timeout cleanup took 3171 ms.`
  - `TokenSanitizerTests.SanitizeLogMessage_PercentEncodedControlChars_DoNotAppearInOutput`: output was `[REDACTED_SANITIZER_TIMEOUT]` instead of containing `<host>`, so the sanitizer regex timed out.
  - `Mxc.MxcExecutorTests.RunAsync_OutputDrainIsBoundedAndPreservesFinalFragments_WhenDescendantRetainsHandles` and `RunAsync_OutputDrainPreservesExactPipeBufferFragments_WhenDescendantRetainsHandles`: `Test descendant did not report its process ID within the expected time.`
  - `MarkdownParserFuzzTests.Build_ManyListItems_ScalingCurve`: `Curve: 10k=180 20k=370 30k=880ms` against a `<500ms` limit.
  - Runs: [36910516055](https://github.com/openclaw/openclaw-windows-node/actions/runs/36910516055).
  - Next: for each test, decide whether the limit protects real behavior. If it does, measure it relative to a baseline from the same run, or isolate the test from parallel load. If it doesn't, drop the absolute limit.

## E2E

- [ ] **`Setup.RevocationAndRecoveryTests.RealGateway_DeviceRemoval_RecoversThroughSharedTokenReconnect`: MCP server never starts.**
  - Symptom: `System.TimeoutException : MCP server never came up on http://127.0.0.1:<port>/mcp within 90s. Last error: .`
  - Runs: [36919488536](https://github.com/openclaw/openclaw-windows-node/actions/runs/36919488536).
  - Next: read the tray logs from that run's E2E artifact (`TestResults/E2E/<id>`) to see whether the app started and why the MCP listener never bound.
- [ ] **`Setup.SetupAndConnectTests.RealGateway_ReusedSetupCode_IsSafeAndIdempotentForSameDevice`: `gateways.json` sharing violation.**
  - Symptom: `System.IO.IOException : The process cannot access the file '...\openclaw-e2e-<id>\gateways.json' because it is being used by another process.`
  - Runs: [36820479630](https://github.com/openclaw/openclaw-windows-node/actions/runs/36820479630).
  - Next: find which process holds `gateways.json` open (the tray process or the test reading it) and whether the read needs to share the file or wait until the writer finishes.

## Seen locally only

- [ ] **`OpenClaw.Shared.Tests.McpHttpServerTests.Dispose_DuringInFlightHandler_DoesNotSurfaceObjectDisposedException`** failed once locally while the tray test suite ran at the same time, then passed when the shared suite ran alone. It has not failed in CI yet. If it shows up in CI, look for a disposal race in the test or in `McpHttpServer`.
