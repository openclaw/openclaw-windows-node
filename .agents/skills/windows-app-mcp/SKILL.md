---
name: windows-app-mcp
description: Bind winnode or raw MCP to one running OpenClaw dev app, then discover and invoke its tools.
---

# Windows App Local MCP

The running Windows app can host a same-machine MCP server. It is a developer
and automation surface, not the Gateway-to-node transport.

## Keep the transports distinct

| Path | Purpose | Transport |
|---|---|---|
| OpenClaw node mode | Product path for an OpenClaw agent to use a paired Windows machine | Gateway WebSocket and `node.invoke`; agents use the Gateway `nodes` tool |
| Windows app local MCP | Developer path for same-machine clients to inspect and invoke the running app | Authenticated loopback MCP HTTP; developers use MCP tools or `winnode` |

A local MCP call is not Gateway proof. Do not substitute it when the behavior
depends on Gateway pairing, allowlists, reapproval, routing, or `node.invoke`.

## Start an isolated development app

Choose the boundary and get launch authority from the
[proof runtime selection](../openclaw-proof-validation/SKILL.md#select-the-runtime-and-its-authority),
which owns the launch flags and their side effects. For deterministic app
exploration, use the [fixture workflow](../../../docs/GATEWAY_FIXTURE_TESTING.md#build-and-explore).
Its synthetic Gateway cannot prove real node commands or Gateway behavior.

For live capability proof, when a launch and the installed Store Gateway are
authorized:

```powershell
.\run-app-local.ps1 -Isolated -UseStoreGateway
```

`-Isolated` sets only `OPENCLAW_TRAY_DATA_DIR` for the app, to a stable
per-worktree folder under `%TEMP%\OpenClawTray`. That moves settings, logs, the
MCP token, and the single-instance mutex. Setup-local and Local AI state still
use the real `%LOCALAPPDATA%` profile unless `OPENCLAW_TRAY_APPDATA_DIR` and
`OPENCLAW_TRAY_LOCALAPPDATA_DIR` are also set. Without `-UseStoreGateway`, the
launch can build and register a development Gateway, which needs separate
authority.

Enable **Local MCP Server** in Permissions, or set it in the isolated
`settings.json` before launch (property names are PascalCase). For MCP without
a Gateway, see [MCP-only mode](../../../docs/MCP_MODE.md).

### Bind the client to one app

Each agent shell call may start a fresh process, so run the whole block in one
call. Use the profile and endpoint printed by the same launch or fixture run:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<printed-profile-dir>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$mcpUrl = '<printed-loopback-mcp-url>'
winnode --mcp-url $mcpUrl --list-tools
```

- Pass `--mcp-url $mcpUrl` on every call. It overrides `--mcp-port` and
  `OPENCLAW_MCP_PORT`. Setting the profile alone does not change the endpoint.
- Do not pass `--mcp-token`. It overrides `OPENCLAW_MCP_TOKEN`, which overrides
  the profile's `mcp-token.txt`. Clearing the variable makes `winnode` read the
  selected profile's token without printing it.
- The default port is 8765. Two MCP trays cannot share a port. Start a second
  tray with a different `OPENCLAW_MCP_PORT` and use its matching `--mcp-url`.
- Expect `app.status` to describe the selected app. Stop on a discovery or
  authentication error. Do not retry against another listener.

Connection refused means no MCP server listens on that endpoint. The tray may
not be running, Local MCP Server may be off, or it may use another port. Do not
reinstall `winnode`. Check the selected tray and its MCP setting, wait for
`mcp-token.txt` to appear in its profile, then rerun discovery.

## Discover the live surface

Settings decide which capabilities are exposed, so always run `--list-tools`
before invoking. The live `tools/list` response is authoritative. The static
reference is [winnode](../winnode/SKILL.md), kept in step with
`McpToolBridge.CommandDescriptions`. It also covers the `app.*` developer
commands and when `browser.proxy` appears.

## Diagnose the boundary before changing code

Keep the same profile, endpoint, build, and command while reproducing. Do not
reset settings or restart an unowned process to hide the fault.

| Symptom | Check and interpretation |
|---|---|
| Listener unavailable or token missing | Inspect Permissions > Integrations > Local MCP Server in the authorized app. Match its endpoint and profile; the token is created when MCP starts. Never print the token. |
| A static command is absent from `tools/list` | Inspect capability settings and live registration before changing the catalog or Gateway allowlist. |
| Discovery works but invocation fails | Compare the JSON object with the command reference; keep the tool or transport error and a bounded reproduction. |
| Local MCP passes but pairing or remote invocation fails | Investigate the [Gateway connection owner](../../../docs/CONNECTION_ARCHITECTURE.md). Local MCP did not exercise that boundary. |
| Snapshot looks correct but the UI is wrong | Inspect the mounted UI on a qualified isolated desktop. Snapshot data is not proof of pixels or selection. |

For an authorized Gateway, an HTTP probe of the selected Gateway's health URL
shows listener liveness only. It does not prove operator authentication, node
pairing, or permission to invoke a capability.

## Invoke through winnode

Run each call in the same block as the binding lines above:

```powershell
winnode --mcp-url $mcpUrl --command app.status --params '{}'
winnode --mcp-url $mcpUrl --command system.which --params '{"bins":["git","node"]}'
```

`--node` is accepted for CLI parity but ignored. Parameters must be a JSON
object; prefix `@` to load one from a file. Use `--invoke-timeout <ms>` for
long calls.

## Raw MCP protocol proof

Use raw JSON-RPC when changing the server or protocol shape. Read the token
without printing it:

```powershell
$token = Get-Content (Join-Path $env:OPENCLAW_TRAY_DATA_DIR 'mcp-token.txt') -Raw
```

Send `initialize`, `tools/list`, and `tools/call` to `$mcpUrl`. Use
`notifications/cancelled` with the active request ID to prove cancellation.
Never publish the token, settings, Gateway records, device identities, or
secret-bearing responses.

## When changing a command

Follow the [new command MCP contract](../../../docs/WINDOWS_NODE_TESTING.md#new-command-mcp-contract)
and the [validation policy](../../../docs/TEST_COVERAGE.md#validation-policy).
For edits to this guide, use [documentation checks](../../../docs/AGENTS.md#format-and-checks).
