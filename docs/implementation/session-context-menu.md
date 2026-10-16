# Workspace chat: sidebar session context menu: implementation summary and proof

Initial implementation: `f51a70a9`. The original measurements below are historical,
not final-head validation.

## Review repairs

The Workspace menu now awaits matching Gateway acceptance for patch/delete rather
than treating a successful socket write as a successful mutation. A captured
operator client and authenticated transport epoch are checked before dispatch
and after responses. Rename cannot retarget a different Gateway while its dialog
is open. Rejections are shown in the Workspace; timeouts explicitly report an
unknown outcome and are not retried automatically.

Existing send-only APIs, including Reset/Compact event consumers, retain their
semantics. Confirmed patch/delete do not emit generic session-action events.
The menu requests a fresh session list after acceptance. Automatic read
acknowledgement uses the confirmed path without action toasts; failures are
recorded in diagnostics and leave authoritative unread state untouched.

Confirmed removal prunes the session from back/forward and remembered chat
destinations. The retained ChatPage explicitly disposes the removed conversation,
cancels pending navigation, and shows a noninteractive selection prompt if there
is no replacement. Normal Home navigation still preserves drafts. Every row title
uses the constrained Star column, preserving room for the optional pin and
busy/unread indicator.

Regression coverage includes correlated response acceptance, rejected envelopes
and payloads, timeout/late response, connection changes across dialogs/responses,
history pruning, and real WinUI long-title layout. The opt-in in-memory Gateway
fixture supports held session mutations; ordinary browse fixtures stay read-only.
Real-app tests drive the context menu with Shift+F10, prove that a held response
does not navigate, preserve a draft after rejection, and remove the final
conversation without retaining its composer or creating a new session.

Validation is recorded in the PR with exact commands and source revision.
Synthetic Gateway proof establishes Companion behavior, not live Gateway
compatibility or managed-local credential-resolution timing.

## Archive navigation design

The Workspace sidebar shows active conversations only. Its Archive context-menu
action remains available, but it does not contain an Archived dropdown or fetch
archived rows. Archived conversations are listed under **Settings > Sessions >
Archived**, with an Unarchive action that waits for Gateway acceptance before
refreshing both lists. Restore a conversation there to use its normal chat and
context-menu actions again.

`ArchivedSessionsSource` now belongs to the Settings Sessions page. It loads only
when the section is expanded and drops snapshots from an old Gateway, transport
epoch, or page lifetime. The removed Workspace responsibility is marked closed
in the architecture ledger.

## Scope delivered

Right-click menu on Workspace chat sidebar session rows, mirroring the web chat session menu plus Windows extras. Archive browsing and restoration live in Settings Sessions.

Core parity: Pin/Unpin, Rename, Mark as unread/read, Archive, Fork conversation, Copy > (session key, session ID, as Markdown), Delete.
Windows extras: Reset, Compact, Export transcript (same behavior as the Sessions page).
Settings Archived rows offer Unarchive. Restored rows support the normal Workspace menu.
State rendering: pinned rows first with a pin glyph, unread accent dot, archived rows hidden from the active list.

Out of scope (unchanged, per plan): Icon & color, Move to group, Assign to, Open in.

## Architecture

| Responsibility | Owner |
|---|---|
| Pure menu entries, patch payloads, fork request | `Presentation/WorkspaceSessionMenu.cs` |
| Flyout population, dialogs, gateway dispatch, read acknowledgement | `Windows/WorkspaceSessionMenuController.cs` |
| Archived session list snapshot and Settings rendering | `Services/ArchivedSessionsSource.cs` and `Pages/SessionsPage.xaml.cs` |
| Active sidebar row application and navigation/leave | `Windows/WorkspaceWindow.xaml.cs` |
| Row ordering, archived/agent filtering, titles | `Presentation/WorkspaceProjection.cs` |
| Icon glyph constants | `Helpers/FluentIconCatalog.cs` |
| Ownership ledger | `docs/ARCHITECTURE.md` (new authoritative row; `WorkspaceWindow.xaml.cs` row extended) |

### Shared protocol (`src/OpenClaw.Shared`)

- `SessionInfo`: `Pinned`, `PinnedAt`, `Unread`, `MarkedUnreadAt`, `Archived`.
- `PopulateSessionFromObject` parses `pinned`, `pinnedAt`, `unread`, `markedUnreadAt`, `archived` with authoritative-list reset semantics (mirrors the `thinkingLevel` reset pattern; explicit `TryGetProperty` shape so the drift guard sees each field).
- `SessionPatch`: `Label`, `Pinned`, `Unread`, `Archived`, `ExpectedMarkedUnreadAt` (documented as a guard, excluded from `HasChanges`). `SessionListResult` added.
- `SessionCreateRequest`: `Fork`, `ForkFrom`; `BuildSessionCreateParameters` emits `fork`/`forkFrom` only when `Fork` is set.
- `ListArchivedSessionsAsync(timeoutMs = 15000)` sends `sessions.list` with `archived:true`; `ParseDetachedSessionRows` returns detached rows that never touch live `_sessions` tracking or raise `SessionsUpdated` (wizard-response early return already bypasses `ParseSessions`).
- `IOperatorGatewayClient`: default `ListArchivedSessionsAsync` returns `IsSupported = false` so fakes stay compiling.
- `SessionTranscriptFormatter.FormatMarkdown(history, title)`: mirrors the web `buildChatMarkdown` (`## User`/`## Assistant`/`## Tool`, ISO-8601 UTC timestamps, blank-message skip, `null` when empty).
- Drift-guard snapshot (`gateway-protocol-snapshot.json`): `sessions.list` request `archived` + response `pinned/pinnedAt/unread/markedUnreadAt/archived`; `sessions.create` `fork`/`forkFrom`; `sessions.patch` `label/pinned/unread/archived/expectedMarkedUnreadAt`; `label` removed from the upstream not-yet-implemented list.

### Tray.WinUI wiring

- `RefreshSidebar` reconciles active rows in place: `SyncSessionItems` keeps one `NavigationViewItem` per session key, refreshes a reused row through `ApplySessionItem` only when its `WorkspaceSession` changed, removes stale rows, and orders rows after `SessionsEmpty` via `WorkspaceItemsSync.Arrange`. The selected container is never destroyed during `SelectionChanged`. There is no sidebar archive header, archived row cache, or archive-list fetch.
- `CreateSessionItem(session, automationPrefix)` sets the stable AutomationId and delegates to `ApplySessionItem`: pin glyph column, unread accent dot or busy ring, item status (`Pinned`, `Working`, `Unread`; always reset so reused rows drop stale status), per-row context flyout.
- Settings `SessionsPage` refreshes its archived snapshot on Sessions changes, manual refresh, and reconnect while expanded; it clears the snapshot on disconnect and unload.
- `RenderDestination`: fires `AcknowledgeReadAsync` for unread sessions on navigation (list refresh does not re-trigger it, so "Mark as unread" on the open session persists until re-navigation).
- Session switch is asynchronous: `RenderDestination` commits history, sidebar, `_chat.QueueSession(key)` and selection synchronously, then `QueueChatApply` schedules one Low-priority `ApplyQueuedChat` (coalesced, latest queued key wins) that calls `_chat.Initialize` and `SignalContentReady`. `ChatPage` no longer resolves gateway credentials on the UI thread: the native surface skips resolution entirely; the legacy WebView surface resolves through `ApplyWebViewSurfaceAsync` on the thread pool, serialized across Hub and Workspace pages by the static `s_chatCredentialGate` (concurrent `NativeGatewayRuntime.Inspect` calls deny each other) and dropped when `_surfaceGeneration` moved on (newer apply or unload).
- Session creation split into `CreateAndSelectSessionAsync` shared by `NewSessionAsync` and `ForkSessionAsync`; `LeaveSession(key)` selects the next non-key session or returns to Home; `ShowInfo(message, severity)` added, `ShowError` now sets `InfoBarSeverity.Error` explicitly.
- Strings use all six locales (`en-us`, `fr-fr`, `nl-nl`, `pt-br`, `zh-cn`, `zh-tw`). The Settings archive strings are translated; the earlier Workspace strings retain their deferred English parity. No em dashes.

## Plan deviation (contingency applied)

The plan's fallback for `MenuFlyout.Target` was needed: `Target` was not populated for `NavigationViewItem.ContextFlyout`, so the shared-flyout `Opening` handler would have hidden the menu. Implemented `WorkspaceSessionMenuController.CreateFlyout(session)`: per-row `MenuFlyout` with the row captured at creation; population code is shared via `Populate(flyout, session)`. No behavior difference otherwise.

## Historical validation (initial implementation)

| Command | Result |
|---|---|
| `./build.ps1` | All 5 projects succeeded (Shared, Cli, WinNodeCli, SetupEngine, WinUI) |
| `dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore` | 4230 passed, 0 failed, 35 skipped |
| `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore` | 3899 passed, 2 failed |

The 2 tray failures are `NativeSpeechStackRuntimeTests.TestHost_AppLocalVCRuntime_MeetsOnnxRuntimeFloor` and `TrayBuildOutput_NativeTtsStack_LoadsWithAppLocalVCRuntime`: pre-existing environmental gap (no Visual Studio "C++ Redistributable Update" component on this machine; `OPENCLAW0001` warning fires before every test run and the failure text names the exact fix). Unrelated to this change; they fail identically on the clean tree when the component is absent.

Focused new tests: 22 shared (patch payload allowlist, guard-only patch, label clear-null, fork params, field parse/reset, detached archived rows, FormatMarkdown) + 16 tray (menu gating, archived layout, disconnect gating, session-ID omission, rename/read-ack/fork builders, projection pin ordering, latest-session independence, archived projection).

## Historical real behavior proof (initial sidebar design)

Launched `.\run-app-local.ps1 -NoBuild -Isolated` connected to the developer's live gateway (`ws://127.0.0.1:18789`; isolated log confirmed `sessions.subscribe` and model resolution). Right-click on the "main" session row opened the context menu showing:

- "Last active 2h ago" disabled header
- Pin session, Rename…, Mark as unread: enabled
- Archive session: disabled (main-session protection)
- Fork conversation, Copy > (Copy session key / Copy session ID / Copy as Markdown), Reset session…, Compact session…, Export transcript…: enabled
- Delete…: disabled, red destructive styling

The sidebar rendered the new collapsible "Archived" header below the Sessions list.

Not verified / blocked: full interactive pass of pin/rename/unread/fork/archive/delete flows and disconnected gating was not individually captured via computer-use screenshots; the pure-model tests cover the gating and payload contracts, and the menu itself is proven live above. The web Control UI cross-check of pin state was not captured (would need the web UI running against the same gateway).

### Async session switch proof

The live gateway was not available for this pass (no `ws://127.0.0.1:18789` listener, no gateway in WSL), so the switch was measured against the gateway fixture (`multi-session-browse` scenario, real app, UIA-driven sidebar clicks) with a temporary `[perf] session-switch handler=… ui-idle=…` probe, on the parent commit (before) and on this change (after). Fixture credentials resolve without the managed-loopback `wsl.exe` provenance check, so the fixture does not show the ~530ms `TryComputeChatUrl` cost; it proves ordering, coalescing and correctness.

| Mode | Build | Switches | handler median / max | ui-idle median / max |
|---|---|---|---|---|
| Native | before | 19 | 2.4 / 6.0 ms | 112.4 / 314.3 ms |
| Native | after | 19 | 1.0 / 2.2 ms | 73.3 / 280.6 ms |
| Legacy WebView | before | 16 | 1.15 / 6.1 ms | 26.1 / 382.1 ms |
| Legacy WebView | after | 16 | 1.05 / 2.5 ms | 14.75 / 405.7 ms |

- Native rapid clicks (Main, Long, Empty, Long at 150ms intervals): sidebar and rendered thread (`selectedThreadId`) both ended on `agent:main:fixture-long` and stayed there 1.5s later. Clicking a session then immediately opening Notifications showed Notifications; returning to chat rendered the clicked session.
- Legacy WebView: every switch navigated the WebView with the clicked session's `session=` query; the rapid sequence ended on `agent:main:fixture-empty`, the 4th click. No "Open Connection settings" error and no credential-resolution failure was logged.
- Turning the legacy setting off right after a click mounted the native chat on the clicked session (`agent:main:fixture-long`). The parent commit failed the same step: the native chat did not show the clicked session within 20s.

Not verified / blocked: the live managed-local gateway numbers (598ms baseline `ui-idle`) and the Fork-then-select flow were not re-measured on this machine.
