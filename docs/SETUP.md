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

### 5. Configure the Connection

On first launch, a **Welcome** dialog appears. Click **Open Settings** to configure:

| Setting | What to enter |
|---------|--------------|
| **Gateway URL** | `ws://localhost:18789` (if running OpenClaw locally) or your remote gateway address |
| **Token** | Your OpenClaw API token from [openclaw.ai](https://openclaw.ai) |

Click **Save**. Molty will connect to the gateway and the tray icon will turn green when connected.

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
| `openclaw://chat` | Open the embedded Web Chat window |
| `openclaw://send` | Open the Quick Send dialog |
| `openclaw://send?message=Hello` | Open Quick Send with pre-filled text |
| `openclaw://settings` | Open the Settings dialog |
| `openclaw://agent?message=Hello` | Send a message directly (with confirmation) |

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
