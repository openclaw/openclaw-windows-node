# A2UI v0.8 — Protocol

This is a faithful summary of the v0.8 wire format, distilled from
<https://a2ui.org/specification/v0.8-a2ui/> and
<https://a2ui.org/specification/v0.8-a2a-extension/>.

## 1. Architecture

A2UI is a **streaming, declarative UI protocol** for LLM-generated
interfaces:

- **Server → client**: a JSONL stream (typically over SSE, but the protocol
  is transport-agnostic) carrying UI updates.
- **Client → server**: A2A messages reporting user events.
- **Surfaces**: independently-controllable UI regions, addressed by
  `surfaceId`. A single agent stream can manage many surfaces in parallel.

The component model is an **adjacency list** — a flat dictionary of
`id → component`, with parents referencing children by id. This is easier
for an LLM to emit incrementally than nested trees and is the foundation of
progressive rendering.

## 2. Server → client envelopes

Each JSONL line is a JSON object containing **exactly one** of these keys:

| Key | Purpose |
| --- | --- |
| `surfaceUpdate` | Add or replace components in a surface's adjacency list |
| `dataModelUpdate` | Mutate the surface's data model |
| `beginRendering` | Signal "ready to render"; specify `root` and chosen catalog |
| `deleteSurface` | Tear down a surface |

### 2.1 `surfaceUpdate`

```json
{ "surfaceUpdate": {
    "surfaceId": "main",
    "components": [
      { "id": "btn-1",
        "component": { "Button": { "child": "lbl-1", "action": { ... } } } }
    ]
}}
```

Each entry has `id`, exactly one `component.{TypeName}` object, and an
optional `weight` (used when the parent applies weighted distribution; not
all parents honor it). The component definition is **catalog-validated**:
unknown types fall back to a placeholder (clients MUST NOT crash on unknown
types).

### 2.2 `dataModelUpdate`

```json
{ "dataModelUpdate": {
    "surfaceId": "main",
    "path": "/optional/base",
    "contents": [
      { "key": "name",   "valueString": "Ada" },
      { "key": "age",    "valueNumber": 36 },
      { "key": "active", "valueBoolean": true },
      { "key": "address","valueMap": [ { "key": "city", "valueString": "London" } ] }
    ]
}}
```

The `contents` array is a **typed key-value list** — `valueString`,
`valueNumber`, `valueBoolean`, `valueMap`, `valueArray`. Updates are merged
into the surface's data model rooted at `path` (default `/`). The spec
leaves "merge vs replace" semantics underspecified; in practice both
reference clients overwrite leaves and recurse into maps.

A special idiom — `path: "/x", contents: [{ "key": ".", "valueString": "v" }]`
— is used to set a primitive at a non-root path.

### 2.3 `beginRendering`

```json
{ "beginRendering": {
    "surfaceId": "main",
    "catalogId": "https://a2ui.org/specification/v0_8/standard_catalog_definition.json",
    "root": "card-1"
}}
```

Acts as a **synchronization gate**: until the client sees this, it should
buffer components/data without rendering. `catalogId` is optional —
default is the v0.8 standard catalog. `styles` may also appear here for
per-surface theme tokens.

### 2.4 `deleteSurface`

```json
{ "deleteSurface": { "surfaceId": "main" } }
```

Disposes the surface, its data model, and any subscriptions.

## 3. Client → server events

### 3.1 `userAction`

```json
{ "userAction": {
    "name": "submit",
    "surfaceId": "main",
    "sourceComponentId": "btn-1",
    "timestamp": "2026-04-27T17:05:00Z",
    "context": { "email": "ada@example.com" }
}}
```

`context` is the **resolved** snapshot of the action's `context[]`
(BoundValues evaluated against the data model at click time — see
[`data-and-actions.md`](./data-and-actions.md)).

### 3.2 `error`

A client-side error reporting envelope. The spec leaves the body shape
underspecified.

## 4. A2A extension (v0.8)

A2UI rides on **A2A** as a typed extension:

- Extension URI: `https://a2ui.org/a2a-extension/a2ui/v0.8`
- Messages are A2A `DataPart` objects with `mimeType: "application/json+a2ui"`.
- Capability negotiation:
  - **Agent advertises** in `AgentCapabilities.extensions`:
    - `supportedCatalogIds: string[]`
    - `acceptsInlineCatalogs: bool`
  - **Client declares** support via transport-specific signaling
    (`X-A2A-Extensions` HTTP header, gRPC metadata, JSON-RPC mechanism).
  - Client may include in A2A message metadata:
    ```json
    { "metadata": { "a2uiClientCapabilities": {
        "supportedCatalogIds": [ "https://a2ui.org/.../standard_catalog_definition.json" ],
        "inlineCatalogs": [ { "catalogId": "...", "components": {...}, "styles": {...} } ]
    }}}
    ```
  - Server picks one in the next `beginRendering`.

The available spec text is partial — push/pull operations, retry,
backpressure, and authentication details are **delegated to the A2A layer**
or to implementations.

## 5. Lifecycle

1. Client opens an A2A session and announces capabilities.
2. Server starts a JSONL stream:
   1. Emits `surfaceUpdate` and `dataModelUpdate` lines (any order).
   2. Emits `beginRendering` once the surface is render-ready.
3. Client renders the tree rooted at `root`.
4. User interacts → client emits `userAction` (A2A message, **not** on the
   JSONL stream).
5. Server responds with more JSONL.
6. Server emits `deleteSurface` when done, or session ends.

## 6. Implementation notes (deltas from raw spec)

These behaviors are spec-silent or under-specified; both reference
implementations and this repo make pragmatic choices:

- **Line-delimited JSON parsing** must tolerate malformed lines gracefully —
  a single bad line MUST NOT abort the stream. Both impls log + skip.
- **Size caps** on lines, components per surface, data-model entries.
  WinUI applies hard caps (1 MiB / 2000 / 1024); Lit does not.
- **Modal lifecycle**: spec defines `entryPointChild` + `contentChild` but
  not _when_ the modal is open. Lit uses `<dialog>.showModal()` driven by
  internal state; WinUI uses a `ContentDialog` triggered by entry click.
- **Streaming partial components**: a `surfaceUpdate` may reference an
  `id` whose contents arrive on a later line. Clients MUST defer rendering
  of undefined refs, not throw.
