# Connection Architecture

This document describes the gateway connection system — how the tray app discovers, authenticates with, and maintains connections to OpenClaw gateways.

## Project structure

Connection management lives in three layers:

```
OpenClaw.Shared (net10.0)           — WebSocket transport, gateway protocol, device identity
    ↑
OpenClaw.Connection (net10.0)       — connection lifecycle, registry, credentials, state machine
    ↑
OpenClaw.Tray.WinUI (net10.0-windows) — UI app, tray icon, pages, windows
```

**OpenClaw.Shared** owns the low-level gateway clients (`OpenClawGatewayClient`, `WindowsNodeClient`, `WebSocketClientBase`), device identity/signing (`DeviceIdentity`), protocol models, and the `IOperatorGatewayClient` interface.

`WindowsNodeClient` also owns gateway invocation lifetime at the transport
boundary. Active invokes are registered by invoke ID in a focused cancellation
registry, linked to the node connection lifetime, and cancelled individually by
the gateway `node.invoke.cancel` event. Active invocations atomically transition
to cancelled or completed when capability execution returns; whichever
transition wins determines the protocol outcome. Capability implementations
remain responsible for cooperative cancellation of their own underlying work.

**OpenClaw.Connection** owns all connection management: `GatewayConnectionManager`, `GatewayRegistry`, `CredentialResolver`, `ConnectionStateMachine`, `NodeConnector`, `SshTunnelService/Manager`, `SetupCodeDecoder`, and all connection interfaces/DTOs/enums. This project has zero WinUI dependencies and is independently testable.

**OpenClaw.Tray.WinUI** consumes the connection layer through interfaces. It never creates gateway clients directly — `GatewayConnectionManager` owns that entirely.

## Consumer API

The tray app interacts with three main objects:

### `IGatewayConnectionManager` — connection lifecycle

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

### `GatewayRegistry` — gateway catalog

```csharp
GetAll() / GetById(id) / GetActive()   // read configured gateways
AddOrUpdate(record)                     // create or update a gateway record
SetActive(id)                           // switch which gateway is active
FindByUrl(url)                          // lookup by URL (deduplication)
Save() / Load()                         // persist to gateways.json
GetIdentityDirectory(id)                // per-gateway identity directory path
MigrateFromSettings(...)                // one-time legacy migration
```

### `IOperatorGatewayClient` — gateway API (via `OperatorClientChanged`)

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

## Companion-owned Gateway update and rollback

`GatewayVersionAlignmentCoordinator` owns exact installed-version comparison and the update transaction for the fixed Companion-owned WSL distro. A normal update:

1. proves the active gateway is the setup-managed WSL gateway;
2. proves current Gateway, Companion, Windows Node, and pairing health;
3. asks `GatewayRollbackPointManager` to terminate only that distro and export a complete offline VHD;
4. verifies the VHDX signature, byte size, SHA-256, and private manifest receipt, and attests the distro machine identity plus exact OpenClaw version (including build metadata) immediately before stop and after export;
5. runs `openclaw update --tag <exact-version> --yes --json` in the existing distro;
6. verifies the exact installed version and resynchronizes Gateway, Companion, Windows Node, and pairing;
7. re-verifies the rollback point, marks the update healthy, and only then applies retention cleanup.

Normal update never unregisters, imports, recreates, or directly replaces the distro's `ext4.vhdx`. A failed or ambiguous update preserves the verified rollback point and requires an explicit restore decision. Its target version is stored in the durable receipt, so retry reuses the same point without requiring the disrupted pre-update runtime to pass the new-update health gate again. Pending-update discovery is scoped to the fixed Companion-owned distro rather than the mutable Gateway record ID; a receipt belonging to an earlier record blocks for explicit recovery instead of starting a second transaction. A later Companion requiring a different target likewise detects the pending transaction and blocks. For a fresh transaction, `UpdateInProgress` is flushed before the final live attestation; immediately after that receipt write and before every updater invocation, and again before post-update healthy marking or cleanup, canonical registration BasePath, machine identity, WSL registration configuration, effective default user, and the expected exact normalized package version must still match the receipt and observed transaction state. Exact equality is ordinal and includes prerelease and build metadata. Ordered numeric build identifiers such as `companion.3` are compared without discarding metadata; differing metadata that cannot be safely ordered blocks instead of guessing. Concurrent drift, including a newer observed package, preserves the receipt and never triggers a downgrade. If the package is already aligned after a transient health failure, retry resumes synchronization, rollback verification, exact live attestation, healthy marking, and cleanup instead of silently abandoning the transaction.

Emergency restore is a separate confirmed operation. It copies the immutable retained VHD through a private `.partial` staging file, flushes and verifies it before promotion, and safely recreates stale or invalid regular staging files from the immutable point before any destructive WSL lifecycle call. Before unregister, it verifies the same-name WSL registry entry maps to the canonical app-owned `BasePath`, the directory contains only its regular `ext4.vhdx`, and no owned path boundary is a reparse point; it repeats host-side validation after termination and revalidates registration absence/path safety before import. It may then unregister the current Companion-owned distro, move the verified staged copy into the canonical app-owned install directory, and use documented `wsl --import-in-place` under the same distro name. The retained rollback VHD never becomes the mutable live disk. Manifest receipts independently capture the supported WSL registration version/default UID/flags and the effective default username/UID selected inside the distro on both sides of export; `/etc/wsl.conf` may make those UID values differ. Receipt writes use write-through and disk flush before `UnregisterPending` and `ImportPending` lifecycle transitions. Across the entire Companion-owned distro, regardless of a later Gateway record-id change, a receipt in `RestoreStaged`, `UnregisterPending`, `DistroUnregistered`, `ImportPending`, or `Imported` blocks both the app-level package probe and every fresh update. Multiple unresolved restore receipts fail as ambiguous. Only `RestoreStaged` may be durably cancelled as `RestoreCancelled`; Settings exposes that exact-point-confirmed non-destructive exit. After the unregister boundary, recovery must resume the same point, and `Imported` remains blocking until full synchronization plus a final exact live attestation records `RestoreHealthy`. Once a receipt reaches the unregister boundary, later probe/preflight failures preserve that monotonic phase rather than degrading it to a generic failure that could repeat lifecycle operations. After import, the supported WSL configuration API restores the recorded registration UID/flags and readback must match before the internal machine identity and independently recorded effective default user are accepted. Durable phases support retry after unregister or failed import; a same-name distro with a different internal machine identity, mismatched registration/default user, or any registration/install-path collision fails closed.

Retention keeps at least the newest verified known-good point. Settings allow one previous version (default), two, or indefinite retention. Optional age retention is additive: points inside the age window are kept in addition to the count floor. Points are cleanup-eligible only after successful post-update or post-restore health.

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
%APPDATA%\OpenClawTray\gateways.json           — gateway records
%APPDATA%\OpenClawTray\gateways\<id>\          — per-gateway identity directory
%APPDATA%\OpenClawTray\gateways\<id>\device-key-ed25519.json  — keypair + tokens
```

Each `GatewayRecord` contains: `Id`, `Url`, `FriendlyName`, `SharedGatewayToken`, `BootstrapToken`, `LastConnected`, `SshTunnel` config, `IsLocal`, `RequiresV2Signature`, `SetupManagedDistroName`, and `BrowserControlPort`. The `IdentityDirName` property is computed from `Id`.

Many gateway records may be saved, but only `ActiveId` in `gateways.json` is the effective gateway. Active gateway changes must be made through `GatewayRegistry.SetActive(...)` and saved immediately by connection flows that switch or apply credentials. `SetActive(...)` raises `GatewayRegistry.Changed`, so UI and diagnostics can observe a gateway switch even before the new connection finishes. Each active gateway resolves identity from `%APPDATA%\OpenClawTray\gateways\<id>\`; old gateway events are ignored by `GatewayConnectionManager` generation + gateway-id guards after a switch.

`SettingsManager` still owns general tray settings (node mode, MCP mode, SSH tunnel toggles, notifications, UI preferences). It may read legacy `Token` / `BootstrapToken` JSON fields into memory for migration, but save must not write those legacy credential fields back.

## Credential precedence

Credential resolution order is intentionally strict:

1. **Stored device token** in the per-gateway identity directory.
2. **`GatewayRecord.SharedGatewayToken`** — shared token for HTTP/chat surfaces.
3. **`GatewayRecord.BootstrapToken`** — one-time setup, limited scopes.
4. **No credential** — caller logs and skips client init.

The invariant is that a paired device token always wins. Do not downgrade a paired operator or node to a shared/bootstrap token, because that can reduce scopes or trigger unnecessary re-pairing.

**`CredentialResolver`** implements the precedence for WebSocket connections (operator and node roles). It also returns a detailed `GatewayCredentialResolution` so the active snapshot and diagnostics can distinguish `Resolved`, `Missing`, `Unreadable`, `Corrupt`, `FallbackUsed`, and `BootstrapRequired`. Shared-token-only gateways are a clean resolved state when no paired device token exists. If a stored per-gateway device token is unreadable or corrupt and the resolver falls back to a shared/bootstrap token, `GatewayConnectionSnapshot` preserves that fallback status instead of reporting only the token source.

Unreadable/corrupt identity fallback is an explicit same-gateway recovery path, not a silent cross-gateway downgrade. A readable stored device token still always wins. When the per-gateway identity file cannot be read or parsed, fallback may use only credentials already stored on the same active `GatewayRecord`; the snapshot and diagnostics report `FallbackUsed` with `PrimaryStatus=Unreadable` or `Corrupt` so UI/diagnostics can prompt repair or re-pair. Credential reads never fall back to another gateway's identity directory.

Node credential precedence follows the same invariant with a distinct stored token:

1. **Stored node device token** in the per-gateway identity directory.
2. **`GatewayRecord.SharedGatewayToken`** — shared token fallback when no paired node token exists.
3. **`GatewayRecord.BootstrapToken`** — one-time setup, limited scopes.
4. **No credential** — caller logs and skips node client init.

**`InteractiveGatewayCredentialResolver`** resolves credentials for HTTP surfaces (chat URL `?token=` auth). It **prefers SharedGatewayToken** over DeviceToken because HTTP endpoints expect the shared token, not the per-device WebSocket token. Browser proxy diagnostics should treat the missing shared token as a browser-control caveat, not as proof that the operator or node gateway connection is disconnected.

## Client instance lifecycle

**Operator client** (`OpenClawGatewayClient`): Single instance at a time, owned by `GatewayConnectionManager`. Created via `GatewayClientFactory.Create()`. Old instance disposed before creating new one. `OperatorClientChanged` event notifies consumers of swaps.

**Node client** (`WindowsNodeClient`): Two mutually exclusive creation paths:
- **Normal**: `NodeConnector` creates it → fires `ClientCreated` → `NodeService.AttachClient()` receives it (no new client created)
- **Local setup**: `NodeService.ConnectAsync()` creates its own client (used only during WSL local gateway setup)

Both paths dispose old clients before creating new ones.

## Setup-code and pairing flow

Setup codes (from QR scan or paste) decode to `{ url, bootstrapToken }` via `SetupCodeDecoder`. The flow:

1. `ApplySetupCodeAsync(code)` decodes and validates
2. Creates/updates a `GatewayRecord` with the bootstrap token
3. Clears stored device tokens (fresh pairing)
4. Connects to the new gateway
5. Gateway returns `hello-ok.auth.deviceToken` after pairing
6. Connection manager persists the device token to the identity file

**Approval boundaries**: `GatewayConnectionManager` leaves node-pair command-trust requests and reapproval pending for explicit operator approval. It may automatically approve and reconnect only an explicitly typed device-pair request used for a device role upgrade.

## Inbound pairing approval (operator)

When **another** device or node requests pairing, the gateway broadcasts `device.pair.requested` / `node.pair.requested` to operators with pairing scope. `OpenClawGatewayClient` refreshes the pending lists and raises `DevicePairListUpdated` / `NodePairListUpdated`, which `GatewayService` forwards via its `PairListsChanged` event.

`PairingApprovalCoordinator` (tray) reconciles those snapshots through the pure `PairingApprovalQueue` (OpenClaw.Connection) into add/resolve deltas, de-duplicating, suppressing already-decided requests, and filtering out the local node's own pending request (handled by the auto-approve path above). For genuinely new requests — when `ShowPairingApprovalDialog` is enabled and the operator holds pairing scope — it raises `ApprovalRequested`, and the app presents a focused **`PairingApprovalDialog`** plus an awareness toast (with a "Review" action). The dialog shows the requester's identity and the **operator scopes being granted** (mapped to friendly text by `PairingScopeDescriptions`), with Approve / Reject / Decide-later. Approve is briefly disabled on each new request to prevent click-through. Approve/Reject call the `IOperatorGatewayClient.{Device,Node}Pair{Approve,Reject}Async` RPCs; the queue advances and the dialog closes when empty. The existing Connections-page "Pending approvals" banner remains as the passive fallback when the dialog is disabled. Pure queue/scope logic is unit-tested in `OpenClaw.Connection.Tests`.

## SSH tunnel integration

`SshTunnelService` manages an SSH local port-forward process and implements `ISshTunnelManager` directly for the connection manager.

When a `GatewayRecord` has `SshTunnel` config, the connection manager starts the tunnel before connecting the WebSocket client to `ws://localhost:<localPort>`. The config stores the SSH daemon port (`sshPort`, default `22`) separately from the remote gateway port forwarded by `-L`.

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

- `ConnectionStateMachineTests` — FSM transitions, derived overall state
- `CredentialResolverTests` — credential precedence for operator and node
- `GatewayConnectionManagerTests` — connect/disconnect/switch, diagnostics, handshake
- `GatewayRegistryTests` / `GatewayRegistryMigrationTests` — persistence, migration
- `InteractiveGatewayCredentialResolverTests` — HTTP credential resolution
- `NodeConnectorTests` — node client lifecycle
- `PairingFlowTests` / `NodePairAutoApproveTests` — pairing lifecycle, device role-upgrade auto-approval, and manual node command-trust boundary
- `SetupCodeFlowTests` / `SetupCodeDecoderTests` — QR code → connect flow
- `StaleEventGuardTests` — generation-guarded event handling
- `SettingsChangeImpactTests` — settings change classification
- `RetryPolicyTests` — backoff policy
- `ConnectionDiagnosticsTests` — ring buffer diagnostics

The heaviest remaining gap is Windows shell UI behavior (tray clicks, tooltip visibility, WinUI menu routing). Cover pure decision logic in unit tests; use manual or integration smoke tests for shell behavior.
