# Native WinUI A2UI Canvas — Design Spec

> **Status:** Draft / proposal
> **Audience:** Contributors implementing a native A2UI renderer for the Windows node
> **Target version:** A2UI v0.8 (parity with current openclaw clients), with a v0.9 migration path

## 1. Motivation

Today the Windows node renders A2UI by hosting a WebView2 control (`CanvasWindow`) that navigates to an HTTP page served by the openclaw gateway at `/__openclaw__/a2ui/`. That page bundles `@a2ui/lit` and openclaw's bridge JS. Pushed messages travel `agent → gateway → node (canvas.a2ui.push) → WebView2 → window.__a2ui.receive(msg)`.

That works, but it has costs:

- **Hard gateway dependency.** A node running in MCP-only mode (no gateway connection) silently drops A2UI pushes — `OnCanvasA2UIPush` bails when `_a2uiHostUrl` is null. The renderer code physically lives at the gateway.
- **WebView2 surface area.** Drag/drop, IME, accessibility, focus, DPI, and keyboard shortcuts inherit WebView2 quirks instead of XAML's native behavior. The canvas always feels like an embedded browser.
- **Bootstrapping latency.** Each cold start has to ensure WebView2 is ready, navigate, and wait for `window.__a2ui` to register before any message can be delivered (`EnsureA2UIHostAsync` + `ensureA2uiReady` polling).
- **Theming drift.** WinUI windows around the canvas use Mica/Fluent; the canvas uses Lit components styled with CSS. Achieving consistent visuals requires duplicate theme work.
- **Hardening.** Surface area for arbitrary script execution remains larger than necessary for what is fundamentally a declarative UI tree.

A native WinUI renderer renders A2UI surfaces directly into XAML — no WebView, no HTTP host, no JS bridge. The node becomes self-contained: it can render A2UI whether it's connected to a gateway, an MCP client, or both.

## 2. Goals & non-goals

### Goals

- **Render A2UI v0.8 standard-catalog surfaces natively** in the Windows node using WinUI 3 / XAML controls.
- **Preserve the existing wire protocol.** Agents continue to send A2UI JSONL via `canvas.a2ui.push` / `canvas.a2ui.reset`. Nothing about the agent side changes.
- **Work offline / gateway-less.** A WSL-less, gateway-less Windows node can still display rich UI from an MCP client.
- **Match Fluent / WinUI design language** by default; allow theme overrides from the surface payload.
- **Stream incremental updates** without flicker (component adds/updates/deletes mid-task).

### Non-goals (initial release)

- No A2UI v0.9 features (bidirectional messaging, prompt-first generation, modular schemas).
- No HTML/JS/CSS escape hatch from inside an A2UI surface (the v0.8 catalog has no such primitive — keep it that way).
- No replacement for `canvas.present` / `canvas.navigate` / `canvas.eval`. Those continue to use WebView2 for general web content. Only A2UI rendering moves.
- No custom (non-catalog) component types in v1. Catalog-strict.

## 3. Architecture

### 3.1 Boundary

```
┌─────────────────────────────────────────────────────────┐
│ OpenClaw.Tray.WinUI (existing)                          │
│ ┌─────────────────────────────────────────────────────┐ │
│ │ NodeService                                         │ │
│ │   CanvasCapability  (existing)                      │ │
│ │     ├─ canvas.a2ui.push   ──► A2UIPushRequested ─┐  │ │
│ │     └─ canvas.a2ui.reset  ──► A2UIResetRequested┐│  │ │
│ │                                                 ││  │ │
│ │   OnCanvasA2UIPush / OnCanvasA2UIReset (existing)││  │ │
│ │     dispatched to UI thread, route to:          ││  │ │
│ └─────────────────────────────────────────────────││──┘ │
│                                                   ▼▼    │
│ ┌─────────────────────────────────────────────────────┐ │
│ │ A2UICanvasWindow  (new) ─ replaces WebView2 path    │ │
│ │   ├─ A2UIRouter        (parses & dispatches msgs)   │ │
│ │   ├─ SurfaceHost x N   (one per createSurface)      │ │
│ │   │     └─ ComponentTree (XAML)                     │ │
│ │   ├─ DataModelStore    (per surface)                │ │
│ │   ├─ ActionDispatcher  (UI events → ws/MCP)         │ │
│ │   └─ ThemeProvider     (Fluent + payload overrides) │ │
│ └─────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────┘
```

The existing `CanvasCapability` and the events it raises (`A2UIPushRequested`, `A2UIResetRequested`) are unchanged. `NodeService.OnCanvasA2UIPush` no longer calls `EnsureA2UIHostAsync` / `SendA2UIMessageAsync` against a WebView2; it instead hands the JSONL to a new `A2UICanvasWindow` (or the existing `CanvasWindow` if we choose to host both renderers).

### 3.2 Coexistence with WebView2 canvas

Two canvas modes share the surface:

| Mode | Trigger | Window |
|---|---|---|
| Web (`canvas.present` / `canvas.navigate` / `canvas.eval`) | URL or HTML payload | `CanvasWindow` (WebView2) — unchanged |
| A2UI native | First `canvas.a2ui.push` since reset | `A2UICanvasWindow` (XAML) — new |

A user-visible toggle is *not* required — the choice is implicit in which MCP command the agent calls. The two windows must not compete for focus; if both want to be visible, the most-recently-targeted wins (last-write-wins, with a small fade between).

### 3.3 Component pipeline

```
JSONL line
  → System.Text.Json deserialize → A2UIMessage (sealed record hierarchy)
  → A2UIRouter.Dispatch(message)
      ├─ CreateSurface  → SurfaceHost.Create(surfaceId, catalogId, theme)
      ├─ UpdateComponents → SurfaceHost.ApplyComponents(adjacencyList)
      ├─ UpdateDataModel  → DataModelStore.Apply(surfaceId, patch)
      └─ DeleteSurface    → SurfaceHost.Dispose(surfaceId)
  → SurfaceHost rebuilds/patches its XAML subtree on the UI thread
  → DataModel changes notify bound components via INotifyPropertyChanged
```

Component identity is by **string ID**. A `LogicalTreeBuilder` keeps an `IDictionary<string, FrameworkElement>` per surface so `updateComponents` can mutate in place without rebuilding the entire tree (avoids flicker, preserves focus and scroll position).

## 4. Wire protocol

### 4.1 Inbound (agent → node)

Use the existing capability commands verbatim. No protocol change is required for this work.

```json
{ "version": "v0.8", "createSurface": { "surfaceId": "main", "catalogId": "https://a2ui.org/specification/v0_8/standard_catalog.json", "sendDataModel": true } }
{ "updateComponents": { "surfaceId": "main", "components": [ { "id": "root", "componentName": "Column", "properties": {...}, "children": ["title","actions"] }, ... ] } }
{ "updateDataModel": { "surfaceId": "main", "patch": { "/userName": "Scott" } } }
{ "deleteSurface": { "surfaceId": "main" } }
```

The renderer SHOULD validate each line against the v0.8 envelope schema before dispatch. The schema lives at `vendor/a2ui/specification/0.8/json/server_to_client.json` in the openclaw repo and should be vendored into `OpenClaw.Shared/Schemas/A2UI_v0_8/`.

Unknown envelope keys → log + skip (do not throw). Unknown component types → render an `A2UIUnknown` placeholder showing the type name and a warning glyph; never crash.

### 4.2 Outbound (node → agent)

When the user interacts with a surface, the renderer raises an A2UI **action** event. Action delivery rides whichever transport the node is currently connected through:

- Gateway-connected: serialize as the v0.8 client→server envelope and ship over the existing WebSocket via `_nodeClient`.
- MCP-only: emit as an MCP notification on a new `canvas/a2ui/action` channel (to be added to `CanvasCapability`).

Action payload shape (v0.8):

```json
{
  "action": {
    "name": "primary",
    "surfaceId": "main",
    "sourceComponentId": "btn_submit",
    "timestamp": "2026-04-25T18:32:11.123Z",
    "context": { "/email": "user@example.com" }
  }
}
```

`context` is the (possibly partial) data model snapshot relevant to the source component, computed by walking the component's `dataBinding` paths and the surface's `sendDataModel` flag.

## 5. Component mapping (v0.8 standard catalog)

| A2UI component | WinUI 3 control | Notes |
|---|---|---|
| **Containers** | | |
| `Row` | `StackPanel` (Horizontal) inside a wrap-aware `ItemsRepeater` when `wrap=true` | Match `bootstrap.js`'s wrap behavior at < 860 px |
| `Column` | `StackPanel` (Vertical) | `min-width: 0` analog: clamp via `MinWidth=0` |
| `List` | `ItemsRepeater` + `ItemsRepeaterScrollHost` | Virtualization on by default |
| `Card` | `Border` with `Microsoft.UI.Xaml.Media.MicaBackdrop`-aware brush + corner radius + drop shadow | |
| `Tabs` | `TabView` (controls) | Lightweight chrome to match Lit version |
| `Modal` | `ContentDialog` (or full-window overlay `Grid` w/ `AcrylicBrush`) | Track Lit's full-screen overlay style — `dialog::backdrop` analog is `AcrylicBrush` over the parent |
| **Display** | | |
| `Text` | `TextBlock` | Map A2UI `style` (h1/h2/body/caption/etc.) to Fluent type ramp |
| `Image` | `Image` w/ `BitmapImage` source; HTTP fetch via `HttpClient` with allowlist | Reject `file:`, `javascript:`, `data:` (except small `image/png|jpeg|webp`) |
| `Icon` | `FontIcon` (Segoe Fluent Icons) keyed by name | Maintain a name→glyph map; missing → outlined question-mark |
| `Video` | `MediaPlayerElement` | |
| `AudioPlayer` | `MediaPlayerElement` w/ audio-only template | |
| `Divider` | `Rectangle` (1px `SystemControlForegroundBaseLowBrush`) or `MenuFlyoutSeparator` style | |
| **Interactive** | | |
| `Button` | `Button` (variants → `AccentButtonStyle`, `DefaultButtonStyle`) | Triggers action with `name` |
| `CheckBox` | `CheckBox` | Two-way bind to data model path |
| `TextField` | `TextBox` (multiline → `TextBox.AcceptsReturn=true`) | `inputType` → `InputScope` mapping |
| `DateTimeInput` | `CalendarDatePicker` + `TimePicker` (composed) | |
| `ChoicePicker` | `ComboBox` (single) / `ListView` w/ `SelectionMode=Multiple` (multi) | |
| `Slider` | `Slider` | |

Each mapping lives in a single `IComponentRenderer` implementation under `OpenClaw.Tray.WinUI/A2UI/Renderers/`. The set is closed at compile time (catalog-strict) — no runtime registration in v1.

## 6. Data model & binding

A2UI surfaces carry a JSON data model. Components reference paths into that model (`"/userName"`, `"/items/0/title"`).

### 6.1 Storage

`DataModelStore` holds one `JsonObject` per surface, mutated via JSON Pointer (RFC 6901) patches. Use `System.Text.Json.Nodes` for in-place edits (already a dependency).

### 6.2 Binding

Bindings are **one-way for display** components, **two-way for interactive** components. Implement via:

- `DataModelObservable` — wraps a `JsonObject` and exposes `INotifyPropertyChanged` per registered path.
- `A2UIBinding` markup extension (or code-behind helpers) — produces `Binding` objects that target a path observer.

Why not raw `Microsoft.UI.Xaml.Data.Binding` paths? JSON paths can include array indices and slashes, which XAML binding paths don't model cleanly. A small adapter is simpler and faster than fighting the binding engine.

### 6.3 Patches

`updateDataModel.patch` is an object whose keys are JSON Pointer paths and whose values are replacement values. Apply atomically; coalesce notifications so multiple paths in one message produce a single render pass.

## 7. Action dispatch

Components that can produce actions register a callback:

```csharp
internal sealed class ButtonRenderer : IComponentRenderer
{
    public FrameworkElement Render(A2UIComponent c, RenderContext ctx)
    {
        var btn = new Button { Content = c.GetText("label") };
        btn.Click += (_, _) => ctx.Actions.Raise(new A2UIAction(
            name: c.GetString("actionName") ?? "press",
            surfaceId: ctx.SurfaceId,
            sourceComponentId: c.Id,
            context: ctx.DataModel.SnapshotFor(c)));
        return btn;
    }
}
```

`ActionDispatcher.Raise` is the single seam through which actions leave the renderer. It handles:

1. Throttle/debounce (per `name` + `sourceComponentId`) to suppress double-clicks.
2. Serialization to A2UI v0.8 client→server envelope.
3. Routing: gateway WS first, then MCP notification, with a fallback queue if neither is available.

## 8. Theming

Default to `XamlControlsResources` + Fluent theme colors. The `createSurface.theme` payload may override:

- `colors`: map onto `ThemeResource` overrides applied to the `SurfaceHost` resource scope (no global mutation).
- `typography`: optional font family override; respect Windows accessibility text scaling first.
- `radius`, `spacing`: passed through to renderers via `RenderContext`.

Theme application is local to the surface's visual tree — switching themes between surfaces does not flash the chrome.

## 9. Lifecycle & hosting

### 9.1 Window

`A2UICanvasWindow` extends `Window`:

- One window total (singleton). Multiple surfaces stack as `TabView` items if `>1` is active; single surface fills the content area.
- Title pulls from `createSurface.title` (new optional v0.8 field already used by openclaw) or defaults to "Canvas".
- Window chrome: backdrop = `MicaBackdrop` on Win11, `AcrylicBackdrop` fallback.
- Persistence: position/size remembered across sessions (per existing `CanvasWindow` settings keys; reuse where possible).

### 9.2 Threading

All renderer mutation runs on the UI dispatcher (`DispatcherQueue.GetForCurrentThread()`). The router accepts pushes from any thread and posts via `TryEnqueue`.

### 9.3 Reset

`canvas.a2ui.reset` (already wired through `A2UIResetRequested`) → `A2UIRouter.ResetAll()` → dispose every `SurfaceHost`, clear stores, re-show empty placeholder.

## 10. Security model

- **Catalog-strict.** Component types are baked in at compile time. There is no JS, no HTML escape, no `eval`. Unknown types render a placeholder.
- **URL allowlist for media.** `Image`, `Video`, `AudioPlayer` URL fetches go through a single `MediaResolver` that:
  - Allows `https://` from a configurable allowlist (default: empty until set by the agent's surface theme/manifest).
  - Allows `data:image/png|jpeg|webp` up to 2 MiB.
  - Rejects everything else; renders broken-image glyph.
- **Action context scoping.** `context` includes only data model paths the source component declares it reads (`dataBinding`), preventing accidental leak of unrelated form state.
- **No file system or process access** from inside a surface. Those go through other capabilities (`system.run`, `screen.*`) which already have their own approval flow.
- **Logging.** Each inbound message is logged at Info with surface ID + component count; PII fields in the data model SHOULD be redacted at log time using a path denylist (`/password`, `/secret*`, `/token`).

## 11. Telemetry

Mirror what `CanvasCapability` already logs:

- `a2ui.push` (count, jsonl byte length, surface IDs touched, render time ms)
- `a2ui.action` (surface ID, action name, queue latency)
- `a2ui.unknown_component` (type name) — to drive catalog upgrades
- `a2ui.media_blocked` (URL scheme/host) — to tune the allowlist

## 12. Testing

- **Unit:** schema validation, JSON pointer apply, action serialization, component-to-XAML mapping per type.
- **Visual regression:** golden images per component using WinAppDriver or a snapshot harness — gate on hash + tolerance.
- **Spec conformance:** drive the renderer with the official v0.8 conformance fixtures from `vendor/a2ui/specification/0.8/eval/` (reused from the openclaw monorepo) and assert action outputs match expected.
- **Stress:** 10k component surface, 1k updateComponents/sec → renderer must not block the UI thread > 16 ms p95.
- **Parity:** record the JSONL stream of an existing Lit-rendered openclaw surface, replay through the WinUI renderer, diff screenshots.

## 13. Phasing

| Phase | Scope | Exit criteria |
|---|---|---|
| **0 — Spike** | `Text`, `Column`, `Button` only; one surface; no data model | Single button click round-trips to agent |
| **1 — Catalog parity** | All v0.8 standard catalog types; data model + bindings; modal/tabs | Full conformance fixtures pass |
| **2 — Polish** | Theming, transitions, focus management, accessibility (Narrator), keyboard nav | A11y audit clean; UX review against Lit version |
| **3 — Coexistence** | Native window default; WebView2 path retained behind `--canvas=web` flag for parity testing | No regressions in WebView2 path |
| **4 — v0.9 migration** | Bidirectional messages, modular schemas, prompt-first | Tracks Google A2UI v0.9 release |

## 14. Open questions

> Resolved 2026-04-27 — see decisions below; previous wording preserved for context.

1. **Window count.** One A2UI window with tabs for multiple surfaces, or one window per surface? Lit version uses one host with multiple stacked surfaces.
   **Decision:** stay with the Lit-compatible single-window-with-tabs layout. Multiple windows is out of scope for v1.
2. **Component overrides.** Should we expose a hook for downstream apps to swap in custom renderers?
   **Decision:** stay catalog-strict for v1. No extension seam yet — easy to add later if a real customer asks.
3. **Theme negotiation.** Should the agent be told "I'm a native WinUI client, prefer Fluent tokens" via `clientCapabilities`?
   **Decision:** yes — advertise Fluent token preference in `clientCapabilities`. (Tracking task: wire this into the capability summary returned by `canvas.caps`.)
4. **Animation budget.** Define a small transition set (fade, slide) and apply automatically, or stay still?
   **Decision:** stay still until the agent asks. No automatic transitions in v1.
5. **Image caching.** Per-surface, per-process, or persistent?
   **Decision:** per-process LRU. Avoids the repeated-fetch cost of per-surface and the staleness risk of persistent disk caching.

## 15. References

- A2UI v0.8 spec: <https://a2ui.org/specification/v0.8-a2ui/>
- v0.8 JSON schemas (vendored): `openclaw/vendor/a2ui/specification/0.8/json/`
- Reference Lit renderer: `openclaw/vendor/a2ui/renderers/lit/`
- Current Windows node A2UI bridge: `src/OpenClaw.Tray.WinUI/Windows/CanvasWindow.xaml.cs` (`EnsureA2UIHostAsync`, `BuildA2UIMessageScript`)
- Current capability surface: `src/OpenClaw.Shared/Capabilities/CanvasCapability.cs`
- Android handler (good reference for v0.8 validation rules): `openclaw/apps/android/.../A2UIHandler.kt`
