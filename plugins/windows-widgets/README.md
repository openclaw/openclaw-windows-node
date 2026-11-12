# OpenClaw Windows Widgets proof of concept

This is an **OpenClaw Gateway plugin**, backed by a C# Windows widget provider
in Companion. It is not a PWA or a Windows DLL plugin.

The initial tracker answers this prompt:

> Create a widget that tells me if Mercury is in retrograde.
> Use https://mercuryretrogradeapi.com/.

The JSON API is polled, not streamed. Its `is_retrograde` field must be a
boolean; `true` displays Yes and `false` displays No. No API response text is
sent to a model or executed as instructions. The source is fixed for this proof
of concept. An HTTP rejection, malformed JSON, missing/non-boolean status,
timeout, or node failure is an error, never a negative Mercury result.
Requests reject redirects, require `application/json`, and retain the
15-second timeout and 256 KiB response limit. The public endpoint requires no
API key, but its uptime, rate limits, and calculation maintenance are not
verified production guarantees.

## Requirements and consent

- An OpenClaw host supporting plugin service scheduling, async keyed state, and
  `api.runtime.nodes.invoke`. The package declares OpenClaw 2026.10.6 or newer;
  later releases still require compatibility verification.
- A paired Windows node running this branch's Companion, with Node Mode enabled.
- Explicit opt-in before launching Companion:
  `$env:OPENCLAW_ENABLE_WIDGETS_POC = '1'`.
- Permission to display the requested information on an ambient Windows surface.
- Windows 11 and a **packaged** build of this Companion for Widgets Board
  registration. An ordinary unpackaged test app supports cache/MCP proof only.

The provider is included in Companion's package, so it does not need a separate
widget-provider installation after that package is installed or updated.
Installing only the OpenClaw plugin cannot register a Windows widget provider.
These commands do not update an installed Companion, install certificates,
enable Developer Mode, change Gateway policy, or pin widgets.

## Install and configure the plugin

Use the Gateway's own environment and filesystem, not the Windows path when
your Gateway is on a different host. Copy this plugin directory there first.

```powershell
openclaw plugins install --link .\plugins\windows-widgets
```

OpenClaw 2026.10.8 additionally requires explicit trust and capability consent
for this local source. Review the plugin first, then repeat the command with
`--force --accept-capabilities`. These flags do not approve Windows node pairing
or pin the widget.

Merge the following into the selected Gateway's configuration. Preserve existing
plugin and tool allowlists. Choose the exact paired Windows node ID, not its
display name or the first connected device.

```json
{
  "plugins": {
    "entries": {
      "windows-widgets": {
        "enabled": true,
        "config": {
          "nodeId": "<paired-windows-node-id>",
          "refreshMinutes": 60
        }
      }
    }
  },
  "tools": {
    "allow": ["windows_widgets"]
  }
}
```

If `plugins.allow` is configured, include `windows-widgets`. The Gateway must
also permit `widgets.list`, `widgets.upsert`, and `widgets.delete` for that node.
Approve any requested node capability reapproval through the normal workflow.
The plugin never modifies these permissions itself.

Inspect registration, then exercise the tool in an actual conversation:

```powershell
openclaw plugins inspect windows-widgets --runtime --json
```

Ask the prompt above. The agent should select `windows_widgets` with
`{"action":"create"}`. This enables refresh, fetches the source, persists its
observation, and invokes `widgets.upsert` on the configured Windows node.

Open **Win+W**, use the Windows widget picker to add **OpenClaw Tracker**, and
select **Mercury retrograde** if needed. If exactly one tracker exists, a newly
pinned instance binds automatically. Additional instances can select different
cached trackers; arbitrary source integrations are not part of this PoC.

**Windows owns pinning.** A successful tool call is not a claim that the card
was pinned or displayed. `host.supported: false` means cache-only.

## Prompt-designed native cards

Describe the result you want. The agent authors a full native Adaptive Card
template in `presentation.card`, not a theme name or a five-element mini-language.
Both Companion and the plugin must be updated. New requests reject the former
`theme`/`body` contract. Old saved PoC compositions migrate to native templates;
existing snapshots without presentation retain the simple title/status fallback.

For example:

> Restyle my Mercury widget using the Weather widget's Fluent hierarchy.
> Put a planet beside simple status text on a restrained dark mystic backdrop.
> Dim Mercury during actual retrograde and brighten it otherwise. On larger
> cards, show "Something else must be bumming you out" when not in retrograde.
> Keep the timestamp and source, and preserve the hourly refresh.

The shipped [mystic composition example](examples/fluent-mystic.json) is a
complete `presentation`, not a theme parameter. The agent can adapt it and use
`create` for a new tracker or `style` for an existing one. Supply selected or
generated PNGs through `artworkFiles` named `backdrop`, `planet-lit` and
`planet-muted`, or change those names to reuse saved artwork. The example
contains no bundled image files, generated-art claims or fixed asset inventory.

### Reuse the Mercury demo template

The [Mercury demo template](examples/mercury-demo.json) preserves the approved
plain Windows layout: **Is Mercury in Retrograde?** in the native header and
a large, centered status. The provider keeps the timestamp and centered
**Open source** hyperlink together at the bottom, without a divider. It needs
no artwork or dark background. Choose **Small** in the Widgets Board for the
compact demo; the template does not resize a pinned tile.

For the demo, ask:

> Use the bundled Mercury demo template for my widget. Keep the large centered
> status, bottom timestamp and source link, and hourly refresh.

The agent reads the file as a complete `presentation` and supplies it to
`windows_widgets`: use `style` for an existing tracker to preserve its source
observation, or `create` for a new tracker. This is a reusable example, not a
new template-name tool parameter or the default for unrelated designs. Do not
copy the provider-owned footer into the template.

The status follows the real Yes/No observation. The playful supporting sentence
appears only outside Small when the answer is No. Windows still owns typography
and header support; other sizes, themes and the negative state require their
own native visual checks.

### Fluent, weather-inspired composition

The Weather reference informs hierarchy, not a forecast feature: one
recognizable illustration beside a concise status, generous space and optional
supporting copy. Use an auto/stretch `ColumnSet` and native semantic typography
and spacing. Avoid stacked oversized headings, nested cards, decorative Accent
text, neon glows and invented forecast rows.

The example confines its dark atmospheric background to the hero `Container`
and uses semantic `Light` text there. The provider-owned observation/source
footer stays on the plain Windows surface, with the host's own text palette.
A root-level dark background would also sit behind that footer and can make
its default text unreadable in light mode. Keep copy in native `TextBlock`
elements instead of baking it into the artwork.

Small cards use the smaller planet and omit the longer supporting sentence.
Other sizes retain the same simple layout, rather than filling space with
extra metrics. The bright/dim artwork and status copy use actual `value`
bindings. Existing designs do not change automatically, and their source
observations and collection schedule are unchanged by a style request.
Native image loading, contrast, clipping, light/dark and accessibility behavior
still need Widgets Board proof; JSON admission is not visual validation.

### Native design surface and host limits

For an authoring inventory, call `windows_widgets` with
`{"action":"capabilities"}`. This works without tracker state or node access,
does not fetch the API, and does not change the current widget. It exposes the
inspected Designer families, bindings, allowed actions and explicit host limits.
The [toolbox reference](skills/windows-widgets/references/adaptive-card-toolbox.md)
records the inspected source and explains standard elements, newer charts,
badges, progress, paging, advanced layouts and usable fallback strategies.
The [responsive example](examples/responsive-status.json) demonstrates columns,
rich text, repetition, a Badge fallback and per-instance customization.
These additions improve agent authoring/discovery, not the Windows renderer.

There is no plugin element/property allowlist. Native card JSON passes through
to Windows: rich text, facts, tables, columns, image sets, backgrounds, spacing,
alignment, sizing, inputs and other host-supported Adaptive Card properties.
This is not a promise that every feature in the general Adaptive Cards schema
is implemented by every Widgets Board version. Windows owns expansion, schema
acceptance, fonts, palette, accessibility and rendering. Prefer semantic colors
and host sizing rather than pretending cards support arbitrary CSS or WinUI.
Use host-supported card versions `1.0` through `1.6`.

The template receives separate, provider-owned data:

| Binding | Owner |
| --- | --- |
| `title`, `value`, `detail`, `observedAt`, `freshness`, `sourceUrl` | Observed snapshot; never replaced by presentation data |
| `content` | The authored `presentation.data` object |
| `state` | Declared local interaction values for this pinned instance |
| `$host.widgetSize`, `$host.hostTheme` | Windows' current size and light/dark mode |
| `$host.isSettingsPayload` | Windows' customization mode |
| `$host.isHeaderSupported`, `$host.isHeader`, `$host.isUserContextAuthenticated` | Windows' actual host capabilities/context |

Use ordinary Adaptive Card templating, including `$when`, `$data`, expressions
and repeated data. Source observations and presentation data remain data, never
recursively evaluated as templates. Root `$data` and `$when` are reserved so an
authored card cannot rebind or hide the provider's observation controls.
Put those directives on body elements instead. `header` accepts a string,
`{text, iconUrl}` or `null`, subject to Windows host support.

Literal image/background/action URLs may use any public HTTPS DNS host, not
three hard-coded image sites. Queries and formats supported by the Windows
host are allowed. URL bindings must be a direct `${$root.content.path}` whose
value is validated, not a computed or context-relative expression. This avoids
changing the validated address inside `$data` loops. Credentials, custom ports,
IP/local hosts, local files and data URLs are rejected. Do not put secrets in
URLs or display data. The provider does not fetch images; Windows may make
external requests. These checks do not prove DNS/redirect containment in the
Windows image loader. Choose lawful public artwork with the user's agreement;
never invent an image URL or claim generated imagery without generating it.

### Agent-created artwork

The plugin can transfer artwork created or selected by OpenClaw, not just
choose built-in designs. First generate or obtain lawful PNG artwork on the
Gateway host using an available image tool or an explicitly selected source.
Get user agreement for the artwork and its transfer to the configured Windows
node. Supply `artworkFiles` with `create` or `style`, alongside the full card:

```json
{
  "action": "style",
  "artworkFiles": {
    "backdrop": "C:\\path\\to\\generated-space.png",
    "planet-lit": "C:\\path\\to\\mercury-bright.png",
    "planet-muted": "C:\\path\\to\\mercury-dim.png"
  },
  "presentation": {
    "card": {
      "type": "AdaptiveCard",
      "version": "1.5",
      "backgroundImage": "openclaw-asset:backdrop",
      "body": [
        {
          "type": "Image", "url": "openclaw-asset:planet-muted",
          "altText": "Mercury, dimmed to indicate retrograde",
          "$when": "${value == 'Yes'}"
        },
        {
          "type": "Image", "url": "openclaw-asset:planet-lit",
          "altText": "Mercury, bright to indicate no retrograde",
          "$when": "${value == 'No'}"
        }
      ]
    }
  }
}
```

Paths must be absolute on the Gateway host, not on the Windows node unless
they are the same host. The plugin reads only explicitly supplied regular PNG
files and encodes them as `presentation.assets[name].base64`. Advanced callers
can supply that assets map directly. Names are chosen for each design: there is
no built-in Mercury or space asset inventory. The native provider validates the
bytes and expands `openclaw-asset:<name>` in image/background/icon fields.
Direct content bindings to those references work too. Missing/invalid images
fail before plugin state changes or node delivery. Artwork persists with the
presentation through source refresh and Gateway/Companion restarts.
Style/create reuse saved artwork when `presentation.assets` is omitted; an
explicit empty assets object removes it. Supplied `artworkFiles` replaces its
named images while retaining the other saved images unless assets are explicitly
replaced. Tool responses return artwork names and byte counts, not base64,
so image data does not flood the agent's context.

Artwork is capped at 262144 decoded bytes total, with PNG headers bounded to
4096 pixels per side. These are admission checks, not a complete PNG decoder;
Windows owns final image decoding and card rendering. Caller-authored `data:`,
`file:` and package URLs remain rejected, and navigation/source links still
require public HTTPS. The provider never receives Gateway file paths.
Reduce artwork resolution/file size when a limit is exceeded. Expanded native
templates are separately capped at 1048576 UTF-8 bytes, including repeated
image uses. Artwork transfer removes the external image network dependency,
but actual native Board rendering still requires proof.

The preceding snippet demonstrates resource transfer and conditions, not the
recommended complete layout. Use the
[Fluent composition example](examples/fluent-mystic.json) for side-by-side
planet/status placement. Compose images and text using native card elements and data-driven conditions.
Adaptive Cards Image has no opacity property. For a fade effect, create two
versions of the same artwork and switch between them with `$when`, as above.
Do not fabricate observations to obtain the desired appearance. Mercury
brightness would be a symbolic retrograde indicator, not a physical phase.

The provider appends the observed title/value/timestamp and source action
outside the authored body. IDs beginning `openclaw-` are reserved, including
ToggleVisibility targets. Keep enough space for those controls at every size.
For compact text trackers, show one `Medium` status on Small and omit duplicate
headings and optional supporting copy. The provider groups compact metadata and
a centered source hyperlink at the bottom of the available content area,
without a divider or a gap between the timestamp and link. Authored actions
remain above that footer. Use the card's `header` string for a requested widget
title on hosts that support header customization.
Do not force card heights or add blank rows. Windows owns the tile dimensions:
choose Small in the Board to shorten a pinned tracker; styling a template does
not resize an existing Medium tile.
The policy gate bounds layout/data/state (excluding artwork bytes) to 65536 UTF-8 bytes, 256 JSON objects,
JSON depth 24 and 4096 bytes of interaction state; a whole snapshot is limited
to 524288 bytes including artwork. It is not a second implementation of Microsoft's card schema.
Native Board acceptance, image loading, clipping, every declared size/theme
and action behavior require current-build Windows proof.

### Native interactions and customization

`Action.OpenUrl` navigates to a public HTTPS URL. Native `Action.ShowCard` and
`Action.ToggleVisibility` are forwarded, but their behavior depends on the
Widgets Board host. Use `Action.Execute` for provider callbacks, not
`Action.Submit`. All Execute actions, including nested and select actions,
are automatically gated on `$host.isUserContextAuthenticated`.

Declare scalar per-instance state in `presentation.initialState`, for example
`{"count":0,"showDetail":true,"units":"C"}`, and bind it as `${state.count}`.
The provider implements these verbs:

| Verb | Action data and effect |
| --- | --- |
| `widget.set` | Input IDs match declared state keys. Submitted scalar values update only those keys; other fields are ignored. Toggle input `"true"`/`"false"` values convert to booleans. |
| `widget.increment` | `{"key":"count","amount":1}` increments a declared number. `amount` defaults to 1. |
| `widget.toggle` | `{"key":"showDetail"}` toggles a declared boolean. |
| `widget.reset` | Restores this instance's declared initial values. |
| `widget.refresh` | Renders cached data. It does not fetch the API or advance freshness. Automatic card `refresh.action` may use only this non-mutating verb. |

For example, add this to the authored card's `actions` array:

```json
{"type":"Action.Execute","title":"Count","verb":"widget.increment","data":{"key":"count"},"associatedInputs":"none"}
```

These are real provider callbacks and versioned Windows custom state, not
agent-generated C# or model calls. Each pinned instance restores independently.
Restyling retains compatible declared keys, initializes newly declared keys
and discards removed keys. `widget.set` cannot rewrite source facts, Gateway
configuration, credentials or other instances. Arbitrary verbs/node commands
are rejected, not executed.

The manifest declares customization support and the provider implements
`IWidgetProvider2`. Author settings content with `$host.isSettingsPayload` and
native inputs/actions; Windows owns entry/exit of its customization surface.
Native callback and rendering proof are separate from pure state-policy tests.

Styling preserves `observedAt` and the collection schedule. A failed styling
delivery keeps the previous design and reports `presentationError`, separately
from source `error`. Successful source collection/delivery clears both errors.
Scheduled refresh and Gateway restart retain the design. Create without
presentation preserves it; supplying presentation replaces the whole design.
Reset appearance by supplying a new minimal card, not a preset name.

### Deliberately separate SDK surfaces

HTML web widgets require a different content/resource/message execution path
and Microsoft currently limits them to the EEA. `metadata.webUrl` is therefore
explicitly rejected here, not silently advertised as working native content.
Card authentication configuration and generated executable code are also
outside this display/state capability. This implementation exposes native
card design, not every Windows App SDK API or arbitrary custom C# callbacks.

## Runtime and recovery

The Gateway owns the tracker and recurring API collection. A host-owned
service scheduler checks due work once per minute; the default fetch interval
is 60 minutes and configuration accepts 15-1440 minutes. No recurring model
call is needed. The tracker survives Gateway restarts in plugin-scoped keyed
state. Refreshes are serialized, failed attempts retain the last successful
snapshot, and the next scheduled attempt retries.

Companion is the only writer of local snapshots, under the selected profile's
`widgets` directory. Atomic file replacement protects the independent provider
reader. Snapshot IDs and payloads are bounded; older/conflicting observations
cannot replace newer ones. At the same observation time only presentation
may change; conflicting source facts are rejected. Snapshots contain display data and source links,
not source credentials or Gateway tokens.

Windows COM activation starts the same packaged executable with
`--widget-provider`, taking a separate headless MTA path before WinUI/Companion
startup. The provider reads snapshots, restores per-instance custom-state
bindings, handles resizing and selection actions, and checks active cards every
15 seconds. `OPENCLAW_WIDGET_POLL_SECONDS` accepts 5-300 seconds when present
in the provider process environment. No API fetch occurs in a rendering
callback. Closed/inactive cards do not trigger provider polling work.

The card always includes its observation timestamp and becomes visibly stale
after twice the configured refresh interval. Sleep, offline devices, a closed
Gateway, or a disconnected node do not guarantee timely refresh. Reopening the
board uses cached data and the next successful refresh replaces it.

Ask to stop the widget, or call `windows_widgets` with `{"action":"stop"}`.
Stopping persists disabled state before removing the Windows snapshot; it
does not unpin the Windows instance. If deletion fails while the node is
offline, retry stop after reconnecting. Disabling the environment opt-in alone
blocks new commands but does not erase already cached/displayed content.

## Development proof

Run the repository-required build, Shared tests and Tray tests, plus:

```powershell
node --test .\plugins\windows-widgets\mercury.test.mjs
dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj
```

With an isolated current-build Companion, local MCP enabled, and the opt-in set:

```powershell
winnode --list-tools
winnode --command widgets.list
```

See the shipped [command reference](../../.agents/skills/winnode/SKILL.md) for
`widgets.upsert` and `widgets.delete`, including the full snapshot shape.
Local MCP proof does not establish Gateway pairing, actual agent tool selection,
package registration, or visible Widgets Board rendering.

On 2026-10-27, this uncommitted PoC worktree was exercised against an isolated
OpenClaw 2026.10.6 Gateway and its normally approved Windows node. The real plugin
loaded, its refresh service started, and direct Gateway tool invocation passed
create/status/refresh/stop using the live Mercury website. Companion persisted
the delivered observation, refresh advanced its timestamp, and stop removed it.
This proves plugin-to-Gateway-to-node delivery, not model tool selection or
native Board rendering. It does not establish compatibility with later releases.

On 2026-10-28, a separately registered ARM64 test package from this uncommitted
worktree completed the live plugin create/refresh path through its approved
Windows node. The native host reported `supported: true` and one pinned instance;
the developer confirmed the Mercury result and timestamp were visible in
Widgets Board. That run also exposed and fixed null native inventory handling
before the first pin. This is one happy-path instance, not proof of model tool
selection, two-instance selection, every size, or restart/stale/error rendering.

After rebasing this uncommitted worktree onto `65c7ddba`, the 2026-10-28 replay
used the task-owned native Gateway patch `w5338-widget`, following the
[named-patch workflow](../../.agents/skills/native-gateway-from-source/SKILL.md#default-multi-instance-native-gateway-testing).
The registered family was
`OpenClawFoundation.OpenClawGateway-w5338-widget_rfcbke2p71se2`, with packaging
commit `a3b62dd70e0510b8c7fe0373054300bb9ea2740c` and OpenClaw 2026.10.8 payload
commit `dc2523e5a5c7a309517fd7fa834add938231d526`. Native status confirmed
`isolated-session`, `running`, port `28766`, and non-null listener ownership.
Plugin create/refresh through the approved packaged Windows node passed, and
the developer confirmed the visible card showed Yes with observation
`2026-10-28 17:56:29Z`. Card timestamps are UTC. This extends happy-path proof
to that native isolated-session payload, not model tool selection or the
remaining native lifecycle and rendering cases.

Required real-native proof is the packaged current-build provider appearing in
the picker, a visible Mercury card, selection in two instances, resize,
restart/recovery, and stale/error behavior. Use a task-owned package/desktop;
do not replace another session's installed Dev Companion to obtain evidence.
The picker PNG is a generated illustration, not a runtime screenshot.

## Scope and distribution limits

This is opt-in proof-of-concept code, not a published plugin or verified Store
release. It supports one Mercury tracker per plugin installation and a single
explicit Windows target. It does not expose arbitrary HTML, C#, scripts,
tracking source URLs, or general-purpose action execution. Public card artwork
and navigation URLs are separate from tracking integrations. Production work should add
a reviewed permissions/settings surface and Gateway-scoped tracker ownership
before multi-Gateway or multi-user deployment.

References: [C# widget provider](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs),
[OpenClaw plugin authoring](https://docs.openclaw.ai/plugins/building-plugins),
[plugin node invocation and scheduling](https://docs.openclaw.ai/plugins/sdk-runtime/gateway-and-nodes).
