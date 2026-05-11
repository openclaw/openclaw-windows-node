# Connection Architecture

This document is the current map for agents changing gateway connection, pairing, node, MCP, or tray action behavior.

## Source of truth

`GatewayRegistry` is the source of truth for configured gateways. It persists records to:

```text
%APPDATA%\OpenClawTray\gateways.json
```

Each gateway has a stable ID, URL, optional shared gateway token, optional bootstrap token, optional SSH tunnel settings, and a per-gateway identity directory:

```text
%APPDATA%\OpenClawTray\gateways\<gateway-id>\device-key-ed25519.json
```

`SettingsManager` still owns general tray settings such as node mode, MCP mode, SSH tunnel toggles, notifications, and UI preferences. It may read legacy `Token` / `BootstrapToken` JSON fields into memory for migration, but save must not write those legacy credential fields back.

## Connection manager

`GatewayConnectionManager` owns runtime connection state:

- Operator client lifecycle.
- Node connector lifecycle when node mode is enabled.
- Active gateway ID and URL.
- State transitions through `ConnectionStateMachine`.
- Credential resolution diagnostics.
- Device-token persistence after gateway handshake.
- SSH tunnel startup when the active gateway record has tunnel settings.

UI surfaces should use `GatewayConnectionManager` and `GatewayRegistry` instead of constructing independent gateway clients. The current tray app wires these in `App.xaml.cs` during startup and passes live references into Hub/Connection Status windows.

## Credential precedence

Credential resolution order is intentionally strict:

1. Stored device token in the per-gateway identity directory.
2. `GatewayRecord.SharedGatewayToken`.
3. `GatewayRecord.BootstrapToken`.
4. No credential.

The invariant is that a paired device token always wins. Do not downgrade a paired operator or node to a shared/bootstrap token, because that can reduce scopes or trigger unnecessary re-pairing.

`CredentialResolver` is the canonical resolver for connection manager operator/node connections. `InteractiveGatewayCredentialResolver` is the WinUI-free resolver for user-facing surfaces such as standalone chat and embedded chat.

## Legacy migration

On startup, the app loads `SettingsManager`, then `GatewayRegistry`. If no active gateway record exists, the app migrates legacy settings credentials into a gateway record:

- `LegacyToken` becomes `GatewayRecord.SharedGatewayToken`.
- `LegacyBootstrapToken` becomes `GatewayRecord.BootstrapToken`.
- The old identity file is copied into the per-gateway identity directory when present.

Migration is idempotent and should not duplicate records for the same URL.

## Setup-code and pairing flow

Setup codes decode to `{ url, bootstrapToken, expiresAtMs }`. Bootstrap tokens are stored as `GatewayRecord.BootstrapToken` and sent as `auth.bootstrapToken`, not as the normal `auth.token`.

After successful pairing, the gateway returns `hello-ok.auth.deviceToken`. The connection manager stores that token in the per-gateway identity file, and future connects use it before shared/bootstrap credentials.

## MCP-only mode

`EnableMcpServer` and `EnableNodeMode` are independent:

| EnableNodeMode | EnableMcpServer | Behavior |
|---|---|---|
| false | false | Operator-only tray app |
| false | true | Local MCP server only; no gateway required |
| true | false | Gateway node only |
| true | true | Gateway node plus local MCP server |

The `EnableMcpServer=true`, `EnableNodeMode=false` path must create a local-only `NodeService` even when there is no gateway credential. Integration tests rely on this mode to start the MCP server without a gateway.

## Tray action UX

Tray actions should never silently no-op on common pairing/configuration issues:

- Standalone chat and embedded chat resolve credentials from the active registry record and per-gateway identity.
- If chat has only a bootstrap token, or no usable credential, it opens Hub Connection settings instead of opening an unusable chat surface.
- Canvas opens only when the Windows node is initialized and paired; otherwise it opens Connection settings.
- Quick Send continues to use the live operator client and can still surface scope/pairing errors from gateway calls.

The tray tooltip is constrained for the Windows shell and reapplied after icon updates because WinUIEx/Explorer can lose the tooltip after tray icon refreshes.

## High-value tests

Connection behavior is mostly covered in `tests\OpenClaw.Tray.Tests\Connection`:

- Registry persistence/migration.
- Credential precedence.
- Connection state machine transitions.
- Gateway connection manager lifecycle.
- Pairing flow and stale event guards.
- Setup-code flow.
- Interactive chat credential resolution.

The heaviest remaining gap is true Windows shell UI behavior: tray hover tooltip visibility, physical tray clicks, and WinUI menu action routing. Cover pure decision logic in unit tests when possible, and use manual or integration smoke tests for shell behavior.
