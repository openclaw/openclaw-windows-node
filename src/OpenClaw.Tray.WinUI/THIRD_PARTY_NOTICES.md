# Third-party notices

## Tool input syntax highlighting

`Chat/ChatToolInputSyntax.cs` uses **ColorCode.Core 2.0.15** to tokenize
PowerShell and JSON display text. Rendering and theme resources are native
Windows-owned code; input is never executed.

- Source: <https://github.com/CommunityToolkit/ColorCode-Universal/tree/e6c2701c365a7a91d74d7ef38a6b60075008ce94>
- Copyright (c) .NET Foundation and Contributors. All rights reserved.
- License: MIT

```
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
FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Sidebar icon attribution

The colorful sidebar SVG files in `Assets/SidebarIcons/` are derived from the
**Fluent UI System Icons** project by Microsoft, color variant
(selected icons from the full `fluent-color` set in the Iconify ecosystem).

`LocalAi.svg` is a custom color adaptation of Fluent's
`developer-board-24-filled`, not an official `fluent-color` variant. It retains
the chip silhouette and pins, removes the circular center detail, and adds a
solid blue/teal gradient body with metallic pins. This is the Local AI sidebar
icon, rendered through `SvgImageSource` / `ImageIcon` like the other sidebar
assets. The existing monochrome glyph catalog still applies to other surfaces.

`Model.svg` adapts the official Fluent `cube-24-filled` silhouette with this
app's existing purple model-icon gradient. Its geometry comes from
[Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons/blob/e15fc0de25fab983e3e42bac8798be69a85c23bf/assets/Cube/SVG/ic_fluent_cube_24_filled.svg).
It is a custom color adaptation, not an official `fluent-color` variant.

`Notifications.svg` is a custom bell drawn in the `fluent-color` gradient style
for the app notifications surface (#748), not an official `fluent-color`
variant. The asset is retained from the former Notifications sidebar entry;
notification preferences now live inside General.

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
| `Instances.svg` | `phone-laptop-24` |
| `Agents.svg` | `bot-sparkle-24` |
| `Config.svg` | `options-24` |
| `Usage.svg` | `data-bar-vertical-ascending-24` |
| `Cron.svg` | `calendar-clock-24` |
| `LocalAi.svg` | `developer-board-24-filled` (custom colors and solid center) |
| `Model.svg` | `cube-24-filled` (existing purple gradient) |
| `Voice.svg` | `mic-24` |
| `Settings.svg` | `settings-24` |
| `Permissions.svg` | `lock-shield-24` |
| `Sandbox.svg` | `shield-24` |
| `Activity.svg` | `data-trending-24` |
| `Notifications.svg` | custom bell in the `fluent-color` style (see above) |
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

## Chat pending queue icons

`Chat/ReactorChatPendingMessages.cs` adapts the list/return and trash geometry
and queue proportions from OpenClaw commit
`fe12d0af7389290693346e816bc345e756168115`:

- [Queue waiting icon](https://github.com/openclaw/openclaw/blob/fe12d0af7389290693346e816bc345e756168115/ui/src/pages/chat/components/chat-composer-queue.tsx)
- [Trash icon](https://github.com/openclaw/openclaw/blob/fe12d0af7389290693346e816bc345e756168115/ui/src/components/icon-data-tools.ts)
- [Queue styling](https://github.com/openclaw/openclaw/blob/fe12d0af7389290693346e816bc345e756168115/ui/src/styles/chat/composer-queue-content.css)

The paths render as native WinUI geometry. No web script or runtime asset fetch
is used. The OpenClaw MIT, Feather MIT and Lucide ISC notices below apply.

## Chat working claw and question presentation

`Chat/ChatWorkingClawMotion.cs`, `Chat/NativeWorkingClaw.cs` and
`Chat/ReactorRunProgress.cs` port the filled claw geometry, timing and keyframes
from OpenClaw commit `db2d951fd047a9e6a03b73d41c8e5757506dedf9`:

- [SVG paths](https://github.com/openclaw/openclaw/blob/db2d951fd047a9e6a03b73d41c8e5757506dedf9/ui/src/components/icon-data-tools.ts)
- [Base keyframes](https://github.com/openclaw/openclaw/blob/db2d951fd047a9e6a03b73d41c8e5757506dedf9/ui/src/styles/components.css)
- [Working indicator and surprise keyframes](https://github.com/openclaw/openclaw/blob/db2d951fd047a9e6a03b73d41c8e5757506dedf9/ui/src/styles/chat/working-indicator.css)
- [Seeded surprise selection](https://github.com/openclaw/openclaw/blob/db2d951fd047a9e6a03b73d41c8e5757506dedf9/ui/src/pages/chat/components/chat-working-indicator-surprise.ts)

`Chat/ReactorQuestionPanel.cs` and `Themes/ChatResources.xaml` adapt the
[question card presentation](https://github.com/openclaw/openclaw/blob/db2d951fd047a9e6a03b73d41c8e5757506dedf9/ui/src/styles/chat/question-card.css),
[answer controls](https://github.com/openclaw/openclaw/blob/db2d951fd047a9e6a03b73d41c8e5757506dedf9/ui/src/pages/chat/components/chat-question-answer-controls.tsx)
and [palette](https://github.com/openclaw/openclaw/blob/db2d951fd047a9e6a03b73d41c8e5757506dedf9/ui/src/styles/base.css)
to native WinUI controls. No web script is executed at runtime. The OpenClaw MIT
notice below applies to these adaptations.

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
