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

## Startup wiring (App.xaml.cs)

```
1. Create GatewayRegistry(dataDir)
2. Create CredentialResolver(identityReader)
3. Create GatewayClientFactory()
4. Create NodeConnector(logger)
5. Create SshTunnelManager(tunnelService, logger)
6. Create GatewayConnectionManager(resolver, factory, registry, ...,
                                    nodeConnector, tunnelManager)
7. Subscribe to StateChanged → update tray icon + hub window
8. Subscribe to OperatorClientChanged → wire/unwire 25+ data event handlers
9. Subscribe to NodeConnector.ClientCreated → NodeService.AttachClient
10. Call ConnectAsync() → connects to active gateway
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
| Connected | Disabled/Off | Connected |

## Gateway registry and persistence

`GatewayRegistry` is the source of truth for configured gateways:

```
%APPDATA%\OpenClawTray\gateways.json           — gateway records
%APPDATA%\OpenClawTray\gateways\<id>\          — per-gateway identity directory
%APPDATA%\OpenClawTray\gateways\<id>\device-key-ed25519.json  — keypair + tokens
```

Each `GatewayRecord` contains: `Id`, `Url`, `FriendlyName`, `SharedGatewayToken`, `BootstrapToken`, `LastConnected`, `SshTunnel` config, and an `IdentityDirName`.

`SettingsManager` still owns general tray settings (node mode, MCP mode, SSH tunnel toggles, notifications, UI preferences). It may read legacy `Token` / `BootstrapToken` JSON fields into memory for migration, but save must not write those legacy credential fields back.

## Credential precedence

Credential resolution order is intentionally strict:

1. **Stored device token** in the per-gateway identity directory.
2. **`GatewayRecord.SharedGatewayToken`** — shared token for HTTP/chat surfaces.
3. **`GatewayRecord.BootstrapToken`** — one-time setup, limited scopes.
4. **No credential** — caller logs and skips client init.

The invariant is that a paired device token always wins. Do not downgrade a paired operator or node to a shared/bootstrap token, because that can reduce scopes or trigger unnecessary re-pairing.

**`CredentialResolver`** implements the precedence for WebSocket connections (operator and node roles).

**`InteractiveGatewayCredentialResolver`** resolves credentials for HTTP surfaces (chat URL `?token=` auth). It **prefers SharedGatewayToken** over DeviceToken because HTTP endpoints expect the shared token, not the per-device WebSocket token.

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

**Auto-approval**: When the node requires pairing and the operator has `operator.admin` or `operator.pairing` scope, `GatewayConnectionManager` automatically approves the node pairing request, waits 1 second, then reconnects the node.

## SSH tunnel integration

`SshTunnelService` manages an SSH local port-forward process. `SshTunnelManager` wraps it behind `ISshTunnelManager` for the connection manager.

When a `GatewayRecord` has `SshTunnel` config, the connection manager starts the tunnel before connecting the WebSocket client to `ws://localhost:<localPort>`.

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
- Canvas opens only when the Windows node is initialized and paired; otherwise it opens Connection settings.
- Quick Send uses the live operator client and surfaces scope/pairing errors from gateway calls.

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

Connection tests live in `tests/OpenClaw.Connection.Tests/` (215 tests):

- `ConnectionStateMachineTests` — FSM transitions, derived overall state
- `CredentialResolverTests` — credential precedence for operator and node
- `GatewayConnectionManagerTests` — connect/disconnect/switch, diagnostics, handshake
- `GatewayRegistryTests` / `GatewayRegistryMigrationTests` — persistence, migration
- `InteractiveGatewayCredentialResolverTests` — HTTP credential resolution
- `NodeConnectorTests` — node client lifecycle
- `PairingFlowTests` / `NodePairAutoApproveTests` — pairing lifecycle, auto-approve
- `SetupCodeFlowTests` / `SetupCodeDecoderTests` — QR code → connect flow
- `StaleEventGuardTests` — generation-guarded event handling
- `SettingsChangeImpactTests` — settings change classification
- `RetryPolicyTests` — backoff policy
- `ConnectionDiagnosticsTests` — ring buffer diagnostics

The heaviest remaining gap is Windows shell UI behavior (tray clicks, tooltip visibility, WinUI menu routing). Cover pure decision logic in unit tests; use manual or integration smoke tests for shell behavior.
