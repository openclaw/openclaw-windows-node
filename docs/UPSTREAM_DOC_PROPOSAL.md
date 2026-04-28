# Upstream Documentation Proposal

This document proposes documentation changes to the main OpenClaw repositories to reflect the Windows onboarding wizard.

## Problem

The main OpenClaw documentation does not reflect the Windows tray app (Molty) as a first-class onboarding path:

1. **`openclaw/openclaw/docs/platforms/windows.md`** states: _"We do not have a Windows companion app yet"_ — this is outdated.
2. **`openclaw/openclaw/docs/start/onboarding-overview.md`** lists only CLI and macOS app onboarding — Windows tray app is missing.
3. **docs.openclaw.ai** has no Windows-specific getting-started guide.

## Proposed Changes

### 1. Update `openclaw/openclaw/docs/platforms/windows.md`

**Remove** the statement: _"We do not have a Windows companion app yet"_

**Add** a new section:

```
## Windows Tray App (Molty)

The recommended way to run OpenClaw on Windows is with the **OpenClaw Tray App (Molty)**, which provides:
- System tray integration with connection status
- Guided onboarding wizard for first-time setup
- Embedded web chat with your agent
- Windows toast notifications
- PowerToys Command Palette integration
- Node mode for full agent capabilities (camera, screen capture, system commands)

Download from [GitHub Releases](https://github.com/openclaw/openclaw-windows-node/releases) or see the [Setup Guide](https://github.com/openclaw/openclaw-windows-node/blob/main/docs/SETUP.md).
```

### 2. Update `openclaw/openclaw/docs/start/onboarding-overview.md`

**Add** Windows Tray App as a third onboarding path:

```
### Windows Tray App (Molty)

If you're on Windows, install the OpenClaw Tray App for a guided onboarding experience:

1. Download the installer from [GitHub Releases](https://github.com/openclaw/openclaw-windows-node/releases)
2. Run the installer (no admin required)
3. The 6-screen onboarding wizard walks you through connection, configuration, permissions, and your first chat

See the [Windows Setup Guide](https://github.com/openclaw/openclaw-windows-node/blob/main/docs/SETUP.md) for detailed instructions.
```

### 3. Propose new `openclaw/openclaw/docs/start/onboarding-windows.md`

A dedicated page mirroring `onboarding.md` (macOS) but for Windows, covering:
- Prerequisites (Windows 10/11, WebView2)
- Installer download and architecture selection
- Onboarding wizard walkthrough (all 6 screens)
- Post-setup: tray menu, web chat, settings

## Implementation

These changes should be submitted as a PR to `openclaw/openclaw` after the onboarding wizard PR is merged to `openclaw-windows-node`. The Windows tray app should have a released version before updating upstream docs.

## Timeline

1. Merge onboarding wizard PR to `openclaw-windows-node`
2. Create a release with the onboarding wizard included
3. Submit upstream documentation PR to `openclaw/openclaw`
