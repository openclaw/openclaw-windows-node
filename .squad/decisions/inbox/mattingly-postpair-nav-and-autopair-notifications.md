# Mattingly — Post-Pairing Navigation + Autopair Notification Suppression

**Author:** Mattingly (Frontend / Onboarding UX)
**Date:** 2026-05-05
**Worktree:** `openclaw-wsl-gateway-clean`
**Live tray:** PID 53736 (Mike driving — DO NOT KILL)
**Status:** Diagnosis only. No code written. Awaiting RubberDucky review before implementation.

---

## Bug #1 plan — Post-pairing nav lands on Permissions instead of Wizard

### Current behavior (cited)

Page-order routing for the easy-setup (Local) path lives in
`src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs:119-151`:

```csharp
// L126-131  (node-mode branch)
if (Settings.EnableNodeMode)
{
    return path == Onboarding.Services.SetupPath.Local
        ? [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Permissions, OnboardingRoute.Ready]
        : [OnboardingRoute.SetupWarning, OnboardingRoute.Connection,         OnboardingRoute.Permissions, OnboardingRoute.Ready];
}

// L133-138  (operator branch — what we WANT)
if (path == Onboarding.Services.SetupPath.Local)
{
    return ShowChat
        ? [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Chat, OnboardingRoute.Ready]
        : [OnboardingRoute.SetupWarning, OnboardingRoute.LocalSetupProgress, OnboardingRoute.Wizard, OnboardingRoute.Permissions, OnboardingRoute.Ready];
}
```

The auto-advance after Phase 16 fires from
`src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs:133-146` →
`Props.RequestAdvance()` → `OnboardingApp.GoNext()`
(`src/OpenClaw.Tray.WinUI/Onboarding/OnboardingApp.cs:36-46`), which simply
increments `pageIndex` against whichever array `GetPageOrder()` returns.

**The pairing step itself sets `EnableNodeMode = true`** —
`src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs:2147` in
`PairAsync`:

```csharp
_settings.GatewayUrl     = state.GatewayUrl;
_settings.UseSshTunnel   = false;
_settings.EnableNodeMode = true;        // ← flips the branch
_settings.Save();
```

So at the moment `Status == Complete` fires and the page tries to advance, the
**node-mode branch (L126)** is now selected and the page array is
`[SetupWarning, LocalSetupProgress, Permissions, Ready]` — `Wizard` was elided.
That's exactly what Mike sees: "lands on Grant Permissions."

### Intended behavior

Post-pair → `OnboardingRoute.Wizard` (which IS the gateway-driven config wizard
— it calls `wizard.start` / `wizard.next` over the gateway WS, see
`src/OpenClaw.Tray.WinUI/Onboarding/Pages/WizardPage.cs:14-20` and the
`Props.WizardSessionId` / `Props.WizardStepPayload` plumbing in
`OnboardingState.cs:96-103`). The wizard surface IS remote-driven (gateway pushes
step payloads via JSON-RPC); WizardPage is a thin local renderer.

Prototype reference: `openclaw-windows-node/.../OnboardingState.cs:74-98` has
the same node-mode-skips-Wizard branch, BUT the prototype's local-easy-setup
flow doesn't enable node mode mid-onboarding the way the worktree's `PairAsync`
does — the prototype lacks the worktree's "tray = node of its own loopback
gateway" provisioning step (the worktree added `EnableNodeMode = true` at L2147
as part of Bug-3 / Round-5 role-upgrade fix). So the prototype's user happens
to land on Wizard because `EnableNodeMode` is still false at advance time, even
though both files have the same `if (Settings.EnableNodeMode) skip Wizard`
shape. **Delta:** worktree forces node mode on during PairAsync; prototype does
not. The page-order function was never updated to know that for an
`SetupPath.Local` flow, even node-enabled, we still want the Wizard hop because
the gateway lives on `localhost`.

### Root cause

Two coupled defects, pick one or both:

1. **`GetPageOrder()` over-applies "node mode skips Wizard."** That heuristic
   is correct for a remote gateway where a node-only client legitimately can't
   call operator RPCs. It is *wrong* for `SetupPath.Local` where the tray is
   simultaneously the node AND the operator on the loopback gateway it just
   stood up — the WizardPage's `wizard.start` call will work fine because
   `OnboardingState.GatewayClient` (set on the Connection page) and/or the new
   loopback connector both have operator credentials.
2. **`PairAsync` flips `EnableNodeMode = true` mid-onboarding.** This is
   intentional for the node-role connect (Bug-3 Round-5), but it has the
   side effect of mutating page-order behavior live, which OnboardingApp
   re-derives on every `GoNext()` (`OnboardingApp.cs:39`).

The cleaner fix is #1 — keep the engine's settings change but make
`GetPageOrder()` not strip Wizard when `SetupPath == Local`.

### Fix plan (file-by-file diff sketch)

**Single-file change preferred:**

- `src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs` (~6 LOC)
  - Reorder the two branches in `GetPageOrder()`: handle `SetupPath.Local`
    *before* the `EnableNodeMode` check, OR add `&& path != SetupPath.Local`
    to the `if (Settings.EnableNodeMode)` guard at L126. The latter is the
    smaller diff:
    ```csharp
    if (Settings.EnableNodeMode && path != Onboarding.Services.SetupPath.Local)
    { ... Permissions-only flow ... }
    ```
  - Add an inline comment explaining: *"Local easy-setup runs the gateway on
    loopback, so the tray-as-node still has operator access — keep the Wizard
    hop. Only skip Wizard for true remote-node deployments."*

**Test plan:**

- New unit test in `tests/OpenClaw.Tray.Tests` (`OnboardingStatePageOrderTests`
  if it exists, else add) covering the matrix:
  - `(SetupPath.Local, EnableNodeMode=true,  ShowChat=true)`  → must contain Wizard
  - `(SetupPath.Local, EnableNodeMode=true,  ShowChat=false)` → must contain Wizard
  - `(SetupPath.Local, EnableNodeMode=false, ShowChat=true)`  → unchanged (regression)
  - `(SetupPath.Advanced, EnableNodeMode=true, *)`            → unchanged (still skips Wizard)
- Manual: relaunch onboarding from SetupWarning → Set up locally → wait
  through Phase 1-16 → confirm auto-advance lands on **Wizard** (gateway
  config / AI provider picker), not Permissions. Verify Back button from
  Wizard returns to LocalSetupProgress and the engine doesn't re-run.
- Required AGENTS.md gates after the implementer runs the fix:
  `./build.ps1`, shared-tests, tray-tests.

**LOC estimate:** ~3 LOC production + ~30 LOC tests.

### Risk / blast radius

- Adds one extra page (Wizard) to the easy-setup happy path — `StepIndicator`
  count auto-derives from `pages.Length` so the dot count adjusts itself.
- `WizardPage` requires `Props.GatewayClient` to be set (currently populated
  on `ConnectionPage`). In the easy-setup flow we skip ConnectionPage, so we
  need to verify the wizard's gateway-client lookup path. Need to check if
  `LocalGatewaySetup.PairAsync` (which already calls `_connector.ConnectAsync`
  at L2154) populates `OnboardingState.GatewayClient` or an equivalent — if
  not, that's a sister fix the implementer needs to wire (the Wizard will
  otherwise show "offline / gateway unreachable"). **Flagging for RubberDucky
  to validate before code lands.** Likely needs a one-line hook in
  LocalSetupProgressPage's Complete handler to seed `Props.GatewayClient` from
  the engine's connector before `RequestAdvance()`.
- No effect on the manual / Advanced setup path — that branch is untouched.
- Node-mode-from-settings (real remote node deployment, set in SettingsWindow
  or SetupWizardWindow) is unaffected because `SetupPath` defaults to `Local`
  only inside the onboarding wizard; outside-of-onboarding callers don't go
  through `GetPageOrder()`.

---

## Bug #2 plan — Suppress "copy pairing command" toast during easy-setup autopair

### Notification source (cited)

The toast is built and shown in
`src/OpenClaw.Tray.WinUI/App.xaml.cs:1227-1240`,
`ShowPairingPendingNotification(string deviceId, string? approvalCommand = null)`:

```csharp
ShowToast(new ToastContentBuilder()
    .AddText(LocalizationHelper.GetString("Toast_PairingPending"))
    .AddText(string.Format(LocalizationHelper.GetString("Toast_PairingPendingDetail"), shortDeviceId))
    .AddButton(new ToastButton()
        .SetContent(LocalizationHelper.GetString("Toast_CopyPairingCommand"))
        .AddArgument("action", "copy_pairing_command")
        .AddArgument("command", command)));
```

It has exactly two callers:

1. **`App.xaml.cs:1196-1205` `OnPairingStatusChanged`** — fires whenever
   `_nodeService.PairingStatusChanged` lands with `PairingStatus.Pending`.
   This is the offending caller during easy-setup: when `PairAsync` calls
   `_connector.ConnectAsync` (LocalGatewaySetup.cs:2154), the loopback gateway
   parks the role-upgrade as `Pending` (Bug-3 Round-5 comment at L2158-2170),
   the node service raises `PairingStatusChanged(Pending)`, and the toast
   fires — even though the engine is about to auto-approve it 100ms later.
2. **`Onboarding/Pages/ConnectionPage.cs:366`** — the *manual* setup path's
   ConnectionPage call site. This one is correct and must stay.

The toast button click handler at `App.xaml.cs:3063-3065` writes the command
to clipboard. That's fine; we just don't want the toast surfaced during
autopair.

### Trigger condition today

Always fires on any `PairingStatus.Pending` event from `_nodeService` for any
device id, regardless of caller context. There is currently **no flag**
distinguishing "I'm doing autopair, expect a pending blip and suppress the
toast" from "the user is mid-manual-setup and needs the copy button."

### Suppression mechanism

Cleanest design: a transient "autopair-in-progress" gate owned by the engine
and read by `App.OnPairingStatusChanged`.

**Recommended approach — engine-owned suppression flag:**

1. Add `bool IsAutoPairing { get; }` (internal/public getter, private setter)
   to `LocalGatewaySetupEngine` in
   `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs`.
2. Set it `true` at the top of `PairAsync` (~L2139) and reset to `false` in
   a `finally` block before return (covers success, failure, retry, cancel).
3. In `App.OnPairingStatusChanged` (App.xaml.cs:1196), early-return on
   `Pending` if the engine instance reports `IsAutoPairing == true`.
   - The App already has access to the engine via the `s_engine` static the
     LocalSetupProgressPage uses. Cleaner to expose a property on `App`:
     `internal bool IsLocalSetupAutoPairing => _localSetupEngine?.IsAutoPairing ?? false;`
     and have `CreateLocalGatewaySetupEngine()` (App.xaml.cs:56) cache the
     engine into a field instead of constructing-and-forgetting. (Today the
     engine is held by the page in `LocalSetupProgressPage.s_engine`; mirror
     that into an `App._localSetupEngine` field, cost ~2 LOC.)
4. The `Paired` and `Rejected` branches at App.xaml.cs:1206-1219 stay
   untouched — those toasts are confirmations, not action prompts, and Mike's
   directive is specifically about the "copy command" notification.

**Why not check `OnboardingState.SetupPath == Local`?** Because the autopair
event window is narrower than the entire Local-setup flow — if the engine
finishes and the user is still on LocalSetupProgress for the 1s pause, a
spurious pending event from a different node legitimately *should* show the
toast. The engine-flag scope is exactly the `PairAsync` window, which is
correct.

### Fix plan (file-by-file diff sketch)

- `src/OpenClaw.Tray.WinUI/Services/LocalGatewaySetup/LocalGatewaySetup.cs`
  (~6 LOC): add `IsAutoPairing` property; set/reset around `PairAsync` body
  (try/finally).
- `src/OpenClaw.Tray.WinUI/App.xaml.cs` (~6 LOC):
  - Add `private LocalGatewaySetupEngine? _localSetupEngine;` field.
  - Cache the engine in `CreateLocalGatewaySetupEngine()` (line 56).
  - In `OnPairingStatusChanged` Pending branch (line 1202-1205), guard:
    ```csharp
    if (_localSetupEngine?.IsAutoPairing == true)
    {
        Logger.Info($"Suppressing pairing-pending toast: autopair in progress for {args.DeviceId}");
        return;
    }
    ShowPairingPendingNotification(args.DeviceId);
    ```
- `src/OpenClaw.Tray.WinUI/Onboarding/Pages/LocalSetupProgressPage.cs`:
  No change needed — page already calls `app.CreateLocalGatewaySetupEngine()`
  which will now also seed the cached field on App.

**Test plan:**

- New unit test in tray tests: `LocalGatewaySetupEngine_IsAutoPairing_ToggledAroundPairAsync`
  — assert false before, true during (use a fake connector that signals via
  TaskCompletionSource), false after.
- Integration: relaunch easy-setup → confirm zero "Copy pairing command"
  toasts during Phase 12-16. Confirm the post-pair "Node paired" success
  toast still fires (different branch).
- Manual setup regression: open SetupWarning → Advanced → ConnectionPage,
  trigger a pairing pending (use a remote gateway that requires manual
  approve) → confirm the toast still appears with the copy button.
- AGENTS.md gates after implementer: `./build.ps1`, shared-tests, tray-tests.

**LOC estimate:** ~12 LOC production + ~30 LOC tests.

### Risk / blast radius

- The suppression is scoped to (a) `Pending` status only and (b) the
  `PairAsync` call window only. `Paired` / `Rejected` confirmation toasts
  remain. Manual-setup `ConnectionPage` path is untouched (it calls
  `ShowPairingPendingNotification` directly, bypassing the
  `OnPairingStatusChanged` event path entirely — so the gate doesn't apply).
- One subtle case: if the engine fails mid-`PairAsync` and we reset
  `IsAutoPairing=false` in `finally`, a *subsequent* late `PairingStatus.Pending`
  event arriving after the engine gave up *will* show the toast. That's
  arguably correct — once auto-pair has bailed, the user does need a manual
  approve route. Worth confirming with Mike but I'd ship as-is.
- Cross-check with Aaron-25: as of report-write time
  `.squad/decisions/inbox/aaron-wsl-gateway-pollution-audit.md` has not
  landed. If Aaron identifies a *different* notification source (e.g., a
  toast emitted from the gateway distro side via `system.notify`), this plan
  needs to be widened — `OnNodeNotificationRequested` (App.xaml.cs:1242) is
  a second toast surface that doesn't go through `ShowPairingPendingNotification`
  but could still surface "copy this command" text. Recommend RubberDucky
  hold the implementer until Aaron's audit lands so we don't ship a
  half-suppression.

---

## Open questions for RubberDucky

1. Bug #1 sister-issue: does `LocalGatewaySetup.PairAsync` populate
   `OnboardingState.GatewayClient` (or the equivalent the WizardPage expects)?
   If not, the Wizard hop will render but show "gateway offline" — needs a
   one-line wire-up alongside the page-order fix.
2. Bug #2: should the suppression also cover `OnNodeNotificationRequested`
   (line 1242) for the autopair window, in case the gateway pushes a
   `system.notify` "copy approval command" message via the node channel?
   Defer to Aaron-25's audit.
3. Both: confirm we should NOT touch the page-order to add the Wizard hop
   for the `EnableNodeMode + Advanced` remote-node case (today it correctly
   skips Wizard there because true remote nodes can't call operator RPCs).

— Mattingly
