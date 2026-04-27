# 🦞 OpenClaw Windows Hub

A Windows companion suite for [OpenClaw](https://openclaw.ai) - the AI-powered personal assistant.

*Made with 🦞 love by Scott Hanselman and Molty*

![Molty - Windows Tray App](docs/molty1.png)

![Molty - Command Palette](docs/molty2.png)

## Projects

This monorepo contains four projects:

| Project | Description |
|---------|-------------|
| **OpenClaw.Tray.WinUI** | System tray application (WinUI 3) for quick access to OpenClaw |
| **OpenClaw.Shared** | Shared gateway client library |
| **OpenClaw.Cli** | CLI validator for WebSocket connect/send/probe using tray settings |
| **OpenClaw.CommandPalette** | PowerToys Command Palette extension |

## 🚀 Quick Start

> **End-user installer?** See [docs/SETUP.md](docs/SETUP.md) for a step-by-step installation guide (no build required).

### Prerequisites
- Windows 10 (20H2+) or Windows 11
- .NET 10.0 SDK - https://dotnet.microsoft.com/download/dotnet/10.0
- Windows 10 SDK (for WinUI build) - install via Visual Studio or standalone
- WebView2 Runtime - pre-installed on modern Windows, or get from https://developer.microsoft.com/microsoft-edge/webview2
- PowerToys (optional, for Command Palette extension) — see [docs/POWERTOYS.md](docs/POWERTOYS.md)

### Build

Use the build script to check prerequisites and build:

```powershell
# Check prerequisites
.\build.ps1 -CheckOnly

# Build all projects
.\build.ps1

# Build specific project
.\build.ps1 -Project WinUI
```

Or build directly with dotnet:

```powershell
# Build all (use build.ps1 for best results)
dotnet build

# Build WinUI (requires runtime identifier for WebView2 support)
dotnet build src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj -r win-arm64  # ARM64
dotnet build src/OpenClaw.Tray.WinUI/OpenClaw.Tray.WinUI.csproj -r win-x64    # x64

# Build MSIX package (for camera/mic consent prompts)
dotnet build src/OpenClaw.Tray.WinUI -r win-arm64 -p:PackageMsix=true  # ARM64 MSIX
dotnet build src/OpenClaw.Tray.WinUI -r win-x64 -p:PackageMsix=true    # x64 MSIX
```

### Run Tray App

```powershell
# Run the exe directly (path includes runtime identifier)
.\src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.19041.0\win-arm64\OpenClaw.Tray.WinUI.exe  # ARM64
.\src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\OpenClaw.Tray.WinUI.exe    # x64
```

### Run CLI WebSocket Validator

Use the CLI to validate gateway connectivity and `chat.send` outside the tray UI.

```powershell
# Show help
dotnet run --project src/OpenClaw.Cli -- --help

# Use tray settings from %APPDATA%\OpenClawTray\settings.json and send one message
dotnet run --project src/OpenClaw.Cli -- --message "quick send validation"

# Loop sends and also probe sessions/usage/nodes APIs
dotnet run --project src/OpenClaw.Cli -- --repeat 5 --delay-ms 1000 --probe-read --verbose

# Override gateway URL/token for isolated testing
dotnet run --project src/OpenClaw.Cli -- --url ws://127.0.0.1:18789 --token "<token>" --message "override test"
```

## 📦 OpenClaw.Tray (Molty)

Modern Windows 11-style system tray companion that connects to your local OpenClaw gateway.

### Features
- 🦞 **Lobster branding** - Pixel-art lobster tray icon with status colors
- 🎨 **Modern UI** - Windows 11 flyout menu with dark/light mode support
- 💬 **Quick Send** - Send messages via global hotkey (Ctrl+Alt+Shift+C)
- 🔄 **Auto-updates** - Automatic updates from GitHub Releases
- 🌐 **Web Chat** - Embedded chat window with WebView2
- 📊 **Live Status** - Real-time sessions, channels, and usage display
- 🧭 **Command Center** - Dense gateway, channel, usage, node, pairing, and allowlist diagnostics from one window
- ⚡ **Activity Stream** - Dedicated flyout for live session, usage, node, and notification events
- 🔔 **Toast Notifications** - Clickable Windows notifications with [smart categorization](docs/NOTIFICATION_CATEGORIZATION.md)
- 📡 **Channel Control** - Start/stop Telegram & WhatsApp from the menu
- 🖥️ **Node Observability** - Node inventory with online/offline state and copyable summary
- ⏱ **Cron Jobs** - Quick access to scheduled tasks
- 🚀 **Auto-start** - Launch with Windows
- ⚙️ **Settings** - Full configuration dialog
- 🎯 **First-run experience** - Welcome dialog guides new users

#### Quick Send scope requirement

Quick Send uses the gateway `chat.send` method and requires the operator device to have `operator.write` scope.

If Quick Send fails with `missing scope: operator.write`, Molty now copies identity + remediation guidance to your clipboard, including:

- operator role and `client.id` used by the tray app
- gateway-reported operator device id (if provided)
- currently granted scopes (if provided)

For this specific error (`missing scope: operator.write`), the cause is an **operator token scope issue**. Update the token used by the tray app so it includes `operator.write`, then retry Quick Send.

If Quick Send fails with `pairing required` / `NOT_PAIRED`, that is a **device approval** issue. Approve the tray device in gateway pairing approvals, reconnect, and retry.

### Menu Sections
- **Status** - Gateway connection status with click-to-view details
- **Command Center** - Status detail window with diagnostics, channel health, usage, sessions, nodes, and copyable repair commands
- **Sessions** - Active agent sessions with preview and per-session controls
- **Usage** - Provider/cost summary with quick jump to activity details
- **Channels** - Telegram/WhatsApp status with toggle control
- **Nodes** - Online/offline node inventory and copyable summary
- **Recent Activity** - Timestamped event stream for sessions, usage, nodes, and notifications
- **Actions** - Dashboard, Web Chat, Quick Send, Activity Stream, History
- **Settings** - Configuration, auto-start, logs

### Mac Parity Status

Comparing against [openclaw-menubar](https://github.com/magimetal/openclaw-menubar) (macOS Swift menu bar app):

| Feature | Mac | Windows | Notes |
|---------|-----|---------|-------|
| Menu bar/tray icon | ✅ | ✅ | Color-coded status |
| Gateway status display | ✅ | ✅ | Connected/Disconnected |
| PID display | ✅ | ✅ | Command Center shows gateway listener process/PID |
| Channel status | ✅ | ✅ | Mac: Discord / Win: Telegram+WhatsApp |
| Sessions count | ✅ | ✅ | |
| Last check timestamp | ✅ | ✅ | Shown in tray tooltip |
| Gateway start/stop/restart | ✅ | ⚠️ | Windows can restart the managed SSH tunnel from Command Center; external gateway process control is not implemented |
| View Logs | ✅ | ✅ | |
| Open Web UI | ✅ | ✅ | |
| Refresh | ✅ | ✅ | Auto-refresh on menu open |
| Launch at Login | ✅ | ✅ | |
| Notifications toggle | ✅ | ✅ | |

### Windows-Only Features

These features are available in Windows but not in the Mac app:

| Feature | Description |
|---------|-------------|
| Quick Send hotkey | Ctrl+Alt+Shift+C global hotkey |
| Embedded Web Chat | WebView2-based chat window |
| Toast notifications | Clickable Windows notifications |
| Channel control | Start/stop Telegram & WhatsApp |
| Modern flyout menu | Windows 11-style with dark/light mode |
| Deep links | `openclaw://` URL scheme with IPC |
| First-run welcome | Guided onboarding for new users |
| PowerToys integration | Command Palette extension |

### 🔌 Node Mode (Agent Control)

When Node Mode is enabled in Settings, your Windows PC becomes a **node** that the OpenClaw agent can control - just like the Mac app! The agent can:

| Capability | Commands | Description |
|------------|----------|-------------|
| **System** | `system.notify`, `system.run`, `system.run.prepare`, `system.which`, `system.execApprovals.get`, `system.execApprovals.set` | Show Windows toast notifications, execute commands with policy controls |
| **Canvas** | `canvas.present`, `canvas.hide`, `canvas.navigate`, `canvas.eval`, `canvas.snapshot`, `canvas.a2ui.push`, `canvas.a2ui.pushJSONL`, `canvas.a2ui.reset` | Display and control a WebView2 window |
| **Screen** | `screen.snapshot`, `screen.record` | Capture screenshots and fixed-duration MP4 screen recordings |
| **Camera** | `camera.list`, `camera.snap`, `camera.clip` | Enumerate cameras and capture still photos or short video clips |
| **Location** | `location.get` | Return Windows geolocation when permission is available |
| **Device** | `device.info`, `device.status` | Return Windows host/app metadata and lightweight status |

#### Node Setup

1. **Enable Node Mode** in Settings (enabled by default)
2. **First connection** creates a pairing request on the gateway
3. **Approve the device** on your gateway:
   ```bash
   openclaw devices list          # Find your Windows device
   openclaw devices approve <id>  # Approve it
   ```
4. **Configure gateway allowCommands** - Add the commands you want to allow under `gateway.nodes` in `~/.openclaw/openclaw.json`:
   ```json
   {
     "gateway": {
       "nodes": {
         "allowCommands": [
           "system.notify",
           "system.run",
           "system.run.prepare",
           "system.which",
           "system.execApprovals.get",
           "system.execApprovals.set",
           "canvas.present",
           "canvas.hide",
           "canvas.navigate",
           "canvas.eval",
            "canvas.snapshot",
            "canvas.a2ui.push",
            "canvas.a2ui.pushJSONL",
            "canvas.a2ui.reset",
            "screen.snapshot",
            "camera.list",
            "camera.snap",
            "camera.clip",
            "location.get",
            "device.info",
            "device.status"
         ]
        }
      }
   }
   ```
    > ⚠️ **Important**: The gateway has a server-side allowlist. Commands must be listed explicitly - wildcards like `canvas.*` don't work! Privacy-sensitive commands such as `screen.record` should only be added to `allowCommands` when you explicitly want to allow them.

5. **Test it** from your Mac/gateway:
   ```bash
    # Show a notification
    openclaw nodes notify --node <id> --title "Hello" --body "From Mac!"
    
    # Open a canvas window
    openclaw nodes canvas present --node <id> --url "https://example.com"
    
    # Execute JavaScript (note: CLI sends "javaScript" param)
    openclaw nodes canvas eval --node <id> --javaScript "document.title"
    
    # Render A2UI JSONL in the canvas (pass the file contents as a string)
    openclaw nodes canvas a2ui push --node <id> --jsonl "$(cat ./ui.jsonl)"
    
    # Take a screenshot
    openclaw nodes invoke --node <id> --command screen.snapshot --params '{"screenIndex":0,"format":"png"}'

    # Record a short screen clip (requires explicitly allowing screen.record on the gateway)
    openclaw nodes screen record --node <id> --duration 3000 --fps 10 --screen 0 --no-audio --out /tmp/openclaw-windows-screen-record-test.mp4 --json

    # List cameras
    openclaw nodes invoke --node <id> --command camera.list

    # Take a photo (NV12/MediaCapture fallback)
    openclaw nodes invoke --node <id> --command camera.snap --params '{"deviceId":"<device-id>","format":"jpeg","quality":80}'

    # Execute a command on the Windows node
    openclaw nodes invoke --node <id> --command system.run --params '{"command":"Get-Process | Select -First 5","shell":"powershell","timeoutMs":10000}'

    # View exec approval policy
    openclaw nodes invoke --node <id> --command system.execApprovals.get

    # Update exec approval policy (add custom rules)
    openclaw nodes invoke --node <id> --command system.execApprovals.set --params '{"rules":[{"pattern":"echo *","action":"allow"},{"pattern":"*","action":"deny"}],"defaultAction":"deny"}'
    ```
    > 📷 **Camera permission**: Desktop builds rely on Windows Privacy settings. Packaged MSIX builds will show the system consent prompt.
    
    > 🔒 **Exec Policy**: `system.run` is gated by an approval policy on the Windows node at `%LOCALAPPDATA%\OpenClawTray\exec-policy.json` (schema: `{ "defaultAction": "...", "rules": [...] }`). This is separate from gateway-side `~/.openclaw/exec-approvals.json`.
    >
    > Rules are matched against the full command line. Known wrapper payloads such as `cmd /c ...`, `powershell -Command ...`, `pwsh -EncodedCommand ...`, and `bash -c ...` are also evaluated before execution. Dangerous environment overrides like `PATH`, `PATHEXT`, `NODE_OPTIONS`, `GIT_SSH_COMMAND`, `LD_*`, and `DYLD_*` are rejected.

#### Command Center diagnostics

Open the status detail/Command Center from the tray menu or with `openclaw://commandcenter`. It shows:

- channel health from gateway `health` events, including node-mode health received without a separate operator connection
- active sessions, usage/cost data, node inventory, declared commands, and Mac parity notes
- allowlist diagnostics that separate safe companion commands from privacy-sensitive opt-ins like `screen.record`, `camera.snap`, and `camera.clip`
- copyable repair commands for safe allowlist fixes and pending pairing approval
- recent activity and node invoke results through the Activity Stream, storing command names/status/duration only (not payloads, screenshots, recordings, or secrets)
    >
    > ```bash
    > openclaw nodes invoke --node <id> --command system.execApprovals.set --params '{"rules":[{"pattern":"powershell.exe","action":"allow"},{"pattern":"pwsh.exe","action":"allow"},{"pattern":"echo *","action":"allow"},{"pattern":"*","action":"deny"}],"defaultAction":"deny"}'
    > ```

    > 🔐 **Web Chat secure context**: Remote web chat requires `https://` (or localhost). If using a self-signed cert, trust it in Windows (Trusted Root Certification Authorities) or use an SSH tunnel to localhost.

#### Node Status in Tray Menu

The tray menu shows node connection status:
- **🔌 Node Mode** section appears when enabled
- **⏳ Waiting for approval...** - Device needs approval on gateway
- **✅ Paired & Connected** - Ready to receive commands
- Click the device ID to copy it for the approval command

### Deep Links

OpenClaw registers the `openclaw://` URL scheme for automation and integration:

| Link | Description |
|------|-------------|
| `openclaw://settings` | Open Settings dialog |
| `openclaw://setup` | Open Setup Wizard |
| `openclaw://chat` | Open Web Chat window |
| `openclaw://commandcenter` | Open Command Center diagnostics |
| `openclaw://activity` | Open Activity Stream |
| `openclaw://history` | Open Notification History |
| `openclaw://dashboard` | Open Dashboard in browser |
| `openclaw://dashboard/sessions` | Open specific dashboard page |
| `openclaw://dashboard/channels` | Open Channels dashboard page |
| `openclaw://dashboard/skills` | Open Skills dashboard page |
| `openclaw://dashboard/cron` | Open Cron dashboard page |
| `openclaw://healthcheck` | Run a manual health check |
| `openclaw://check-updates` | Run a manual update check |
| `openclaw://logs` | Open the current tray log file |
| `openclaw://log-folder` | Open the logs folder |
| `openclaw://config` | Open the config folder |
| `openclaw://diagnostics` | Open the diagnostics JSONL folder |
| `openclaw://support-context` | Copy redacted support context |
| `openclaw://browser-setup` | Copy browser.proxy/browser-control setup guidance |
| `openclaw://port-diagnostics` | Copy gateway/browser/tunnel port diagnostics with owner PID stop hints |
| `openclaw://capability-diagnostics` | Copy permissions, allowlist, and parity diagnostics |
| `openclaw://node-inventory` | Copy node capabilities, commands, and policy status |
| `openclaw://restart-ssh-tunnel` | Restart the tray-managed SSH tunnel when enabled |
| `openclaw://send?message=Hello` | Open Quick Send with pre-filled text |
| `openclaw://agent?message=Hello` | Send message directly to the connected gateway |

Deep links work even when Molty is already running - they're forwarded via IPC.

## 📦 OpenClaw.CommandPalette

PowerToys Command Palette extension for quick OpenClaw access.

### Commands
- **🦞 Open Dashboard** - Launch the OpenClaw web dashboard
- **💬 Dashboard: Sessions** - Open the sessions dashboard
- **📡 Dashboard: Channels** - Open the channel configuration dashboard
- **🧩 Dashboard: Skills** - Open the skills dashboard
- **⏱️ Dashboard: Cron** - Open the scheduled jobs dashboard
- **💬 Web Chat** - Open the embedded Web Chat window
- **📝 Quick Send** - Open the Quick Send dialog to compose a message
- **🧭 Setup Wizard** - Open pairing/setup
- **🧭 Command Center** - Open diagnostics and support actions
- **🔄 Run Health Check** - Refresh connection health
- **⬇️ Check for Updates** - Run a manual GitHub Releases update check
- **⚡ Activity Stream** - Open recent activity
- **📋 Notification History** - Open notification history
- **⚙️ Settings** - Open the OpenClaw Tray Settings dialog
- **📄 Open Log File / 📁 Logs / 🗂️ Config / 🧪 Diagnostics** - Open support files and folders
- **📋 Copy Support Context** - Copy redacted Command Center metadata
- **🌐 Copy Browser Setup** - Copy browser.proxy and node-host setup guidance
- **🔌 Copy Port Diagnostics** - Copy gateway/browser/tunnel port owners and stop hints
- **🛡️ Copy Capability Diagnostics** - Copy permission, allowlist, and parity diagnostics
- **🖥️ Copy Node Inventory** - Copy node capabilities, commands, and policy status
- **🔁 Restart SSH Tunnel** - Restart the tray-managed SSH tunnel when enabled

### Installation
1. Run the OpenClaw Tray installer and tick **"Install PowerToys Command Palette extension"**, or
2. Register manually: `Add-AppxPackage -Register "$env:LOCALAPPDATA\OpenClawTray\CommandPalette\AppxManifest.xml" -ForceApplicationShutdown`
3. Open Command Palette (`Win+Alt+Space`) and type "OpenClaw" to see commands

See [docs/POWERTOYS.md](docs/POWERTOYS.md) for detailed setup and troubleshooting.

## 📦 OpenClaw.Shared

Shared library containing:
- `OpenClawGatewayClient` - WebSocket client for gateway protocol
- `IOpenClawLogger` - Logging interface
- Data models (SessionInfo, ChannelHealth, etc.)
- Channel control (start/stop channels via gateway)

## Development

### Project Structure
```
openclaw-windows-node/
├── src/
│   ├── OpenClaw.Shared/           # Shared gateway library
│   ├── OpenClaw.Tray.WinUI/       # System tray app (WinUI 3)
│   └── OpenClaw.CommandPalette/   # PowerToys extension
├── tests/
│   ├── OpenClaw.Shared.Tests/     # Shared library tests
│   └── OpenClaw.Tray.Tests/       # Tray app helper tests
├── docs/
│   └── molty1.png                 # Screenshot
├── moltbot-windows-hub.slnx       # Solution file (historical name)
├── README.md
├── LICENSE
└── .gitignore
```

### Configuration

Settings are stored in:
- Settings: `%APPDATA%\OpenClawTray\settings.json`
- Logs: `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log`

Default gateway: `ws://localhost:18789`

### First Run

On first run without a token, Molty displays a welcome dialog that:
1. Explains what's needed to get started
2. Links to [documentation](https://docs.molt.bot/web/dashboard) for token setup
3. Opens Settings to configure the connection

## License

MIT License - see [LICENSE](LICENSE)

---

*Formerly known as Moltbot, formerly known as Clawdbot*

