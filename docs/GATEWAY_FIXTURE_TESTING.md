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

The host is `tests\OpenClaw.GatewayFixtureHost`, a plain .NET console app, not a
test container. The request-driven server and scenario live in
`tests\OpenClaw.TestSupport\Gateway`. Repeated reads and different request
orders work; this is deliberately not sequential packet playback.

## Run the application smoke

```powershell
.\scripts\test-gateway-fixture.ps1 -AppPath $app
```

For screenshots, use the already installed Windows App CLI:

```powershell
.\scripts\test-gateway-fixture.ps1 -AppPath $app -Screenshots
```

The script runs profile/preflight and polling tests, real-process MCP tests, and native UI
tests. It rejects failed, skipped, missing-report and zero-test runs. It needs
a Windows desktop and the same WinUI prerequisites as the existing UI suite.
There is no skip-as-success fallback when the desktop is unavailable.

The tests use local MCP for discovery, startup state and page navigation, and
UI Automation for the actual session-picker flyout, scrolling and settings
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
For the delayed-history test, a passive `ChatComposerSessionPicker` UI Automation
`ItemStatus` acknowledgement records which loaded-history keys the render
consumed. It contains no messages, is empty outside explicit fixture mode,
and never changes chat state. The test waits for this render acknowledgement
before checking that the previously selected session is still visible.
Each UI test also has an outer deadline so a blocked synchronous UI Automation
call cannot prevent failure reporting and owned-process cleanup.

Inspect the captured PNGs when claiming visual proof. UI Automation alone does
not detect every overlap, clipping or theme defect.

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
| `OPENCLAW_TRAY_LOCAL_DATA_DIR` | Separate synthetic setup/Local AI state. |
| `OPENCLAW_TRAY_APPDATA_DIR` | Isolated roaming fallback root. |
| `OPENCLAW_MCP_PORT` | Dedicated, non-default MCP port. |
| Runtimeconfig `OpenClaw.GatewayFixtureIsolationVersion=1` | Preflight rejects old binaries before they can execute unguarded startup paths. |

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
