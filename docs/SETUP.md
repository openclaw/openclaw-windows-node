# OpenClaw Tray — Installation & Setup Guide

This guide covers installing OpenClaw Tray (Molty) on Windows using the pre-built installer. For building from source, see [DEVELOPMENT.md](../DEVELOPMENT.md).

## Prerequisites

Before installing, make sure you have:

- **Windows 10 (20H2 or later)** or **Windows 11**
- **WebView2 Runtime** — pre-installed on Windows 11 and most up-to-date Windows 10 systems. If missing, download from [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/).
- An active **OpenClaw account** with a gateway token — sign up at [openclaw.ai](https://openclaw.ai).

## Step-by-Step Installation

### 1. Download the Installer

Go to the [Releases page](https://github.com/openclaw/openclaw-windows-node/releases) and download the latest installer for your architecture:

| File | Architecture |
|------|-------------|
| `OpenClawTray-Setup-x64.exe` | Intel / AMD (most PCs) |
| `OpenClawTray-Setup-arm64.exe` | ARM64 (Surface Pro X, Snapdragon laptops) |

If you're unsure, use the **x64** installer.

### 2. Run the Installer

Double-click the downloaded `.exe`. Windows may show a SmartScreen prompt — click **More info → Run anyway** (this is normal for code-signed apps that haven't yet accumulated reputation).

The installer runs without requiring administrator privileges.

### 3. Choose Optional Components

The installer offers two optional components:

- **Create Desktop Icon** — adds a shortcut to your desktop.
- **Start OpenClaw Tray when Windows starts** — launches Molty automatically at login (recommended).
- **Install PowerToys Command Palette extension** — enables OpenClaw commands in PowerToys Command Palette (requires [PowerToys](https://github.com/microsoft/PowerToys) to be installed). See [POWERTOYS.md](./POWERTOYS.md) for details.

### 4. First Launch

After the installer finishes, OpenClaw Tray starts automatically. Look for the 🦞 lobster icon in the system tray (bottom-right corner of the taskbar, near the clock).

If you don't see it, check the **hidden icons** area (the `^` arrow next to the tray).

### 5. Onboarding Wizard

On first launch, Molty opens a **6-screen onboarding wizard** that walks you through setup:

1. **Welcome** — A friendly greeting introducing OpenClaw and Molty. Click **Get Started** to begin.

2. **Connection** — Choose how to connect to your gateway:
   - **Local** — Select this if the gateway runs on the same machine or in WSL. The URL is pre-filled to `ws://localhost:18789`.
   - **Remote** — Enter your gateway URL and bootstrap token manually, **or** paste a base64url-encoded **setup code** (a single string containing both URL and token).
   - **Later** — Skip connection setup for now. You can configure it later from the tray menu → Settings.

   After entering your details, click **Test Connection**. The wizard performs a real WebSocket handshake with Ed25519 device authentication and shows real-time status feedback (connecting → connected → pairing).

3. **Wizard** — If your gateway supports it, this screen walks you through gateway-driven configuration steps (AI provider selection, personality setup, communication channels). The steps are defined by your gateway via RPC. If the gateway doesn't support wizard mode, this screen is skipped automatically.

4. **Permissions** — Reviews Windows system permissions needed for full functionality:
   - **Notifications** — for toast alerts
   - **Camera** — for camera capture
   - **Microphone** — for voice input
   - **Screen Capture** — for screenshots
   - **Location** — optional, for location-aware features; packaged installs declare this capability so Windows may prompt for location consent the first time it is used

   Each permission shows its current status. Click **Open Settings** next to any permission to jump directly to the relevant Windows Settings page.

5. **Chat** — Meet your agent! This screen opens a live chat powered by the gateway's web UI. A bootstrap message is sent automatically to kick off your first conversation.

6. **Ready** — A summary of available features (tray menu, channels, voice, canvas, skills). Toggle **Launch at Login** to start Molty with Windows, then click **Finish** to complete setup.

After the wizard, the tray icon turns green when connected. You can re-run the wizard or change settings anytime from the tray menu.

## Tray Icon Status

| Icon colour | Meaning |
|-------------|---------|
| 🟢 Green | Connected to gateway |
| 🟡 Amber | Connecting / reconnecting |
| 🔴 Red | Error |
| ⚫ Grey | Disconnected |

Left-click the icon to open the quick-access menu. Right-click for context options.

## Deep Links

OpenClaw Tray responds to `openclaw://` deep links, which can be invoked from a browser or another app:

| Link | Action |
|------|--------|
| `openclaw://dashboard` | Open the OpenClaw web dashboard |
| `openclaw://dashboard/sessions` | Open the sessions dashboard page |
| `openclaw://dashboard/channels` | Open the channels dashboard page |
| `openclaw://dashboard/skills` | Open the skills dashboard page |
| `openclaw://dashboard/cron` | Open the cron dashboard page |
| `openclaw://chat` | Open the embedded Chat page |
| `openclaw://send` | Open the Quick Send dialog |
| `openclaw://send?message=Hello` | Open Quick Send with pre-filled text |
| `openclaw://settings` | Open the Settings page |
| `openclaw://setup` | Open the Setup Wizard |
| `openclaw://commandcenter` | Open Command Center diagnostics |
| `openclaw://activity` | Open the Activity page |
| `openclaw://history` | Open the Activity page filtered to notification history |
| `openclaw://healthcheck` | Run a manual health check |
| `openclaw://check-updates` | Run a manual update check |
| `openclaw://logs` | Open the current tray log file |
| `openclaw://log-folder` | Open the logs folder |
| `openclaw://config` | Open the config folder |
| `openclaw://diagnostics` | Open the diagnostics JSONL folder |
| `openclaw://support-context` | Copy redacted support context |
| `openclaw://debug-bundle` | Copy a combined debug bundle for support |
| `openclaw://browser-setup` | Copy browser.proxy/browser-control setup guidance |
| `openclaw://port-diagnostics` | Copy gateway/browser/tunnel port diagnostics with owner PID stop hints |
| `openclaw://capability-diagnostics` | Copy permissions, allowlist, and parity diagnostics |
| `openclaw://node-inventory` | Copy node capabilities, commands, and policy status |
| `openclaw://channel-summary` | Copy channel health and start/stop availability |
| `openclaw://activity-summary` | Copy recent tray activity for troubleshooting |
| `openclaw://extensibility-summary` | Copy channel, skills, and cron dashboard surface guidance |
| `openclaw://restart-ssh-tunnel` | Restart the tray-managed SSH tunnel when enabled |
| `openclaw://agent?message=Hello` | Send a message directly to the connected gateway |

## Troubleshooting

### Tray icon doesn't appear

1. Check Task Manager for `OpenClaw.Tray.WinUI.exe` — if it's running, the icon may be hidden.
2. Drag the icon out of the hidden overflow area to always show it.
3. If the process isn't running, try launching from Start Menu → **OpenClaw Tray**.

### "WebView2 Runtime is missing" error

Download and install WebView2 from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/). The **Evergreen Standalone Installer** is the easiest option.

### Can't connect to gateway

- Verify the gateway URL in Settings (default: `ws://localhost:18789`).
- Make sure the OpenClaw gateway process is running.
- Check Windows Firewall — if your gateway runs on a different machine, allow inbound traffic on port 18789.
- See the log at `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` for connection errors.

### "Not yet paired" message on reconnect

If the tray shows **Pending approval** after reconnecting, run the approval command shown in the tray or log:

```
openclaw devices approve <device-id>
```

See [issue #81](https://github.com/openclaw/openclaw-windows-node/issues/81) for context on this flow.

### Setup code doesn't work

- Make sure you paste the **entire** setup code — it's a single base64url-encoded string.
- Check for accidental leading/trailing whitespace.
- The code must be from a compatible gateway version. Try entering the gateway URL and token manually instead.

### Connection test fails

- Verify the gateway URL is correct (e.g., `ws://localhost:18789` for local, or the full URL for remote).
- Check that your token is valid and hasn't expired.
- If the gateway is on another machine, ensure Windows Firewall allows traffic on the gateway port.
- See the log at `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` for detailed error messages.

### Wizard shows "offline"

The Wizard screen relies on the gateway's wizard protocol. If it shows offline:
- The gateway may not support wizard mode yet — this is fine, configuration can be done later.
- Check that the gateway is running and reachable.
- You can skip the Wizard screen and configure your gateway manually from the tray menu → Settings.

### Settings are not saved

Settings are stored at `%APPDATA%\OpenClawTray\settings.json`. If this file is corrupt, delete it and reconfigure from scratch.

### Auto-start isn't working

1. Open Settings and toggle **Start with Windows** off, then on again.
2. Check `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` for a `OpenClawTray` entry.

## Updating

OpenClaw Tray checks for updates automatically and shows a notification when a new version is available. Click **Update** to download and apply the update. You can also manually check by re-downloading from the [Releases page](https://github.com/openclaw/openclaw-windows-node/releases).

## Uninstalling

Go to **Settings → Apps → Installed apps**, find **OpenClaw Tray**, and click **Uninstall**. Alternatively, use **Add or Remove Programs** in the Control Panel.

Your settings file at `%APPDATA%\OpenClawTray\settings.json` and device key at `%LOCALAPPDATA%\OpenClawTray\device-key-ed25519.json` are not removed automatically — delete them manually if you want a clean uninstall.
