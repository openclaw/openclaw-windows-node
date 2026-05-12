# mattingly History

## 2026-05-11T19:49:00-07:00 — PR #312 H1 onboarding chat bootstrap race

- `OnboardingChatBootstrapper` proves the first-run hatching prompt via gateway `chat.send` plus a matching final `AgentEventReceived` / `ChatEventReceived` event before consuming `SettingsManager.HasInjectedFirstRunBootstrap`.
- The completion listener must be attached before `SendChatMessageForRunAsync`; `chat.send` can synchronously receive and raise the final event before the RPC task returns.
- Because `runId` is only known from the `chat.send` response, the safe pattern is subscribe-first with a small final-event buffer keyed by `runId`, then drain/filter after the response arrives.
- Timeout should be anchored at subscription time so a slow send cannot extend the bounded completion proof window.
