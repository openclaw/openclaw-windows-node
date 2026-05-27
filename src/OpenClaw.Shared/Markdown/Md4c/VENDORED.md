# Vendored: md4c SAX parser engine

The C# files in this directory are vendored from the
[microsoft-ui-reactor](https://github.com/microsoft/microsoft-ui-reactor) repository,
specifically from `src/Reactor/Markdown/`. They are a faithful C# port (by Chris
Lovett) of [Martin Mitáš's md4c](https://github.com/mity/md4c) CommonMark/GFM
parser.

## What was vendored

| File | Upstream path |
|---|---|
| `MarkdownEnums.cs`        | `src/Reactor/Markdown/MarkdownEnums.cs` |
| `MarkdownTypes.cs`        | `src/Reactor/Markdown/MarkdownTypes.cs` |
| `Md4cParser.cs`           | `src/Reactor/Markdown/Md4cParser.cs` |
| `Md4cParser.Block.cs`     | `src/Reactor/Markdown/Md4cParser.Block.cs` |
| `Md4cParser.Inline.cs`    | `src/Reactor/Markdown/Md4cParser.Inline.cs` |
| `Md4cEntity.cs`           | `src/Reactor/Markdown/Md4cEntity.cs` |
| `Md4cUnicode.cs`          | `src/Reactor/Markdown/Md4cUnicode.cs` |

What was **not** vendored: `MarkdownBuilder.cs` (Reactor-specific Element/Factory
output), `MarkdownHtml.cs` (HTML renderer not needed in tray). We ship a chat-
focused renderer in `src/OpenClaw.Tray.WinUI/Chat/Markdown/ChatMarkdownRenderer.cs`
that targets `OpenClawTray.FunctionalUI` Elements instead.

## Upstream revision

- Repo:   `microsoft/microsoft-ui-reactor`
- Commit: `01bb3fbcdc21e20db48aa9b6aaf3f70b651de919`
- Branch: tip of `main` at vendor time

## Local modifications

- The namespace was changed from `Microsoft.UI.Reactor.Markdown` to
  `OpenClaw.Shared.Markdown.Md4c`. No other changes.

## Re-syncing

To re-sync from a newer upstream commit:

1. `git -C path/to/microsoft-ui-reactor pull`
2. Copy the seven files listed above into this directory, overwriting locals.
3. Re-apply the namespace rename (`Microsoft.UI.Reactor.Markdown` →
   `OpenClaw.Shared.Markdown.Md4c`).
4. Update the commit SHA above.
5. Run shared + tray tests.

If upstream gains new files (e.g. an extension), prefer extending this
list rather than reaching back into Reactor's project layout.
