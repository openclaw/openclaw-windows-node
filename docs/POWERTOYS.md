# PowerToys Command Palette — OpenClaw Extension

The OpenClaw Command Palette extension integrates with [PowerToys Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette) to give you fast keyboard-driven access to OpenClaw from anywhere on your desktop.

## Prerequisites

- [PowerToys](https://github.com/microsoft/PowerToys) installed (v0.90 or later recommended — this is the version that shipped Command Palette).
- OpenClaw Tray (Molty) installed and configured.

## Installation

### Via the OpenClaw Installer (recommended)

When running the OpenClaw Tray installer, tick the **"Install PowerToys Command Palette extension"** checkbox. The installer will register the extension automatically.

### Manual Registration

If you installed without the Command Palette option, or need to re-register after a repair:

1. Open **PowerShell** (no admin needed).
2. Run:

   ```powershell
   Add-AppxPackage -Register "$env:LOCALAPPDATA\OpenClawTray\CommandPalette\AppxManifest.xml" -ForceApplicationShutdown
   ```

3. Restart PowerToys if it was running.

### Verifying Registration

Open Command Palette (`Win+Alt+Space`), type **"OpenClaw"** — you should see the OpenClaw commands appear.

## Available Commands

| Command | Action |
|---------|--------|
| **🦞 Open Dashboard** | Opens the OpenClaw web dashboard in your default browser |
| **💬 Dashboard: Sessions** | Opens the sessions dashboard |
| **📡 Dashboard: Channels** | Opens the channel configuration dashboard |
| **🧩 Dashboard: Skills** | Opens the skills dashboard |
| **⏱️ Dashboard: Cron** | Opens the scheduled jobs dashboard |
| **💬 Web Chat** | Opens the embedded Chat page in OpenClaw Tray |
| **📝 Quick Send** | Opens the Quick Send dialog to compose a message |
| **🧭 Setup Wizard** | Opens QR, setup code, and manual gateway pairing |
| **🧭 Command Center** | Opens gateway, tunnel, node, browser, and support diagnostics |
| **🔄 Run Health Check** | Refreshes gateway or node connection health |
| **⬇️ Check for Updates** | Runs a manual GitHub Releases update check |
| **⚡ Activity Stream** | Opens recent tray activity and support bundle actions |
| **📋 Notification History** | Opens recent OpenClaw tray notifications in the Activity page |
| **⚙️ Settings** | Opens the OpenClaw Tray Settings page |
| **📄 Open Log File** | Opens the current OpenClaw Tray log |
| **📁 Open Logs Folder** | Opens the OpenClaw Tray logs folder |
| **🗂️ Open Config Folder** | Opens the OpenClaw Tray configuration folder |
| **🧪 Open Diagnostics Folder** | Opens the diagnostics JSONL folder |
| **📋 Copy Support Context** | Copies redacted Command Center support metadata |
| **🧰 Copy Debug Bundle** | Copies combined support, port, capability, node, channel, and activity diagnostics |
| **🌐 Copy Browser Setup** | Copies browser.proxy and node-host setup guidance |
| **🔌 Copy Port Diagnostics** | Copies gateway/browser/tunnel port owners and stop hints |
| **🛡️ Copy Capability Diagnostics** | Copies permission, allowlist, and parity diagnostics |
| **🖥️ Copy Node Inventory** | Copies node capabilities, commands, and policy status |
| **📡 Copy Channel Summary** | Copies channel health and start/stop availability |
| **⚡ Copy Activity Summary** | Copies recent tray activity |
| **🧩 Copy Extensibility Summary** | Copies channel, skills, and cron surface guidance |
| **🔁 Restart SSH Tunnel** | Restarts the tray-managed SSH tunnel when enabled |

## Usage

1. Press `Win+Alt+Space` to open Command Palette.
2. Type `OpenClaw` (or just `oc`) to filter to OpenClaw commands.
3. Select the action with arrow keys and press `Enter`.

Commands are also surfaced as deep links — you can invoke them from a browser or script using `openclaw://` URIs (see [SETUP.md](./SETUP.md#deep-links)).

## Troubleshooting

### OpenClaw commands don't appear in Command Palette

1. Make sure PowerToys Command Palette is enabled: **PowerToys Settings → Command Palette → Enable Command Palette**.
2. Try re-registering the extension (see [Manual Registration](#manual-registration) above).
3. Restart PowerToys after registration.
4. Check that the extension files exist at `%LOCALAPPDATA%\OpenClawTray\CommandPalette\`.

### Commands appear but do nothing

The extension communicates with OpenClaw Tray via `openclaw://` deep links. Make sure:
- OpenClaw Tray (`OpenClaw.Tray.WinUI.exe`) is running.
- The `openclaw://` URI scheme is registered. If not, re-run the OpenClaw Tray installer.

### Extension was removed after a PowerToys update

PowerToys updates can sometimes unregister third-party extensions. Re-register with:

```powershell
Add-AppxPackage -Register "$env:LOCALAPPDATA\OpenClawTray\CommandPalette\AppxManifest.xml" -ForceApplicationShutdown
```

### Unregistering the extension

To remove the OpenClaw extension from Command Palette without uninstalling Tray:

```powershell
Get-AppxPackage -Name '*OpenClaw*' | Remove-AppxPackage
```

## Notes

- The extension is a **sparse MSIX package** registered per-user, so no administrator rights are required.
- It is built against the `Microsoft.CommandPalette.Extensions` SDK and communicates with Tray exclusively via `openclaw://` deep links — there is no direct IPC between the extension and Tray.
- Command Palette extension commands and their deep link targets:

  | Command | Deep link |
  |---------|-----------|
  | Open Dashboard | `openclaw://dashboard` |
  | Dashboard: Sessions | `openclaw://dashboard/sessions` |
  | Dashboard: Channels | `openclaw://dashboard/channels` |
  | Dashboard: Skills | `openclaw://dashboard/skills` |
  | Dashboard: Cron | `openclaw://dashboard/cron` |
  | Web Chat | `openclaw://chat` |
  | Quick Send | `openclaw://send` |
  | Setup Wizard | `openclaw://setup` |
  | Command Center | `openclaw://commandcenter` |
  | Run Health Check | `openclaw://healthcheck` |
  | Check for Updates | `openclaw://check-updates` |
  | Activity Stream | `openclaw://activity` |
  | Notification History | `openclaw://history` |
  | Settings | `openclaw://settings` |
  | Open Log File | `openclaw://logs` |
  | Open Logs Folder | `openclaw://log-folder` |
  | Open Config Folder | `openclaw://config` |
  | Open Diagnostics Folder | `openclaw://diagnostics` |
  | Copy Support Context | `openclaw://support-context` |
  | Copy Debug Bundle | `openclaw://debug-bundle` |
  | Copy Browser Setup | `openclaw://browser-setup` |
  | Copy Port Diagnostics | `openclaw://port-diagnostics` |
  | Copy Capability Diagnostics | `openclaw://capability-diagnostics` |
  | Copy Node Inventory | `openclaw://node-inventory` |
  | Copy Channel Summary | `openclaw://channel-summary` |
  | Copy Activity Summary | `openclaw://activity-summary` |
  | Copy Extensibility Summary | `openclaw://extensibility-summary` |
  | Restart SSH Tunnel | `openclaw://restart-ssh-tunnel` |
