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
  widgets, read with `board.get`.
- A **widget** has one content kind: `html`, `mcp-app`, or `plugin`. HTML and
  MCP app content is untrusted web content and runs in an isolated WebView2
  sandbox. Plugin widgets with a trusted core kind render natively.
- The **gallery** lists every session that has a board for the selected agent.

## Delivery steps

| Step | Scope | Status |
|---|---|---|
| 1 | Gallery and dashboard view with HTML and plugin widgets | Done, awaiting approval |
| 2 | Widget permissions, host bridge actions, tab and widget moves, resize, auto height toggle, remove | Planned |
| 3 | MCP app host and MCP app widgets | Planned |

## Gateway contract

Step 1 uses only these methods and events. Steps 2 and 3 add the board
mutation, grant, bridge, and `mcp.app.*` client methods together with the UI
that calls them.

| Method or event | Params / payload | Result |
|---|---|---|
| `sessions.list` (dashboard rows) | `rowMode:"dashboard"`, `source:"dashboard"`, `hasBoard:true`, `excludeDock:true`, `archived:"all"`, `includeGlobal`, `includeUnknown`, `configuredAgentsOnly`, `limit`, `offset`, `agentId?` | `sessions[]`, `pagination?` |
| `board.get` | `sessionKey`, `agentId?` | board snapshot with minted `frameUrl`, `viewTicket`, `viewTicketTtlMs`, `viewGeneration`, `sandboxUrl`, `sandboxPort`, `sandboxOrigin?` |
| `board.changed` event | `sessionKey`, `revision`, `widget?` | refetch `board.get` |
| `board.command` event | `sessionKey`, `command: focus_tab | set_chat_dock` | select tab |

Widget HTML is fetched over HTTP from the active Gateway origin at
`/__openclaw__/board/<sessionKey>/<name>/index.html?bt=<ticket>`. The sandbox
proxy document is served by the Gateway's sandbox listener at
`/mcp-app-sandbox` on `sandboxPort` (or the configured `sandboxOrigin`).

## Parity checklist

Status values: **Done** (available natively), **Step 2/3** (scheduled),
**Deferred** (not planned in this work, with reason).

### Gallery

| Web item | Native equivalent | Data source | Status |
|---|---|---|---|
| Gallery of sessions with a board, scoped to the selected agent | Dashboards hub page; agent picker in the header; cards in an adaptive grid | `sessions.list` dashboard rows, `agentId` | Done |
| Load every page (`pagination.hasMore`) | Pages fetched until `hasMore` is false, capped at 25 pages | `pagination.nextOffset` | Done |
| Search title, author, last message, key | Search box | Row `label`/`displayName`/`derivedTitle`, author, `lastMessagePreview`, `key` | Done |
| Author filter with all-authors option | Author combo box | `createdActor`, `owner.actor`, `agentId` fallback | Done |
| Sort by recently updated or title | Sort combo box | `updatedAt`, display title | Done |
| Result count and total count | Result line; hero stats for dashboards, live sessions, and authors | filtered rows | Done |
| Card: title, author avatar initial, "By author", updated relative time, unknown time | Card text, avatar, relative time | row fields | Done |
| Live indicator for running sessions | Live badge | `status == "running"` | Done |
| Live preview loaded only near the viewport | Preview mounts when the card is realized and visible; a global limiter bounds concurrent preview WebViews | `board.get` | Done |
| Preview shows saved HTML and preview-safe core widgets only, without host bridge | Passive preview: HTML (no bridge) and `session:report` | widget kind | Done |
| Preview load failure message | Inline preview error | `board.get` error | Done |
| Open the full dashboard from a card | Card click opens the dashboard view | session key | Done |
| Loading skeleton | Shimmer placeholders | none | Done |
| Empty gallery and no-results states | Empty and no-results panels | rows | Done |
| Load error with stale data retained | Error banner over retained rows | request failure | Done |
| Docs link | Header "Learn more" link to `https://docs.openclaw.ai/web/dashboards` | none | Done |

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
| Full-width page widget (`pageWidgetName`, website/browser page chrome) | Full-width page layout | layout | Step 2 |

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
| Auto height from content | Height reports applied to auto-height widgets | `openclaw:widget-size` | Done (toggle in Step 2) |
| MCP app widget | Placeholder card until the MCP app host lands | `contentKind:"mcp-app"` | Step 3 |
| Stale widget | Stale notice with Retry and Remove | MCP view lease | Step 3 |
| Remove | Menu Remove | `board.update` `widget_remove` | Step 2 |
| Menu: move to tab, no other tabs, resize presets SM/MD/LG/XL, auto height toggle | Widget `MenuFlyout` | `board.update` | Step 2 |
| Drag to move, drag to resize, keyboard focus and Alt+arrow nudge, live announcements | Pointer and keyboard gestures, `AutomationProperties.LiveSetting` | `board.update` | Step 2 |
| Action in progress and failure feedback | Disabled chrome while pending; inline failure overlay | mutation outcome | Step 2 |
| Disabled plugin state with Remove | "Widget from disabled plugin" card | `controlUiWidgetKinds` | Done (Remove in Step 2) |
| Core plugin `session:report` | Native report: text, metrics, table, bar/line chart, links | `props` | Done |
| Core plugin `session:website` | Isolated website WebView2 with origin footer, "Open website", and a load-failure state with Retry | `props.url` | Done |
| Core plugin `session:progress` | Native progress card | progress card RPC and session row | Step 2 |
| Core plugin `browser:dashboard` | none | browser plugin live viewer | Deferred: Windows has no Gateway browser panel host |
| Third-party plugin widget contributions | none | Control UI JavaScript bundles | Deferred: web-only plugin UI bundles cannot run in native UI; shown as disabled plugin |

### Widget permissions and actions

| Web item | Native equivalent | Data source | Status |
|---|---|---|---|
| Needs approval card listing network origins and host tools, or summary | Approval card in the widget | `grantState:"pending"`, `declared`, `declaredSummary` | Step 2 |
| Allow and Reject require an explicit choice | Buttons only; never automatic | `board.widget.grant` | Step 2 |
| Rejected state with Remove | Rejected card | `grantState:"rejected"` | Step 2 |
| Granted badge with active capabilities | Chrome badge with tooltip | `declared` | Step 2 |
| Allow or reject failure message | Inline error | grant failure | Step 2 |
| Widget bridge: open link, state emit, data read, action run, cron trigger, prompt send with confirmation | Native relay over the private bridge port | `board.event`, `board.data.read`, `board.action`, `board.prompt.authorize` | Step 2 |

### MCP app widgets

| Web item | Native equivalent | Data source | Status |
|---|---|---|---|
| App UI in a sandbox with Gateway CSP | Reusable MCP app host in WebView2 | `mcp.app.view` | Step 3 |
| Tool calls, tool list, resources list/templates/read/write/subscribe/unsubscribe | Bridge relay restricted to allowed methods | `mcp.app.*` | Step 3 |
| Model context update, open file with confirmation, messages with confirmation | Native confirmation dialogs | `mcp.app.*` | Step 3 |
| Host context and theme, display mode, container size | Host context projection | app theme, layout | Step 3 |
| Resource updated and host context changed events | Forwarded notifications | events | Step 3 |
| Lease renewal, stale and retired apps, teardown on unmount | Lifecycle owner | `board.widget.appView` | Step 3 |

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
| Bridge authority only through the first private `MessagePort` | Step 1 has no bridge: the relay page closes every offered port, so widgets run without host authority |
| Popups and navigation | `NewWindowRequested` is denied; top-level navigation outside the relay page is cancelled; links open in the browser only after HTTP(S) validation (Step 2 bridge) |
| Device permissions | `PermissionRequested` is denied for every kind |
| Isolation between widgets and from the app | One WebView2 per widget in an in-private profile and a browser process separate from Canvas and Gateway Chat; no host objects, DevTools, context menus, autofill, or password save. HTML widgets share one profile because their content runs in an opaque-origin sandbox frame. Each `session:website` widget gets its own profile, deleted when it closes, so same-origin sites in two widgets never share cookies or storage |
| Concurrent previews are bounded | A process-wide limiter caps live preview hosts |
| `session:website` takes a credential-free HTTPS URL | Same, and the Windows host also requires a public host for the first load, every navigation and redirect, and subframes, and refuses every request from documents, frames and workers (fetch, scripts, images) to a private-network host: literal loopback, private, link-local, CGNAT, unspecified, `localhost` and single-label names, with or without a trailing dot. Residual risk, as in the upstream browser iframe: a public hostname whose DNS answer is a private address is not detected, and WebSocket handshakes are not exposed to WebView2 request interception (Chromium blocks `ws:` from HTTPS pages; `wss:` needs a trusted certificate) |

## Validation

- Shared tests: dashboard row and board snapshot parsing, gallery
  filtering/sorting/scoping, widget URL policy, report parsing.
- Tray tests: route, navigation, and command registration.
- Visual proof uses the [Gateway fixture](GATEWAY_FIXTURE_TESTING.md) scenario
  `dashboards`, which serves synthetic boards, widget HTML, and a sandbox proxy.
  Never change real boards or grant real permissions on a production Gateway.

## Configuration

| Variable | Default | Effect |
|---|---|---|
| `OPENCLAW_BOARD_WIDGET_LOAD_TIMEOUT_SECONDS` | 30 | Timeout for fetching one widget document |
| `OPENCLAW_DASHBOARD_PREVIEW_LIMIT` | 8 | Maximum live gallery preview WebViews; 0 shows outlines only |

## Platform dependencies

- Panel `Padding` and `CornerRadius` on `Grid` require Reactor preview.13 or
  later, so widget chrome uses `Border` for padding, stroke and corners.
- WinUI `WebView2` paints an opaque theme backplate even when
  `DefaultBackgroundColor` is transparent, so frameless widgets show a solid
  fill behind their content. The widget host already requests a transparent
  background and picks up the fix when it ships in WinUI.