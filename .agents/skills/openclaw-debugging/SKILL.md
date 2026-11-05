---
name: openclaw-debugging
description: Debug OpenClaw Windows Companion, setup, Gateway connection, node commands, packaging, or system.run failures by finding the first failing boundary.
---

# OpenClaw Windows Debugging

Start from the reported behavior, find the first failing boundary, and collect
the narrowest evidence that separates setup, configuration, transport, product
code, Windows permission, packaging, and runtime failures.

This skill does not authorize installing software, changing shared runtimes,
resetting pairing, approving requests, invoking sensitive capabilities, or
editing user configuration. Get that authority first. Never print or share
Gateway tokens, setup codes, MCP tokens, device identities, private keys, raw
settings, screenshots, recordings, camera or microphone data, or chat payloads.

## Read first

Read only the owner named by the first failing boundary:
[architecture ledger](../../../docs/ARCHITECTURE.md),
[connection architecture](../../../docs/CONNECTION_ARCHITECTURE.md),
[local MCP binding](../windows-app-mcp/SKILL.md),
[winnode commands](../winnode/SKILL.md),
[Windows node testing](../../../docs/WINDOWS_NODE_TESTING.md),
[setup engine](../../../docs/SETUP_ENGINE_REDESIGN.md),
[onboarding](../../../docs/ONBOARDING_WIZARD.md), or
[proof selection](../openclaw-proof-validation/SKILL.md) before runtime or UI
claims. File locations, the verbose trace, and capability probes are in
[REFERENCE.md](REFERENCE.md).

## Default loop

1. Record the exact HEAD, dirty files, app build, runtime identity, selected
   profile, Gateway kind, and repro command or UI route.
2. State one suspected boundary before changing code.
3. Collect the narrowest signal from that boundary.
4. Reproduce without switching profile, build, Gateway, transport, command, or
   permission state unless that variable is the test.
5. Classify the failure as environment/setup, configuration/permission,
   transport/pairing, product code, packaging/update, or unproven.
6. Patch the owner of the first incorrect behavior, not the next layer that
   reports it.
7. Rerun the exact failing probe, then follow the
   [validation policy](../../../docs/TEST_COVERAGE.md#validation-policy).

An exit code alone is not proof. Check that the intended tests ran, that the
command appeared in `tools/list`, that the app came from the expected HEAD, and
that UI evidence shows the asserted state.

## Choose the boundary

| Symptom | First evidence | Boundary interpretation |
|---|---|---|
| App will not start, exits, or opens the wrong instance | `openclaw-tray.log`, `crash.log`, `run.marker`, activation arguments, runtime identity | Startup, single-instance, activation, migration admission, or crash handling |
| Setup fails before Companion is usable | Latest setup JSONL and matching journal, failed step, command exit/timeout, rollback result | Setup pipeline or host prerequisite, not automatically a product runtime defect |
| WSL Gateway is installed but unavailable | WSL service status and journal as user `openclaw`, then Windows reachability | Gateway service/runtime or Windows-to-WSL reachability |
| Native Gateway is installed but unavailable | Existing setup logs, **Collect Gateway diagnostics**, or **Open log file** | Package service, isolated session, listener ownership, or native runtime readiness; `clawctl gateway-service status` needs runtime authority |
| Companion is disconnected or pairing is pending | `app.connection.status`, `app.connection.gateways`, pending approvals, connection event timeline | Registry, credential, protocol, operator, node, or approval state |
| Local MCP is unavailable | Selected profile, `mcp-token.txt` existence, listener state, `winnode --list-tools` | Local MCP startup/authentication, not Gateway pairing |
| Command is absent | Live `tools/list`, capability settings, disabled commands, Gateway allowlist | Registration or gating before invocation |
| Command is listed but fails | Exact `winnode` request, tool error, tray log entry, native permission state | Capability implementation or Windows runtime permission |
| Local MCP works but remote use fails | Same command through Gateway `node.invoke`, node pairing/trust, allowlist | Gateway transport or policy; local MCP is only supporting evidence |
| `system.run` fails | `system.which`, `system.run.prepare`, approval snapshot, MXC diagnostics, exact argv/cwd | Binding, approval, policy, containment, execution, or cleanup |
| Update check behaves unexpectedly | Build identity, package state, update status, tray log | Store/Dev/Debug intentional skip, release updater, or optional dependency |
| Installer or migration fails | Exact artifact/identity, installer source checks, migration receipts and runtime logs | Packaging/migration; unit or source checks do not prove installed behavior |
| UI state is wrong but snapshots look correct | Current-head native UI evidence on the selected profile | Presentation or lifecycle; MCP snapshots do not prove pixels |

Stop at the first boundary whose result differs from its contract. Do not reset
settings, delete state, re-pair, restart an unowned Gateway, or swap transport
to make the symptom disappear.

## Establish the exact runtime

Record the branch, full HEAD, and dirty files, then:

- Companion build configuration and architecture.
- Release or Dev identity.
- Packaged MSIX, unpackaged Inno/portable, fixture, or source-built app.
- Selected profile and every `OPENCLAW_TRAY_*` override.
- WSL, Store native Gateway, development native Gateway patch, remote Gateway,
  or synthetic fixture.
- Local MCP or Gateway node transport.

`OPENCLAW_TRAY_DATA_DIR` is a direct profile folder. It moves settings, logs,
crash state, the MCP token, the run marker, identities, and the single-instance
mutex. It does not move setup-local or Local AI state, which stay under the
real `%LOCALAPPDATA%` profile unless `OPENCLAW_TRAY_APPDATA_DIR` and
`OPENCLAW_TRAY_LOCALAPPDATA_DIR` are also set. `OPENCLAW_STATE_DIR` in the app
process separately moves the exec-approvals file, so record it before claiming
approval isolation. Release and Dev profiles are distinct. Packaged apps can use
redirected storage, so read the profile from the running app's Diagnostics page
rather than guessing a package path.

Isolated and fixture instances suppress Windows toasts, skip startup update
checks, and do not register URI schemes. Use an authorized non-isolated
identity for toast or capture-indicator proof. Do not invoke `screen.snapshot`,
`camera.snap`, or `location.get` in an isolated or fixture instance, because
sensor access can continue after the suppressed indicator.

The fixture Gateway is deterministic app and UI evidence. It is not real WSL
setup, pairing, provider, node invocation, sensor, notification, or MXC proof.

## Use the repository diagnostics

Open **Settings > Diagnostics**, then **Create diagnostics bundle**. Review the
preview before copying or saving it. The bundle holds bounded, sanitized:

- support and debug summaries
- connection event timeline
- `openclaw-tray.log` and `openclaw-tray.log.old` tails
- `Logs\diagnostics.jsonl` tail
- `crash.log` tail
- latest setup JSONL tails

It excludes raw settings, Gateway records, MCP tokens, device identities,
screenshots, recordings, chat payloads, camera data, and microphone data.
**Open diagnostics folder** opens the selected profile's diagnostics folder.
A non-isolated Release app also opens it from `openclaw://diagnostics`; Dev uses
`openclaw-dev://diagnostics`. Isolated instances register neither scheme.

## Distinguish setup failures from product defects

For a developer build-host failure, run `.\scripts\setup-dev.ps1 -CheckOnly`.
It checks build prerequisites only, not WSL2, Gateway, MXC, Store, packaged
permission, or desktop proof readiness.

For a product setup failure, start from the failed setup JSONL step. A missing
Windows capability, Store access, WSL feature, or runtime prerequisite stays an
environment/setup failure until the supported setup path mishandles it.

For WSL setup, correlate:

1. `setup-engine-*.jsonl` `step.started`, `cmd.start`, `cmd.done`,
   `step.completed`, and `step.exception` records.
2. The matching `.journal.jsonl` start, completion, and rollback records.
3. Whether the original command timed out. A timeout is an unknown outcome, not
   a clean failure.
4. The first failed step and any later rollback failure, separately.

List distros first. `wsl.exe -d` starts a stopped distro, so run the service
probes only when the distro is already Running or starting it is authorized:

```powershell
wsl.exe --list --verbose
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "systemctl --user status openclaw-gateway.service --no-pager || true"
wsl.exe -d OpenClawGateway --user openclaw -- bash -lc "journalctl --user-unit openclaw-gateway.service --no-pager -n 80 || true"
```

Use the actual distro name for Dev or custom setup. Do not run the user-service
checks as root, edit the VHD, or read the full Gateway configuration. See
[WSL Gateway administration](../../../docs/WSL_GATEWAY_ADMIN.md).

Native Gateway setup writes `native-setup-*.jsonl`. Isolated native Gateway
console output comes through its authenticated `logs.tail` setup path, not a
host log file. Store acquisition failures, unavailable aliases, ambiguous
package registration, and unsupported Windows capability are setup evidence,
not proof of a later Companion runtime defect.

For an installed native Gateway that cannot start, use the setup page's
**Collect Gateway diagnostics** and **Open log file** actions. The first runs
`clawctl collect-logs --json` and creates a redacted ZIP under package-owned
state; review it before sharing. `clawctl gateway-service status` can start an
already-recorded isolated session, so it is not a passive inventory probe.
Use it only with authority to change that runtime; see the
[native Gateway procedure](../native-gateway-from-source/SKILL.md).

## Connection, pairing, and node flow

Bind the client as in
[windows-app-mcp](../windows-app-mcp/SKILL.md#bind-the-client-to-one-app), then
read connection state before changing connection code:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<selected-profile>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$mcpUrl = '<selected-loopback-mcp-url>'
winnode --mcp-url $mcpUrl --command app.connection.status --params '{}'
winnode --mcp-url $mcpUrl --command app.connection.gateways --params '{}'
winnode --mcp-url $mcpUrl --command app.connection.pendingApprovals --params '{}'
```

`app.connection.status` is the primary read-only projection; its fields are
listed in [winnode](../winnode/SKILL.md). `app.connection.gateways` returns
credential-presence booleans, never token values. A device identity load
failure is not permission to replace the key. Device token precedence must not
be downgraded to a shared or bootstrap token. Repeated reconnects do not fix a
protocol mismatch.

The pairing approve/reject and reconnect tools mutate live state. Use them only
when that exact action is authorized, with a request ID read from the same
Gateway.

Local MCP diagnostics do not prove remote `node.invoke`. For Gateway-mediated
bugs, reproduce the same command through the active Gateway after pairing,
trust, and allowlist state are known.

If discovery succeeds but a command is absent, inspect live capability settings
and `NodeCapabilityGating` before editing the static catalog. For a refused
endpoint, see [windows-app-mcp](../windows-app-mcp/SKILL.md#bind-the-client-to-one-app).

## Native Windows capabilities

Probe recipes are in [REFERENCE.md](REFERENCE.md#native-capability-probes).
Screen, camera, location, and microphone results are sensitive. Never let them
print into the agent transcript; redirect each payload to an authorized private
artifact and report only non-sensitive metadata.

For packaged permission bugs, record package identity, architecture, app
version, Windows version, the exact privacy setting, and whether the same build
behaves differently unpackaged. A permission denial is not a code regression
until identity and OS permission state are established.

## `system.run` and MXC

Start with non-executing boundaries, in one bound block:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<selected-profile>'
Remove-Item Env:\OPENCLAW_MCP_TOKEN -ErrorAction SilentlyContinue
$mcpUrl = '<selected-loopback-mcp-url>'
winnode --mcp-url $mcpUrl --command system.which --params '{"bins":["cmd","powershell","git"]}'
winnode --mcp-url $mcpUrl --command system.run.prepare --params '{"command":["cmd.exe","/d","/s","/c","echo diagnostic"],"rawCommand":"echo diagnostic"}'
```

Preserve canonical argv, `rawCommand`, cwd, timeout, selected profile, approval
policy, and the exact tool error. Do not convert a rejected string command into
another shell shape after approval. A non-empty custom environment is rejected.
An omitted cwd uses owned scratch; an explicit cwd must be granted.

`system.execApprovals.get` is not a read-only probe. It can migrate a legacy
file or create the default approvals file, and the app's `OPENCLAW_STATE_DIR`
can redirect that write outside `OPENCLAW_TRAY_DATA_DIR`. Invoke it only when
that policy-file write is authorized, then check that the returned path belongs
to the intended profile.

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

For Gateway-mediated behavior, also run the same safe command through the
active node, from a shell where the selected Gateway is authenticated:

```powershell
openclaw nodes list --json
openclaw nodes invoke --node '<selected-windows-node-id>' --command system.which --params '{"bins":["git"]}'
```

For the required MXC proof and its authority, see
[MXC and `system.run`](../../../AGENTS.md#mxc-and-systemrun).

## Update, installer, and packaged-app boundaries

Update behavior depends on identity:

- Packaged builds are Microsoft Store-managed and skip the release updater.
- Dev and Debug builds skip release-channel checks.
- Isolated profiles and `OPENCLAW_SKIP_UPDATE_CHECK=1` skip the startup check.
- Production unpackaged Release uses the optional updater.
- An authenticated Gateway on extended-stable can suppress an ordinary
  Companion update.
- An optional updater dependency load failure disables automatic checks for that
  process, logs a warning, and leaves the app usable.

Capture the update status from the Diagnostics support context and the matching
tray-log lines before changing updater code. An intentional skip is not a
failed network check. A Debug build cannot prove release download or install.

For updater or installer source changes, run the focused owner tests (for
example `UpdateCheckPipelineTests` or `InstallerIssAssertionTests`). Source
checks do not prove install, upgrade, uninstall, Store migration, package
registration, startup task, protocol activation, or permission behavior. Those
need the matching disposable-machine pool in
[proof pools](../../../docs/PROOF_POOLS.md) and exact signed artifacts. Never
test destructive migration or uninstall on a shared machine without explicit
authority and the [uninstall](../uninstall/SKILL.md) workflow.

## End-to-end repro routes

Choose the smallest route that crosses the failing boundary:

| Boundary | Route |
|---|---|
| Pure helper, projection, or policy | Focused owning test project and filter |
| App UI with deterministic Gateway data | [Fixture workflow](../../../docs/GATEWAY_FIXTURE_TESTING.md) |
| Local MCP command or raw protocol | [windows-app-mcp](../windows-app-mcp/SKILL.md) |
| WSL setup, pairing, reconnect, safe Gateway invoke | `windows-wsl-gateway-e2e` in [proof pools](../../../docs/PROOF_POOLS.md) |
| MXC or `system.run` | [MXC and `system.run`](../../../AGENTS.md#mxc-and-systemrun) |
| Native screen, camera, audio, notification | Current-head app, exact `winnode` call, inspected result, then Gateway invoke when relevant |
| Installer, update, or package behavior | Focused source checks plus the applicable disposable-machine pool |

Real Gateway routes provision runtimes and need explicit authority. Without it,
report the gate as blocked rather than running a partial command.

## Handoff

Report:

- boundary tested
- exact app, Gateway, profile, and HEAD
- exact redacted command or UI route
- observed log, diagnostic, tool, or visible signal
- classification: setup/environment, configuration/permission,
  transport/pairing, product code, packaging/update, or blocked
- owner and fix location
- focused validation and end-to-end proof
- unproven boundaries and why

Keep validation, source-review, and runtime/UI proof confidence separate.

Adapted from the MIT-licensed OpenClaw
[debugging skill](https://github.com/openclaw/openclaw/blob/0969d13b34cb8dc3279eac337b0c4fc589abe204/.agents/skills/openclaw-debugging/SKILL.md).
It is not a synchronized copy.
