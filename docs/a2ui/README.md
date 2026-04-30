# A2UI v0.8 — Overview & Implementation Grading

This folder is the entry point for everything A2UI in this repo. It captures
the v0.8 specification, the standard catalog, and a side-by-side grading of
two implementations:

- **Lit reference** in `C:\Users\andersonch\Code\openclaw` (web components,
  rendered in a browser via the OpenClaw canvas host).
- **Native WinUI** in this repo (`src/OpenClaw.Tray.WinUI/A2UI/`,
  branch `feat/a2ui-native-winui`).

The native WinUI design doc that predates this overview lives at
[`../A2UI_NATIVE_WINUI.md`](../A2UI_NATIVE_WINUI.md); this folder
supersedes the parts of that doc that describe the spec and adds the
grading.

## Contents

| Doc | What's in it |
| --- | --- |
| [`protocol.md`](./protocol.md) | Wire protocol — envelopes, JSONL, A2A extension, capability negotiation, lifecycle |
| [`components.md`](./components.md) | Standard catalog — every component, every property, type, enum, behavior |
| [`data-and-actions.md`](./data-and-actions.md) | A2UIValue tagged union, data model & paths, action dispatch, security |
| [`grading.md`](./grading.md) | Side-by-side scoring of Lit vs WinUI vs spec, with file:line citations |

## Spec source of truth

| Document | URL |
| --- | --- |
| Protocol v0.8 | https://a2ui.org/specification/v0.8-a2ui/ |
| A2A extension v0.8 | https://a2ui.org/specification/v0.8-a2a-extension/ |
| Standard catalog (JSON) | https://a2ui.org/specification/v0_8/standard_catalog_definition.json |
| Source / schemas | https://github.com/google/A2UI |
| Evolution v0.8 → v0.9 | https://a2ui.org/specification/v0.9-evolution-guide/ |

These pages were captured 2026-04-27. v0.8 is the **stable / public preview**
release; v0.9 exists as a draft.

## TL;DR — how the two implementations stack up

| Area | Lit (OpenClaw) | WinUI (this repo) | Spec |
| --- | --- | --- | --- |
| Component coverage | 18/18 | 18/18 | 18 in standard catalog |
| Component property completeness | ~85% (4 documented TODOs) | ~95% | — |
| Streaming / JSONL parser | Per-line, lenient | Per-line, lenient + size caps | line-delimited JSON |
| Data model paths | Custom JSON-pointer-ish + auto-parse | Strict RFC 6901 | Path strings, format underspecified |
| Action transport | DOM `CustomEvent` bubbling | Debounced dispatcher → gateway via `agent.request` | Client-to-server A2A `userAction` |
| Bi-directional binding | ✓ via `processor.setData` | ✓ via `DataModelStore.Write` | Spec is silent — both impls add it |
| Markdown in `Text` | ✓ (sandboxed iframe for HTML, escaped code) | ✗ (plain text only) | Spec is silent |
| Modal | `<dialog>` w/ `showModal()` | `ContentDialog` (native) | Spec leaves shape open |
| List virtualization | ✗ (StackPanel-style, all-at-once) | ✓ `ItemsRepeater` + cached child template | Spec calls for it |
| URL safety / SSRF | None — passes URLs through to `<img>`/`<video>` | HTTPS+allowlist for `Image`/`Video`/`AudioPlayer`; DNS-rebinding pin via `SocketsHttpHandler.ConnectCallback` on `Image` only — `Video`/`AudioPlayer` hand the URI to `MediaSource.CreateFromUri`, which re-resolves at playback | Spec is silent (deferred) |
| Secret redaction | ✗ | ✓ denylist (`password`, `secret`, `token`) + registered paths | Spec is silent |
| Action context scoping | Caller's responsibility | Explicit `dataBinding` + implicit walk + secret filter | Spec defines `context[]` only |
| Test coverage | One model unit test; no per-component | Render matrix, scale test, security tests, integration smoke | — |

The detailed scorecard with deductions per category is in
[`grading.md`](./grading.md).

## How to use this folder

- If you're **adding a renderer** for a component: read
  [`components.md`](./components.md) for the spec'd properties, then
  [`grading.md`](./grading.md) for known WinUI gaps.
- If you're **wiring a transport** (gateway, MCP bridge, etc.): read
  [`protocol.md`](./protocol.md) and the `data-and-actions.md` action
  section.
- If you're **reviewing a PR that touches A2UI**: skim
  [`grading.md#known-deviations-by-category`](./grading.md#known-deviations-by-category)
  to see which deviations are intentional (good) vs known gaps (bad).
