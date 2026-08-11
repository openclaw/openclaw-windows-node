# 🏗️ Architecture: Windows Platform Strategy & Native Node Roadmap

> **📝 Note**: This document was written during the initial planning phase
> (early 2026). Windows Node mode has since been implemented with canvas,
> screen, camera, `system.run`, and notification capabilities. Treat roadmap
> prose and code sketches as historical context. Use the
> [Gateway, node, and exec flow FAQ](OPENCLAW_GATEWAY_NODE_EXEC_FAQ.md) for the
> current end-to-end execution and authority model, and README.md for the
> current capability list.

## Summary

OpenClaw has **excellent** macOS support - the native menubar app runs as a full node with camera, canvas, screen capture, notifications, location, system exec, and more. Windows users today rely on **WSL2** for the gateway and get a limited experience: no native UI integration, no camera, no canvas surface, and NAT networking quirks.

This issue proposes a comprehensive Windows platform strategy that evolves `OpenClaw.Tray.WinUI` from a gateway *client* into a **native Windows node** - giving the agent eyes, hands, and a voice on Windows, and eventually exploring a fully native Windows gateway.

**This is the umbrella issue for the Windows platform story.** It maps every deployment scenario, identifies capability gaps, proposes a phased roadmap, and provides enough technical detail for contributors to pick up work items.

Related issues: #5 (Canvas Panel), #6 (Skills Settings UI), #7 (DEVELOPMENT.md), #9 (WebView2 ARM64)

---

## Table of Contents

- [Current State](#current-state)
- [The Vision](#the-vision-now-implemented-for-the-windows-node)
- [Deployment Scenario Matrix](#deployment-scenario-matrix)
- [Capability Matrix by Node Type](#capability-matrix-by-node-type)
- [Node Protocol Overview](#node-protocol-overview)
- [Windows API Mapping](#windows-api-mapping)
- [Architectural Questions](#architectural-questions)
- [Phased Roadmap](#phased-roadmap)
- [Technical Deep Dives](#technical-deep-dives)
- [Contributing](#contributing)

---

## Current State

### What exists today

| Component | Status | Details |
|-----------|--------|---------|
| `OpenClaw.Shared` | ✅ Working | Gateway WebSocket client library (.NET) |
| `OpenClaw.Tray.WinUI` | ✅ Working | System tray app - status, Quick Send, WebChat (WebView2), toast notifications, channel control |
| Windows Node | ✅ Implemented | Canvas, screen, camera, location, device info/status, system.run, notifications - all working via Node Mode |
| Windows Gateway | ❌ Unexplored | Gateway runs in WSL2 only |

### Historical setup that motivated the Windows node

Before the native Windows node shipped, the Windows PC commonly used two
connections to a Mac gateway: a headless WSL2 node for exec and the tray app as
an operator client. In that historical setup the agent could not:
- Show a canvas on Windows
- Take screenshots of the Windows desktop
- Capture from a Windows webcam
- Send native Windows notifications (from the agent, vs. from the tray app's event listener)
- Get the Windows machine's location

---

## The Vision, now implemented for the Windows node

The tray app is now a first-class OpenClaw node that registers with
`role: "node"` and advertises Windows-native capabilities. WSL2 is not required
for the node; it is used only when the Windows setup owns a local WSL gateway.

For the current topology and authority boundaries, use the canonical
[topology diagram](diagrams/openclaw-topologies-and-authority.svg) and its
[editable Excalidraw source](diagrams/openclaw-topologies-and-authority.excalidraw).

---

## Deployment Scenario Matrix

### Scenario 1: Mac Only ⭐⭐⭐⭐⭐

| Aspect | Details |
|--------|---------|
| **Gateway** | macOS native (Node.js) |
| **Nodes** | macOS native app (full capabilities) |
| **Capabilities** | Camera ✅ Canvas ✅ Screen ✅ Notifications ✅ Browser ✅ Exec ✅ Location ✅ Audio/TTS ✅ Accessibility ✅ AppleScript ✅ |
| **Networking** | Loopback, zero config |
| **Setup complexity** | `openclaw onboard --install-daemon` → done |
| **UX Rating** | ⭐⭐⭐⭐⭐ Best possible experience |

The gold standard. Everything works out of the box. This is what Windows should feel like.

---

### Scenario 2: Windows Only - WSL2 Gateway + WSL2 Node ⭐⭐

| Aspect | Details |
|--------|---------|
| **Gateway** | WSL2 (Ubuntu) |
| **Nodes** | WSL2 headless node (exec only) |
| **Capabilities** | Camera ❌ Canvas ❌ Screen ❌ Notifications ❌ Browser Proxy ✅ Exec ✅ Location ❌ Audio/TTS ❌ |
| **Networking** | WSL2 NAT - `localhost` works but external access needs `--bind` + firewall rules. HTTPS can be tricky with self-signed certs. |
| **Setup complexity** | Install WSL2 → install Node.js → install openclaw → configure networking → hope NAT cooperates |
| **UX Rating** | ⭐⭐ Functional but headless. The agent is blind. |

**Pain points:**
- WSL2's NAT means `127.0.0.1` inside WSL ≠ `127.0.0.1` on Windows
- No way to interact with the Windows desktop
- Browser proxy works but can't see what the user sees
- Every WSL2 restart may change the internal IP

---

### Scenario 3: Windows Only - WSL2 Gateway + Tray App as Client ⭐⭐⭐

| Aspect | Details |
|--------|---------|
| **Gateway** | WSL2 (Ubuntu) |
| **Nodes** | None registered as node - tray app is operator-only |
| **Capabilities** | Camera ❌ Canvas ❌ (WebChat only) Screen ❌ Notifications ⚠️ (tray-side only, not agent-driven) Browser ❌ Exec ✅ (WSL2) Location ❌ Audio/TTS ❌ |
| **Networking** | WSL2 → Windows: `localhost:18789` usually works. Windows → WSL2: same. But HTTPS cert validation can fail for WebView2 connecting to WSL2's self-signed cert. |
| **Setup complexity** | Medium - WSL2 + openclaw + configure tray app to point at `ws://localhost:18789` |
| **UX Rating** | ⭐⭐⭐ Nice UI wrapper but agent still can't see or interact with Windows |

This operator-only mode provides Quick Send, embedded WebChat, Command Center diagnostics, activity stream, and status display. But without Node Mode it is still a viewport into the agent, not a bridge for the agent to interact with Windows.

---

### Scenario 4: Windows Only - WSL2 Gateway + Tray App as Native Node ⭐⭐⭐⭐

| Aspect | Details |
|--------|---------|
| **Gateway** | WSL2 (Ubuntu) |
| **Nodes** | OpenClaw.Tray registers as `role: "node"` from Windows |
| **Capabilities** | Camera ✅ (MediaCapture API) Canvas ✅ (WebView2) Screen ✅ (Graphics Capture) Notifications ✅ (Toast + agent-driven) Browser ✅/⚠️ (local `browser.proxy` bridge; requires browser-control host on gateway port + 2) Exec ✅ (WSL2 + optionally Windows `cmd`/`powershell`) Location ⚠️ (Windows Location API - desktop, less useful) Voice/TTS ⚠️ (separate parity track) |
| **Networking** | WSL2 NAT still involved for gateway, but tray app connects outward to WSL2's WS - simpler direction. |
| **Setup complexity** | Medium - WSL2 gateway + tray app auto-discovers and pairs |
| **UX Rating** | ⭐⭐⭐⭐ Agent can now see and interact with Windows! |

**This is the sweet spot for Phase 1.** The gateway stays in WSL2 (proven, works), but the tray app lights up all the Windows-native capabilities. The agent gains eyes and hands on Windows.

The tray now also has a Command Center surface that combines gateway channel health, sessions, usage/cost, node inventory, pairing state, command allowlist diagnostics, and recent invoke activity. It is read-only by default and does not invoke camera or screen commands while diagnosing capability health.

---

### Scenario 5: Windows Native Gateway + Tray App as Node ⭐⭐⭐⭐⭐

| Aspect | Details |
|--------|---------|
| **Gateway** | Windows native (Node.js on Windows - `node.exe`) |
| **Nodes** | OpenClaw.Tray as full Windows node |
| **Capabilities** | Camera ✅ Canvas ✅ Screen ✅ Notifications ✅ Browser ✅/⚠️ (`browser.proxy` bridge; needs browser-control host on gateway+2) Exec ✅ (native `cmd.exe`, PowerShell, `wsl.exe`) Location ⚠️ Voice/TTS ⚠️ (separate parity track) |
| **Networking** | `ws://127.0.0.1:18789` - pure loopback, no NAT, no WSL2 networking issues |
| **Setup complexity** | Low - `npm install -g openclaw && openclaw onboard` from PowerShell. Same as Mac. |
| **UX Rating** | ⭐⭐⭐⭐⭐ True feature parity with Mac |

**The dream.** No WSL2 dependency at all. The gateway runs natively on Windows (Node.js works fine on Windows), and the tray app provides all native capabilities. This is the Mac experience, on Windows.

**Key question:** Does the OpenClaw gateway actually *work* on Windows? It's Node.js, so *in theory* yes. But there may be Unix-specific assumptions (signals, file paths, spawning, etc.) that need auditing. See [Architectural Questions](#architectural-questions).

---

### Scenario 6: Mac Gateway + Windows WSL2 Node (Current Multi-Machine) ⭐⭐⭐⭐

| Aspect | Details |
|--------|---------|
| **Gateway** | macOS (local Mac) |
| **Nodes** | macOS native + WSL2 headless node on Windows |
| **Capabilities** | Full Mac capabilities + Windows exec via WSL2 node |
| **Networking** | Tailnet or SSH tunnel between machines. Reliable but requires network setup. |
| **Setup complexity** | Medium - two machines, tailnet/SSH, node pairing |
| **UX Rating** | ⭐⭐⭐⭐ Great for multi-machine setups where Mac is primary |

**Today's power-user setup.** Works well for "Mac as brain, Windows as build server" use cases. Adding tray-app-as-node would make this ⭐⭐⭐⭐⭐.

---

### Scenario 7: Mac Gateway + Tray App as Windows Node ⭐⭐⭐⭐⭐ (with Node)

| Aspect | Details |
|--------|---------|
| **Gateway** | macOS |
| **Nodes** | macOS native + Windows native (tray app) |
| **Capabilities** | Everything from Mac + camera, canvas, screen, notifications on Windows |
| **Networking** | Tailnet/LAN between Mac gateway and Windows tray app |
| **Setup complexity** | Medium - network between machines, but tray app handles pairing |
| **UX Rating** | ⭐⭐⭐⭐⭐ Best of both worlds for multi-machine |

The agent can see both the Mac and Windows desktops, capture from either machine's camera, show canvas on both screens. Multi-machine nirvana.

---

### Scenario 8: WSL2 Gateway + Mac Node ⭐⭐⭐½

| Aspect | Details |
|--------|---------|
| **Gateway** | WSL2 on Windows |
| **Nodes** | macOS native app connecting to Windows WSL2 gateway |
| **Capabilities** | Full Mac node capabilities, but gateway is in WSL2 |
| **Networking** | WSL2 must bind non-loopback (`--bind 0.0.0.0` or tailnet). Mac connects to Windows IP. |
| **Setup complexity** | High - WSL2 networking config + cross-machine pairing |
| **UX Rating** | ⭐⭐⭐½ Unusual topology but works. Why not put gateway on Mac? |

Niche scenario. If the "server" must be Windows for some reason, this works but Mac-gateway-with-Windows-node is almost always better.

---

### Summary Table

| # | Scenario | Gateway | Node(s) | Capabilities | Complexity | Rating |
|---|----------|---------|---------|-------------|------------|--------|
| 1 | Mac only | macOS | macOS app | Full | Low | ⭐⭐⭐⭐⭐ |
| 2 | Win WSL2 only | WSL2 | WSL2 headless | Exec only | High | ⭐⭐ |
| 3 | Win WSL2 + tray client | WSL2 | None (operator) | Exec + UI | Medium | ⭐⭐⭐ |
| 4 | **Win WSL2 + tray node** | WSL2 | **Tray app (node)** | **Most** | **Medium** | **⭐⭐⭐⭐** |
| 5 | **Win native gateway + tray node** | **Windows** | **Tray app (node)** | **Full** | **Low** | **⭐⭐⭐⭐⭐** |
| 6 | Mac gw + WSL2 node | macOS | macOS + WSL2 | Mac full + Win exec | Medium | ⭐⭐⭐⭐ |
| 7 | **Mac gw + tray node** | macOS | macOS + **Tray app** | **Full both** | Medium | **⭐⭐⭐⭐⭐** |
| 8 | WSL2 gw + Mac node | WSL2 | macOS app | Mac full | High | ⭐⭐⭐½ |

**Bold = new scenarios this issue enables.**

---

## Capability Matrix by Node Type

| Capability | macOS App | iOS App | Android App | WSL2 Headless | **Windows Tray** | Windows API |
|-----------|-----------|---------|-------------|---------------|---------------------------|-------------|
| `canvas.present` | ✅ SwiftUI WebView | ✅ WKWebView | ✅ WebView | ❌ | **✅ WebView2** | WebView2 |
| `canvas.snapshot` | ✅ | ✅ | ✅ | ❌ | **✅** | WebView2 CapturePreviewAsync |
| `canvas.eval` | ✅ | ✅ | ✅ | ❌ | **✅** | WebView2 ExecuteScriptAsync |
| `canvas.a2ui.push/reset` | ✅ | ✅ | ✅ | ❌ | **✅** | WebView2 |
| `canvas.a2ui.pushJSONL` | ✅ | ✅ | ✅ | ❌ | **✅** | Legacy alias over A2UI push |
| `camera.snap` | ✅ AVFoundation | ✅ AVFoundation | ✅ CameraX | ❌ | **✅** | MediaCapture + frame reader fallback |
| `camera.clip` | ✅ | ✅ | ✅ | ❌ | **✅** | MediaCapture + MediaEncoding |
| `camera.list` | ✅ | ✅ | ✅ | ❌ | **✅** | DeviceInformation.FindAllAsync |
| `screen.record` | ✅ CGWindowListCreateImage | ✅ ReplayKit | ✅ MediaProjection | ❌ | **✅** | Windows.Graphics.Capture |
| `system.run` | ✅ | ❌ | ❌ | ✅ | **✅** | Process.Start + V2 exec-approval coordinator |
| `system.execApprovals` | ❌ | ❌ | ❌ | ❌ | **✅** | V2 store (`exec-approvals.json`) with base-hash CAS |
| `system.notify` | ✅ NSUserNotification | ✅ UNUserNotification | ✅ NotificationManager | ❌ | **✅** | ToastNotificationManager |
| `location.get` | ✅ CLLocationManager | ✅ CLLocationManager | ✅ FusedLocation | ❌ | **✅** | Windows.Devices.Geolocation |
| `device.info/status` | ✅ shared schema | ✅ shared schema | ✅ shared schema | ❌ | **✅** | .NET runtime, storage, network |
| `sms.send` | ❌ | ❌ | ✅ | ❌ | ❌ | N/A |
| Browser proxy | ✅ | ❌ | ❌ | ✅ Playwright | **✅/⚠️ Local bridge** | Browser-control host on gateway port + 2 |
| Accessibility | ✅ AX API | ❌ | ❌ | ❌ | **⚠️ Future** | UI Automation |
| Speech/TTS | ✅ NSSpeechSynthesizer | ❌ | ❌ | ❌ | **⚠️ Planned** | Windows.Media.SpeechSynthesis |
| Microphone | ✅ AVAudioEngine | ✅ | ✅ | ❌ | **⚠️ Future** | Windows.Media.Audio |

---

## Node Protocol Overview

For contributors: here's what implementing a Windows node means at the protocol level.

### 1. Connect as a node

The tray app uses a dedicated node connection (`WindowsNodeClient`) with `role: "node"`:

```json
{
  "type": "req",
  "id": "connect-1",
  "method": "connect",
  "params": {
    "minProtocol": 3,
    "maxProtocol": 3,
    "client": {
      "id": "windows-tray",
      "version": "1.0.0",
      "platform": "windows",
      "mode": "node"
    },
    "role": "node",
    "scopes": [],
    "caps": ["canvas", "camera", "screen", "notifications", "system", "device", "browser"],
    "commands": [
      "canvas.present", "canvas.hide", "canvas.navigate",
      "canvas.eval", "canvas.snapshot", "canvas.a2ui.push",
      "canvas.a2ui.pushJSONL", "canvas.a2ui.reset",
      "camera.list", "camera.snap", "camera.clip",
      "screen.snapshot", "screen.record",
      "location.get",
      "device.info", "device.status",
      "system.run", "system.run.prepare", "system.which", "system.notify",
      "system.execApprovals.get", "system.execApprovals.set",
      "browser.proxy"
    ],
    "permissions": {
      "camera.capture": true,
      "screen.record": true
    },
    "auth": { "token": "..." },
    "device": {
      "id": "windows-machine-fingerprint",
      "publicKey": "...",
      "signature": "...",
      "signedAt": 1706745600000,
      "nonce": "..."
    }
  }
}
```

### 2. Handle `node.invoke` requests

The gateway sends commands via `node.invoke`:

```json
{
  "type": "req",
  "id": "invoke-42",
  "method": "node.invoke",
  "params": {
    "command": "canvas.snapshot",
    "args": { "format": "png", "maxWidth": 1200 }
  }
}
```

The tray app responds:

```json
{
  "type": "res",
  "id": "invoke-42",
  "ok": true,
  "payload": {
    "format": "png",
    "base64": "iVBORw0KGgo..."
  }
}
```

### 3. Dual-role connection

The tray app could connect **twice** (operator + node) or the protocol may support a **dual-role** connection. Operator gives Quick Send / status / WebChat. Node gives the agent capabilities. Both over the same WebSocket.

**Investigation needed:** Can a single WS connection carry both roles, or does it need two connections?

---

## Windows API Mapping

### Canvas → WebView2

The tray app *already has WebView2* for WebChat (#5 is the Canvas Panel issue). The same control can serve as the node canvas surface.

```csharp
// canvas.present - navigate WebView2 to a URL
await webView.CoreWebView2.Navigate(url);

// canvas.eval - execute JavaScript
string result = await webView.CoreWebView2.ExecuteScriptAsync(js);

// canvas.snapshot - capture the WebView2 content
using var stream = new InMemoryRandomAccessStream();
await webView.CoreWebView2.CapturePreviewAsync(
    CoreWebView2CapturePreviewImageFormat.Png, stream);
byte[] bytes = new byte[stream.Size];
await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
return Convert.ToBase64String(bytes);
```

**Blocker:** #9 - WebView2 fails to initialize on ARM64 in WinUI 3 unpackaged mode. This needs resolution first.

### Camera → Windows.Media.Capture / MediaFoundation

```csharp
// camera.list
var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

// camera.snap
var capture = new MediaCapture();
await capture.InitializeAsync(new MediaCaptureInitializationSettings {
    VideoDeviceId = deviceId,
    StreamingCaptureMode = StreamingCaptureMode.Video
});
var photo = await capture.CapturePhotoToStreamAsync(
    ImageEncodingProperties.CreateJpeg(), stream);
```

For WinUI 3 / .NET, the [Windows.Media.Capture](https://learn.microsoft.com/en-us/uwp/api/windows.media.capture) namespace is available. Alternatively, `MediaFoundation` via COM interop gives more control.

### Screen Capture → Windows.Graphics.Capture

The [Graphics Capture API](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture) (Windows 10 1803+) provides screen recording:

```csharp
// screen.record
var picker = new GraphicsCapturePicker();
var item = await picker.CreateForMonitorAsync(monitorHandle);
// Or capture programmatically without picker (requires capability declaration)

var framePool = Direct3D11CaptureFramePool.Create(device, pixelFormat, 2, size);
var session = framePool.CreateCaptureSession(item);
session.StartCapture();
```

**Note:** Programmatic capture (without the user picker) requires the `graphicsCapture` restricted capability or using `CreateForMonitorAsync`. On Windows 11+, `GraphicsCaptureAccess.RequestAccessAsync` enables background capture.

### Notifications → ToastNotificationManager

```csharp
// system.notify - agent-driven notifications
var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
var textNodes = xml.GetElementsByTagName("text");
textNodes[0].InnerText = title;
textNodes[1].InnerText = body;

var toast = new ToastNotification(xml);
ToastNotificationManager.CreateToastNotifier("OpenClaw.Tray").Show(toast);
```

The tray app *already does* toast notifications from gateway events. The change is to also handle `system.notify` commands from the node protocol so the agent can *request* a notification.

### System Exec: canonical argv, local approval, and MXC

The earlier PowerShell-only sketch is no longer the implementation. The current
path is:

```text
node.invoke system.run
  -> local Run system tools gate
  -> Windows V2 exec approval
  -> resolved absolute executable + canonical argv
  -> MXC AppContainer, strict deny, or approved host fallback
  -> Process.Start with UseShellExecute=false
```

The normal upstream `exec host=node` path is still shell-oriented. The gateway
builds platform argv such as `cmd.exe /d /s /c <command>` for Windows before it
calls `system.run`. It calls `system.run.prepare` first only when gateway policy
requires approval or strict inline-eval review. A low-level caller can instead
send direct argv. In that case, Windows resolves `argv[0]` to an absolute
executable and passes arguments through `ProcessStartInfo.ArgumentList` without
adding another shell.

Exec approvals are enforced locally, as they are for macOS and headless nodes.
The default store is `%APPDATA%\OpenClawTray\exec-approvals.json`;
`OPENCLAW_STATE_DIR` overrides that location. See the
[exec flow FAQ](OPENCLAW_GATEWAY_NODE_EXEC_FAQ.md#what-exactly-happens-inside-the-windows-node-for-systemrun)
for gateway-owned approval, node-local approval, and sandbox ordering.

#### Decision: retire Windows V1 exec policy without migration

Windows V1 `exec-policy.json` command-text globs are not evaluated or converted after the
V2 cutover. When no valid V2 policy exists, the node uses an empty allowlist with
prompt-on-miss and deny fallback.

**Consequences:**

- Existing V1 files remain untouched during normal runtime but no longer authorize or deny execution.
- Prior V1 allows require attended V2 reapproval; unattended calls deny.
- Prior V1 denies are not imported and may be explicitly superseded through an attended V2 prompt.
- Malformed or untrusted V2 state remains hard deny and is never replaced from V1 state.

This compatibility break is accepted because V1 matched shell command text while V2 binds
resolved executable identity and canonical argv. Mechanical conversion could widen a narrow
command rule into a reusable `cmd.exe` or PowerShell grant, while retaining both evaluators
would preserve parallel authorization paths and bypass drift.

**Rejected alternatives:** V1 runtime fallback, mechanical rule conversion, and file-triggered
migration UI. Revisit this decision only if measured support impact justifies a separate
compatibility feature.

#### Decision: version the low-level `system.run` boundary as canonical argv

The V2 low-level node contract accepts `command` only as `string[]` canonical argv.
String-form `command`, implicit `shell`, separate `args`, and non-empty custom `env`
are intentional breaking changes. The normal gateway `exec host=node` flow already
constructs canonical Windows argv, so this affects raw MCP, direct `node.invoke`,
plugins, and other callers that bypass gateway exec orchestration.

Migration example:

```json
// Before
{"command":"echo hello","shell":"cmd"}

// V2
{"command":["cmd.exe","/d","/s","/c","echo hello"],"rawCommand":"echo hello"}
```

The node returns `command-array-required` for a string command and
`custom-env-not-supported` for a non-empty environment. This explicit boundary
keeps approval identity and process execution on one argv representation.

`"version": 1` inside `exec-approvals.json` is the persisted file schema
version. It is not the retired Windows V1 `exec-policy.json` evaluator.

#### Decision: bind reusable gateway commands to direct argv

The gateway represents Windows shell text as
`["cmd.exe", "/d", "/s", "/c", "<command>"]` with one pre-joined command element.
Low-level callers and upstream approval fixtures may instead supply a
reconstructible tokenized tail. The carrier executable may be the bare name or
the fully qualified system `cmd.exe` path; an arbitrary file merely named
`cmd.exe` is never a transparent durable-approval carrier. A bare name is only a
token check, so durable binding additionally resolves the carrier through
`ResolveTrustedCarrierPath` and requires a real image in a Windows system
directory, then pins that absolute path into the executed argv so the loader
cannot re-resolve it at launch.
`CanonicalCmdCarrier` is the single owner of that recognition, shared by the
approvals binder and the MXC command-line builder so the layer that authorizes a
shape and the layer that runs it cannot disagree. A multi-element tail is only
accepted when every element is free of whitespace and quotes, because otherwise
the original process-creation quoting is not recoverable by a space join.

V2 may persist or consume an
allowlist rule only when that carrier contains one statically bindable external
command. The binder accepts an intentionally small grammar: unquoted literal
tokens separated by whitespace. It rejects quoting, pipelines, command chains,
redirection, expansion, caret escapes, grouping, CMD built-ins, unresolved or
nonexistent executables, and wrapper/interpreter targets. Durable binding is
further restricted to native `.exe` images, which CreateProcess runs
directly: PATH resolution probes every `PATHEXT` entry, so a bare name can
otherwise resolve to `.bat`, `.cmd`, `.com`, `.vbs`, `.js`, `.wsf`, or `.msc`
content whose meaning is delegated to an interpreter without any change to the
approved path. The allowlist is `.exe` only on purpose: adding another image
format to durable authorization is a separate decision with its own review, not
a detail of carrier binding. A `.com` target is still runnable, it is simply
prompt-only, which is the fail-closed side of that decision.

For a successful binding, one immutable reusable command supplies the resolved
path used for matching, the persisted pattern, usage metadata, and the direct
argv executed by the runner. The original CMD wrapper remains the approved
execution only for an attended Allow once decision or locally selected full
policy. This prevents the former split where an inner executable suppressed the
prompt but the outer `cmd.exe` identity was then rejected.

Permissions **Ask** maps to prompt-on-miss: a reusable allowlist match runs
without prompting. A manually configured literal `ask: "always"` still prompts
every time. Allow always is offered only for a reusable command under allowlist
security and persists that command's resolved executable path.

When nothing binds, the prompt still shows the operator a resolved executable
path, falling back to the carrier's own resolution. An approval dialog must never
ask for a decision with no resolved path displayed.

Durable identity is the executable **plus an argument pattern**, matching macOS and
the shared protocol. A generated rule persists `path`, `argPattern`, `commandText`,
and `source`, so approving `where.exe hostname.exe` authorizes exactly that
argument form and nothing else. This is what makes an explicit code-host catalog
unnecessary: an argument-selected host such as `mshta.exe` or `rundll32.exe` cannot
be blanket-approved, because the persisted rule is pinned to the arguments the
operator actually saw.

`argPattern` is written in the platform form the gateway expects. On Windows each
argument is separator-normalized and the pattern is the anchored, NUL-joined
regular expression `^escaped(join("\0"))\0$`; zero arguments serialize as `^\0\0$`.
The matcher selects its separator by testing whether the pattern contains a NUL,
so both the Windows and the hashed non-Windows form remain readable. Matching runs
against the full argv including argv[0].

Authorization consequences follow upstream `matchAllowlist` exactly:

- A **generated** entry with no `argPattern` never matches. It is skipped rather
  than widened to a path-only grant, so a truncated or hand-edited rule fails
  closed. Any non-empty `source` counts as generated, not just the exact marker
  this node writes, so a differently cased, padded, or foreign marker cannot fall
  through and widen the rule. Provenance is absent only when `source` is empty or
  whitespace.
- A **path-only** entry authorizes its executable regardless of arguments. That is
  the operator writing a deliberately broad rule by hand, and it is honored as
  written, with one carve-out described under "Legacy quarantine" below. Path-only
  matches are deferred so a precisely bound rule always wins.
- Normalization preserves `Source` and `ArgPattern` on rewrite. Dropping either
  would silently convert a narrow generated rule into a broad path-only one.

##### Legacy quarantine for provenance-less command-host rules

An entry with **no `source` and no `argPattern`** predates argument binding, and we
cannot tell a deliberate operator rule from one written when this node still refused
interpreters durable approval by name. For an ordinary program that ambiguity is
harmless and the entry keeps working. For a program the previous model refused
outright (`python`, `cmd`, `powershell`, `pwsh`, `wsl`, `node`, `cscript`, the
indirect execution hosts such as `mshta`, `rundll32`, `regsvr32`, `msbuild` and
`certutil`, and versioned interpreters such as `python3.12`) it is not: honoring it
would convert a case that used to be denied into an unconditional allow, purely as a
side effect of changing the model. Those entries go **inert**. The command falls
through to a prompt.

The quarantined set is a verbatim copy of the catalog as it stood immediately before
argument binding replaced it, because the question it answers is purely historical:
would this exact entry have been refused when it was written? It must not be curated,
pruned, or extended, and its matching rules are fixed for the same reason: it compares
a basename with only a `.exe` suffix stripped, exactly as the original did.

The entry is not deleted and not migrated. The only way to make such a host reusable
is an explicit Allow always, which writes an argument-bound sibling carrying `source`
and `argPattern`; that sibling then matches its own invocation and nothing else.

This name list is a compatibility measure for records already on disk. It is not the
security boundary and must not be used as one. The boundary is that every rule this
node generates pins its arguments.

##### Trusted carrier: approval identity is separate from execution transport

When the payload inside a strictly trusted canonical `cmd` carrier binds, the node
authorizes the **inner** executable and argument pattern, and executes a carrier
reconstructed from the validated request. The carrier is not incidental: under MXC it
transports the PATH and TEMP bootstrap in band, because MXC 0.7 rejects a
non-empty `process.env`. Substituting the bound direct argv would authorize
correctly and then run in an environment the command was never prepared for.

The reconstructed carrier differs from the request in exactly two places, and both
of them remove a resolution that would otherwise happen at launch instead of at
approval:

- **argv[0]** is pinned to the resolved `System32` or `SysWOW64` `cmd.exe`. A
  relative `cmd.exe` would let Windows re-resolve the image against PATH and the
  working directory at spawn time.
- **The payload's executable token** is pinned to the binder-resolved absolute path.
  `cmd.exe` resolves that token itself, at launch, searching the working directory
  **before** PATH, while the binder's resolver searches PATH only. Leaving it
  unpinned means one resolver authorizes the command and a different one picks what
  runs, and anything able to write to the working directory in between decides the
  outcome. A pinned absolute path leaves `cmd` nothing to search for.

Everything else, including interior spacing, is byte-preserved, so the executed
carrier reconstructs the approved `rawCommand` exactly and no metacharacter can be
introduced after approval. The rewrite is built from the payload's parsed token
spans, never by string replacement, so an argument that repeats the executable's
text is untouched. The tail's arity is preserved as well, so a pre-joined tail stays
one element and a tokenized tail keeps its elements and no new process-creation
quoting is introduced.

Both pins apply to **every** approved run of a recognized canonical carrier: a durable
Allow Always, a one-time Allow Once, an allowlist hit that never prompts, and a
pre-approved `security=full` run. Approval identity and durability are separate from
execution transport. The prompt names the inner executable the binder resolved through
a trusted system `cmd.exe`, so executing the request's own argv instead would reopen
both launch-time lookups after the decision had been made, and a `cmd.exe` planted
earlier on PATH than the system directory would run in place of the image that was
shown. Choosing this transport persists nothing; durability is gated separately on an
Allow Always decision with a bound reusable command.

The same rule covers a directly invoked executable, which is resolved twice: once by
the normalizer for the execution identity and once by the binder for the identity that
is displayed and stored. Execution uses the binder's resolution, so the two lookups can
never disagree about which image the operator approved.

Pinning is refused rather than approximated. The pinned path must be writable into
the payload as a single token that `cmd` reads back byte for byte: no whitespace, no
quote, none of `% ! ^ & | < > ( )`, none of `, ; =` (which end `cmd`'s command-name
token even though our own tokenizer does not model them), no control characters, and
no trailing backslash. Whitespace is refused rather than quoted, because under `/s`
`cmd` strips the first and last quote of the payload and uses the remainder verbatim,
so quoting a spaced path removes the quotes again and leaves it ambiguous. After
reconstruction the result is re-parsed and compared against the original: same
argument count, the executable equal to the pinned path, every other argument
ordinal-identical, and the whole carrier still recognized as the canonical shape.
Anything that fails is not bound and stays prompt-only
(`carrier-payload-not-pinnable`). The same equivalence is re-checked at execution
time rather than trusted from bind time.

Trust is deliberately narrow. A carrier is trusted only when argv[0] is the bare
name `cmd`/`cmd.exe` or a fully qualified path under `System32`/`SysWOW64`. A
renamed or relocated image named `cmd.exe` is refused for durable binding and
falls back to one-time or prompt handling, so an attacker-supplied binary cannot
be looked through. Non-canonical carriers stay one-time and report an explicit
diagnostic (`carrier-payload-not-static`) rather than silently failing to bind.

Pinning supersedes the earlier approval-time working-directory ambiguity check. That
check could only observe the directory as it was when approval was granted, so a
writable working directory could gain a shadowing file before launch and win anyway.
It has been deleted rather than kept as a diagnostic, and must not be restored as an
authorization boundary.

##### Behavior changes from the previous executable-path-only binding

- A stored rule naming an interpreter or code host (for example `**/wsl.exe`)
  previously produced a hard `persistent-approval-not-permitted-for-command-host`
  refusal. A hand-written path-only rule now authorizes that executable, matching
  upstream, **except** for entries that carry neither `source` nor `argPattern`,
  which stay inert for those hosts (see "Legacy quarantine" above). Generated rules
  still pin arguments, so a broad grant cannot happen by accident through the Allow
  always UX.
- A separator-bearing path to a nonexistent file is now rejected at bind time.
  Binding requires the resolved executable to exist.
- UNC and other network paths are refused for durable approval. Their contents are
  remotely mutable at a stable path, so a persisted rule would be a standing grant
  over content the node does not control.
- Integrity binding is by resolved path only, with no content hash, inode, or
  signature check. This matches macOS `lastResolvedPath` behavior, and leaves the
  same time-of-check to time-of-use exposure between approval and launch.
- An argument containing NUL is refused. NUL separates arguments inside a stored
  `argPattern`, so `"a\0b"` would render identically to the two arguments
  `"a"`, `"b"` and let a stored rule match a differently segmented command. It is
  not representable in a Windows command line either, so refusing costs nothing.
- A remote `system.execApprovals.set` may retain existing allowlist entries but
  not alter them. Retention compares the whole authorization identity (pattern,
  `argPattern`, and `source`) field by field. Comparing paths alone would let a
  caller keep the executable while dropping the binding, silently widening a
  generated rule into a path-only grant.

##### Closed: working-directory substitution for bare carrier payloads

Previously, for a bare (unqualified) payload name inside a trusted carrier, the
executable the node authorized and the executable `cmd.exe` launched were resolved at
different times by different code, and a writable working directory could gain a
shadowing file between approval and launch.

This is closed by pinning the payload's executable token to its resolved absolute
path inside the reconstructed carrier, described above. `cmd` no longer performs a
second resolution, so there is no window to win. Cases where the resolved path cannot
be represented safely in the payload are refused durable binding rather than
transported unpinned.

Time-of-check to time-of-use on the *contents* of the resolved path remains, and is
unchanged: integrity binding is by path only, matching macOS `lastResolvedPath`.

### Location → Windows.Devices.Geolocation

```csharp
var geolocator = new Geolocator {
    DesiredAccuracy = PositionAccuracy.High
};
var position = await geolocator.GetGeopositionAsync();
// position.Coordinate.Point.Position.Latitude / .Longitude
```

**Note:** Desktop PCs usually have poor location accuracy (IP-based). Laptops with WiFi can do better. This is a "nice to have" - lower priority than camera/canvas/screen.

### TTS → Windows.Media.SpeechSynthesis

```csharp
var synth = new SpeechSynthesizer();
var stream = await synth.SynthesizeTextToStreamAsync(text);
// Play via MediaElement or save to file
```

This is a candidate implementation path, not an implemented node command yet. Voice/Talk mode parity should stay on its own track so Windows does not advertise a speech capability before there is a shared command contract and permission model.

Current PR review status: open PR #120 (`feature/voice-mode`) is a useful prototype but should not merge as-is. It currently conflicts with the active capability-settings branch, advertises `voice.*` commands without the default-off Settings gate used for other privacy-sensitive capability groups, widens operator scopes in the same PR, persists cloud TTS provider keys in plain settings JSON, and introduces a Windows-specific wire schema before the Mac runtime/controller/session contract is agreed. Safe next step: split schema, gateway scope, chat transport, Windows runtime, WebChat integration, and cloud-provider credentials into separate reviews; keep the first merge behind a default-off Voice Settings group and gateway dangerous-command allowlist.

---

## Architectural Questions

### 1. Should the tray app be a dual-role connection (operator + node)?

**Recommendation: Yes, dual-role.**

The tray app already maintains a WebSocket connection as an operator. It should *also* register as a node on the same or a second connection. This means:

- **Option A:** Single WS, dual role - connect once with `role: ["operator", "node"]` (if protocol supports it)
- **Option B:** Two WS connections - one operator (existing), one node (new)
- **Option C:** Node-only, deprecate operator features - bad idea, lose Quick Send / status

Option A is cleanest but requires protocol support. Option B works today with no gateway changes.

### 2. Can the OpenClaw gateway run natively on Windows?

**Likely yes, with work.**

The gateway is Node.js. Node.js runs natively on Windows. But:

| Concern | Risk | Notes |
|---------|------|-------|
| Unix signals (SIGTERM, SIGHUP) | Medium | Gateway likely uses process signals. Windows has different signal model. Node.js abstracts some of this but not all. |
| File paths (forward vs back slash) | Low | Node.js `path` module handles this if used consistently. |
| Spawning child processes | Medium | `spawn('sh', ['-c', ...])` won't work on Windows. Need `cmd.exe` or `powershell.exe`. |
| `launchd`/`systemd` service install | High | `openclaw onboard --install-daemon` installs a launchd/systemd service. Windows needs a Windows Service or Task Scheduler equivalent. |
| WhatsApp/Telegram/Discord channels | Low | These are network clients, platform-agnostic. |
| Pi agent RPC | Low | Spawns Node.js processes - should work cross-platform. |
| File watching (chokidar) | Low | Works on Windows. |
| Browser automation (Playwright) | Low | Playwright supports Windows natively. |

**Recommendation:** Audit the gateway codebase for Unix assumptions. This could be a relatively tractable porting effort - most of the gateway is pure Node.js WebSocket/HTTP work.

### 3. What about the service lifecycle on Windows?

On macOS: launchd plist. On Linux: systemd unit. On Windows, options include:

- **Windows Service** (via [node-windows](https://github.com/coreybutler/node-windows) or .NET service host)
- **Task Scheduler** (run at logon)
- **Startup folder** (simplest, least robust)
- **Tray app manages gateway process** (like macOS menubar app can start/stop gateway)

The Mac menubar app has "Gateway start/stop/restart" in its menu. Windows Command Center can restart a tray-managed SSH tunnel, but it intentionally does not stop or kill externally managed gateway processes. If the gateway runs as a future Windows-managed process, the tray app could add explicit start/stop/restart controls for that owned process.

### 4. WSL2 networking: the NAT problem

WSL2 runs behind a NAT. The implications:

| Direction | Works? | Notes |
|-----------|--------|-------|
| Windows → WSL2 localhost | ✅ Usually | `localhost` forwarding works for TCP. |
| WSL2 → Windows localhost | ⚠️ Varies | Use `$(hostname).local` or `host.docker.internal`. |
| External → WSL2 | ❌ By default | Needs port forwarding or `--bind 0.0.0.0`. |
| WSL2 → External | ✅ | NAT outbound works fine. |

**For the tray-app-as-node scenario:** The tray app (Windows) connects *outward* to the WSL2 gateway. This is the easy direction - Windows → WSL2 localhost works. No NAT issues.

**For native Windows gateway:** No NAT at all. Everything is loopback. Problem solved.

### 5. Dual canvas: WebChat + Node Canvas

The tray app currently uses WebView2 for WebChat. The node canvas is a *separate* surface. Options:

- **Two WebView2 instances** - one for chat, one for canvas (each in its own window/panel)
- **Tab-based UI** - WebView2 with tab switching between chat and canvas
- **Canvas as separate window** - floating overlay window with WebView2 (like macOS canvas)

**Recommendation:** Separate floating window for canvas (matches macOS behavior). The chat WebView2 stays in the tray flyout/window. Canvas appears when the agent calls `canvas.present` and hides on `canvas.hide`.

### 6. Device identity + pairing

The node protocol requires a stable device identity (`device.id`) derived from a keypair. The tray app needs to:

1. Generate an Ed25519 keypair on first run
2. Store it in `%APPDATA%\OpenClaw\device.json`
3. Derive a fingerprint as the device ID
4. Sign the challenge nonce during connect
5. Handle the pairing approval flow (first time only; device token persisted after approval)

.NET has `System.Security.Cryptography` for Ed25519 (or use a NuGet package for older .NET versions).

---

## Phased Roadmap

### Phase 1: Tray App as Native Windows Node - Notifications + Canvas
**Priority: HIGH | Effort: Medium | Impact: Huge**

- [x] Implement node protocol in `OpenClaw.Shared` (connect with `role: "node"`, handle `node.invoke`)
- [x] Device identity + keypair generation + pairing flow
- [x] `system.notify` - agent can request Windows toast notifications
- [x] `canvas.present` / `canvas.hide` - floating WebView2 canvas window
- [x] `canvas.navigate` / `canvas.eval` / `canvas.snapshot` - full canvas support
- [x] `canvas.a2ui.push` / `canvas.a2ui.pushJSONL` / `canvas.a2ui.reset` - A2UI rendering
- [x] `device.info` / `device.status` - metadata and lightweight status payloads
- [x] `system.run` - exec commands on Windows (PowerShell/cmd) with ICommandRunner abstraction
- [x] `system.execApprovals.get/set` - remote-manageable exec approval policy
- [x] Settings UI for node capabilities (enable/disable canvas, screen, camera, location, browser proxy)
- [x] Resolve #9 (WebView2 ARM64) - required for canvas

**Depends on:** #5 (Canvas Panel), #9 (WebView2 ARM64)

### Phase 2: Screen Capture + Camera
**Priority: HIGH | Effort: Medium | Impact: High**

- [x] `camera.list` - enumerate Windows cameras (DeviceInformation.FindAllAsync)
- [x] `camera.snap` - capture photo from webcam (MediaCapture + frame reader fallback)
- [x] `camera.clip` - record short video clip (MediaCapture + MediaEncoding)
- [x] `screen.record` - capture Windows desktop via Graphics Capture API
- [x] `screen.snapshot` - screenshot via Windows.Graphics.Capture
- [x] Permission prompts (camera: UnauthorizedAccessException → toast; future MSIX consent)
- [x] Multi-monitor support for screen capture (`screenIndex` param)

### Phase 3: Native Windows Gateway (Exploration)
**Priority: MEDIUM | Effort: High | Impact: High**

- [ ] Audit OpenClaw gateway for Unix-specific code
- [ ] Test `openclaw gateway` on Windows (Node.js native)
- [ ] Fix platform-specific issues (signals, paths, child process spawning)
- [ ] Windows Service integration for daemon mode
- [ ] Tray app: "Start/Stop/Restart Gateway" menu items (parity with Mac menubar)
- [ ] `openclaw onboard --install-daemon` for Windows (Task Scheduler or Windows Service)
- [ ] Document Windows-native gateway setup

### Phase 4: Feature Parity + Polish
**Priority: LOW | Effort: Medium | Impact: Medium**

- [x] `location.get` - Windows Location API
- [ ] TTS / Speech Synthesis
- [ ] Microphone / voice input
- [x] `browser.proxy` - local browser-control bridge on gateway port + 2, including SSH companion-forward diagnostics
- [x] Browser-control host setup guidance and local host runtime smoke for end-to-end browser smoke tests
- [ ] Bundled/browser-control host installer/launcher
- [ ] UI Automation (Windows equivalent of macOS Accessibility API)
- [ ] Auto-update improvements (current auto-update from GitHub Releases → MSI/MSIX?)

---

## Technical Deep Dives

### Architecture: Node Protocol Handler

```
OpenClaw.Shared/
├── OpenClawGatewayClient.cs    ← operator client
├── WindowsNodeClient.cs        ← node protocol handler
├── DeviceIdentity.cs           ← Ed25519 keypair + device token
├── NodeCapabilities.cs         ← command/capability interfaces
└── Capabilities/
    ├── CanvasCapability.cs
    ├── CameraCapability.cs
    ├── ScreenCapability.cs
    ├── LocationCapability.cs
    └── SystemCapability.cs

OpenClaw.Tray.WinUI/
├── Services/
│   ├── NodeService.cs          ← orchestrates node connection
│   ├── CameraCaptureService.cs
│   ├── ScreenCaptureService.cs
│   ├── ScreenRecordingService.cs
│   ├── LocalCommandRunner.cs
│   └── SettingsManager.cs
├── Windows/
│   ├── CanvasWindow.xaml       ← floating WebView2 canvas
│   └── CanvasWindow.xaml.cs
```

### Architecture: Dual-Role Connection Flow

```
Tray App Start
    │
    ├─ Load settings (gateway URL, token)
    ├─ Load/generate device identity (keypair)
    │
    ├─ Connect WS #1: role=operator
    │   ├─ Quick Send, status, WebChat, channel control
    │   └─ (existing functionality)
    │
    └─ Connect WS #2: role=node
        ├─ Advertise caps: [canvas, camera, location, screen, system]
        ├─ Advertise commands: [canvas.*, camera.*, location.get, screen.*, system.*]
        ├─ Handle node.invoke requests
        │   ├─ canvas.present → show/navigate CanvasWindow
        │   ├─ canvas.snapshot → WebView2 CapturePreview
        │   ├─ camera.snap → MediaCapture → JPEG → base64
        │   ├─ camera.clip → MediaCapture → MP4 → base64
        │   ├─ location.get → Windows.Devices.Geolocation
        │   ├─ screen.snapshot → GraphicsCapture → image base64
        │   ├─ screen.record → GraphicsCapture → MP4 → base64
        │   ├─ system.run → Process.Start → stdout/stderr
        │   └─ system.notify → ToastNotification
        └─ Report permissions changes
```

---

## Contributing

This is a big effort and **contributions are very welcome!** Here's how to get started:

### Good First Issues

1. **Capability diagnostics copy** - ✅ Command Center can copy a summary of declared commands, gateway allowlist status, and dangerous-command opt-ins.
2. **Gateway health summary** - Show version, update state, auth state, and active connection health in one panel.
3. **Channel status cards** - Surface configured/running/error/probe state for channels.

### Medium Issues

4. **Browser proxy parity** - Windows now includes a Mac-compatible local `browser.proxy` bridge to the browser control host on gateway port + 2, and managed SSH tunnel mode forwards local+2 to remote+2 when the browser proxy capability is enabled; continue hardening live browser-host setup guidance and diagnostics.
5. **Gateway/channel flyout** - Show configured/running/error/probe state for channels and gateway health in the tray.

### Harder Issues

6. **Voice mode parity** - PR #120 has been reviewed and should stay blocked until it is rebased/split, gated default-off through Settings, aligned with a shared Mac/gateway voice command contract, and hardened for credential storage and permission prompts.
7. **Native Windows gateway audit** - Run `openclaw gateway` on Windows, identify and fix platform-specific failures.
8. **Richer channel operations** - Add tray surfaces for channel configuration, probe status, token source, last error, and recovery actions.

### Development Setup

See `DEVELOPMENT.md`. Quick start:
```powershell
git clone https://github.com/shanselman/openclaw-windows-hub.git
cd openclaw-windows-hub
.\build.ps1
dotnet run --project src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj
```

Requires .NET 10.0 SDK, Windows 10/11. For testing node protocol, you'll need a running OpenClaw gateway (in WSL2 or on another machine).

---

## Open Questions

- [x] Should dangerous command opt-ins be shown in the tray as a guided repair flow, a docs link, or both? Command Center now shows copyable safety guidance but intentionally avoids one-click dangerous repair commands.
- [ ] How much channel management should live in the native tray versus opening the web dashboard?
- [x] Should Voice Mode land as a separate parity track after the open PR is reviewed against current Mac architecture? Yes. PR #120 should not advertise voice commands from Windows until the shared contract, Settings gate, gateway allowlist, and credential-storage concerns are resolved.

---

*This issue is a living document. As we make progress, sub-issues will be filed for individual work items and linked back here.*

/cc @shanselman
