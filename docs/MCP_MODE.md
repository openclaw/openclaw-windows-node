# Local MCP Mode

**Status:** Implemented (initial cut). See `src/OpenClaw.Shared/Mcp/`, `src/OpenClaw.Shared/Mcp/McpHttpServer.cs`, and the Settings UI MCP section.

## Summary

The Windows tray app now ships a **local Model Context Protocol (MCP) server** alongside its existing OpenClaw gateway client. The same node capabilities the agent reaches over the OpenClaw gateway WebSocket — `system.run`, `screen.snapshot`, `canvas.*`, `camera.list`, `camera.snap`, `camera.clip`, `location.get`, `tts.speak`, `system.notify`, `system.execApprovals.*` — are advertised, on the same machine, as MCP tools over `http://127.0.0.1:8765/`.

This means any local MCP client (Claude Desktop, Claude Code, Cursor, an MCP-aware CLI, a custom dev script) can reach into the running tray and drive Windows-native capabilities directly, without an OpenClaw gateway in the loop. The tray app can run in **MCP-only mode** with no gateway connection at all.

The implementation is structured so that **adding a new node capability automatically exposes it via MCP** — no MCP-side code changes required. That is the central design constraint and the main reason we built MCP in-process rather than as a separate adapter.

## Goals

1. **Single source of truth for capabilities.** A new `INodeCapability` registered with `WindowsNodeClient.RegisterCapability(...)` is reachable via every transport the tray supports. Today: gateway WebSocket and local MCP HTTP. Future transports (named pipe, gRPC, whatever) plug in the same way.
2. **Local-first development.** Capabilities can be exercised on Windows without standing up an OpenClaw gateway, without an account, without auth, without a tunnel.
3. **Make MCP clients first-class consumers** of the OpenClaw native node, not afterthoughts. The tooling investment in capabilities (camera consent flows, exec approval policy, canvas WebView2 plumbing) pays off in both directions: agent-via-gateway and agent-via-local-MCP.

## Non-goals (for this iteration)

- **No remote authentication.** Loopback bind + Origin/Host checks keep the endpoint unreachable from any other machine. A local bearer token guards against untrusted local processes on the same box (see [Authentication](#authentication) below). We will revisit ACLs / multi-user when we want remote MCP, multiple users on one box, or shared dev VMs.
- **No SSE / streaming.** Plain JSON-RPC request/response is enough for the synchronous capabilities we have today.
- **No per-tool input schemas.** Capabilities don't expose schemas; MCP `inputSchema` is permissive (`{type: "object", additionalProperties: true}`). When/if `INodeCapability` grows a schema property, the MCP bridge picks it up with no other changes.
- **No port configuration UI.** Default `8765` is hardcoded. Easy to lift into `SettingsManager` later.

## Architecture

### Single capability registry, two transports

```
                ┌─────────────────────────────────────────────┐
                │                NodeService                  │
                │                                             │
                │   List<INodeCapability> _capabilities ◄───┐ │
                │                                           │ │
                │   private void Register(INodeCapability)  │ │
                │   {                                       │ │
                │       _capabilities.Add(cap);             │ │
                │       _nodeClient?.RegisterCapability(cap)│ │
                │   }                                       │ │
                └────┬───────────────────────┬──────────────┘─┘
                     │                       │
                     │                       │
                     ▼                       ▼
          ┌─────────────────────┐  ┌─────────────────────┐
          │ WindowsNodeClient   │  │ McpToolBridge       │
          │ (gateway WebSocket) │  │ (JSON-RPC dispatch) │
          └─────────┬───────────┘  └─────────┬───────────┘
                    │                        │
                    ▼                        ▼
            OpenClaw gateway          McpHttpServer
                                  (HttpListener@127.0.0.1:8765)
                                            │
                                            ▼
                                Local MCP clients
                            (Claude Code, Cursor, etc.)
```

The capability list lives on `NodeService`, *not* on `WindowsNodeClient`. That single change is what makes MCP-only mode possible: the gateway client is now optional. When it exists, `Register(cap)` pushes capabilities into both the local list and the gateway client's registration message. When it doesn't (MCP-only), capabilities still populate the local list and the MCP bridge serves them.

### MCP bridge

`OpenClaw.Shared/Mcp/McpToolBridge.cs` is transport-agnostic JSON-RPC 2.0. It implements:

- `initialize` — protocol version `2024-11-05`, server info.
- `tools/list` — flattens `_capabilities` into MCP tools. Tool name = command name (`"screen.snapshot"`); description = `"{category} capability: {command}"`; `inputSchema` is permissive.
- `tools/call` — finds the capability via `INodeCapability.CanHandle(name)`, builds a `NodeInvokeRequest` (the same struct the gateway path uses), calls `ExecuteAsync`, wraps the result as MCP `content[].text`. Tool failures come back as `result.isError = true`, not JSON-RPC errors (per MCP spec — JSON-RPC errors are reserved for protocol issues).
- `ping`, `notifications/initialized` — protocol housekeeping.

The bridge takes a `Func<IReadOnlyList<INodeCapability>>` rather than a snapshot. Every `tools/list` re-reads the live list. This is what guarantees zero-cost capability addition — register a new capability after server start and it appears on the next `tools/list`.

### HTTP transport

`OpenClaw.Shared/Mcp/McpHttpServer.cs` is `System.Net.HttpListener` bound to `http://127.0.0.1:8765/`. Loopback-only by construction; not reachable from any other machine even with firewall holes. A defensive `IPAddress.IsLoopback` check on each request acts as belt-and-suspenders.

`GET /` returns a friendly text probe. `POST /` is JSON-RPC. Anything else → `405`. When a bearer token is configured, every verb must pass the token gate before method dispatch.

## Authentication

The HTTP transport requires a bearer token on every request. Defense-in-depth on top of loopback bind + Origin/Host checks: if an attacker can run code in *any* local user context they can reach `127.0.0.1:8765`, so we don't want the listener to be open-by-construction.

**Where the token lives.** `%APPDATA%\OpenClawTray\mcp-token.txt`. The exact path is composed by `NodeService.McpTokenPath` from `SettingsManager.SettingsDirectoryPath`, so the test-suite override `OPENCLAW_TRAY_DATA_DIR` isolates the token file too. The file inherits the parent directory's ACL — by default only the current user (and SYSTEM/Administrators) can read it.

**When it's created.** Lazily, on the first `NodeService.StartMcpServer()` call — i.e. the first time the user enables Local MCP Server in Settings and saves. **Until that toggle has been on at least once, the file does not exist.** This trips up users who try to grab the token before flipping the switch.

**How long it is.** 32 bytes of CSPRNG output, base64url-encoded with padding stripped → **43 ASCII characters** (~256 bits of entropy). See `McpAuthToken.Generate()`.

**Lifetime.** The token is **persistent across tray restarts**. It's only regenerated if the file is deleted or its contents are emptied. There is no automatic rotation.

**On the wire.** Every request must carry `Authorization: Bearer <token>` when the server has a configured token. Missing or wrong token → `401 Unauthorized` with no body. `GET /` remains a "yes I'm here" probe after auth passes.

**How users find it.** Settings → Developer Mode → MCP section shows the live token (masked, with Reveal/Copy buttons) and the storage path. For agents that read from disk (Claude Code, custom scripts), pointing them at `McpTokenPath` is preferable to embedding the token in their prompt or config — the path is stable, the token is a secret. For agents that only accept literal bearer values in config (Claude Desktop, Cursor), use Copy.

### Settings model

Two independent toggles in `SettingsData`:

```csharp
public bool EnableNodeMode { get; set; }      // open WebSocket to gateway
public bool EnableMcpServer { get; set; }     // run local MCP HTTP server
```

| `EnableNodeMode` | `EnableMcpServer` | Result |
|---|---|---|
| off | off | Operator-only (legacy default) |
| off | on | **MCP server only, no gateway** |
| on | off | Gateway node, no MCP |
| on | on | Gateway node + MCP |

Settings UI exposes both toggles in the Advanced section, with the live MCP endpoint URL and current status (`Listening` / `Stopped — save and restart to start` / `Disabled`).

A legacy `McpOnlyMode` field is migrated automatically on load and never re-written.

## Why this matters

### Testing

The tray's most interesting code lives in capabilities — `system.run` (LocalCommandRunner + ExecApprovalPolicy), `screen.snapshot` (Windows.Graphics.Capture + GraphicsCapturePicker), `canvas.*` (WebView2 with trusted origin enforcement), `camera.snap`/`camera.clip` (MediaCapture + consent prompt), `location.get` (Windows.Devices.Geolocation). All of that has nontrivial Windows-only behavior and almost none of it is currently exercised end-to-end without first standing up a gateway and authenticating.

Local MCP changes that. Concrete benefits:

- **Manual smoke tests in seconds.** `curl -s -X POST http://127.0.0.1:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'` validates that the capability dispatch path works, the WinUI dispatcher marshaling is correct, the result shape matches expectations. No gateway, no token, no SSH tunnel.
- **Reproducible bug reports.** A repro becomes a `tools/call` body the bug filer can paste verbatim. No "what was the gateway doing at the time."
- **Integration tests against a real instance.** A future `tests/integration/` project can spin up the tray in MCP-only mode, fire JSON-RPC, assert results. The same test bodies a developer runs by hand are the same ones CI runs. (Harnessing WinUI itself in CI is harder, but the bridge logic — `McpToolBridge` — is already covered by `McpToolBridgeTests` with no UI involvement.)
- **Coverage for the dispatch path itself.** `WindowsNodeClient`'s capability-routing logic (`CanHandle` → `ExecuteAsync`) was previously only exercised against a live gateway. The MCP server hits the same code paths, so any local MCP test is implicit coverage of the gateway dispatch.
- **Bridge unit tests already exist.** `tests/OpenClaw.Shared.Tests/McpToolBridgeTests.cs` (9 cases) covers initialize, tools/list, runtime capability registration, tool calls, unknown tools, capability failures, JSON-RPC unknown method, notifications, and parse errors. These are pure C# unit tests with fake capabilities — no HTTP, no UI, no gateway.

### Access from CLIs and agents

The exact same node tools the OpenClaw gateway uses are now invocable by any local MCP-aware client:

- **Claude Code** (this CLI). Add to `~/.claude.json` or per-project `.mcp.json`:

  ```json
  {
    "mcpServers": {
      "openclaw-tray": {
        "type": "http",
        "url": "http://127.0.0.1:8765/"
      }
    }
  }
  ```

  The agent then sees `screen.snapshot`, `system.run`, `canvas.*`, etc. as tools, with whatever arguments the capability accepts.

- **Claude Desktop.** Same config shape under MCP servers.
- **Cursor.** Same.
- **GitHub Copilot CLI / Copilot in the terminal.** As MCP support lands in those clients, the endpoint is already there.
- **Custom dev scripts.** Anything that can speak HTTP + JSON-RPC. A 30-line Python or Node helper can drive the entire capability surface.

In all cases the user gets a Windows-native agent experience without OpenClaw infrastructure. They can be entirely offline w.r.t. an OpenClaw gateway and still hand the LLM a working set of "do something on my Windows box" tools.

### Dev acceleration when building new features

This is the strongest argument for making MCP a first-class citizen, not an afterthought.

When a contributor adds a new capability — say, `clipboard.read`, `clipboard.write`, `windows.list`, `audio.transcribe`, `git.status`, `office.draft_email` — today the workflow looks like:

1. Implement `INodeCapability`.
2. Wire it into `NodeService.RegisterCapabilities()`.
3. Stand up a gateway, authenticate, pair the device, etc., to test.
4. Drive the capability from within an agent conversation, observing logs and taking screenshots to confirm correctness.

With MCP in-process the workflow shortens to:

1. Implement `INodeCapability`.
2. Wire it into `NodeService.RegisterCapabilities()`.
3. Restart the tray. The new tool is *immediately* visible to any local MCP client (`tools/list` re-reads the registry every call), and to manual `curl` tests.

The dev loop for capabilities is now identical to the dev loop for any local HTTP server: edit, restart, hit the endpoint, observe. No gateway, no agent, no auth.

This compounds when you stack it with Claude Code or Cursor on the same machine. A contributor can:

- Open the repo in their IDE.
- Run the tray with `EnableMcpServer = true`.
- Have Claude Code connected to the same MCP endpoint.
- Iterate on a new capability while the agent — using that very capability — helps drive the iteration. The capability under development can be invoked by the assistant on the next turn after a tray restart. That's a tight self-hosted feedback loop.

It also reduces the cost of "speculative" capabilities. Today, adding a capability has a tax: it must be useful enough to justify the extra surface in the gateway/agent stack. With local MCP, a contributor can build a capability speculatively, validate it against their own MCP-aware agent, and only later decide whether to formalize it for gateway use. That lowers the bar for experimentation.

## Security model

The server is built on **three** defensive layers, not just one. Loopback alone is *not* sufficient — a browser tab the user opens is also on the loopback interface, so a malicious page could otherwise reach `http://127.0.0.1:8765/` directly.

1. **Loopback bind.** `HttpListener` is registered with the prefix `http://127.0.0.1:8765/`. The Windows kernel binds the listening socket to the loopback interface only — packets from other interfaces are not delivered to it. Firewall configuration is irrelevant. Defends against: another machine on the network.
2. **Defensive `IsLoopback` check.** Each incoming request validates `ctx.Request.RemoteEndPoint.Address`. Belt-and-suspenders for #1.
3. **CSRF / browser gate.** Each request is rejected if any of the following holds:
   - the request carries an `Origin` header (real MCP clients — Claude Desktop, Cursor, Claude Code, curl — never send `Origin`; browsers always do for cross-origin fetches);
   - the `Host` header is anything other than `127.0.0.1[:port]` or `localhost[:port]` (defends against DNS-rebinding pivots);
   - on `POST`, the `Content-Type` is anything other than `application/json` (forces a CORS preflight from a browser, which we never satisfy).
   - the request body exceeds 4 MiB (DoS / OOM cap).

   Together these three checks force a malicious cross-origin browser fetch into a CORS preflight that we deliberately do not honor (no `Access-Control-Allow-*` is ever emitted), so the actual call is blocked before reaching capability code.
4. **Concurrency cap.** A semaphore limits in-flight handlers to 8. A misbehaving local client cannot pin every threadpool thread on long-running screen/camera calls.
5. **Capability-level controls remain in force.** `SystemCapability.SetApprovalPolicy(...)` (the exec approval policy) still gates `system.run`. Camera and screen capture still go through Windows consent flows. MCP doesn't bypass any of those.

**Still no authentication.** Any user-context local process with a TCP socket and the port number can drive any capability. This is the same trust boundary as anything that runs as the user — a malicious process on the box could already invoke arbitrary Win32 APIs without going through MCP. We don't try to stop user-context processes from talking to MCP. If that turns out to matter (multi-user shared boxes, low-trust local processes), the right answer is per-call bearer tokens issued by the tray (one-time copy-to-clipboard from the Settings UI), not URL ACLs or HTTPS — both add deployment pain without solving the actual problem.

### Verifying the gate

These should all be **rejected** with `403 Forbidden`:

```powershell
# Browser pretending to come from another origin
curl -X POST http://127.0.0.1:8765/ -H "Origin: https://evil.com" -H "Content-Type: application/json" -d '{}'

# DNS rebinding attempt
curl -X POST http://127.0.0.1:8765/ -H "Host: evil.com" -H "Content-Type: application/json" -d '{}'
```

This should be **rejected** with `415`:

```powershell
curl -X POST http://127.0.0.1:8765/ -H "Content-Type: text/plain" --data '{"jsonrpc":"2.0","id":1,"method":"ping"}'
```

These should **succeed**:

```powershell
curl http://127.0.0.1:8765/ -H "Authorization: Bearer <token>"   # GET probe
curl -X POST http://127.0.0.1:8765/ -H "Authorization: Bearer <token>" -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"ping"}'
```

## What's deliberately deferred

These are reasonable next steps but explicitly out of scope for the initial implementation:

1. **Per-tool input schemas.** Add an `IReadOnlyDictionary<string, JsonElement> InputSchemas` (or per-command descriptor) to `INodeCapability`. The MCP bridge's `HandleToolsList` picks them up automatically. Until then, MCP clients see permissive schemas and the agent has to figure out arg shapes from descriptions and trial-and-error.
2. ~~**Authentication.**~~ Implemented. See [Authentication](#authentication) below.
3. **Streamable HTTP / SSE.** For long-running tools (`screen.record`, future `audio.transcribe`), MCP supports streaming progress. The bridge needs to learn about it and the HTTP server needs to optionally upgrade.
4. **Resource and prompt support.** MCP has `resources/*` and `prompts/*` methods we currently no-op. Notifications, recent activity, channel state could be modeled as MCP resources.
5. **Configurable port.** Move `McpDefaultPort` into `SettingsManager`. Probably also pick a free port at startup if the default is in use, and surface the actual port in the Settings UI.
6. **Setup Wizard step.** Today the Settings Advanced section is the only way to enable MCP. The Setup Wizard could offer it as a one-click option, especially attractive for users who don't run a gateway at all.

## File map

| File | Role |
|---|---|
| `src/OpenClaw.Shared/Mcp/McpToolBridge.cs` | Transport-agnostic JSON-RPC dispatcher. |
| `src/OpenClaw.Shared/SettingsData.cs` | Settings JSON model. Adds `EnableMcpServer`; deprecates `McpOnlyMode`. |
| `src/OpenClaw.Shared/Mcp/McpHttpServer.cs` | `HttpListener`-based loopback HTTP transport. |
| `src/OpenClaw.Tray.WinUI/Services/NodeService.cs` | Owns the capability list. Hosts the MCP server when enabled. |
| `src/OpenClaw.Tray.WinUI/Services/SettingsManager.cs` | In-memory settings model + load/save. Migrates legacy `McpOnlyMode`. |
| `src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml(.cs)` | Settings UI surface hosted by `HubWindow`. |
| `src/OpenClaw.Tray.WinUI/App.xaml.cs` | Bootstraps `NodeService` based on the new mode matrix. |
| `tests/OpenClaw.Shared.Tests/McpToolBridgeTests.cs` | 9 unit tests for the bridge. |

## Quick verification

With the tray running and `EnableMcpServer = true`:

```powershell
# Server is up
curl http://127.0.0.1:8765/

# List tools
curl -s -X POST http://127.0.0.1:8765/ `
  -H "Content-Type: application/json" `
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# Take a screenshot of the primary monitor
curl -s -X POST http://127.0.0.1:8765/ `
  -H "Content-Type: application/json" `
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"screen.snapshot"}}'
```

For Claude Code, drop this into `.mcp.json` at the repo root or `~/.claude.json`:

```json
{
  "mcpServers": {
    "openclaw-tray": {
      "type": "http",
      "url": "http://127.0.0.1:8765/"
    }
  }
}
```
