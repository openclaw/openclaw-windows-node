# Onboarding Wizard

The onboarding wizard is a guided 6-screen setup experience for new Windows users, matching the macOS onboarding flow.

## Overview

On first launch (or when no gateway token is configured), the wizard walks users through:

1. **Welcome** — Greeting and introduction
2. **Connection** — Gateway selection and authentication
3. **Wizard** — Gateway-driven configuration (AI provider, personality, channels)
4. **Permissions** — Windows system permission review
5. **Chat** — First conversation with the agent
6. **Ready** — Feature summary and completion

The wizard adapts based on the connection mode:
- **Local gateway**: All 6 screens (including Wizard for gateway configuration)
- **Remote gateway**: Skips Wizard (assumes gateway is pre-configured)
- **Configure Later**: Minimal flow — Welcome → Connection → Chat → Ready

## Screen Details

### Welcome
Displays the OpenClaw lobster icon, app title, and a brief description. Single "Get Started" button advances to Connection.

### Connection
Three connection modes via radio buttons:
- **Local** — Pre-fills `ws://localhost:18789` for a gateway running on the same machine or in WSL
- **Remote** — Enter a gateway URL and bootstrap token, or paste a base64url-encoded setup code
- **Later** — Skip connection for now; configure from the tray menu after setup

Connection testing performs a real WebSocket handshake with Ed25519 device authentication. Status feedback shows connecting, connected, pairing required, token mismatch, or timeout.

For local gateways, device approval is handled automatically by writing to the WSL gateway's `devices/paired.json`. For remote gateways, the wizard displays the CLI approval command.

### Wizard
Renders server-defined setup steps via RPC (`wizard.start` / `wizard.next`). The gateway controls the flow — steps can be:
- **Note** — informational messages
- **Confirm** — yes/no decisions
- **Text** — free-form input (with PasswordBox for sensitive fields like API keys)
- **Select** — radio button choices (e.g., AI provider selection)
- **Progress** — loading indicator for background operations

If the gateway doesn't support the wizard protocol or is unreachable, this screen shows an "offline" message and can be skipped.

### Permissions
Checks 5 Windows permissions using native APIs and registry:
- Notifications (Toast capability)
- Camera (Windows.Devices.Enumeration)
- Microphone (Windows.Devices.Enumeration)
- Screen Capture (Graphics.Capture)
- Location (optional, registry-based)

Each permission shows its current status (Enabled/Disabled/Allowed/Denied) with an "Open Settings" button linking to the relevant `ms-settings:` URI.

### Chat
Embeds the gateway's web chat UI via WebView2, matching the post-setup `WebChatWindow` for visual consistency. Uses the shared `GatewayChatHelper` for URL building and WebView2 initialization.

On first load, a bootstrap message is auto-injected to kick off the gateway's first-run ritual (BOOTSTRAP.md). The message is safely encoded using `JsonSerializer.Serialize` to prevent XSS.

### Ready
Displays 5 feature cards (Tray Menu, Channels, Voice, Canvas, Skills) with localized subtitles. Includes a "Launch at Login" toggle and a "Finish" button that saves settings and closes the wizard.

## Security

The onboarding wizard follows these security practices:

- **XSS prevention**: Bootstrap messages encoded via `JsonSerializer.Serialize` for safe JS injection
- **Input validation**: Setup codes limited to 2KB, decoded JSON validated, gateway URLs checked via `GatewayUrlHelper`
- **URI scheme whitelists**: Only `ms-settings:` for permissions, `http/https` for chat
- **Navigation restriction**: WebView2 `NavigationStarting` handler blocks navigation to external origins
- **Token protection**: Query params stripped from all log output; WebView2 accelerator keys disabled
- **Path traversal prevention**: WSL paths validated against expected prefix; `..` and null bytes rejected
- **Atomic file writes**: Device pairing uses temp→rename pattern to prevent corruption
- **Error sanitization**: Exception details logged but not shown to users

## Localization

All user-visible strings use `LocalizationHelper.GetString()` with the `Onboarding_*` key namespace. Supported languages: English, French, Dutch, Chinese Simplified, Chinese Traditional.

Translations are AI-generated following the repo convention. Technical terms (Gateway, Token, Node Mode) are kept in English across all locales.

## Developer Guide

See [DEVELOPMENT.md](../DEVELOPMENT.md#developing--testing-the-onboarding-wizard) for build instructions, environment variables, and testing workflow.

### Key Files

| Path | Purpose |
|------|---------|
| `Onboarding/OnboardingWindow.cs` | Host window with WebView2 overlay |
| `Onboarding/OnboardingApp.cs` | Functional UI root component, page navigation |
| `Onboarding/Services/OnboardingState.cs` | Shared state across all pages |
| `Onboarding/Pages/*.cs` | Individual wizard screens |
| `Onboarding/Services/SetupCodeDecoder.cs` | Base64url setup code parsing |
| `Onboarding/Services/InputValidator.cs` | Security input validation |
| `Onboarding/Services/WizardStepParser.cs` | Wizard JSON step parsing |
| `Onboarding/Services/LocalGatewayApprover.cs` | WSL device auto-approval |
| `Onboarding/Services/PermissionChecker.cs` | Windows permission checks |
| `Helpers/GatewayChatHelper.cs` | Shared WebView2 chat URL builder |
