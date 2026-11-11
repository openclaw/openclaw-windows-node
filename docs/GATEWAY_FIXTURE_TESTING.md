# Fixture-backed application tests

The fixture Gateway fills the gap between unit/component tests and full system
E2E. It runs the real Windows app, Gateway client, chat provider, history loader
and native UI against a deterministic loopback WebSocket server. There is no AI,
WSL Gateway, provider account, real pairing, or live-network fallback.

The same `multi-session-browse` scenario powers interactive exploration and
automated tests. Its five sessions include an empty conversation, distinct
session identities and titles, and 240 mixed-height messages with an explicit
final-message marker. Supporting model, agent, health, usage and configuration
responses populate the application rather than just its disconnected shell.
Read-only `agent.identity.get` resolves each roster agent (and the agent the
`agent-creation` scenario creates) to its roster name with no avatar, so the app
renders generated faces; an unknown ID receives `INVALID_REQUEST`, as on a real
Gateway.
Read-only placement discovery returns an empty project catalog and one synthetic
local environment through `projects.list` and `environments.list`. It creates no
project or host resources; `projects.add` is rejected as a write.

The fixture also advertises read-only `sessions.search` over its synthetic
message text and accepts the scoped metadata search fields on `sessions.list`.
The main-search UI regressions exercise icon/shortcut opening and immediate
reopening, shared Settings search, draft-preserving dismissal, read-only setting
navigation and native conversation navigation from a message hit. Keyboard
regressions also exercise Enter on Show more/Retry, immediate cross-shell
reopening, absent Config sections and Dashboard-only skill results.
Cross-surface search waits for the previous flyout's existing close owner to
settle before opening the visible surface. A delayed destination reveal cannot
take focus from a newer search generation; an ordinary Settings search still
reveals its target while its original flyout closes. The immediate Enter/Ctrl+K
journey has no added delay or retry. Focused
cases cover a failed Config reveal followed by manual navigation to a section
with the same field name, and session title/recency changes while result or
input focus is active, including user-controlled result scroll. The
disappearing-result case renames the focused conversation out of the query,
checks input focus and the remaining selection, then repeats with no matches.
Config detail cancellation is also exercised directly on the real page before
its pending continuation, so a search reveal cannot retain the loading overlay.
The search-navigation scenario adds optional schema fields without corresponding
saved values and a read-only skill catalog. Read-response overrides model
failures and long descriptions. Skills are explicitly labeled Dashboard
destinations rather than retired native pages. The separately verified `/skills`
route on released Gateway `2026.11.1` does not enable its other unverified
Dashboard routes; unknown versions retain root fallback. No provider is contacted,
no skill mutation is accepted, and config writes remain rejected. Keyboard
journeys use the shared native SendInput helper, with the fixture opt-in,
foreground ownership and released-modifier guards intact. Open-popup Axe scans
cover empty results, matching results and source-status states, including a
horizontal-clipping assertion. This proves the application boundary, not a real
Gateway's indexing or Dashboard route compatibility.

The search popup has no footer or visible source-status/compatibility messages.
More/Retry stay beside the search input; UI Automation ItemStatus on that input
retains accessible source and empty-result feedback without a message block.
The long-description journey verifies that the complete last description can
be scrolled into view without a footer consuming space or covering it.
Keyboard expansion must focus and reveal the first newly added result without
test-driven scrolling or a jump back to an old selected row. Expansion appends
to the existing observable groups rather than replacing the native grouped view.
Native page-name proof searches About, General, notification preferences and
Config, checks plain destination names, captures the actual symbol icons and
navigates to the corresponding native pages without sending chat or config
writes. Notification preferences resolves to General. Settings split proof
discovers local MCP tools, rejects removed Event Stream routes without changing
the visible page, and toggles the three app-access consents through native
controls. It verifies their saved values after page reactivation without
enabling node capabilities. This is disposable-profile persistence proof, not
an installed-profile upgrade or Store migration.

## Build and explore

### Assistant continuity capture

The opt-in UI case
`SelectedAssistantAndConversationSurviveTransientRosterLossButNotAuthoritativeRemoval`
selects an assistant and conversation through normal UI Automation. It closes
only fixture-owned connections, holds the next connection's `agents.list`
responses, and then releases the unchanged roster. It then sends one message
through the ordinary composer and requires the fixture Gateway to record a
successful `chat.send` for the restored conversation. A second reconnect returns
an authoritative roster without that assistant and requires a fresh selection.
`FixtureGatewayServer.SetAgentsRoster`, `HoldAgentsList`, and `ReleaseAgentsList`
control only this synthetic producer; authentication and the production client
are unchanged. With `-Screenshots`, the case saves five owned-HWND PrintWindow
PNGs and adjacent UIA assertion metadata in the fixture artifact directory.
Without requested screenshots, the same UIA selection and protocol assertions
still run, but no pixel proof is claimed. Requested capture remains strict:
blank or unavailable native pixels fail instead of producing a fallback image.

Use the opt-in UI runner described below with this exact method-name filter.
These captures show actual Workspace controls with synthetic Gateway data, not
installed-app or live-provider behavior.

`UnsupportedAgentListing_PreservesLegacySessionsAndDefaultCreation` exercises
a different compatibility path. After a fixture-owned reconnect, the actual
client receives `unknown method: agents.list` while `sessions.list` still works.
Workspace must show the legacy sessions and enable new-conversation creation
with the Gateway's default target. The assistant selector remains unavailable,
because no authoritative agent roster was delivered. A pending or failed listing
is not treated as an explicitly unsupported method.

The disconnected assistant menu still exposes its existing connection guidance.
This does not authorize session creation or treat a pending connected roster as
available.

`VerifiedSetupBindingClearsBeforeNewConversationResponse` starts with a real
production handoff receipt and a synthetic model-verification response. It
holds the fixture's session-creation response and checks that the prior bound
setup warning is already cleared, then releases creation and observes the
ordinary selected conversation. Existing warning recovery coverage checks the
navigation path; the shutdown guard has a mounted WindowManager case. These
are behavioral handoff checks, not source-text ordering assertions.

Channels management lives in the Gateway Dashboard, and the native Channels
page no longer exists. Four cases in `GatewayFixtureDashboardUiTests` keep its
entry points honest through the real app: a legacy receipt whose destination is
Channels, `app.navigate channels` and the owner menu's **Pair a device** item,
which opens the Dashboard Devices page through `/nodes`.
The shared-search case also checks the Dashboard badge and opens Channels from
both Workspace and Settings, using Enter and Alt+Enter respectively. It checks
that local MCP discovery and `app.search` return the same Dashboard destination.
A valid receipt performs one `openclaw.setup.verify` and then one Dashboard
launch for `/channels` on the fixture Gateway's port. A receipt whose persisted
device identity is rotated while verification is held is rejected with the
"Gateway or agent changed" dialog (no Retry), consumes the receipt and never
launches. A simulated browser failure shows the matching Retry dialog, keeps a
receipt for explicit retry, and Retry launches and consumes it. The launch
evidence is the redacted line described in the isolation contract, because
fixture mode does not start a browser or serve the Dashboard page.
Replacement of the active Gateway and the final dispatch authorizer are covered
at launcher level by `SetupDashboardDispatchRequiresTheOriginalGatewayAndIdentity`,
not by a full-app case.

Build the current app first, then pass its executable explicitly. An installed
app is never discovered or selected implicitly.

CI builds the UI and integration test projects before running the fixture and
passes `-NoBuild` to reuse their matching configuration and RID outputs. Local
runs build the test projects by default. Neither path adds a separate MSBuild
`Platform` output tree. Each test-project invocation retains a 900-second default
deadline; `-TestTimeoutSeconds` accepts 1 through 1800 seconds. The ordinary hosted
mock-Gateway scenarios step explicitly uses 1500 seconds inside its unchanged
30-minute outer bound. Timeouts still terminate only the owned test process tree;
missing reports, zero tests, failures and skipped selected tests still fail proof.

The same hosted job also builds the unsigned non-Dev Release app and runs both
`CompletedRunLateSnapshotLeavesNativeComposerReadyAndQueueDraining` variants.
`Invoke-CiTest.ps1 -Configuration Release -ExpectedTestCount 2 -RequireAllTestsPassed`
requires exactly two passing cases. Captures, TRX and the compiled app assembly
for configuration/identity verification are retained under `TestResults\GatewayFixtureRelease`.
This targeted Release check does not replace the ordinary Debug fixture inventory.
Only that fixture build sets `RequireSignedCleanupHost=false`; signed release
packaging retains its normal signing requirement.
The same built Release output also runs the mounted reader layout proof at
480 and 1100 DIP in a separate exact-two-case invocation. It retains the
Standard resource profile and original predicates, with reports under
`TestResults\ReaderRelease`; it does not enable capture-driven resizing or
replace the Debug full UI lane.

```powershell
.\build.ps1
$app = '.\src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\win-x64\OpenClaw.Tray.WinUI.exe'
.\scripts\run-gateway-fixture.ps1 -AppPath $app
```

Use `win-arm64` instead of `win-x64` on a native ARM64 host. The launcher prints
the app PID, isolated profile, Gateway/MCP endpoints and artifact directory.
Leave it running while browsing Chat, Sessions, Settings and Configuration.
Press Ctrl+C to stop only this run and remove its synthetic profile.
For an unattended, bounded exploration/capture run, add `-DurationSeconds 30`.
The timer starts after readiness and uses the same cleanup path as Ctrl+C.

The default browse Gateway is read-only. Chat sends, session mutations and configuration
writes receive protocol errors, not simulated success. Local preferences such
as chat tool visibility can be saved inside the disposable profile. This is
not a fixture for the legacy WebView Gateway dashboard, installation,
uninstallation, host management, or node command execution.
The one write-shaped exception is `exec.approval.resolve`: the fixture validates
the fixture-owned approval ID and decision, records only that safe correlation,
and returns a synthetic acknowledgement without executing a command.

The native agent-creation UI test explicitly opts into a separate `agent-creation`
scenario. Only that scenario grants administrator scope and accepts one
`agents.create` request into an in-memory catalog. It validates name/workspace
fields, returns a server-assigned ID, and exposes it through subsequent
`agents.list` responses. It creates no files or real agents; other mutations
remain rejected. The test verifies the refreshed selector while retaining the
existing session and unsent draft. Normal interactive browse runs remain read-only.

Chat cancellation tests explicitly opt into the `chat-send` scenario. It issues
synthetic run IDs and accepts `chat.abort` only for a run belonging to the supplied
fixture session. The server can hold send acknowledgements and abort replies,
then release an abort successfully or with a protocol error. Run-correlated
lifecycle, assistant-delta, tool-outcome and structured-content chat events exercise the real client and provider
without contacting an AI provider. These controls do not enable writes in the
normal browse scenario.

The opt-in `session-mutations` scenario accepts in-memory `sessions.patch`,
`sessions.delete`, and parallel `sessions.create` requests. Creation uses a known
parent, returns a distinct server-assigned key/session ID, and exposes the empty
conversation through session-list and history reads without replacing its parent.
The response gate can hold creation while the UI navigates elsewhere. Composer
`/new` tests use this path to check initial Home -> new conversation -> Back/Forward
and delayed completion on Notifications. A newer page navigation fences the
pending selection: returning Home retains the original conversation, while the
created session remains available for explicit sidebar selection. No provider, transcript file, or real
Gateway is involved; other mutations and unsupported creation parameters still fail.

The host is `tests\OpenClaw.GatewayFixtureHost`, a plain .NET console app, not a
test container. The request-driven server and scenario live in
`tests\OpenClaw.TestSupport\Gateway`. Repeated reads and different request
orders work; this is deliberately not sequential packet playback.

MCP startup retries transient sharing and lock violations when reading the
published token file, within the existing startup deadline. Missing-file,
permission and other I/O failures are not hidden.
The token value is never included in readiness diagnostics.

Teardown retains process handles for the fixture app and its confirmed nested
WebView2 descendants before stopping the app tree. It reaps those owned
processes before deleting the isolated profile. Process, listener and profile
cleanup failures remain separate errors in the teardown aggregate; a profile
deletion failure does not hide an earlier process failure.

## Run the application smoke

Use an authorized, exclusive interactive desktop. The complete ordinary UI
selection includes native mouse-wheel input to the owned foreground fixture
window, so request that proof explicitly:

```powershell
.\scripts\test-gateway-fixture.ps1 -AppPath $app -NativeInputProof
```

For screenshots, use owned-window native capture (no Windows App CLI installation is needed):

```powershell
.\scripts\test-gateway-fixture.ps1 -AppPath $app -Screenshots -NativeInputProof
```

The script runs profile/preflight and polling tests, real-process MCP tests, and native UI
tests. It rejects failed, skipped, missing-report and zero-test runs. It needs
a Windows desktop and the same WinUI prerequisites as the existing UI suite.
There is no skip-as-success fallback when the desktop is unavailable.

`-NativeInputProof` enables the strict native-input opt-in only in the ordinary
fixture UI child process. Profile/preflight and released-profile children retain
that stricter opt-in at `0`, and inherited environment variables cannot enable it.
Existing fixture-keyboard journeys retain their own fixture/foreground guards. Without
the switch, the wheel-bearing selection fails closed; it never omits wheel
assertions or reports complete proof from UIA scrolling alone. The test still
checks the owned HWND, foreground state, exact injected event count and cursor
restoration. No machine-wide setting or CI identity override is used.

Add `-KeyboardProof` separately to select the existing keyboard-only fixture lane
as well. That option enables only its own child and still rejects failed,
skipped, and zero-test runs. The ordinary UI filter continues to exclude those
keyboard-only cases; `-NativeInputProof` does not select them.

### Saved Gateway ownership and released profiles

`GatewayManagement_UsesSavedOwnershipWithoutSetupState` renders native-only,
native-plus-WSL, and WSL-only saved profiles with no `setup-state.json`. These
cases use credential-free, MCP-only profiles: their package/distro markers are
synthetic and never authorize a connection, native package, or WSL process.
The real Settings page must show WSL removal only for the WSL cases. Onboarding
remains visible only when WSL is active, not when the selected Gateway is native.
The capture keeps the Local Gateway heading and final applicable action visible:
setup when no managed WSL record exists, or removal when one does.
With `-Screenshots`, the cases retain complete Settings captures and the actual
Shell tooltip. The tooltip capture first resolves icon ID 1 against this app's
owned native window, then hovers that icon; an app-name match alone is not proof
of ownership. Unavailable or blank native capture fails rather than substituting
a screenshot or computed tooltip.

For profile compatibility on an authorized disposable desktop, supply a stable
portable app explicitly. Both builds must advertise fixture isolation version 1;
an older unsupported binary is rejected before launch. This command selects only
the released-profile case, not the ordinary UI smoke:

```powershell
.\scripts\test-gateway-fixture.ps1 -AppPath $app -StableAppPath '<extracted-stable-app>\OpenClaw.Tray.WinUI.exe'
```

`ReleasedProfile_PreservesGatewayRecordsIdentitiesAndSettings` starts that released
app against the synthetic Gateway with native and WSL records saved but inactive.
The test requires the released app to persist its successful connection, generate
its operator identity, and save changed notification preferences. It copies
that released-format identity into the inactive synthetic records, then stops only
the fixture-owned process and opens the same profile with the candidate. Existing
record fields, active selection, identity hashes, and preferences must survive.
The normal connection timestamp may advance. This proves profile compatibility,
not installer/update execution, Store migration, real native/WSL lifecycle, or
production credentials.

The hosted Gateway fixture lane separately downloads the latest stable x64 portable archive
and verifies its published SHA256 before this case runs. `stable-release.json`
records the archive and candidate checkout identity; `stable-run.json`,
`run.json`, `profile-compatibility.json`, and `fixture-released-profile.trx`
retain the two executable identities and actual result in `test-results-gateway-fixture`.
No private keys or raw profile files are published. A missing baseline, digest
mismatch, failed case, or zero/skipped selection is not compatibility proof.

The dedicated `windows-latest` Gateway fixture job runs this script against its explicitly
built Debug x64 app after installing the Windows App Runtime selected by
`Directory.Build.props`. Its dedicated step passes `-NativeInputProof` to the
wrapper on that owned interactive runner. The wrapper enables native input only
in the ordinary fixture UI child and selects `Category=GatewayFixture` without
keyboard-only cases, including the
context-usage and chat-run correlation scenarios. The general UI test step
excludes that category so each scenario runs once. TRX, synthetic request logs,
run receipts and owned-window screenshots are uploaded in `test-results-gateway-fixture`
under `GatewayFixture`, including on failure. Other keyboard/compositor proof
opt-ins, production-shaped Release proof, real Gateway, WSL and MXC validation
remain separate; this hosted smoke does not replace them.

The disposable hosted runner configures a 1920x1080 desktop before UI tests and
verifies a work area large enough for the 1300x950 wide-window proof. Its actual
screen/work-area receipt is retained at `Desktop\geometry.json` in `test-results-gateway-fixture`.
The original two-width and visible-content assertions remain unchanged. The local
fixture runner does not change your desktop resolution.

The fixture job starts after change classification, independently of the native
UI job, whenever `ui_tests` is selected. CI Gate requires both jobs to succeed;
a failed, cancelled or unexpectedly skipped fixture job cannot pass the gate.
Fixture scenarios require successful build, runtime installation and desktop
preflight. Stable-profile compatibility still runs after a scenario failure when
those prerequisites succeeded, preserving separate results and screenshots.

For failed fixture tests, browse screenshots without downloading a ZIP: open the workflow run's
**Summary** and find **Mock Gateway screenshots** in the Gateway fixture job summary.
The index contains only failed tests and interrupted runs with a failure capture,
grouped by test with failure captures first. Click a capture filename to open its PNG
in the browser. GitHub sign-in is required. Each PNG also appears individually
under **Artifacts**, with a test/capture name and unique fixture run ID.
These previews use unarchived GitHub artifacts, retain the repository's normal
artifact retention, and are published only when the fixture step fails.
Successful tests do not create screenshot summary entries or individual PNG
artifacts; their captures stay inside `test-results-gateway-fixture\GatewayFixture`.
The `test-results-gateway-fixture` ZIP remains the complete diagnostic download, including
TRX, request logs, run receipts and original screenshot paths. If no capture was
produced, the summary says so; inspect the fixture step for a build/startup failure.

The browse fixture also supplies placement discovery reads: empty projects and
recents, each agent's synthetic workspace, and one available local Gateway
environment. Composer-created empty conversations can load their location
choices without an unsupported-RPC error. These are synthetic catalog reads,
not host filesystem discovery, remote worker capacity or placement mutation
proof. Unsupported methods and parameters still fail explicitly.

Ordinary browse/setup/context scenarios answer `question.list` with an empty
question collection, so the real client's question recovery can complete without
an unsupported-RPC error. This does not enable question creation or mutation:
interactive question lifecycles remain owned by the explicit interactive scenario.

`GatewayFixtureStartupTests` exercise parent and foreign browser-control startup
without launching the app. Cancellation and readiness failures must reap the
owned child, remove its readiness file, and release both listener ports.
They share the nonparallel `Gateway fixture environment` collection so another
test cannot allocate a just-released ephemeral port before the cleanup rebind
assertion. Occupancy, child-exit, readiness-file and rebind checks remain required.
The Shared browser-proxy regression runs the production client and a default-handler
control in separate test-host processes with explicit proxy settings. It does not
change the test runner's environment or cached default proxy.

The tests use local MCP for discovery, startup state and page navigation, and
UI Automation for the actual agent selector, Workspace session sidebar, scrolling and settings
controls. They verify:

- Real operator connection and populated sessions with node execution off.
- Independent profiles, endpoints, tokens and preferences in two concurrent
  application instances. Stopping one must leave the other usable.
- Repeated A/B session selection and session-specific visible histories.
- Session-scoped configured model reads retain a current model outside the manual
  selection policy. Its label and catalog-only reasoning remain available while
  its picker row stays disabled. The fixture's omitted view uses `default`, like
  the Gateway, so omitting `view: "configured"` cannot accidentally pass this case.
- Natural initial-tail visibility of message 240 before any explicit scroll
  command, plus final-message visibility after scrolling at two window sizes.
- Sessions/Settings/Configuration/Chat navigation, profile-only preference
  persistence, and the Sessions-page Open in chat action.
- A deliberately held history response arriving after a different session is
  selected, without replacing the visible transcript.
- Cancelling A through the native Stop action, then streaming and completing B
  despite A's late terminal and final events.
- A deferred abort failing after its run has ended, restoring the legitimate
  trailing final both during and after a newer streaming turn without ending
  or corrupting that turn. Replayed starts and deltas from the old run remain
  unable to resume over the new turn.
- A terminal run's late tool completion repairs only its retained tool while
  a newer run reuses the tool ID. Repeated background finals render once, with
  media kept separate from the newer streaming entry. Synthetic audio cards
  exercise metadata/rendering without playback, resolution or downloads.

The cancellation cases use process-scoped UI Automation, not global keyboard or
pointer input. They assert visible text and the native Stop/Send action as well
as supporting MCP state. Notifications and audio remain disabled in the fixture;
these cases do not prove toast presentation or TTS playback.

`app.chat.snapshot` is supporting evidence, not a rendering assertion. It
returns only the last 30 entries and does not prove the mounted UI selection.
The UI smoke checks the actual selected control and final text bounds within
the transcript viewport, rather than merely checking the current scroll extent.
For the delayed-history test, a passive `ChatComposerInput` UI Automation
`ItemStatus` acknowledgement records which loaded-history keys the render
consumed. It contains no messages, is empty outside explicit fixture mode,
and never changes chat state. The test waits for this render acknowledgement
before checking that the previously selected session is still visible.
Each UI test retains its 150-second body deadline (or its existing explicit
case budget). Failure capture, including synchronous entry into UI Automation
and PrintWindow, runs on a separate bounded worker. The failure owner releases
pending response gates, cancels late failure-screenshot publication, stops only its
owned app, and joins body/capture workers before disposing the server/profile.
It does not start another UIA capture when the original body is still running.

Failure phases are bounded to 10 seconds each. Set
`OPENCLAW_GATEWAY_FIXTURE_FAILURE_PHASE_SECONDS` to an integer from 1 through 10
to shorten that limit; other values retain 10 seconds. These teardown budgets
do not increase the body deadline or the fixture runner's 900-second limit.
If a native worker cannot unwind after owned app termination, or termination
cannot be confirmed, the fixture test worker exits nonzero instead of continuing
with stale UIA or deleting a live worker's profile. It does not use FailFast,
collect dumps, or kill unrelated processes. Such a worker exit is failed proof,
not an accepted missing TRX. Catchable capture/cleanup errors retain the original
assertion. A cached, redacted `failure-lifecycle.json` records the original failure
and phase outcomes without post-failure UIA reads; the usual full run report and
failure screenshot remain best-effort bounded diagnostics.

The repeated 12-cycle session-switch proof also saves
`session-switch-stages.jsonl` as transitions occur: cycle number, timestamp,
fixed fixture agent IDs (or unavailable/other markers), and row
presence/offscreen/selection flags. It waits for the selected-agent UIA
acknowledgement using the existing readiness budget after any needed native activation,
without reissuing selection or bypassing the sidebar. Repeated identical
observations are omitted. These cached observations do not establish the cause
of a missing row; all visible-history, native-selection and screenshot assertions
remain required.

Inspect the captured PNGs when claiming visual proof. UI Automation alone does
not detect every overlap, clipping or theme defect.

`HistoricalFractionalVoiceConversationRendersWithoutLoadingPlaceholder` uses
the opt-in `GatewayScenario.CreateFractionalVoiceHistory()` scenario. It adds
30 synthetic historical messages, including 18 fractional-millisecond Talk
rows with realtime voice provenance, block-array user text and no usage fields.
The real Gateway client protocol test checks every message's content, ID,
sequence and truncated timestamp. The native UI test checks all 30 projected
texts, visible final voice content, no loading placeholder, and switching to an
integer-timestamp typed control and back. Screenshots and the synthetic MCP
snapshot are retained with the run artifacts. This proves stored-history
compatibility, not microphone capture, live voice streaming or real Gateway
pairing, and does not use the reporter's private sessions.

## Automations proof

`GatewayScenario.CreateAutomations()` (host scenario `automations`) serves an
in-memory Automations catalog: interval, one-time, cron, MCP event, stream and
auto-disabled jobs for agent `main`, one job owned by agent `ops`, and none for
`research`. It answers `cron.list` filters, paging and agent scope (or every
agent when the scope is omitted), `cron.get`, a revision-guarded `cron.update`,
`cron.remove` and `cron.runs`. Request parameters follow the released Gateway's
closed schemas: an unknown property is rejected as `INVALID_PARAMS`, so
`cron.runs` has no `view` and `cron.remove` takes only the job id (the client
confirms a loaded revision with `cron.get` first). Morning brief runs carry chat
session keys. Nothing is scheduled or executed; `cron.run` only records a
synthetic run. Browse the scenario with:

```powershell
.\scripts\run-gateway-fixture.ps1 -AppPath $app -Scenario automations
```

The UI proof checks the route-agent filter and All agents, inline expansion with
a native CommandBar and recent-run chat icons, and the ContentDialog editor in
Light and Dark. A basic edit preserves hidden settings; pause still requires
confirmation. Separate cases cover a disconnected editor draft, an old save
completing after a newer draft opens, read-only and denied operators, initial
loading/error/retry, failed refresh with retained content, scheduler off, and
offline last-seen content. The large fixture adds 205 jobs to verify agenda
paging beyond 200 and a jump beyond the first inventory page. Populated, detail
and editor states are scanned with Axe.Windows using the fixture-owned app PID
and verified top-level HWND. Native windows in unrelated processes are outside
that scope; hosted cross-process and unknown providers retain their rule checks.
Do not treat a `ProviderDescription` PID as the element's owning process: it can
identify the client-side proxy. Accessibility failures are saved and asserted
after the behavior sequence so they do not prevent remaining popup/toast captures.
Diagnostics are bounded and sanitized: scan/item ordinals, rule identifiers,
process/window ownership, scope decisions, fixed reasons and numeric geometry or
scroll state. They do not log names, raw property/provider dumps, editor ancestry
or child content. Scope filtering follows scan metadata/count validation and
never excludes incomplete results. It excludes only positively identified
unhosted foreign windows or a verified owned scroll-viewport edge clip for
`BoundingRectangleSizeReasonable`; unknown ownership or geometry retains the check.
The empty-proxy predicate remains covered by unit tests but is inactive in live
scans: content absence is unknown, so no `BoundingRectangleNotNull` violation is
excluded as an empty proxy.
`AutomationsResponsiveLayoutTests` separately mounts the real Reactor page at
page-host widths 1400, 1000, 937, 760 and 520 DIPs, with a synthetic Gateway socket.
It checks responsive column/row counts, horizontal bounds, one-row mast actions,
and ellipsis/tooltips for row titles. The Wide case admits operator admin after
initial read-only rendering, checks the live New style/accent fill, then resizes
Wide/Medium/Wide and transitions populated/empty/populated without replacing the
Reactor root. Refresh/More must remain neutral and retired menus must be detached.
With
`OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS=1`, it opens only its own Light proof window
and saves real owned-HWND `PrintWindow` captures beneath
`OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS`; this mode requires the coordinated desktop
lock and explicit native-input opt-in (`OPENCLAW_RUN_KEYBOARD_PROOF=1`). The
capture path activates only its newly created proof window before initial focus;
later resize captures still require that owned window to remain foreground.
Select `Category=ProductionResources` separately from the external-app
Automations theory and from the mixed UI suite. All mounted native runs, including
offscreen runs, require the authorized serial native slot. Each case uses its own correctly sized offscreen client viewport,
not the shared fixture's 1x1 window, so layout and containment checks remain faithful.

The steady-resource lane covers responsive widths, toast/remount lifecycle,
same-control focus retention, editor saves, full run results and hero summaries.
It uses
`OPENCLAW_UI_TEST_RESOURCE_PROFILE=Production`: the existing `UIThreadFixture`
startup callback loads actual `App.xaml` resources once, after the `Application.Start`
callback returns and before readiness. Markup errors fail setup. Individual page mounts
do not replace or restore the application resource graph. Pooling, same-host
resize checks, retired-flyout cleanup and resource/style assertions remain enabled.
This models production resource ownership; it does not establish a native crash
cause or repair.

After building the matching UI-test output and acquiring the native slot, run
in a dedicated PowerShell process (use `win-arm64` on ARM64):

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
$env:OPENCLAW_UI_TEST_RESOURCE_PROFILE = 'Production'
$env:OPENCLAW_UI_TEST_APP_THEME = 'Light'
$env:OPENCLAW_RUN_KEYBOARD_PROOF = '1'
$env:OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS = '1'
$env:OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS = Join-Path (Get-Location) 'artifacts\automations-mounted'
.\scripts\Invoke-CiTest.ps1 -Project tests\OpenClaw.Tray.UITests `
    -Runtime win-x64 -ResultsDirectory TestResults\AutomationsSteadyResources `
    -TrxFileName AutomationsSteadyResources.trx `
    -Filter 'Category=ProductionResources&Category!=AnimationPreferenceEnabled&Category!=AnimationPreferenceDisabled' `
    -RequireAllTestsPassed -HangTimeoutSeconds 300
```

The unset/default `Standard` profile preserves the legacy synthetic resources.
CI runs the two profiles in separate test processes. A profile mismatch fails
the selected test, rather than skipping it or silently using fallback resources.
`ChatThemeProofScope` rejects replacement in the Production profile.
Existing chat/markdown scope replacement and synthetic HighContrast projection
are unchanged in the Standard lane. Do not mix them into steady-resource proof.

The separate real-app `Automations_HeroWashScrollsWhileTheBackplateStaysAnchored`
case tracks corresponding gradient pixels through a settled partial scroll,
then verifies that the hero scrolls out of view and compares the plain page
margin before and after. Synthetic pixel controls reject a viewport-anchored
gradient even if its text moves.
It does not expect the retired viewport-wide wash to remain behind the content.

The separate `Automations_ResponsiveShellWidthsHaveDistinctMeasuredFrames`
case captures reachable native page-host widths and the unchanged shell minimum,
including a collapsed-pane frame followed by pane restoration. It records
`GetDpiForWindow` scaling, requested window DIPs, measured host DIPs, canonical content width/tier,
and window rectangles in pixels/DIPs. `SetWindowPos` must change actual bounds;
unchanged/clamped frames or duplicate image hashes fail rather than claim a
narrow host. The normal shell minimum is not relaxed for proof.
The host measurement uses the real ScrollPattern ancestor of the exposed
`CronPageMarker`, not an assumed UIA peer for a Reactor Grid or ScrollView ID.
Before resizing, the case records the actual marker ancestor chain and captures
the loaded baseline. Missing ownership stops the shell case without resizing.
Initial window widths are 1661, 1261, 1021 and 1000 DIPs. For desired host widths
1386, 1008 and 754 DIPs, at most two measured-host corrections adjust window pixels
by the actual DIP deficit times DPI scale. Two stable host reads determine the
page tier independently of changing navigation/chrome dimensions. Initial and
corrected targets, loaded PNGs and JSON are saved before strict tier, final-pixel
and distinct-frame assertions. The current bounded parent pass runs Light only.
UIA helper classification cases use a nonparallel collection, without starting
their own WinUI fixture. Parallel UIA helper and mounted geometry collections
produced a native MeasureOverride COM failure; serializing those owners preserves
all checks and allows the combined 24-case lane to pass.
The Light/Dark behavior theory records each state as captured or failed, preserves
owned-HWND failure captures and accessibility diagnostics, and asserts failures
after attempting the remaining states. Dark runs only after the Light proof passes.
Before Remove and during its dialog scan, transient UIA menus/input-site peers
are recorded. Visible menu peers under the modal fail; typed/content-bearing
accessibility violations are never treated as empty-proxy exceptions.
Weekly-field proof waits for the selected combo peer to reconcile and reacquires
the field before applying the unchanged clipping guard. Error-announcement proof
records every owned-hub notification, captures the permission-error toast promptly,
then verifies ImportantAll independently of UI capture timing. Error toasts persist
past the normal four-second lifetime and disappear only after Close; no duplicate
global notification or stale page error bar is accepted.
Light and Dark captures include the plain new editor, editing with hidden
settings, Copy as new, Pause, and Remove. Remove checks the actual fixture
Gateway display name, its Agent/Schedule/Next run facts, alert-dialog semantics
and initial Cancel focus. Toast proof covers queued IDs, already-running
refusals, removal and automatic dismissal. These screenshots are review
evidence, not a declaration of visual matching before design-coordinator review.
No browser or clipboard handoff is invoked by UI proof. Run only with exclusive
desktop access:

```powershell
$env:OPENCLAW_RUN_GATEWAY_FIXTURE_UI = '1'
$env:OPENCLAW_GATEWAY_FIXTURE_APP = (Resolve-Path $app).Path
$env:OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS = Join-Path (Get-Location) 'artifacts\automations-proof'
$env:OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS = '1'
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj -r win-arm64 -p:Platform=ARM64 -p:DevBuild=false --filter 'FullyQualifiedName~GatewayFixtureUiTests.Automations_' --logger 'console;verbosity=normal'
```

On x64 use `-r win-x64 -p:Platform=x64`. Artifacts include owned-window
captures of each state. This proves the app/fixture wire path, not production
Gateway persistence or scheduling.

Published Reactor preview.12 and preview.16 do not theme ContentDialog correctly
in Dark. Preview.17 includes the
[upstream fix #1389](https://github.com/microsoft/microsoft-ui-reactor/pull/1389).
Before capture, verify that `project.assets.json` resolves the pinned
`Microsoft.UI.Reactor` and `Microsoft.UI.Reactor.Advanced` versions, and record
package provenance with the evidence. NU1603 is not successful integration, and
exact version resolution alone does not establish app API compatibility. For a
direct restore, first bootstrap preview.17 as described in
[Reactor compatibility proof](WINDOWS_NODE_TESTING.md#reactor-compatibility-proof).

The same filter includes editor lifetime regressions: an unsaved draft survives
reconnect and a delayed save cannot close a newer editor. Host-side
`HoldResponses`/`ReleaseResponses` gates delay real fixture replies; they do not
bypass page callbacks or supply the UI behavior being asserted.

## Systems inventory proof

The `systems-inventory` and `systems-empty` scenarios back the native Systems
page. `systems-inventory` serves `environments.list`, `system.info`, `node.list`
with host statistics, pending node and device pairings, and presence beacons
for connected apps. Session rows carry placement and exec
bindings so machines show related sessions. Timestamps are relative to the
current time, and host statistics change between reads. Node rename and removal
still receive the read-only protocol error; Systems routes pairing decisions to
the Connection page and opens deeper machine and desktop administration in the
web interface. `systems-empty` returns an empty inventory.

```powershell
.\scripts\run-gateway-fixture.ps1 -AppPath $app -Scenario systems-inventory
.\scripts\run-gateway-fixture.ps1 -AppPath $app -Scenario systems-inventory -Theme Dark
```

Navigate with local MCP `app.navigate` and `{"page":"systems"}`, or open one
machine with `{"page":"systems:node:fixture-studio"}`. `-Theme` (Light by
default, or Dark) sets the isolated profile's app theme, because local MCP cannot
change it.

The page is one scroll, not a master-detail list. Select an inventory row to
expand it inline; other rows can remain expanded independently.
`systems:<id>` still opens exactly that row, and an unavailable id keeps its
missing-selection explanation. "Devices and Gateway" always shows the full
inventory in online-first order, with no search, status filter or sort controls.
The shared hero reports this PC's observed Gateway attachment. Online and Issues
are its only summary tiles; backup reads and badges remain closed. Pending pairing
requests appear first as separate Wants to join rows. Other attention badges stay
on their actual machine/worker rows. Issues reveals the first badged row without
hiding the inventory; there is no separate Issues view. Workers and Connected
apps retain their groups. Pairing review routes to Connection, not inline approval.
Presence does not create machine rows or connection topology.

For native mounted-control proof on an authorized desktop, use the host's RID
(`win-arm64` on native ARM64):

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj `
  -r win-arm64 --filter 'FullyQualifiedName~SystemsPageProofTests'
```

This covers inline row invocation, the minimum-height scroll, pending recovery,
read-only access, resource disclosure, management confirmation epochs, the single
page-level direct Open in web action, Issues jump without hidden inventory, independent
expanders and narrow recovery-action layout, and the Capabilities column width
and gap. It is not screenshot proof. Capture the hero, row badges and
expanded row in light, dark and narrow layouts from the current fixture build. Verify
the native CommandBar overflow and keyboard focus, collapsed Diagnostics,
separate CPU count/load and the icon-only chat action. The fixture cannot prove
real pairing or live desktop control.

`GatewayFixtureUiTests.SystemsDesign_RealAppLightAndDark` launches an isolated
real app for each theme and captures the loaded page, expanded machine, Add
system dialog and cancelled Forget dialog. Set `OPENCLAW_RUN_GATEWAY_FIXTURE_UI=1`,
`OPENCLAW_GATEWAY_FIXTURE_APP` to the current executable and
`OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS` to an evidence directory before selecting
that test. Captures use the existing owned-window capture helper. Mounted
`RenderTargetBitmap` exports can omit compositor surfaces, opacity or inherited
theme brushes; do not treat them as equivalent native screenshot evidence.

`SystemsPage_OmitsConnectionMapInLightAndDark` verifies that the fixture-mounted
page has no connection-map elements in either theme. `SystemsInventory_ExistingSeparatedCards`
captures separated inventory rows, then scrolls the expanded Gateway's related
sessions and Diagnostics into view before capture. `SystemsHero_TopAndScrolled`
captures the scrolling hero with an unchanged scroll viewport.
`SystemsHero_FullHostLightAndDark` captures wide and medium native shell widths
in both themes. Widths below 720 use the existing mounted viewport route without
lowering the production 800-DIP minimum. These renamed hero cases are source-only
until their granted lane executes; earlier backdrop screenshots do not prove the
new header. Fixture captures remain synthetic proof.

Add a system opens three instructions and forwards to Connection. Forget names
the node, displays only reported platform, last-seen and approval facts, and
defaults to Cancel. A successful rename or forget shows a four-second in-page
notification; errors remain in the dialog and are announced assertively.

The page polls the selected machine's reported measurements every `OPENCLAW_SYSTEMS_POLL_SECONDS`
(default 15, minimum 5) and reloads the inventory once per burst of Gateway
events, `OPENCLAW_SYSTEMS_EVENT_REFRESH_MS` (default 1500) after the first one.
Paired Windows nodes publish host statistics immediately after handshake and every
`OPENCLAW_NODE_HOST_STATS_INTERVAL_SECONDS` (default 60).

Node wire fixtures must therefore correlate command replies rather than assume
the next frame is a reply. `NodeExecDenialWireTests` acknowledges only validated
`node.event` requests for `node.host.stats`, using the Gateway's
`handled: true, reason: "updated"` payload. Wrong invocation ids, unexpected
methods or events, and malformed statistics fail the fixture. Continued
statistics cannot extend its existing invocation cancellation deadline.

## Targeted context-usage proof

`ContextFooterReconcilesLiveReplayHistoryAndAuthoritativeUsage` opts into
`context-usage-snapshots`. It uses the existing isolated app/server ownership,
native composer, real client/provider/history loader, and mounted transcript.
Only two exact synthetic `chat.send` prompts are accepted in order; arbitrary
prompts, additional sends, attachments and other mutations remain rejected.
The normal browse scenario remains read-only.

The test holds the selected session's initial history while sending a
1,000-token request followed by a 1,500-token request. The second final is followed by an identical identityless,
undated replay, then the held matching history at T-1 reports 1,000 tokens and
20%. The history includes the complete transcript prefix, and the test checks
that the final reply remains the last assistant entry. Its visible footer must
remain `1.5K/5.0K (30%)`. It then exercises:

| Listing timestamp | Wire usage | Required mounted footer |
| --- | --- | --- |
| T+10 | input 12, output 480, missing total, `totalTokensFresh=false` | `1.5K/5.0K (30%)` |
| T+5 | valid total 800 | `800/5.0K (16%)` |
| T+12 | valid total 600 | `600/5.0K (12%)` |
| T+11, response held until after T+12 renders | valid total 1,200 | `600/5.0K (12%)` |

A following `sessions.changed` request, with its reply held, acts as a wire
barrier: all client subscribers for the preceding listing have finished before
the native assertion, and another reply cannot repair a rewind before capture.
No extra production debug command, provider replacement or timing sleep is used.
Each asserted state always retains an owned-HWND PNG and adjacent JSON with
footer UIA bounds, text and image SHA256, even without the screenshots flag.
`context-usage-proof.json` records the scenario and supporting final snapshot.

Run from the worktree on an interactive Windows desktop (use `win-x64` on x64):

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
$env:OPENCLAW_RUN_GATEWAY_FIXTURE_UI = '1'
$env:OPENCLAW_GATEWAY_FIXTURE_APP = (Resolve-Path '.\src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\win-arm64\OpenClaw.Tray.WinUI.exe').Path
$env:OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS = Join-Path $env:OPENCLAW_REPO_ROOT 'TestResults\context-usage-artifacts'
# Keep this invocation's disposable profiles inside the worktree.
$env:TEMP = $env:TMP = Join-Path $env:OPENCLAW_REPO_ROOT 'TestResults\fixture-runtime'
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'FullyQualifiedName~ContextFooterReconcilesLiveReplayHistoryAndAuthoritativeUsage' -ResultName 'context-usage-native' -RuntimeIdentifier win-arm64
```

For a separate bounded computer-use capture, start an interactive host console:

```powershell
.\scripts\run-gateway-fixture.ps1 -AppPath $env:OPENCLAW_GATEWAY_FIXTURE_APP -Scenario context-usage-snapshots -ArtifactsDirectory $env:OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS -DurationSeconds 120
```

Select **Fixture Main**, then **Fixture: context usage Initial**. Send
`Fixture usage: first request`, then `Fixture usage: repeat request` using the
native composer. Enter `missing-total`, `lower800`, `lower600`, and
`delayed1200` in the **host console**, capturing the footer after each changed
sidebar label appears. These controls only select synthetic listing replies
and notify the real client. Unknown console controls do nothing. `stop`, Ctrl+C
or the duration limit cleans up only this app/profile/server. The host prints
its exact PID and artifact path. The interactive host does not hold initial
history or transport-delay a listing; use the automated case for those ordered
proof assertions, and inspect its seven images before claiming complete proof.

The separate accessibility pool remains required, for example:

```powershell
.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'Category=Accessibility' -ResultName 'context-usage-accessibility' -RuntimeIdentifier win-arm64
```

That existing gate launches its own isolated app and Axe.Windows scans. It needs
an unlocked desktop, Windows App SDK prerequisites, and the current app executable,
PRI and compiled page resources beside the native UI test assembly. It does not
use this Gateway scenario or prove usage reconciliation. Run it serially with
the fixture UI test and computer-use, not concurrently on the same desktop.

## Usage analytics proof

The `usage-analytics` scenario opens the Settings Usage page against synthetic
`sessions.usage` and `usage.status` replies. Data is deterministic and anchored
to the requested date range, so every preset shows populated charts. Twelve
sessions cover several agents, channels, providers and models, plus cache,
error and tool activity. Every other scenario also answers `sessions.usage`,
which the Gateway does not advertise in `hello-ok`. The fixture models the
native page's range, detail and bounded browsing reads, not web-only heatmaps,
creator attribution, recent/multi-session selection or prompt-context analysis.

```powershell
.\scripts\run-gateway-fixture.ps1 -AppPath $app -Scenario usage-analytics
```

Enter one of these modes in the host console, then select Refresh on the page.
For unattended runs, pass `-UsageMode <mode>` to start in that mode:

| Mode | Replies |
|---|---|
| `populated` | Complete usage with sessions, details and provider plans |
| `empty` | A complete report with no sessions |
| `partial` | Lower-bound totals with `cacheStatus` pending and stale sessions |
| `legacy` | `sessions.usage` fails as an unknown method, like a Gateway older than any setup installs, so the page shows its update notice and provider plans only |
| `released` | Like released Gateways through 2026.10.9, including `extended-stable` 2026.9.36: `sessions.usage` rejects the overview parameters with the released schema error, so the page retries with full rows and shows its basic view without search, sort or paging |

The app remembers unknown methods for the life of a connection. After leaving
`legacy`, reconnect from Connection settings before refreshing, or start a new
run with `-UsageMode`.

Disconnect the app from Connection settings to show the disconnected banner.
The fixture proves rendering and the native client contract, not a real
Gateway's session aggregation or cost estimates. **Open in web** and the
row's **Open chat** button use the app's normal Dashboard launcher and chat routes;
the fixture does not serve the Gateway's web Usage page, so prove those
handoffs against a real Gateway.

The native Usage page is a read-only analysis surface. The default installed
Gateway view has no server search, sort or paging. It previews ten loaded rows,
then reveals ten more at a time; the coverage note distinguishes a reported
total from an unknown population beyond the 1,000-row cap. The advanced view
adds those server controls, without pretending a loaded-row drilldown is an
exhaustive search.

The hero shows estimated range cost and contributor details only when reported
data supports them. Agent cost shares require complete priced totals; independent
model aggregates do not claim an agent-to-model relationship. The header retains
Open in web and Export CSV actions, with the range bar directly below it.
Cross-year ranges retain both years; the tooltip and accessibility help
retain the full dates and reporting zone. On Wide hosts, custom date pickers
sit beside the presets, with server search on a separate row when available.
Medium and Narrow hosts retain stacked controls. **Options** holds reporting-day
boundaries and session-family counting. **Daily usage** contains cost/tokens,
chart/table, token/cache facts and defined activity counts. Provider allowance
windows at or above 75% carry a **Near limit** badge on their allowance row,
with its **See what's using it** action. Missing allowance percentages are not
zero usage, and billing amounts or absent/zero billing limits never invent an
allowance percentage. Disconnected rows retain their last-reported context.
There is no separate Needs you section. **Usage breakdown** retains all contributor
buckets behind a top-five plus Other legend and opens loaded sessions. Session
details expand inline, and the trailing row chat button passes the exact key.
No chart selection changes the requested range.

Usage facts come from authenticated Gateway reads, not local inventory or
fixture generators in production:

| Gateway owner | Native projection and surfaces |
| --- | --- |
| `sessions.usage` range read | Cost/token totals, reported or loaded session count, dated trend, contributor buckets and session rows |
| `sessions.usage` single-key read | Expanded session model, token/cache counters, messages, tools and errors |
| `usage.status` | Independently scoped provider allowance windows, reset times, plans, errors and billing amounts |

`UsageJsonParser` preserves missing numeric counters separately from explicit
zero. Charts and the metric-aware daily table contain only reported dates;
missing calendar days are not synthesized as zero-cost rows. A reported row with
a missing selected counter remains unavailable. The 30-day mast names the first
and last reported dates and states how many days were reported. Both the daily
series and headline totals come from the same `sessions.usage` snapshot, never
from a separate `usage.cost` read. Provider windows
or billing entries without valid numbers are omitted instead of inventing a
zero allowance or balance. Missing aggregate counts render unavailable, and CSV
cells stay blank for absent counters. Merged contributor counters are known
only when every subtotal reported them; incomplete shares and extrema are
withheld. Missing session usage does not imply a Gateway computing status, and
a reported nonzero population cannot become an empty success or complete CSV.
Calendar ranges, local/UTC formatting,
selection, connection/loading state and presentation-only ranking are client
state or deterministic transformations, not additional Gateway facts. Synthetic
records are confined to the test Gateway scenarios.

`PageResponsiveLayout` selects tiers from the page host width, not the window:
Wide starts at 1,000 DIP, Medium at 720 DIP, and Narrow supports hosts down to
480 DIP. The daily/allowance stage and contributor/detail pairs stack below
Wide. The hero uses a card-local accent tint, not a page-wide glow.
Native Border padding
owns their insets; bounded explanatory text stays left-aligned.
Session and provider card rows have separate rounded borders with exactly
`PageResponsiveLayout.GapInner` (DESIGN spacing2, 8 DIP) between adjacent cards,
including expanded session details. Native session and contributor expanders
retain independent open states without closing other panels or scrolling on
ordinary expansion. Session panels reuse loaded summaries; only the active
detail read is controller-owned. Native Expander animation follows Windows
animation preferences without a separate entrance-transition host.
Dense tables and legends keep their own spacing. The daily table grows to its
content and uses the page's scroll viewer, not an internal scrolling region.
Average and range-wide peak claims remain withheld when reported dates do not
cover the requested calendar range. When available, the daily-average value is labelled directly on the dashed plot line as well
as in the card header and chart accessibility description. Bars use the solid
native accent. Left/Right moves native focus and day selection within the chart;
each bar exposes its date and available counters. The
secondary activity strip uses four columns or a two-by-two grid, with
single-line trimmed labels and full-text tooltips. A metric pivot on its own
row aligns its first label with the card heading. The session-list badge counts
the actual loaded rows; a separate line states shown and loaded counts. The
range-wide population remains in the mast and range facts. Incomplete totals
have a partial tag and reason immediately beside the headline, while missing
prices retain their distinct explanation.

CSV export re-reads the selected range before opening the system Save dialog.
If a sessions file would omit rows or still-computing usage, a native
ContentDialog offers **Export daily totals** or **Close**. Its coverage facts
come from the export read, not a request limit. When no total was reported, the
dialog says so. The daily alternative keeps the original range and must pass
its own completeness check; it is not a bypass for partial data.

After a successful save, a page-local in-app notification shows the actual saved
file name and **Open folder**. Reactor's announcement region announces errors
assertively. Actionable results dismiss after seven seconds and passive errors
after four (override with
`OPENCLAW_USAGE_NOTIFICATION_MS`). Cancelling Save produces no success
notification. Gateway/page changes fence pending export work, and missing prices
remain blank CSV costs rather than zero. Suggested file names include the
actual reporting dates.
Results sit bottom-center, 20 logical pixels above the page edge, at up to
480 logical pixels wide. Native flyout fill/stroke and ThemeShadow provide
their neutral surface; only the severity icon is colored.
The export dialog keeps its frozen facts while resizing. Its footer stacks
primary-first on Narrow. The dialog body reconciles viewport updates through
Reactor's released overlay lifecycle.

For deterministic light/dark captures and interaction checks without a shared
Gateway, use the mounted native page tests:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
$env:OPENCLAW_VISUAL_TEST = '1'
$env:OPENCLAW_VISUAL_TEST_DIR = '<absolute-local-evidence-directory>'
dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj -r win-arm64 --filter 'FullyQualifiedName~UsagePageRenderingTests|FullyQualifiedName~UsageChartColorTests'
```

Select `win-x64` on an x64 host. The mounted matrix includes installed,
advanced, sparse, bounded, empty, offline, loading, first-read failure,
refresh failure, no-read-access, unsupported and computing states. The page
issues reads only, so a connection with read permission but no write permission
has the same analysis controls. These captures prove the native renderer over
recorded responses, not a live provider account, real Gateway or browser launch.
The export popup tests invoke the real menu/dialog/notification workflow but
intercept file saving and folder opening, so they do not mutate user documents.
The mounted resize cases exercise 1,400, 1,000, 760 and 520 DIP hosts and return
to Wide, checking column counts, sibling bounds, text clipping/ellipsis,
one-row mast actions, card insets/alignment and export feedback. These are
geometry/renderer checks, not real-shell screenshot acceptance.

Pinned Usage keyboard proof retains main's native tab cycle: pane/history,
title bar, Dashboard, Notifications, then visible page content. Exact intermediate
focus and a reverse/forward content roundtrip guard against skipped controls and
focus traps; visual adjacency is not the native XAML tab order.

`UsageResponsive_RealShellCapturesMeasuredWidthsAndCollapsedNavigation` uses
the isolated released-Gateway fixture and owned-HWND PrintWindow capture.
Its per-state manifest records measured host/content widths, the shared tier,
window rectangles in pixels and DIP, and image hashes. The Hub's unchanged
800 DIP window minimum and expanded pane prevent a real-shell 520 DIP host; the native pass
therefore captures the shell minimum with the pane open and collapsed.
It samples host/navigation bounds at least 300 ms apart before capture.
Run native proof only on an authorized, serial desktop slot with a fresh
explicit app binary. Inspect the PNGs; a passed geometry test or a rendered
bitmap does not establish visible shell acceptance.

Reactor preview.17 includes the dialog theme and pooled-background fixes that
previously required a task-local preview.12 backport. Validate the released
package identities and loaded DLL hashes, not the historical local override.
Usage retains the aligned Core/Advanced preview.17 pair when integrating the
shared page foundation and Windows App SDK 2.6.5. Its declarative export
`ContentDialog` needs inherited theme and host-disposal handling, and its pooled
count-pill `Border` needs resource-backed background precedence. The mounted
`ExportBlock_OffersDailyTotalsThenShowsTheSavedToastAndFolderAction` cases
assert the actual Light/Dark dialog theme; the count-pill regression below
checks the native brush. These are dependency regression requirements, not
evidence that the newly merged package combination has passed native proof.
`NuGet.Config` maps Reactor exclusively to `artifacts/reactor-packages`.
Run `.\scripts\Initialize-ReactorPackages.ps1` before direct WinUI restore,
build or run commands; `build.ps1` and building through `run-app-local.ps1`
already do this. The initializer verifies the official preview.17 release
digests without changing global NuGet configuration or clearing its cache.
A successful build alone is not dark-dialog proof.
The count-pill contrast regression also checks the actual native brush after
pooled reuse. A pooled Border must release a local null background when its
theme style owns that property; a correctly defined resource alone does not
prove that it was painted.

Usage retains figures during a same-query refresh, but clears them when the
date range, reporting zone, scope or server filters change. A failed new query
must not relabel old values. Basic browsing retains its row limit while
disclosing at most one extra selected loaded session. Missing prices qualify
cost independently of token completeness and suppress unsupported exact
contributor percentages and costliest-session claims.

## Known runtime regression

The strict cached-session tail assertion has exposed an intermittent failure:
after A -> B -> A, the long session can remain at messages 1-3 instead of its
previously visible final message. A Release run captured this state while the
Gateway returned the correct histories successfully. The cause is still under
investigation, including potential automation timing effects.

[Issue #1437](https://github.com/openclaw/openclaw-windows-node/issues/1437)
tracks the separate fix. Keep the assertion and failing artifacts; do not
substitute an earlier passing run, introduce blind retries, or skip the case.
The harness change deliberately does not change production scrolling to make
the test green. Desktop CI promotion remains separate from providing the
opt-in smoke entry point.

## Production-shaped Release proof

Debug and DevBuild are not interchangeable with the production runtime. Run
the same smoke against a non-Dev Release build without an attached debugger:

```powershell
.\build.ps1 -Project WinUI -Configuration Release
$releaseApp = '.\src\OpenClaw.Tray.WinUI\bin\Release\net10.0-windows10.0.22621.0\win-x64\OpenClaw.Tray.WinUI.exe'
.\scripts\test-gateway-fixture.ps1 -AppPath $releaseApp -Configuration Release -Screenshots -NativeInputProof
```

An explicitly selected unpackaged publish executable is also supported. This
does not prove installer/MSIX behavior, real Gateway pairing, live provider streaming,
provider behavior, or MXC. Keep the existing full E2E proof for those changes.

## Isolation contract

Each run generates a fresh temporary root, profile, Gateway record, credentials
and identities. It never copies installed settings, pairings or chat caches.
The child receives its own environment rather than changing the caller's
environment. All inherited `OPENCLAW_*` overrides are removed before the
explicit fixture environment is supplied.

The important controls are:

| Control | Purpose |
| --- | --- |
| `OPENCLAW_GATEWAY_FIXTURE=1` | Explicit host-side-effect suppression; normal isolated runs retain normal behavior. |
| `OPENCLAW_TRAY_DATA_DIR` | Synthetic settings, registry, identities, MCP token, logs, caches and instance mutex. |
| `OPENCLAW_TRAY_LOCALAPPDATA_DIR` | Isolated local root; setup appends `OpenClawTray`. |
| `OPENCLAW_TRAY_LOCAL_DATA_DIR` | Legacy direct setup path, matching the local root with `OpenClawTray` appended. |
| `OPENCLAW_TRAY_APPDATA_DIR` | Isolated roaming fallback root. |
| `OPENCLAW_MCP_PORT` | Dedicated, non-default MCP port. |
| Runtimeconfig `OpenClaw.GatewayFixtureIsolationVersion=1` | Preflight rejects old binaries before they can execute unguarded startup paths. |

The fixture guard validates the effective local path using SetupEngine's root
precedence. A supplied modern local root must be absolute and resolve to the same
directory as the validated legacy direct path; mismatches fail before startup.
Legacy launchers without a modern root still require an absolute direct path.
These checks do not change path resolution or side effects outside explicit fixture
mode. The launcher retains ownership and reparse-point cleanup checks.

The opt-in native setup warning regression uses a synthetic `native-setup`
scenario and an actual disposable signing identity and handoff receipt.
`--post-setup-launch` exercises the product authority check and model-verification
protocol, then connection loss, recovery with the same unsent composer draft,
changed identity, and ordinary navigation.
Model replies are synthetic; this is not real Gateway or inference proof.
Captured frames use `PrintWindow(PW_RENDERFULLCONTENT)` on the owned App HWND,
reject blank content and cropped asserted controls, and record SHA256 plus UIA
evidence beside each PNG.

Composer/controller tests separately cover `/new` releasing the setup binding
before async session creation, reporting the selected session back to the page,
and keeping the subsequent send on that session. Mounted UI tests cover
binding invalidation and voice activation waiting for visible, verified readiness.
Temporary unavailability retains draft/attachments but cancels capture; actual
authority failure still disposes the host. The synthetic full-app scenario does
not claim a real Gateway session-creation or microphone proof.

WSL keepalive/startup cleanup and Windows autostart writes are explicitly
guarded. A loopback URL alone is not enough to prevent the regular keepalive
policy from considering the installed default WSL distro. Sensitive node
capabilities, automatic repair, hotkeys, notifications and voice are disabled
in the synthetic settings. Telemetry export remains unconfigured.

Opening the Gateway Dashboard is also a host side effect. A launch that goes
through App's `GatewayDashboardLauncher` (the Dashboard buttons and menu items,
setup receipts and `app.navigate`) ends in `DashboardBrowser`. Outside fixture
mode it hands the exact URL to Windows. In fixture mode it never reaches the
user's default browser: it logs `Gateway fixture mode: suppressed Dashboard
browser launch:` plus the redacted target (`http://<host>:PORT/channels`, never
the credential fragment) and reports success. Creating
`dashboard-browser-failure.once` in the run's profile directory arms one
simulated browser failure: the next launch deletes the file, logs `simulated
Dashboard browser failure` and returns the failure Windows would. An invalid
fixture context fails closed instead of reaching the browser. Other places that
call Windows directly, such as the Connection page's saved-gateway row action,
are outside this guard and must not be invoked from a fixture test.

The fake binds a numeric IPv4 loopback listener to an OS-assigned port. The
launcher never takes over a standard Gateway/MCP port or terminates a process
by name. MCP bind collisions have a bounded retry limited to the owned child.
Cleanup refuses reparse points and only removes the run's owned data.
After the owned app exits, profile removal waits up to 10 seconds for Windows
sharing or lock violations to clear. Set
`OPENCLAW_GATEWAY_FIXTURE_CLEANUP_TIMEOUT_SECONDS` to a positive finite number
to override that cleanup deadline. A persistent lock remains an explicit
timeout with the original I/O error; other cleanup errors propagate immediately.
This does not retry the test or relax its assertions.

This remains a desktop application, not a general OS sandbox. Only the
documented browse/preferences and inert interactive-chat workflows are supported on a developer machine.
Use a disposable Windows environment for setup, external integration or other
host-changing workflows.

## Results and extending scenarios

Artifacts default to a per-run child under
`%TEMP%\openclaw-gateway-fixture-artifacts`. Override the parent directory with
`-ArtifactsDirectory`. The run report records the exact app path/hash/version,
runtime configuration, architecture, nonsecret endpoints and outcome. The
server records request methods and results, and unexpected requests fail the
smoke. Isolated logs redact the run's credentials. Profile files, Gateway
registries and identity files are not copied into artifacts.

Polling timeouts retain the wait description and artifact directory, including
when an individual probe times out. Malformed or disconnected HTTP-upgrade peers
are recorded as unexpected `<upgrade>` requests without retaining headers.
They do not prevent other clients from connecting or make server cleanup throw;
the unexpected-request assertions still surface them as smoke failures.
Non-interactive scenario responder exceptions still propagate through server
cleanup, preserving negative-test guards. Only interactive runtime faults are
translated into `INTERNAL` responses and recorded as unexpected requests.

The server also answers one plain HTTP request: the authenticated scenario
profile's photo at `GET /api/users/<profile id>/avatar`, which the Workspace
footer sends after `users.self` returns a profile. The browse scenario serves
`fixture-owner`; owner-identity UI cases can select another synthetic profile
with `CreateBrowseWithOwnerProfile`, and the HTTP route serves that scenario's
authenticated profile ID. A request with the shared
Gateway token (or the issued device token once the server accepts one) for the
scenario profile receives a small solid-colour PNG. A missing or wrong
credential receives 401 and an unknown profile receives 404. Each request is recorded as
`http:users.avatar` with only its outcome, never the credential, the profile id
or the query string. The 401 and 404 outcomes are unexpected requests, so a
broken photo path fails the smoke instead of quietly falling back to initials.
Any other request that is not a WebSocket upgrade is still rejected as an unexpected `<upgrade>`.
`OwnerFooterUsesAuthenticatedProfileAndLiveConnectionStatus` waits for the served
photo. With `-Screenshots` it also counts footer pixels in that photo's colour in
the captured Workspace window, so the capture shows the image was decoded and
painted, not only requested.

Add new behavior at the server boundary. Do not introduce fixture providers,
demo branches in pages, bypasses in the connection manager, or a second chat
renderer. Keep synthetic wire examples aligned with upstream Gateway wire
contracts, and exercise them through the real client. The separate nightly
upstream monitor detects changes; fixture examples do not pin the Gateway version.

Backend and independent app instances can run concurrently. UI tests are
serialized on a shared desktop; focus-dependent GUI workers need separate
Windows sessions or VMs. Interactive streaming uses the same per-instance
server/profile ownership described below.

## Dashboards without a real Gateway

The opt-in `dashboards` scenario serves synthetic boards for the Dashboards
page: a multi-tab board with HTML, report, website, pending, rejected,
broken, MCP app and disabled-plugin widgets, plus release, research and empty
boards. Widget documents and the sandbox proxy are served from loopback, so
no real board or capability grant is touched:

```powershell
.\scripts\run-gateway-fixture.ps1 -AppPath $app -Scenario dashboards
```

Pass `-Theme Dark` to seed the disposable profile for dark-theme proof.
Omitting `-Theme` uses `Light`:

```powershell
.\scripts\run-gateway-fixture.ps1 -AppPath $app -Scenario dashboards -Theme Dark
```

The focused native regression runs both themes, captures the gallery, HTML and
report widgets, approval/error states, tabs, the website link card and an empty
board, and scans the populated gallery and board with Axe.Windows. It also
asserts that the gallery sends no `board.get` before a card opens and that no
board mutation, grant, or action request is sent. English Windows OCR verifies
visible widget text in owned-window captures because UI Automation on some
hosts stops at the WebView pane. A pixel guard also requires the deployment
curve's plot area to have a luminance range of at least 64; a blank SVG cannot
pass merely because its heading rendered. The companion mounted tests
(`DashboardWidgetCellTests`) verify widget recovery messages, website links
without an embedded browser, and the web Dashboards handoff. The scenario needs
no outbound network: websites are links, not embedded pages, and the run uses no
production Gateway or browser credentials.

The gallery fixture includes six main-agent boards to exercise Fresh today and
the filter threshold. The title-bar Back button is exercised after visiting the
website tab. `DashboardWidgetCellTests.GalleryStates_KeepFailuresDistinctFromEmptyAndDenied`
also mounts the actual Reactor gallery for empty, loading, read-denied,
first-load error, stale-refresh, and 30-row states. Set
`OPENCLAW_DASHBOARD_STATE_ARTIFACTS` to a dedicated local directory to save
light/dark RenderTargetBitmap captures of those native controls; these do not
capture Mica or HTML WebViews and are separate from owned-window fixture proof.

On an authorized Windows desktop, after building the explicit app:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
$env:OPENCLAW_RUN_GATEWAY_FIXTURE_UI = '1'
$env:OPENCLAW_GATEWAY_FIXTURE_APP = (Resolve-Path $app).Path
$env:OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS = '<dedicated-artifact-directory>'
$env:OPENCLAW_TRAY_DATA_DIR = '<dedicated-test-profile>'
$env:OPENCLAW_TRAY_LOCAL_DATA_DIR = '<dedicated-test-local-directory>'
dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj -c Debug -r win-arm64 -p:Platform=ARM64 -p:DevBuild=false --filter 'FullyQualifiedName~DashboardsRenderNativeReports|FullyQualifiedName~DashboardWidgetCellTests'
```

For x64 use `-r win-x64 -p:Platform=x64`. Inspect the saved PNGs as well as the
test results. This is synthetic Gateway/UI proof, not real Gateway pairing, a
browser launch, or a working MCP app host, which remains deferred to the web
Dashboards.

Fixture teardown retains handles to WebView browser children whose parent ID
and start time match the owned app, and stops only those processes. A sharing
violation while their Crashpad files close is retried for at most 10 seconds.
Set `OPENCLAW_FIXTURE_CLEANUP_TIMEOUT_SECONDS` before starting the test host to
override that bound. Other cleanup errors and expired sharing violations fail.

## Interactive chat without a real Gateway

The opt-in `chat-interactive` scenario runs a memory-only chat runtime behind
the same real WebSocket client and disposable app profile:

```powershell
.\scripts\run-gateway-fixture.ps1 -AppPath $app -Scenario chat-interactive
.\scripts\run-gateway-fixture.ps1 -AppPath $app -Scenario chat-interactive -Interaction approval
```

Send normal text from Chat or create a new conversation. No provider account,
real Gateway pairing, node execution, or model download is needed. Every response
is synthetic; tool names, commands and patch summaries are display data only.
The read-only browse scenario and its existing assertions remain unchanged.

### Repeatable interactions

Type an interaction name in the host console, then send a message in the app.
It applies to the next admitted send across that fixture's sessions and then
returns to `streaming`. A `send-error` attempt consumes its selection but not
the message or idempotency key.

| Interaction | Behavior |
| --- | --- |
| `streaming` | Lifecycle start, reasoning, paced assistant deltas, Markdown/code/table, final message and lifecycle end |
| `chat-deltas` | The dedicated `chat` delta contract (`deltaText` and accumulated message), rather than `agent` assistant deltas |
| `tools`, `tool-error` | Correlated tool start/result, success or failure, with tool-call/result transcript entries |
| `tool-powershell` | A synthetic PowerShell command with variables, keywords and strings for input syntax-color proof, followed by literal output. Nothing executes |
| `agent-activity` | Job activity, then `exec` and `apply_patch` activity items, each paired with correlated tool start/result events and transcript entries. All operations are inert |
| `nested-tools` | A wrapper `exec` call with nested `read` and failed `exec` calls that carry `parentToolCallId` in tool events and transcript entries, and a reply with a Markdown link, a bare URL and inline code. All operations are inert |
| `question-choice`, `question-text`, `question-multi` | A pending question blocks that run until a valid answer, cancellation, expiry or Stop |
| `approval`, `approval-deny-only` | Top-level exec approval events, validated one-shot decisions, then continuation without command execution |
| `slow` | Repeated startup status frames before the answer |
| `send-error`, `stream-error` | Retryable send rejection or a terminal mid-stream error with partial history |
| `disconnect` | Disconnects authenticated sockets mid-run; server-owned work continues and history is available after reconnect |

`pause`, `step`, and `resume` control scripted steps without blocking request
processing. A step is one scripted action, which can produce multiple related
frames. With concurrent runs, the next ready run consumes a step. Stop,
question answers, expiry, and approval decisions remain responsive while paused.
`disconnect` as a console command immediately drops the current connections;
select the `disconnect` interaction with `-Interaction` to script it on a send.
Restart the launcher for a completely fresh scenario/profile.

`questions` lists synthetic question records. `resolve` submits JSON using the
same fixture-side validation as the Gateway `question.resolve` RPC:

```text
resolve {"id":"fixture-question-1","answers":{"answers":{"fixture_choice":["Inspect"]}}}
resolve {"id":"fixture-question-1","cancel":true}
```

Use the current ID printed by `questions`; IDs are not fixed across runs.
Console controls require an interactive terminal. Redirected/bounded runs still
support startup `-Interaction` and automated WebSocket clients.

### Protocol coverage and boundaries

`InteractiveGatewayRuntime` owns state, not `App`, the connection manager or
the renderer. It advertises the methods/events and synthetic operator scopes
needed for this scenario. Accepted sends are keyed by session and idempotency
key; reuse with different input or after session reset/replacement is rejected. Events
carry run/session correlation and increasing per-run sequence numbers.
Different sessions can run concurrently; a second direct send to a busy session
is rejected, allowing the production client's existing queue to own queuing.

| Surface | Implemented fixture behavior |
| --- | --- |
| `chat.send`, `chat.abort` | Text-only sends, acknowledgement before playback, named or session-wide stop, terminal state and partial transcript |
| `chat.history`, `chat.inject` | Updated bounded histories and inert assistant-message insertion when idle |
| `sessions.create` | New canonical keys and IDs, optional in-memory transcript fork from `parentSessionKey`; `forkFrom: "last-completed"` excludes an unfinished turn, including its tool-call/result entries |
| `sessions.list`, `sessions.preview` | Current catalog and transcript data, filtering, limits and previews |
| `sessions.patch`, `sessions.reset`, `sessions.delete` | Label/pin/unread/archive/model/thinking changes, guarded read acknowledgement, reset/delete when idle |
| `question.request`, `question.list`, `question.get`, `question.waitAnswer`, `question.resolve` | One to three scalar questions, single/multi choice, custom text, defaults, cancellation, expiry, resolution-ID retry, recovery and multi-client broadcasts |
| `exec.approval.resolve` | Only fixture-issued pending approvals; deny-only requests cannot be allowed |
| Existing browse reads | Models, agents, health, usage, configuration and discovery remain available |

Forks without `forkFrom` copy the full parent history. The `last-completed`
selector copies through the last persisted assistant reply that is not a
tool-use checkpoint; it produces an empty history if no such reply exists.
The copied history is independent of later parent updates or resets.

The implementation follows the upstream `packages/gateway-protocol/src/schema/`
`logs-chat.ts`, `questions.ts`, session schemas and current Windows event consumers.
Protocol research used upstream main during implementation, including
`6eacab6c2940a7dcb36574cf45daff1141f945ce`; this is evidence provenance,
not a supported-Gateway version pin.

This is **not yet an emulator for the entire Gateway protocol**. Attachments,
secret/resource questions, provider inference, server-side queue/steer policies,
session succession/compaction, configuration writes, installation, cron and real
node operations are not simulated. Unsupported parameters/methods fail explicitly.
Question `url`, resource previews and credential storage are deliberately not
accepted; no fixture should open external resources or save credentials implicitly.
Extend the same server boundary with a scenario and protocol/consumer tests when
adding an interaction, rather than accepting arbitrary requests as success.

The native Reactor chat consumes `question.requested`/`question.resolved` and
shows supported questions above the composer. Start with
`-Scenario chat-interactive -Interaction question-choice`, then send a message
in the app to show the choice panel. Select **Inspect** or **Summarize** and use
**Submit answer**, or choose **Cancel question**. The run continues after the
fixture acknowledges the separate `question.resolve` RPC. Use `question-text`
or `question-multi` in the host console before the next send to try other inputs.
Protocol/provider tests do not by themselves prove question-panel rendering;
verify the visible native controls separately.

Runs are bounded to 256 sends and 100 sessions, transcripts to 1,000 entries,
text input to 32,768 characters, and external questions to a five-minute timeout.
The default interactive decision timeout is 60 seconds. State is disposable;
no live-network fallback exists. Artifacts record request outcomes and event
names/delivery counts, not chat text, question answers or credential values.
Scripted work is cancelled and drained on shutdown; playback faults fail the host.

### Focused validation

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --filter FullyQualifiedName~GatewayFixtureProtocolTests
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --filter FullyQualifiedName~InteractiveGatewayChatTests
```

The first suite covers real-client chat plus raw WebSocket question/approval
round trips, invalid inputs, idempotency, session isolation, expiry, recovery,
stop and bounded shutdown. The second uses the production chat provider and
bridge, with isolated persistence. The opt-in desktop fixture suite additionally
contains `InteractiveChatShowsStreamedAnswerAndGatewayApproval`, which sends via
local MCP, verifies the visible answer and approval buttons, clicks Allow, and
captures the continuation when screenshots are enabled.

The fixture suites supplement, not replace, the required build, Shared and
Tray tests. Run those on every implementation change as documented in
`AGENTS.md`. The real-app smoke is an explicit opt-in lane; its script is the
entry point for desktop CI workers and local release proof.
