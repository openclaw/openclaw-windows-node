# Mattingly — PR #274 finish should open Hub chat

## Audit

Command requested: `grep -rn "launching chat\|ShowChatWindow\|ShowHub\|OnWizardComplete" src/OpenClaw.Tray.WinUI` (run with ripgrep equivalent because `rg` was not on PATH in PowerShell; Copilot rg tool was used against the same tree).

HEAD before this fix: `8c68111 Launch hub chat after onboarding`.

Matches found:

- `src/OpenClaw.Tray.WinUI/App.xaml.cs:498` — tray icon click calls `ShowChatWindow()`.
- `src/OpenClaw.Tray.WinUI/App.xaml.cs:501` — `ShowChatWindow()` method.
- `src/OpenClaw.Tray.WinUI/App.xaml.cs:542` — `ShowChatWindow` deferred-show warning string.
- `src/OpenClaw.Tray.WinUI/App.xaml.cs:644` — tray menu `openchat` calls `ShowChatWindow()`.
- `src/OpenClaw.Tray.WinUI/App.xaml.cs:562,581,647,652,654,710,1043,1855,2809,2928,3048,3101,3603,4265` — `ShowHub(...)` method/call sites.
- `src/OpenClaw.Tray.WinUI/Onboarding/OnboardingWindow.cs:587` — Finish event calls `OnWizardComplete()`.
- `src/OpenClaw.Tray.WinUI/Onboarding/OnboardingWindow.cs:596` — X/Closed path calls `OnWizardComplete()`.
- `src/OpenClaw.Tray.WinUI/Onboarding/OnboardingWindow.cs:620` — single `OnWizardComplete()` implementation.
- `src/OpenClaw.Tray.WinUI/Onboarding/OnboardingWindow.cs:649` — required diagnostic log line.
- `src/OpenClaw.Tray.WinUI/Onboarding/OnboardingWindow.cs:650,658,660,667,671,675,679` — deferred Hub chat launch helper.
- Documentation/comment-only references in `ChatWindow.xaml.cs`, `HubWindow.xaml.cs`, `VoiceOverlayWindow.xaml.cs`, and `OnboardingState.cs`.

The literal old string `launching chat` has no remaining source match in this worktree.

## Diagnosis

The log Mike captured (`[OnboardingWindow] OnWizardComplete launching chat`) corresponds to the pre-`8c68111` body of `OnboardingWindow.OnWizardComplete` in `src/OpenClaw.Tray.WinUI/Onboarding/OnboardingWindow.cs`, the only wizard-completion implementation. In the current clean worktree, `8c68111` did change that exact method to log `[OnboardingWindow] OnWizardComplete launching HubWindow on chat tab` and call `App.ShowHub("chat")`.

I did not find a second `OnWizardComplete`, overload, post-finish hook, or hidden `ShowHub` fallback to `ChatWindow`. `App.ShowHub(...)` creates a `HubWindow` when `_hubWindow` is null/closed, sets state, navigates, and activates it. The remaining `ShowChatWindow()` calls are tray quick-chat entry points, not wizard finish paths.

The prior fix therefore did not take in the live run because that run was not executing source/binaries containing `8c68111` (or was launched from another stale build/worktree). To make the wizard finish path more robust and easier to verify, this follow-up keeps the exact required log line and dispatches `ShowHub("chat")` at low priority after the wizard close event settles, so the Hub opens after the wizard finishes closing and cannot lose an ordering fight to wizard teardown.

## Changes

- `src/OpenClaw.Tray.WinUI/Onboarding/OnboardingWindow.cs`
  - Keeps the required log line: `[OnboardingWindow] OnWizardComplete launching HubWindow on chat tab`.
  - Replaces the inline post-finish call with `ShowHubChatAfterWizardClose()`.
  - The helper dispatches `App.ShowHub("chat")` on the UI dispatcher at low priority, with a direct fallback if enqueue fails.
  - Adds an explicit warning if `Application.Current` is not the tray `App`.
  - Updates stale bootstrap comment from `App.ShowChatWindow()` to HubWindow chat navigation.

- `src/OpenClaw.Tray.WinUI/Onboarding/Services/OnboardingState.cs`
  - Updates stale route comment to say the Ready path launches the Hub chat tab, not the old chat window.

- `src/OpenClaw.Tray.WinUI/Services/BootstrapMessageInjector.cs`
  - Updates stale comment to describe HubWindow chat page injection instead of post-wizard `App.ShowChatWindow()`.

## Validation

- `git pull --rebase fork feat/wsl-gateway-clean` before commit: already up to date.
- `./build.ps1`: passed.
- Tests intentionally not run per active directive: NO tests, incremental `./build.ps1` only.

## Verification log line

Mike should verify this exact line on the next finish run:

`[OnboardingWindow] OnWizardComplete launching HubWindow on chat tab`
