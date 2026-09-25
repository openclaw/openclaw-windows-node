# Windows companion UI review

## Review status

**Current review: colleague workspace layouts plus the native-capture companion
reference, not a completed native port.**

The companion's abbreviated browser pages have been replaced with captures of
the actual WinUI application built from this branch. This is a screenshot-backed
click-through reference, not an HTML recreation claiming native control fidelity.
The reviewed Dashboard shell is retained, with the workspace updates below.

### Colleague workspace pages

The user-provided `OpenClaw Windows Prototype` (`realistic.js`, `realistic.css`,
and `windows-realistic.html`) is the current layout/content reference for five
pinned pages. It supersedes the earlier upstream-fixture placeholders for these
pages only. Its shell and separate Settings taxonomy are not imported.

| Page | Adopted layout and click-through |
| --- | --- |
| Agents | Assistant, Research, and Developer cards with identity, availability, latest work, workspace, Open chat, and Recent work timeline |
| Dashboards | Northstar and Weekly overview preview tiles; project details with metrics, priorities, task status toggles, activity, and back navigation |
| Systems | Workstation and Gateway tiles, desktop illustration, utilization, connection/pairing status, and Device access links |
| Automations | Morning brief, Weekly project review, and Release watch table; enable switches, mock run results, and new/edit form layouts |
| Plugins | Installed/Discover tabs, Calendar/GitHub/Notes/Linear cards, tool counts, and reversible sample install/remove dialogs |

The page hierarchy and fictional fixtures follow the colleague's rendered
prototype. Colors, spacing, typography, icons, buttons, selectors, and switches
use this repository's tokens and component patterns. Its raw palette and
incompatible spacing scale are not copied. The original reference files are
unchanged.

Sessions now has its own subtle Fluent Add button (U+E710), matching the
assistant-header Add button. Both use the same new-conversation action. The
secondary date line beneath each existing conversation title is removed.

**Boundaries:** all device, task, automation, and plugin data are illustrative.
Automation forms are editable layout previews with Save explicitly disabled;
they do not save edited values or create routines. Plugin catalog and task/
enable-state changes live only in the click-through session. Run displays a
canned result without contacting a Gateway. Agent chats open the existing new
conversation preview with agent context, not a live model.

Agent settings opens the existing native Workspace page. Connection/Manage
opens native Connection, This computer/Permissions opens native Permissions,
and Devices opens native Instances. These actual captured companion pages
retain their disconnected reference profile; they do not inherit the fictional
connected Systems fixtures. Native Settings, the Owner menu, and unrelated
pages remain unchanged.

Browser proof covers all five pages in Light, Dark, and High contrast at both
1024x640 and 800x560 (30 combinations). It checks the actual responsive agent
column count, non-overlapping card footers, single-line conversation rows,
Sessions Add by mouse and keyboard, page navigation, reversible sample
interactions, and the portable export with no external requests. The compact
Agents view uses two columns instead of squeezing three narrow cards.

After the responsive correction, the normal full build passed, Shared tests
passed 3,989 (34 skipped), and Tray tests passed 2,897. Native-reference
regression passed all 136 combinations and four Owner-menu destinations.
One browser regression run timed out while the build was running; the rerun
after the build passed without further changes. Prototype validation reports
no errors and only the pre-existing sidebar spacing-token `"0"` warning.

### Native companion reference

The standalone **Settings | native companion** page contains 17 production
pages in Light and Dark:
Connection, Sessions, Skills, Channels, Instances, Cron, Event Stream,
Workspace (`agent:main`), Bindings, Config, Usage, Local AI, Voice & Audio,
Permissions, Sandbox, Diagnostics, and Settings.

The left navigation is extracted from `Windows/HubWindow.xaml`, with English
labels resolved through `Strings/en-us/Resources.resw` and the production SVG
artwork. Advanced contains Event Stream, the separate Agents group with its
nested main item, Bindings, Config, and Usage. This Computer and the
Diagnostics/Settings footer retain their source order. Only Chat navigation is
removed. Chat-related preferences inside Settings remain.

Settings now includes the real General, Chat, Notifications (including all eight
categories), Privacy, Local Gateway, Developer, and About sections. Long pages
retain their full captured scroll range. Native text, control templates, cards,
icons, and spacing come from the running application rather than replacement
copy or invented browser controls. Window-only capture excludes other desktop
windows. The computer name on Permissions is redacted.

Click navigation, expand Advanced and Agents, and scroll the page images.
The toolbar's Light/Dark selection switches between real native captures.
Pictured controls are not live: no setting, permission, install, download, or
gateway operation is performed. The sidebar is a source-derived click-through
adapter, not a running WinUI NavigationView.

**Coverage limits:** the capture profile has no paired gateway and disables
MCP/node mode. Gateway-dependent pages therefore show their real disconnected
state, not invented populated data. All navigation entries are exposed for
review, although the disconnected production app hides gateway-only entries.
Captures cover the pages' default states, not every expander, dialog, connected
state, or data-driven form. Images scale to the preview frame rather than
reflowing like a live WinUI page. High contrast explicitly displays a missing
native-capture notice instead of recoloring screenshots and claiming proof.
The upstream build-hash About view, event-timeline mock, and the other prototype
screens remain the earlier models, not native captures.

`native-reference.json` records the page source hashes, complete navigation
inventory, capture themes, redaction, and limitations. The portable artifact is
`prototype-export/index.html`. Before the separate-window change, browser proof
passed 136 route/frame/theme/window
combinations, exact navigation order, nested-group collapse, full-page scrolling,
all four companion footer destinations, and an export with no external requests.
Native capture manifests additionally verify every route's page marker, top
scroll position, and bottom position for long pages.

The local reference executable was built with GitVersion calculation disabled
and explicit `0.0.0-reference.869b21a4` version metadata after GitVersion stalled.
That targeted build did not replace full-repository validation. After the
requested punctuation cleanup in `AGENTS.md` and `DESIGN.md`, the normal
`build.ps1` passed on September 24: documentation validation and all five
projects (Shared, Cli, WinNodeCli, SetupEngine, and WinUI) succeeded.

The required closeout was rerun after `scripts/setup-dev.ps1` restored the missing
.NET SDK (10.0.401). Shared tests passed 3,989 with 34 skipped; tray tests passed
2,897. One queued-send lifecycle test failed in the first full tray run, then
passed its targeted rerun and the full rerun without any production-code edits.
Prototype validation has no errors; its existing Dashboard spacing-token `"0"`
warning is outside the companion replacement.

### Retained Dashboard and footer flow

The reviewed Dashboard shell now links to the remaining workspace destinations.
Its New session buttons use Segoe Fluent Icons Add (U+E710), with the requested
larger size and subtle styling. The original conversation and composer layout
and local Inspect edits are retained.

The Prototype canvas has five entry screens:

| Entry | Review coverage |
| --- | --- |
| Workspace | 22 content views: Home, Agents, two agent details, Dashboards, dashboard detail, Canvas, Systems, system detail, Automations, editor, Plugins, Skills, Sessions, Usage, Activity, Tasks, Meetings, Apps, Portals, Notifications, and More |
| Companion | 17 native-captured pages plus the retained upstream About model, with production navigation and Chat omitted |
| Onboarding | Security, gateway choice, capabilities, blocked readiness, progress, failure/retry, gateway-driven onboard handoff, completion |
| Command center | Navigation/action list based on `HubPageRegistry`, not a new health dashboard |
| Tray menu | Connection status, workspace, recent session, settings and action grouping |

Start at **Workspace**, use the five pinned pages, and choose **More** for the
remaining destinations and Windows surfaces. The owner identity
opens the floating account menu described below. Controls that would perform
real operations instead explain the preview boundary.

### Footer menu and separate Settings page

**Prototype:** Settings is a separate full-page destination within the existing
prototype canvas. It is not an overlay and does not open a browser window or tab.
All 21 entry points (Owner menu, More, Agents, Systems, onboarding, Command center,
and tray) navigate there. Usage, Channels, Permissions, and other deep links
select their corresponding companion page. Back returns to the previous
prototype screen.

**Production implementation intent:** Settings must open in a separate native
WinUI companion window, while the main workspace remains open. The prototype's
page transition represents this future window boundary; it is not a requirement
to replace the workspace in the shipping app.

The experimental browser-pop-up behavior and its player support have been
removed. `prototype-navigation-state.patch` preserves only the small player fix
that applies a deep link's state when navigating. If Colophon is reinstalled,
apply that patch in its `extensions/colophon` directory before regenerating the
canvas/export. The portable `prototype-export/index.html` embeds the player.
Run `node --test .agents/design/prototype-navigation.test.mjs` for its navigation
regressions. No production app code is changed.

Page-navigation proof passed all 21 entry points and their Back paths, 68 native
page/theme/frame combinations, and four offline footer destinations. No browser
pop-ups or page errors occurred. Navigation and player/export tests passed 25/25.

The standalone Settings gear in the Dashboard's left pane is removed. Settings
remains available in the Owner menu; the native companion's Settings page and
navigation entry are unchanged.

Notifications is a separate subtle Fluent bell button immediately to the right
of Owner / Personal workspace in the sidebar footer, matching the Mac layout.
It opens the existing Notifications page, not the Owner menu. The workspace
titlebar and conversation header have no duplicate notification button.
Native companion screenshot chrome remains the unmodified production reference.

The footer now follows the pinned upstream `app-sidebar-agent-menu.ts` identity
menu, instead of navigating directly to the earlier grouped Settings proposal.
Menu options and separators retain the reference order:

| Item | Windows prototype destination |
| --- | --- |
| Settings | Separate prototype page, initially on Settings |
| Usage | The separate page, directly on Usage, regardless of the previously selected page |
| Pair device | The separate page, directly on Channels |
| Get apps | Existing Apps workspace view |
| Connection event timeline | Right-floating connection window, replacing upstream System vitals |
| Help | Reference submenu: Documentation, Get help, Discord and View changelog |
| Build hash | About and build details, matching upstream `sidebar-build-chip.ts` |

The companion window follows `Windows/HubWindow.xaml` and `HubPageRegistry`,
not the previous Personal/This PC/Connections settings taxonomy. It includes
Connection, Sessions, Skills, Channels, Instances, Cron, the Advanced pages,
Local AI, Voice & Audio, Permissions, Sandbox, Diagnostics and Settings.
There is no Chat page in its navigation. The standalone Settings canvas entry
reviews the same page model. In production, closing the separate companion
window must not replace or reset the main workspace.

The connection window takes its structure from `ConnectionStatusWindow.xaml`:
operator/node state, gateways, credential summary, and event timeline with
Copy/Clear affordances. It floats at the right without navigating away from
the workspace. Events are fixtures, credentials are never loaded, Copy opens
the preview boundary, and Clear affects only the sample timeline.

Help retains the exact reference destinations:
`https://docs.openclaw.ai`, `https://docs.openclaw.ai/help`,
`https://discord.gg/clawd`, and `https://docs.openclaw.ai/releases`.
The build chip explicitly identifies `869b21a4` as the prototype's base
revision, not a claimed installed app version. The theme control in the footer
is a local visual preview; the canvas toolbar still selects the rendered theme.

These browser windows are visual models, not real OS windows. Click-away
dismissal is provided for the menu. Native MenuFlyout keyboard/focus behavior,
window dragging/resizing, and WindowManager lifecycle remain implementation
work. Help opens on click in this prototype rather than simulating native hover
submenu timing.

This expansion was checked against the actual locally hosted frontend at
`openclaw/openclaw@857173632fc708b8824c3129cad6abe6347eaa79`, its
`ui/src/app-navigation.ts` and `app-route-paths.ts`, plus this repository's
`HubPageRegistry` and connection, onboarding, MCP and architecture documentation.
It is **not a restoration of the rejected first draft below**.

The upstream fixtures for Systems, Plugins, Tasks and Meetings are incomplete;
Usage does not provide billing results. These limitations are visible in the
prototypes. Native feature parity is not assumed for tasks, meetings, plugin
management, portals, worktrees, or saved-dashboard management. Those surfaces
show a reference state or an explicit gateway handoff rather than fabricated
native success. Settings retains the known Windows subset instead of inventing
all Mac-only settings. Text fields are typeable fixtures, not functional search
or persisted forms. ComboBox selections are local visual previews and reset
when the scene rerenders; use the canvas theme toolbar for rendered light,
dark and high-contrast views.

### Production-derived Fluent page styling

The page mockups now reference the app's actual XAML controls and resource
usage, rather than treating every preference or filter as an ordinary Button.
The reviewed Dashboard sidebar, titlebar controls and conversation are unchanged.

| Production reference | Pattern applied in the prototypes |
| --- | --- |
| `SettingsPage.xaml`, `PermissionsPage.xaml` | Card rows with label/caption hierarchy, trailing ToggleSwitch, capability FontIcons, and an App theme ComboBox |
| `SessionsPage.xaml`, `UsagePage.xaml` | SelectorBar-style filters with selected accent indicators; bordered list cards; Usage total-cost hero, three metrics, provider breakdown and daily-cost section |
| `CronPage.xaml` | Schedule and agent ComboBoxes in the automation editor |
| `ConnectionPage.xaml` | Connected, approval-required and error cards with the production leading status-accent treatment |
| `SkillsPage.xaml` | Agent ComboBox and enabled/disabled skill Expanders |
| `WorkspacePage.xaml` | File-list / selected-file split for agent Files, using clearly labeled sample contents |
| `DebugPage.xaml`, `SandboxPage.xaml` | InfoBar-style status and scoped detail Expanders |
| Setup `ProgressPage.xaml` | Expandable sample Live activity |
| `App.xaml`, `FluentIconCatalog.cs` | Existing theme-resource token mappings, Segoe Fluent glyphs and high-contrast palette |

Page titles use the `title` token matching `TitleTextBlockStyle`. Page padding
uses spacing `5`; cards use `surface`, `line`, and radius `md`. Card row padding
is snapped to spacing `4` rather than introducing a separate 14px spacing value.
The 900px content cap is retained. The compact workspace preview uses equal
columns instead of the native fixed-width file list.

These are browser approximations of WinUI controls, not replacements for their
native templates. InfoBars deliberately use the existing neutral surface and
semantic glyph tokens; the native port must retain `InfoBar.Severity` and its
platform severity backgrounds. Selector previews are accessible pressed-state
buttons, not an implementation of native SelectorBar arrow-key behavior.
Switches and Expanders drive the existing scene state and support pointer and
keyboard activation. ComboBoxes do not claim to change theme, agent or schedule
behavior. No production C#/XAML or installed extension code was changed.

**Current validation:** 414 rendered state/theme/desktop-size cases and 1,198
declared scene-action checks passed, with no JavaScript errors or horizontal
overflow. A separate footer-flow pass checked every menu destination and all
18 floating companion pages across three themes and two frame sizes, including
overriding the previous page on Usage/Pair device, absence of Chat navigation,
click-away dismissal, the four Help URLs, and timeline Clear/Close behavior.
It also caught a modal stacking issue: the workspace is now an isolated stacking
context so preview dialogs remain above floating windows. Copy/dialog-close
checks pass in all six theme/frame combinations.

Current menu, companion and timeline screenshots were inspected, including
compact high contrast. The final offline export passed the menu destinations
and timeline dialog layering checks without external requests. Earlier focused
control proof also covered pointer/keyboard switches and Expanders, ComboBox
selection, selected indicators and theme-aware text.

Prototype validation has no errors and one existing warning for the
Dashboard sidebar's string `"0"` padding token. Shared tests: 3,989 passed and
34 skipped. Tray tests: 2,897 passed on the final full rerun. One intermediate
shared run failed
`Dispose_DuringInFlightHandler_DoesNotSurfaceObjectDisposedException`; its
targeted retry and the subsequent full shared/tray suites passed without
production code changes. The full `build.ps1` command initially stopped at
documentation validation because of U+2014 characters in inherited `AGENTS.md`
and `DESIGN.md`. The user-requested punctuation cleanup resolved that failure;
the subsequent normal full build passed.

Required commands were rerun with `OPENCLAW_REPO_ROOT` set to this worktree and
`OPENCLAW_TRAY_DATA_DIR` pointing to isolated test data:

```powershell
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

**Review gate:** Native XAML/C#/Reactor implementation has not started.
The remaining designs need review before implementation. Preview evidence is
not proof of native Mica, Windows accessibility, gateway behavior or UI runtime.

## Historical rejected draft

**Historical and rejected.** The rest of this document describes the first
speculative Windows pass. Its 26 scenes have been removed from the active canvas.
They were replaced with a single Dashboard translation test based on the
[source-faithful upstream reference](upstream-reference/README.md): preserve
the assistant switcher, pinned pages, sessions, account footer and conversation;
translate the controls, surfaces, selection treatment and typography to Fluent.
New session, session selection, navigation collapse and the assistant picker
are simulated. No Windows implementation is approved or changed.

**Mockups only. Native implementation and feature-gap scope are not approved.**

The user selected the full current Mac hosted Dashboard information architecture,
not a native-Mac-only refresh. When unavailable for the next design decision, the
user authorized autonomous work. Gap destinations therefore have clearly marked
future-state mockups rather than being silently omitted or implemented.

Open the repository's **Prototype** canvas to click through the scenes in Windows
desktop frames. Begin with **Dashboard workspace**; use **More** for unpinned
destinations, **Settings** for the complete grouped settings navigation, and
**Review feature gaps** for the review overview. The canvas Screen selector also
opens any standalone scene directly.

All data, approvals, service states, setup steps, inputs and results are synthetic.
Nothing connects to a gateway, reads user settings, captures audio/screens, runs
commands, installs software, changes credentials or uploads data.

## Authoritative reference

Mac source: [`openclaw/openclaw` at
`ba853de3369af457e5f7cb877174721968aabcbd`](https://github.com/openclaw/openclaw/tree/ba853de3369af457e5f7cb877174721968aabcbd).
This was a source audit, not a running Mac app comparison.

| Source | What it establishes |
| --- | --- |
| [`AppNavigationActions.swift`](https://github.com/openclaw/openclaw/blob/ba853de3369af457e5f7cb877174721968aabcbd/apps/macos/Sources/OpenClaw/AppNavigationActions.swift#L8-L45) | The normal Mac experience hosts the Dashboard. Native chat/navigation is experimental. |
| [`app-navigation.ts`](https://github.com/openclaw/openclaw/blob/ba853de3369af457e5f7cb877174721968aabcbd/ui/src/app-navigation.ts#L16-L73) | Full app route order and default pinned destinations. |
| [`app-navigation.ts`, settings groups](https://github.com/openclaw/openclaw/blob/ba853de3369af457e5f7cb877174721968aabcbd/ui/src/app-navigation.ts#L198-L262) and [`settings-sidebar.ts`](https://github.com/openclaw/openclaw/blob/ba853de3369af457e5f7cb877174721968aabcbd/ui/src/components/settings-sidebar.ts#L370-L482) | Grouped settings, contextual agent, search and connection footer. |
| [`ConnectionWindow.swift`](https://github.com/openclaw/openclaw/blob/ba853de3369af457e5f7cb877174721968aabcbd/apps/macos/Sources/OpenClaw/ConnectionWindow.swift#L3-L32) | Native Connection, Gateways and optional Debug tabs. |
| [`Onboarding.swift`, `pageOrder`](https://github.com/openclaw/openclaw/blob/ba853de3369af457e5f7cb877174721968aabcbd/apps/macos/Sources/OpenClaw/Onboarding.swift#L785-L800) | Welcome, gateway location, conditional installation and AI setup progression. |
| [`StatusMenuDescriptor.swift`](https://github.com/openclaw/openclaw/blob/ba853de3369af457e5f7cb877174721968aabcbd/apps/macos/Sources/OpenClaw/StatusMenuDescriptor.swift#L133-L236) | Status menu grouping, recent work, actions and footer. |
| [`ChatWindowShell.swift`](https://github.com/openclaw/openclaw/blob/ba853de3369af457e5f7cb877174721968aabcbd/apps/shared/OpenClawKit/Sources/OpenClawChatUI/ChatWindowShell.swift#L65-L140) | Shared native chat shell consumed by the experimental Mac route. |

Provider steps, plugin content, Canvas content and conversational caretaker setup
are runtime-defined. Their fixtures demonstrate layout families, not an exhaustive
inventory of every generated screen. An installed gateway can serve a Dashboard
version different from this pinned reference.

## Screen audit and Windows mapping

**Existing** means a dedicated Windows owner exists, not that every Mac action has
parity. **Partial** means related UI exists with different scope. **Gap** means no
dedicated matching page was found in the Windows page registry. Backend availability
is unverified, not asserted absent.

| Mac family and layout | Windows owner or analogue | Status and prototype coverage |
| --- | --- | --- |
| Agents gallery; contextual overview/files/sessions/skills/jobs | Agent routes, WorkspacePage, SkillsPage, CronPage | Partial. `hub` Agents and `agent` tabs. |
| Dashboard gallery and selected overview | Tray summaries and CommandCenterStateBuilder diagnostics | Partial. `hub` Dashboards. Diagnostic projection is not a standalone Command Center destination. |
| Systems inventory and machine workspace | InstancesPage, PermissionsPage, Connection diagnostics | Partial. `hub` Systems, `system`, `permissions`. |
| Usage filters, summaries and breakdowns | UsagePage | Existing core. `hub` Usage uses summary tiles and a synthetic breakdown; chart interactions are not modeled. |
| Automations list, editor and run history | CronPage | Existing jobs; broader automation parity unverified. `hub` Automations and `automation`. |
| Tasks active/recent lists | No matching dedicated page | Gap. `hub` Tasks; task detail hands off to a sample conversation. |
| Sessions list/search/detail, contextual Worktrees | SessionsPage and Chat | Partial; Worktrees management is a gap. `hub` Sessions tabs and `chat`. |
| Activity event feed | AgentEventsPage | Partial; live event stream is not persistent activity history. `hub` Activity. |
| Meetings library and summary/transcript reader | No matching dedicated page | Gap. `hub` Meetings and `meeting`; real transcript data is not supplied. |
| Plugins catalog/inventory, access review | SkillsPage and channel plugin results are only analogues | Gap for a plugin-management hub. `hub` Plugins and access dialog. |
| Skills library/dependencies and Skill workshop | SkillsPage | Existing library core; workshop gap. Plugins tabs and Settings Skills. |
| Apps gallery | No matching dedicated page | Gap. `hub` Apps. |
| Portals list and live content host | Canvas/A2UI are content-host analogues | Gap for management/publishing/access control. `hub` Portals and `canvas`. |
| Conversation rail, transcript, composer and detail sheets | ChatPage, ChatWindow and ReactorChatTimeline | Existing core. `chat`, thread actions, attachment, tool and question dialogs. |
| Full grouped Settings | SettingsPage, ConfigPage and subsystem pages | Mixed. All 29 destinations have distinct panels in `settings`. |
| Notification preferences and notification inbox | SettingsPage and NotificationsPage | Separate surfaces retained. `settings` Notifications and `notifications`. |
| Menu-bar status, recent sessions, approvals, capability actions | TrayMenuPresenter and renderer | Windows tray adaptation. `tray`. |
| Native Connection/Gateways/Debug and add/edit/remove flows | ConnectionPage, GatewayRegistry, GatewayDirectConnectService | Existing. `connection`, `gateway-editor`, removal/switch review dialogs. |
| Operator/node pairing and approval requests | Existing connection and approval owners | Existing core. `pairing`, `approval`, `connection-states`. |
| Native onboarding and hosted continuation | Windows onboarding and gateway-driven setup wizard | Partial. `setup`, `channel`, contextual Caretaker. Windows security/WSL gates retained. |
| Quick Chat, capture consent and detached conversation | Detached ChatWindow and screen capability analogues | Partial; capture/paste-back parity requires a decision. `quick-chat`. |
| Floating Canvas | CanvasWindow and A2UICanvasWindow | Existing host core. `canvas` contains illustrative content. |
| Voice/Talk and microphone permission/testing | Voice settings and VoiceOverlayWindow | Existing core with Windows OS semantics. `voice`. |
| Local model and execution-context settings | LocalAiPage and SandboxPage | Windows-specific controls retained. `local-ai`, `sandbox`, `system`. |
| Diagnostics, logs, update, About, recovery | DebugPage, diagnostic bundle and lifecycle owners | Existing or partial. `diagnostics`, `update`, `setup`, grouped Settings. |
| Browser panel/inspector and terminal | Browser proxy and gateway terminal are partial analogues | Dedicated panel parity unverified. `contextual` Browser and Terminal. |
| Agent Defaults, plugin Workboard, Lobsterdex | Config or runtime-owned content | Contextual, not fixed core routes. `contextual` tabs. |
| Browser-login import | Mac-specific integration; no Windows parity established | Consent/boundary concept only. `contextual` Browser import. |

Default pinned order is **Agents, Dashboards, Systems, Automations, Plugins**.
The complete upstream order is Agents, Dashboards, Usage, Automations, Tasks,
Sessions, Systems, Activity, Meetings, Plugins, Apps, Portals. The unpinned
destinations are under More. Chat is reached through conversations rather than
invented as a fixed primary Mac route.

### Settings inventory

| Group | Destinations, in source order |
| --- | --- |
| Ungrouped | OpenClaw, Profile, Appearance, Notifications |
| This PC (adapted from This Mac) | This PC, Permissions |
| Connections | Gateway, Channels, Communications, Talk, Devices, Cloud workers |
| Agents & Tools | Agents, Models, Plugins, Skills, MCP, Memory, Automation |
| Privacy & Security | Privacy & Security, Secrets, Approvals |
| System | Infrastructure, Labs, Advanced, Debug, Logs, Updates, About |

No dedicated matching Profile, Cloud workers, Memory, Secrets browser or Labs
page was established. Communications, gateway MCP, model accounts and unified
infrastructure are partial matches. These panels are future-state proposals, not
authorization to add their backends.

### State review

`connection-states` covers disconnected, connecting, pairing-required,
node-degraded, MCP-only, error and unsupported-gateway states. `system` covers all
four node/local-MCP enablement combinations. Pairing and command approvals have
separate approve/reject states. Setup includes blocked readiness, progress,
failure/retry and completion. Voice includes permission-needed, denied, ready,
listening and reply. Updates include progress, failure and restart-ready.

## Design and implementation boundaries

The existing `design.json`, `components.jsonc` and `principles.md` are unchanged.
Scenes use their typography, spacing, radii and semantic color names, including
existing Button, Field, Badge, Card, ChatBubble and ChatComposer patterns.
ComposerPicker and ComposerPickerOptions are consumed by the existing composer.
ChatThread is composed from the same bubble/composer patterns to avoid inheriting
unrelated seeded sample messages.

Layout widths derive from spacing tokens rather than new fixed pixel values.
Device-frame dimensions belong to the prototype tool, not shipping UI. Native
implementation must bind the tokens' WinUI resources, not exported preview CSS or
color swatches. Mica, OS accent, light/dark and native contrast-theme behavior need
real WinUI proof. Chat ships through Reactor, not copied component JSON.

Preserve `HubPageRegistry` routing ownership, `GatewayConnectionManager` runtime
ownership and `GatewayRegistry` per-gateway identity/credentials. Windows retains
one active gateway. Preserve device-token precedence, direct-connect rollback,
MCP-only startup without gateway credentials, and separate operator/node approval.
No responsibility may return to a god file after being marked closed in
`docs/ARCHITECTURE.md`.

The following remain review decisions: which gaps should be native, which should
hand off to the existing Dashboard, and which should be deferred. Break any
approved implementation into small ownership-preserving slices.

## Prototype limitations

- Text inputs are visual/typeable fixtures, not saved data or functional search.
- Chat's seeded Send and icon controls are visual. Use **Preview reply**,
  **Attachment preview**, **Tool detail** and **Question preview** to advance.
- Composer option lists are the seeded representative model options, not live
  gateway/session/reasoning options. No real model selection occurs.
- Settings theme buttons simulate selection. Use the canvas's Light, Dark and
  High contrast buttons to change rendered theme.
- The tool's classic Windows frame captions the outer window as "App". The scene
  itself supplies the OpenClaw title and contextual header.
- Scene-graph text navigation is pointer-driven; it is not evidence of native
  keyboard, screen-reader, focus, automation or accessibility behavior.
- Back/cancel/retry controls affect preview state only. No external links,
  account changes, permissions, purchases, downloads or destructive actions run.

## Validation

Prototype component/token/navigation validation and rendered-browser checks are
recorded in the session proof artifacts. A fresh standalone export can be produced
with the Prototype canvas **export** action and opened locally from
`prototype-export/index.html`. Generated export files are ignored because they
embed the renderer and should not duplicate the design source in a PR.

| Check | Result |
| --- | --- |
| Prototype schema, components, tokens and navigation | Passed, no errors or warnings; loaded repository source, not bundled sample. |
| Rendered Edge checks | 26 desktop scenes; 912 view/state/theme/size cases, no rendering errors or horizontal layout overflow. |
| Click-through actions | 298 declared scene actions passed. Does not include static controls inside seeded components. |
| Theme/frame coverage | Light, dark and high-contrast preview themes at 1024 x 640 and 800 x 560 Windows desktop frames. |
| `.\build.ps1` | Passed all five build targets. |
| Shared tests, `--no-restore` | 3,989 passed, 34 skipped, 0 failed. |
| Tray tests, `--no-restore`, isolated settings | 2,897 passed, 0 skipped, 0 failed on final sequential run. |

An intermediate full tray run failed
`QueuedSend_LifecycleStartBeforeAck_PromotesByIdempotencyKey`. That unchanged test
passed in isolation, followed by a clean sequential full build/shared/tray rerun.
No application or test code was altered to suppress the failure.

Native implementation, gateway/MCP invocation, OS interaction and real behavior
proof are **not performed** by this mockup deliverable.
