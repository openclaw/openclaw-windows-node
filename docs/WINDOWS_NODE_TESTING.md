# Windows Node Testing Guide

## Overview

The Windows Node feature allows the tray app to receive commands from the OpenClaw agent (canvas, screenshots, screen recordings, camera, location, notifications, and controlled command execution). This is **experimental** and must be explicitly enabled in Settings.

## How to Enable

1. Open the tray app
2. Right-click → Settings
3. Scroll to "ADVANCED (EXPERIMENTAL)"
4. Toggle "Enable Node Mode" ON
5. Click Save

## What You Can Test Now

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
- When the agent captures your screen, you should see "📸 Screen Captured" toast
- This is throttled to max once per 10 seconds

### 4. Command Center
- Open the tray status detail or launch `openclaw://commandcenter`
- In Node Mode, verify the window shows gateway channel health from node `health` events plus a synthesized local Windows node when operator `node.list` is not connected
- Check diagnostics for pairing approval, stale health, all-stopped channels, allowlist filtering, browser control host availability for `browser.proxy`, and usage-cost gaps
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
| `screen.snapshot` | Take screenshot | Captures screen, shows notification, returns base64 |
| `screen.record` | Record short screen clip | Returns MP4/base64 metadata; requires explicit gateway allowlist |
| `system.notify` | Show notification | Displays toast notification |
| `system.run` / `system.which` | Controlled command execution | Uses local exec approval policy; `prompt` decisions show a Windows Allow once / Always allow / Deny dialog |
| `camera.list` | Enumerate cameras | Returns device IDs and names |
| `camera.snap` | Capture photo | Returns base64 image (NV12 fallback) |
| `camera.clip` | Capture video clip | Returns MP4/base64 metadata |
| `location.get` | Get Windows location | Uses Windows location permission/settings |
| `device.info` / `device.status` | Device metadata/status | Returns host/app/locale plus battery/storage/network/uptime payloads |
| `browser.proxy` | Proxy browser-control host requests | Requires Browser proxy bridge enabled, a compatible browser-control host listening on gateway port + 2, and matching browser-control auth |
| `stt.transcribe` | Speech-to-text from default microphone | Default-off; bounded `maxDurationMs` ≤ 30000; concatenates phrases until duration elapses; requires explicit gateway allowlist |
| `tts.speak` | Speak text aloud | Requires Text-to-speech playback enabled in Settings; gateway mode also requires `tts.speak` in `gateway.nodes.allowCommands` |

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

## Security Features

- **URL Validation**: Canvas blocks `file://`, `javascript:`, localhost, private IPs, IPv6 localhost
- **Screen Capture Notification**: User is notified when screen snapshots are captured
- **Screen Recording Allowlist**: `screen.record` must be explicitly allowed by the gateway and does not leave a hidden local MP4 copy on Windows
- **Command Center Redaction**: recent node invoke activity records command name, status, duration, node id, and privacy class only; it does not store base64 payloads, screenshots, recordings, tokens, or command arguments
- **Node Mode Toggle**: Must be explicitly enabled by user
- **Command Validation**: Only alphanumeric commands with dots/hyphens allowed

## Troubleshooting

### Node doesn't connect
- Check that gateway URL and token are correct in Settings
- Check logs for connection errors
- Verify gateway is running and accessible

### No "Node Mode Active" notification
- Ensure Windows notifications are enabled for the app
- Check if notification settings in the app are enabled

### `browser.proxy` reports no browser-control host
- Confirm the Browser proxy bridge toggle is enabled in Settings, then save and reconnect or re-pair if the gateway keeps an older command snapshot.
- The bridge is local-only: it calls `http://127.0.0.1:<gateway-port+2>` from Windows. For a gateway on `ws://127.0.0.1:18789`, the browser-control host must listen on `127.0.0.1:18791`.
- In managed SSH tunnel mode, keep Browser proxy bridge enabled so the tray forwards local gateway port + 2 to remote gateway port + 2. Settings shows a selectable preview of the exact `ssh -N -L ...` command.
- If using a manual SSH tunnel, add both forwards, for example: `ssh -N -L 18789:127.0.0.1:18789 -L 18791:127.0.0.1:18791 <user>@<host>`. If local and remote gateway ports differ, forward `<local-gateway-port+2>` to `127.0.0.1:<remote-gateway-port+2>`.
- A local SSH forward is not enough if the remote browser-control host is not running. Command Center port diagnostics should show whether the local gateway and browser-control ports are listening and which process owns them.
- If Command Center shows the browser-control port listening but `browser.proxy` returns an auth error, verify the Windows Settings gateway token matches the browser-control host token/password. QR/bootstrap pairing can connect the node without saving a shared gateway token, but browser-control auth may still require one.
- A local smoke can verify the host dependency without proving gateway invoke auth: start the upstream browser-control host with a temporary no-secret config, confirm `http://127.0.0.1:<gateway-port+2>/` and `/tabs` return HTTP 200, then stop the captured host process. The full parity smoke is not complete until `openclaw nodes invoke --command browser.proxy` succeeds through the active gateway.

### Canvas window doesn't appear
- Check logs for `canvas.present` command received
- Verify URL is not blocked by security validation

### Camera permission denied
- If you see "Camera access blocked", enable camera access for desktop apps in Windows Privacy settings
- Packaged MSIX builds will show the system consent prompt automatically

### `stt.transcribe` returns "Speech recognition failed" or "Internal Speech Error"
- Open Windows Settings → Privacy & security → Speech (`ms-settings:privacy-speech`)
- Turn **Online speech recognition** = On. The Windows speech recognizer's default dictation grammar often fails without it, and Windows surfaces an unmapped HRESULT as "Internal Speech Error"
- Open Windows Settings → Time & language → Language & region (`ms-settings:regionlanguage`), select your display language → Language options, and confirm **Speech** appears under Installed features (install it if not, ~50 MB; reboot or sign out/in afterward)
- Verify the recognizer end-to-end with `ms-settings:speech` → "Microphone" → **Get started** before re-trying `stt.transcribe`

### `stt.transcribe` returns "Microphone permission denied"
- Open Windows Settings → Privacy & security → Microphone
- Ensure **Microphone access** (top-level toggle) is on
- For **unpackaged** tray builds (the default `.\build.ps1` output): ensure **Let desktop apps access your microphone** is on. The tray exe will **not** appear as its own row — desktop-app access is granted as a group, not per-app
- For **packaged MSIX** tray builds: the tray appears as its own entry under "Let apps access your microphone" and must be individually enabled (the OS shows a consent prompt on first use)
- After changing permissions, re-pair the node so the gateway picks up the new advertised command

### `stt.transcribe` returns "Language pack 'X' is not installed"
- Open Windows Settings → Time & language → Language & region
- Add the requested display language and ensure the **Speech** optional feature is installed
- Restart the tray after installing the speech pack

### Manual STT validation
1. Enable Node Mode in Settings.
2. Enable **Speech-to-text (microphone)** in Settings → Node mode.
3. Append `stt.transcribe` to your existing gateway allowlist (do **not** copy a literal `...` — substitute the commands you already allow). For example, starting from the recommended Windows safe companion list:
   ```bash
   openclaw config set gateway.nodes.allowCommands '["canvas.present","canvas.hide","canvas.navigate","canvas.eval","canvas.snapshot","canvas.a2ui.push","canvas.a2ui.pushJSONL","canvas.a2ui.reset","camera.list","location.get","screen.snapshot","device.info","device.status","system.execApprovals.get","system.execApprovals.set","stt.transcribe"]'
   openclaw gateway restart
   ```
4. Re-pair or re-approve the node so the gateway refreshes its command snapshot.
5. Invoke and speak a short phrase:
   ```bash
   openclaw nodes invoke --node <id> --command stt.transcribe \
       --params '{"maxDurationMs":5000,"language":"en-US"}'
   ```
6. The Windows microphone OS indicator should appear during recognition. Confirm a `transcribed:true` payload returns the text.

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
- `src/OpenClaw.Tray.WinUI/Services/NodeService.cs` - Orchestrates capabilities
- `src/OpenClaw.Tray.WinUI/Services/ScreenCaptureService.cs` - screen snapshots
- `src/OpenClaw.Tray.WinUI/Services/ScreenRecordingService.cs` - screen recordings
- `src/OpenClaw.Tray.WinUI/Services/CameraCaptureService.cs` - camera photo/video capture
- `src/OpenClaw.Tray.WinUI/Windows/CanvasWindow.xaml` - WebView2 canvas
