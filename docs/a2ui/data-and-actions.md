# A2UI v0.8 — Data Binding & Actions

## A2UIValue

Almost every property on a component is an `A2UIValue` — a tagged union of
literal types and a path into the data model.

```jsonc
// All of these are valid:
{ "literalString": "Hello" }
{ "literalNumber": 42 }
{ "literalBoolean": true }
{ "literalArray": ["a", "b", "c"] }   // array-of-string only
{ "path": "/user/name" }              // bind to data-model location

// "Implicit initialization" (literal + path together):
{ "literalString": "default", "path": "/form/title" }
// → on first resolve, the client writes "default" to /form/title,
//   then binds. After that it's a path binding.
```

The spec does **not** enumerate `literalArray<number>` or `literalArray<bool>`
— string arrays are the only explicit array literal in v0.8.

### Resolution at runtime

When a component renders or re-renders, each `A2UIValue` property is
resolved:

1. If a literal is present → use it. (Casting is impl-defined; both
   impls coerce numbers ↔ strings as needed for display.)
2. Else if `path` is present → look up the value in the surface's data
   model and use it.
3. Else → property is "unset" (component decides default behavior).

### Path syntax

Paths are JSON-pointer-_ish_ strings (`/foo/bar/0`). The spec doesn't
formally cite RFC 6901; both impls treat them similarly but differ at
edges:

- **WinUI**: strict RFC 6901 via `DataModelStore.SetByPointer` /
  `Read` (`src/OpenClaw.Tray.WinUI/A2UI/DataModel/DataModelStore.cs`).
- **Lit**: relative paths supported (`.` = current `dataContextPath`,
  bare names resolve relative to context); auto-parses `valueString`
  fields that look like JSON (`vendor/a2ui/.../model-processor.ts:198–225`).
  This is convenient but can be surprising — a string `"[1,2]"` becomes
  an array.

## Data model

A surface's data model is a JSON tree. `dataModelUpdate` envelopes patch
into this tree:

```jsonc
{ "dataModelUpdate": {
    "surfaceId": "main",
    "path": "/user",
    "contents": [
      { "key": "name",  "valueString": "Ada" },
      { "key": "age",   "valueNumber": 36 },
      { "key": "tags",  "valueArray":  [
          { "valueString": "admin" }, { "valueString": "beta" }
      ]},
      { "key": "address","valueMap": [
          { "key": "city", "valueString": "London" }
      ]}
    ]
}}
```

Behaviors **not nailed down by the spec** that matter in practice:

| Question | Lit | WinUI |
| --- | --- | --- |
| Replace vs. merge `valueMap`? | Merge per leaf | Merge per leaf (RFC 6901 set) |
| Notification granularity? | Coalesced via Lit signals | Coalesced via subscription set |
| Per-update size caps? | None | 1024 entries / update; 256-char keys; 64 KiB strings; 32-deep maps |

### Subscriptions

Components watch the model so they can re-render when the agent or another
component writes:

- **Lit**: `@lit-labs/signals`; the root applies an `effect()` to the
  `childComponents` signal so the light-DOM tree re-renders when the
  signal fires (`vendor/a2ui/.../ui/root.ts:39, 85`).
- **WinUI**: `DataModelObservable.Subscribe(path, callback)` returns
  `IDisposable`; renderers call `ctx.WatchValue(componentId, name, value, callback)`
  which installs a per-component subscription that's torn down when the
  component is recycled (`src/OpenClaw.Tray.WinUI/A2UI/Rendering/IComponentRenderer.cs`).

## Actions

A `Button.action` (and other action-bearing properties) declares
**what to send to the agent**:

```jsonc
{
  "name": "submit",
  "context": [
    { "key": "email",   "value": { "path": "/form/email" } },
    { "key": "kind",    "value": { "literalString": "primary" } }
  ]
}
```

When the user clicks, the client must:

1. Resolve every `context[].value` against the data model right now.
2. Build a `userAction` event:
   ```jsonc
   { "userAction": {
       "name": "submit",
       "surfaceId": "main",
       "sourceComponentId": "btn-1",
       "timestamp": "2026-04-27T17:05:00Z",
       "context": { "email": "ada@example.com", "kind": "primary" }
   }}
   ```
3. Send it back via A2A (the spec is explicit: **not** on the SSE/JSONL
   stream).

### What "context" should and shouldn't contain

The spec is silent on **scoping** — i.e., is it OK for a Button to
declare `context: [{ key: "all", value: { path: "/" } }]` and exfiltrate
the entire data model?

The two impls take very different positions here:

- **Lit**: passes `action` and `dataContextPath` straight through in a
  DOM `CustomEvent`. The host (canvas) is responsible for resolving and
  sanitizing — there's no defense at the renderer.
- **WinUI**: `RenderContext.BuildActionContext()` (`IComponentRenderer.cs:183–249`)
  collects an **allowed-paths set** from either:
  - explicit `dataBinding: [ { path: "..." } ]` on the component, or
  - implicit walk over component properties' own `A2UIValue.path` values.

  Each declared `context[].path` is then `IsAllowedPath`-filtered (exact
  match or ancestor with `/` boundary). Secret paths (registered or
  denylisted by substring) are excluded unless explicitly allowed.

This is one of the most consequential **good deviations** in the WinUI
impl — see [`grading.md#security-deviations`](./grading.md#security-deviations).

### Transport

After context is built, both impls hand off to a transport:

- **Lit**: dispatches `StateEvent<"a2ui.action">` (CustomEvent, bubbling,
  composed). Listener wires up however the embedding app wants.
- **WinUI**: `ActionDispatcher` (`src/OpenClaw.Tray.WinUI/A2UI/Actions/IActionSink.cs`):
  - **Debounces** by `surfaceId|sourceComponentId|name` (200 ms window).
  - **Single-flight gate** so a fallback dequeue can't race a fresh send.
  - **Fallback queue** when no transport is connected.
  - Tries each registered transport (`GatewayActionTransport`,
    `LoggingActionTransport`) until one delivers.

For the gateway path, `GatewayActionTransport`
(`src/OpenClaw.Tray.WinUI/A2UI/Actions/GatewayActionTransport.cs`) emits
an `agent.request` node event:

```jsonc
{
  "message": "CANVAS_A2UI action=submit session=main surface=main component=btn-1 host=… instance=… ctx=… default=update_canvas",
  "sessionKey": "main",
  "thinking": "low",
  "deliver": false,
  "key": "<action-id>"
}
```

`AgentMessageFormatter` is a deliberate byte-for-byte port of the Android
node's formatter — the gateway parses tags identically across platforms.

## Security boundaries

| Concern | Spec | Lit | WinUI |
| --- | --- | --- | --- |
| URL fetching for `Image`/`Video`/`AudioPlayer` | silent | unrestricted | HTTPS+allowlist for all three; DNS-rebinding pin only on `Image` fetches (`MediaResolver.cs`'s `SocketsHttpHandler.ConnectCallback`). `Video`/`AudioPlayer` hand the validated URI to `MediaSource.CreateFromUri`, which performs its own DNS at playback — allowlist is the load-bearing defense for media. |
| Unknown component types | "render placeholder, don't crash" | placeholder for spec'd missing; **registers user-supplied custom elements** if a flag is set | strict 18-only `UnknownRenderer` placeholder |
| Markdown / HTML in `Text` | spec says plain string | parses Markdown; HTML blocks rendered in `iframe sandbox=""`; code escaped | renders as plain string |
| Action context leakage | underspecified | passthrough — host's problem | server allowlist + secret denylist |
| Bearer / token surfaces | n/a | n/a | MCP token shown in Settings UI w/ copy button (out-of-band) |
| `canvas.navigate` | n/a (out of A2UI) | n/a | `HttpUrlValidator` gates URLs; user choice of "canvas" vs "browser" opener |

The "Spec is silent" rows are the spots where a reviewer should keep
their guard up — anything Lit forwards to the embedding host can become
a vulnerability if that host doesn't apply policy.
