# Bug #3 Fix Plan — QuickSend dialog uses stale gateway client across pairing

**Author:** Aaron (Backend / Infrastructure)
**Date:** 2026-05-05
**Status:** PROPOSAL — awaiting RubberDucky review. **Do NOT implement until approved.**
**Bug:** #3 in Aaron-25's audit (the post-pair "copy pair command to clipboard" toast Mike saw).
**Mode:** Diagnose-only pass. No code modified.

---

## 1. Root cause (confirmed)

The QuickSend dialog captures a reference to the App's gateway client **once, at dialog construction time**, into a `readonly` field. After autopair completes, the App swaps `_gatewayClient` for a freshly-seeded instance — but the dialog still holds the old reference and continues to call `SendChatMessageAsync` against the stale (and unpaired) client. The catch block at `QuickSendDialog.cs:219` then matches on `NOT_PAIRED` / `not paired` / `pairing required` and fires the manual-recovery clipboard toast Mike saw.

**Evidence chain:**

1. `Dialogs/QuickSendDialog.cs:22` — `private readonly OpenClawGatewayClient _client;` — captured **once**.
2. `Dialogs/QuickSendDialog.cs:50–52` — constructor parameter assigned directly: `_client = client;`. Never re-read.
3. `Dialogs/QuickSendDialog.cs:201–229` — `SendMessageAsync` `catch` ⇒ `IsPairingRequired(ex.Message)` (line 219) matches `"NOT_PAIRED"` (line 302) ⇒ builds remediation commands, copies to clipboard, fires toast (lines 221–229). This is **exactly** Mike's symptom.
4. `Dialogs/QuickSendDialog.cs:334` — `EnsureGatewayConnectedAsync` only checks `_client.IsConnectedToGateway` and calls `_client.ConnectAsync()`. It never re-resolves the client from the App.
5. `App.xaml.cs:1892` — `var dialog = new QuickSendDialog(_gatewayClient, prefillMessage);` — passes whatever `_gatewayClient` is *at that moment*.
6. `App.xaml.cs:1875–1888` — if `_quickSendDialog != null` and no prefill, the **existing** dialog (with its long-captured client reference) is reactivated — multiplying the staleness window across pairing events.
7. `App.xaml.cs:1029` — `InitializeGatewayClient` constructs a NEW `OpenClawGatewayClient(... _settings.Token ...)` and reassigns the field; the previous instance is unsubscribed (line 1052) and `Dispose()`d at sites 1800/1801, 1949/1950, 2463/2464, 3129/3130.
8. `App.xaml.cs:79` — `ReinitializeGatewayClient` is called only from `ConnectionPage.cs:321` (manual onboarding test) and indirectly via `InitializeGatewayClient` on tunnel restart / onboarding-closed callback / mode toggle. **It is not called on autopair completion** — autopair runs through `LocalGatewaySetup.RunAsync` → `_operatorPairing.PairAsync` (`LocalGatewaySetup.cs:2396`) which uses a transient `using var client` (`LocalGatewaySetup.cs:1355`) and never touches the long-lived `App._gatewayClient`.
9. The post-onboarding callback `App.xaml.cs:2454–2475` only reinitializes the App client if `_gatewayClient?.IsConnectedToGateway != true`. After bootstrap-token autopair, the stale client may still be in `IsPairingRequired==true` (NOT `IsConnectedToGateway==true`), so the path *does* re-init — but only **after** `OnboardingCompleted` fires. If QuickSend is opened in the brief window between "pairing succeeded server-side" and "onboarding window closed → App reinitializes client", the dialog gets the stale instance.

**The "separate client" framing in Aaron-25's audit is technically a single shared field that the dialog snapshots and the App later swaps out from under it — same observable symptom.** From the dialog's point of view, after a swap it is effectively holding a "second" client that no one else uses.

## 2. Other tray-side gateway client holders (sister-bug audit)

```
src/OpenClaw.Tray.WinUI/App.xaml.cs:39                       _gatewayClient (singleton — gets reinitialized; CORRECT)
src/OpenClaw.Tray.WinUI/Dialogs/QuickSendDialog.cs:22        _client  ← BUG #3 (stale snapshot)
src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs:92  GatewayClient { get; set; }  ← potentially stale; see below
src/OpenClaw.Tray.WinUI/Windows/SetupWizardWindow.cs:723     using var client = new ...   (transient, scoped to test — OK)
src/OpenClaw.Tray.WinUI/Windows/SettingsWindow.xaml.cs:446   var client = new ...         (transient connection test — OK; minor leak — not Disposed, separate issue)
src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:1355  using var client = new ...  (transient handshake probe — OK)
```

**Sister bug worth fixing in same PR:** `OnboardingState.GatewayClient` is set at `ConnectionPage.cs:326,337` to `app.GatewayClient` and then read by downstream pages (e.g., `WizardPage`). Same staleness pattern — captured once into a property, may be replaced underneath. Lower-impact during onboarding because `ConnectionPage` polls and re-syncs (line 337), but should be a `Func<>` accessor for correctness.

**Non-bugs:** the three `new OpenClawGatewayClient(...)` sites in `SetupWizardWindow`, `SettingsWindow`, and `OpenClawGatewayOperatorConnector` are all single-purpose probes whose lifetime ends with the test. Token staleness is impossible because the user just typed/pasted the token in that flow.

`SettingsWindow.xaml.cs:446` — note: this client is **not** in a `using` — it leaks if the test errors before `client.Dispose()`. Tracked as a follow-up, not part of Bug #3 fix.

## 3. Fix options

| Opt | Description | Pros | Cons |
|---|---|---|---|
| **A** | Re-read `_settings.Token` inside QuickSend on every Send / catch-and-retry | Tiny change; no event plumbing | Doesn't help — QuickSend's problem is the *client instance* not the token string. Reseeding the token on the wrong client doesn't help. **Rejected.** |
| **B** | QuickSend takes a `Func<OpenClawGatewayClient?>` (or `Func<App>`) and resolves the live `App.GatewayClient` on every Send | Minimal LOC; no event plumbing; works whether dialog is freshly opened or reactivated; survives any number of swaps | Slight indirection in tests (must wrap test client in a Func) |
| **C** | Eliminate the second client entirely — make QuickSend invoke a method on App (`App.SendQuickMessageAsync(message)`) | Maximally defensive; one true client; future dialogs can't repeat the bug | Larger refactor; couples App API surface; harder to unit-test the dialog independently |
| **D** | Fire a `GatewayClientReplaced` event from App; QuickSend subscribes and rebinds `_client` | Explicit; defends other future holders too | Event plumbing + lifecycle management (unsubscribe on Closed); race window between swap and event delivery; more moving parts than B |

## 4. Recommended fix

**Option B**, with a sister-bug fix to `OnboardingState.GatewayClient` as a `Func<>`-backed accessor.

**Rationale:**
- B is the smallest change that actually addresses the captured-reference root cause (A doesn't, C is a refactor, D is over-engineered for a single dialog).
- It preserves the existing recovery toast for the legitimate "really not paired" case — we only stop firing it when there *is* a fresh, paired client available and we just weren't using it.
- B composes naturally with Mattingly's post-pair nav work and Kranz's autopair notifications (those pieces don't take dependencies on QuickSend's client).
- B does **not** interact with Aaron-23's pending QR-token-harvest follow-up (see §6).

**Shape:**

```csharp
// QuickSendDialog.cs
private readonly Func<OpenClawGatewayClient?> _clientProvider;

public QuickSendDialog(Func<OpenClawGatewayClient?> clientProvider, string? prefillMessage = null) { ... }

private async Task SendMessageAsync()
{
    var client = _clientProvider() ?? throw new InvalidOperationException("Gateway client not available");
    ...
    await client.SendChatMessageAsync(message);
    ... // catch block uses the same `client`
}

// App.xaml.cs:1892
var dialog = new QuickSendDialog(() => _gatewayClient, prefillMessage);
```

**LOC estimate:** ~35 LOC in `QuickSendDialog.cs` (field rename, ctor signature, threading the local `client` var through `SendMessageAsync` and `EnsureGatewayConnectedAsync`), 1 LOC in `App.xaml.cs:1892`, plus a parallel 5-LOC change to `OnboardingState.GatewayClient` (turn the property into `Func<OpenClawGatewayClient?> GatewayClientProvider` with a back-compat shim for in-flight onboarding code, or — simpler — leave OnboardingState alone for this PR and file as Aaron-26 follow-up). Net: **~40 LOC of product code**.

## 5. Test plan

Failure modes the test plan must cover:

1. **Happy stale-snapshot replay (the actual Mike scenario).**
   Open QuickSend with a stale/unpaired App client → autopair completes → App reinitializes `_gatewayClient` to a paired instance → user clicks Send → message is sent successfully, **no clipboard toast fires**.
   Test: `QuickSendDialogTests.Send_AfterClientReinitialized_UsesFreshClient`.
2. **Reused-dialog staleness.** `_quickSendDialog` reactivation path (`App.xaml.cs:1885–1888`): same scenario but the dialog instance is reused across the swap. Same expected outcome.
3. **Genuinely unpaired (regression guard).** App `_gatewayClient` is `null` *or* still in `IsPairingRequired==true` after `EnsureGatewayConnectedAsync` timeout → clipboard toast still fires. Mike explicitly does not want this suppressed.
   Test: `QuickSendDialogTests.Send_WhenStillUnpaired_StillCopiesPairCommand`.
4. **Mid-onboarding race — dialog opened during PairAsync.** `ShowQuickSend` currently early-returns if `_gatewayClient == null` (line 1865), and the App's gateway client is only constructed once `_settings.Token` is non-empty (line 1019). Verify autopair sets `_settings.Token` *before* the user can be prompted; if so, document the invariant. If not, `_clientProvider()` returning `null` must surface a clean "still pairing, try again in a moment" state in the dialog instead of NRE.
   Test: `QuickSendDialogTests.Send_WhenProviderReturnsNull_ShowsPairingInProgress`.
5. **Manual ConnectionPage path.** User pastes token → hits Test → `ConnectionPage.TestConnection` calls `ReinitializeGatewayClient` → opens QuickSend later → Send works first try. Regression test: `ConnectionPageIntegrationTests.ManualPair_Then_QuickSend_Succeeds`.
6. **SSH tunnel restart mid-dialog.** `App.xaml.cs:1948–1973` disposes and reinitializes `_gatewayClient` on user-requested SSH tunnel restart. With Option B this is now safe; without it, Send-after-restart would NRE on the disposed client. Test: `QuickSendDialogTests.Send_AfterTunnelRestart_UsesFreshClient`.
7. **Disposal race.** Old `_gatewayClient` is `Dispose()`d at line 1949 *before* the new one is constructed at 1973. If `_clientProvider()` is called in that window, it returns the old (disposed) instance. Mitigation: in `InitializeGatewayClient`, set `_gatewayClient = null` between dispose and reassign, OR guard inside the provider lambda. Add unit test `QuickSendDialogTests.Send_DuringSwapWindow_DoesNotThrow`.

All tests live in `tests/OpenClaw.Tray.Tests/Dialogs/` (new file) and run under the existing `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj` validation gate per AGENTS.md.

## 6. Interaction with Aaron-23 (QR-token-harvest follow-up)

**No interaction.** Aaron-23 is about how the tray harvests/persists the token from a scanned QR code into `_settings.Token` / `_settings.BootstrapToken` (write side). Bug #3 is about how a long-lived dialog *consumes* `App._gatewayClient` (read side). They touch disjoint code paths: Aaron-23 lives in `SetupCodeDecoder` + `ConnectionPage` setter wiring; Bug #3 lives in `QuickSendDialog` + `App.ShowQuickSend`. Either can ship first; merging order is irrelevant.

The only shared invariant either fix relies on: **"after pairing completes, `_settings.Token` reflects the device token (not the bootstrap token), and `App._gatewayClient` is reconstructed against that token before the user can take a paired-only action."** Today that invariant holds for the manual onboarding path and (post-Mattingly) for the autopair path via the `OnboardingCompleted → InitializeGatewayClient` flow. Both Aaron-23 and Bug #3 should add explicit assertions on this invariant in their respective test suites.

## 7. Hard guardrails honored

- ✅ No code modified in this pass — diagnosis only.
- ✅ Did not touch PID 53736 (Mike's live tray) — read-only `grep`/`view` operations on source only.
- ✅ Did not touch the OpenClawGateway distro or 17 prototype distros — no `wsl` calls in this pass.
- ✅ Plan delivered to `.squad/decisions/inbox/` for RubberDucky review before any implementation.

## 8. Awaiting RubberDucky decision

Specifically requesting RubberDucky's view on:

1. Option B vs Option C (do we want the dialog to keep its own client at all, or push QuickSend through App?).
2. Scope: include the `OnboardingState.GatewayClient` sister-bug in the same PR, or split as Aaron-26?
3. Disposal-race mitigation: guard in the provider lambda, or fix in `InitializeGatewayClient` ordering?
