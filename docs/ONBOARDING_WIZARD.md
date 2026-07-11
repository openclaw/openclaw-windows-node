# Onboarding Wizard

The onboarding wizard installs a managed local gateway natively on Windows or in an app-owned WSL 2 distro, then runs OpenClaw onboard.

## Overview

On first launch, the wizard appears only when there is no usable saved gateway connection. Users choose the recommended WSL 2 runtime, the simpler native Windows runtime, or an existing gateway. Existing connections remain managed from the tray app's Connections tab.

The setup flow walks users through:

1. **Security notice** — Device-trust warning before setup choices
2. **Welcome / Advanced** — Choose native Windows, app-owned WSL, or an existing gateway
3. **Capabilities** — Recommended profile, inline Windows permission status, and install review
4. **Local setup progress** — Native Windows or fresh app-owned `OpenClawGateway` WSL installation
5. **Gateway installed** — Explicit handoff from infrastructure setup to OpenClaw onboard
6. **OpenClaw onboard** — Gateway-driven provider/model/key configuration
7. **All set** — Feature summary, startup preference, and completion

The setup flow no longer configures remote/manual gateways inline. The Welcome page's **Connect to an existing gateway** option routes through `AdvancedSetupPage`, closes setup, and opens the tray app's Connections tab.

## Screen Details

### Welcome
Displays the OpenClaw icon, app title, and three explicit choices. **Install in WSL** is recommended for the safest local boundary and uses an isolated Ubuntu WSL 2 instance. **Install on Windows (native)** is the simpler setup path, but runs directly in the Windows user context. **Connect to an existing gateway** hands off to Connections. Local install choices continue to the capabilities review.

### Local setup progress
Native mode installs the pinned OpenClaw CLI through the official HTTPS PowerShell installer into an app-owned LocalAppData prefix, configures an isolated `OpenClawGateway` profile, and installs its per-user Windows Scheduled Task. Existing global OpenClaw packages, wrappers, PATH entries, and the default profile are preserved. WSL mode installs and connects a new app-owned `OpenClawGateway` instance from a clean WSL baseline. It does not export from or mutate an existing user Ubuntu distro; if WSL cannot create the named app-owned distro directly, setup fails with an actionable update message.

Switching modes stops the previous local gateway before claiming the loopback port. Switching to native preserves the app-owned WSL distro files. Switching to WSL removes the native gateway Scheduled Task so it cannot restart and conflict, while a failed switch restores the previous native service during rollback.

The managed distro is locked down and is not intended to be a normal interactive Ubuntu profile. For editing `openclaw.json` as the `openclaw` user and using root for protected-file administration, see [Managing the locked-down WSL gateway](WSL_GATEWAY_ADMIN.md).

### Capabilities and Windows permissions

The Capabilities page applies the selected profile to both setup config and runtime `Node*` settings. Inline Windows permission rows are shown only for capabilities that need OS-level state (camera, microphone, location, screen capture). Notifications are always shown as an app-level permission. Screen capture is passive: Windows asks what to share each capture through the Graphics Capture picker.

### OpenClaw onboard

After OpenClaw onboard completes—or when the user explicitly skips it—WSL setup runs the pinned gateway CLI's non-interactive baseline initializer against the final runtime workspace, then writes fixed Windows-node guidance into a setup-owned managed section of that workspace's `AGENTS.md`. The section is replaced idempotently between markers, preserves user-authored `AGENTS.md` content and file permissions outside those markers, and does not modify OpenClaw source files. Native setup skips this WSL-specific workspace injection. In both modes, the tray app registers the Windows node capabilities selected during onboarding.

Renders server-defined setup steps via RPC (`wizard.start` / `wizard.next`). The gateway controls the flow — steps can be:
- **Note** — informational messages
- **Confirm** — yes/no decisions
- **Text** — free-form input (with PasswordBox for sensitive fields like API keys)
- **Select** — radio button choices (e.g., AI provider selection)
- **Progress** — loading indicator for background operations

If the gateway doesn't support the wizard protocol or is unreachable, this screen shows an "offline" message and can be skipped.

The wizard keeps recovery choices visible while setup steps are running so users can start the wizard again or skip it for now if an auth flow stalls. If the gateway restarts or the wizard connection is lost while setup is running, the same recovery choices are presented in the error state so the user is not trapped retrying a broken session.

When the gateway config wizard surfaces an error and the active gateway is an app-managed WSL distro, the error state also offers **Open terminal** and **Restart gateway**. The wizard does not parse or classify the gateway's error text; it leaves the message visible and selectable so the user can copy any command the gateway reports. The buttons reuse the shared `GatewayTerminalLauncher` and `WslGatewayController` (in `OpenClaw.Connection`, also used by the Connections tab). Restart re-enters the gateway config wizard (the provider/model onboarding step — not the whole V2 onboarding, and without re-installing the WSL distro) so fixes such as newly-installed tools are picked up on `PATH`. Because the gateway restart clears its wizard session, this resumes at the first config question rather than the exact step that failed. Detection is gated on `GatewayRecord.SetupManagedDistroName`, so it never appears for remote/SSH gateways.

### All set
Displays a completion summary, a Launch at startup toggle, and a Finish button that saves the startup preference before restarting the tray. Launch at startup defaults on so OpenClaw is ready after reboot.

## Security

The onboarding wizard follows these security practices:

- **Input validation**: Setup codes limited to 2KB, decoded JSON validated, gateway URLs checked via `GatewayUrlHelper`
- **URI scheme whitelists**: Only `ms-settings:` for permissions and `http/https` for browser-launch links
- **Token protection**: Query params stripped from all log output
- **Gateway-owned pairing**: Device approval uses the gateway CLI/API path so scope checks, token issuance, audit, and broadcasts stay centralized
- **Error sanitization**: Exception details logged but not shown to users

## Credential Storage

Gateway credentials are registry-backed. Setup codes and QR payloads create or update a `GatewayRecord`; bootstrap credentials live in `GatewayRecord.BootstrapToken`, long-lived manual tokens live in `GatewayRecord.SharedGatewayToken`, and post-pairing device tokens are saved in the per-gateway identity directory. `SettingsManager` may read legacy `Token` / `BootstrapToken` JSON fields for migration, but it does not write them back.

## Localization

All user-visible strings use `LocalizationHelper.GetString()` with the `Onboarding_*` key namespace. Supported languages are discovered from the `Strings/<locale>/Resources.resw` directories; the current locales are English, French, Dutch, Chinese Simplified, and Chinese Traditional.

Translations are AI-generated following the repo convention. Technical terms (Gateway, Token, Node Mode) are kept in English across all locales.

## Developer Guide

See [DEVELOPMENT.md](../DEVELOPMENT.md#developing--testing-the-onboarding-wizard) for build instructions, environment variables, and testing workflow.

### Test Isolation

`SettingsManager` loads `%APPDATA%\OpenClawTray\settings.json` by default. Onboarding tests must not use `new SettingsManager()` without an isolated settings directory, because local user settings such as `EnableNodeMode=true` change setup behavior.

Use a temp settings directory for tests that construct `SettingsManager`, or set `OPENCLAW_TRAY_DATA_DIR` before the test process starts.

### Key Files

| Path | Purpose |
|------|---------|
| `src/OpenClaw.SetupEngine.UI/SetupWindow.xaml(.cs)` | Tray-hosted setup shell, run lock, preview routing, and page navigation |
| `src/OpenClaw.SetupEngine.UI/Pages/SecurityNoticePage.xaml(.cs)` | First-run device-trust warning before setup choices |
| `src/OpenClaw.SetupEngine.UI/Pages/WelcomePage.xaml(.cs)` | Native Windows, WSL, or connect-existing choice |
| `src/OpenClaw.SetupEngine.UI/Pages/AdvancedSetupPage.xaml(.cs)` | Connect-existing handoff to Connection settings |
| `src/OpenClaw.SetupEngine.UI/Pages/CapabilitiesPage.xaml(.cs)` | Capability profile, inline Windows permission status, and install review |
| `src/OpenClaw.SetupEngine.UI/Pages/ProgressPage.xaml(.cs)` | Mode-aware local gateway install progress and gateway-installed handoff |
| `src/OpenClaw.SetupEngine.UI/Pages/WizardPage.xaml(.cs)` | OpenClaw onboard provider/model/key wizard driven by gateway `wizard.*` frames |
| `src/OpenClaw.SetupEngine.UI/Pages/CompletePage.xaml(.cs)` | Success, failure, log/help, and startup preference summary |
| `src/OpenClaw.SetupEngine.UI/Pages/SetupPermissionHelper.cs` | Passive Windows permission checks and inline permission rows |
| `src/OpenClaw.Connection/GatewayRegistry.cs` | Persistent gateway records and migration target |
| `src/OpenClaw.Connection/GatewayConnectionManager.cs` | Operator/node connection lifecycle used by onboarding |
| `src/OpenClaw.Tray.WinUI/Services/SetupExistingGatewayClassifier.cs` | Existing gateway classification for Welcome and startup gating |
