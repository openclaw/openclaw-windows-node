# Third-party icon attribution

The colorful sidebar SVG files in `Assets/SidebarIcons/` are derived from the
**Fluent UI System Icons** project by Microsoft, color variant
(selected icons from the full `fluent-color` set in the Iconify ecosystem).

`LocalAi.svg` is a custom color adaptation of Fluent's
`developer-board-24-filled`, not an official `fluent-color` variant. It retains
the chip silhouette and pins, removes the circular center detail, and adds a
solid blue/teal gradient body with metallic pins. This is the Local AI sidebar
icon, rendered through `SvgImageSource` / `ImageIcon` like the other sidebar
assets. The existing monochrome glyph catalog still applies to other surfaces.

- Upstream repositories:
  - <https://github.com/microsoft/fluentui-system-icons> (sidebar icons)
- License: MIT
- Attribution: not required by MIT; this file documents derivation for compliance.

## Mapping (sidebar item → upstream icon name)

| File | Upstream icon |
|---|---|
| `Chat.svg` | `chat-24` |
| `Connection.svg` | `globe-24` |
| `Sessions.svg` | `chat-multiple-24` |
| `Skills.svg` | `toolbox-24` |
| `Channels.svg` | `molecule-24` |
| `Instances.svg` | `phone-laptop-24` |
| `Advanced.svg` | `org-24` |
| `AgentEvents.svg` | `list-bar-24` |
| `Agents.svg` | `bot-sparkle-24` |
| `Bindings.svg` | `link-multiple-24` |
| `Config.svg` | `options-24` |
| `Usage.svg` | `data-bar-vertical-ascending-24` |
| `Cron.svg` | `calendar-clock-24` |
| `LocalAi.svg` | `developer-board-24-filled` (custom colors and solid center) |
| `Voice.svg` | `mic-24` |
| `Settings.svg` | `settings-24` |
| `Permissions.svg` | `lock-shield-24` |
| `Sandbox.svg` | `shield-24` |
| `Activity.svg` | `data-trending-24` |
| `Debug.svg` | `wrench-screwdriver-24` |
| `Info.svg` | `book-open-lightbulb-24` |

## Upstream license

```
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Chat provider display names

`ChatModelLabels.FormatProviderName` follows the provider branding and
separator-based fallback in OpenClaw's published Control UI:

- <https://github.com/openclaw/openclaw/blob/3a9d69db306cd7f081e06254cb89c4bcc14a7107/ui/src/components/provider-icon.ts>

The OpenClaw MIT notice below also applies to this mapping.

## Chat effort gauge

`Chat/ChatEffortGauge.cs` adapts the inline SVG geometry and needle-position
calculation from OpenClaw's published Control UI at commit
`3a9d69db306cd7f081e06254cb89c4bcc14a7107`:

- <https://github.com/openclaw/openclaw/blob/3a9d69db306cd7f081e06254cb89c4bcc14a7107/ui/src/pages/chat/components/chat-effort-picker.ts>
- <https://github.com/openclaw/openclaw/blob/3a9d69db306cd7f081e06254cb89c4bcc14a7107/ui/src/components/icons-tools.ts>

The vector is rendered with native WinUI shapes. OpenClaw identifies the shared
stroke-icon shell as Lucide; both notices are retained here.

### OpenClaw (MIT)

Copyright (c) 2026 OpenClaw Foundation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

### Lucide (ISC)

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of
Feather (MIT). All other copyright (c) for Lucide are held by Lucide Contributors 2022.

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
