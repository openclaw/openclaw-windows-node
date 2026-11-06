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

The Reactor gallery uses an agent-scoped mast, static agent-tinted covers,
two-line message summaries, sibling chat buttons, a Fresh today shelf for five
or more boards, and batches of 24 cards with Show more. Sort and search appear
above five boards. On the first unscoped visit, the gallery discovers the most
recent board's configured agent before loading that agent's boards. Explicit
routes and later picker choices win. Agent labels come from `agents.list`;
missing labels fall back to IDs. The empty gallery's Open chat action opens the
existing chat entry without prefilled text; it does not invent an agent session.
The heading names the selected agent ("Dashboards for main", or its configured
display name). A secondary line reports only the loaded Gateway row count:
"No dashboards yet", "1 dashboard", or the plural count. Loading, unavailable
and failed reads do not masquerade as an empty result.

The board header uses the same subtle system-accent gradient and an icon-only
chat, refresh, and More options group. The Workspace Back button
returns to the gallery. Board content and ticket/sandbox ownership are unchanged.

## Responsive layout

The page measures its Reactor host, not the window. The canonical
`PageResponsiveLayout` helper selects Wide (1000 or more DIP), Medium (720 to
999 DIP), or Narrow (below 720 DIP, supported down to a 480-DIP host), including
snapping of WinUI's fractional boundary measurements. Content is centered,
capped at 1200 DIP after horizontal padding (32 DIP, or 20 in Narrow).
The mast's system-accent backdrop is a full-width page-host layer, including
the centered column's side margins and scrollbar gutter. Its height follows
the measured mast; the content column remains centered above it. This decorative,
non-hit-testable layer is outside the page scroller and stays at the viewport top.
In Light and Dark, the wash fades vertically to transparent before its bottom,
so the full-width layer has no tinted horizontal edge.
The gallery and board use native `ScrollViewer` controls with horizontal
scrolling disabled. Their content fills the viewport and exposes the native
ScrollPattern. Each page scrolls its mast, tabs and content together; there is no
sticky header or scroll-offset compensation.

The mast uses a native bordered **Open in web** button followed by Refresh.
The action group aligns to the content column's right edge. Explicit native
button brush resources keep its chrome intact when Reactor reuses controls.
Mast actions use one row under the text in Narrow; the browser command moves
into More options. Search is right-aligned in Wide and gets its own full-width
row in Medium/Narrow. Board cards use explicit equal-width Grid columns with a
240-DIP minimum and 12-DIP gaps. Last-row cards do not stretch into empty columns.
Fresh today uses top-aligned, stretched feature/side columns in Wide, feature
plus a two-up side row in Medium (full width when only one side card exists),
and stacked cards in Narrow. Grid spacing, not retained per-card margins, owns
the gaps across resizes. Layout cells own placement independently
of entrance motion. Widget documents remain mounted when the page reflows.

`ResponsivePage_UsesMeasuredHostWidthWithoutOverlappingOrUnmarkedClipping`
mounts the gallery and board at 1400, 1000, 760, and 520 DIP, checking geometry,
ellipsis, column counts, mast action rows, and retained board loading. The
real-shell fixture capture also records measured host/content widths, tiers,
window rectangles in pixels/DIP, and image hashes. The unchanged Hub shell
minimum is 1000 DIP, so its open-pane minimum host remains about 740 DIP:
Narrow is a mounted-page contract, not a claim that the shell reaches 520 DIP.

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

## Delivery steps

| Step | Scope | Status |
|---|---|---|
| 1 | Native reader: gallery, board view, reports, HTML viewer, website links, web handoff | Done, awaiting approval |
| 2 | Widget permission grants, host bridge actions, tab and widget moves, resize, auto height toggle, remove | Deferred: web Dashboards |
| 3 | MCP app host and MCP app widgets | Deferred: web Dashboards |

## Gateway contract

Step 1 uses only these methods and events. It sends no board mutation, grant,
bridge, or `mcp.app.*` request.

Displayed boards come from `sessions.list`; tabs, widget grants and report props
come from `board.get`. Live badges require the session's `running` status.
Counts, sorting, relative update times and avatar initials are deterministic
projections of those responses, not locally generated board content. Report
metrics and chart values are required payload fields; missing values are rejected,
not substituted with zero. Missing session, tab or widget collections are errors,
not evidence of an empty gallery or board. Explicit empty arrays remain valid.
Refresh failures retain previously received data with an error indication.

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
`/__openclaw__/board/<sessionKey>/<name>/index.html?bt=<ticket>`. The sandbox
proxy document is served by the Gateway's sandbox listener at
`/mcp-app-sandbox` on `sandboxPort` (or the configured `sandboxOrigin`).

## Parity checklist

Status values: **Done** (available natively), **Deferred** (not in the native
reader, with where the work lives instead).

### Gallery

| Web item | Native equivalent | Data source | Status |
|---|---|---|---|
| Gallery of sessions with a board, scoped to the selected agent | Dashboards hub page; agent picker in the header; cards in an adaptive grid | `sessions.list` dashboard rows, `agentId` | Done |
| Load every page (`pagination.hasMore`) | Pages fetched until `hasMore` is false, capped at 25 pages of 200 rows; rows beyond 5,000 are not shown | `pagination.nextOffset` | Done (capped) |
| Search title, author, last message, key | Search box | Row `label`/`displayName`/`derivedTitle`, author, `lastMessagePreview`, `key` | Done |
| Author filter with all-authors option | Search matches author; no separate author combo | `createdActor`, `owner.actor`, `agentId` fallback | Deferred |
| Sort by recently updated or title | Sort combo box | `updatedAt`, display title | Done |
| Result count and total count | Section count and live-region result count (loaded rows only) | filtered rows | Done |
| Card: title, author avatar initial, "By author", updated relative time, unknown time | Static card: agent-tinted cover, avatar, last message (two lines), relative time, and sibling Open chat action | row fields | Done |
| Live indicator for running sessions | Live badge | `status == "running"` | Done |
| Live board preview on each card | none: cards never load a board, widget ticket, or web content | `board.get` | Deferred: the gallery stays a static list |
| Open the full dashboard from a card | Card click opens the dashboard view | session key | Done |
| Loading skeleton | Static native placeholders and an accessible loading label | none | Done |
| Empty gallery and no-results states | Empty and no-results panels | rows | Done |
| Load error with stale data retained | Error banner over retained rows | request failure | Done |
| Docs link | No header link; this guide documents the reader | none | Deferred |
| Web gallery | Header **Open in web** | Control UI `dashboards` route | Done |

### Opening a dashboard

| Web item | Native equivalent | Data source | Status |
|---|---|---|---|
| Open from gallery | Dashboard view inside the Dashboards page with Back | session key | Done |
| Open from a session | Sessions page row menu "Open dashboard"; command palette; an explicit route agent also selects that gallery scope | session key, route `agent` | Done |
| Widget grid with spans | 12-column grid with column/row spans | `sizeW`, `sizeH`, `position` | Done |
| Tabs with overflow | Tab strip (`SelectorBar`), hidden for one tab, overflow menu after six | `tabs[]` ordered by `position` | Done |
| Remote tab focus | Selects the requested tab | `board.command` `focus_tab` | Done |
| Live refresh on change | Refetch on `board.changed` and reconnect | events | Done |
| Empty board state | "A clear board, ready for work" | empty tab | Done |
| Missing session, not found, unavailable, load error | Status panels | request outcome | Done |
| No permission (no `operator.read` or `board.get` not advertised) | "Dashboards are unavailable" state | hello scopes and methods | Done |
| Disconnected | Disconnected state with an Open connection action | connection state | Done |
| Chat dock beside the board (`chatDock`, `set_chat_dock`) and focus mode | "Open chat" action routes to native chat for the session | `tabs[].chatDock` | Deferred: native chat is a single-surface owner; embedding a second chat host needs a chat ownership change |
| Full-width page widget (`pageWidgetName`, website/browser page chrome) | Grid layout only | layout | Deferred: web Dashboards |
| Open the board in the web Control UI | **Open in web** opens the web gallery; **Copy session key** to find the board | Control UI `dashboards` route | Done (gallery, not the exact board) |

### Widgets

| Web item | Native equivalent | Data source | Status |
|---|---|---|---|
| Header: title, kind label (HTML, MCP, Plugin, core label) | Widget chrome title and kind chip | widget fields | Done |
| HTML content in an isolated sandbox | WebView2 widget host (see Security) | `frameUrl`, `sandboxUrl` | Done |
| Presentation `card`, `full-bleed`, `frameless` | Chrome padding and border variants | `presentation` | Done |
| Loading, slow-loading notice, load timeout | Progress ring, slow notice after 10 s, timeout error | host lifecycle | Done |
| Unauthorized ticket retries `board.get`; repeated failure message | Refetch; a failed refresh shows content unavailable with Retry, while repeated ticket rejection shows the authorization error | HTTP 401 and refresh outcome | Done |
| Sandbox origin required hint for remote Gateways | Same message after repeated authorization failures, or when the sandbox doesn't respond to three fresh relay pages | `sandboxOrigin` absent on non-loopback | Done |
| Ticket renewal before expiry | Refetch board before `viewTicketTtlMs` elapses | `viewTicketTtlMs` | Done |
| Error state with details and Retry | Error card with expandable details and Retry | host error | Done |
| Browser or renderer repeatedly fails | Three automatic restarts, then the WebView closes and shows a localized error until Retry; crashes do not imply a sandbox configuration failure | WebView2 process failure | Done |
| Theme matching | Initial `#theme=` seed and widget theme message on theme change | app theme | Done |
| Auto height from content | Height reports applied to auto-height widgets | `openclaw:widget-size` | Done (toggle deferred: web Dashboards) |
| MCP app widget | Placeholder card with **Open in web** | `contentKind:"mcp-app"` | Deferred: web Dashboards |
| Stale widget | none | MCP view lease | Deferred: web Dashboards |
| Remove | none | `board.update` `widget_remove` | Deferred: web Dashboards |
| Menu: move to tab, no other tabs, resize presets SM/MD/LG/XL, auto height toggle | none | `board.update` | Deferred: web Dashboards |
| Drag to move, drag to resize, keyboard focus and Alt+arrow nudge, live announcements | none | `board.update` | Deferred: web Dashboards |
| Action in progress and failure feedback | none | mutation outcome | Deferred: web Dashboards |
| Disabled plugin state with Remove | "Widget from disabled plugin" card | `controlUiWidgetKinds` | Done (Remove deferred: web Dashboards) |
| Core plugin `session:report` | Native report: text, metrics, table, bar/line chart, links | `props` | Done |
| Core plugin `session:website` | Link card: the site's host and **Open website**, which opens the validated URL in the user's browser. Windows does not embed websites | `props.url` | Done (embedding deferred) |
| Core plugin `session:progress` | Unsupported card with **Open in web** | progress card RPC and session row | Deferred: web Dashboards |
| Core plugin `browser:dashboard` | none | browser plugin live viewer | Deferred: Windows has no Gateway browser panel host |
| Third-party plugin widget contributions | none | Control UI JavaScript bundles | Deferred: web-only plugin UI bundles cannot run in native UI; shown as disabled plugin |

### Widget permissions and actions

| Web item | Native equivalent | Data source | Status |
|---|---|---|---|
| Needs approval card listing network origins and host tools, or summary | Approval card in the widget (read-only) with **Open in web** | `grantState:"pending"`, `declared`, `declaredSummary` | Done |
| Allow and Reject require an explicit choice | none; never automatic | `board.widget.grant` | Deferred: web Dashboards |
| Rejected state with Remove | Rejected card with **Open in web** | `grantState:"rejected"` | Done (Remove deferred) |
| Granted badge with active capabilities | Chrome badge with tooltip | `declared` | Done |
| Allow or reject failure message | none | grant failure | Deferred: web Dashboards |
| Widget bridge: open link, state emit, data read, action run, cron trigger, prompt send with confirmation | none: the relay closes every offered port | `board.event`, `board.data.read`, `board.action`, `board.prompt.authorize` | Deferred: web Dashboards |

### MCP app widgets

Windows shows a placeholder card for MCP app widgets and hands off to the web
Dashboards gallery. App hosting, `mcp.app.*` tool and resource relays,
confirmation dialogs, host context, notifications, and view leases
(`board.widget.appView`) are deferred.

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