# Connection Architecture

This document describes the gateway connection system - how the tray app discovers, authenticates with, and maintains connections to OpenClaw gateways.

## Project structure

### Workspace and Settings companion

The normal foreground entry point is `WorkspaceWindow`, a native WinUI 3 shell
with Reactor chat, assistant selection, Home, sessions, and an Owner
menu. Notifications is an independent footer action immediately beside Owner.
There is no Settings item in the Workspace rail.
The rail is the same native `NavigationView` as the companion, using its existing
colourful sidebar SVG assets via direct `ImageIcon` controls. Native menu items
retain their icon-column sizing; the pane uses the expanded theme background
without a second acrylic fill. Session-row selection is retained by original
session key across list refresh, pane toggling, and companion refocus.
Home explicitly selects Home without discarding the current chat draft.
Native menu items
own selection and keyboard behavior; assistant, sessions, and footer controls
use the pane's header and footer slots. Sessions follow Home in the
same native scrolling menu with a native section header. Owner stays fixed
in the footer. Native WinUI `TitleBar` owns title/icon layout and typography.
`NavigationView.PaneHeader` puts the pane-collapse button and sidebar-right
Back/Forward in one row, with the assistant selector below. Its final dropdown
option invokes the existing New conversation workflow and restores the selected
agent rather than persisting the action as an agent. It remains disabled while
disconnected or creating a session. The native ComboBox uses transparent/subtle
chrome. Sessions retains its independent subtle Add button.
NavigationView has zero compact width. Home's icon is inline native content
so it is not clipped by WinUI's zero-width icon column. The mode stays `Left`. Home,
sessions, and the entire footer disappear, and content fills the vacated width.
The native 160ms slide goes directly to zero width; `PaneClosed` hides offscreen
controls without a second layout step. Native motion preferences still apply.
Both toggle states share a stable overlay position outside the animated pane,
so native focus restoration and intermediate layout cannot move the target.
A floating-style subtle reopen button reserves a dedicated 56 DIP row at
the content's top-left below the titlebar, never covering hosted hit targets.
The pane toggle retains a 40 DIP target and 16 DIP glyph in both states; focus
moves to the surviving toggle. Pane changes do not remount chat or reset drafts.
The companion uses a native TitleBar titled OpenClaw Settings with shared claw
artwork. Search, Back, and Forward live beside the toggle in its stable sidebar
toolbar. The native pane reserves 56 DIP above its items, including in compact mode.
Connection status occupies the footer bar above Diagnostics and Settings;
compact mode retains its icon-only target. Settings has no notification button.
Workspace's bell opens `NotificationFlyoutContent` using the existing
`AppNotificationService`, without replacing chat or changing history. Its
explicit Open notifications link and existing deep links retain the full page.
Owner's connection entry shows the same `ConnectionStatusPresenter` label/accent
as Settings, refreshed both on menu opening and every manager snapshot through
`WindowManager`, including richer changes that keep the same legacy status.
It opens `GatewayStatusContent`, shared with Settings,
using the current manager snapshot and existing reconnect/Connection actions.
The flyout contents own named-control application; windows own popup lifetime
and route side effects. Notification subscriptions detach when the flyout closes.

The assistant selector projects the gateway's existing `agents.list` response:
`identity.name` (then roster name/ID), `identity.emoji`, and `identity.avatarUrl`
(then configured `identity.avatar`). The gateway resolves workspace-local avatar
files to data URLs for native clients. Windows never reads those paths locally.
`AgentIdentityBadge` uses native `PersonPicture` and the existing bounded
`MediaResolver` for image data and public HTTPS sources; blocked/failed images
are logged and retain emoji/initials. The secondary `main`-style label is the
agent ID, not a Git branch; this RPC has no branch-name field. Selection honors
`defaultId` and `selectionRequired`, without sending a chat or creating a session
when metadata refreshes. Existing item identities/pictures survive roster refresh.
Contract reference: `openclaw/openclaw` gateway `agents-list.ts`,
`session-utils-store.ts`, and `packages/gateway-protocol/src/schema/agents-models-skills.ts`.
Search retains the existing catalog and keyboard shortcuts through a native
flyout. Both Frame history stacks prune unavailable gateway/diagnostics routes.
All footer
buttons use native `SubtleButtonStyle` state brushes, and Owner uses the native
`PersonPicture` avatar rather than a font glyph.

`WindowManager` owns Workspace and `HubWindow` independently. The latter is the
Settings companion and reuses the existing native Settings pages, excluding
Chat. Owner's Settings, Usage, Pair device, and About links open or focus that
companion at `settings`, `usage`, `channels`, and `about` respectively. Closing
the companion does not close Workspace or discard an unsent chat draft.
Generic Workspace refocus preserves the current page; explicit page/session
links still navigate. `agent:<id>:workspace` retains its original agent-files
meaning and is not the main Workspace route.

Verified native setup completion follows the same window boundary. Chat binds
the receipt's exact session to Workspace's retained ChatPage and waits for the
native composer before activating the window and consuming the receipt.
Channels and Skills keep typed companion navigation. Both paths retain fresh
gateway, endpoint, identity, agent, and session checks; selecting a different
Workspace destination invalidates an in-flight chat handoff.
Admitted assistant changes and new-conversation creation invalidate that binding
before any asynchronous session creation, even while the old conversation remains
visible. Same-session rebinding must use the new request identity. Cancellation
is checked on already-ready paths and immediately before receipt consumption;
a canceled launch retains its receipt and restart recovery for explicit retry.

`WorkspaceNavigation` owns only Home and the footer Notifications destination.
The Home/Sessions-only user correction supersedes the expanded prototype.
`WorkspaceNavigationHistory` owns Back/Forward history and clears forward
entries only on a different destination. All 20 deprecated `workspace:` links
(including agents, dashboards, systems, automations, plugins, detail pages,
sessions, and more) explicitly return Home without creating obsolete history
entries. Unknown Workspace routes are not accepted as compatibility aliases.
Both native window entry points use `WorkspaceNavigation.Dispatch` to reject
unknown prefixed routes before creating a companion or forwarding navigation.
`app.navigate` reports this rejection as an MCP tool error, not a successful
no-op.
Unprefixed companion routes, including `cron`, `sessions`, `skills`, `usage`,
and `agent:<id>:workspace`, are unchanged.

The removed `WorkspaceContentPage` and `WorkspacePageRenderer` no longer host
placeholder management pages. Native Cron keeps its existing companion
list/editor and gateway submissions, without a Workspace-specific layout.
`WorkspaceProjection` still supplies real assistant and conversation identities.
Its sidebar and latest assistant conversation use
`SessionDisplayResolver.IsBackground`: explicit `isBackground` wins, otherwise
gateway classification and legacy session keys determine background status.
Home uses the existing Reactor chat and session creation/history; disconnected
state directs users to Connection. Notifications uses the existing notification
service. Owner Settings provides access to the full companion catalog, while
Owner Usage and Pair device keep their deep links. Owner Get apps opens the
existing platforms documentation directly, without a placeholder Apps page.
No underlying management
APIs, companion pages, or capabilities were removed.

The connection event timeline remains an independent `ConnectionStatusWindow`.
Its initial position is aligned to the right of the active main window's monitor
work area; subsequent activation reuses it without resetting a user's position.
It reads the same connection manager diagnostics, not a parallel client.

An argument-free user launch opens only setup when no gateway is configured
(unless local MCP mode is enabled). After gateway configuration it opens only
Workspace, even when disconnected or node pairing is still pending. Repeat
launches and Workspace tray/deep-link activation refocus required setup instead
of opening Workspace alongside it. Node pairing and credential checks still
gate actual connections; this is only foreground-window selection.
New autostart registrations pass
`--background` to remain quiet. The installer migrates only exact, argument-free
Run/task entries for its own executable and preserves customized entries and
task enablement. A portable/manual binary replacement bypasses that migration;
re-enable **Start with Windows** once in Settings to refresh a legacy entry.
Explicit protocol and post-setup restart behavior is unchanged.
Packaged Windows StartupTask activations retain their activation kind through
initial launch and secondary-instance forwarding. Unlike a real interactive
argument-free launch, StartupTask does not implicitly open Workspace. Existing
first-run setup and restart guards remain in effect.

Chat response notifications are suppressed only while native Workspace is
visible, not minimized, and showing Home/chat, or the legacy compact chat is
visible. An existing Settings companion, a hidden/minimized Workspace, or the
Notifications destination is not evidence that chat is visible. The chat and
per-type notification settings still apply.

New Workspace resource keys exist in every supported locale. Non-English copy
is explicitly deferred English pending translation; resource-key parity is
still enforced. Native Light/Dark disconnected proof and synthetic composer
regressions are distinct from connected gateway proof. In-process captures do
not include the native window frame or Mica; system high contrast requires its
own approved host/session and must not be inferred from Dark mode.

Connection management lives in three layers:

```
OpenClaw.Shared (net10.0)           - WebSocket transport, gateway protocol, device identity
    ↑
OpenClaw.Connection (net10.0)       - connection lifecycle, registry, credentials, state machine
    ↑
OpenClaw.Tray.WinUI (net10.0-windows) - UI app, tray icon, pages, windows
```

**OpenClaw.Shared** owns the low-level gateway clients (`OpenClawGatewayClient`, `WindowsNodeClient`, `WebSocketClientBase`), device identity/signing (`DeviceIdentity`), protocol models, and the `IOperatorGatewayClient` interface.

`WindowsNodeClient` also owns gateway invocation lifetime at the transport
boundary. Active invokes are registered by invoke ID in a focused cancellation
registry, linked to the node connection lifetime, and cancelled individually by
the gateway `node.invoke.cancel` event. Active invocations atomically transition
to cancelled or completed when capability execution returns; whichever
transition wins determines the protocol outcome. Capability implementations
remain responsible for cooperative cancellation of their own underlying work.

**OpenClaw.Connection** owns all connection management. `GatewayConnectionManager`
is the public lifecycle façade and sole writer of the overall state machine,
operator lifecycle, active gateway context, tunnel, and operator reconnect
orchestration. Three narrower owners sit behind it:

- `NodeConnectionCoordinator` owns node generation/CTS/start ordering, connector
  event handling, node token-mismatch recovery, and node connection telemetry.
- `BootstrapTokenLifecycle` owns bootstrap/shared/device handoff timing, the
  durable two-role token clear gate, and operator token-mismatch recovery timing.
- `DevicePairApprovalCoordinator` owns typed device role-upgrade approval and its
  one-in-flight plus one-queued bounded node reconnect workflow.

`GatewayRegistry`, `CredentialResolver`, `ConnectionStateMachine`,
`NodeConnector`, `SshTunnelService/Manager`, `SetupCodeDecoder`, and connection
interfaces/DTOs/enums remain separate. This project has zero WinUI dependencies
and is independently testable.

Native Check/Next keeps device-token precedence. Only a typed
`AUTH_DEVICE_TOKEN_MISMATCH` may trigger one recovery in the disposable
`GatewayValidationIdentity`: recheck trusted transport and owned-listener
provenance, clear its rejected operator token, then resolve shared before
bootstrap. The saved identity, keypair and original compare-and-swap baseline
are not changed by Check. Successful bootstrap authentication retains its
replacement operator token in memory for Next instead of replaying bootstrap.
Wrong shared tokens, plain remote WebSocket endpoints, ambiguous listeners and
repeated mismatch cannot cause additional credential fallback.
Native automatic recovery also rejects unowned manual-loopback listeners.
Explicit manual-loopback connection is unchanged; the existing non-native
recovery owner retains its prior admission policy through the shared policy's
explicit legacy allowance. Disposable identity copies use the product's
sensitive-file ACL writer, not inherited copy permissions.

Successful native-editor commit results carry the committed Gateway ID and
`GatewayDashboardBinding`, captured from the transaction candidate rather than a
later active selection. `SetupAccessDraft` retains both through capability
selection. AI admission checks them before creating an operator client or
borrowing a native manager client; changing the active Gateway, endpoint,
runtime contract or SSH realm cannot silently redirect onboarding.

During an expected model-activation restart, setup validates its captured
registry/endpoint binding and persisted signing identity without requiring live
handshake fields that disconnect deliberately clears. The bounded wait must
finish on a fresh matching authenticated route. Normal setup requests and
verification still require connected admin scope and the full live route, with
their existing post-await checks. No activation is replayed to recover downtime.

Canceling a Check, or a Next with confirmed rollback, retains the same draft's
staged keypair and authenticated replacement token. Draft edits and close discard
it; completed or uncertain commits also discard it. No ambiguous transaction
result can be reused as a validated draft.

Setup's persisted-registry snapshot comparison ignores only `LastConnected`,
which can differ briefly between a connection's Update and Save. The snapshot
retains canonical in-memory records; active gateway, credentials, endpoint and
other configuration differences still reject admission.
Reconciliation additionally requires the operation-produced expected output
snapshot. The pipeline checks its prior expected state before reading/writing
registry changes and carries its own resulting snapshot to the UI; it does not
derive authority by rereading disk immediately before adoption.

Pipeline settlement runs after execution and rollback on success, failure,
cancellation, and window close. It adopts only the operation's known final
registry output against its admitted baseline, refreshes the persistence
baseline, and publishes changes outside locks. Stale live connections are
disconnected conditionally against their captured connection snapshot; a newer
connection is not canceled. External conflicts preserve live edits and provide
an explicit reopen/reload recovery message instead of weakening save CAS.

If settlement itself fails, `SetupPipelineSettlementException` retains the
original `PipelineResult` or thrown exception alongside the settlement error.
This includes cancellation, failed-step identity, compatibility details and
restart requirements. Progress reports both outcomes and remains failed; a
reconciliation error cannot replace the original diagnostics or become success.

Direct-connect commit and rollback use admitted registry snapshots. If rollback
loses a CAS race, it observes the actual persisted active selection and
reconciles only that selection, never reasserting the stale candidate. Unreadable
state remains unknown with attention required, no guessed settings or old
connection restore. Identity preparation failures before transaction admission
remain retryable and cannot be reported as committed.

After a failed initial commit, cleanup removes a newly copied candidate identity
only when no live or persisted record adopted its ID and its sole key file still
matches this operation's copy. The registry lease spans that final absence check
and removal. Copy publication returns an exact-content creation transaction:
the absence check, baseline calculation and write share the existing identity
mutex. Cleanup acquires that same identity mutex inside the registry lease for
comparison and deletion. No unlocked post-copy read can adopt a newer writer's
bytes as the cleanup baseline. Existing identities, changed files, reparse paths
and unknown persisted state are preserved.

`PersistenceFileLease` serializes cooperating settings/registry writers by
normalized path across instances and local processes. Registry Load/Save,
UpdateAndSave, setup's expected-output Save and reconciliation share that lease.
The final persisted-snapshot comparison and atomic replacement happen within
one lease. A stale writer must reload after a conflict; it cannot overwrite a
new endpoint, credential, active selection or record addition. LastConnected
alone merges monotonically for unchanged authorities.

Hosted setup applies only its owned fields through `ISettingsStore`, including
the background pipeline settings save. It rejects conflicting same-field edits
while preserving unrelated `app.settings.set` changes. Standalone setup uses the
same path lease for read/merge/replace. `SettingsManager` additionally checks its
last loaded/saved JSON before replacing the file, rolls back failed store edits,
and publishes notifications after releasing the file lease.

A stale `SettingsManager` raises a typed persistence conflict for both the
best-effort `Save()` path and throwing store updates. It does not adopt the new
disk baseline or overwrite another writer. `SettingsPersistenceNotification`
projects that state through the UI dispatcher into one persistent error notice.
The notice directs the user to exit OpenClaw from the tray menu, reopen it, and
retry their changes. Repeated failures do not stack notifications or emit
`Saved`; `ISettingsStore.Update` still rolls back and throws. Explicit `Load()`
refreshes both data and baseline and clears the conflict notice, without
automatically restarting the app or replaying edits.

**OpenClaw.Tray.WinUI** consumes the connection layer through interfaces. It never creates gateway clients directly - `GatewayConnectionManager` owns that entirely.

## Consumer API

The tray app interacts with three main objects:

### `IGatewayConnectionManager` - connection lifecycle

```csharp
// Lifecycle
ConnectAsync(gatewayId?)          // connect to active or specified gateway
DisconnectAsync()                 // tear down all connections
ReconnectAsync()                  // disconnect + connect
SwitchGatewayAsync(gatewayId)     // switch to different gateway (stops tunnel, resets state)
ApplySetupCodeAsync(setupCode)    // decode QR/setup code → register → connect

// State
CurrentSnapshot                   // immutable GatewayConnectionSnapshot
OperatorClient                    // IOperatorGatewayClient for sending gateway requests
ActiveGatewayUrl                  // which gateway we're connected to
Diagnostics                       // ring buffer of connection events

// Events
StateChanged                      // snapshot updated → UI refreshes tray icon, status
OperatorClientChanged             // client swapped → rewire data event handlers
DiagnosticEvent                   // timeline entry for Connection Status window
```

### `GatewayRegistry` - gateway catalog

```csharp
GetAll() / GetById(id) / GetActive()   // read configured gateways
AddOrUpdate(record)                     // create or update a gateway record
SetActive(id)                           // switch which gateway is active
FindByUrl(url)                          // lookup by URL (deduplication)
Save() / Load()                         // persist to gateways.json
GetIdentityDirectory(id)                // per-gateway identity directory path
MigrateFromSettings(...)                // one-time legacy migration
```

### `IOperatorGatewayClient` - gateway API (via `OperatorClientChanged`)

The operator client is received through the `OperatorClientChanged` event. The app subscribes to data events (sessions, nodes, usage, config, pairing, models, agents, etc.) and calls request methods for chat, node invocations, and configuration.

### Chat timeline event routing

Inbound chat and agent timeline events must include the gateway's canonical `sessionKey`. The tray client must not synthesize a literal `main` key for keyless inbound events, because that can merge unrelated events into the wrong timeline. When a keyless chat or agent event arrives, the tray drops it and raises a one-shot diagnostic so the protocol issue is visible without exposing the dropped message contents.

## Startup wiring (App.xaml.cs)

```
1. Create GatewayRegistry(SettingsManager.SettingsDirectoryPath)
2. Load gateway registry from gateways.json
3. Create CredentialResolver(DeviceIdentityFileReader.Instance)
4. Create GatewayClientFactory()
5. Create ConnectionDiagnostics()
6. Create NodeConnector(logger, diagnostics)
7. Wire NodeConnector.ClientCreated → NodeService.AttachClient
8. Create SshTunnelService(logger)
9. Create GatewayConnectionManager(resolver, factory, registry, logger,
                                    identityStore, nodeConnector, node mode flag,
                                    diagnostics, tunnelService)
10. Subscribe to OperatorClientChanged → wire/unwire 25+ data event handlers
11. Subscribe to StateChanged → update tray icon + hub window
12. Ensure NodeService exists before gateway initialization
13. Call InitializeGatewayClient() → connects to active gateway
```

Settings changes are classified by `SettingsChangeClassifier.Classify()` which compares `ConnectionSettingsSnapshot` before/after to determine the minimum reconnect action:

| Impact | Action |
|--------|--------|
| `NoOp` | Nothing |
| `UiOnly` | Nothing (UI preferences only) |
| `CapabilityReload` | Reload node capabilities |
| `NodeReconnectRequired` | Reconnect node only |
| `OperatorReconnectRequired` | Reconnect operator (SSH tunnel changed) |
| `FullReconnectRequired` | Full tear down and reconnect (gateway URL changed) |

## Connection state machine

`ConnectionStateMachine` (internal) drives state transitions for both operator and node roles:

```
Idle → Connecting → Connected
                  → PairingRequired → (approved) → Connected
                  → Error → (reconnect) → Connecting
                  → RateLimited
```

`OverallConnectionState` is derived from both roles:

| Operator | Node | Overall |
|----------|------|---------|
| Error | * | Error |
| PairingRequired | * | PairingRequired |
| Connected | Connected | Ready |
| Connected | Error/Rejected | Degraded |
| Connected | PairingRequired | PairingRequired |
| Connected | Connecting | Connecting |
| Connected | Idle while Node mode is intended | Degraded |
| Connected | Disabled/Off | Ready |

`GatewayConnectionSnapshot.NodeConnectionIntended` records the Node mode intent used by the manager's state machine. If Node mode is enabled but node startup is skipped, blocked, or missing a node credential, the manager publishes a blocked node snapshot (`NodeState=Error`, `NodeError=...`) instead of leaving the node idle and letting tray surfaces report a healthy connection.

### Status projection and legacy ledger

`GatewayConnectionManager.CurrentSnapshot` is the lifecycle truth. Tray/UI state
must treat `AppState.Status` / `ConnectionStatus` as a derived compatibility
projection only, produced from the manager snapshot by
`ConnectionStatusPresenter`. New connection diagnostics should read
`GatewayConnectionSnapshot`, `GatewayRegistry`, and `ConnectionDiagnostics`
directly instead of writing a second runtime model.

Current derived compatibility debt:

| Surface | Status | Notes |
|---|---|---|
| `AppState.Status` | Derived read-side adapter | The only writer is the manager `StateChanged` handler, which maps the snapshot through `ConnectionStatusPresenter` for older UI consumers. |
| `ConnectionStatus` enum | Retained | Still used by shared gateway/client and tray read-side surfaces. Do not remove it until protocol/client and UI consumers are separated in a smaller migration. |
| Command Center / tray projections | Mixed | New diagnostics use snapshot-derived DTOs. Some older warnings still read `AppStateSnapshot.Status`; those reads are compatibility gates, not lifecycle ownership. |

The local MCP `app.connection.status` command is the agent-facing projection of
this model. It reports effective mode/state, active gateway metadata,
operator/node credential resolution, MCP runtime state, browser-proxy caveats,
pending approval actions, retry hints from diagnostics, and recent diagnostic
events without exposing token values.

## Gateway registry and persistence

`GatewayRegistry` is the source of truth for configured gateways:

```
%APPDATA%\OpenClawTray\gateways.json           - gateway records
%APPDATA%\OpenClawTray\gateways\<id>\          - per-gateway identity directory
%APPDATA%\OpenClawTray\gateways\<id>\device-key-ed25519.json  - keypair + tokens
```

Each `GatewayRecord` contains: `Id`, `Url`, `FriendlyName`, `SharedGatewayToken`, `BootstrapToken`, `LastConnected`, `SshTunnel` config, `IsLocal`, `RequiresV2Signature`, `SetupManagedDistroName`, and `BrowserControlPort`. The `IdentityDirName` property is computed from `Id`.

Many gateway records may be saved, but only `ActiveId` in `gateways.json` is the effective gateway. Active gateway changes must be made through `GatewayRegistry.SetActive(...)` and saved immediately by connection flows that switch or apply credentials. `SetActive(...)` raises `GatewayRegistry.Changed`, so UI and diagnostics can observe a gateway switch even before the new connection finishes. Each active gateway resolves identity from `%APPDATA%\OpenClawTray\gateways\<id>\`; old gateway events are ignored by `GatewayConnectionManager` generation + gateway-id guards after a switch.

`SettingsManager` still owns general tray settings (node mode, MCP mode, SSH tunnel toggles, notifications, UI preferences). It may read legacy `Token` / `BootstrapToken` JSON fields into memory for migration, but save must not write those legacy credential fields back.

`GatewayDirectConnectService` is the single transaction owner for direct-connect UI surfaces. It commits the registry and active id, applies identity changes, persists `SettingsManager`, waits for a terminal manager state, and rolls back ordinary asynchronous connection failures as well as thrown failures. When the operation replaced a live operator connection, successful rollback reconnects that previous gateway before returning the failure. The Connection page and Connection Status window only validate controls and render the result. MCP shared-token replacement keeps its device-token-preserving validation semantics, then asks this service to synchronize the committed active gateway into settings and the runtime tunnel.

## Credential precedence

### Native onboarding connection boundary

`SetupNativeConnectionPage` retains an immutable `SetupNativeConnectionRequest`.
Its `ISetupNativeConnectionHost` is composed by `WindowManager`, using the same
`GatewayDirectConnectService` instance as the existing Connection surfaces.
It does not create a second connection manager or write settings itself.

**Check connection** uses `GatewayConnectionValidator` with a disposable identity
copy, no handshake-token persistence and no reconnect. SSH checks use a separate
owned tunnel on an isolated port, with generation checks again at authentication.
Exact bootstrap-scope and signature compatibility fallbacks use at most two
additional fresh clients with endpoint authorization repeated, not unrestricted
transport reconnect.
Managed-local strong credentials still require the connection manager's
provenance authorization. Setup-code addresses must match an explicitly edited
address; bootstrap tokens never become shared tokens.

The setup host retains that temporary key for the same immutable draft across
Check, approval retry and Next. Successful handshake credentials are retained
only in setup-owned memory, so a consumed bootstrap code is upgraded to explicit
device-token authentication for revalidation. They are not written into the
temporary identity file. A draft change or editor close discards the temporary
key and in-memory credentials; a successful Next writes them only into the
transaction's candidate identity. Temporary key files do exist during editing,
but the previous saved identity remains untouched.

**Next** always repeats validation against the current draft before mutating
saved or active state. For the same credential realm, it retains the logical
gateway ID, Local AI ownership and all ID-keyed state. The validated identity is
promoted using the canonical identity lock and atomic writer, with a snapshot and
compare-and-swap rollback. Paired device credentials retain precedence over shared
and bootstrap tokens. A newer identity writer is preserved and reported as an
incomplete rollback, not silently overwritten.

A changed address or SSH endpoint never inherits the old credentials. Native
onboarding adds that realm as a separate gateway and retains the prior saved
gateway and identity. The existing Direct editor's replacement semantics are
unchanged. Failed or cancelled connection attempts restore the old record,
settings and live connection, and discard the candidate identity when newly
created. Rollback errors remain failures even if a candidate remains
committed. Incomplete rollback or cleanup also reaches a persistent host
notification and Connection settings, even if the editor has already closed.
Operator pairing-pending is not AI-ready.

When the editor has no saved gateway ID, native Check, identity staging and Next
share one lookup by logical URL and SSH credential endpoint before the ordinary
URL fallback. This reuses the correct saved identity when several SSH gateways
have the same public or loopback URL. Direct editing keeps its existing lookup.

Next is the explicit commit boundary. After it succeeds, later setup cancellation
may retain the chosen gateway. Setup config receives only the effective endpoint;
the existing `SetupGatewaySession` reads credentials from the active registry.
No browser-profile credentials are accepted by this port, and it does not change
Node mode, local MCP, capability settings, or install WSL.

Credential resolution order is intentionally strict:

1. **Stored device token** in the per-gateway identity directory.
2. **`GatewayRecord.SharedGatewayToken`** - shared token for HTTP/chat surfaces.
3. **`GatewayRecord.BootstrapToken`** - one-time setup, limited scopes.
4. **No credential** - caller logs and skips client init.

The invariant is that a paired device token always wins. Do not downgrade a paired operator or node to a shared/bootstrap token, because that can reduce scopes or trigger unnecessary re-pairing.

**`CredentialResolver`** implements the precedence for WebSocket connections (operator and node roles). It also returns a detailed `GatewayCredentialResolution` so the active snapshot and diagnostics can distinguish `Resolved`, `Missing`, `Unreadable`, `Corrupt`, `FallbackUsed`, and `BootstrapRequired`. Shared-token-only gateways are a clean resolved state when no paired device token exists. If a stored per-gateway device token is unreadable or corrupt and the resolver falls back to a shared/bootstrap token, `GatewayConnectionSnapshot` preserves that fallback status instead of reporting only the token source.

Unreadable/corrupt identity fallback is a credential-resolution diagnostic, not permission to replace an existing keypair. A readable stored device token still always wins. When the per-gateway identity file cannot be read or parsed, resolution may identify a same-gateway shared/bootstrap credential, but gateway client construction fails closed until the persisted identity is readable or the user explicitly resets pairing. OpenClaw never regenerates, overwrites, or otherwise changes the identity path on a load failure. The snapshot and diagnostics report the persisted-identity error so UI and diagnostics can prompt repair or explicit re-pair. Credential reads never fall back to another gateway's identity directory.

Node credential precedence follows the same invariant with a distinct stored token:

1. **Stored node device token** in the per-gateway identity directory.
2. **`GatewayRecord.SharedGatewayToken`** - shared token fallback when no paired node token exists.
3. **`GatewayRecord.BootstrapToken`** - one-time setup, limited scopes.
4. **No credential** - caller logs and skips node client init.

**`InteractiveGatewayCredentialResolver`** resolves credentials for HTTP surfaces (chat URL `?token=` auth). It **prefers SharedGatewayToken** over DeviceToken because HTTP endpoints expect the shared token, not the per-device WebSocket token. Browser proxy diagnostics should treat the missing shared token as a browser-control caveat, not as proof that the operator or node gateway connection is disconnected.

The legacy web-chat readiness probe bypasses the process proxy only for loopback
URLs; remote HTTPS gateways retain proxy support. It accepts the original
200-399 response without following redirects, so a readiness check never
contacts a `Location` destination. Browser navigation remains a separate step.

## Self-recovery and automatic local-gateway repair

Two orthogonal self-healing behaviors keep the connection reliable without dead-ending the user:

### Stale device-token self-recovery (operator + node)

The gateway may reject a stored device token with the structured code `AUTH_DEVICE_TOKEN_MISMATCH` (a rotated/revoked/replaced device token) - distinct from a wrong *shared* token. `GatewayErrorClassifier` is the single classifier for this: `ClassifyWithCode(message, ...codes)` inspects the structured `error.code`/`error.details.code` **before** the textual heuristic and returns the exact `GatewayErrorKind.DeviceTokenMismatch`, keeping a stale *device* token (auto-recoverable) separate from a wrong *shared* token (`Auth`, not device-recoverable). Broad `GatewayErrorKind.TokenDrift` remains a manual re-pair signal for UI copy.

On a device-token mismatch, the applicable credential owner clears **only the
rejected role's** device token and reconnects, letting `CredentialResolver` fall
back to the same record's `SharedGatewayToken` (preferred) or
`BootstrapToken`. `BootstrapTokenLifecycle` owns operator recovery;
`NodeConnectionCoordinator` owns node recovery from the connector's classified
`INodeConnectorTelemetryEvents.ConnectionFailure(GatewayErrorKind)`. The
manager subscribes once and forwards typed connector events. The node owner
captures lifecycle plus node generation and is the only implementation of the
combined current-node-attempt check. A per-gateway, per-role attempt guard
(reset on handshake success / node pairing) prevents
clear→reconnect→mismatch loops.

**Security - trust gate and endpoint provenance.** Clearing a device token downgrades to the more powerful shared/bootstrap credential, so `IsRecoverySafeEndpoint` restricts recovery to trusted endpoints: an owned SSH tunnel, a validated TLS (`wss`/`https`) endpoint, or a setup-managed WSL loopback gateway proven by `ManagedLocalGatewayPortProvenanceService`. The managed-local proof accepts either the existing verified Windows WSL relay identity or a relayless mirrored-networking endpoint with a complete empty Windows listener snapshot, positive expected-distro systemd MainPID ownership, and an immediate second complete empty snapshot. Strong credentials repeat that relayless proof immediately before use. Loopback is not treated as identity by itself: incomplete capture or any unknown, conflicting, or changed Windows listener blocks fallback, so a wrong local process cannot return a device-token mismatch to induce disclosure of the shared credential. A plain `ws://` remote endpoint is never eligible.

### Automatic managed-local WSL gateway repair (tray)

For an app-owned setup-managed local WSL gateway (`WslKeepAlivePolicy.IsSetupManagedLocalRecord` - never SSH/remote/ambiguous-localhost), the tray owns process supervision, keeping it out of the connection layer. `ManagedLocalGatewayAutoRepairMonitor` watches the operator connection and, when it is positively transport-unreachable (`GatewayErrorKind.Network`/`Server`, plus a cold-start `Connecting` state with no failure yet; never unknown/auth/pairing/rate-limit/scope/TLS/tunnel/token-drift) for a sustained window, invokes `ManagedLocalGatewayRepairCoordinator`. A typed `LocalPortConflict` is also repairable because its remediation is provenance-gated rather than a blind process restart. The monitor honors a **startup grace** (so a slow WSL cold start is not interrupted), a per-gateway unhealthy threshold and cooldown, a manager-owned explicit disconnect/stop intent, and a settings **kill switch** (`SettingsData.EnableManagedLocalGatewayAutoRepair`, default on).

**Default-on product contract and macOS parity.** App-installed local gateways are supervised by default for both fresh setups and upgrades, matching the macOS local-mode contract where launchd supervision is active unless OpenClaw is paused. Fresh Windows setup writes `EnableManagedLocalGatewayAutoRepair=true` explicitly; an existing settings file that predates the field deserializes to the same default. This enrollment is restricted to records whose setup-managed ownership is positively linked to the installed endpoint. Manual localhost, repointed, SSH, and remote records are never adopted. The user-facing controls are **Disconnect** and **Stop** on the Connection page: either records explicit operator intent and suppresses automatic restart, process remediation, and reconnect until the operator explicitly connects/starts again. An explicitly persisted `false` remains available as a policy/debug kill switch and is never overwritten by setup merge.

`ManagedLocalGatewayRepairCoordinator` **probes before it restarts**: if the gateway is already reachable it just reconnects (the macOS "attach" path); only a genuinely-down gateway triggers a WSL distro restart (via `WslGatewayController`), a keepalive re-arm (`WslGatewayKeepAliveService.TryEnsureAsync`), and a reconnect. For the native-vs-WSL collision case, `ManagedLocalGatewayPortProvenanceService` classifies listeners by address and proves process command line plus scheduled-task/profile lineage. Relayless mirrored networking is accepted only when complete Windows captures remain empty around positive expected-distro systemd MainPID proof. It automatically disables/stops only a fully proven obsolete native OpenClaw gateway; an unknown, incomplete, or conflicting listener is never trusted or killed and produces precise `LocalPortConflict` diagnostics. The shared lifecycle lease serializes that destructive work with manual WSL actions. Reconnect is **gateway-pinned, intent-aware, and cancellable** (`GatewayConnectionManager.ReconnectIfCurrentAsync(gatewayId, ct)`), so gateway switches, explicit Disconnect/Stop, and shutdown always win. Repair is single-flight, verifies success by a real operator connection to the same gateway, is per-gateway restart-budget-bounded, and never reads or logs credentials.

## Client instance lifecycle

**Operator client** (`OpenClawGatewayClient`): Single instance at a time, owned by `GatewayConnectionManager`. Created via `GatewayClientFactory.Create()`. Old instance disposed before creating new one. `OperatorClientChanged` event notifies consumers of swaps.

**Node client** (`WindowsNodeClient`): Two mutually exclusive creation paths:
- **Normal**: `NodeConnector` creates it → fires `ClientCreated` → `NodeService.AttachClient()` receives it (no new client created)
- **Local setup**: `NodeService.ConnectAsync()` creates its own client (used only during WSL local gateway setup)

Both paths dispose old clients before creating new ones.

## Setup-code and pairing flow

Setup codes (from QR scan or paste) decode to `{ url, bootstrapToken }` via `SetupCodeDecoder`. The flow:

1. `ApplySetupCodeAsync(code)` decodes and validates the gateway URL and bootstrap token
2. Creates/updates and persists the active `GatewayRecord`, preserving any shared token and durable per-role device tokens
3. Disconnects the previous connection only after the record is durable
4. `BootstrapTokenLifecycle` forces `auth.bootstrapToken` for this connection attempt without clearing stored device tokens; the record-scoped force flag is consumed or cleared even when identity loading fails
5. After successful pairing, the gateway returns `hello-ok.auth.deviceToken` and `BootstrapTokenLifecycle` persists the replacement role token
6. If pairing or connection fails, the previously stored device tokens remain intact, so retrying or returning to the prior pairing does not require an unintended full re-pair

The bootstrap token is cleared only after operator and node role tokens are both durably readable.

**Approval boundaries**: `GatewayConnectionManager` leaves node-pair command-trust requests and reapproval pending for explicit operator approval. It may automatically approve and reconnect only an explicitly typed device-pair request used for a device role upgrade.

In-chat exec approval cards sanitize the gateway's command and message before rendering. If either cannot be reviewed in full because it is truncated, suppressed, or conceals command syntax, the card offers only Deny; the chat provider also rejects Allow RPCs that are not permitted by the matching pending card.

## Inbound pairing approval (operator)

When **another** device or node requests pairing, the gateway broadcasts `device.pair.requested` / `node.pair.requested` to operators with pairing scope. `OpenClawGatewayClient` refreshes the pending lists and raises `DevicePairListUpdated` / `NodePairListUpdated`, which `GatewayService` forwards via its `PairListsChanged` event.

`PairingApprovalCoordinator` (tray) reconciles those snapshots through the pure `PairingApprovalQueue` (OpenClaw.Connection) into add/resolve deltas, de-duplicating, suppressing already-decided requests, and filtering out the local node's own pending request (handled by the auto-approve path above). For genuinely new requests - when `ShowPairingApprovalDialog` is enabled and the operator holds pairing scope - it raises `ApprovalRequested`, and the app presents a focused **`PairingApprovalDialog`** plus an awareness toast (with a "Review" action). The dialog shows the requester's identity and the **operator scopes being granted** (mapped to friendly text by `PairingScopeDescriptions`), with Approve / Reject / Decide-later. Approve is briefly disabled on each new request to prevent click-through. Approve/Reject call the `IOperatorGatewayClient.{Device,Node}Pair{Approve,Reject}Async` RPCs; the queue advances and the dialog closes when empty. The existing Connections-page "Pending approvals" banner remains as the passive fallback when the dialog is disabled. Pure queue/scope logic is unit-tested in `OpenClaw.Connection.Tests`.

## SSH tunnel integration

`SshTunnelService` manages an SSH local port-forward process and implements `ISshTunnelManager` directly for the connection manager.

When a `GatewayRecord` has `SshTunnel` config, the connection manager starts the tunnel before connecting the WebSocket client to `ws://localhost:<localPort>`. The config stores the SSH daemon port (`sshPort`, default `22`) separately from the remote gateway port forwarded by `-L`. Startup allows up to 20 seconds for SSH transport, key exchange, authentication, and local-forward binding, while every sample still fails closed on incomplete listener capture or a conflicting loopback/wildcard owner. Same-number listeners bound only to non-loopback interfaces are irrelevant to the local forward and are ignored.

Credential handoff pins the verified tunnel lifecycle generation (or managed-local process identity) from preflight through the initial challenge authorization. If ownership changes between WebSocket acceptance and the credential frame, the current socket is aborted and only a fresh, reauthorized socket may retry.

`SshTunnelSnapshot` provides a read-only point-in-time view of tunnel state for UI consumption (avoids coupling UI to the mutable service).

## MCP-only mode

`EnableMcpServer` and `EnableNodeMode` are independent:

| EnableNodeMode | EnableMcpServer | Behavior |
|---|---|---|
| false | false | Operator-only tray app |
| false | true | Local MCP server only; no gateway required |
| true | false | Gateway node only |
| true | true | Gateway node plus local MCP server |

The `EnableMcpServer=true`, `EnableNodeMode=false` path creates a local-only `NodeService` without requiring a gateway credential.

## Tray action UX

Tray actions should never silently no-op on common pairing/configuration issues:

- Chat resolves credentials from the active registry record and per-gateway identity. If no usable credential exists, it opens Connection settings instead.
- Canvas opens only when the Windows node is initialized, paired, and the Canvas capability is enabled in settings; otherwise it opens Connection settings.
- Quick Send uses the live operator client and surfaces scope/pairing errors from gateway calls.
- `system.run` and `system.run.prepare` are gated by `NodeSystemRunEnabled` (default `true` for backward compatibility). When disabled, those commands are dropped from advertised capabilities and invocations are rejected.

## Legacy migration

On first startup with a `GatewayRegistry`, if no active gateway record exists, the app migrates legacy settings credentials:

- `LegacyToken` → `GatewayRecord.SharedGatewayToken`
- `LegacyBootstrapToken` → `GatewayRecord.BootstrapToken`
- Old identity file copied into per-gateway identity directory

Migration is idempotent and deduplicates by URL.

## Signature protocol

The connect handshake uses Ed25519 signatures with v3→v2 fallback:
- Client tries v3 signature first (includes platform and device family)
- If gateway rejects v3, falls back to v2 and remembers for the session
- The `_gatewayNeedsV2Signature` flag persists across reconnects within the same `GatewayConnectionManager` lifetime

## Tests

Connection tests live in `tests/OpenClaw.Connection.Tests/`:

- `ConnectionStateMachineTests` - FSM transitions, derived overall state
- `CredentialResolverTests` - credential precedence for operator and node
- `GatewayConnectionManagerTests` - connect/disconnect/switch, diagnostics, handshake
- `GatewayRegistryTests` / `GatewayRegistryMigrationTests` - persistence, migration
- `InteractiveGatewayCredentialResolverTests` - HTTP credential resolution
- `NodeConnectorTests` - node client lifecycle
- `PairingFlowTests` / `NodePairAutoApproveTests` - pairing lifecycle, device role-upgrade auto-approval, and manual node command-trust boundary
- `SetupCodeFlowTests` / `SetupCodeDecoderTests` - QR code → connect flow
- `StaleEventGuardTests` - generation-guarded event handling
- `SettingsChangeImpactTests` - settings change classification
- `RetryPolicyTests` - backoff policy
- `ConnectionDiagnosticsTests` - ring buffer diagnostics
- `NodeConnectionCoordinatorTests` - singular node generation and telemetry contract
- `BootstrapTokenLifecycleTests` - durable role-token handoff and clear gate
- `DevicePairApprovalCoordinatorTests` - bounded device role-upgrade reconnect workflow

The heaviest remaining gap is Windows shell UI behavior (tray clicks, tooltip visibility, WinUI menu routing). Cover pure decision logic in unit tests; use manual or integration smoke tests for shell behavior.
