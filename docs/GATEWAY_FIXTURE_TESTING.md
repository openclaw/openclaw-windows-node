# Fixture-backed application tests

The fixture Gateway fills the gap between unit/component tests and full system
E2E. It runs the real Windows app, Gateway client, chat provider, history loader
and native UI against a deterministic loopback WebSocket server. There is no AI,
WSL Gateway, provider account, real pairing, or live-network fallback.

The `multi-session-browse` scenario powers interactive exploration and
automated tests. Its five sessions include an empty conversation, distinct
session identities and titles, and 240 mixed-height messages with an explicit
final-message marker. Supporting model, agent, health, usage and configuration
responses populate the application rather than just its disconnected shell.
Read-only placement discovery returns an empty project catalog and one synthetic
local environment through `projects.list` and `environments.list`. It creates no
project or host resources; `projects.add` is rejected as a write.

## Build and explore

Build the current app first, then pass its executable explicitly. An installed
app is never discovered or selected implicitly.

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

The opt-in `usage-period-legacy` scenario returns a synthetic seven-day seed on
the first cost response, then returns thirty-day totals for every later request.
The seed deliberately lets the UI exercise a matching cache followed by a
mismatched range; it is not a faithful simulation of every legacy Gateway
request sequence. Use it to verify that Usage keeps cached seven-day totals
truthful, unlocks period selection when a later response mismatches, and
displays thirty-day totals only after the user selects that period.

For gateway ownership proof, use `-Scenario usage-period-switch`. It registers
two synthetic loopback Gateways in the same disposable profile. The initial
Gateway A returns seven days and `$7.00`. Switch to **Fixture Usage B** through
Connection settings, then return to Usage. B initially returns thirty days and
`$300.00`, including for seven-day requests: Usage must show the requested-range
warning, not A's totals, keep 7 Days selected, and finish loading. Selecting
30 Days explicitly shows `$300.00`. Returning to 7 Days or reopening Usage must
not resurrect A's cache. Enter `seven` in the fixture host console to enable B's
seven-day `$70.00` response, then select 30 Days followed by 7 Days. B's timestamp
is one day older than A's, so the matching result must still apply after the
gateway ownership reset. The control changes only synthetic server responses,
not application state. `stop`, Ctrl+C, or the duration bound disposes both
servers and the isolated profile. Reports retain both endpoints and separate
`gateway-b-requests.jsonl` / `gateway-b-events.jsonl` logs.

The existing native `GatewayFixtureUiTests` owner also covers this sequence in
`UsagePeriodSwitch_ClearsPreviousGatewayAndAcceptsOlderMatchingSnapshot`. It
uses the mounted Connection action and Usage selector, not a production bypass.
Opt into the existing fixture UI lane and screenshot flag for native captures.
This is automatic native-test evidence, not Computer Use observation or proof of
real provider, pairing, or node invocation.

`UsagePeriodSwitch_MountedPageClearsBeforeNewCostsAndAcceptsOlderSnapshot`
keeps Usage mounted while the supported `app.connection.connectSharedToken`
command connects to synthetic B through the real connection manager. A server-side
cost-response gate proves A's total clears before B supplies any costs. UI Automation
retains the same `TotalCostText` identity through the mismatch warning and older
matching B response, without changing the selected period. Separate range gates
release the thirty-day mismatch before the delayed seven-day response, covering
the page-local display and timestamp reset without a selector change resetting it.

Browse-based scenarios advertise and answer the startup `question.list` read
with an empty synthetic `questions` array. Question mutations remain confined
to the opt-in interactive scenario.

The host is `tests\OpenClaw.GatewayFixtureHost`, a plain .NET console app, not a
test container. The request-driven server and scenario live in
`tests\OpenClaw.TestSupport\Gateway`. Repeated reads and different request
orders work; this is deliberately not sequential packet playback.

## Run the application smoke

```powershell
.\scripts\test-gateway-fixture.ps1 -AppPath $app
```

For screenshots, use owned-window native capture (no Windows App CLI installation is needed):

```powershell
.\scripts\test-gateway-fixture.ps1 -AppPath $app -Screenshots
```

The script runs profile/preflight and polling tests, real-process MCP tests, and native UI
tests. It rejects failed, skipped, missing-report and zero-test runs. It needs
a Windows desktop and the same WinUI prerequisites as the existing UI suite.
There is no skip-as-success fallback when the desktop is unavailable.

`GatewayFixtureStartupTests` exercise parent and foreign browser-control startup
without launching the app. Cancellation and readiness failures must reap the
owned child, remove its readiness file, and release both listener ports.
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
Each UI test also has an outer deadline so a blocked synchronous UI Automation
call cannot prevent failure reporting and owned-process cleanup.

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

## Cron editor proof

`GatewayScenario.CreateCronEditing()` explicitly opts into in-memory `cron.add`
and `cron.update`, with listed, absent, unlisted, and interval schedules.
`cron.list` returns the updated jobs; nothing is scheduled or executed.
The normal browse scenario still rejects cron writes.

The focused proof uses the existing isolated profile, real Gateway client and
cron form. Its native title click verifies the owning process, foreground window,
physical point and UIA target before sending input. Run it only with exclusive
desktop access:

```powershell
$env:OPENCLAW_RUN_GATEWAY_FIXTURE_UI = '1'
$env:OPENCLAW_GATEWAY_FIXTURE_APP = (Resolve-Path $app).Path
$env:OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS = "$env:TEMP\openclaw-cron-proof"
$env:OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS = '1'
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj -r win-arm64 -p:Platform=ARM64 -p:DevBuild=false --filter 'FullyQualifiedName~GatewayFixtureUiTests.CronEditor_' --logger 'console;verbosity=normal'
```

On x64 use `-r win-x64 -p:Platform=x64`. Keep the selected executable on the
tested commit. The two tests must execute, not skip. They check timezone
preservation and explicit replacement, switching jobs and schedule kinds,
and resetting for a new job. Artifacts include owned-window editor captures
and the exact synthetic schedules read back after accepted saves. This proves
the app/fixture wire path, not production Gateway persistence or scheduling.

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
.\scripts\test-gateway-fixture.ps1 -AppPath $releaseApp -Configuration Release -Screenshots
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

The fake binds a numeric IPv4 loopback listener to an OS-assigned port. The
launcher never takes over a standard Gateway/MCP port or terminates a process
by name. MCP bind collisions have a bounded retry limited to the owned child.
Cleanup refuses reparse points and only removes the run's owned data.

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

Add new behavior at the server boundary. Do not introduce fixture providers,
demo branches in pages, bypasses in the connection manager, or a second chat
renderer. Keep synthetic wire examples aligned with upstream Gateway wire
contracts, and exercise them through the real client. The separate nightly
upstream monitor detects changes; fixture examples do not pin the Gateway version.

Backend and independent app instances can run concurrently. UI tests are
serialized on a shared desktop; focus-dependent GUI workers need separate
Windows sessions or VMs. Interactive streaming uses the same per-instance
server/profile ownership described below.

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
| `agent-activity` | Job activity, command item/output and patch item/summary events. All operations are inert |
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
