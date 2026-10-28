# Windows Node Testing Guide

## Overview

The Windows Node feature allows the tray app to receive commands from the OpenClaw agent (canvas, screenshots, screen recordings, camera, location, notifications, and controlled command execution). This is **experimental** and must be explicitly enabled in Settings.

## How to Enable

1. Open the tray app
2. Right-click → Settings
3. Scroll to "ADVANCED (EXPERIMENTAL)"
4. Toggle "Enable Node Mode" ON
5. Click Save

## Companion-App Setup Guidance

For app-owned local WSL setup, after OpenClaw onboard completes or is explicitly skipped, setup runs the installed gateway CLI's non-interactive baseline initializer against the final runtime workspace and then injects fixed Windows-node guidance into that workspace's `AGENTS.md`. The injected block is setup-owned and idempotently replaced between managed markers, preserving user-authored content and file permissions outside those markers and leaving OpenClaw source files unchanged.

**Note on the apply script's WSL invocation.** The `WindowsNodeBootstrapContextStep` apply and rollback scripts are piped to `bash -s` via stdin (`RunInWslAsync(..., inputViaStdin: true)`) rather than the default `bash -c` argv path. This is required because `wsl.exe` performs shell variable expansion on argv before invoking bash, which would drop user-defined `$var` references in the multi-line script (`workspace='...'` followed by `mkdir -p "$workspace"` becomes `mkdir -p ""`). See `docs/WSL_EXE_ARGV_PITFALL.md` for the full writeup.

The guidance helps the first companion-app OpenClaw session route Windows desktop, files, screenshots, camera, notifications, browser proxy, and Windows command tasks through the Windows node / `nodes` tool.

## What You Can Test Now

### Agent-driven UI and MCP validation

For changes touching tray UX, Settings, onboarding, chat/canvas, Command Center, Windows node capabilities, local MCP, gateway pairing/connection, permissions, or diagnostics, use `.agents/skills/openclaw-proof-validation/SKILL.md`.

Short version: run required tests, collect a closeout proof pass with `.\run-app-local.ps1 -Isolated` when UI is involved, use computer-use or developer-provided screenshots/output for the active changed UI state, prove MCP with `winnode` or raw JSON-RPC, prove gateway paths when available, and include current-head concrete output under `## Real behavior proof`. Mid-development computer-use/MCP/rubber-duck validation is fine when explicitly requested or needed to unblock work.

### Reactor preview.12 compatibility proof

The [fixture Gateway harness](GATEWAY_FIXTURE_TESTING.md) can populate the real
app without a running WSL Gateway or AI provider. Run
`.\scripts\run-gateway-fixture.ps1 -AppPath '<built-app.exe>'` to explore, or
`.\scripts\test-gateway-fixture.ps1 -AppPath '<built-app.exe>'` for automated
Workspace sidebar selection, 240-message final-item, navigation and delayed-history proof. Use a
non-Dev Release binary as well as Debug; an empty page or MCP snapshot is not
equivalent to visible native history proof. Streaming still needs separate
coverage.

Both Reactor packages are temporarily pinned to `0.1.0-preview.12` while
[microsoft/microsoft-ui-xaml#11865](https://github.com/microsoft/microsoft-ui-xaml/issues/11865)
awaits a released and validated fix. `ReactorChatTimeline.BuildSafeMarkdown`
uses `MarkdownOptions.ListItem` to replace the default horizontal list row with
an Auto-marker/Star-content Grid, preserving the existing marker, content and
spacing. This locally supplies the wrapping fix from
[microsoft/microsoft-ui-reactor#1197](https://github.com/microsoft/microsoft-ui-reactor/pull/1197)
without adopting preview.13's row eviction behavior.

Run `ReactorMarkdownListProofTests` in `OpenClaw.Tray.UITests` on the host's
native architecture. These mounted tests check ordered/unordered wrapping at
240 DIPs, reflow at 600 DIPs, and nested/loose list content and formatting.
Keep the existing table and disposal proof in the focused run.

Before release or removing the pin, also exercise repeated session switching
while a bring-into-view request is pending, using 240 mixed-height messages.
Verify that message 240 is actually visible, not just that the current scroll
extent was reached. Verify streaming tail-follow and arrivals while scrolled
up. Do not substitute extent-only scrolling: the alternative workaround
stopped near message 233 in the reported scenario.
[Tracking issue #1424](https://github.com/openclaw/openclaw-windows-node/issues/1424)
owns the package upgrade and removal gates.

### New command MCP contract

Every new Windows node call must be exposed through local MCP and `winnode`: register the capability, update `McpToolBridge.CommandDescriptions`, update `.agents/skills/winnode/SKILL.md`, add focused tests, and prove discovery/invocation with `winnode` or raw MCP JSON-RPC.

### Permissions exec policy editor

The Permissions page shows the `main`, wildcard (`*`), then defaults cascade
and lists both wildcard and main allowlist entries. Removing a displayed entry
edits its original bucket, not every matching pattern. A duplicate in the other
bucket remains effective until it is removed separately. Adding a rule writes
to `main` and preserves inherited wildcard security and ask settings, except
for the existing transition from effective Deny to a main allowlist.

Use an isolated profile for visual proof: seed distinguishable wildcard and
main rules, remove the wildcard row, then reopen the page and confirm only
that entry is gone from its original bucket. Also check equal patterns in both
buckets and adding a main rule while inheriting wildcard Full. These editor
checks do not require executing a command and are not MXC containment proof.

### Node Sandbox settings persistence

Node containment (filesystem, network, clipboard, desktop isolation,
timeout, output limit, and sandbox/fallback switches) is saved in `settings.json`,
not the gateway configuration or `exec-approvals.json`. The runtime reads the
same app-owned settings.

The build/release matrix in `.github\workflows\ci.yml`, `build.ps1`, and
`installer.iss` uses the following profiles for both x64 and ARM64:

| Distribution | Runtime identity | Settings profile |
| --- | --- | --- |
| GitHub `OpenClawCompanion-Setup-<arch>.exe` (Inno) | Release | `%APPDATA%\OpenClawTray\settings.json` |
| GitHub `OpenClawTray-<version>-win-<arch>.zip` (portable executable and DLLs) | Release | Same release profile, not the extracted ZIP directory |
| GitHub `OpenClaw-Dev-<arch>.zip` (signed MSIX, certificate, metadata, and `INSTALL.txt`) | Dev | `OpenClawTray-Dev` through Windows package storage |
| Store `.msix` and multi-architecture `.msixbundle` (unsigned CI submission artifacts, signed by the Store) | Release | `OpenClawTray` through Windows package storage |
| `OpenClaw-MigrationTest-<arch>.msix` (workflow-only, test-signed production package) | Release, despite "dev" in the workflow artifact name | Same Store identity/profile; use a disposable machine |
| Local `-DevBuild` executable/publish output and `/DDevBuild=1` Inno installer with a matching Dev payload | Dev | `%APPDATA%\OpenClawTray-Dev\settings.json` |

Stable and alpha describe the update/version channel, not the app identity.
Both channels ship release-identity EXE/portable artifacts and Dev-identity MSIX
ZIPs. Debug also defaults to release identity unless `DevBuild=true`.

For MSIX, Windows can redirect the logical roaming path into
`%LOCALAPPDATA%\Packages\<CompanionPackageFamilyName>\LocalCache\Roaming\<OpenClawTray-or-OpenClawTray-Dev>\settings.json`.
Resolve the installed package family rather than hard-coding a publisher suffix.
The app continues using the Windows roaming-folder API and its compiled profile
name; do not scan and merge package-private and unpackaged copies. Store adoption
is owned by the explicit migration flow described in [RELEASING.md](RELEASING.md).

For isolated development runs, `%OPENCLAW_TRAY_DATA_DIR%\settings.json` overrides
the profile for either identity. This is not a way to run production Store
migration against scratch data; its admission guard rejects redirected paths.

Use the profile of the running Companion, not an old unpackaged copy or the
native Gateway's package. `exec-approvals.json` separately stores command
approval policy in the app data directory, or `OPENCLAW_STATE_DIR` when set.
A missing approval file is not evidence that sandbox settings were lost.

For a persistence smoke, set a non-default command timeout (for example,
95 seconds), navigate away and back, then restart the isolated app and reopen
Node Sandbox. Confirm the displayed and persisted timeout stays at 95 seconds,
and repeat with a preset and individual controls. Merely constructing the page
must not save its XAML defaults over existing settings.

`SettingsManagerIsolationTests` checks default compiled-identity routing,
isolated overrides, persistence and runtime policy in ordinary and package-private
directory layouts, and preservation of the other identity's settings.
The normal tray suite runs release identity; CI recompiles these tests and the
Sandbox page initialization guards with `-p:DevBuild=true` as a separate step.
Directory-layout tests do not emulate MSIX virtualization or replace installed
package proof. When validating deployment changes, repeat the smoke above on
each installed package identity and the unpackaged executable.

### 1. Settings Toggle
- Verify the toggle appears in Settings under "ADVANCED"
- Verify it saves and persists across app restarts

### 2. Node Connection
- Enable Node Mode and save
- Watch for "🔌 Node Mode Active" toast notification
- Check logs at `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` for:
  ```
  [INFO] Starting Windows Node connection to ws://...
  [INFO] Node connected, waiting for challenge...
  [INFO] Registered capability: screen (2 commands)
  [INFO] All capabilities registered
  [INFO] Node status: Connected
  ```

### 3. Screen Capture Notification
- The first screen capture requires explicit remembered consent.
- Every screen capture shows a "📸 Capturing screen" toast before sensor access.

### 4. Command Center
- Open the tray status detail or launch `openclaw://commandcenter`
- In Node Mode, verify the window shows gateway channel health from node `health` events plus a synthesized local Windows node when operator `node.list` is not connected
- Check diagnostics for pairing approval, pending reapproval, stale health, all-stopped channels, allowlist filtering, browser control host availability for `browser.proxy`, and usage-cost gaps
- When only the synthesized local Windows node is available, verify its locally declared capabilities/commands are labeled unverified and are not counted as approved/effective
- For `pending-reapproval`, verify effective capabilities/commands remain unchanged, pending declarations are listed separately, and the copy action emits `openclaw nodes approve <pendingRequestId>`
- During a changed-command handshake, verify authoritative `pending-reapproval` replaces the generic node-pair approval card and exposes only the node-list trust command; explicitly typed device role-upgrade, Node mode off/hidden, and failure cards remain higher priority
- If the gateway omits a safe pending request ID, verify the copy action emits `openclaw nodes pending`, labels it as discovery only, and does not offer reconnect-after-approval yet
- Approve the request explicitly, reconnect the node, and verify the effective capability/command counts update and the pending reapproval warning clears
- Use "Copy fix" only for safe repair commands; privacy-sensitive commands remain informational unless you explicitly opt in on the gateway

## What Requires Gateway Support

These features need the gateway to send `node.invoke` commands:

| Command | Description | Expected Behavior |
|---------|-------------|-------------------|
| `canvas.present` | Show WebView2 window | Opens floating window with URL or HTML |
| `canvas.hide` | Hide canvas window | Closes the canvas window |
| `canvas.eval` | Execute JavaScript | Runs JS in canvas, returns result |
| `canvas.snapshot` | Capture canvas | Returns base64 PNG of canvas content |
| `canvas.a2ui.pushJSONL` | Legacy A2UI JSONL push | Routes through same renderer path as `canvas.a2ui.push` |
| `screen.snapshot` | Take screenshot | Requires remembered consent, shows a notification before every capture, and returns base64 |
| `screen.record` | Record short screen clip | Returns MP4/base64 metadata; requires explicit gateway allowlist |
| `system.notify` | Show notification | Displays toast notification |
| `system.run` | Controlled command execution | Uses local exec approval policy. A simple unquoted gateway command can bind to an allowlisted executable and run as direct argv; shell syntax remains one-time. Prompt decisions show a Windows Allow once / Always allow / Deny dialog when Allow always is safe. |
| `system.run.prepare` | Pre-flight command execution | Parses and validates a `system.run` invocation without executing it |
| `system.which` | Resolve executables | Returns absolute paths for requested binaries |
| `camera.list` | Enumerate cameras | Returns device IDs and names |
| `camera.snap` | Capture photo | Requires remembered consent, shows a notification before every capture, and returns a base64 image (NV12 fallback) |
| `camera.clip` | Capture video clip | Returns MP4/base64 metadata |
| `location.get` | Get Windows location | Requires remembered consent, shows a notification before every access, and uses Windows location permission/settings |
| `device.info` / `device.status` | Device metadata/status | Returns host/app/locale plus battery/storage/network/uptime payloads |
| `browser.proxy` | Proxy browser-control host requests | Requires Browser proxy bridge enabled, a compatible browser-control host listening on gateway port + 2, and matching browser-control auth |
| `tts.speak` | Speak text aloud | Requires Text-to-speech playback enabled in Settings; gateway mode also requires `tts.speak` in `gateway.nodes.allowCommands` |
| `stt.transcribe` | Bounded microphone transcription | Requires Speech-to-text enabled in Settings; uses local Whisper.net |
| `stt.listen` | Voice-activity microphone transcription | Returns when the user stops speaking or timeout expires |
| `stt.status` | Speech-to-text readiness | Returns Whisper.net model download/readiness state |
| `ollama.models` | Discover local Ollama chat models | Requires Share Windows Ollama in Permissions; reads the separately installed Ollama service on `127.0.0.1:11434` |
| `ollama.chat` | Run bounded local Ollama inference | Requires Share Windows Ollama, gateway command approval, and an exact local model returned by `ollama.models` |

For the Windows command-approval dialog, verify a long command remains scrollable
and selectable, but **Allow Once** becomes enabled after the 1.5-second
anti-clickthrough delay without scrolling. **Deny** remains available immediately;
initial focus stays on Deny, and Escape or closing the dialog denies the request.
**Allow Always** follows the same delay when offered. It is hidden for
`ask=always`, non-allowlist security, or commands without a reusable identity,
including inline PowerShell `-Command` scripts. Check a simple resolved executable
under `security=allowlist` and `ask=on-miss` to exercise the remembered-rule action.

### Cancelling an invocation

The gateway may send the `node.invoke.cancel` event with
`payload.invokeId` matching an active `node.invoke.request`. The Windows node
cancels only that invocation and completes its original result with
`ok: false, error: "cancelled"`; unknown or already-completed IDs are ignored.
The legacy `payload.requestId` spelling is also accepted for compatibility.
Operation completion is the linearization point: once capability execution
returns and atomically marks the invocation complete, later cancellation is too
late and the completed result is preserved.

For local MCP, send a JSON-RPC `notifications/cancelled` notification with
`params.requestId` matching the active `tools/call` JSON-RPC ID. Cancellation
must stop queued camera admission, recording delays/frame waits, and active
recording cleanup rather than only abandoning the HTTP waiter.

## Capabilities Advertised

When the node connects, it advertises these capabilities:
- `canvas` - WebView2-based canvas window
- `screen` - Screen snapshot and recording via Windows.Graphics.Capture
- `system` - Notifications, command execution (`system.run`, `system.run.prepare`, `system.which`), exec approval policy
- `camera` - MediaCapture photo/video capture (frame reader fallback)
- `location` - Windows.Devices.Geolocation
- `device` - Host/app metadata and lightweight status
- `browser` - Local `browser.proxy` bridge to a browser-control host on gateway port + 2, when enabled in Settings
- `tts` - Windows speech synthesis or ElevenLabs playback, when enabled in Settings
- `stt` - Local speech-to-text via Whisper.net, when enabled in Settings
- `local-inference` - `ollama.models` and `ollama.chat` against a separately installed Windows Ollama service, when explicitly enabled in Permissions

Local MCP clients also see MCP-only `app.*` commands such as `app.navigate`, `app.status`, `app.chat.snapshot`/`app.chat.send`/`app.chat.reset`, and `app.chat.queue.list`/`app.chat.queue.cancel`. Connection diagnostics and setup tools live under `app.connection.*`; use `app.connection.status` to inspect active gateway, operator/node credential state, MCP runtime status, browser proxy caveat, pending approval commands, and recent diagnostics, and `app.connection.gateways` to list saved gateway records without token values. These are local testing and automation hooks registered with the tray's MCP server and are not advertised to the gateway WebSocket.

## Security Features

- **URL Validation**: Canvas blocks `file://`, `javascript:`, localhost, private IPs, IPv6 localhost
- **Sensitive Capture Consent**: Screen snapshots, camera photos, and location reads require remembered per-capability consent
- **Per-use Capture Notification**: Every screen snapshot, camera photo, and location read is visibly indicated before sensor access
- **Screen Recording Allowlist**: `screen.record` must be explicitly allowed by the gateway and does not leave a hidden local MP4 copy on Windows
- **Session Attribution**: Only the optional top-level `sessionKey` stamped by the Gateway on `node.invoke.request` is trusted. Older Gateways omit it, so those invokes remain unattributed; a caller-supplied nested `args.sessionKey` is never used as a fallback.
- **Command Center Redaction**: recent node invoke activity records command name, status, duration, node id, and privacy class only; it does not store base64 payloads, screenshots, recordings, tokens, or command arguments
- **Node Mode Toggle**: Must be explicitly enabled by user
- **Ollama Sharing Toggle**: Off by default; enabling it shares Windows Ollama compute with the active paired local or remote gateway without changing the app-managed Local AI provider
- **Command Validation**: Only alphanumeric commands with dots/hyphens allowed

## Troubleshooting

### Node doesn't connect
- Check the active gateway in Connection settings. Gateway records live in `%APPDATA%\OpenClawTray\gateways.json`; post-pairing device tokens live under `%APPDATA%\OpenClawTray\gateways\<gateway-id>\device-key-ed25519.json`.
- Check logs for connection errors
- If logs report that the saved device identity could not be loaded, fix access to the existing identity file or use an explicit reset/re-pair action. The tray preserves an unreadable or corrupt identity instead of replacing it automatically.
- Verify gateway is running and accessible
- If only a bootstrap token exists, finish pairing or approve the device; paired device tokens take precedence on future connects.

### No "Node Mode Active" notification
- Ensure Windows notifications are enabled for the app
- Check if notification settings in the app are enabled

### Browser control stays enabled but never declares `browser`
- Setup-code / QR pairing can connect with a device token and leave `GatewayRecord.SharedGatewayToken` empty. Browser control will not declare `browser` / `browser.proxy` until a shared gateway token is saved for that gateway.
- Expect Connection capability pills to say **Needs gateway shared token** (not "Enabled, not active yet") only while the node WebSocket session is live and the shared token is missing. Disconnected or attached-but-disconnected states should ask for reconnect, not a token paste. The pill keeps that short label; its tooltip matches Command Center remediation detail.
- Command Center, Connection pill tooltips, and `app.connection.status` / `app.connection.gateways` use the same live-session rule for the shared-token caveat. For a remote (non-loopback) gateway without an explicit `BrowserControlPort` or SSH browser-proxy forward - including SSH tunnels whose effective URL is `127.0.0.1` - that caveat also mentions the endpoint/forward requirement; the shared token alone is not enough for usable remote browser.proxy.
- Enter the gateway shared token in Settings, save, and reconnect node mode. Bootstrap tokens are not the shared gateway token.

### `browser.proxy` reports no browser-control host
- Confirm the Browser proxy bridge toggle is enabled in Settings, then save and reconnect or re-pair if the gateway keeps an older command snapshot.
- The bridge is local-only: it calls `http://127.0.0.1:<gateway-port+2>` from Windows. For a gateway on `ws://127.0.0.1:18789`, the browser-control host must listen on `127.0.0.1:18791`.
- In managed SSH tunnel mode, keep Browser proxy bridge enabled so the tray forwards local gateway port + 2 to remote gateway port + 2. Settings shows a selectable preview of the exact `ssh -N -L ...` command.
- If using a manual SSH tunnel, add both forwards, for example: `ssh -N -L 18789:127.0.0.1:18789 -L 18791:127.0.0.1:18791 <user>@<host>`. If the SSH daemon is not listening on port 22, include `-p <ssh-port>`. If local and remote gateway ports differ, forward `<local-gateway-port+2>` to `127.0.0.1:<remote-gateway-port+2>`.
- Advanced split/remote topologies can pin the browser-control listener with the active gateway record's `BrowserControlPort` field in `%APPDATA%\OpenClawTray\gateways.json`. This value is a local TCP port on Windows and is scoped to that gateway record. Configure it only for a trusted browser-control forward, because `browser.proxy` sends the saved shared gateway token to the selected local listener for browser-control authentication. When a gateway uses SSH, tunnel-derived `localPort + 2` browser-control routing is used only when that gateway's managed tunnel has `IncludeBrowserProxyForward` enabled; otherwise set `BrowserControlPort` to a trusted manual forward.
- A local SSH forward is not enough if the remote browser-control host is not running. Command Center port diagnostics should show whether the local gateway and browser-control ports are listening and which process owns them.
- If Command Center shows the browser-control port listening but `browser.proxy` returns an auth error, verify the Windows Settings gateway token matches the browser-control host token/password. QR/bootstrap pairing can connect the node without saving a shared gateway token, but browser-control auth may still require one.
- A local smoke can verify the host dependency without proving gateway invoke auth: start the upstream browser-control host with a temporary no-secret config, confirm `http://127.0.0.1:<gateway-port+2>/` and `/tabs` return HTTP 200, then stop the captured host process. The full parity smoke is not complete until `openclaw nodes invoke --command browser.proxy` succeeds through the active gateway.

### Canvas window doesn't appear
- Check logs for `canvas.present` command received
- Verify URL is not blocked by security validation

### Camera permission denied
- If you see "Camera access blocked", enable camera access for desktop apps in Windows Privacy settings
- Packaged MSIX builds will show the system consent prompt automatically

### Local sandbox validation
- Shared unit tests inject the official SDK's `IMxcProcess` and typed native probe. They prove the managed contract, not OS containment.
- The exact `Microsoft.Mxc.Sdk` 1.0.0 NuGet dependency supplies native assets. No root npm bridge or `wxc-exec` discovery is used.
- On supported Windows, every `system.run` requires a complete typed request to admit BaseContainer, native sensitive-path denial, and `needsDaclAugmentation=false`. Unknown, failed or augmented admission blocks execution. Only a positively unsupported Windows SKU or successful known weaker-tier probe with the BaseContainer API absent selects direct uncontained compatibility execution. SDK loading, request policy and spawn errors never do. Approvals, pinned argv/cwd, output limits, cancellation and deadlines remain enforced; sandbox file/network/clipboard/UI permissions do not. OS IsolationSession capability is independent, read from the official DLL's probe facts. Companion's OS minimum still applies.
- The Windows SKU is checked before FFI. A known unsupported Server SKU selects compatibility execution; an unknown SKU blocks execution. Availability never changes saved permissions.
- The [sandbox presets](../README.md#sandbox-command-execution) do not change capability membership or bypass approvals. `SetupAccessDraft` carries the selected preset to `SetupSettingsWriter`, which initializes untouched settings once through `ISettingsStore` and rejects stale writes. Existing Custom settings survive setup resume/restart.
- Reapplying a preset previews and confirms replacement of its folder/network fields. Settings derives Custom from the current policy, not the last clicked preset. Node Sandbox uses one editable folder list for resolved preset roots and custom paths, with no separate scope selector. Strict, Balanced and Open retain their default scope/network behavior; their cards describe intent, while the confirmation retains the exact access description.
- `MxcFolderPermissions` projects the same normalized exact-path entries for the editor and SDK builder. Custom path overrides replace known-folder overrides, which replace preset entries. Equal-priority duplicate conflicts prefer No access, then RO, then RW. An exact-root edit replaces its generated entry. Each personal/custom row offers No access, Read only and Read & write, stays visible and can be changed again. Reapplying a preset resets overrides through the existing confirmation; there is no Remove button.
- No access is persisted as `SandboxFolderAccess.NoAccess` (3), distinct from legacy `Blocked` (2) quarantine. User No access blocks a folder's otherwise inherited access; explicitly listed deeper RO/RW children retain their permissions. The SDK applies specificity, with deny strongest at the same path. Cwd and executable admission use this same rule, not a broad-parent grant. Built-in runtime and owned scratch grants remain available as more-specific exceptions. Exact runtime-deny attempts fail visibly.
- Immutable credential/state protection is separate from user No access. Every allowed grant, cwd and executable within a protected root is rejected even if it is more specific than that root. User deny paths receive the same no-follow metadata/alias admission and no-volume-root checks as grants. Missing leaves may be prospective denies only with an existing verified parent; missing parents block instead of moving a user deny upward. Protected future-path handling remains unchanged.
- The list coalesces lexical known-folder aliases without inferring physical aliases. Edits retain Documents/Desktop/Downloads identity; Profile/OneDrive edits use explicit paths. Legacy Blocked entries remain quarantined and visible for review, never become No access or active grants on page load. Exact Windows runtime entries remain visibly read-only. Clipboard and limits remain independent.
- Windows UI API compatibility is fixed inside containment (`Ui.Disable=false`), not a user permission or preset field. Win32k syscall blocking is retired so PowerShell and console tools can initialize. Container desktop isolation, desktop/job restrictions, input-injection denial and configured clipboard/network/file policies remain enforced. Default settings and legacy `SystemRunAllowWindowsUi=false/true` must build the same compatible request; the retired field is omitted on the next save while unknown fields and other permissions survive. No compatibility section, toggle, warning or confirmation remains on Node Sandbox. Preset confirmation still has a localized Cancel action.
- UI regression proof: verify the blue shield header, stable title, colored preset icons, one folder list and Custom after editing. Check keyboard/pointer edits, delayed realization, recycled containers and zero settings writes on open. Verify every preset/custom row can change to No access and back; denied parents keep explicitly listed child access; aliases coalesce; persisted 2/3 values remain distinct. Native product proof must verify read/create/overwrite, denied-region cwd/image rejection, protected-grandchild rejection and owned scratch under denied parents. Do not infer rename/delete support from probes where their positive controls failed.
- Permissions' System tools switch remains the command kill switch. The retired `SystemRunBlockHostFallbackWhenMxcUnavailable` setting was hidden JSON, not a UI option; it no longer overrides the supported/unsupported Windows policy.
- Non-root directory grants cover descendants. **No readonly or readwrite volume-root entries are emitted.** Native Windows proof found readonly roots permitted unrelated sibling reads; they are not treated as harmless metadata. The same no-root restriction applies to runtime roots, explicit grants and cwd authority.
- Balanced/Open cover the OS user profile and resolved Documents/Desktop/Downloads/available configured OneDrive roots, including redirected roots outside the profile. Other users, whole drives and arbitrary projects outside the declared scope are not included. Custom grants remain explicit; user-folder RO plus selected RW children is supported.
- `MxcRequestContext` is the small injectable OS snapshot used by the product builder. Native tests replace its personal/sensitive roots with synthetic fixtures, never real user-profile grants. Missing/inaccessible required roots block with diagnostics. Optional unavailable OneDrive is explicitly not included.
- `MxcFolderAdmission` validates grant/cwd ancestors root-first with `FILE_READ_ATTRIBUTES`, `OPEN_REPARSE_POINT` and `BACKUP_SEMANTICS`. Admission does not read file data or request hydration. Ordinary paths and directory `CLOUD`/`CLOUD_1` through `CLOUD_F` tags must resolve to the same normalized final path. Symlinks, junctions, legacy OneDrive, ProjFS, WCI, unknown tags, non-directory cloud tags, metadata errors and final-path mismatches remain blocked. Reparse tags are ignored when the reparse attribute is absent, as required by the Windows metadata contract. Physical alias failures identify the offending component separately from unsupported reparse points; mapped-drive and SUBST spellings remain fail-closed, not an assurance that another spelling such as UNC will work. A null `LinkTarget` is not proof of safety. No volume-root grants, protected-deny exceptions or automatic cwd grants are added.
- Cloud admission does not relax startup/build/installer cleanup: those paths still refuse every reparse point. Product OneDrive validation must separately prove actual cloud-tagged RO/RW, protected-child denial and online-only hydration, with owned dummy files and cleanup, on each affected architecture. Pure tag tests and synthetic profiles are not native Cloud Files proof. Network permission applies to the command, not independent sync clients; writable synchronized folders can sync changes.
- Protected roots include active/default OpenClaw settings, `.openclaw`, `.openclaw-companion`, `.ssh`, supported browser profiles and PSReadLine. Companion's private root includes host-executed package cleanup scripts and receipts, including prospective state not yet created. Open profile grants and explicit descendant grants never override these immutable denies. Existing legacy grants previously filtered for protected-root conflicts stay blocked until explicitly reviewed.
- Protected denies retain their logical spelling and also include physical spellings obtained from same-handle directory metadata. This covers state redirected by MSIX without guessing package family names or denying the whole `Packages` tree. Missing descendants map beneath the nearest verified existing directory; the builder applies its missing-parent deny boundary independently to both spellings. Metadata failure, unsupported reparse ancestry or a non-directory component blocks admission rather than dropping protection. These mappings expand denies only, never user grants.
- `NodeService` also supplies a fresh `MxcWindowsRequestContext` for each contained command, including when Companion is unpackaged. The adapter enumerates current-user Windows registrations without starting packages. Only the Store Companion name `OpenClawFoundation.OpenClaw` with publisher `CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC`, and the development name `OpenClawFoundation.OpenClaw.Dev` with publisher `CN=OpenClaw Local Development`, add protection. Framework/resource packages and unrelated identities (including Native Gateway) are excluded. Windows-reported family names are validated as single components; publisher hashes are never guessed. `LocalAppData` uses `KF_FLAG_NO_PACKAGE_REDIRECTION` so a packaged caller does not nest another `Packages` tree inside its own cache.
  Each matching registration adds only `Packages\<family>\LocalCache\Roaming\<data-leaf>` and `Packages\<family>\LocalCache\Local\<data-leaf>`, where Store uses `OpenClawTray` and Dev uses `OpenClawTray-Dev`. Existing unpackaged and current-identity physical protection remains. Missing state stays covered by the existing missing-parent boundary without creating folders or changing ACLs. No blanket package-root deny is added: assets and owned MXC scratch/SDK-default temporary storage retain their existing policy. Absent registrations are valid; enumeration, matching-family validation and needed local-path resolution failures visibly block the command, never return partial protection. A new registration is observed on the next contained command.
  `MxcCompanionPackagePathsTests` source-links the pure projection and covers fake inventory, fail-closed errors, fresh immutable snapshots, future state, package scratch and the NodeService callback contract. Native proof must separately exercise the Windows adapter from both registered identities and unpackaged operation, deny dummy state in both Companion packages (all four state leaves), preserve ordinary sibling/scratch access, and compare ACLs. Source-linked packaged console proof does not establish full Companion UI or gateway-path proof.
- Cwd outside effective grants is blocked; omitted cwd uses private working scratch.
  An absent or blank `OPENCLAW_TRAY_LOCALAPPDATA_DIR` uses Windows LocalAppData
  for the app-owned scratch root. Relative or malformed roots are rejected
  before creating directories. In PowerShell, remove an override with
  `Remove-Item Env:\OPENCLAW_TRAY_LOCALAPPDATA_DIR -ErrorAction SilentlyContinue`;
  passing `$null` to a .NET string parameter can leave an empty value instead.
  After creating scratch, the runner resolves its physical directory for the SDK's
  grant and default cwd, retaining the logical path for cleanup and late-launch
  ownership. This exception is limited to runner-owned scratch, not general
  mapped-drive, SUBST or virtualized caller paths. Packaged proof must verify
  current-source logical/physical deny behavior and ordinary sibling positive
  controls for each identity; a minimal packaged harness is not full
  Companion UI proof.
  The SDK owns the child environment and package-private temporary storage:
  `ContainerRequest.Environment` is omitted. OpenClaw does not replace
  TEMP/TMP, APPDATA/LOCALAPPDATA, PATH or other Windows environment values.
  The SDK builds the Windows user-profile environment without copying the
  parent process environment; persisted user variables can still be present.
  File-path exclusions are not an environment-secret filter. Caller-supplied
  env remains rejected by approval identity validation.
  `NativeSdk_DefaultEnvironmentProvidesTempWithoutInheritingParentProcessVariables`
  proves TEMP creation/read without mkdir and checks a synthetic parent-only
  variable is absent. The native permission matrix also uses SDK TEMP directly.
  `NativeSdk_TempIsReadyForCreateRenameDeleteWithoutPayloadInitialization`
  separately tests the full temporary-file lifecycle. TEMP rename is denied
  on the tested Windows client even with the SDK defaults; do not hide that
  failure or infer full tool compatibility from successful creation/read.
  No initializer, directory-name guessing, extra launcher or host TEMP grant
  is added. Native payloads are fixed synthetic commands, not another OpenClaw
  process that could use the SDK-default appdata paths.
- Output retains bounded UTF-8 bytes per stream, including truncation markers; BOMs can be fragmented and discarded bytes continue to drain. Shared incremental capture exposes complete characters (including 1-3 byte short output) before EOF. Deadline responses preserve captured stdout/stderr without waiting for cleanup. `[truncated]` denotes either a byte-budget limit or capture cut off before EOF, including a deadline before any output arrived. Incomplete BOMs/characters at a cutoff are not invented; real EOF still uses the decoder's replacement behavior.
- One command deadline covers launch/wait/drains. Direct root exit is independent of output EOF: at most 500 ms of post-exit drain grace fits inside the remaining deadline. Native execution observes and caches root exit through the SDK's non-finalizing `TryGetExitCode` before calling `WaitAsync`. A known root exit retains its exit code rather than becoming a timeout because a descendant holds a pipe or SDK finalization crosses the deadline. Incomplete capture uses the existing truncation marker when it fits the byte budget. Native SDK finalization still reaps descendants and completes teardown before ownership is released. Only unsupported-Windows host compatibility execution leaves intentional background descendants running after normal root exit.
- Host pipe readers use cancellable public `ReadAsync`; native SDK readers use synchronous `Read` interrupted by `IMxcStreamCloser`. Cancellation returns promptly while a late launch, blocked kill or pending SDK finalization remains owned. Capacity (eight owners) is held until readers, native tasks and the scratch cleanup attempt finish. In host compatibility execution, a background descendant retaining scratch as its cwd can prevent directory deletion; that failure is logged, not remedied by killing the background application. Running commands retain their immutable policy; pending commands revalidate settings and approvals before spawn.
- Saturation returns a `node_busy` tool error for either execution route, not a sandbox denial or a successful RPC payload with a failed exit. The eight owners can be running commands or pending cleanup. Admission has not selected an execution route, so busy diagnostics report neither host execution nor applied containment. Retry when an owner finishes; the limit still includes late launches and cleanup.
- Approved direct executable arguments preserve embedded CR/LF, including multiline PowerShell `-Command` scripts. Null/NUL arguments are rejected for every route. Cmd transport additionally rejects CR/LF and requires the canonical `/d /s /c` shape whenever command mode is selected. Combined, attached, whitespace-prefixed, late or embedded `/c`, `/k` and `/r` switches are not reinterpreted with direct-argv quoting or silently rewritten after approval.
- `NativeRootExitRetainsStatusWhenFinalizationCrossesDeadline` deterministically tests delayed finalization with a fake SDK process. The smallest opt-in native smoke is `NativeSdk_NormalRootExitReapsBackgroundDescendant` plus `NativeSdk_DeadlineRetainsStdoutAndStderr`: it verifies nonzero root exit, a live descendant reaped on normal completion, retained output and a genuinely running root timing out. It does not claim to reproduce slow native finalization.
- Never run `wxc-host-prep`, mutate host DACLs or force a native tier as proof.
- Native tests are separately opt-in with `OPENCLAW_RUN_MXC_NATIVE_PROOF=1`. Skipped tests are not proof. Prove RO/RW, protected children, aliases, environment secrecy, internet-only networking, UI/clipboard, timeout/kill and x64/ARM64 before completion.
  Product-runner native tests instead require `OPENCLAW_MXC_PROOF_ROOT` outside
  the checkout and all three isolated tray roots before process creation. The
  `validate-mxc-userfolders-native.ps1` script records process ownership, forbids
  skipped native results and compares real appdata metadata. Its fixture checks
  synthetic protected children, ungranted sibling **reads and writes**, redirected
  roots, selected writes and new descendants; it records fixture/ancestor ACLs.
  Run the script in PowerShell 7.2 or newer. Supply an existing, writable `-ArtifactDirectory` outside the checkout and a
  nonempty JSON `-SourceManifest`. The registry defaults to `runs.jsonl` under
  that artifact root; `-RegistryPath` is an optional absolute override whose
  parent must already exist. `-PreflightOnly` validates paths and write access
  without creating a run or claiming native proof. The script restores all
  changed environment variables in `finally` and clears unrelated `OPENCLAW_RUN_*`
  flags during the native lane. The full native lane still includes the known
  TEMP-rename proof; preflight/plumbing checks do not bypass or satisfy it.
- Safe focused packaging/script checks:

  ```powershell
  .\tests\PackagingTests\Test-MxcNativeProofPlumbing.ps1 -ArtifactDirectory '<owned external artifact root>'
  .\tests\PackagingTests\Test-MxcInstallDelete.ps1 -CompilerPath '<installed ISCC.exe>'
  ```

  The first is explicitly plumbing-only, with an intercepted failing launch.
  The second compiles only the seven legacy deletion entries and their guard
  into a minimal fixture (`Uninstallable=no`, `CreateUninstallRegKey=no`).
  It never executes the product installer or an uninstaller. Ordinary/empty
  paths delete normally; junction ancestors/final directories and access
  failures are refused per entry. Unknown files and the app-root SDK DLL survive.
  Optional `-Baseline` records the unguarded engine's behavior, which may already
  preserve junction victims on newer Inno versions. Do not disable those protections
  or call preservation a reproduced deletion.
- For MXC-related merge validation, prefer the formal script below because it sets the required gates and fails if MXC is skipped.

  ```powershell
  .\scripts\validate-mxc-e2e.ps1
  ```

### `system.run` failure outcomes

- An explicit pre-execution security, ask, allowlist, or user denial, including a
  revoked approval, carries `error.code = "SYSTEM_RUN_DENIED"` in both Gateway
  `node.invoke.result` and legacy invocation responses. The message is retained.
  This lets the Gateway distinguish a definite denial from an unknown outcome.
  Disabling Run system tools after command registration also returns this code
  for `system.run` and `system.run.prepare`, before approval dispatch or execution.
  Handler exceptions, failed or unavailable policy revalidation, command timeouts, and
  failures after process launch do not acquire this code. The same distinction
  applies at the SDK prelaunch boundary: confirmed approval or permission
  revocation is a policy denial, while unavailable revalidation is an internal
  failure. Both prevent launch.
  A malformed, unreadable, unsupported or untrusted approval store is unavailable
  policy, not proof that the owner revoked approval. Initial authorization,
  post-prompt checks and execution-boundary revalidation retain that distinction
  from the same policy read, fail closed and do not emit `SYSTEM_RUN_DENIED`.
  With no legacy store pending migration, a missing current store still uses
  the normal fresh-policy defaults.
  Presenter exceptions, failed dispatcher enqueue, dialog errors and cancelled
  prompts fail closed as `InternalError (prompt-failed)` without using
  `askFallback` or claiming a user refusal. An expired unanswered prompt likewise
  remains `InternalError (prompt-timeout)`, even if a late answer arrives.
  Confirmed policy tightening while a
  prompt is open retains the `ValidationFailed` result code, with typed
  definite-denial provenance so the Gateway receives `SYSTEM_RUN_DENIED`.
- A contained, non-timeout process exit `0xC0000142` (`STATUS_DLL_INIT_FAILED`)
  includes a diagnostic explaining that Windows UI APIs (USER32) or another DLL
  dependency can be involved. The original exit code, output, native error detail,
  and containment remain unchanged. The current SDK policy already enables
  contained Windows UI API compatibility; there is no user-controlled UI opt-in.
  This is contextual troubleshooting guidance, not proof that every DLL
  initialization failure has the same cause. The diagnostic does not alter
  permissions, retry the command, or fall back to host execution.

### Full Gateway `system.run` MXC runtime proof
- The focused E2E below provisions a fresh WSL Gateway, starts an isolated tray instance with fixed contained Windows UI API compatibility, sets local exec approval policy, invokes the real Gateway `node.invoke` path, and checks SDK admission/execution and protected writes. Native `MxcCommandRunnerIntegrationTests.NativeSdk_DefaultPolicyStartsPowerShellWithoutOptIn` additionally verifies PowerShell startup and output with default settings, without enabling an opt-in. **Provisioning is a runtime mutation and requires authorization.** If it is forbidden, record this gate as blocked rather than launching it or using `-AllowSkip` as validation.
- Run it when validating the Gateway/Windows node runtime path, not just direct MCP or shared library behavior.
- GitHub-hosted Actions runners do not provide a working MXC/AppContainer runtime. The regular cloud E2E matrix should report these MXC proofs as skipped while still running the rest of setup-connect. Run the proof on a local MXC-enabled Windows machine. Only set `OPENCLAW_RUN_MXC_E2E=1` in GitHub Actions when using an MXC-enabled self-hosted runner.
- Use `.\scripts\validate-mxc-e2e.ps1` for normal local validation. It sets `OPENCLAW_RUN_E2E` and `OPENCLAW_RUN_MXC_E2E`, runs the real Gateway MXC proofs, and fails if the MXC proof skips. `-AllowSkip` is only for documenting a non-MXC host, not for merge validation of MXC-related work.
- When reproducing this manually against an existing Gateway, confirm
  `gateway.nodes.denyCommands` does not block `system.run`,
  `system.run.prepare`, or `system.which`, then approve any
  `pending-reapproval` request with
  `openclaw nodes approve <pendingRequestId>`. Current gateways include these
  commands in the canonical Windows desktop defaults. Older or deliberately
  customized gateways may still need exact `gateway.nodes.allowCommands`
  entries.

  ```powershell
  .\build.ps1
  $env:OPENCLAW_REPO_ROOT = (Get-Location).Path
  $env:OPENCLAW_RUN_E2E = "1"
  dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj `
    --no-restore `
    --filter "FullyQualifiedName~RealGateway_SystemRun" `
    --logger "console;verbosity=normal" `
    -r win-x64
  ```

- Expected proof markers:
  - The bound-hostname proof succeeds with a local `**/hostname.exe` rule, logs `promptAttempted=false`, and executes its pinned argv through the SDK.
  - Gateway response contains PowerShell output `OPENCLAW_GATEWAY_SYSTEM_RUN_MXC_OK` with `exitCode=0`.
  - The denied-write proof targets a fresh file under the isolated tray data directory, returns non-zero, and leaves that file absent.
  - `openclaw-tray.log` contains `[mxc] operation=spawn requested=base-container applied=unknown` and grant counts.
  - `openclaw-tray.log` contains `[mxc] operation=exit applied=unknown` with the exit/timeout result. The public SDK has no post-launch applied-tier field; logs must not fabricate one.
- E2E artifacts are written under `TestResults\E2E\<run-id>` and skip known secret-bearing files such as gateway records and settings.

## Remaining Work (Roadmap)

1. ~~**system.run + exec approvals**~~ ✅ Implemented
    - `system.run` with PowerShell/cmd support
    - `system.run.prepare` pre-flight command
    - `system.which` command lookup
    - `system.execApprovals` allowlist flow with base-hash optimistic concurrency for remote edits
    - `system.run` environment override sanitizer blocks path/toolchain injection and secret-looking variables
2. ~~**screen.record**~~ ✅ Implemented
    - Graphics Capture video recording (MP4/base64)
3. ~~**camera.clip**~~ ✅ Implemented
    - Short webcam video capture (MediaCapture + encoding)
4. ~~**A2UI pushJSONL alias + device status**~~ ✅ Implemented
    - Legacy `canvas.a2ui.pushJSONL`
    - Safe `device.info` / `device.status`
5. ~~**Command Center diagnostics**~~ ✅ Implemented
    - Channel/node/usage/pairing/allowlist diagnostics and recent invoke timeline
6. **Packaging & consent prompts**
    - MSIX packaging with camera/screen capabilities for system prompts
7. **Test matrix & polish**
    - Canvas/screen/camera regression tests
    - Handle timeouts/disconnects, reduce verbose logging

## Files Involved

- `src/OpenClaw.Shared/WindowsNodeClient.cs` - Node protocol client
- `src/OpenClaw.Shared/Capabilities/*.cs` - Capability handlers
- `src/OpenClaw.Tray.WinUI/Services/Connection/GatewayRegistry.cs` - persistent gateway records
- `src/OpenClaw.Connection/GatewayConnectionManager.cs` - public lifecycle façade, operator/state/tunnel orchestration
- `src/OpenClaw.Connection/NodeConnectionCoordinator.cs` - node generation, start/recovery, connector events, and telemetry
- `src/OpenClaw.Connection/BootstrapTokenLifecycle.cs` - bootstrap/device-token handoff and durable clear timing
- `src/OpenClaw.Connection/DevicePairApprovalCoordinator.cs` - device role-upgrade approval and bounded reconnect
- `src/OpenClaw.Connection/CredentialResolver.cs` - device-token/shared/bootstrap credential precedence
- `src/OpenClaw.Tray.WinUI/Services/NodeService.cs` - Orchestrates capabilities
- `src/OpenClaw.Tray.WinUI/Services/ScreenCaptureService.cs` - screen snapshots
- `src/OpenClaw.Tray.WinUI/Services/ScreenRecordingService.cs` - screen recordings
- `src/OpenClaw.Tray.WinUI/Services/CameraCaptureService.cs` - camera photo/video capture
- `src/OpenClaw.Tray.WinUI/Windows/CanvasWindow.xaml` - WebView2 canvas
