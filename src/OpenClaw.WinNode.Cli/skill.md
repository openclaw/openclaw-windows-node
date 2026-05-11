<!--
  REGENERATE-ME-WHEN-CAPABILITIES-CHANGE

  The list of supported commands below is checked at CI time against the live
  capability surface (see SkillMdDriftTests). When a capability is added,
  removed, or renamed in src/OpenClaw.Shared/Mcp/McpToolBridge.cs
  (CommandDescriptions), update this document so the drift test stays green —
  the test compares command identifiers, so prose can still be tweaked by hand.
-->

# winnode skill reference

`winnode.exe` invokes OpenClaw Windows-node commands on the local tray over a
loopback MCP HTTP endpoint (default `http://127.0.0.1:8765/`). Enable
**Local MCP Server** in the tray's Settings → Advanced before calling.

This document is the agent-facing reference: every supported command, its
argument shape, and the A2UI v0.8 JSONL grammar. It is shipped alongside
`winnode.exe` so an agent can read it once and emit token-efficient calls.

---

## Invocation shape

```
winnode --command <name> [--params '<json-object>'] [--invoke-timeout <ms>]
```

- `--command` (required) — node command (e.g. `system.which`, `canvas.a2ui.push`).
- `--params` — single JSON **object** string, default `{}`. Must be a JSON object,
  not an array or scalar. **`--params @<path>`** loads the JSON object from a
  file on disk (useful for big A2UI payloads / `canvas.eval` scripts).
- `--invoke-timeout` — milliseconds, default 15000, max 600000 (10 min). HTTP
  timeout adds a 5s buffer.
- `--node` — accepted for parity with `openclaw nodes invoke`; **ignored**
  locally. Safe to copy/paste from gateway-side commands.
- `--idempotency-key` — accepted for parity; **ignored**, and the CLI emits a
  `[winnode] WARN` to stderr because local MCP does *not* dedupe retries —
  re-running a command after a transient failure can double-execute side
  effects. If you need idempotency, target the gateway, not winnode.
- `--mcp-url <url>` / `--mcp-port <port>` — override the endpoint. Falls back to
  `OPENCLAW_MCP_PORT` env var, then port 8765. `--mcp-port` must be in
  `[1, 65535]`; out of range fails with exit code 2.
- `--mcp-token <token>` — bearer token override (testing / explicit only). The
  literal value is **visible to other same-user processes via the OS process
  listing** (`Get-CimInstance Win32_Process | Select CommandLine`,
  Process Explorer, etc.). The CLI emits a stderr warning when this flag is
  used. **Prefer `OPENCLAW_MCP_TOKEN` (env var) or the on-disk
  `%APPDATA%\OpenClawTray\mcp-token.txt`** which the tray writes when MCP is
  enabled. Both `OPENCLAW_MCP_TOKEN` and the on-disk file should themselves be
  treated as sensitive operational secrets.
- `--verbose` — log endpoint + ignored flags to stderr. Without `--verbose`,
  HTTP error bodies are emitted only as the first line; with `--verbose`, the
  full body is shown (after sanitization + token-shape redaction).

**Output contract:** stdout receives the capability payload as pretty-printed
JSON (matches `openclaw nodes invoke`). stderr receives errors. Exit code:

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Tool error, JSON-RPC error, transport failure, or HTTP non-2xx |
| 2    | Argument error (missing/invalid flags, bad `--params` JSON, out-of-range port/timeout, non-http URL) |

**Off-loapback safety:** when `--mcp-url` points at a non-loopback host, the
CLI **refuses to send the auto-loaded local MCP token** (and warns on stderr).
An explicitly supplied `--mcp-token` is honored with a warning. This preserves
the loopback-only threat model the tray's MCP server relies on.

---

## Commands

### system.notify
Show a Windows toast notification.
```
{"title": "OpenClaw", "body": "string", "subtitle": "string", "sound": true}
```
Returns `{ "sent": true }`. All fields optional except `body` in practice.

### system.run
Execute a shell command. Subject to the local exec approval policy at
`%LOCALAPPDATA%\OpenClawTray\exec-policy.json`.
```
{
  "command": "string OR string[]",   // required
  "args":    ["string", ...],         // optional, appended to command
  "shell":   "powershell|pwsh|cmd|bash",
  "cwd":     "string",
  "timeoutMs": 30000,
  "env":     { "KEY": "VALUE" }
}
```
Returns `{ stdout, stderr, exitCode, timedOut, durationMs }`.

### system.run.prepare
Pre-flight a `system.run` invocation. Same args as `system.run`. Returns the
parsed plan (`argv`, `cwd`, `rawCommand`, `agentId`, `sessionKey`) without
executing.

### system.which
Resolve binary names to absolute paths.
```
{"bins": ["git", "node", "powershell"]}
```
Returns `{ "bins": { "git": "C:\\...", ... } }`. Names not found are omitted.

### system.execApprovals.get
No params. Returns the active exec policy:
`{ enabled, defaultAction, rules: [{pattern, action, shells?, description?, enabled}] }`.

### system.execApprovals.set
Replace the exec policy.
```
{
  "rules": [{"pattern": "echo *", "action": "allow"}, ...],
  "defaultAction": "allow|deny|prompt"
}
```

### canvas.present
Open the WebView2 canvas window.
```
{
  "url":   "string",          // OR "html": "string"
  "html":  "string",
  "width": 800, "height": 600,
  "x": -1, "y": -1,           // -1 centers
  "title": "Canvas",
  "alwaysOnTop": false
}
```
Returns `{ "presented": true }`.

### canvas.hide
No params. Hides the canvas without destroying state.

### canvas.navigate
```
{"url": "https://..."}    // also accepts file:// or local canvas paths
```

### canvas.eval
```
{"script": "document.title"}    // also accepts "javaScript" or "javascript"
```
Returns the evaluated result.

### canvas.snapshot
```
{"format": "png|jpeg", "maxWidth": 1200, "quality": 80}
```
Returns `{ format, base64 }`.

### canvas.a2ui.push
Render an A2UI v0.8 surface in the canvas. The canvas window opens
automatically — no `canvas.present` required.
```
{
  "jsonl":     "string",   // OR jsonlPath
  "jsonlPath": "string",   // must live under %TEMP%
  "props":     {}           // optional
}
```
Returns `{ "pushed": true }`. **See A2UI grammar below.**

### canvas.a2ui.pushJSONL
Streaming variant of `canvas.a2ui.push` for very large surfaces. Same protocol
contract; `jsonlPath` argument must live under the system temp directory.

### canvas.a2ui.reset
No params. Clears any rendered surfaces. Returns `{ "reset": true }`.

### canvas.a2ui.dump
No params. Returns the current surface graph for introspection. **Read-all:**
this exposes every currently-rendered surface — operators should treat it as
equivalent to a screenshot of every open A2UI surface.

### canvas.caps
No params. Returns renderer capabilities (renderer, snapshot, a2ui version).

### screen.snapshot
```
{
  "format": "png|jpeg", "maxWidth": 1920, "quality": 80,
  "monitor": 0, "screenIndex": 0,    // 0 = primary
  "includePointer": true
}
```
Returns `{ format, width, height, base64, image }` (image is a `data:` URL).

### screen.record
```
{
  "durationMs": 5000,         // required, max 300000
  "format": "mp4|webm",
  "monitor": 0, "screenIndex": 0,
  "maxWidth": 1920, "fps": 30
}
```
Returns `{ format, durationMs, base64 }`.

### camera.list
No params. Returns `{ cameras: [{ deviceId, name, isDefault }] }`.

### camera.snap
```
{"deviceId": "string", "format": "jpeg|png", "maxWidth": 1280, "quality": 80}
```
Returns `{ format, width, height, base64 }`. `deviceId` defaults to system
default camera.

### camera.clip
```
{
  "deviceId": "string",       // optional
  "durationMs": 3000,         // required, max 60000
  "format": "mp4|webm",
  "maxWidth": 1280
}
```
Returns `{ format, durationMs, base64 }`.

## Speech-to-text (stt.*)

Local Whisper.net runs on this device — no audio leaves the box. The
model is downloaded on first use; until then every `stt.*` call returns
a clear error pointing the caller at the Voice Settings page.
**Privacy-sensitive: requires `NodeSttEnabled` in tray Settings.**

### stt.transcribe
Bounded fixed-duration mic capture + transcription.
```
{
  "maxDurationMs": 5000,      // required, > 0, max 30000
  "language": "en"            // optional BCP-47 tag or "auto" — falls back to SttLanguage setting
}
```
Returns `{ transcribed, text, durationMs, language, engineEffective: "whisper" }`.

### stt.listen
Mic capture with voice-activity detection. Returns when the user stops
speaking or after `timeoutMs`. Result is the full silence-bounded
utterance (all Whisper segments concatenated), not a partial first
segment.
```
{
  "timeoutMs": 30000,         // optional, default 30000, range 1000..120000
  "language": "auto"          // optional BCP-47 tag or "auto"
}
```
Returns `{ text, language, durationMs, segments[{ text, startMs, endMs }], engineEffective: "whisper" }`.

### stt.status
Engine readiness. No params. Carries no PII (no transcript history,
no language history, no device IDs, no model paths).
Returns `{ engine: "whisper", readiness, modelDownloadProgress, isListenWithVadSupported, isBoundedTranscribeSupported }`
where `readiness` ∈ `"ready" | "initializing" | "model-downloading" | "model-not-downloaded" | "unavailable"`.

## Text-to-speech (tts.*)

Three providers — Piper (local neural via Sherpa-ONNX, default), Windows
built-in speech, and ElevenLabs (cloud). Provider + per-provider voice
are configured in tray Settings.

### tts.speak
Speak text aloud on the Windows node.
```
{
  "text": "string",           // required
  "provider": "piper|windows|elevenlabs",  // optional, falls back to TtsProvider setting
  "voiceId": "string",        // optional, overrides the per-provider configured voice
  "model": "string",          // optional, ElevenLabs only
  "interrupt": false          // default false; true cuts off any in-progress playback
}
```
Returns `{ spoken, provider, contentType, durationMs }`.

## App control (app.*)

Read-only and small write operations targeting the running tray. Used
by the command palette and by automation that wants to drive the UI.

### app.navigate
Navigate the companion app to a specific page.
```
{"page": "home|sessions|settings|chat|voice|connection|capabilities|conversations|...""}
```
Returns `{ navigated, page }`.

### app.status
Current connection / node state.
No params. Returns `{ connectionStatus, nodeConnected, nodePaired, nodePendingApproval, gatewayVersion, sessionCount, nodeCount }`.

### app.sessions
Active sessions, optionally filtered by agent.
```
{"agentId": "string"}        // optional
```
Returns array of `{ Key, Status, Model, AgeText, tokens }`.

### app.agents
List agents from the connected gateway. No params. Returns the raw
agents JSON array.

### app.nodes
List connected nodes and their capabilities. No params. Returns array
of `{ DisplayName, NodeId, IsOnline, Platform, CapabilityCount }`.

### app.config.get
Read gateway configuration value at a dot-path.
```
{"path": "string"}           // optional; omit to fetch the full config tree
```
Returns the config subtree (or full config) as JSON.

### app.settings.get
Read a local app setting by name.
```
{"name": "string"}           // required
```
Returns the setting value (type depends on the setting).

### app.settings.set
Set a local app setting.
```
{"name": "string", "value": "string"}  // both required
```
Returns `{ name, value }`.

### app.menu
Get tray menu state (status, session count, node count). No params.
Returns array of menu items.

### app.search
Search the command palette and return matching commands.
```
{"query": "string"}          // required
```
Returns array of `{ Title, Subtitle, Icon }`.

---

## A2UI v0.8 grammar (for canvas.a2ui.push)

The `jsonl` argument is a string of newline-separated JSON-RPC-like messages.
Three message kinds are supported. **createSurface and v0.9 messages are
rejected.**

### Message kinds

```jsonc
// 1. Declare components for a surface (creates the surface if new).
{"surfaceUpdate": {
  "surfaceId": "string",
  "components": [ ComponentDef, ... ]
}}

// 2. Pick the root component and (optionally) styles. Send AFTER surfaceUpdate.
{"beginRendering": {
  "surfaceId": "string",
  "root": "componentId",
  "styles": { "primaryColor": "#FF6F61", "radius": 8.0, "spacing": 12.0 }
}}

// 3. Seed/update the data model bound by Path() values.
{"dataModelUpdate": {
  "surfaceId": "string",
  "contents": [
    {"key": "headline", "valueString":  "Hi"},
    {"key": "agreed",   "valueBoolean": false},
    {"key": "volume",   "valueNumber":  20.0}
  ]
}}

// 4. (Optional) Remove a surface.
{"deleteSurface": {"surfaceId": "string"}}
```

### ComponentDef

```jsonc
{"id": "uniqueId", "component": {"<ComponentName>": { ...props }}}
```

### Value bindings (inside a component prop)

| Form                          | Meaning                                |
|-------------------------------|----------------------------------------|
| `{"literalString": "x"}`      | Literal string                         |
| `{"path": "/key"}`            | Read/write the data model              |
| Plain string `"x"`            | Component-id reference (e.g. `child`)  |
| Plain number / bool           | Used directly for numeric/bool props   |

### Component catalog

| Category     | Name          | Notable props |
|--------------|---------------|---------------|
| Container    | `Row`         | `children: {explicitList: ["id", ...]}` |
| Container    | `Column`      | `children: {explicitList: ["id", ...]}` |
| Container    | `List`        | `children`, `dataBinding` |
| Container    | `Card`        | `child: "id"` |
| Container    | `Tabs`        | `tabItems: [{title, child}]` |
| Container    | `Modal`       | `child` |
| Container    | `Divider`     | `axis: "horizontal" | "vertical"` |
| Display      | `Text`        | `text: Lit/Path`, `usageHint: "h1|h2|h3|h4|h5|body|caption"` |
| Display      | `Image`       | `url: Lit/Path`, `fit: "contain|cover|fill|none"`, `usageHint` |
| Display      | `Icon`        | `name: Lit("settings"\|...)` |
| Display      | `Video`       | `url`, `autoplay`, `controls` |
| Display      | `AudioPlayer` | `url`, `controls` |
| Interactive  | `Button`      | `child`, `primary: bool`, `action: {name, ...context}` |
| Interactive  | `CheckBox`    | `label: Lit/Path`, `value: Path` |
| Interactive  | `TextField`   | `value: Path`, `textFieldType: "shortText|longText|obscured"` |
| Interactive  | `DateTimeInput` | `value: Path`, `mode: "date|time|datetime"` |
| Interactive  | `MultipleChoice` | `value: Path`, `options: [{value, label}]` |
| Interactive  | `Slider`      | `value: Path`, `minValue`, `maxValue`, `step` |

Lit/Path = the value-binding shapes from the previous section.

### Minimal "hello world" payload

```
{"surfaceUpdate":{"surfaceId":"hello","components":[{"id":"helloText","component":{"Text":{"text":{"literalString":"Hello, world!"},"usageHint":"h1"}}}]}}
{"beginRendering":{"surfaceId":"hello","root":"helloText"}}
```

Pass this as the `jsonl` value (a single JSON string with `\n` between messages).

---

## Token-efficient call patterns

1. **Skip `--node` / `--idempotency-key`** — they're ignored locally; including
   them just costs tokens. `--idempotency-key` triggers a stderr warning.
2. **Omit `--params` when the command takes no args** (`camera.list`,
   `canvas.hide`, `canvas.a2ui.reset`, `canvas.a2ui.dump`, `canvas.caps`,
   `system.execApprovals.get`).
3. **Large A2UI payloads** — write the JSONL to a file under the system temp
   directory and pass `{"jsonlPath": "<path>"}`. The capability rejects paths
   outside `%TEMP%`. Or pass `--params @<path>` to load the entire JSON
   argument object from disk.
4. **Big binary results (snapshots, captures)** — output is base64 in stdout.
   Pipe to a file (`> capture.json`) instead of letting the agent read it
   inline.
5. **Errors are exit-code-driven** — check `$LASTEXITCODE` (or `$?` in bash)
   first, then read stderr only on non-zero. Exit 2 = your call is malformed.
6. **Debug with `--verbose`, not by sharing transcripts** — without
   `--verbose` the CLI shows only the first line of an HTTP error body and
   redacts long base64url runs. With `--verbose` it shows the full sanitized
   body. Treat any verbose output as containing potentially sensitive paths
   or partial command output before pasting it elsewhere.

## What's NOT exposed
- Pairing / device approval (gateway concept; doesn't apply locally).
- `chat.send`, `sessions.list`, `usage.list`, `node.list` — these belong to the
  operator-side `OpenClaw.Cli.exe`, not `winnode.exe`.
- Idempotency. The gateway de-dupes retries against `--idempotency-key`; local
  MCP does not. Retrying a `system.run` / `system.notify` / `canvas.present`
  call after a transient failure can double-execute the side effect.
- Wildcards in `--command`. The MCP server has an explicit allowlist; unknown
  commands return `Unknown tool: <name>`.
