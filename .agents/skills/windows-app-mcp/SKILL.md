---
name: windows-app-mcp
description: Use the OpenClaw Windows app's local MCP server and winnode CLI for development, debugging, proof, and automation.
---

# Windows App Local MCP

Use this skill when developing, debugging, or validating the OpenClaw Windows
app through its same-machine MCP server. This is a developer and automation
surface, not the OpenClaw gateway-to-node transport.

## Keep the transports distinct

| Path | Purpose | Transport |
|---|---|---|
| OpenClaw node mode | Product path for an OpenClaw agent to use a paired Windows machine | Gateway WebSocket and `node.invoke`; agents use the gateway `nodes` tool |
| Windows app local MCP | Developer/debugging path for same-machine clients to inspect and invoke the running Windows app | Authenticated loopback MCP HTTP; developers use MCP tools or `winnode` |

Do not describe a local MCP invocation as gateway proof. Do not use local MCP
as a substitute when the behavior being validated depends on gateway pairing,
allowlists, reapproval, routing, or `node.invoke`.

## Start an isolated development app

First choose the required boundary and obtain launch/runtime authority using
the [proof runtime selection](../openclaw-proof-validation/SKILL.md#select-the-runtime-and-its-authority).
That section owns the launch flags and side-effect contract. `-Isolated`
separates profile data only; `-NoBuild` can still prepare a Gateway, and
`-DryRun` can build Companion and create directories. Do not use the launcher
as a read-only planner.

For supported deterministic app exploration, use the
[fixture workflow](../../../docs/GATEWAY_FIXTURE_TESTING.md#build-and-explore)
with an explicit current-build app. Its synthetic Gateway and disabled sensitive
node capabilities cannot prove arbitrary node commands or real Gateway behavior.

Before fixture discovery or invocation, bind the client to both the printed
fixture profile and MCP endpoint. The fixture sets these only for its app
process, not your client shell. These blocks work in both persistent PowerShell
sessions and fresh-process agent shells. Replace the placeholders with values
from the same active fixture run and run each block in full. Agent shell tools
may start a fresh process per call, so variables and environment edits may not
carry over, even when reusing a tool session ID.

Discover tools first:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<printed-fixture-profile>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$fixtureMcpUrl = '<printed-fixture-loopback-MCP-url>'
winnode --mcp-url $fixtureMcpUrl --list-tools
```

Only after discovery succeeds, run the invocation block in full:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<printed-fixture-profile>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$fixtureMcpUrl = '<printed-fixture-loopback-MCP-url>'
winnode --mcp-url $fixtureMcpUrl --command app.status --params '{}'
```

For each later fixture command, repeat all three setup lines in the same tool
invocation as that command. Do not carry only the URL into a fresh process.

Use `--mcp-url $fixtureMcpUrl` on every fixture call, including later examples
in this guide. An explicit URL takes precedence over `--mcp-port` and inherited
`OPENCLAW_MCP_PORT`; setting the profile alone does not change the endpoint.
Do not pass `--mcp-token`: it overrides `OPENCLAW_MCP_TOKEN`, which in turn
overrides the profile token file. Clearing that inherited token in the dedicated
shell ensures the CLI reads the fixture profile's token without printing it.
The explicit data directory takes precedence over the app identity's default
profile. Expect `app.status` to describe the selected fixture app; stop on
discovery/authentication errors rather than retrying against the default 8765
listener. Close a persistent dedicated client shell after proof to discard its
overrides; fresh-process tool calls discard theirs when each process exits.

For live MCP capability proof outside the fixture, prefer isolated tray data so
tests do not change `%APPDATA%\OpenClawTray`. For example, when a live app launch
and use of the installed Store Gateway are authorized:

```powershell
.\run-app-local.ps1 -Isolated -UseStoreGateway
```

This avoids development Gateway packaging/registration, not app side effects.
The default launch without `-UseStoreGateway` can build/register a development
Gateway and needs separate authority. Expect the selected output, Gateway and
profile to be printed. Copy that data directory into the client shell:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<isolated-data-dir>'
```

Enable **Local MCP Server** in Permissions, or write that choice into the
isolated `settings.json` before launch. The property names are PascalCase.
`EnableMcpServer=true` with `EnableNodeMode=false` starts the loopback MCP
server without a gateway credential, and `RequiresSetup` stays false so the
first-run wizard does not block startup. Set `OPENCLAW_TRAY_DATA_DIR` to that
same directory when you run `winnode`, or the CLI reads a different token file.
`OPENCLAW_SKIP_UPDATE_CHECK=1` skips the startup update check.

A connection refused on `http://127.0.0.1:8765/` means the tray MCP server is
not running. Do not reinstall `winnode`. Start this isolated tray, wait until
`mcp-token.txt` appears, then run `winnode --list-tools`. An isolated data
directory uses its own mutex, so it can run beside the normal tray. The listen
port stays 8765 unless `OPENCLAW_MCP_PORT` is set.

## Discover the live surface

Always discover before invoking because settings determine which capabilities
are currently exposed:

```powershell
winnode --list-tools
```

The live `tools/list` response is authoritative. The complete static command
reference is [winnode](../winnode/SKILL.md), and
`McpToolBridge.CommandDescriptions` is the canonical description source.

## Diagnose the boundary before changing code

Keep the same selected profile, endpoint, app build, and command while
reproducing. Choose the narrowest check that distinguishes the suspected
boundary; do not reset settings or restart an unowned process to hide the fault.

| Symptom | Check and interpretation |
|---|---|
| Listener unavailable or token missing | Inspect Permissions > Integrations > Local MCP Server in the authorized app. Match its endpoint and selected profile; the token is created when MCP starts. Never print the token. |
| A static command is absent from `tools/list` | Inspect capability settings and live registration before changing the catalog or Gateway allowlist. |
| Discovery works but invocation fails | Compare the JSON object with the command reference; preserve the tool/transport error and bounded reproduction. A visible tool is not permission for every operation. |
| Local MCP passes but pairing or remote invocation fails | Investigate the [Gateway connection owner](../../../docs/CONNECTION_ARCHITECTURE.md). Local MCP did not exercise that boundary. |
| Snapshot looks correct but the UI is wrong | Inspect the mounted UI on a qualified isolated desktop. Snapshot data is supporting evidence, not proof of pixels or selection. |

If access, authorization, or a qualified host is missing, report the specific
blocked assertion rather than substituting a different transport.

For an authorized Gateway, an HTTP health probe can distinguish listener
liveness from a connection failure. Use the health URL for the selected
Gateway, not an assumed default port:

```powershell
Invoke-WebRequest -UseBasicParsing -Uri '<selected-gateway-health-url>' -TimeoutSec 10
```

An HTTP success is only liveness evidence. It does not prove operator
authentication, node pairing, or permission to invoke a capability.

## Invoke through winnode

```powershell
winnode --command app.status --params '{}'
winnode --command app.connection.status --params '{}'
winnode --command system.which --params '{"bins":["git","node"]}'
```

`winnode` targets the local Windows app only. Its `--node` argument is accepted
for CLI parity but ignored. It loads the MCP token from the selected tray
profile; do not place the token on the command line.

Use `--invoke-timeout <ms>` for bounded long-running calls. Parameters must be
a JSON object. Prefix with `@` to load a large JSON object from a file.

## Raw MCP protocol proof

Use raw JSON-RPC when changing server or protocol shape. Read the token from
the selected isolated profile without printing it:

```powershell
$tokenPath = Join-Path $env:OPENCLAW_TRAY_DATA_DIR 'mcp-token.txt'
$token = Get-Content $tokenPath -Raw
```

Send `initialize`, `tools/list`, and `tools/call` to the loopback endpoint
printed by the app. Use `notifications/cancelled` with the active JSON-RPC
request ID to prove cancellation. Never publish the bearer token, settings,
gateway records, device identities, or raw secret-bearing responses.

## Useful developer-only app commands

Local MCP exposes `app.*` commands that are not advertised to the gateway node:

- `app.status`, `app.connection.status`, `app.connection.gateways`
- `app.navigate`, `app.menu`, `app.search`
- `app.chat.snapshot`, `app.chat.send`, `app.chat.reset`
- `app.chat.queue.list`, `app.chat.queue.cancel`
- `app.settings.get`, `app.settings.set`

Use these to set up deterministic UI state, navigate the current build, inspect
connection state, and drive functional proof. Re-read `tools/list` for the
exact commands available on the running build.

## Capability commands

The same `INodeCapability` registration seam serves both local MCP and gateway
node mode, so local MCP can exercise capability implementations directly:

- `system.*`, `device.*`, `screen.*`, `camera.*`, `location.*`
- `canvas.*`, including A2UI
- `tts.*`, `stt.*`
- `ollama.models`, `ollama.chat` when **Share Windows Ollama** is enabled

`browser.proxy` is not in that MCP-only list. `BrowserProxyActivation` registers
it only when the running app has a gateway client, that gateway's shared token,
and a verified browser endpoint. `winnode --command browser.proxy` then returns
`Unknown tool: browser.proxy`. A refused connection on port 8765 is not a
successful discovery. Re-read `tools/list` after the gateway client is attached.

This proves the local capability path only. Add a real gateway invocation when
the change affects paired-gateway behavior. The review can accept a concrete
gateway-availability blocker. It does not accept a refused MCP port as the
`browser.proxy` call.

## Required closeout

For a new or changed Windows node command:

1. Register it through the shared `INodeCapability` path.
2. Update `McpToolBridge.CommandDescriptions`.
3. Update `.agents\skills\winnode\SKILL.md`.
4. Add capability, MCP bridge, and `winnode` tests.
5. Prove `winnode --list-tools` and `winnode --command ...`.
6. Prove the gateway path separately when gateway-mediated behavior changed.

Follow the [proof skill](../openclaw-proof-validation/SKILL.md) for the complete
validation and PR evidence checklist. For ordinary changes to this guide,
use [documentation validation](../../../docs/AGENTS.md#format-and-checks);
do not invoke live commands solely to validate prose.
