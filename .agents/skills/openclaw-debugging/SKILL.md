---
name: openclaw-debugging
description: Debug OpenClaw Windows Companion, setup, Gateway connection, node capabilities, packaged permissions, updates, installers, and system.run by selecting the failing boundary and proof path before changing code.
---

# OpenClaw Windows Debugging

Use this skill to debug OpenClaw Companion and the Windows node. Start from the
reported behavior, identify the first failing boundary, and collect the narrowest
evidence that distinguishes setup, configuration, transport, product code,
Windows permission, packaging, and runtime failures.

This skill does not authorize installing software, changing shared runtimes,
resetting pairing, approving requests, invoking sensitive capabilities, or
editing user configuration. Obtain the required authority first. Never print or
share Gateway tokens, setup codes, MCP tokens, device identities, private keys,
raw settings, screenshots, recordings, camera data, microphone data, or chat
payloads.

## Read first

- [Architecture ownership](../../../docs/ARCHITECTURE.md) for the affected owner.
- [Connection architecture](../../../docs/CONNECTION_ARCHITECTURE.md) for
  credentials, pairing, reconnect, and node state.
- [Local MCP](../windows-app-mcp/SKILL.md) for same-machine discovery and
  invocation.
- [winnode](../winnode/SKILL.md) for exact command arguments and output shapes.
- [Windows node testing](../../../docs/WINDOWS_NODE_TESTING.md) for capability,
  permission, and `system.run` behavior.
- [Setup engine](../../../docs/SETUP_ENGINE_REDESIGN.md) and
  [onboarding](../../../docs/ONBOARDING_WIZARD.md) for WSL and native Gateway
  setup.
- [Proof selection](../openclaw-proof-validation/SKILL.md) before runtime or UI
  claims.

Do not preload unrelated guides. Follow the owner named by the first failing
boundary.

## Default loop

1. Record the exact repository HEAD, dirty files, app build, runtime identity,
   selected profile, Gateway kind, and repro command or UI route.
2. State one suspected boundary before changing code.
3. Collect the narrowest signal from that boundary.
4. Reproduce without switching profile, app build, Gateway, transport, command,
   or permission state unless that variable is the test.
5. Classify the failure as environment/setup, configuration/permission,
   transport/pairing, product code, packaging/update, or unproven.
6. Patch the owner of the first incorrect behavior, not the next layer that
   reports it.
7. Rerun the exact failing probe, then add the required broader validation.

An exit code alone is not proof. Check that the intended tests executed, that
the selected command appeared in `tools/list`, that the app under test came
from the expected HEAD, and that UI evidence shows the asserted state.

## Choose the boundary

| Symptom | First evidence | Boundary interpretation |
|---|---|---|
| App will not start, exits, or opens the wrong instance | `openclaw-tray.log`, `crash.log`, `run.marker`, activation arguments, runtime identity | Startup, single-instance, activation, migration admission, or crash handling |
| Setup fails before Companion is usable | Latest setup JSONL and matching journal, failed step, command exit/timeout, rollback result | Setup pipeline or host prerequisite, not automatically a product runtime defect |
| WSL Gateway is installed but unavailable | WSL service status and journal as user `openclaw`, then Windows reachability | Gateway service/runtime or Windows-to-WSL reachability |
| Native Gateway is installed but unavailable | Setup page status, **Collect Gateway diagnostics**, **View setup log**, or `clawctl gateway-service status` | Package service, isolated session, listener ownership, or native runtime readiness |
| Companion is disconnected or pairing is pending | `app.connection.status`, `app.connection.gateways`, pending approvals, connection event timeline | Registry, credential, protocol, operator, node, or approval state |
| Local MCP is unavailable | Selected profile, `mcp-token.txt` existence, listener state, `winnode --list-tools` | Local MCP startup/authentication, not Gateway pairing |
| Command is absent | Live `tools/list`, capability settings, disabled commands, Gateway allowlist | Registration or gating before invocation |
| Command is listed but fails | Exact `winnode` request, tool error, tray log entry, native permission state | Capability implementation or Windows runtime permission |
| Local MCP works but remote use fails | Same command through Gateway `node.invoke`, node pairing/trust, allowlist | Gateway transport or policy; local MCP is only supporting evidence |
| `system.run` fails | `system.which`, `system.run.prepare`, approval snapshot, MXC diagnostics, exact argv/cwd | Binding, approval, policy, containment, execution, or cleanup |
| Update check behaves unexpectedly | Build identity, package state, update status, tray log | Store/Dev/Debug intentional skip, release updater, or optional dependency |
| Installer or migration fails | Exact artifact/identity, installer source checks, migration receipts and runtime logs | Packaging/migration; unit or source checks do not prove installed behavior |
| UI state is wrong but snapshots look correct | Current-head native UI evidence on the selected profile | Presentation or lifecycle; MCP snapshots do not prove pixels |

Stop at the first boundary whose actual result differs from its contract. Do not
reset settings, delete state, re-pair, restart an unowned Gateway, or swap to a
different transport to make the symptom disappear.

## Establish the exact runtime

Before branch-specific, proof-specific, or runtime-specific debugging:

```powershell
git branch --show-current
git rev-parse HEAD
git --no-pager status --short
```

Record:

- Companion build configuration and architecture.
- Release or Dev identity.
- Packaged MSIX, unpackaged Inno/portable, fixture, or source-built app.
- Selected profile and any `OPENCLAW_TRAY_*` overrides.
- WSL, Store native Gateway, development native Gateway patch, remote Gateway,
  or synthetic fixture.
- Local MCP or Gateway node transport.

`OPENCLAW_TRAY_DATA_DIR` is a direct isolated profile. It controls settings,
logs, crash state, MCP token, run marker, identities, and the isolated mutex.
`OPENCLAW_STATE_DIR` in the app process separately overrides the exec-approvals
location, so record it before claiming approval isolation. Keep the same profile
in the app and every `winnode` command. Release and Dev profiles are distinct.
Packaged apps can use Windows redirected storage, so use the running app's
Diagnostics page or resolved profile rather than guessing a package-family path.

Isolated and fixture instances intentionally suppress Windows toasts, skip
startup update checks, and do not register URI schemes. Use a non-isolated,
authorized identity for visible toast or capture-indicator proof. Do not invoke
`screen.snapshot`, `camera.snap`, or `location.get` in an isolated or fixture
instance because sensor access can continue after the suppressed indicator.
Use the tray-log suppression entry only to debug the non-sensitive notification
path, not as sensitive-capture proof.

The fixture Gateway is deterministic app and UI evidence. It is not real WSL
setup, pairing, provider, node invocation, sensor, notification, or MXC proof.

## Use the repository diagnostics

### Preferred sanitized bundle

Open **Settings > Diagnostics**, then use **Create diagnostics bundle**. Review
the preview before copying or saving it. The bundle includes bounded, sanitized:

- support and debug summaries
- connection event timeline
- `openclaw-tray.log` and `openclaw-tray.log.old` tails
- `Logs\diagnostics.jsonl` tail
- `crash.log` tail
- latest setup JSONL tails

It excludes raw settings, Gateway records, MCP tokens, device identities,
screenshots, recordings, chat payloads, camera data, and microphone data.
**Open diagnostics folder** opens the selected profile's structured diagnostics
directory. For a non-isolated Release identity, `openclaw://diagnostics` opens
that registered app's folder. Dev uses `openclaw-dev://diagnostics`; isolated
instances do not register either scheme.

### File locations

For the normal unpackaged Release identity:

| Evidence | Default location |
|---|---|
| Tray log | `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` |
| Tray archive | `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log.old` |
| Crash log | `%LOCALAPPDATA%\OpenClawTray\crash.log` |
| Structured events | `%LOCALAPPDATA%\OpenClawTray\Logs\diagnostics.jsonl` |
| Setup logs | `%APPDATA%\OpenClawTray\Logs\Setup\setup-engine-*.jsonl` |
| Setup journal | Beside its setup log as `*.journal.jsonl` |
| Native setup log | `%APPDATA%\OpenClawTray\Logs\Setup\native-setup-*.jsonl` |

Dev uses `OpenClawTray-Dev`. An isolated run puts these artifacts under the
selected direct profile. For packaged storage, resolve the active profile
through the app instead of hard-coding the Windows package publisher suffix.

Set `OPENCLAW_TRAY_TRACE=1` only for a bounded reproduction that needs verbose
Gateway protocol traces. Restart the selected app with the same profile and
unset the variable afterward. Trace is not a replacement for
`app.connection.status` or a real invocation.

## Distinguish setup failures from product defects

For a developer build-host failure, check prerequisites without installing
anything:

```powershell
.\scripts\setup-dev.ps1 -CheckOnly
```

This checks repository build prerequisites such as Git, .NET, Node, Windows SDK,
WebView2, and the build script's prerequisite contract. It does not prove WSL2,
Gateway, MXC, Store, packaged permission, or desktop proof readiness.

For a product setup failure, start from the failed setup JSONL step. A missing
Windows capability, Store access, WSL feature, or runtime prerequisite remains
an environment/setup failure until the supported setup path handles it
incorrectly. Preserve the exact prerequisite result and setup step.

For WSL setup, correlate:

1. `setup-engine-*.jsonl` `step.started`, `cmd.start`, `cmd.done`,
   `step.completed`, and `step.exception` records.
2. The matching `.journal.jsonl` start, completion, and rollback records.
3. Whether the original command timed out. A timeout is an unknown outcome and
   must not be rewritten as a clean failure.
4. The first failed step and any later rollback failure separately.

Inspect the managed distro without mutating it:

```powershell
wsl.exe --list --verbose
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "systemctl --user status openclaw-gateway.service --no-pager || true"
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "journalctl --user-unit openclaw-gateway.service --no-pager -n 80 || true"
```

Use the actual distro name for Dev or custom setup. Do not run the user-service
checks as root, edit the VHD, or inspect the full Gateway configuration. See
[WSL Gateway administration](../../../docs/WSL_GATEWAY_ADMIN.md).

Native Gateway setup writes `native-setup-*.jsonl`. Isolated native Gateway
console output is obtained through its authenticated `logs.tail` setup path,
not an invented host log file. Store package acquisition failures, unavailable
aliases, package registration ambiguity, and unsupported Windows capability are
setup/admission evidence. They are not proof that later Companion runtime code
is defective.

For an installed native Gateway that cannot start, prefer the setup page's
**Collect Gateway diagnostics** and **View setup log** actions. The former runs
`clawctl collect-logs --json` and creates a redacted ZIP under package-owned
state; review it before sharing. `clawctl gateway-service status` is the
read-only lifecycle probe named by startup timeout guidance.

## Connection, pairing, and node flow

Use local MCP diagnostics before changing connection code:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<selected-profile>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$mcpUrl = '<selected-loopback-mcp-url>'
winnode --mcp-url $mcpUrl --list-tools
winnode --mcp-url $mcpUrl --command app.connection.status --params '{}'
winnode --mcp-url $mcpUrl --command app.connection.gateways --params '{}'
winnode --mcp-url $mcpUrl --command app.connection.pendingApprovals --params '{}'
```

Use the endpoint printed by the same selected app or fixture. Profile selection
does not select the MCP port, and an inherited `OPENCLAW_MCP_TOKEN` overrides
the profile token. Repeat the profile, token clearing, and explicit URL in each
fresh agent shell. Follow the canonical
[client binding procedure](../windows-app-mcp/SKILL.md#start-an-isolated-development-app).

`app.connection.status` is the primary read-only projection. Preserve:

- active Gateway and effective mode
- operator and node state
- credential source and credential-resolution status
- accepted and supported protocol versions
- MCP enabled/running/error state
- browser proxy caveat
- pending actions and retry hint
- recent connection diagnostic events

`app.connection.gateways` returns credential-presence booleans, never token
values. A device identity load failure is not permission to replace the key.
Device token precedence must not be downgraded to shared or bootstrap token.
Protocol mismatch is not fixed by repeated reconnects.

Pairing approval tools and reconnect tools mutate live state. Use them only when
that exact action is authorized, with a request ID read from the same selected
Gateway:

- `app.connection.approveDevicePairing`
- `app.connection.rejectDevicePairing`
- `app.connection.approveNodePairing`
- `app.connection.rejectNodePairing`
- `app.connection.reconnect`
- `app.connection.reconnectNode`

Local MCP connection diagnostics do not prove remote `node.invoke`. When the
bug is Gateway-mediated, reproduce the same command through the active Gateway
after pairing, trust, and allowlist state are known.

## Local MCP and capability discovery

Keep local MCP and Gateway node mode distinct:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<selected-profile>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$mcpUrl = '<selected-loopback-mcp-url>'
winnode --mcp-url $mcpUrl --list-tools
winnode --mcp-url $mcpUrl --command app.status --params '{}'
winnode --mcp-url $mcpUrl --command app.connection.status --params '{}'
```

A refused selected endpoint means that tray MCP server is not listening. Port
8765 is only the default. Do not reinstall `winnode`. Confirm **Permissions >
Integrations > Local MCP Server**, the profile, endpoint, and token-file
creation. Never print the token or put it on a command line.

If discovery succeeds but a command is absent, inspect live capability settings
and `NodeCapabilityGating` before editing the static catalog. `browser.proxy` is
intentionally absent until a Gateway client, shared token, enabled bridge, and
verified browser-control endpoint are all present.

## Native Windows capability probes

Run only probes whose side effects and permission prompts are authorized. Start
with discovery and status-shaped commands. Use the same explicit profile and
MCP URL binding established above; exact schemas remain in
[winnode](../winnode/SKILL.md).

```powershell
winnode --mcp-url $mcpUrl --list-tools
winnode --mcp-url $mcpUrl --command camera.list --params '{}'
winnode --mcp-url $mcpUrl --command stt.status --params '{}'
winnode --mcp-url $mcpUrl --command tts.status --params '{}'
winnode --mcp-url $mcpUrl --command system.which --params '{"bins":["git","powershell"]}'
```

Sensitive capture results contain screen images, camera images, locations, or
microphone transcripts. Do not let them print into the agent transcript. For an
authorized non-isolated Release or Dev identity, bind `winnode` to that exact
app and redirect each complete payload to a separate private artifact:

```powershell
$screenArtifact = '<authorized-private-screen-result.json>'
winnode --mcp-url $mcpUrl --command screen.snapshot --params '{"format":"png","maxWidth":1280}' > $screenArtifact

$cameraArtifact = '<authorized-private-camera-result.json>'
winnode --mcp-url $mcpUrl --command camera.snap --params '{"format":"jpeg","maxWidth":1280}' > $cameraArtifact

$sttArtifact = '<authorized-private-stt-result.json>'
winnode --mcp-url $mcpUrl --command stt.transcribe --params '{"maxDurationMs":5000,"language":"auto"}' > $sttArtifact
```

Check `$LASTEXITCODE` after each call. Do not read or publish raw base64,
location, or transcript content unless that exact inspection and destination
are authorized. Report only the bounded non-sensitive metadata needed for the
debug claim. Use the exact command schemas in [winnode](../winnode/SKILL.md).

Non-capture presentation probes can remain inline when authorized:

```powershell
winnode --mcp-url $mcpUrl --command tts.speak --params '{"text":"OpenClaw diagnostic probe"}'
winnode --mcp-url $mcpUrl --command system.notify --params '{"body":"OpenClaw diagnostic probe","sound":false}'
```

Interpret results by boundary:

- Absence from `tools/list` usually means a disabled capability or registration
  gate, not a Windows API failure.
- Screen, camera, and location require remembered app consent and a visible
  per-use indication before sensor access.
- Camera failures that report blocked access require Windows camera access for
  desktop apps. Packaged and unpackaged identities can have different Windows
  permission state.
- Microphone capture and camera clips with audio need the relevant Windows
  microphone permission and an available input device.
- TTS status separates configuration/readiness from playback.
- Notification proof requires a visible toast from the tested app identity.
  A successful return without visible presentation is incomplete UI proof.
  Isolated and fixture identities suppress Windows toasts by design and cannot
  provide visible notification or sensitive capture proof.
- Base64 payload success proves the capability return, not image/audio quality.
  Inspect authorized artifacts without publishing sensitive content.

For packaged permission bugs, record package identity, architecture, app version,
Windows version, the exact privacy setting, and whether the same build behaves
differently unpackaged. Do not treat a permission denial as a code regression
until identity and OS permission state are established.

## `system.run` and MXC

Start with non-executing boundaries:

```powershell
winnode --mcp-url $mcpUrl --command system.which --params '{"bins":["cmd","powershell","git"]}'
winnode --mcp-url $mcpUrl --command system.run.prepare --params '{"command":["cmd.exe","/d","/s","/c","echo diagnostic"],"rawCommand":"echo diagnostic"}'
```

Preserve canonical argv, `rawCommand`, cwd, timeout, selected profile, approval
policy, and the exact tool error. Do not convert a rejected string command into
another shell shape after approval. A non-empty custom environment is rejected.
An omitted cwd uses owned scratch; an explicit cwd must be granted.

`system.execApprovals.get` is not a read-only filesystem probe. It can migrate a
legacy file or create the default approvals file when missing, and the app's
`OPENCLAW_STATE_DIR` can redirect that write outside
`OPENCLAW_TRAY_DATA_DIR`. Invoke it only when that exact policy-file mutation is
authorized, then verify the returned path belongs to the intended profile.

Classify the first failure:

1. command binding or argument validation
2. live System tools permission
3. approval policy or stale approval snapshot
4. MXC availability/probe
5. folder, cwd, executable, or protected-path admission
6. native spawn, output drain, timeout, cancellation, or cleanup

On supported Windows, SDK load, probe, policy, or spawn failures block. They do
not fall back to uncontained execution. Positive unsupported-Windows
compatibility is a separate explicit result.

Local MCP proves the local capability path only. For Gateway-mediated behavior,
also use the same safe command through the active node. Run the CLI where the
selected Gateway is authenticated, obtain the node ID from that same Gateway,
and pass it explicitly:

```powershell
openclaw nodes list --json
openclaw nodes invoke --node '<selected-windows-node-id>' --command system.which --params '{"bins":["git"]}'
```

MXC, `system.run`, exec approvals, or Windows node execution changes require:

```powershell
.\scripts\validate-mxc-e2e.ps1
```

The script sets its own E2E gates and fails if MXC proof skips. `-AllowSkip`
documents a host limitation only; it is not merge validation. This provisions a
fresh WSL Gateway and mutates runtime state, so run it only with explicit
authorization. Otherwise report the gate as blocked.

## Update, installer, and packaged-app boundaries

Update behavior depends on identity:

- Packaged builds are treated as Microsoft Store-managed and intentionally skip
  the release updater.
- Dev and Debug builds intentionally skip release-channel checks.
- Isolated profiles and `OPENCLAW_SKIP_UPDATE_CHECK=1` skip the startup check.
- Production unpackaged Release uses the optional updater.
- An authenticated Gateway on extended-stable can suppress an ordinary
  Companion update.
- An optional updater dependency load failure disables automatic checks for that
  process, logs a warning, and leaves the app usable.

Capture the update status from the Diagnostics support context and the matching
tray-log lines before changing updater code. Do not diagnose an intentional
skip as a failed network check. Do not use a Debug build to prove release
download or install behavior.

For source-level updater or installer changes, use focused checks:

```powershell
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --filter "FullyQualifiedName~UpdateBackendLoaderTests|FullyQualifiedName~UpdateCheckPipelineTests"
.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj' -Filter 'FullyQualifiedName~InstallerIssAssertionTests' -ResultName 'installer-source'
```

Installer source checks do not prove install, upgrade, uninstall, Store
migration, package registration, startup task, protocol activation, or Windows
permission behavior. Those claims require the matching disposable-machine proof
pool and exact signed artifacts. Never test destructive migration or uninstall
against a shared machine without explicit authority and the repository's
uninstall/migration workflow.

## End-to-end repro and proof routes

Choose the smallest route that crosses the failing boundary:

| Boundary | Supported route | What it proves |
|---|---|---|
| Pure helper, projection, or policy | Focused owning test project and filter | Managed contract only |
| App UI with deterministic Gateway data | Build the exact app, then `.\scripts\test-gateway-fixture.ps1 -AppPath '<built-app.exe>'` | Real app, Gateway client, MCP, and selected native UI against synthetic data |
| Local MCP command | `winnode --list-tools`, then `winnode --command ...` | Selected app's local dispatch and capability |
| Raw MCP protocol | Authenticated `initialize`, `tools/list`, `tools/call` against the selected loopback endpoint | HTTP/JSON-RPC/auth/cancellation shape |
| WSL setup, pairing, reconnect | The `windows-wsl-gateway-e2e` proof pool's exact `run-gateway-product-path` command | Real product WSL Gateway path |
| Safe Gateway-to-node invocation | `FullSetup_SafeNodeInvocation_RoutesThroughRealGateway` | Real setup, pairing, node registration, and `system.which` invoke |
| MXC or `system.run` | `.\scripts\validate-mxc-e2e.ps1` | Required real Gateway and MXC proof on a capable host |
| Native screen/camera/audio/notification | Current-head app, exact `winnode` call, visible/inspected result, then Gateway invoke when relevant | Native Windows behavior for the tested identity |
| Installer/update/package behavior | Focused source checks plus applicable disposable-machine proof pool | Installed identity and lifecycle only when the runtime proof ran |

Example real WSL setup/connect lane:

```powershell
$env:OPENCLAW_RUN_E2E = '1'
$env:OPENCLAW_RUN_OLLAMA_E2E = '1'
.\scripts\run-proof-tests.ps1 `
  -Project 'tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj' `
  -Filter 'FullyQualifiedName~OpenClaw.E2ETests.Setup.SetupAndConnectTests' `
  -ResultName 'gateway-setup-connect' `
  -RuntimeIdentifier win-x64
```

Use `win-arm64` on native ARM64. The selected cases must execute with no skipped
required proof. This proof pool provisions a WSL Gateway, mutates runtime state,
and requires maintainer approval. If that authority is unavailable, report the
proof gate as blocked rather than running a partial command.

## Validation after a fix

Every code change still requires the repository closeout:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

Add the focused owner test and the end-to-end route selected above. In a fresh
worktree, build a test project first or omit `--no-restore` once so a successful
no-op is not mistaken for executed tests.

For this skill or other prose-only debugging guidance:

```powershell
.\scripts\validate-docs.ps1
.\scripts\validate-agent-skills.ps1
.\scripts\test-agent-skills-validator.ps1
git diff --check
```

Do not launch the app or mutate a Gateway solely to validate prose.

## Handoff

Report:

- boundary tested
- exact app/Gateway/profile/HEAD
- exact redacted command or UI route
- observed log, diagnostic, tool, or visible signal
- classification: setup/environment, configuration/permission,
  transport/pairing, product code, packaging/update, or blocked
- owner and fix location
- focused validation and end-to-end proof
- unproven boundaries and why

Keep validation confidence, source-review confidence, and runtime/UI proof
confidence separate.

## Source and maintenance

Windows-owned adaptation of the MIT-licensed OpenClaw
[debugging skill](https://github.com/openclaw/openclaw/blob/0969d13b34cb8dc3279eac337b0c4fc589abe204/.agents/skills/openclaw-debugging/SKILL.md).
This is not a synchronized copy. The Windows repository owns its diagnostics,
commands, setup paths, packaging behavior, native permissions, and proof
requirements.
