# Mattingly H1 Fix Summary — PR #312

**Date:** 2026-05-11T19:49:00-07:00

## Choice

Used the subscribe-first plus buffer-replay pattern in `OnboardingChatBootstrapper`.

## Why

Bostick's Hanselman review found that `BootstrapAsync` sent `chat.send` first and subscribed to completion events only after the send response returned. The gateway client raises `AgentEventReceived` / `ChatEventReceived` directly as frames arrive, so a final event delivered during the send-response window could be lost.

The fix attaches `RunCompletionObserver` before `SendChatMessageForRunAsync`. Since the `runId` is not available until the send response, the observer buffers final events by `runId`; after send returns it either consumes the buffered match or waits for a matching future final event. The 90-second bound is measured from observer attachment, preserving a bounded gate-consumption proof without reintroducing DOM injection or multi-node ownership.

## Regression

Added `BootstrapAsync_ConsumesGate_WhenCompletionArrivesSynchronouslyDuringSend`, where the fake gateway raises the final assistant event synchronously inside `SendChatMessageForRunAsync` before returning the `runId`. With the old send-first implementation, the test fails and the bootstrap gate remains unconsumed; with the fix, `BootstrapAsync` succeeds and `HasInjectedFirstRunBootstrap` is true.
