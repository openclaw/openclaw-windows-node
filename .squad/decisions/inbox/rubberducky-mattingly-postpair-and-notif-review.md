# RubberDucky Review — Mattingly post-pair nav + autopair notifications

**Date:** 2026-05-05T07:10:00-07:00  
**Reviewer:** RubberDucky  
**Plan reviewed:** `.squad/decisions/inbox/mattingly-postpair-nav-and-autopair-notifications.md`  
**Aaron-25 audit update:** User reported Aaron-25 found no WSL pollution and identified the post-pairing QuickSend toast as separate Bug #3. Filesystem lookup still did not find `.squad/decisions/inbox/aaron-wsl-gateway-pollution-audit.md` in either worktree during this review update, so this addendum cites the user update plus `QuickSendDialog.cs` evidence.

---

## Bug #1 — Post-pair nav lands on Permissions instead of Wizard

**Verdict:** CONDITIONAL AGREE

### What's right

- Root cause is real: `PairAsync` mutates `_settings.EnableNodeMode = true` before setup completes (`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:2145-2148`), and `GetPageOrder()` currently strips `Wizard` whenever node mode is true (`src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs:125-131`).
- The auto-advance path re-derives the page array at advance time (`src/OpenClaw.Tray.WinUI/Onboarding/OnboardingApp.cs:36-45`), so the mid-flow settings mutation can change the destination after `LocalSetupProgress` completes.
- Keeping `EnableNodeMode=true` is consistent with Round-17 Bug 3: Phase 14 intentionally pairs the Windows tray as a node (`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:2398-2402`; `.squad/decisions.md:79-87`). Do not “fix” this by disabling node mode.

### What's wrong, missing, or assumed

1. **Page-order-only fix is insufficient: Wizard will likely render offline.** `WizardPage` only proceeds when `app.GatewayClient ?? Props.GatewayClient` is connected; otherwise it waits up to 30s and saves `offline` (`src/OpenClaw.Tray.WinUI/Onboarding/Pages/WizardPage.cs:188-207`). Easy setup creates a local setup engine, not a gateway client (`src/OpenClaw.Tray.WinUI/App.xaml.cs:56-64`), and `LocalSetupProgressPage` only calls `RequestAdvance()` on completion (`src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs:133-145`). After onboarding, node mode takes the startup branch that initializes `NodeService`, not the operator `GatewayClient` (`src/OpenClaw.Tray.WinUI/App.xaml.cs:383-394`).
2. **Mattingly's prototype claim is not supported by the prototype tree.** The prototype `SettingsWindowsTrayNodeProvisioner.PairAsync` also sets `_settings.EnableNodeMode = true` (`C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-windows-node\src\OpenClaw.Tray.WinUI\Services\LocalGatewaySetup\LocalGatewaySetup.cs:2104-2113`). Do not use “prototype did not flip node mode” as evidence for the chosen fix.
3. **The proposed unit test matrix catches route shape, not the live failure.** Existing tests already include a node-mode-local expectation that skips Wizard (`tests/OpenClaw.Tray.Tests/OnboardingStateTests.cs:106-118`); changing that proves the array only. It does not prove the auto-advance from `LocalSetupProgress` visits Wizard or that Wizard has a connected gateway client (`src/OpenClaw.Tray.WinUI/Onboarding/OnboardingApp.cs:36-45`; `src/OpenClaw.Tray.WinUI/Onboarding/Pages/WizardPage.cs:188-207`).
4. **`SetupPath == Local` is the right nav discriminator, but only for onboarding.** `SetupPath` defaults to Local in `GetPageOrder()` (`src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs:121-124`), so the guard must be covered by tests for explicit `SetupPath.Local`, explicit `SetupPath.Advanced`, and null default. The fix should not add Wizard for explicit Advanced node-mode flows (`src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs:141-149`).

### Recommended changes

1. Keep the `path == SetupPath.Local` exception to the node-mode skip, or reorder `GetPageOrder()` so explicit Local is handled before node mode.
2. Add the sister fix in the same implementation: before auto-advancing to Wizard, seed a connected operator `OpenClawGatewayClient` into `App.GatewayClient` or `OnboardingState.GatewayClient` using the paired local gateway credentials. The smallest acceptable shape is: on setup completion, call the existing app gateway-client initialization path and only then request advance.
3. Update `OnboardingStateTests.GetPageOrder_LocalPath_NodeMode_SkipsWizardAndChat` (`tests/OpenClaw.Tray.Tests/OnboardingStateTests.cs:106-118`) to the new expected route and add explicit Advanced node-mode coverage.
4. Add an integration-style test/seam that simulates `LocalSetupProgress` completion after `EnableNodeMode=true` and asserts the next route is `Wizard` and `WizardPage` sees a connected client or a test double.

### Failure modes to test

- Local setup, `EnableNodeMode=true`, `ShowChat=true`: auto-advance from `LocalSetupProgress` lands on `Wizard`, not `Permissions`.
- Local setup, `EnableNodeMode=true`, `ShowChat=false`: route includes `Wizard` and then `Permissions`.
- Advanced setup, `EnableNodeMode=true`: route still skips `Wizard`.
- Null `SetupPath` default: no unexpected remote-node behavior from the Local default.
- Wizard after local setup has connected `GatewayClient`; no `offline` state after the 30s poll.
- Back from Wizard to `LocalSetupProgress` does not restart the engine (`LocalSetupProgressPage.s_engine` persists at `src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs:41-44`).

### Closure conditions to convert to AGREE

- Production diff includes both route fix and gateway-client seeding for the Wizard hop.
- Tests cover route matrix plus a completion/advance assertion that would fail if `OnboardingApp.GoNext()` landed on `Permissions`.
- Manual/e2e verification confirms Wizard is live, not offline.

**Confidence:** HIGH

---

## Bug #2 — Clipboard “copy pair command” notification fires during autopair

**Verdict:** CONDITIONAL AGREE

### What's right

- The offending toast path is real: `App.OnPairingStatusChanged` shows `ShowPairingPendingNotification` for every `PairingStatus.Pending` (`src/OpenClaw.Tray.WinUI/App.xaml.cs:1196-1205`), and that toast includes the `copy_pairing_command` button (`src/OpenClaw.Tray.WinUI/App.xaml.cs:1227-1239`).
- The current Phase 14 event surface still produces `Pending`: `WindowsNodeClient` raises `PairingStatus.Pending` on `NOT_PAIRED` (`src/OpenClaw.Shared/WindowsNodeClient.cs:730-749`), `NodeService` forwards it (`src/OpenClaw.Tray.WinUI/Services/NodeService.cs:494-503`), and the local setup path wires `NodeService.PairingStatusChanged += OnPairingStatusChanged` (`src/OpenClaw.Tray.WinUI/App.xaml.cs:1131-1155`).
- Manual ConnectionPage notification is a separate direct call (`src/OpenClaw.Tray.WinUI/Onboarding/Pages/ConnectionPage.cs:359-366`), so gating only the node-service event path should preserve manual setup.

### What's wrong, missing, or assumed

1. **The plan names the wrong owner for the flag.** `LocalGatewaySetupEngine` has `RunLocalOnlyAsync`, not `PairAsync` (`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:2212-2267`). The `PairAsync` that emits the node-role connect is on `SettingsWindowsTrayNodeProvisioner` (`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:2139-2155`). A flag cached on `App._localSetupEngine` cannot observe a private flag on the provisioner unless the engine explicitly exposes/brackets the Phase 14 call.
2. **Scope must be Phase 14 / node-role autopair, not all local setup.** `RunLocalOnlyAsync` also performs operator pairing at Phase 12 (`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:2395-2397`) before Phase 14 node pairing (`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:2398-2402`). If the flag wraps the whole engine run, it may suppress unrelated pending notifications longer than needed.
3. **The initial Pending event is not racy if the flag brackets the connect call.** `WindowsNodeClient` invokes `PairingStatusChanged` synchronously in `HandleRequestError` (`src/OpenClaw.Shared/WindowsNodeClient.cs:730-749`), and `NodeService` forwards synchronously (`src/OpenClaw.Tray.WinUI/Services/NodeService.cs:494-503`). Therefore the safe bracket is around `_windowsTrayNode.PairAsync(...)` / `_connector.ConnectAsync(...)`, not a delayed app-level heuristic.
4. **Aaron-25 releases Mattingly's hold; it does not close the implementation issues above.** The user-reported Aaron-25 finding says the post-pairing toast Mike saw is a separate QuickSend bug, not a second `App.OnPairingStatusChanged`/`system.notify` surface. Code supports that separation: QuickSend has its own catch block that copies remediation commands and shows a toast on pairing-required errors (`src/OpenClaw.Tray.WinUI/Dialogs/QuickSendDialog.cs:216-229`), while `OnNodeNotificationRequested` only displays title/body and no copy-command button (`src/OpenClaw.Tray.WinUI/App.xaml.cs:1242-1251`).
5. **Test plan needs an App-level event-path test.** A `LocalGatewaySetupEngine_IsAutoPairing_ToggledAroundPairAsync` test proves the flag toggles, but not that `App.OnPairingStatusChanged` suppresses `ShowPairingPendingNotification` while leaving `Paired`/manual notifications intact (`src/OpenClaw.Tray.WinUI/App.xaml.cs:1202-1211`; `src/OpenClaw.Tray.WinUI/Onboarding/Pages/ConnectionPage.cs:359-366`).

### Recommended changes

1. Implement the suppression state where the app can observe it and where the scope is exact: either
   - add an engine property that is set only while executing the Phase 14 `RunProvisioningPhaseAsync(... PairWindowsTrayNode ...)`, or
   - add a `NodeService`/connector suppression scope used only by `SettingsWindowsTrayNodeProvisioner.PairAsync`.
2. Do not call it “engine PairAsync”; there is no such method. The code should bracket `SettingsWindowsTrayNodeProvisioner.PairAsync` or the engine's Phase 14 invocation.
3. In `App.OnPairingStatusChanged`, suppress only `Pending` while that exact auto-pair scope is active; leave `Paired` and `Rejected` unchanged.
4. Do not block on Aaron-25 for this scoped fix. Track QuickSend's separate gateway-client/token seeding failure as Bug #3; do not try to solve it by widening Bug #2 suppression.

### Failure modes to test

- During Phase 14 local role-upgrade pending, no toast with `copy_pairing_command` is shown.
- During the same flow, `PairingStatus.Paired` still records/shows the paired confirmation.
- After the auto-pair scope exits, a later unrelated `Pending` event does show the copy-command toast.
- Manual Advanced/ConnectionPage pairing still shows and copies the command.
- Failure path: if auto-approve fails and a later `Pending` arrives after scope exit, the toast appears.
- QuickSend pairing-required toast remains reproducible as a separate Bug #3 and is not suppressed by the Bug #2 change (`src/OpenClaw.Tray.WinUI/Dialogs/QuickSendDialog.cs:216-229`).

### Closure conditions to convert to AGREE

- The implementation brackets the actual Phase 14 node-role `PairAsync` call, not the whole local setup run and not a nonexistent engine `PairAsync`.
- Tests exercise `App.OnPairingStatusChanged` or an extracted pure suppression decision, not only the raw flag toggle.
- Manual ConnectionPage notification remains covered.
- Bug #2 implementation does not attempt to suppress or fix QuickSend; that is separate Bug #3.

**Confidence:** HIGH


