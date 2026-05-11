# Aaron fix DE summary

Date: 2026-05-11T13:09:00-07:00
Requested by: Mike Harsh
Branch: fix/bootstrap-injector-properly
PR: #312

## What changed

- Fix D: `ChatPage` now opens the Chat tab immediately but holds WebView navigation in a lightweight waiting state until the active gateway operator handshake has reached hello-ok (`OperatorState == Connected`) and the tokenized chat HTTP URL returns a successful response. The wait is bounded, logs readiness/probe failures, and exposes an inline Retry button instead of showing a transient WebView 404.
- Fix E: `BootstrapMessageInjector` no longer treats `form.requestSubmit()` or `button.click()` as proof of send. After the submit/click attempt, it polls for accepted-send proof: the composer cleared or the bootstrap text appears in a user-message-like transcript element. If proof is not seen within the bounded poll window, the script returns `unconfirmed` and leaves `HasInjectedFirstRunBootstrap` open for retry.

## Tests added/updated

- Added `ChatNavigationReadiness` coverage proving ChatPage navigation readiness does not complete until handshake-succeeded transitions the operator state to connected.
- Updated bootstrap injector tests so only proven `sent` consumes the gate; `unconfirmed`, `rendered`, and failure statuses keep the gate open.
- Added script-shape assertions for accepted-send proof helpers.

## Validation

- `./build.ps1` passed.
- `dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore` passed with `OPENCLAW_REPO_ROOT` set to the worktree.
- `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore` passed with `OPENCLAW_REPO_ROOT` set to the worktree.

## Manual smoke

- Stopped the locking tray process, reset WSL validation state with `scripts/reset-openclaw-wsl-validation-state.ps1 -ConfirmDestructiveClean -CleanInstallLocation`, launched with `OPENCLAW_VISUAL_TEST=1`, and confirmed the app starts without startup exceptions.
- Startup screenshot: `visual-test-output\verify\openclaw-setup-smoke.png`.
- Isolated Chat smoke screenshot: `visual-test-output\verify-chat\chat-waiting-desktop.png`; this environment could not fully drive the wizard or foreground the Chat window reliably, but the app stayed up and logs showed no startup exception.

## Mike manual checklist

A. Tray auto-opens to Chat tab when wizard finishes.
B. Chat connects to gateway.
C. Hatching prompt submits, not just typed.
D. No second pairing notification.
E. `wsl -d OpenClawGateway -- cat ~/.openclaw/devices/paired.json` shows exactly one Windows-node entry.
F. WebView never shows a 404; it shows a brief waiting state and then the chat default state directly.
