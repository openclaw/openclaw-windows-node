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
| **💬 Web Chat** | Opens the embedded Web Chat window in OpenClaw Tray |
| **📝 Quick Send** | Opens the Quick Send dialog to compose a message |
| **⚙️ Settings** | Opens the OpenClaw Tray Settings dialog |

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
  | Web Chat | `openclaw://chat` |
  | Quick Send | `openclaw://send` |
  | Settings | `openclaw://settings` |
