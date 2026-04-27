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
| `system.run` / `system.which` | Controlled command execution | Uses local exec approval policy |
| `camera.list` | Enumerate cameras | Returns device IDs and names |
| `camera.snap` | Capture photo | Returns base64 image (NV12 fallback) |
| `camera.clip` | Capture video clip | Returns MP4/base64 metadata |
| `location.get` | Get Windows location | Uses Windows location permission/settings |
| `device.info` / `device.status` | Device metadata/status | Returns host/app/locale plus battery/storage/network/uptime payloads |

## Capabilities Advertised

When the node connects, it advertises these capabilities:
- `canvas` - WebView2-based canvas window
- `screen` - Screen snapshot and recording via Windows.Graphics.Capture
- `system` - Notifications, command execution (`system.run`, `system.run.prepare`, `system.which`), exec approval policy
- `camera` - MediaCapture photo/video capture (frame reader fallback)
- `location` - Windows.Devices.Geolocation
- `device` - Host/app metadata and lightweight status

## Security Features

- **URL Validation**: Canvas blocks `file://`, `javascript:`, localhost, private IPs, IPv6 localhost
- **Screen Capture Notification**: User is notified when screen snapshots are captured
- **Screen Recording Allowlist**: `screen.record` must be explicitly allowed by the gateway and does not leave a hidden local MP4 copy on Windows
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

### Canvas window doesn't appear
- Check logs for `canvas.present` command received
- Verify URL is not blocked by security validation

### Camera permission denied
- If you see "Camera access blocked", enable camera access for desktop apps in Windows Privacy settings
- Packaged MSIX builds will show the system consent prompt automatically

## Remaining Work (Roadmap)

1. ~~**system.run + exec approvals**~~ ✅ Implemented
   - `system.run` with PowerShell/cmd support
   - `system.run.prepare` pre-flight command
   - `system.which` command lookup
   - `system.execApprovals` allowlist flow
2. ~~**screen.record**~~ ✅ Implemented
   - Graphics Capture video recording (MP4/base64)
3. ~~**camera.clip**~~ ✅ Implemented
   - Short webcam video capture (MediaCapture + encoding)
4. ~~**A2UI pushJSONL alias + device status**~~ ✅ Implemented
   - Legacy `canvas.a2ui.pushJSONL`
   - Safe `device.info` / `device.status`
5. **Packaging & consent prompts**
   - MSIX packaging with camera/screen capabilities for system prompts
6. **Test matrix & polish**
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
