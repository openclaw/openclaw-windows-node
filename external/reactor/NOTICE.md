# Third-party notices for vendored components

## Microsoft.UI.Reactor

- Source: <https://github.com/microsoft/microsoft-ui-reactor>
- Copyright (c) Microsoft Corporation.
- License: MIT (see [`LICENSE`](LICENSE))

This product includes a vendored snapshot of `microsoft/microsoft-ui-reactor`
(the `Reactor`, `Reactor.Analyzers`, `Reactor.Localization.Generator` projects).
Used under the terms of the MIT license.

The `OpenClaw.Chat.Model` and `OpenClaw.Chat.UI` projects under `src/` were
originally derived from the upstream `samples/apps/chat/` projects and have
since been adopted into our own source tree under the same MIT terms.

- **`MarkdownBuilder.LeaveLink` LinkBuilder hook wired** (in
  `src/Reactor/Markdown/MarkdownBuilder.cs`): the upstream snapshot declared
  `MarkdownOptions.LinkBuilder` (TASK-048) but never invoked it. The local
  edit invokes the hook when set and tightens the callback type to
  `Func<RichTextInline[], Uri, RichTextInline>`. See
  `external/reactor/README.md` § *Local edits* for rationale.

The MIT license text accompanying the upstream sources is preserved verbatim
at `external/reactor/LICENSE`.
