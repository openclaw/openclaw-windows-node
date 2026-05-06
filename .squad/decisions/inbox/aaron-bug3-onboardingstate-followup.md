# Follow-up: OnboardingState.GatewayClient stale-snapshot sister-bug (Aaron-26)

**Status:** DEFERRED from Bug #3 PR (RubberDucky closure condition #1).
**Filed:** 2026-05-05 by Aaron during Bug #3 implementation.

## What's the bug

`src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs:92` exposes
`OpenClawGatewayClient? GatewayClient { get; set; }` — a mutable property that
downstream onboarding pages capture into `Props.GatewayClient` and read later.
If the App rotates `_gatewayClient` (autopair completion, manual reinit) while
the onboarding wizard is still mid-flow, those page props can hold a stale
reference. Same root pattern as Bug #3.

`WizardPage` already partially compensates by preferring `App.GatewayClient`
and polling before falling back to `Props.GatewayClient`
(`Onboarding/Pages/WizardPage.cs:188-199`, `:257-260`), so the observable
impact is smaller than QuickSend's — but the latent shape is identical.

## Why deferred (lifecycle delta from QuickSend)

1. **Different access window.** OnboardingState.GatewayClient is read
   *during* pairing (before pairing completes), not *after*. The Bug #3
   "post-pair stale snapshot" failure mode does not apply 1:1.
2. **Different ownership semantics.** `OnboardingState.Dispose()` currently
   disposes whatever is in `GatewayClient`
   (`Onboarding/Services/OnboardingState.cs:171-175`). Replacing the property
   with a `Func<>` provider changes ownership — the App owns the lifetime,
   not OnboardingState. That requires:
   - Updating `OnboardingState.Dispose()` to NOT dispose what it doesn't own.
   - Auditing every reader for assumed ownership.
   - Updating Wizard/Connection page tests.
3. **Blast radius.** Folding this into Bug #3 expands the PR from a single
   dialog refactor to onboarding-wide ownership rework. RubberDucky
   explicitly asked us not to do that in the same PR.

## Who picks it up

Either Aaron or Mattingly, in a separate plan/PR titled
**"fix(onboarding): resolve gateway client per-access in OnboardingState +
WizardPage props (Aaron-27)"**. Recommended approach: convert the property to
`Func<OpenClawGatewayClient?> GatewayClientProvider`, change `Dispose()` to
no-op on the gateway client (App owns it), update callers in `WizardPage`,
`ConnectionPage`, and any other reader to invoke the provider on demand.
Mirror the QuickSend test strategy (per-access stale-snapshot,
null-during-swap, genuinely-unpaired regression).

## Why not now

Bug #3 manual-test severity is high (Mike just hit it). Bug #3 fix is
small/contained. Sister-bug fix is bigger and lower observed severity.
Shipping Bug #3 alone unblocks Mike's tray testing without taking on the
onboarding ownership refactor in the same diff.
