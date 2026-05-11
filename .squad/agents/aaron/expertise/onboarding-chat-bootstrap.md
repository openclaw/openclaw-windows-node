# Onboarding chat bootstrap handoff

## Handoff chain

The post-wizard chat path is: `OnboardingWindow.OnWizardComplete()` detects that the user finished from `OnboardingRoute.Ready` and that `StartupSetupState.RequiresSetup(...)` is false, then calls `ShowHubChatAfterWizardClose()`. That defers onto the UI dispatcher and calls `App.ShowHub("chat")`, which creates/shows the `HubWindow` and routes to `HubWindow.NavigateTo("chat")`; the chat navigation constructs/initializes `ChatPage`, and `ChatPage.Initialize(...)` loads the gateway WebView and invokes `BootstrapMessageInjector` after successful navigation.

## Hidden contract

The injector is in the post-wizard launch chain. Any exception from injection dispatch, selector logic, WebView script execution, timing, or settings persistence must be swallowed/logged and must not prevent the Hub/Chat auto-launch. `BootstrapMessageInjector.InjectAsync(...)` catches `OperationCanceledException` and all other exceptions and returns `false`; `ChatPage`, `ChatWindow`, and the legacy onboarding overlay also wrap the fire-and-forget dispatch site so no injector failure can bubble into window/navigation code.

## PR archeology

PR #274 (`581f78d276e1e6569f6385a9a991b60467c4c6e5`) introduced the local WSL onboarding flow and removed the in-wizard chat preview from the page order. That created the hidden contract that first-run completion from Ready should open the Hub chat tab after the wizard closes, while still preserving a one-shot BOOTSTRAP.md kickoff. PR #307 (`7f49beb0f571ca7ebbaf1e36bc1c685bcbf06b49`) touched only `BootstrapMessageInjector` and its tests; it tried to verify rendered composer text before consuming the gate, but over-specialized the JS selectors to a particular captured composer shape and left the injector coupled tightly enough that a failure could break the perceived post-wizard handoff.

## Live-DOM / selector evidence

This fix deliberately does not depend on one exact live chat composer DOM. The prior narrow selectors (`textarea[placeholder="Message Assistant (Enter to send)"]`, `textarea[aria-describedby="chat-slash-active-announcement"]`, and `.chat-send-btn`) came from the failed #307-era approach and are too brittle for the gateway chat UI. Instead, the injector now discovers broad chat-composer primitives across the document and open shadow roots: enabled `textarea`, text `input`, plain `input`, `[contenteditable="true"]`, and `[role="textbox"]`, then tries form submission, explicit send buttons, and submit buttons. Because the final approach avoids a DOM-shape-specific matcher, live-DOM capture is not blocking for correctness; visual smoke did capture the onboarding startup page at `visual-test-output\verify\page-00.png`, and no bootstrap/onboarding exception lines were present in `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` during launch.

## Matchers and timing

`BootstrapMessageInjector.BuildInjectionScript(...)` encodes the message with `JsonSerializer.Serialize` so BOOTSTRAP text cannot break out of JS string context. Discovery walks `document` plus open `shadowRoot`s, filters to visible/enabled/editable elements, sets native input values (or `textContent` for contenteditable), dispatches `input` and `change`, and verifies the composer contains the message before consuming `SettingsManager.HasInjectedFirstRunBootstrap`. `ChatPage` and `ChatWindow` call injection after successful navigation with a 500ms delay; the legacy onboarding overlay still uses the service default 3000ms delay. The one-shot gate is consumed only for `sent` or `rendered`, so a missing input or failed render retries on the next chat visit instead of permanently losing the hatching prompt.

## 2026-05-11 double-init investigation

The post-wizard handoff guard held in the live repro: `OnWizardComplete` logged the Hub chat launch once. The duplicate notification/state churn came after handoff from two Windows-node connection owners. Local setup had already created and paired the legacy `NodeService` identity under `%APPDATA%\OpenClawTray`; after onboarding completed, `GatewayConnectionManager.HandleHandshakeSucceededAsync()` started its `NodeConnector` and used the per-gateway/operator identity path as a node identity, causing a role-upgrade pairing and a second Windows node entry in gateway `paired.json`.

Do not assume post-wizard chat weirdness means `ShowHubChatAfterWizardClose()` fired twice. First check for two node identities in gateway `paired.json`: the canonical tray identity from `%APPDATA%\OpenClawTray\device-key-ed25519.json`, plus a per-gateway/operator identity being connected as `role=node`. If both exist, the immediate fix should be single node ownership: suppress the connection-manager `NodeConnector` while local setup/legacy `NodeService` owns the tray node, or make both paths share the same canonical node identity.

Bootstrap has its own race window: `InjectAsync()` checks `HasInjectedFirstRunBootstrap` before `initialDelayMs`, then executes later without re-checking. Multiple WebView navigation completions can therefore schedule overlapping injector tasks that all passed the gate before the first one persisted `HasInjectedFirstRunBootstrap`. Re-check the gate after the delay and add an in-flight guard before treating any future double-send or text-in-composer symptom as a gateway pairing bug.

## Bug A PR archeology

Bug A's double Windows-node ownership is not ancient. The local WSL setup side came from PR #274 (`581f78d276e1e6569f6385a9a991b60467c4c6e5`): `CreateLocalGatewaySetupEngine()` eagerly creates `NodeService`, and `LocalGatewaySetupEngineFactory` wraps it in `NodeServiceWindowsNodeConnector` to pair the canonical tray node identity. The second owner came later from PR #304 (`e3c6504aaf27bfe5862ed2029865086613d6f866`): `GatewayConnectionManager.HandleHandshakeSucceededAsync()` starts `StartNodeConnectionAsync()` via `NodeConnector` when `ShouldInitializeNodeService()` returns true. Treat WSL local gateway symptoms after setup as a single-ownership problem first: once setup has paired the canonical `NodeService`, the connection manager must not also start a per-gateway/operator-identity node connector for that same local gateway.

## 2026-05-11 fix ABC notes

The local WSL gateway has two possible Windows-node startup paths: the setup-owned `NodeService` using the canonical `%APPDATA%\OpenClawTray` identity, and PR #304's manager-owned `NodeConnector` using the per-gateway/operator identity. For local `GatewayRecord.IsLocal` entries, if `StartupSetupState.HasStoredNodeDeviceToken(IdentityDataPath)` is true and `EnsureNodeServiceForLocalGatewaySetup(...)` can provide the local node service, suppress the manager-owned connector; keep the manager connector for remote/no-local-setup gateways.

Bootstrap injection must treat `HasInjectedFirstRunBootstrap` as both a persistent gate and an in-process critical section. Check the gate before scheduling, acquire an in-flight guard before the delay, re-check the gate immediately after the delay, and only consume the gate when script status is proven `sent`; `rendered`, `unconfirmed`, missing input, and missing send control are diagnostic only and should retry later. A proven `sent` means the submit/click was followed by accepted-send proof: the composer cleared or the hatching text appeared in a user-message-like transcript element.

## 2026-05-11 navigation readiness hazard

Post-wizard ChatPage navigation can race the local gateway's chat HTTP surface. `OnWizardComplete()` launches Hub chat immediately, and `ChatPage.Initialize(...)` resolves credentials/builds `http://localhost:18789?token=...`; the safe pattern is to keep the Chat tab open in a lightweight waiting state, wait for `GatewayConnectionManager` operator `hello-ok` (`OperatorState == Connected`), then GET-probe the tokenized chat URL until it returns a non-error response before calling `WebView.CoreWebView2.Navigate(...)`. In the live PR #312 repro, tray operator/node sockets were still receiving `gateway starting; retry shortly` while the WebView was already opening chat; Mike saw an initial 404, then a reload/default chat state, then the hatching prompt stuck in the composer. Treat future 404/reload/bootstrap symptoms as a navigation-vs-readiness race first: defer WebView navigation and bootstrap injection until the gateway proves the HTTP chat app is serving, bound the wait with an inline retry path, and do not consume `HasInjectedFirstRunBootstrap` on a click-only false-positive `sent`.
