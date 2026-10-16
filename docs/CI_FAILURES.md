# CI failure backlog

Open `Build and Test` failures seen on `main` push runs that no change has fixed yet. Check an item off, and link the fixing PR, once its fix lands and CI passes on a rerun. Delete an item if a later investigation shows it doesn't reproduce.

#1597 (fix(ci): Build and Test flakes when Workspace keyboard input or console tail polling runs late) fixes two other failures, so they are not listed here: the `WizardConsoleTailTests` cursor race and the `Missing native control 'WorkspaceNavHome'` mode of the Workspace sidebar test.

Run links point to `https://github.com/openclaw/openclaw-windows-node/actions/runs/<id>`.

## UI, functional, and accessibility tests

- [ ] **`WorkspaceWindowProofTests.SidebarSessions_SelectOriginalKeys_AndKeepCompanionDraft`: pressing Space on `WorkspaceTogglePane` does not hide the pane.**
  - Symptom: `Native navigation did not reach the expected state.` from the `WorkspaceReopenPane` wait right after `SendKeys.SendWait(" ")`. Fails in either theme.
  - Runs: [36958321581](https://github.com/openclaw/openclaw-windows-node/actions/runs/36958321581), [36943555246](https://github.com/openclaw/openclaw-windows-node/actions/runs/36943555246), [36910516055](https://github.com/openclaw/openclaw-windows-node/actions/runs/36910516055), both themes on #1597 head `3ee08eee` ([36975087646](https://github.com/openclaw/openclaw-windows-node/actions/runs/36975087646)), and Light on head `142e7d42` ([36979192680](https://github.com/openclaw/openclaw-windows-node/actions/runs/36979192680)).
  - Known: on #1597 the test checks just before the key that the Hub window is in the foreground and that `AutomationElement.FocusedElement` is `WorkspaceTogglePane`. That check passes, but does not prove key delivery or rule out a subsequent focus change. In the same job, `NativePagesAndOwnerLinks_KeepCompanionIndependent` presses Space on the same toggle successfully. Locally the test passes 20/20.
  - Suspect: an earlier step in the sidebar test, such as the Settings round-trip, `RefocusWorkspaceAsync`, or the `WorkspaceWindow.RefreshSidebar` item rebuild after a session is selected, leaves the toggle ignoring keyboard activation. Treat this as a possible product bug until proven otherwise.
  - Mitigation in #1597: the session/draft-preservation test now presses the toggle through UI Automation (`InvokePattern`). `NativePagesAndOwnerLinks_KeepCompanionIndependent` retains mandatory Space activation and focus-transfer assertions after a Settings round-trip. No retries or fallback activation were added to the keyboard test.
  - Still unverified: why Space intermittently fails in the session-selected state. Separating the preservation test from synthetic keyboard delivery does not establish a product fix for that state.
  - Local follow-up: the dedicated keyboard test also timed out after Space on `WorkspaceReopenPane` in Light theme. Its reopen-button Space and Tab steps now use the same foreground-and-focus helper as the collapse-button step, rather than checking control focus without verifying the foreground window.
  - CI follow-up: [36986094081](https://github.com/openclaw/openclaw-windows-node/actions/runs/36986094081) passed the keyboard test but failed the Dark session-preservation test with a COM exception at the new toggle invocation. The foreground-and-focus barrier removed with `SendKeys` is restored before invoking. `RefocusWorkspaceAsync` alone only finds an already-existing marker, so it does not establish native activation readiness. This readiness correction needs a new CI run; no COM exceptions are suppressed or retried.
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
