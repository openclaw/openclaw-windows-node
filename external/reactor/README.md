# Vendored Microsoft.UI.Reactor

This directory contains a vendored snapshot of [`microsoft/microsoft-ui-reactor`](https://github.com/microsoft/microsoft-ui-reactor) — the declarative WinUI framework used by `OpenClaw.Tray.WinUI` to render the native chat UI (replacing the previous WebView2-hosted gateway web client).

The chat-specific projects (`OpenClaw.Chat.Model` / `OpenClaw.Chat.UI`) originally started as the upstream `samples/apps/chat/` projects but have since been adopted into our own source tree under `src/` and are no longer treated as samples.

## Why vendored?

`Microsoft.UI.Reactor` is not published to public NuGet, and the upstream repository is internal. Vendoring keeps the build hermetic (no internal feeds required) and lets CI build offline.

## Provenance

- Upstream: <https://github.com/microsoft/microsoft-ui-reactor>
- Snapshot date: 2026-05-05
- License: MIT (see `LICENSE`)

## Layout

```
external/reactor/
  src/
    Reactor/                       Core declarative UI framework
    Reactor.Analyzers/             Roslyn analyzers (netstandard2.0, bundled)
    Reactor.Localization.Generator/  Source generator for .resw → strongly-typed accessors
  Directory.Build.props            (vendored from upstream)
  Directory.Build.targets          (vendored from upstream)
```

The chat data + UI projects live in our own `src/` tree:

```
src/
  OpenClaw.Chat.Model/             Provider-neutral chat state, reducer, IChatDataProvider
  OpenClaw.Chat.UI/                Reactor chat components (SessionHeader, etc.)
```

## Local edits

A minimal set of edits has been applied so the vendored projects build cleanly inside this repo:

- **TFM**: `Reactor.csproj` bumped from `net9.0-windows10.0.22621.0` to `net10.0-windows10.0.22621.0` to match the rest of this repository (which targets net10).
- **`MarkdownBuilder.LeaveLink` LinkBuilder hook wired** (`src/Reactor/Markdown/MarkdownBuilder.cs`): the upstream snapshot declared `MarkdownOptions.LinkBuilder` (TASK-048) but never invoked it — `LeaveLink` always emitted a clickable `RichTextHyperlink`. The local edit (a) hands the captured inline elements + URI to `LinkBuilder` when set, and (b) tightens the callback type from `Func<Element[], Uri, Element>` to `Func<RichTextInline[], Uri, RichTextInline>` so the override slots into the inline run buffer correctly. OpenClawTray uses this hook to render untrusted assistant Markdown links as inert `RichTextRun` plain text.

When refreshing from upstream, re-apply these edits.

## Refreshing from upstream

```powershell
# 1. Pull a fresh clone of the upstream repo somewhere outside this tree.
git clone https://github.com/microsoft/microsoft-ui-reactor.git D:\reactor-chat\reactor-chat

# 2. Mirror src/Reactor*, Directory.Build.{props,targets}, LICENSE into
#    external/reactor/. bin/obj are stripped.

# 3. Re-apply the TFM bump in src/Reactor/Reactor.csproj
#    (net9.0-windows10.0.22621.0 → net10.0-windows10.0.22621.0).

# 4. dotnet restore && ./build.ps1
```

The `OpenClaw.Chat.Model` / `OpenClaw.Chat.UI` projects in `src/` are NOT refreshed from upstream automatically — they are part of our codebase and may diverge freely.
