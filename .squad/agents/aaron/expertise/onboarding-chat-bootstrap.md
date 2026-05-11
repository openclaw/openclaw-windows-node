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
