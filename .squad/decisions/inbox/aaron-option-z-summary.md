# Aaron Option Z summary

Date: 2026-05-11T14:47:00-07:00
Requested by: Mike Harsh
Branch: fix/bootstrap-injector-properly
PR: #312

## Gateway send finding

The gateway chat send surface is the authenticated operator WebSocket RPC `chat.send`, not a WebView DOM interaction. The tray already sends this shape through `OpenClawGatewayClient`: `{ type: "req", method: "chat.send", params: { sessionKey, message, idempotencyKey } }`. The operator handshake provides the main session key, so the bootstrap does not need to wait for WebView hydration to create a session.

## What changed

- Added `OnboardingChatBootstrapper` to send the hatching prompt via `IOperatorGatewayClient.SendChatMessageForRunAsync`.
- `ChatPage` now waits for operator handshake and chat HTTP readiness, sends the bootstrap through gateway `chat.send`, waits for completion when a run ID is returned, then navigates the WebView.
- `OpenClawGatewayClient` now surfaces chat stream events through `ChatEventReceived` so bootstrap completion can observe the assistant final emitted by gateway `event:"chat"` frames.
- The one-shot gate is consumed only after successful gateway send/completion; failures log diagnostics and fall back to opening empty chat.
- Deleted `BootstrapMessageInjector` and the WebView composer injection dispatches from `ChatPage`, `ChatWindow`, and the legacy onboarding chat overlay.

## Tests

- Added fake-client coverage for gateway bootstrap success, send failure, and completion timeout.
- Removed obsolete BootstrapMessageInjector tests.
- Kept readiness and single-node ownership code paths untouched.

## Manual checklist for Mike

A. Tray auto-opens to Chat tab when wizard finishes.
B. Chat connects to gateway.
C. Hatching conversation appears as a real two-way thread: one user message and one claw response; composer is empty and send button is ready.
D. No second pairing notification.
E. `wsl -d OpenClawGateway -- cat ~/.openclaw/devices/paired.json` shows exactly one Windows-node entry.
F. WebView never shows a 404; it shows the waiting state until the gateway/chat/bootstrap path is ready, then loads chat.
