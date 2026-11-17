# OpenClaw Windows Widgets proof of concept

This is an **OpenClaw Gateway plugin**, backed by a C# Windows widget provider
in Companion. It is not a PWA or a Windows DLL plugin.

The bridge separates source collection, normalized observations, card authoring
and native deployment. New widgets should use independent IDs and bot-pushed
snapshots; autonomous polling is not needed for this workflow. The
original Mercury tracker remains available without an ID for compatibility.

## Recommended: bot-pushed widgets

Use the existing Windows provider as a publishing target, not a new app or
custom renderer for each request. The bot collects authorized observations
using its available tools, authors an Adaptive Card, then calls:

```json
{
  "action": "publish",
  "id": "project-status",
  "snapshot": {
    "title": "Project status",
    "value": "Ready",
    "sourceUrl": "https://example.com/project",
    "observedAt": "2026-01-01T12:00:00Z",
    "presentation": {
      "card": {
        "type": "AdaptiveCard",
        "version": "1.6",
        "body": [{ "type": "TextBlock", "text": "${value}", "wrap": true }]
      }
    }
  }
}
```

The timestamp and values above illustrate the format, not a live observation.
Repeat `publish` with the same ID and a complete new snapshot for updates.
Publication never fetches a source, starts a schedule or invokes lighting.
Without new pushes, Windows retains the last cached observation and marks it
stale according to its age. A bot job may publish with separate authorization;
neither bot scheduling nor Windows-side polling is created here.

For collected history, supply `snapshot.series` instead of `value`/`observedAt`:

```json
{
  "action": "publish",
  "id": "sensor-history",
  "snapshot": {
    "title": "Synthetic sensor history",
    "sourceUrl": "https://example.com/observations.json",
    "detail": "Synthetic example. Not for treatment decisions.",
    "series": {
      "units": "mg/dL",
      "points": [
        { "x": "2026-01-01T11:55:00Z", "y": 90 },
        { "x": "2026-01-01T12:00:00Z", "y": 108 }
      ],
      "windowMinutes": 240,
      "gapMinutes": 15
    }
  }
}
```

Supply 1-96 actual points already in the declared units. The bridge sorts and
deduplicates timestamps, rejects conflicts, derives latest value/time and
generates the existing gap-aware PNG plus native text fallback. The window
ends at the newest sample; older points outside it are omitted. Optional
`value`/`observedAt` must match the derived facts. No source collection, unit
conversion or medical interpretation occurs. For authorized Nightscout SGV
data, map `date` milliseconds to ISO `x` and `sgv` to numeric `y` in `mg/dL`.
No personal endpoint is bundled or fetched by this example.

Custom cards bind `content.source.graphUrl`, `graphAlt` and `rangeLabel`;
series reserves `content.source` and asset `source-series`. Restyling retains
the graph and observation time. For other artwork, top-level `artworkFiles`
works with `publish` and `snapshot.presentation`, as well as `create`/`style`.
Publication replaces the snapshot, so include needed data, presentation and
assets on each push.

Read the [publishing skill](skills/windows-widgets/SKILL.md) and
[Microsoft Learn adaptation](skills/windows-widgets/references/microsoft-widget-guidance.md)
for responsive layouts, authenticated actions, provider lifecycle, packaging
and honest Board verification. Feed providers are a separate EEA-only preview
web-dashboard feature, not a replacement JSON collector for pinned cards.

## Existing Mercury compatibility

The original tracker answers this prompt:

> Create a widget that tells me if Mercury is in retrograde.
> Use https://mercuryretrogradeapi.com/.

The JSON API is polled, not streamed. Its `is_retrograde` field must be a
boolean; `true` displays Yes and `false` displays No. No API response text is
sent to a model or executed as instructions. This legacy route's source is fixed.
An HTTP rejection, malformed JSON, missing/non-boolean status,
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

### Keep private configuration local

Source endpoints, credentials, collected readings, personal cards, screenshots
and deployment profiles do not belong in this repository or its examples.
Keep them in the selected Gateway/Companion profile or outside the checkout.
If checkout-local configuration is necessary, use the ignored `local/`
directory or `*.local.json` files in this plugin directory. Ignore rules are
an accident-prevention measure, not a privacy boundary or permission to force-add
private files.

The package uses an explicit file list for runtime modules, guidance and
reusable examples. Local configuration and capture output must never be added
to that list. Check `npm pack --dry-run --json --ignore-scripts` from this plugin
directory before distributing it. Custom-feed examples use reserved
`example.com` addresses and synthetic observations, not personal endpoints.

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
          "refreshMinutes": 60,
          "allowedSourceUrls": []
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

For the bot-pushed workflow, ask the agent to read the skill and use `publish`
with a complete snapshot. If it only discovers Mercury actions or no `publish`,
the deployed plugin is stale: updating this checkout does not update a running
Gateway. Refresh only the authorized deployment and verify live tool discovery.

For Mercury compatibility, ask its prompt above. The agent should select `windows_widgets` with
`{"action":"create"}`. This enables refresh, fetches the source, persists its
observation, and invokes `widgets.upsert` on the configured Windows node.

Open **Win+W**, use the Windows widget picker to add **OpenClaw Tracker**, and
select the intended tracker if needed. If exactly one tracker exists, a newly
pinned instance binds automatically. Additional instances can select different
cached trackers, including the independent source integrations described below.

**Windows owns pinning.** A successful tool call is not a claim that the card
was pinned or displayed. `host.supported: false` means cache-only.

## Create another source-backed widget

The existing opt-in polling route uses `id`, `title` and a declarative `source`.
It is not needed for bot-pushed widgets and must not be enabled implicitly.
An administrator must first add the **exact, credential-free public HTTPS URL**
to `plugins.entries.windows-widgets.config.allowedSourceUrls`. This is separate
from agent arguments and Windows display consent. Do not approve sources you
are not authorized to collect or show to other people. Query strings and
subdomains may themselves identify a person or contain secrets.

For example, after approving `https://example.com/observations.json`, a source
returning `{"active":false,"sampledAt":"2026-01-01T12:00:00Z"}` can use:

```json
{
  "action": "create",
  "id": "source-status",
  "title": "Source status",
  "refreshMinutes": 5,
  "staleAfterMinutes": 15,
  "source": {
    "kind": "json",
    "url": "https://example.com/observations.json",
    "valuePointer": "/active",
    "observedAtPointer": "/sampledAt",
    "valueMap": { "true": "Active", "false": "Inactive" }
  }
}
```

`valuePointer` selects a non-null scalar. Optional `valueMap` maps every
permitted raw value to literal display copy; unmapped values fail rather than
guessing. Optional `fields` maps up to 16 simple names to scalar JSON Pointers
under `${content.source.name}`. No response text is executed, merged into card
definitions or treated as model instructions. Observed timestamps come from
`observedAtPointer`, or collection time when it is omitted.

New polling intervals accept 1-1440 minutes and default to five. Staleness
defaults to twice that interval, with a five-minute minimum. Source adapters
reject redirects, non-JSON responses, private/reserved DNS answers, oversized
bodies and deadlines. TLS connects to a checked public address, preventing a
second DNS lookup from redirecting collection into the local network.
Removing approval prevents subsequent collection.

Use `{"action":"list"}` for metadata, then `status`, `style`, `refresh` or `stop`
with that widget's `id`. New responses do not include readings, source URLs or
PNG bytes. This does not make the feature a privacy boundary: observations
persist in Gateway plugin state, Windows snapshots and rendered cards.
The existing raw `widgets.list` node/MCP command returns cached snapshots.
Restrict access accordingly. Failed collection retains last-good observations
and records an error; styling does not erase source errors or advance freshness.
Stopped IDs can be reused. The bounded registry retains 19 new ID slots, plus
the legacy Mercury slot; it does not invent an unsupported keyed-store delete API.

### Timestamped sensor history

Use `source.kind: "nightscout"` for a Nightscout-style entries endpoint you are
authorized to display. No live endpoint or personal readings are bundled:

```json
{
  "action": "create",
  "id": "sensor-history",
  "title": "Sensor history",
  "refreshMinutes": 5,
  "source": {
    "kind": "nightscout",
    "url": "https://example.com/api/v1/entries.json",
    "units": "mg/dL",
    "windowMinutes": 240,
    "maxPoints": 96,
    "gapMinutes": 15
  }
}
```

The exact URL also needs administrator approval. Entries with `type: "sgv"`
require a positive finite numeric `sgv` in mg/dL and a millisecond Unix `date`.
Other entry types are ignored. Sorting uses timestamps, not response order;
conflicting duplicates, invalid samples and empty sensor responses fail.
Choose `mg/dL` or `mmol/L` explicitly. The latter divides by 18 and rounds to
one decimal. The newest sample, not the polling clock, owns freshness.

The adapter exposes at most 96 real `{x: ISO timestamp, y: value}` points in
`content.source.points`. The default window is four hours ending at the latest
sample; up to 24 hours is configurable. If the point limit truncates a busy
window, only the newest samples are shown. A locally generated PNG uses actual
time coordinates and leaves gaps longer than `gapMinutes` unconnected. Its
monochrome plot has native value/unit/range text and image alternative text.
It is a data graphic, not a medical monitor, alert system or treatment tool.
There are no clinical target bands, dosing advice or health-to-light automations.
Native `Chart.Line` support is still unverified; this path uses an ordinary
`Image` instead. Small shows the current value without the graph.

The source adapter owns `content.source`, `source-series` artwork and actual
observation facts. Custom presentations can bind
`${$root.content.source.graphUrl}` and `${content.source.graphAlt}` while keeping
their own data elsewhere. Styling preserves the same observation and series.
Windows image decoding, size/theme parity and accessibility still need current
native proof.

### Manual snapshots and lighting controls

`publish` with an `id` and `snapshot` deploys an actual observation collected
elsewhere without starting a source schedule. Snapshot fields are `title`,
`value`, optional `detail`, public `sourceUrl`, actual ISO `observedAt`, optional
`staleAfterMinutes` and optional complete `presentation`. Alternatively, use the
`series` history payload described above to derive value/time and artwork. Stop an active polling
ID before switching it to manual mode. Later updates require another `publish`,
not a fabricated observation or `refresh`.

The [lighting presentation](examples/lighting-controls.json) is a manual control
panel, not effect telemetry. Its publication time describes the panel definition;
the protected native feedback describes the last driver interaction separately.
It reuses the separately installed C# driver from
[windows-personalization-skill](https://github.com/samanthamsong/windows-personalization-skill),
whose protocol was inspected at revision
`fc3e07adb0c3cf0f583d9aa8cfbc1cad02b5edcc`. The driver is not vendored, downloaded,
installed, registered or upgraded by this plugin. Follow its installation and
hardware requirements with separate permission. Its expected executable is
`%LOCALAPPDATA%\DynamicLightingDriver\DynamicLightingDriver.exe`.

On the Windows profile used by the provider, explicitly create
`widget-lighting.json` containing `{"enabled":true}` beside the `widgets`
directory, normally under `%APPDATA%\OpenClawTray`. A side-by-side packaged
profile can use a different data root. Neither a prompt, card nor source can
enable this consent. The driver window can appear even for device discovery;
foreground/background lighting priority can affect actual results.

Only a native click can invoke `widget.lighting`, with `associatedInputs: "none"`
and literal data:

| Operation | Literal fields | Effect |
| --- | --- | --- |
| `status` | `operation` only | Starts/reuses the driver and reads device layout, not `DIAGNOSE`. |
| `color` | `color: "#RRGGBB"` | Requests a solid color on the first discovered device. |
| `preset` | `preset`, `color`, optional `accentColor` | Requests solid, wave, breathe, twinkle, gradient or rainbow on the first device. |
| `release` | `operation` only | Quits only this provider's driver and releases its effects. |

Generated commands, shell, Python effects, layers, arbitrary executable paths
and bound/submitted inputs are rejected. The callback also verifies that the
requested action is present in the current selected card. Publication, polling,
styling, cached refresh and provider activation never apply an effect.
The provider keeps its own driver's stdin alive, drains output with bounds and
serializes exchanges. A per-profile file lease prevents duplicate provider
drivers; other profiles or unrelated lighting apps can still compete.
ACK means **driver acknowledged**, never verified physical lights or verified
current-effect telemetry. Feedback is localized, provider-owned and isolated
from source observations and `initialState`.

Use Release before unpinning or reconfiguring controls. Revoking the consent
file, removing all lighting snapshots or disposing the provider releases its
owned process. No process search or broad termination is used. Consent
revocation is checked on the provider timer even with no active tile.
The default protocol deadline is ten seconds;
`OPENCLAW_WIDGET_LIGHTING_TIMEOUT_SECONDS` accepts 2-30 in the provider process.
Unsupported hardware, missing installation or protocol failures remain visible
errors. No real lighting driver or hardware has been exercised by the synthetic
protocol tests.

## Prompt-designed native cards

Describe the result you want. The agent authors a full native Adaptive Card
template in `presentation.card`, not a theme name or a five-element mini-language.
Both Companion and the plugin must be updated. New requests reject the former
`theme`/`body` contract. Old saved PoC compositions migrate to native templates;
existing snapshots without presentation retain the simple title/status fallback.

Adapt the [responsive status example](examples/responsive-status.json) for a
text-first card, or the [artwork status example](examples/artwork-status.json)
for an illustration beside the actual `value` and optional `detail`. Both are
complete presentations, not tool preset names or scenario-specific defaults.
Use `publish` for a new snapshot and `style` to preserve an existing observation.
Neither example changes a widget until explicitly published.

The artwork example uses caller-selected PNGs named `backdrop` and
`illustration`, plus meaningful `data.imageAlt`. It bundles no images. Keep a
dark background and semantic `Light` text inside the hero `Container`, leaving
the provider's footer on the plain host surface. Use native auto/stretch columns,
semantic typography and smaller illustrations on Small. Do not add fabricated
metrics, decorative frames or text baked into images.

Reserve space for the provider-owned footer at every size. Use Small in the
Board for a shorter tile; a template cannot resize a pinned tile. Native image
loading, contrast, clipping, light/dark and accessibility still need Widgets
Board proof. JSON admission is not visual validation.

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
  "id": "project-status",
  "artworkFiles": {
    "illustration": "C:\\path\\to\\selected-status.png"
  },
  "presentation": {
    "card": {
      "type": "AdaptiveCard",
      "version": "1.5",
      "body": [
        {
          "type": "Image", "url": "openclaw-asset:illustration",
          "altText": "Illustration accompanying the project status",
          "size": "Small"
        },
        {
          "type": "TextBlock", "text": "${value}", "wrap": true
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
no built-in asset inventory. The native provider validates the
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
[artwork composition example](examples/artwork-status.json) for side-by-side
illustration/status placement. Compose images and text using native card elements and data-driven conditions.
Adaptive Cards Image has no opacity property. For a fade effect, create two
versions of the same artwork and switch between them with `$when`.
Do not fabricate observations to obtain the desired appearance.

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
| `widget.lighting` | The separately opted-in, literal user-click controls described above. Never an automatic refresh verb or general command. |

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

On the original Mercury route, styling preserves `observedAt` and the collection schedule. A failed styling
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

The Gateway owns trackers and recurring API collection. A host-owned
service scheduler checks due work once per minute; the default fetch interval
is 60 minutes for legacy Mercury, with configuration accepting 15-1440 minutes.
New independent collectors use the intervals described above. No recurring model
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
callback. Closed/inactive cards do not trigger source collection. The provider
still checks lighting consent and ownership while it remains alive.

The card always includes its observation timestamp and becomes visibly stale
after its configured stale interval (twice the refresh interval by default). Sleep, offline devices, a closed
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
node --test .\plugins\windows-widgets\*.test.mjs
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
release. It supports one legacy Mercury tracker plus 19 reusable independent
widget IDs and a single explicit Windows target. It does not expose arbitrary
HTML, C#, scripts or general-purpose action execution. Source URLs require
administrator approval; artwork and navigation URLs remain separate.
The generalized JSON/sensor and lighting additions have synthetic automated
coverage. Current-build isolated local MCP publication and Widgets Board
rendering were checked with a synthetic history graph (including a deliberate
gap) and the two-row lighting panel in Large. The disabled-consent message
remained visible after clicking Check devices; no driver was enabled or started.
Use Large for these multi-tracker demos: the selector can push lower controls
outside Medium's available height. Small/Medium layout parity, live source
collection, the generalized Gateway/agent flow and physical lighting remain
unverified.
Production work should add
a reviewed permissions/settings surface and Gateway-scoped tracker ownership
before multi-Gateway or multi-user deployment.

References: [C# widget provider](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs),
[OpenClaw plugin authoring](https://docs.openclaw.ai/plugins/building-plugins),
[plugin node invocation and scheduling](https://docs.openclaw.ai/plugins/sdk-runtime/gateway-and-nodes).
