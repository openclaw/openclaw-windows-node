# Bug #3 implementation report — QuickSend stale-token fix (Aaron-26)

**Author:** Aaron (Backend / Infrastructure)
**Date:** 2026-05-05
**Status:** READY FOR RUBBERDUCKY RE-REVIEW (CONDITIONAL → AGREE conversion)
**Branch:** `feat/wsl-gateway-clean`
**Commit SHA:** `ba58226`
**Push status:** NOT pushed (Mike will decide push timing).

---

## File changes

| Path | LOC | Kind |
|---|---|---|
| `src/OpenClaw.Tray.WinUI/Dialogs/QuickSendCoordinator.cs` | +233 | NEW (interface + adapter + outcome record + coordinator) |
| `src/OpenClaw.Tray.WinUI/Dialogs/QuickSendDialog.cs` | -110 / +75 (net −35) | refactor: ctor + SendMessageAsync delegated to coordinator |
| `src/OpenClaw.Tray.WinUI/App.xaml.cs` | +4 / -1 | wire `() => _gatewayClient` provider in `ShowQuickSend` (line 1916) |
| `tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj` | +1 | link the coordinator file into the test project |
| `tests/OpenClaw.Tray.Tests/QuickSendCoordinatorTests.cs` | +296 | NEW (15 tests) |

Total product code delta: ~+200 LOC (most in the new isolated coordinator file).
QuickSendDialog itself shrank by ~35 LOC.

---

## How the implementation satisfies each RubberDucky closure condition

### Closure condition #1 — Scope to QuickSend ONLY

- **Did NOT touch** `src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs`. Confirmed via `git diff --stat ba58226^..ba58226` listing only the five files above.
- **Did NOT touch** `WizardPage`, `ConnectionPage`, or any other onboarding component.
- Filed a separate follow-up plan at `.squad/decisions/inbox/aaron-bug3-onboardingstate-followup.md` for the deferred `OnboardingState.GatewayClient` sister-bug (one paragraph: bug, lifecycle delta, owner).

### Closure condition #2 — Provider/lifetime contract for null/disposed clients + tests

The contract is documented in `QuickSendCoordinator.cs:88-104` (XML-doc on the `_provider` field) and enforced by the coordinator's `SendAsync`:

| Provider state | Behavior | Code | Test |
|---|---|---|---|
| (a) Returns `null` (App field is null mid-init / mid-restart / post-dispose) | Retry once after `providerRetryDelayMs`; if still null, return `GatewayInitializing` (NO clipboard toast) | `QuickSendCoordinator.cs:140-152` | `Send_WhenProviderReturnsNull_ShowsInitializing_NoClipboard`, `Send_WhenProviderReturnsNullThenClient_RetriesAndSends` |
| (b) Returns a previously-disposed instance (race between ResolveClient and Dispose) | `SendChatMessageAsync` throws `ObjectDisposedException` or `InvalidOperationException("Gateway connection is not open")`; classified as `Failed` (NOT clipboard) | `QuickSendCoordinator.cs:200-208` | `Send_WhenResolvedClientThrowsObjectDisposed_ReturnsFailed_NoClipboard` |
| (c) Returns a live current client genuinely reporting NOT_PAIRED | Build remediation commands from THIS resolved client (not any stale snapshot); return `PairingRequired` so dialog renders the clipboard toast | `QuickSendCoordinator.cs:213-221` | `Send_WhenLiveClientGenuinelyUnpaired_StillFiresClipboardRemediation`, `Send_PairingRemediationIsBuiltFromLiveClient_NotStaleSnapshot` |

Additional swap-window mitigations:
- **Single resolution per Send.** `SendAsync` resolves once into a local `client` variable (`QuickSendCoordinator.cs:142, 156`), then passes that local into `EnsureConnectedAsync`, `SendChatMessageAsync`, and the failure classifier — no second `_provider()` call that could race.
- **Provider-throws guard.** Belt-and-braces: if a future provider impl throws, treated as `GatewayInitializing`, not as a hard fault (`QuickSendCoordinator.cs:166-176`, test `Send_WhenProviderItselfThrows_ShowsInitializing`).
- **App-side dispose ordering.** Audited all `_gatewayClient?.Dispose()` sites in `App.xaml.cs:1824-1825, 1973-1974, 2487-2488, 3153-3154`: every site already does `Dispose()` then `= null` (no dispose-after-reassign). The coordinator's null-retry covers the brief null window between dispose and the next `InitializeGatewayClient` assignment.

### Closure condition #3 — Genuine-unpaired regression test PLUS autopair integration

**Genuine-unpaired regression guard** (the case Mike does NOT want suppressed):
- `Send_WhenLiveClientGenuinelyUnpaired_StillFiresClipboardRemediation` (`QuickSendCoordinatorTests.cs:160-176`) — live client throws NOT_PAIRED, asserts `PairingRequired` outcome and that the commands come from the live client (`from=LIVE-but-unpaired`).
- `Send_PairingRemediationIsBuiltFromLiveClient_NotStaleSnapshot` (`QuickSendCoordinatorTests.cs:178-198`) — even after an A→B swap where B is genuinely unpaired, the remediation comes from B, never A.

**Autopair end-to-end at the resolver-contract layer**:
- `Autopair_End_To_End_Reinit_Then_QuickSend_Sends_Successfully` (`QuickSendCoordinatorTests.cs:233-272`) — full sequence: bootstrap-token client A reports NOT_PAIRED → App swaps field A→null→B (paired) → user clicks Send → outcome is `Sent`, A.SendCount=0, B.SendCount=1, no clipboard toast. This exercises the same resolver pathway the real autopair → `OnboardingCompleted` → `InitializeGatewayClient` flow uses.

**Real tray-process e2e harness step** (because integration-testing a WinUI window in xunit is impractical):

> 1. Start tray fresh (no token).
> 2. Drive front-door Local autopair through onboarding to completion.
> 3. While onboarding is still mid-flight (between Phase 14 success and the onboarding-window-closed callback), open Quick Send via the global hotkey and type a message.
> 4. Click Send.
> 5. **Expected after fix:** message lands in OpenClaw chat. Clipboard contents are unchanged (no pair-command remediation copied). No "device approval required" toast appears.
> 6. **Expected before fix (baseline regression):** clipboard now contains the pair-command remediation; toast appears. (This is exactly the symptom Mike captured on 2026-05-05.)

Mike runs this harness against PID 53736 (his live tray) after the next clean rebuild from `feat/wsl-gateway-clean`.

---

## Test list (15 new in `QuickSendCoordinatorTests.cs`)

| # | Test | Asserts |
|---|---|---|
| 1 | `Send_AfterClientReinitialized_UsesFreshClient` | A→B swap before Send routes to B; A untouched |
| 2 | `ReusedDialog_AfterClientSwap_UsesNewClient` | Same coordinator instance survives across swap and still routes to B |
| 3 | `Send_WhenProviderReturnsNull_ShowsInitializing_NoClipboard` | Provider always-null returns `GatewayInitializing`, never `PairingRequired` |
| 4 | `Send_WhenProviderReturnsNullThenClient_RetriesAndSends` | One null then a live client → second-try succeeds (closes swap-window race) |
| 5 | `Send_WhenResolvedClientThrowsObjectDisposed_ReturnsFailed_NoClipboard` | Disposed mid-send → `Failed`, NOT `PairingRequired`, NOT clipboard toast |
| 6 | `Send_WhenProviderItselfThrows_ShowsInitializing` | Defensive: provider exception treated as initializing |
| 7 | `Send_WhenLiveClientGenuinelyUnpaired_StillFiresClipboardRemediation` | **REGRESSION GUARD:** live client NOT_PAIRED → clipboard remediation STILL fires |
| 8 | `Send_PairingRemediationIsBuiltFromLiveClient_NotStaleSnapshot` | Remediation commands come from current resolved client, never a captured stale one |
| 9 | `Send_AfterSshTunnelRestart_UsesNewClient` | `RestartSshTunnel` swap → next Send routes to new client |
| 10 | `Send_AfterManualConnectionPageReinit_UsesNewClient` | `ConnectionPage.TestConnection → ReinitializeGatewayClient` swap → next Send routes to new client |
| 11 | `Autopair_End_To_End_Reinit_Then_QuickSend_Sends_Successfully` | **INTEGRATION:** bootstrap A → autopair → paired B → Send succeeds; no clipboard toast |
| 12 | `Send_EmptyMessage_ReturnsFailed` | Whitespace-only message rejected before any provider call |
| 13 | `Send_MissingScope_ReturnsMissingScopeOutcome_FromLiveClient` | Missing-scope error → `MissingScope` outcome with commands from live client |
| 14 | `Send_WhenNotConnected_AttemptsConnect` | Coordinator calls `ConnectAsync` when client reports not connected |
| 15 | `IsPairingRequired_MatchesAllKnownVariants` | Pattern matcher accepts all 3 production variants, rejects unrelated |

---

## Test counts

- **Tray tests:** 551 / 551 passing (baseline 536; +15 new). `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore` with `OPENCLAW_REPO_ROOT` set.
- **Shared tests:** 1158 passed / 22 skipped / 1180 total — exactly baseline (no Shared changes).
- **Build:** `./build.ps1` → all four projects (Shared / Cli / WinNodeCli / WinUI) green.

## DLL freshness

```
src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\OpenClaw.Tray.WinUI.dll  →  2026-05-05 07:46:25 (post-edit)
src\OpenClaw.Shared\bin\Debug\net10.0\OpenClaw.Shared.dll                                       →  2026-05-05 07:45:52 (post-edit)
```

Both DLLs are post-commit (`ba58226` was commit-only; build ran before commit).

## Commit SHA

`ba58226` on `feat/wsl-gateway-clean` — single commit titled
`fix(quicksend): resolve gateway client per-send to avoid stale-snapshot clipboard toast (Bug #3 from manual test)` with `Co-authored-by: Copilot` trailer.

---

## Notes for RubberDucky's re-review

1. **Coordinator is the only place pairing-required clipboard logic lives now.** The dialog is reduced to rendering the discriminated `QuickSendOutcome`. This makes the contract auditable from one file (`QuickSendCoordinator.cs`).
2. **Adapter pattern (`OpenClawGatewayClientAdapter`) keeps `OpenClawGatewayClient` untouched.** No new `virtual` methods on the shared client; no Shared.dll churn; no risk to other consumers.
3. **Swap window is closed by retry-after-null + dispose-before-null in App.** Audited all four App-side dispose sites — every one already pairs `Dispose()` immediately with `= null`. The 100ms retry inside the coordinator covers the gap between that null and the next `InitializeGatewayClient` assignment without needing a lock or a `Volatile.Read` (the field is reference-typed, atomic in CLR; tearing isn't possible).
4. **Sister-bug docstring at top of `QuickSendCoordinator.cs:1-24`** explicitly limits scope to QuickSend and points to the follow-up file for OnboardingState.
5. **Integration realism caveat.** Test #11 (`Autopair_End_To_End_Reinit_Then_QuickSend_Sends_Successfully`) simulates the autopair sequence at the resolver-contract layer because xunit cannot host a real WinUI window with a real WebSocket peer. The complementary tray-process e2e harness step is documented above; Mike runs it before merge approval.
6. **No regressions.** All 6 prior failing tests on this branch (`LocalizationValidationTests`) pass once `OPENCLAW_REPO_ROOT` is set — same pre-existing behavior as before this change.
