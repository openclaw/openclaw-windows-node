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

The Gateway is read-only. Chat sends, session mutations and configuration
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

The host is `tests\OpenClaw.GatewayFixtureHost`, a plain .NET console app, not a
test container. The request-driven server and scenario live in
`tests\OpenClaw.TestSupport\Gateway`. Repeated reads and different request
orders work; this is deliberately not sequential packet playback.

## Controlled chat protocol prerequisite

Protocol tests explicitly select `GatewayScenario.CreateChat()` or
`GatewayScenario.LoadBuiltin("controlled-chat")`. This is not a launcher/UI
journey option. Default `multi-session-browse` behavior, its five sessions and
its read-only operator scope remain unchanged. The chat scenario retains those
same reads and grants only `operator.read` and `operator.write`, not administrator
or node execution authority. It accepts only `chat.send` and `chat.abort` in
addition to the existing browse surface; other mutations still fail explicitly.

The local wire contract is traced from the production client and chat mapper:

| Operation | Fixture behavior |
|---|---|
| `chat.send` | Validates known `sessionKey`, nonempty `message` and `idempotencyKey`. Returns `{runId, sessionKey, status: "started"}` with wire `runId` equal to the idempotency key, as upstream does. Diagnostic observations use a separate fixture-owned alias and never retain the caller's key. Replay is explicitly rejected rather than pretending to implement upstream deduplication. |
| Streaming | Publishes `agent` lifecycle `start`, two fixed `assistant` `data.delta` fragments, a `chat` `state: "final"` assistant message with `content[].text`, then lifecycle `end`. Every frame carries its run and session identity; sequences are 1, 2, 3, 4 and 5. No input text is echoed. |
| `chat.abort` | Validates run, known session and owning connection, serializes against publication, cancels held output, emits the upstream-shaped `chat` event with `state: "aborted"` and `stopReason: "rpc"`, and acknowledges `{ok: true, aborted: true, runIds: [runId]}`. No further delta or successful final is published for that run. No invented agent lifecycle abort event is emitted. |
| Rejection | Unknown sessions/runs, mismatched ownership, inactive runs, unsupported fields (including attachments) and unknown methods fail explicitly. Browse still rejects both chat writes. |

`FixtureGatewayServer.HoldChatResponse(sessionKey)` holds acknowledgements;
`HoldChatDeltas(sessionKey, afterDeltaCount: 0 or 1)` holds before the indicated
delta in the fixed two-delta output. Configure holds before sending.
`ReleaseChatResponse` and `ReleaseChatDeltas` release them. These are operation
barriers, not timers or scripted RPC responses. Independent reads and aborts
remain available while a run is held. `WaitForChatRunAsync(sessionKey, phase,
occurrence, cancellationToken)` waits for the specified session's one-based run
to reach a phase. Use a held or terminal phase for deterministic assertions,
since streaming is transient.
`HoldChatCompletion`/`ReleaseChatCompletion` can hold lifecycle end after a
successful final. Successful final publication is recorded immediately and
remains true if disconnect/shutdown interrupts later completion work. Client
observations combine agent and chat events in one timeline, so tests verify
cross-event order rather than two separately ordered lists.

`ChatRuns` contains only fixture-owned run IDs, known session aliases, coarse
phases, delta counts and final-publication flags. `Requests` continues to omit
request IDs, payloads, input, idempotency keys and credentials. Publication locks
are never held at a barrier; abort cancels barrier waits without cancelling an
in-flight WebSocket send. Disconnect and server disposal cancel held work and
drain owned connection tasks.

The focused suite runs the real `OpenClawGatewayClient` against real loopback
WebSockets, including default browse tests:

```powershell
# Set OPENCLAW_REPO_ROOT and all three OPENCLAW_TRAY_* isolation paths first.
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false --filter "FullyQualifiedName~GatewayFixtureProtocolTests|FullyQualifiedName~GatewayFixtureChatProtocolTests"
```

This prerequisite does **not** complete D/C03. The explicit supported subset
is independently pinned in `gateway-chat-fixture-contract.json` to upstream
commit `f4d6e4d457baf0282dc7cdb98f1b0a6848852417`, including send identity,
abort response and aborted/final event shapes. Authoritative references:
[request/event schemas](https://github.com/openclaw/openclaw/blob/f4d6e4d457baf0282dc7cdb98f1b0a6848852417/packages/gateway-protocol/src/schema/logs-chat.ts#L334-L381),
[run identity](https://github.com/openclaw/openclaw/blob/f4d6e4d457baf0282dc7cdb98f1b0a6848852417/src/gateway/server-methods/chat-send-session.ts#L213),
[abort result](https://github.com/openclaw/openclaw/blob/f4d6e4d457baf0282dc7cdb98f1b0a6848852417/src/gateway/server-methods/chat-abort-handler.ts#L366-L371),
and [aborted event](https://github.com/openclaw/openclaw/blob/f4d6e4d457baf0282dc7cdb98f1b0a6848852417/src/gateway/chat-abort.ts#L422-L445).
These protocol tests do not prove UI rendering or user-driven cancellation.
Optional upstream inputs, administrative/cross-connection cancellation,
unknown/completed-run no-op results and authorization details are not emulated.
The fixture keeps histories static and does not model persisted aborted
partials, idempotency replay, attachments, provider execution or inference.
Those paths must not be marked verified by this protocol suite.

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

The tests use local MCP for discovery, startup state and page navigation, and
UI Automation for the actual agent selector, Workspace session sidebar, scrolling and settings
controls. They verify:

- Real operator connection and populated sessions with node execution off.
- Independent profiles, endpoints, tokens and preferences in two concurrent
  application instances. Stopping one must leave the other usable.
- Repeated A/B session selection and session-specific visible histories.
- Natural initial-tail visibility of message 240 before any explicit scroll
  command, plus final-message visibility after scrolling at two window sizes.
- Sessions/Settings/Configuration/Chat navigation, profile-only preference
  persistence, and the Sessions-page Open in chat action.
- A deliberately held history response arriving after a different session is
  selected, without replacing the visible transcript.

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
does not prove installer/MSIX behavior, real Gateway pairing, streaming,
provider behavior, or MXC. Keep the existing full E2E proof for those changes.

## Contract journeys with lvt

This separate opt-in lane uses the real app's UI Automation tree through
`lvt mcp --allow-input`, with the official .NET MCP client. It does not edit
framework properties, use coordinate fallbacks, or drive the tested navigation
through app MCP. MCP opens the initial window and supplies startup readiness.
UIA acts on freshly resolved, unique AutomationIds. Incomplete tree reads,
ambiguous IDs, missing properties, tool errors and lost window/process ownership
fail instead of becoming success.

Provision lvt explicitly on an authorized interactive Windows x64 or ARM64 desktop.
Download the complete archive matching the native PowerShell/.NET process architecture from the
[v0.5.0 release](https://github.com/asklar/lvt/releases/tag/v0.5.0) and verify:

| Architecture | Artifact | SHA256 |
|---|---|---|
| x64 | `lvt-v0.5.0-x64.zip` | `c43f934384aebf565ccb74824ea3d343084bb78f6eeaca0563f9701a51b731cc` |
| x64 | Extracted `lvt.exe` | `40080f40637e393116b19c00525130e62bf6c8191dc9d4c33207e67882ad75e9` |
| ARM64 | `lvt-v0.5.0-arm64.zip` | `942c8f19aa2ffa84d35889d4cbfb07dbc6fb0ac6df8657af57797c17021dbeee` |
| ARM64 | Extracted `lvt.exe` | `0ed7c2e1925c140eef7a2c7a4b3bc220476f1468ad1e17f6da98040d74cf68da` |

The runner, adapter and payload snapshot reject a different architecture's
executable even when it is a pinned release. They never download,
install, update a plugin, or attach to an installed app automatically. The
first lane uses UIA only, with no visual-provider injection.

Build the test project and exact app before running. Journeys execute the exact
prebuilt test assembly being snapshotted, so they neither rebuild the app nor
silently switch to another configuration's test output:

Use native ARM64 PowerShell and .NET on ARM64 hosts. Keep the app, test assembly,
runner and driver on the same architecture; x64 emulation is not native ARM64
proof. The tests use temporary profiles, not machine-specific settings. They
only inspect real-profile file metadata for the shared-writer safety check.
Close other Companion/Gateway instances before the run, keep the desktop
unlocked and visible, and avoid concurrent desktop input.

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
$arch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$rid = if ($arch -eq 'Arm64') { 'win-arm64' } elseif ($arch -eq 'X64') { 'win-x64' } else { throw "Unsupported architecture: $arch" }
$platform = if ($arch -eq 'Arm64') { 'ARM64' } else { 'x64' }
dotnet build .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj -c Release -r $rid "-p:Platform=$platform" -p:DevBuild=false
.\build.ps1 -Configuration Release
$app = ".\src\OpenClaw.Tray.WinUI\bin\Release\net10.0-windows10.0.22621.0\$rid\OpenClaw.Tray.WinUI.exe"
$lvt = "C:\approved-tools\lvt-v0.5.0-$($arch.ToLowerInvariant())\lvt.exe"
.\scripts\test-gateway-fixture.ps1 -AppPath $app -Configuration Release -Suite Journeys -LvtPath $lvt
```

The required case IDs are `C01.NavigationPreservesPreferences`,
`C04.PreferenceOffToOnSurvivesRestart`, and
`C04.PreferenceOnToOffSurvivesRestart`, plus
`C01.McpOnlyStartsWithoutGatewayCredential` and
`C01.UnavailableGatewayStillOpensWorkspace`, followed by
`C03.SendStreamsIntoSelectedConversation` and
`C03.CancelStopsOutputAndAllowsNextSend`, and
`C02.SwitchingSavedPeerRoutesWorkToOnlyNewPeer`. Navigation tests visit Settings using
its UIA selection, go back and forward, and independently check persisted
preferences. Persistence tests seed both initial states, toggle via UIA, await
the persisted effect, replace only the owned app, reconnect a new lvt session,
and verify the rendered toggle and unrelated fields. A separate synthetic
profile must remain byte-for-byte unchanged.

The startup cases do not use MCP navigation to open the asserted window. They
observe the real no-argument app launch, then check the owned Workspace via
UIA. MCP-only starts with no gateway record or URL and must serve local MCP
without connecting to the synthetic peer. The unavailable case retains its
configured destination while the synthetic peer explicitly rejects connects
with `UNAVAILABLE`; it must not fabricate connected sessions. The peer keeps
its dedicated port owned. This is a protocol-level unavailable response,
not a claim of listener termination or real gateway replacement.

Fixture startup readiness is mode-specific. Normal browse waits for the
connected/populated catalog; the two startup modes wait for local MCP and
their declared disconnected/empty state. This does not bypass product
readiness, change the app connection manager, or suppress errors in normal mode.

Restart is deliberately abrupt after persistence is observed. This proves
saved preference restoration, not graceful shutdown or installer migration.
The chat journeys explicitly use `controlled-chat`. They reuse the existing
UIA session-selection setup helper, then enter synthetic text and invoke
Send/Stop through lvt UIA. Response/delta barriers expose pending and streaming
states without sleeps. Actual transcript text must be visible inside the
viewport, and the peer's accepted method/session/run/terminal observations
must agree. The cancellation case also sends a subsequent turn and requires
the aborted run not to revive. Screenshots cover pending, streaming, final,
cancelled and recovered states.

The C02 case uses `GatewayFixtureRun.StartTwoPeerAsync` to own two authenticated
loopback peers and seed a second inactive gateway before app startup.
`GatewayPeerProfile` gives each peer a distinct synthetic identity and catalog;
the default browse data remains unchanged. The case invokes the real saved
gateway Connect action through lvt, checks registry/connection ownership, then
selects the new peer's distinct conversation and verifies history reaches that
peer while the old peer receives no further requests. Both peer secrets are
redacted, both listeners are disposed, and secondary request metadata is written
to `secondary-gateway-requests.jsonl`. Captures require
`SavedGatewayConnectAction` before switching and `WorkspaceNavigation` after.
`GatewayTwoPeerRoutingTests` covers the protocol/profile mechanism separately;
passing those tests or compiling C02 does not establish live UI acceptance.

These journeys do not yet exercise fresh setup, listener/process loss,
in-flight cross-session UI ownership, late peer replies across a rendered
gateway switch, credential recovery, real permissions,
setup or Local AI. Static fixture histories do not prove persisted chat after
reload. Merely compiling these journey sources is not live acceptance.

All eight cases must execute and pass. A previous seven-case report cannot
qualify this suite. The runner rejects missing, duplicate,
unexpected, skipped or aborted cases. It snapshots HEAD/dirty-file hashes and
every file in the app, test, and pinned driver output directories, including
dependencies, compiled XAML, PRI resources and plugins. It rejects source,
payload, file-set, or host identity changes. App and test entry points must be
from this worktree and the app must have the non-Dev identity marker.
Use `-TestAssemblyPath` only when the prebuilt test output is in a different
worktree-relative layout. These hashes prove the payload did not change during
proof, not that arbitrary old outputs correspond to HEAD. Build them from the
recorded source first. CI promotion remains separate work.

The runner prints a phase summary and writes `suite-summary.json` plus one
`<case-id>.json` report per required case. `Behavior` is `passed`, `failed`, or
`not-run` (or `not-verified` when observation was interrupted); `Isolation` distinguishes unchanged metadata, observed changes,
an audit failure, and a check that never ran. `Cleanup` is separate.
`Qualification` passes only when behavior, isolation, cleanup, evidence, and
the exact TRX case set all pass. A successful UI action followed by changing
shared files is shown as **behavior passed, qualification blocked**, not an
app regression. Later cases stopped by the guard are shown as **not-run**.
The underlying test command still exits nonzero, so blocked proof cannot
become a green gate. Automation, capture, setup, report, isolation and cleanup
failures are recorded by category; only a product assertion marks behavior
failed. Completed checkpoints remain available for interrupted cases.

The final suite summary is written only after strict case/TRX admission and
the after-run full payload/source snapshot. Missing evidence, nonzero process
exit, or a changed dependency cannot leave the suite summary marked passed.
Per-case observed success does not override a failing final gate.
Required images and receipts must still exist at closeout, match their run
identity and SHA256, include the asserted control in bounds, and correlate
with the case's completed capture checkpoint. Those integrity checks do not
replace inspecting the rendered pixels.

Changed entries include their relative path and before/after size and UTC
timestamp, including additions and removals. No file contents or credential
values are read into this metadata evidence. Background log or WSL disk writes
are not ignored; metadata comparison cannot identify which process wrote them.
An audit that cannot complete also blocks proof and prevents subsequent launches.

Before launching any fixture app, `Category=ContractPreflight` observes the
same real-profile metadata for ten seconds. If it changes, all eight journeys
are reported as **not-run, qualification blocked**, with changed-entry evidence.
Only the preflight test process ran; the runner does not launch the app or lvt.
This avoids presenting a busy shared environment as app failures.
A quiet preflight is only admission, not isolation proof: each executed journey
still checks the complete before/after metadata and cleanup independently.

Owned-window screenshots are mandatory at asserted checkpoints and reject
blank/cropped content. Inspect their rendered pixels before claiming visual
acceptance. The run records lvt PID/start time and app/window ownership; all
owned processes are disposed. Safe metadata comparisons of real roaming/local
tray profiles must remain unchanged. Do not run with other foreground workers
or shared-profile writers. There is no full-desktop capture or raw settings
artifact export.

Adapter refusal and result-manifest tests run without launching the app:

```powershell
dotnet test .\tests\OpenClaw.Tray.IntegrationTests\OpenClaw.Tray.IntegrationTests.csproj --filter "FullyQualifiedName~LvtProtocolTests|FullyQualifiedName~OwnedStdioMcpClientTests|FullyQualifiedName~ContractJourneyReportTests|FullyQualifiedName~ProfileMetadataSnapshotTests|FullyQualifiedName~GatewayFixtureProfileTests|FullyQualifiedName~GatewayFixtureRunTests"
.\scripts\test-contract-journey-results.ps1
.\scripts\test-contract-proof-finalization.ps1
.\scripts\test-contract-lvt-release.ps1
```

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
documented browse/preferences workflow is supported on a developer machine.
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

Add new behavior at the server boundary. Do not introduce fixture providers,
demo branches in pages, bypasses in the connection manager, or a second chat
renderer. Keep synthetic wire examples aligned with the existing Gateway
protocol snapshot, and exercise them through the real client.

Backend and independent app instances can run concurrently. UI tests are
serialized on a shared desktop; focus-dependent GUI workers need separate
Windows sessions or VMs. Future multi-Gateway switching and canned streaming
can reuse the existing per-instance server/profile ownership.

The fixture suites supplement, not replace, the required build, Shared and
Tray tests. Run those on every implementation change as documented in
`AGENTS.md`. The real-app smoke is an explicit opt-in lane; its script is the
entry point for desktop CI workers and local release proof.
