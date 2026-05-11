# Aaron: actual fixes for PR #274 bugs 2 and 3

## Bug 2 — tray quick-chat broken

Traced tray left-click to `InitializeTrayIcon()` -> `_trayIcon.Selected += OnTrayIconSelected` -> `OnTrayIconSelected()` -> `ShowChatWindow()`. The quick-chat path did use `ShowChatWindow`, but it resolved only `settings.Token` while the working operator client resolves `settings.Token`, `settings.BootstrapToken`, then stored `DeviceIdentity.DeviceToken` via `GatewayCredentialResolver`.

Changes:
- `App.ShowChatWindow()` and chat pre-warm now use the same `GatewayCredentialResolver` pattern as the operator client.
- `ShowChatWindow()` calls `ChatWindow.RefreshCredentials()` on every tray click, including newly-created windows.
- `ChatWindow.RefreshCredentials()` always rebuilds the URL and navigates initialized WebView2 to it; it no longer returns early when the same stale URL is cached.
- Added diagnostic logs: `[ChatWindow] Quick-chat credentials resolved from ...` and `[ChatWindow] Refreshing to ...`.
- Applied Mattingly Bug 4 handoff: bootstrap injection now runs from `ChatWindow` after successful WebView navigation.

Manual validation for Mike: click tray icon; tail `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` and look for `[ChatWindow] Refreshing to ...`, then verify chat loads without login loop.

## Bug 3 — pairing toast notification storm

Searched toast paths and traced pairing notifications through `WindowsNodeClient` direct `PairingStatusChanged` emitters (`pairing.requested`, `pairing.resolved`, `NOT_PAIRED`, and `hello-ok`) plus tray toasts in `App.OnPairingStatusChanged()` and `App.OnNodeStatusChanged()`.

Changes:
- Routed all `WindowsNodeClient` pairing emitters through `EmitPairingStatusOnTransition()`; duplicates now log `[NODE] Suppressing duplicate pairing status event: ...`.
- Added a toast-boundary 30-second dedupe in `App.ShowToast(builder, toastTag, deviceId)`, keyed by `(toastTag, deviceId)`.
- Tagged node pairing pending/paired/rejected and node-connected toasts.
- Suppressed the node-connected toast if a node-paired toast was just shown for the same device.
- Added diagnostic logs: `[ToastDeduper] Showing toast tag=... deviceId=...` and `[ToastDeduper] Suppressed duplicate toast tag=... deviceId=...`.

Manual validation for Mike: complete pairing; expect exactly one node-paired toast and log line `[ToastDeduper] Showing toast tag=node-paired deviceId=...`; duplicates should log suppression.

## Validation

Ran `./build.ps1`: passed. Per fast-loop directive, skipped `dotnet test`.
