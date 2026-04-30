---
name: windows-a2ui
description: Generate A2UI v0.8 surfaces for the OpenClaw Windows tray node, which renders A2UI natively in WinUI/XAML (not a WebView). Read this when pushing canvas.a2ui.push/canvas.a2ui.pushJSONL to a Windows node — covers the 18 supported components (Row, Column, List, Card, Tabs, Modal, Divider, Text, Image, Icon, Video, AudioPlayer, Button, CheckBox, TextField, DateTimeInput, MultipleChoice, Slider), the icon-name enum, the A2UIValue tagged union, JSONL envelope shapes, and action.context security / dataBinding rules. Catalog-strict: components outside this list render as the fallback Unknown placeholder.
---

# Windows Node A2UI

The Windows tray node (`OpenClaw.Tray.WinUI`) implements an A2UI v0.8 renderer in **native WinUI 3 / XAML**. Each A2UI component maps to a real `FrameworkElement` (StackPanel, Border, TabView, TextBox, Slider, etc.) — there is no WebView, no Lit, no shadow DOM. The renderer is **catalog-strict**: a `surfaceUpdate` referencing a component name not in the table below renders as a single Unknown placeholder, not as an attempted best-effort.

Use this skill whenever the user asks the agent to push UI to a Windows node, or whenever you'd otherwise generate a generic A2UI payload — Windows specifics (component set, icon enum, secret-path rules) differ from the Lit reference renderer.

## Wire shape

A2UI is delivered to the node via the gateway RPC `canvas.a2ui.push` (or the alias `canvas.a2ui.pushJSONL`), with a single string parameter `jsonl` holding **newline-separated JSON envelopes**. Surfaces are torn down with `canvas.a2ui.reset` (no parameters).

Four envelope kinds, sent in order:

```jsonl
{"surfaceUpdate":{"surfaceId":"main","components":[ /* component defs */ ]}}
{"dataModelUpdate":{"surfaceId":"main","path":"/","contents":[ /* entries */ ]}}
{"beginRendering":{"surfaceId":"main","root":"<root-component-id>","catalogId":"a2ui-v0.8","styles":{}}}
{"deleteSurface":{"surfaceId":"main"}}
```

- `surfaceUpdate` declares (or replaces) the component set for a surface. There is no separate createSurface envelope in v0.8 — the surface is implied by its first surfaceUpdate.
- `dataModelUpdate` writes typed entries into the surface's data model. `path` scopes the keys; omit (or use `"/"`) to replace the whole model.
- `beginRendering` picks the root component for the surface and applies optional styles. Send it after the surfaceUpdate that introduces the components.
- `deleteSurface` tears a surface down. Use `canvas.a2ui.reset` if you want every surface gone.

Lines exceeding 1 MiB are dropped server-side. Malformed lines are logged and skipped — they do not abort the stream.

## Component definition shape

Each entry of `surfaceUpdate.components`:

```json
{
  "id": "btnPrimary",
  "component": { "<ComponentName>": { /* properties */ } },
  "weight": 1.0
}
```

- `id` is the surface-unique handle used as a child reference and as `sourceComponentId` on outbound actions.
- `component` is a one-key wrapper whose key is the discriminator. Names are case-sensitive (the registry is loaded with `OrdinalIgnoreCase` lookup, but match the wire exactly).
- `weight` is optional and currently ignored by the Windows renderer's StackPanel layout.

## A2UIValue (the literal-or-bound tagged union)

Almost every component property accepts a value of this shape:

```json
{ "literalString":  "Hello" }
{ "literalNumber":  42 }
{ "literalBoolean": true }
{ "literalArray":   ["a", "b"] }
{ "path":           "/user/name" }
```

A `path` is a JSON Pointer into the surface's data model. The renderer subscribes to the path and re-renders the affected element on `dataModelUpdate`. Two-way bindings (TextField, CheckBox, Slider, DateTimeInput, MultipleChoice) write back to the same path on user interaction.

## Data model entries

`dataModelUpdate.contents[]` items:

```json
{ "key": "name",     "valueString":  "Alice" }
{ "key": "count",    "valueNumber":  3 }
{ "key": "enabled",  "valueBoolean": true }
{ "key": "user",     "valueMap": [
  { "key": "first",  "valueString": "Alice" },
  { "key": "last",   "valueString": "Smith" }
] }
```

Exactly one of `valueString` / `valueNumber` / `valueBoolean` / `valueMap` should be set per entry. `valueMap` is a recursive adjacency list (each item is itself an entry). Arrays of strings are not first-class on the data-model side — store them as a `path` consumed by `MultipleChoice.selections` or by a literalArray default.

## Components

### Containers

| Component | Children | Properties (Windows-renderer) |
|---|---|---|
| **Row** | `children: { "explicitList": ["id1","id2",...] }` | `distribution`: `start`/`center`/`end`/`spaceBetween`/`spaceAround`/`spaceEvenly` (main-axis); `alignment`: `start`/`center`/`end`/`stretch` (cross-axis). Renders as horizontal `StackPanel`; default spacing 8. |
| **Column** | `children: { "explicitList": [...] }` | Same as Row, vertical. |
| **List** | `children: { "explicitList": [...] }` | `direction`: `vertical` (default) or `horizontal`; `alignment`. Wrapped in a `ScrollViewer`. **Template/data-bound rows are not supported in v1** — supply explicit child ids. |
| **Card** | `child: "<id>"` (single string, not explicitList) | None. Renders as a themed `Border` with 16 px padding and rounded corners. |
| **Tabs** | per-tab via `tabItems` | `tabItems: [{ "title": <A2UIValue>, "child": "<id>" }, ...]`. Renders as a `TabView` with non-closable, non-reorderable tabs. |
| **Modal** | `entryPointChild: "<id>"`, `contentChild: "<id>"` | None. **Currently rendered inline as an `Expander`**, not a real dialog. The entryPointChild becomes the header; the contentChild becomes the expanded body. |
| **Divider** | — | `axis`: `horizontal` (default) or `vertical`. Renders as a 1 px rectangle. |

Children are referenced by **id**, not nested in place. Build the surface as a flat array of components and let `beginRendering.root` pick the entry point.

### Display

| Component | Properties |
|---|---|
| **Text** | `text`: A2UIValue; `usageHint`: `h1`/`h2`/`h3`/`h4`/`h5`/`body` (default) /`caption`. Maps to WinUI text styles (`TitleLargeTextBlockStyle` etc.). Wraps. |
| **Image** | `url`: A2UIValue; `fit`: `contain`/`cover`/`fill`/`none`/`scale-down` (defaults to `contain`); `usageHint`: `icon` (24 px square), `avatar` (40 px square), `smallFeature` (h=80), `mediumFeature` (h=160), `largeFeature` (h=240), `header` (stretch). `description` or `label` is used as the automation/alt name. Subject to MediaResolver allowlist. SVG is supported via the same `url` slot. Accepted as `data:image/svg+xml;base64,…` (≤ 2 MiB) or as `https://allowed-host/…svg` (host must be in `Settings.A2UIImageHosts`). The renderer uses Direct2D's static SVG 1.1 subset — no scripts, no animation, no `<foreignObject>`, no external references. Self-contained SVGs only. |
| **Icon** | `name`: A2UIValue resolving to an entry in the icon enum below. |
| **Video** | `url`: A2UIValue. Renders as `MediaPlayerElement` with transport controls. URL must pass MediaResolver allowlist. |
| **AudioPlayer** | `url`: A2UIValue; optional `description`: A2UIValue (caption above the player). |
| **Divider** | (listed under Containers) |

#### Icon enum

The Windows renderer recognizes only the v0.8 Material-derived icon-name enum, mapped onto Segoe Fluent Icons glyphs. Anything outside this set falls back to the Help glyph:

```
accountCircle, add, arrowBack, arrowForward, attachFile, calendarToday,
call, camera, check, close, delete, download, edit, event, error,
favorite, favoriteOff, folder, help, home, info, locationOn, lock,
lockOpen, mail, menu, moreVert, moreHoriz, notificationsOff,
notifications, payment, person, phone, photo, print, refresh, search,
send, settings, share, shoppingCart, star, starHalf, starOff, upload,
visibility, visibilityOff, warning
```

`moreHoriz` falls back to the same glyph as `moreVert` (no horizontal-three-dots in MDL2). Use one of these names verbatim — arbitrary glyph strings will not work.

### Interactive

| Component | Properties |
|---|---|
| **Button** | `child`: `<id>` of the label/icon content; `primary`: bool (accent style); `action`: `{ "name": "<actionName>", "context": [{ "key":"...", "value":<A2UIValue> }, ...] }`. Click raises an outbound A2UIAction (see below). Auto-applies `action.name` as the accessibility name when no label/description is set. |
| **CheckBox** | `label`: A2UIValue; `value`: A2UIValue (boolean). When `value.path` is set, toggling the checkbox writes the new boolean back to the path. |
| **TextField** | `label`: A2UIValue; `text`: A2UIValue; `textFieldType`: `shortText` (default), `longText` (multiline TextBox, min height 80), `obscured` (PasswordBox — see secret-path rules below), `number` (numeric InputScope), `date` (date InputScope). When `text.path` is set, edits write back. |
| **DateTimeInput** | `value`: A2UIValue (ISO 8601 string round-trip); `enableDate`: bool (default `true`); `enableTime`: bool (default `false`); `outputFormat`: optional .NET format string applied to the writeback (defaults to ISO 8601 `o`). |
| **MultipleChoice** | `options`: `[{ "label": <A2UIValue>, "value": "<string>" }, ...]`; `selections`: A2UIValue resolving to an array of selected `value` strings; `maxAllowedSelections`: int. **`maxAllowedSelections == 1` renders as a `ComboBox` (single-select)**; everything else renders as a multi-select `ListView`. The path is always written back as a JSON array. |
| **Slider** | `value`: A2UIValue (number); `minValue` (default 0); `maxValue` (default 100); `step` or `stepSize` (default 1.0) — wired to `Slider.StepFrequency`. |

Any property accepting an A2UIValue accepts both literals and `path` bindings interchangeably — pick `path` whenever the value should react to `dataModelUpdate`.

## Outbound actions

When a Button is clicked (the only action source in v1), the node sends an A2UIAction back over the gateway:

```json
{
  "name":              "submitForm",
  "surfaceId":         "main",
  "sourceComponentId": "btnPrimary",
  "context":           { "email": "alice@example.com" },
  "timestamp":         "2026-04-27T18:12:34.000Z"
}
```

`context` is the flattened result of the button's `action.context` array, with each entry's value resolved (literal or path read).

### Action-context security (dataBinding)

The renderer scopes which paths are allowed to flow into `context`:

- **Explicit scope (preferred):** declare `dataBinding` on the source component as an array of paths or `{ "path": "..." }` objects. Only those paths (and their subtrees) can be read into `context`. A `dataBinding: ["/"]` opts in to everything.
- **Implicit scope (fallback):** if no `dataBinding` is set, the scope is the union of all paths referenced by the component's *non-action* properties. Useful but easy to misjudge.
- **Secret paths** (any path bound to a `TextField` of type `obscured`) are dropped from `context` unless `dataBinding` lists them **explicitly**. The implicit scope never picks up secrets, even if they're trivially within reach.

Paths outside the allowed set are silently dropped from the action envelope. If you need a value to round-trip back to the agent, either bind a property to it on the source component, or list it in `dataBinding`.

## Capability gate

Three RPCs are involved, all of which must be on the gateway's `gateway.nodes.allowCommands` list (and the node must advertise them via `node.describe`):

```
canvas.a2ui.push
canvas.a2ui.pushJSONL
canvas.a2ui.reset
```

The Windows node advertises all three. The richer v0.9 envelopes (e.g. `canvas.a2ui.schema`, `canvas.a2ui.dump`) are **not** implemented; do not attempt them.

## Worked example

Sign-in form with a path-bound text field and a primary button:

```jsonl
{"surfaceUpdate":{"surfaceId":"main","components":[
  {"id":"root","component":{"Column":{"alignment":"stretch","children":{"explicitList":["title","emailField","pwField","submit"]}}}},
  {"id":"title","component":{"Text":{"text":{"literalString":"Sign in"},"usageHint":"h2"}}},
  {"id":"emailField","component":{"TextField":{"label":{"literalString":"Email"},"text":{"path":"/form/email"},"textFieldType":"shortText"}}},
  {"id":"pwField","component":{"TextField":{"label":{"literalString":"Password"},"text":{"path":"/form/password"},"textFieldType":"obscured"}}},
  {"id":"submit","component":{"Button":{"primary":true,"child":"submitLabel","dataBinding":["/form"],"action":{"name":"signIn","context":[
    {"key":"email","value":{"path":"/form/email"}},
    {"key":"password","value":{"path":"/form/password"}}
  ]}}}},
  {"id":"submitLabel","component":{"Text":{"text":{"literalString":"Sign in"}}}}
]}}
{"dataModelUpdate":{"surfaceId":"main","path":"/","contents":[
  {"key":"form","valueMap":[{"key":"email","valueString":""},{"key":"password","valueString":""}]}
]}}
{"beginRendering":{"surfaceId":"main","root":"root","catalogId":"a2ui-v0.8"}}
```

Notes on the example:

- The Button declares `dataBinding: ["/form"]` so both `/form/email` and `/form/password` are in scope. Without that, `/form/password` would be dropped from `action.context` because the `obscured` TextField marks it as secret and the implicit scope refuses to include secrets.
- Each child is declared as a top-level component and referenced by id; do not nest component objects inline.
- `beginRendering.root` is sent last, after all component and data-model state is in place.

## Prefer generated SVG over external raster URLs

The Windows node's image pipeline is **closed by default for HTTPS hosts** — `Settings.A2UIImageHosts` is empty until the operator adds entries. That means `<Image>` components pointing at arbitrary `https://cdn.example.com/icon.png` URLs will almost always render as a broken-image placeholder on a fresh install. Don't assume any specific image host is reachable.

**Default to inline-generated SVG via `data:image/svg+xml;…`.** SVG is small in bytes, expressive enough for icons, charts, badges, status glyphs, sparklines, simple diagrams, and arbitrary vector illustration, and works on every Windows node without any operator configuration. The 2 MiB data-URL cap is generous — text SVG compresses well, and 2 MiB holds a substantial amount of geometry.

When to use which:

- **Inline SVG (`data:image/svg+xml;…`)** — first choice for anything you can author from primitives: icons not in the v0.8 icon enum, status indicators, progress bars beyond `<Slider>`, simple charts, color swatches, ASCII-style diagrams as vector, decorative dividers, signature/handwriting, geometry the agent computed.
- **Inline data-URL raster (`data:image/png;…`, `data:image/jpeg;…`, `data:image/webp;…`)** — when the agent already has raster bytes (e.g., from a screenshot tool or a generated image). Same 2 MiB cap.
- **Allowlisted `https://`** — only when you have specific reason to believe the host is on `Settings.A2UIImageHosts` (e.g., the user previously confirmed it, or you're writing a surface for a known operator deployment with a known image CDN). Otherwise, prefer one of the two above.

Authoring guidance for SVG:

- Always include `xmlns="http://www.w3.org/2000/svg"` on the root `<svg>` element.
- Set an explicit `viewBox` (e.g. `viewBox="0 0 24 24"` for icons) and let `fit` on the `<Image>` component handle scaling. Don't bake fixed `width`/`height` into the SVG.
- Use `<rect>`, `<circle>`, `<ellipse>`, `<line>`, `<polyline>`, `<polygon>`, `<path>`, `<g>`, `<text>` — these are well-supported by the static SVG 1.1 subset.
- Avoid `<script>`, event-handler attributes (`onload`, etc.), `<animate>`/SMIL, `<foreignObject>`, `<image href="https://…">`, `<use href="https://…">`. They will be silently ignored — the renderer is a static subset — but emitting them is a tell that the SVG was poorly authored, and at minimum wastes data-URL budget.
- Encoding: prefer raw `data:image/svg+xml;utf8,<svg…>` (URL-encode the `<`, `>`, `#`, and `"` characters) for small SVGs to keep them human-readable; switch to `data:image/svg+xml;base64,…` once the payload exceeds a few hundred bytes.
- Keep payloads well below the 2 MiB cap. Aim for "small icons under 1 KB, complex illustrations under 100 KB, charts under 500 KB." If a single image needs more than that, the right answer is probably to compose multiple A2UI components rather than ship one giant SVG.

Example — inline SVG icon used in place of a raster URL:

```json
{"id":"checkIcon","component":{"Image":{
  "url":{"literalString":"data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path d='M5 12l4 4 10-10' stroke='currentColor' stroke-width='2' fill='none'/></svg>"},
  "fit":"contain",
  "usageHint":"icon"
}}}
```

The single biggest UX difference between a Windows-node A2UI surface that "just works" and one that's full of broken-image placeholders is whether the agent reaches for inline SVG or external raster URLs. Reach for SVG.

## Things to avoid

- **Inline children.** Always declare components flat and reference by id via `children.explicitList` (containers) or `child` (Card/Button/Modal halves) or per-tab `child` (Tabs).
- **Template/data-bound List rows.** Not implemented — pre-expand or use repeated explicit children.
- **Modal as a real dialog.** It currently renders as an inline Expander. Don't rely on focus-trapping or backdrop dismiss.
- **Custom icon names.** Anything outside the icon enum becomes the Help glyph. Don't ship Material/Fluent codepoints directly.
- **Putting secrets in implicit context.** `obscured` TextField paths only flow into `action.context` when listed in the source component's `dataBinding`. Make the opt-in explicit.
- **v0.9 envelope kinds.** `canvas.a2ui.schema`, `canvas.a2ui.dump`, etc. are not supported on this node; they will land in `UnknownEnvelopeMessage` and be dropped.
