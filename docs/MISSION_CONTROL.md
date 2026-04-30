# Mission Control: Topology-Aware Command Center Plan

This plan turns the Windows tray from a "connected/not connected" companion into a Mission Control surface for any OpenClaw gateway topology. It is based on a deep audit of:

- Current Windows code in this repository on `mission-control-audit`.
- Current upstream Mac app code in `openclaw/apps/macos/Sources/OpenClaw`.
- Current upstream gateway, node policy, browser proxy, health, presence, usage, pairing, and discovery code in `openclaw/src` and `openclaw/extensions`.

The main product decision is deliberate: **do not make a native Windows gateway the center of gravity.** The Windows app should be a first-class node and command center for any OpenClaw gateway: Mac over SSH tunnel, WSL, Windows Node.js, LAN, Tailscale, or unknown/remote.

## 1. Goals

1. Make the Windows tray explain *what* it is connected to: local gateway, WSL gateway, Mac via SSH tunnel, Tailscale/LAN gateway, or unknown remote.
2. Reach deeper Mac parity by porting the valuable Mac "mission control" ideas, not just matching command names.
3. Keep OpenClaw open and topology-neutral: the tray should observe, classify, diagnose, and repair; it should not force one gateway hosting model.
4. Prioritize privacy and safety. Diagnostics must not trigger camera, screen recording, microphone, or broad command execution.
5. Make every repair action copyable, explainable, and topology-aware.

## 2. Audit findings

### 2.1 Windows current state

Windows now has a strong foundation:

- Node Mode with canvas, camera, screen snapshot/record, location, device info/status, system commands, notifications, and exec approval policy.
- Command Center status detail window with channels, sessions, usage, local/operator node inventory, allowlist diagnostics, pairing warnings, and activity stream.
- SSH tunnel settings and service.
- Activity Stream and support-bundle copy path that avoid storing invoke payloads.
- Deep links including `openclaw://commandcenter`.

The biggest missing model is not another gateway implementation. It is **topology state**. Current settings collapse all topologies into:

- `GatewayUrl`
- `UseSshTunnel`
- SSH host/user/ports
- `EnableNodeMode`

There is no first-class concept of "Mac over SSH", "WSL", "Windows native", "Tailscale", "LAN", or "unknown".

### 2.2 Mac Mission Control behaviors worth porting

The Mac app is not just a menu bar icon. It is a gateway/node/control-plane cockpit.

Important Mac surfaces:

- Status icon with activity badge and gateway error dot.
- Hover HUD with current status and last tool/activity.
- WebChat, Canvas, Settings, Onboarding, Agent Events, Notify Overlay, Voice/Talk overlays.
- Menu sections for sessions, usage, cost, nodes, gateway discovery, channel state, browser control, camera/canvas/voice toggles, exec approvals, debug actions, and update status.
- Per-session submenus with preview, thinking/verbose settings, reset, compact, delete, and log opening.
- Per-node submenus with copy actions for node ID, name, IP, platform, versions, caps, and commands.
- Channel settings driven by gateway schemas and channel health/probe details.
- Debug/diagnostic actions: health check, test heartbeat, open logs, open config, open session store, restart gateway, reset SSH tunnel, port diagnostics, kill process by PID, rolling JSONL diagnostics, and verbose logging.

Important Mac gateway lifecycle pieces:

- `GatewayProcessManager` state machine: stopped, starting, running, attachedExisting, failed.
- Attach-existing path before spawning a gateway.
- `GatewayEnvironment`: Node runtime, OpenClaw CLI location/version, port/bind resolution.
- `PortGuardian`: identifies listeners on gateway ports, classifies expected vs unexpected processes, and can kill with confirmation.
- `GatewayEndpointStore`: async-stream state for local/remote/unconfigured endpoint readiness.
- Gateway discovery via Bonjour/SRV plus Tailscale selection rules.
- Remote SSH tunnel actor with robust SSH options, fast-fail check, random local port fallback, and tunnel reuse across app restarts.
- Control channel with friendly error mapping and recovery scheduling.
- Presence reporter every 180 seconds with host/IP/mode/version/platform/device fields.

Important Mac security/privacy pieces:

- Permission matrix for notifications, automation, accessibility, screen recording, microphone, speech, camera, and location.
- Onboarding security banner warning that agents can run commands, read/write files, and capture screenshots.
- Exec approval UX with Deny / Allow Once / Allow Always.
- Command display sanitizer for control chars, invisible characters, and non-ASCII spaces to prevent spoofing.
- Glob allowlist matcher semantics.
- Host environment sanitizer with large inherited secret/toolchain blocklist, PATH override rejection, and shell-wrapper allowlist.
- Exec approval edits with base-hash optimistic concurrency: **implemented for `system.execApprovals.get/set`; stale remote writes are rejected**
- Pairing prompt with name, node ID, platform, app, IP, and approve/reject/later actions.

### 2.3 Gateway and browser proxy findings

`browser.proxy` is the main concrete remaining Mac node command gap.

Gateway/browser facts:

- `browser.proxy` is a canonical node command and included in Windows platform defaults at the gateway policy level.
- Gateway policy still requires both gates:
  - command allowed by platform defaults or `gateway.nodes.allowCommands`
  - command declared by the node
- The browser plugin/node-host contract is:
  - input: `method`, `path`, optional `query`, `body`, `timeoutMs`, `profile`
  - default timeout: 20 seconds
  - output: `{ result, files? }`
  - files are base64 payloads with path/mime metadata
- Persistent profile mutations are blocked at gateway and node-host levels.
- Mac implements `browser.proxy` only for local mode, proxying to `127.0.0.1:{gatewayPort+2}` with Bearer or `x-openclaw-password` auth, and a 10 MB/file extraction cap.
- Windows managed SSH tunnel mode now forwards both the gateway port and the browser-control companion port (`local+2` to `remote+2`) when the browser proxy capability is enabled, so Mac-over-SSH topologies can satisfy the same local-only browser proxy contract.

Gateway APIs and signals worth surfacing:

- `hello-ok` snapshot/policy fields, including tick interval and limits.
- `health`, `presence`, `tick`, `status`, `system-presence`, `sessions.*`, `usage.status`, `usage.cost`, `sessions.usage*`, `node.list`, `node.describe`, pairing APIs, and config/wizard APIs.
- Snapshot fields such as presence, health, stateVersion, uptimeMs, auth/session defaults.
- Non-loopback gateway security expectations: use `wss`, auth/trusted proxy, and explicit Control UI origins.
- Discovery signals: mDNS/SRV, wide-area DNS-SD, Tailscale modes.

## 3. Topology model

### 3.1 Gateway kinds

Initial enum:

| Kind | Meaning | Detection signals |
|---|---|---|
| `MacOverSsh` | Localhost URL backed by an SSH tunnel to a Mac/remote host | `UseSshTunnel=true`, localhost gateway URL, SSH host present; future: presence platform macOS |
| `Wsl` | Gateway likely running in WSL2 | localhost URL without tunnel, `wsl.exe` available, port/listener/process hints indicate WSL |
| `WindowsNative` | Gateway likely running directly on Windows | localhost URL without tunnel and no WSL evidence |
| `Tailscale` | Gateway reached via Tailscale DNS/IP | host ends `.ts.net` or IP is in 100.64.0.0/10 |
| `RemoteLan` | Gateway reached via LAN/mDNS/private host | RFC1918 IP, `.local`, or non-loopback private hostname |
| `Remote` | Public/unknown non-local remote gateway | non-loopback public host |
| `Unknown` | Cannot classify | invalid/missing URL or conflicting settings |

### 3.2 State objects

Additive shared models:

```csharp
public enum GatewayKind
{
    Unknown,
    WindowsNative,
    Wsl,
    MacOverSsh,
    Tailscale,
    RemoteLan,
    Remote
}

public enum TunnelStatus
{
    NotConfigured,
    Stopped,
    Starting,
    Up,
    Restarting,
    Failed
}

public sealed class GatewayTopologyInfo
{
    public GatewayKind DetectedKind { get; set; }
    public string DisplayName { get; set; }
    public string GatewayUrl { get; set; }
    public string Host { get; set; }
    public bool UsesSshTunnel { get; set; }
    public string Transport { get; set; }
    public string Detail { get; set; }
}

public sealed class TunnelCommandCenterInfo
{
    public TunnelStatus Status { get; set; }
    public string LocalEndpoint { get; set; }
    public string RemoteEndpoint { get; set; }
    public string? Host { get; set; }
    public string? User { get; set; }
    public string? LastError { get; set; }
    public DateTime? StartedAt { get; set; }
}
```

Extend `GatewayCommandCenterState` with:

```csharp
public GatewayTopologyInfo Topology { get; set; } = new();
public TunnelCommandCenterInfo? Tunnel { get; set; }
```

### 3.3 Classifier rules

Phase 1 classifier should be pure and unit-testable:

1. If `UseSshTunnel` is true and SSH host is set:
   - if gateway URL host is localhost/127.0.0.1/::1, classify `MacOverSsh` for now.
   - if SSH host ends `.ts.net`, include "over Tailscale SSH" in detail but keep tunnel as the primary transport.
2. Else if gateway URL host is localhost/127.0.0.1/::1:
   - classify `WindowsNative` initially.
   - a later WSL probe can refine to `Wsl`.
3. Else if host ends `.ts.net` or IP is in 100.64.0.0/10:
   - classify `Tailscale`.
4. Else if host is RFC1918, `.local`, or common private names:
   - classify `RemoteLan`.
5. Else if host is non-empty:
   - classify `Remote`.
6. Else:
   - classify `Unknown`.

Phase 2 WSL refinement:

- Probe `wsl.exe -l -q` with a short timeout.
- Optional port/process detection should be cached and never block UI.
- If localhost gateway is connected and WSL evidence is strong, classify `Wsl`.

## 4. Command Center UX target

### 4.1 Gateway/topology header card

Add a top card under the current status header:

- "Gateway: Windows native / Mac over SSH / WSL / Tailscale / LAN / Remote / Unknown"
- URL host and transport: `ws`, `wss`, `ssh tunnel`, `tailnet`, `lan`
- tunnel state if configured: `Up`, `Restarting`, `Failed`, `Stopped`
- last health timestamp and gateway version/uptime once available from protocol

### 4.2 Diagnostics categories

Add categories beyond current node/channel/allowlist/parity:

| Category | Examples | Repair action |
|---|---|---|
| `topology` | Localhost URL but no local/tunnel evidence; remote plaintext `ws://`; unknown public host | Explain expected topology; copy URL/settings hints |
| `tunnel` | SSH tunnel stopped/restarting/failed | Copy `ssh -N -L ...` command; "Reset tunnel" later |
| `wsl` | Localhost likely backed by WSL; NAT or distro reboot may break it | Show WSL-specific diagnostic hints |
| `tailscale` | Tailnet host but no tunnel/direct auth mismatch | Show Tailscale/wss/auth hints |
| `browser` | `browser.proxy` disabled, policy-filtered, or missing a gateway+2 browser-control host | Explain Settings, allowlist, SSH forward, or local browser-host repair path |
| `gateway` | stale health/stateVersion, auth error, not connected | Existing patterns plus topology-specific detail |

### 4.3 Tray menu badge

Add a small topology badge next to status:

- "Gateway: Connected - Mac over SSH"
- "Gateway: Connected - Windows native"
- "Gateway: Connected - Tailscale"

### 4.4 Settings hint

In Settings, show read-only detected topology near gateway URL/tunnel settings: **implemented with a live summary under the topology guide**

- detected kind
- whether settings imply tunnel/direct
- warning if URL/tunnel conflict

### 4.5 Future Mission Control pages

Keep StatusDetailWindow as the first Command Center, but plan for tabs/sections:

1. Overview
2. Gateway topology
3. Tunnel/transport
4. Channels
5. Sessions
6. Nodes/capabilities
7. Command policy/allowlist
8. Pairing/devices
9. Activity/events
10. Permissions/privacy
11. Logs/debug/repair

## 5. Mac parity matrix

### 5.1 Node command surface

| Command area | Mac status | Windows status | Priority |
|---|---|---|---|
| Canvas core | Present | Mostly present | Verify defaults, payload names, A2UI bridge, snapshot shape |
| Screen snapshot | Present | Present | Verify defaults: max width, format, quality, metadata |
| Screen record | Present | Present | Verify clamps/audio fields; do not live-test without permission |
| Camera list/snap/clip | Present | Present | Verify facing/deviceId/delay/default quality |
| Location | Present | Present | Align error tokens and permission mode |
| Device info/status | Present | Present | Done; keep payload shape tests |
| System notify | Present | Present | Add overlay/priority parity later |
| System run/which | Present | Present | Verify push event names and approval reasons |
| Exec approvals get/set | Present | Present | Base-hash optimistic concurrency implemented |
| Browser proxy | Present, local-only | Local bridge present; live smoke blocked until browser-control host listens on gateway+2 | Continue host setup/live-smoke guidance |

### 5.2 Mission Control surfaces

| Mac capability | Windows today | Plan |
|---|---|---|
| Gateway process state | Implemented for detected/managed runtimes | Command Center shows topology, gateway listener process/PID, and managed/detected SSH context; process manager remains only for a future owned local Windows gateway |
| Endpoint store/discovery | Implemented first slice | Settings topology presets and detected topology summaries classify local, SSH, WSL, and remote gateway shapes |
| SSH tunnel robust state | Implemented | Managed SSH tunnel status/error/runtime details surface in Settings, Command Center, support context, and restart actions |
| PortGuardian | Partial | Read-only port diagnostics identify local listeners and owning process/PID; destructive kill actions remain intentionally absent |
| HealthStore derived states | Implemented first slice | Command Center warnings include topology-aware gateway, tunnel, browser-control, channel, usage, and node health |
| Nodes submenu copy actions | Implemented | Per-node copy and full node inventory copy include command groups, filtered commands, disabled settings, and parity gaps |
| Session previews/settings | Implemented | Tray session rows include previews plus thinking/verbose, reset, compact, and delete actions |
| Cost 30-day chart | Implemented | Command Center renders 30-day cost bars from `usage.cost` daily totals |
| Agent events ring | Implemented | Activity Stream keeps a 400-event rich ring and support bundle window |
| Permissions matrix | Implemented first slice | Command Center shows safe Windows privacy settings deep links without probing devices |
| Onboarding security banner | Implemented | Setup Wizard warns about agent control of enabled local command/screen/camera/location/browser/canvas surfaces |
| Debug actions | Implemented | Tray, Command Center, deep links, and PowerToys expose logs/config/diagnostics, health/update actions, managed SSH restart, support context, debug bundle, browser setup, and copyable diagnostics/summaries |
| Voice/Talk | Missing | Separate roadmap track |
| Cron/Skills settings | Missing/limited | Separate roadmap track |

## 6. Browser proxy feasibility

### 6.1 What it is

`browser.proxy` is not a generic HTTP proxy. It is a node command that forwards browser-plugin requests through a node-host endpoint and returns structured results and optional extracted files.

### 6.2 Windows options

1. **Local gateway/browser-host proxy parity**
   - Implement only when gateway is local or tunnel-local.
   - Proxy to `127.0.0.1:{gatewayPort+2}` like Mac.
   - Use Bearer/token or password header as gateway expects.
   - Enforce same method/path/query/body/timeout/profile contract.
   - Enforce same persistent-profile mutation block and file-size cap.
   - Best Mac parity, but depends on browser plugin host availability on Windows.

2. **Edge/WebView2 DevTools bridge**
   - Use WebView2/Edge DevTools protocol from the tray.
   - More Windows-native, but diverges from gateway browser extension contract.
   - Riskier and likely not the immediate parity path.

3. **Do not implement in tray; require browser extension node-host**
   - Keep tray focused on desktop node and command center.
   - Command Center explains why `browser.proxy` is absent and how to install/enable the browser plugin.
   - Lowest risk, but leaves a Mac command gap.

Recommended: investigate option 1 first, with `browser.proxy` gated to local/tunnel topologies and disabled for remote public gateways unless the upstream browser host contract says otherwise.

Current Windows implementation status: Windows node now advertises `browser.proxy` and forwards it to the local browser control host at `127.0.0.1:{gateway port + 2}`. It uses the gateway bearer token first and retries with the same shared secret as browser-host password/basic auth if bearer auth is rejected. Managed SSH tunnel mode also forwards the companion browser-control port (`local gateway port + 2` to `remote gateway port + 2`) when the browser proxy capability is enabled. Command Center still performs the read-only feasibility probe and warns when no compatible local browser host is listening, because the command depends on that local service being available.

## 7. Security and privacy requirements

1. Diagnostics must never take screenshots, record screen, capture camera, start microphone, or run arbitrary commands.
2. Support bundles must not include base64 payloads, tokens, screenshots, recordings, camera data, or command arguments.
3. Browser proxy must be local-only until we prove remote behavior is safe and intended.
4. Exec approval UI must include command display sanitization before adding "Allow Once/Always" UX.
5. Environment override parity should reject PATH and dangerous inherited/override keys.
6. Pairing approvals must show identity, platform, app, IP, and repair status before approval.
7. Allowlist repair should distinguish safe commands from privacy-sensitive commands. This is already in the Windows Command Center and should remain a product rule.

## 8. Implementation phases

### Phase 1: Topology model and gateway card

Files:

- `src/OpenClaw.Shared/Models.cs`
- `src/OpenClaw.Shared/SettingsData.cs` if optional declared kind is persisted
- `src/OpenClaw.Tray.WinUI/App.xaml.cs`
- `src/OpenClaw.Tray.WinUI/Services/SshTunnelService.cs`
- `src/OpenClaw.Tray.WinUI/Windows/StatusDetailWindow.xaml`
- `src/OpenClaw.Tray.WinUI/Windows/StatusDetailWindow.xaml.cs`
- `tests/OpenClaw.Shared.Tests/ModelsTests.cs`
- `tests/OpenClaw.Tray.Tests/SettingsRoundTripTests.cs` if settings change

Deliverables:

- `GatewayKind`, `TunnelStatus`, `GatewayTopologyInfo`, `TunnelCommandCenterInfo`.
- Pure topology classifier.
- Tunnel state/error/startedAt from `SshTunnelService`.
- Gateway card in Command Center.
- Topology/tunnel warnings.

Risk: low. No protocol changes.

### Phase 2: Better tunnel and WSL diagnostics

Deliverables:

- Mac-equivalent SSH options: **implemented for tunnel startup**
  - `BatchMode=yes`
  - `ExitOnForwardFailure=yes`
  - `ServerAliveInterval=15`
  - `ServerAliveCountMax=3`
  - `TCPKeepAlive=yes`
- Explicit tunnel states (`NotConfigured`, `Stopped`, `Starting`, `Up`, `Restarting`, `Failed`): **implemented**
- Fast-fail detection.
- Optional random local port fallback.
- WSL detection helper with timeout/cache. Explicit `wsl.localhost` / `.wsl` host classification is implemented.
- Tunnel reset action.

Risk: medium. Process lifecycle and port behavior need careful tests.

### Phase 3: Gateway self and presence model

Deliverables:

- Parse `hello-ok` snapshot/version/policy fields: **implemented**
- Parse/preserve presence events.
- Show gateway version, uptime/stateVersion, auth source, presence count: **implemented in Command Center gateway card**
- Add node/presence freshness warnings.

Risk: low-medium; mostly parsing and UI.

### Phase 4: Mac-like diagnostics and repair

Deliverables:

- Debug/Mission Control actions:
  - open log: **implemented as Open Logs folder**
  - open config folder: **implemented**
  - open session store
  - run health now: **implemented as Refresh Health**
  - send test heartbeat
  - reset managed SSH tunnel: **implemented as Restart SSH Tunnel when Settings owns the tunnel**
  - restart local gateway if topology is WindowsNative and managed
  - copy privacy-safe support context: **implemented**
- Rolling diagnostics JSONL with rotation: **implemented for privacy-safe app/connection/gateway/tunnel metadata**
- Port diagnostics table: **read-only local listener visibility implemented, including owning PID/process name when Windows exposes it**
- Manual SSH tunnel detection: **implemented Command Center classification for loopback gateway ports owned by `ssh`, so hand-started local forwards are not mislabeled as native Windows gateways**
- Gateway runtime owner summary: **implemented in Command Center topology/support context so local gateway or SSH-forward listener process name, PID, and port are visible without managing the process**
- Browser proxy SSH forward warning: **implemented targeted Command Center guidance when an SSH tunnel gateway is up but the companion `gateway port + 2` browser-control forward is missing**
- Browser proxy invoke error guidance: **implemented `browser.proxy` unreachable/timeout errors that name `127.0.0.1:{gateway+2}` and show the exact SSH local-forward shape**
- Settings SSH browser-forward guidance: **implemented Settings copy explaining that the managed SSH tunnel forwards `local-port+2` to `remote-port+2` for `browser.proxy` when the browser proxy bridge is enabled**
- Settings SSH test tunnel parity: **implemented temporary Settings test tunnels with the same optional browser-control `local+2` forward runtime uses when Browser proxy bridge is enabled**
- Settings SSH tunnel preview: **implemented selectable Settings preview of the exact managed `ssh -N -L ...` command, including the optional browser-control companion forward**
- Browser proxy disabled guidance: **implemented a specific Command Center warning/copy hint when `browser.proxy` is intentionally disabled in Settings**
- Asymmetric SSH browser guidance: **fixed Command Center and `browser.proxy` invoke guidance so local `gateway+2` and remote `gateway+2` can differ**
- SSH local browser-port source: **fixed Command Center browser diagnostics to derive the local browser-control port from the active tunnel local endpoint instead of stale saved gateway URLs**
- Browser-control host runtime smoke: **verified the upstream browser-control host can listen locally on `127.0.0.1:{gateway+2}`, return HTTP 200 from `/` and `/tabs`, and appear in Command Center port diagnostics with owning PID/process**
- Browser proxy auth guidance: **implemented warnings for QR/bootstrap-paired Windows nodes that advertise `browser.proxy` without a saved gateway shared token, and clarified invoke errors for missing versus mismatched browser-control auth**

Risk: medium-high for kill/restart actions; start as read-only/copy actions.

### Phase 5: Node command byte-for-byte parity audit fixes

Deliverables:

- Verify and align canvas/screen/camera/location/system payload defaults and error tokens.
- Verify push event names for exec.
- Add missing base-hash concurrency semantics if needed: **implemented for remote exec approval policy edits**
- Add `browser.proxy` feasibility prototype or explicit "not implemented" install guidance: **local browser-control bridge implemented; host runtime and Command Center listener detection smoke-tested; remaining end-to-end invoke blocker is matching operator/gateway auth for the active gateway**

Risk: varies; `browser.proxy` is medium-high.

### Phase 6: Security/privacy UX parity

Deliverables:

- Windows permission matrix with deep links:
  - camera
  - microphone
  - location
  - notifications
  - broad file system access if relevant
  - screen capture/graphics capture guidance
  - First read-only Command Center slice is implemented. It surfaces these settings pages and explanatory rows, but intentionally does not query, request, or exercise device permissions.
  - Capability diagnostics copy is implemented for declared commands, gateway allowlist status, and privacy-sensitive opt-ins.
- Mac-style onboarding security warning: **implemented in Setup Wizard Node Mode step, warning users that approved agents can run local commands and access enabled screen/camera/location/browser/canvas surfaces**
- Topology choice onboarding: **first Settings guide implemented with local, WSL, SSH tunnel, and remote/Tailscale presets**
- Exec approval dialog with sanitizer and three-button flow: **implemented for local `Prompt` policy decisions with Allow once / Always allow / Deny**
- Exec approval remote-policy hardening: **implemented guardrails so `system.execApprovals.set` cannot remotely switch to default allow, install broad/dangerous allow rules, or overwrite a newer local policy without a matching `baseHash`**
- Host env sanitizer parity hardening: **implemented expanded blocking for secret-looking overrides such as tokens, passwords, API keys, access keys, private keys, client secrets, and connection strings**
- Dangerous command opt-in guidance: **implemented copyable safety guidance for camera/screen privacy-sensitive commands without emitting one-click dangerous repair commands**
- Node capability settings: **implemented Settings toggles for canvas, screen, camera, location, and browser proxy command groups so privacy-sensitive surfaces can be disabled before reconnecting/re-pairing**
- Disabled capability diagnostics: **implemented Command Center distinction between intentionally disabled Settings groups and true gateway allowlist/parity gaps**
- Browser proxy policy diagnostics: **implemented a specific Command Center warning/copy action for declared `browser.proxy` commands filtered by gateway policy, instead of burying them under generic blocked-command output**

Risk: high for exec/security. Do not rush.

### Phase 7: Mission Control depth

Deliverables:

- Session previews with thinking/verbose controls.
- Cost 30-day bars: **implemented in Command Center from `usage.cost` daily totals**
- Node copy submenus / summaries: **implemented first Command Center copy action**
- Channel health summary and copyable context: **implemented first summary plus Command Center start/stop actions**
- Channel schema forms and QR login flows: **implemented first Windows surface with channel setup/dashboard deep links and copyable channel context**
- Skills/Cron settings: **implemented first Windows surface with Command Center dashboard entrypoints and copyable guidance**
- Agent events ring expansion: **implemented first Command Center recent-activity panel with copy/open-stream actions**
- Hover HUD / richer tray tooltip: **implemented with topology, channel, node, warning, and activity summary**
- Update status: **implemented in Command Center support/debug section and copied support context, including current version, latest prompted version when known, and last check outcome**

Risk: medium; mostly UI and gateway method plumbing.

### Phase 8: Optional local Windows gateway convenience

This is optional and should not block Mission Control.

Deliverables:

- Detect existing local Windows gateway.
- Attach to it and show logs/version/port.
- Only if user opts in: start/stop/restart a managed local gateway.

Risk: high. Requires Node/runtime/version/process ownership. Keep separate from topology-aware Command Center.

## 9. Test strategy

### Unit tests

- Topology classifier matrix:
  - localhost/no tunnel -> WindowsNative
  - localhost/tunnel -> MacOverSsh
  - `.ts.net` -> Tailscale
  - 100.64.0.0/10 -> Tailscale
  - 192.168/10/172.16/172.31 -> RemoteLan
  - `.local` -> RemoteLan
  - public host -> Remote
  - invalid/missing -> Unknown
- Tunnel info state mapping.
- Diagnostic sorting/dedupe with topology/tunnel warnings.
- Settings round-trip if new persisted fields are added.
- Existing capability and command-center tests stay green.

### Safe live tests

No screen recording, camera capture, or microphone.

1. Mac gateway over SSH tunnel:
   - Enable tunnel.
   - Expect Command Center topology: Mac over SSH.
   - Expect tunnel state: Up.
   - Health/channel events continue.
2. Localhost without tunnel:
   - Expect Windows native until WSL detection exists.
   - If no gateway, show clear connection warning.
3. Tailscale URL:
   - Use a synthetic settings profile or non-invasive connection check.
   - Expect topology classification only.
4. Remote LAN URL:
   - Expect Remote LAN classification.
5. Tunnel failure:
   - Stop only the known SSH process if started by the app.
   - Expect tunnel warning/restart state.
6. Allowlist regression:
   - Safe repair remains copyable.
   - Dangerous camera/screen commands remain informational.

### Required validation

After code changes:

```powershell
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

## 10. Open questions

1. Should `DeclaredGatewayKind` be a persisted user hint, or should detection remain purely derived?
2. Should Mac-over-SSH be named `SshTunnel` until presence confirms a Mac platform?
3. Should `browser.proxy` live in the tray, or should Command Center guide users to install/enable the browser plugin host?
4. Do we want a future "managed local gateway" mode, or only "detected local gateway"?
5. How much Tailscale integration should Windows own vs merely detect?
6. Should WSL detection use process/port probing, `wsl.exe`, or gateway presence fields once available?
7. Should support bundles include topology/tunnel diagnostics by default, and how should they redact host/user/IP? **Implemented for Command Center copy support context with redacted gateway URL, topology detail, tunnel endpoints/errors, and port details.**

## 11. Immediate recommendation

Implement Phase 1 now:

- Add topology/tunnel models and classifier.
- Surface them in Command Center.
- Add topology/tunnel warnings.
- Keep everything read-only and diagnostic.

This is the cleanest bridge between today's working Command Center and the Mac-style Mission Control product vision. It does not require a native Windows gateway, protocol changes, or privacy-sensitive live tests.

