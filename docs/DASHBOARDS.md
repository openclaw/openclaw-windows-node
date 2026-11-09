# Native Dashboards

This page describes the native Windows **Dashboards** surface: what it shows,
which Gateway contracts it uses, how widget content is isolated, and how its
behavior maps to the upstream OpenClaw web Dashboards page. Ownership rows live
in the [architecture ledger](ARCHITECTURE.md); this page does not restate them.

Upstream reference: `openclaw/openclaw@5089aa71b681ee275515f2de037b7813d73b45c3`
(`ui/src/pages/dashboards/`, `ui/src/components/board/`,
`ui/src/components/mcp-app-*`, `ui/src/lib/board/`, `src/gateway/board-*`,
`src/gateway/server-methods/board.ts`, `src/gateway/server-methods/mcp-app.ts`).
The Windows implementation reproduces the information and capabilities, not
the web code structure.

## Concepts

- A **dashboard** is a session's **board**: tabs plus a 12-column grid of
  widgets, read with `board.get`. Agents build boards; this is not the browser
  Control UI (the existing singular **Dashboard** action) or a health overview.
- A **widget** has one content kind: `html`, `mcp-app`, or `plugin`. HTML
  content is untrusted web content and runs in an isolated WebView2 sandbox.
  Plugin widgets with a trusted core kind render natively. MCP app content is
  not hosted on Windows.
- The **gallery** lists every session that has a board for the selected agent.

## Scope

Dashboards is a standalone page beside Chat, not a Settings section. It is
pinned in the main sidebar by default for each Gateway scope without a saved
pin choice. Use **Customize pages** beside Home to unpin or restore it.
Existing customized pin lists and explicit empty lists are preserved; unpinning
the last page stays empty after restart. Session links and command navigation
open the same standalone reader without showing the Settings section list.

The Reactor gallery uses an agent-scoped mast, static title-initial identity
tiles on agent-tinted bands, readable message summaries (or "No summary
available"), bordered sibling chat buttons, and batches of 24 cards with Show
more. Card footers show author/update metadata, Live when reported, and an
explicit Open board label. Card automation names announce the open action,
title, live state, author and update time; the summary is help text.
Fresh today requires five or more boards and at least two updates within the
current local calendar day. Its featured board leads with a title and summary,
not an empty cover. Larger inventories use denser bands. Sort and search appear
above five boards. On the first unscoped visit, the gallery discovers the most
recent board's configured agent before loading that agent's boards. Explicit
routes and later picker choices win. Agent labels come from `agents.list`;
missing labels fall back to IDs. The empty gallery's Open chat action opens the
existing chat entry without prefilled text; it does not invent an agent session.
The shared `PageHero` card names the page. Its answer line contains the
single-agent picker and the latest board with a valid reported update time,
independent of search and sort. The caption explains that boards are read-only
here and names the agent to ask for changes. No summary tiles or decorative
scope chips are added. Loading, unavailable and failed reads do not masquerade
as an empty result. The gallery contract does not contain a blocked-content
field, so cards do not invent a "Content blocked" badge or load boards to infer it.

The board header uses the same shared hero card with the board name, author
identity, update time and reported Live tag, including narrow layouts, and an icon-only
chat, refresh, and More options group. The Workspace Back button
returns to the gallery, retaining search, sort and the shown card count, and
restoring focus to the exact originating card (including a Fresh today card).
If it disappeared, focus falls back to the named gallery heading.
The gallery is not persisted offline: disconnect retains the page hero and
shows an error InfoBar with Open Connection directly below it, not cached boards.
Board content and ticket/sandbox ownership
are unchanged.

Board tabs select named native panels, with automation control relationships.
Overflow tabs open through More tabs and return focus to the selected tab.
An empty section explicitly says that no content was supplied and offers
Open chat for that section. Tabs display only their supplied widgets; Windows
does not manufacture category details or subscription records.

## Responsive layout

The page measures its Reactor host, not the window. Geometry, tiers and
identity colours follow the [Dashboards design contract](../DESIGN.md#dashboards-expressive-extension);
shell/history allocation follows the Settings width-dependent chrome row in
the [architecture ledger](ARCHITECTURE.md). Resizing retains board/session
identity and mounted widget documents. The hero, tabs and content scroll
together in a native vertical `ScrollViewer`, without a sticky header,
horizontal scrolling or a full-page decorative backdrop.

The mast uses a native bordered **Open in web** button followed by Refresh.
The action group aligns to the content column's right edge. Header actions use
stock buttons and FontIcons, with native sizing, brushes and interaction states.
In Narrow, actions move under the copy and the browser command moves into
More options. Search gets its own row below Wide. Grid cells own placement
and spacing independently of entrance motion; last-row cards do not stretch
into empty columns.

A stock button paints each gallery card's full surface and owns keyboard focus,
hover and pressed states. The coloured cover and identity badge remain decorative
content. Open chat is a separate sibling in an auto-sized footer, not a button
nested inside the board action. Card content can grow without a fixed minimum
height, and the card does not apply its own hover translation.

Mounted tests cover Wide, Medium and Narrow hosts; full-shell captures are a
separate proof. The shell's minimum width does not imply it can expose every
supported mounted-page width.

Windows is a native **reader** for agent-created boards. It covers what people
check daily and links to the Gateway's web Dashboards for the rest.

| Area | Windows | Where the rest lives |
|---|---|---|
| Find boards | Searchable, sortable, agent-scoped gallery of static cards | |
| Read boards | Tabs, live refresh of the open board, native reports, the ticketed HTML viewer, website links, Open chat | |
| Widget access requests | Shown as pending, rejected, or granted | Allow, Reject, and Remove: web Dashboards |
| Arrange boards | | Move, resize, tabs, auto-height toggle, removal: web Dashboards, or ask the agent |
| Widget actions | None: the relay closes every bridge port | Bridge actions, prompts, cron triggers: web Dashboards |
| MCP app widgets | Placeholder card | Web Dashboards |

Native editing, permission grants, privileged widget actions, and a reusable
MCP app host are deferred future work, not planned for this reader.

## Web handoff

**Open in web** (gallery header, the board's **More options**
menu, and the cards of widgets Windows only displays) opens the Gateway's web
Dashboards gallery through the existing Dashboard launcher
(`IAppCommands.OpenDashboard`), at the `dashboards` path under the Control UI
base path. That route is the upstream `dashboards` page
(`ui/src/pages/dashboards/route.ts` at the reference commit).

It opens the gallery, not the current board. Upstream exact-board paths
(`dashboard/<agent>/...` from `packages/session-url-contract`) depend on the
installed Gateway's configured main key, reserved names, and short session ids,
so Windows does not construct them. **Copy session key** gives the stable
identity to find the board there. The native `dashboards?session=&agent=` hub
tag is an in-app route, never a browser URL.

Copy session key reports success or failure in the same neutral flyout surface
as Usage: `FlyoutPresenterBackground`, `FlyoutBorderThemeBrush`, `ThemeShadow`,
and a severity-colored icon. The toast auto-dismisses after four seconds.
It is bottom-centered (maximum width 480, margin 20), with its native theme
explicitly forwarded from the page. A stable Reactor announcement region
announces failures assertively and success politely after the menu/commit turn.
It is page-owned because the shared persistent banner filters success notices.

## Gateway contract

The reader uses only these methods and events. It sends no board mutation, grant,
bridge, or `mcp.app.*` request.

Displayed boards come from `sessions.list`; tabs, widget grants and report props
come from `board.get`. Live badges require the session's `running` status.
Counts, sorting, relative update times and avatar initials are deterministic
projections of those responses, not locally generated board content. Report
metrics and chart values are required payload fields; missing values are rejected,
not substituted with zero. Missing session, tab or widget collections are errors,
not evidence of an empty gallery or board. Explicit empty arrays remain valid.
Refresh failures retain previously received data with an error indication.
Gallery invalidation observes the operator owner's `SessionsUpdated` event as
well as `board.changed`, so runs can change card titles, recency and Live without
editing the board. Both event sources share the same queued-refresh coalescing,
load generation fence and disposal unsubscribe. Detached gallery reads do not
publish operator session events.

Report tables contain column labels and string cells, not column types.
Cells remain left-aligned, including numeric-looking strings: right-aligned
numbers/currency require a supported typed contract, not guessing from text.
Native report cards grow their shared grid rows to fit the content, retaining
the saved row span as a minimum. Tables wrap within the card's column width;
reports use page-level scrolling, not nested scroll viewers. An identical first
report heading is omitted when the native card title already presents it.
Neighboring card surfaces stretch to the shared row height, while HTML and
other non-report content retain their allocated height and top alignment.
Ticketed HTML keeps its native identity title even when the untrusted document
contains the same heading. Windows does not inspect or rewrite the sandboxed
document to deduplicate it; native report heading suppression uses typed data only.
Charts require finite numeric values and supplied labels. Values appear above
bars with round-trip numeric precision, full tooltips and automation names,
while zero and negative values retain their actual baseline. The schema has no
typed date axis or illustrative-trend metadata. Windows neither invents dates
nor labels real numeric reports as shape-only trends. Non-numeric or metadata-only
chart payloads remain invalid; adding such chart support is deferred.

Loading, disconnected, unsupported and authorization states describe the native
client's connection/hosting state. The empty gallery shows only its unavailable
data explanation and an Open chat action, not locally authored examples or
suggestions. Synthetic boards and reports belong only to the test Gateway
fixture, not the production data path.

| Method or event | Params / payload | Result |
|---|---|---|
| `sessions.list` (dashboard rows) | `rowMode:"compact"`, `source:"dashboard"`, `hasBoard:true`, `excludeDock:true`, `archived:"all"`, `includeGlobal`, `includeUnknown`, `configuredAgentsOnly`, `limit`, `offset`, `agentId?` | `sessions[]`, `count`, `hasMore`, `nextOffset?` |
| `board.get` | `sessionKey`, `agentId?` | board snapshot with minted `frameUrl`, `viewTicket`, `viewTicketTtlMs`, `viewGeneration`, `sandboxUrl`, `sandboxPort`, `sandboxOrigin?` |
| `board.changed` event | `sessionKey`, `revision`, `widget?` | refetch `board.get` |
| `board.command` event | `sessionKey`, `command: focus_tab | set_chat_dock` | select tab |

Widget HTML is fetched over HTTP from the active Gateway origin at
`/__openclaw__/board/<sessionKey>/<name>/index.html?bt=<ticket>`.
Loopback document requests use a direct, no-proxy transport so inherited
`HTTP_PROXY`/`ALL_PROXY` cannot receive bearer view tickets or returned HTML.
Remote Gateway requests preserve their existing proxy routing. Both transports
disable redirects and cookies. The sandbox proxy document is served by the
Gateway's sandbox listener at
`/mcp-app-sandbox` on `sandboxPort` (or the configured `sandboxOrigin`).

## Reader limits

- Gallery fetching is capped at 25 pages of 200 rows (5,000 boards). Counts
  describe loaded rows. Search includes author; there is no separate author
  filter or live widget preview in gallery cards.
- Boards use a 12-column grid. Tabs are hidden for one tab, with More tabs
  overflow after six. Remote `focus_tab` selects the named tab. Chat dock/focus
  mode and `pageWidgetName` presentation remain web-only; Open chat uses the
  existing native chat surface.
- HTML hosting shows a slow notice after 10 seconds, renews expiring tickets,
  retries rejected authorization, and stops after three automatic renderer
  restarts until Retry. Repeated sandbox failure is reported separately from
  renderer crashes. Granted capabilities remain visible; pending and rejected
  grants are read-only and link to web Dashboards.
- Native plugins support `session:report` and validated `session:website`
  browser links. `session:progress`, `browser:dashboard`, third-party UI bundles
  and MCP app hosting remain web-only. Unsupported/disabled contributions show
  an explanation rather than executing a plugin bundle.
- The deferred editing and actions in the scope table include all move/resize
  presets, drag/keyboard nudging, auto-height toggles, widget removal, permission
  grants, bridge actions and MCP view leases. Read-only auto-height reports are
  applied without granting any of those capabilities.

## Widget hosting security

The upstream web host uses a trusted page, a Gateway-served sandbox proxy
frame on a separate origin, and an untrusted inner `srcdoc` frame. Windows keeps
the Gateway proxy and its CSP unchanged and replaces the trusted web page with
a local relay page in a dedicated WebView2.

| Upstream rule | Windows host |
|---|---|
| Widget bytes come only from the active Gateway origin with a ticket | `BoardWidgetUrlPolicy` resolves `frameUrl` against the Gateway HTTP origin and rejects any other origin, scheme, or credentials before a native `HttpClient` fetch with a 10 MiB limit |
| Sandbox proxy runs on a distinct origin (`sandboxPort` or `sandboxOrigin`), never the Gateway or host origin, path `/mcp-app-sandbox` | Same validation as upstream `resolveSandboxHostUrl`; in addition, an HTTPS Gateway refuses a plain-HTTP sandbox that isn't loopback, because native code hands the fetched document to that frame |
| Proxy enforces the widget CSP, `sandbox="allow-scripts allow-forms"`, permissions policy, and descendant-frame blocking | Unchanged: enforced by the Gateway proxy document |
| Host accepts messages only from the proxy frame and sandbox origin | The relay page checks `event.source` and `event.origin`; native accepts `WebMessageReceived` only from the relay page URL |
| Bridge authority only through the first private `MessagePort` | The reader has no bridge: the relay page closes every offered port, so widgets run without host authority |
| Popups and navigation | `NewWindowRequested` is denied; top-level navigation outside the relay page is cancelled |
| Device permissions | `PermissionRequested` is denied for every kind |
| Isolation between widgets and from the app | One WebView2 per HTML widget in an in-private profile and a browser process separate from Canvas and Gateway Chat; no host objects, DevTools, context menus, autofill, or password save. HTML widgets share one profile because their content runs in an opaque-origin sandbox frame |
| `session:website` takes a credential-free HTTPS URL, embedded in an iframe | Windows validates the URL (HTTPS, no credentials, at most 2048 characters, and a public host: literal loopback, private, link-local, CGNAT, unspecified, `localhost` and single-label names are refused) and offers it only as a link to the user's browser. No website runs inside the app |
| Report links | `session:report` link blocks accept only credential-free HTTP(S) URLs and open in the browser |

## Validation

- Shared tests: dashboard row and board snapshot parsing, gallery
  filtering/sorting/scoping, widget and website URL policy, report parsing.
- Tray tests: route, navigation, command registration, and the web handoff:
  the page wires the existing app command to the `dashboards` path. The
  real `GatewayDashboardLauncher` with an intercepted browser delegate
  produces the Control UI gallery URL without a session or exact-board path.
- Mounted UI tests (`DashboardWidgetCellTests`): ticket-failure recovery,
  website link cards without an embedded browser, refused website addresses,
  the web handoff from the gallery header (whose static cards load no board),
  display-only widgets, and the board menu, plus the board menu's session-key copy.
  Designer-delta cases cover the Fresh today thresholds, exact-card/removed-card
  focus return after Show more, named overflow panels and tab focus, untyped
  table alignment, and exact chart values above positive, zero and negative bars.
  Test definitions are not execution evidence; current-source results and
  Light/Dark 1400-DIP gallery/board frames must be collected separately.
- Visual proof uses the [Gateway fixture](GATEWAY_FIXTURE_TESTING.md) scenario
  `dashboards`, which serves synthetic boards, widget HTML, and a sandbox proxy.
  Never change real boards or grant real permissions on a production Gateway.

## Configuration

| Variable | Default | Effect |
|---|---|---|
| `OPENCLAW_BOARD_WIDGET_LOAD_TIMEOUT_SECONDS` | 30 | Timeout for fetching one widget document |

## Platform dependencies

Core and Advanced package references target the published Reactor
`0.1.0-preview.17`. Restore and build must not use the former
`0.1.0-preview.12.usage-theme.2` version override or source-built backport.
`build.ps1` and the affected CI restore lanes run
`scripts\Initialize-ReactorPackages.ps1`, which downloads the two official GitHub
release packages into the ignored `artifacts\reactor-packages` feed and verifies
pinned SHA-256 digests. For direct `dotnet restore` on a fresh checkout, run that
script first. Repository `NuGet.Config` maps only the two Reactor package IDs to
that feed and retains nuget.org for other packages. No version override, private
package feed or machine-wide configuration change is needed.
The earlier backport screenshots are historical evidence, not proof of the
published preview.17 runtime; rerun mounted and native theme/scroll proof after
the upgrade.

- Panel `Padding` and `CornerRadius` on `Grid` require Reactor preview.13 or
  later, so widget chrome uses `Border` for padding, stroke and corners.
- WinUI `WebView2` paints an opaque theme backplate even when
  `DefaultBackgroundColor` is transparent, so frameless widgets show a solid
  fill behind their content. The widget host already requests a transparent
  background and picks up the fix when it ships in WinUI.