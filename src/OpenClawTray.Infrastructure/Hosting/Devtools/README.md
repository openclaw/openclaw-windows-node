# Reactor Devtools — MCP Surface

This folder implements the `--devtools` runtime path: the MCP server that
exposes `reactor.*` tools (tree, screenshot, click, state, fire, …), the
`mur devtools` supervisor, and the rolling log that records every call.

Reference specs: [024-ai-agent-devtools.md](../../Docs/specs/024-ai-agent-devtools.md),
[task list](../../../../docs/specs/tasks/ai-agent-devtools-implementation.md).

## Security model

The devtools surface is **developer-loop only**. It is not designed to be a
production endpoint and ships with three hard gates:

1. **Opt-in at the app level.** `ReactorApp.Run(..., devtools: true)` is the
   only way to start the MCP server, the capture server, or the logger. When
   `devtools: false` (the default), no extra listeners are created.
2. **`#if DEBUG` in the scaffold.** `mur new` generates a call with
   `, devtools: true` wrapped in `#if DEBUG`, so a Release build of the
   template has no devtools surface at all.
3. **Loopback-only binding.** The MCP `HttpListener` binds to
   `http://127.0.0.1:{port}/`. It is never exposed on any non-loopback adapter
   and the `ScreenshotCapture` path only reads the local window.

### Caveat: any local process can connect

There is **no authentication on the MCP port in v1**. Any process running on
the same machine as the app — including unrelated applications, browser
tabs connecting over `fetch("http://127.0.0.1:NNNN/mcp", …)`, other user
sessions with local access, etc. — can call every registered tool. That is
acceptable for the dev-inner-loop use case (a human running `mur devtools`
with an agent in the same terminal session), but it means:

- **Do not run `mur devtools` in an environment with untrusted local
  processes.** `reactor.fire` can reach any handler on the live component;
  `reactor.state` can enumerate hook shapes; `reactor.click` can drive the UI.
- **Do not enable `devtools: true` in Release builds that ship to end users.**
  The scaffold's `#if DEBUG` guard makes this hard to do by accident.
- **Do not assume the MCP surface is safe to expose beyond localhost** — e.g.
  via a reverse proxy, SSH tunnel with remote binding, or container port
  forwarding to 0.0.0.0. Loopback is the only supported deployment.

If v1 usage signals push us toward scenarios where these caveats bite
(e.g., CI runners, remote pair-programming), we revisit with an auth story
in a follow-up spec.

## Observability

`DevtoolsLogger` writes one line per tool call to
`%LOCALAPPDATA%/Reactor/devtools/{pid}.log` (Windows) or
`$XDG_STATE_HOME/reactor/devtools/` (non-Windows). Files roll at 10 MB
and we keep the newest five archives. Log level is configured via
`--devtools-log-level off|error|call|trace` (default: `call`).

Line shape (tab-separated):

```
2026-04-18T12:34:56.789Z	tree	r:main/btn-inc	42ms	ok	0
```

Columns: UTC timestamp, tool name, selector (or `-`), latency, `ok`/`err`,
JSON-RPC result code.

## ETW trace correlation

Reactor emits a TraceLogging-style `EventSource` at `Microsoft-UI-Reactor`
(see `Core/Diagnostics/ReactorEventSource.cs`). Hot-path emit sites are
guarded by call-site `IsEnabled()` checks, so when no listener is attached
the disabled path does no Stopwatch, type-name, or event-method work beyond
a single word-sized flag read. Events cover reconcile passes, component
render start/stop with trigger (self/parent), effects flush, child
reconcile, state writes, unmounts, render errors, and MCP call start/stop.

Keywords: `Reconcile=0x1`, `Render=0x2`, `State=0x4`, `Mcp=0x8`,
`Lifecycle=0x10`, `Errors=0x20`. Full keyword mask = `0x3F`.

Provider GUIDs:

- `Microsoft-UI-Reactor` → `{2BA6BC23-ABF9-56DE-3922-8CC701F16EDE}` (name-hashed by EventSource)
- `Microsoft-Windows-XAML` → `{531A35AB-63CE-4BCF-AA98-F88C7A89E455}` (WinUI 3 core, native ETW)

### Required: capture via `xperf` (not `dotnet-trace`)

The WinUI core provider is a **native ETW provider** emitted from WinUI's C++
code. `dotnet-trace` uses the managed EventPipe transport and **cannot
surface native ETW providers** — it will silently drop `Microsoft-Windows-XAML`
events and you will get a Reactor-only trace. To get the correlated
Reactor + XAML timeline you need a classic ETW session via `xperf`, `wpr`,
`logman`, or PerfView.

Recipe (xperf ships with the Windows Performance Toolkit at
`C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\xperf.exe`):

```
set XPERF="C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\xperf.exe"

%XPERF% -start ReactorSession ^
    -on "2BA6BC23-ABF9-56DE-3922-8CC701F16EDE:0x3F:5+531A35AB-63CE-4BCF-AA98-F88C7A89E455:0x9240:5" ^
    -f trace.etl -BufferSize 1024 -MinBuffers 32 -MaxBuffers 256

REM …launch / exercise the app…

%XPERF% -stop ReactorSession
```

The `0x9240` mask on `Microsoft-Windows-XAML` enables **Layout + Rendering +
Input + DComp** keywords. Verbose level (`5`) is recommended so the full
frame-timing / composition events come through.

### Viewing the trace

`.etl` opens natively in **PerfView** (github.com/microsoft/perfview) and
**WPA** (`C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpa.exe`).
PerfView is recommended for CLR + ETW correlation; WPA for composition /
GPU / CPU-sampling views.

For a web-based timeline (Perfetto / Firefox Profiler / Speedscope /
`chrome://tracing`) the ETW stream needs to be converted to Chromium JSON
— `dotnet-trace convert --format Chromium` only handles CPU samples, not
custom `EventSource` events, so a custom `.etl`-to-Chromium converter is
required. One is tracked separately; when it lands, swap the `.etl` path
into it and the resulting JSON drops into https://ui.perfetto.dev/ directly.

## `--print-config`

`mur devtools --print-config [--mcp-port N]` emits JSON fragments for
Claude Code, VS Code, and GitHub Copilot MCP configs, parameterized with
the requested port. The tool never writes to disk — the user pastes the
fragment they want into the target config file themselves.
