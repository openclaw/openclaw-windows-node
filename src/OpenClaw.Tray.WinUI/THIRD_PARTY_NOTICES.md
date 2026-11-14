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

## Gateway theme typefaces

`Assets/Fonts/` bundles the typefaces that the OpenClaw Gateway's built-in
themes use, so a selected theme renders with its own fonts. The files are the
unmodified roman TrueType builds from google/fonts, renamed without axis
brackets by `scripts/Update-ThemeFonts.ps1`. Each family is licensed under
the SIL Open Font License 1.1, reproduced once below.

- Source: <https://github.com/google/fonts/tree/0fb1bdc740dd9d495e639a160778db34c28807b7/ofl>
- Instrument Sans: Copyright 2022 The Instrument Sans Project Authors (https://github.com/Instrument/instrument-sans)
- Geist: Copyright 2024 The Geist Project Authors (https://github.com/vercel/geist-font.git)
- DM Sans: Copyright 2014 The DM Sans Project Authors (https://github.com/googlefonts/dm-fonts)
- IBM Plex Sans: Copyright © 2017 IBM Corp. with Reserved Font Name "Plex"
- Space Grotesk: Copyright 2020 The Space Grotesk Project Authors (https://github.com/floriankarsten/space-grotesk)
- Atkinson Hyperlegible Next: Copyright 2020-2024 The Atkinson Hyperlegible Next Project Authors (https://github.com/googlefonts/atkinson-hyperlegible-next)
- Fraunces: Copyright 2018 The Fraunces Project Authors (https://github.com/undercasetype/Fraunces)
- Lora: Copyright 2011 The Lora Project Authors (https://github.com/cyrealtype/Lora-Cyrillic), with Reserved Font Name "Lora".
- JetBrains Mono: Copyright 2020 The JetBrains Mono Project Authors (https://github.com/JetBrains/JetBrainsMono)

```
This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
https://openfontlicense.org


-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```
