# OpenClaw Windows Hub

![OpenClaw Windows Node banner](docs/assets/readme-banner.jpg)

[![CI](https://img.shields.io/github/actions/workflow/status/openclaw/openclaw-windows-node/ci.yml?branch=main&style=flat-square&label=ci)](https://github.com/openclaw/openclaw-windows-node/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Discord](https://img.shields.io/discord/1456350064065904867?label=discord&logo=discord&logoColor=white&color=5865F2&style=flat-square)](https://discord.gg/clawd)

The native Windows companion for [OpenClaw](https://github.com/openclaw/openclaw). Connect your PC to your gateway and let the agent run commands, capture your screen, present UI, and speak aloud, all permission-gated and sandboxed.

[Download](https://docs.openclaw.ai/platforms/windows) · [Docs](https://docs.openclaw.ai/platforms/windows) · [Setup Guide](docs/SETUP.md) · [Discord](https://discord.gg/clawd)

---

## Install

| Architecture | Installer |
|---|---|
| x64 | [OpenClawCompanion-Setup-x64.exe](https://github.com/openclaw/openclaw-windows-node/releases/latest/download/OpenClawCompanion-Setup-x64.exe) |
| ARM64 | [OpenClawCompanion-Setup-arm64.exe](https://github.com/openclaw/openclaw-windows-node/releases/latest/download/OpenClawCompanion-Setup-arm64.exe) |

[Checksums](https://github.com/openclaw/openclaw-windows-node/releases/latest/download/OpenClawCompanion-SHA256SUMS.txt) · Windows 10 20H2+ or Windows 11 · No build required.

On first launch, a setup wizard walks you through connecting to an existing gateway or installing one locally in WSL. No gateway yet? Choose "Set up locally" and the wizard handles everything.

---

## Node Mode

Once paired with your gateway, the agent can act on your PC through these capabilities:

| | What the agent can do |
|---|---|
| **Run commands** | Execute shell commands, scripts, and tools (`system.run`) |
| **Show things** | Toast notifications, WebView2 canvas windows, A2UI rendering |
| **See your screen** | Screenshots and short screen recordings |
| **Use your camera** | List cameras, take photos, record short clips |
| **Speak** | Text-to-speech via Windows SAPI or ElevenLabs |
| **Know context** | Device info, geolocation, microphone transcription |

### Connect in 3 steps

1. **Enable Node Mode** in the app settings (on by default)
2. **Approve the device** on your gateway: `openclaw devices approve <id>`
3. **Allow capabilities** on your gateway: `openclaw nodes allow <id> system.run canvas.present screen.capture`

That's it. The agent can now use your PC. See [Node Concepts](docs/OPERATOR_NODE_CONCEPTS.md) for the full pairing and approval model, and [Windows Node Testing](docs/WINDOWS_NODE_TESTING.md) for the capabilities reference.

---

## Sandboxing

Every command the agent runs on your PC goes through **MXC process isolation**. You control what the sandbox allows:

- **Files** — per-folder grants (Documents, Downloads, Desktop, custom paths). SSH keys and browser profiles are always blocked.
- **Network** — internet on/off. LAN is always blocked.
- **Clipboard** — none, read, write, or both.
- **Limits** — per-command timeout and output cap.

Choose a preset (Locked Down, Recommended, Unprotected) or configure each control individually. See [Sandboxing docs](https://docs.openclaw.ai/gateway/sandboxing) for details.

---

## Features

- 💬 **Native chat** — WebView2 chat UI and Quick Send hotkey (Ctrl+Alt+Shift+C)
- 🧭 **Command Center** — diagnostics hub for sessions, nodes, channels, and usage
- 🔔 **Toast notifications** — clickable Windows notifications with smart categorization
- 🔄 **Auto-updates** — background updates from GitHub Releases
- 🔗 **Deep links** — `openclaw://` URL scheme for automation
- 📡 **Local MCP server** — Model Context Protocol endpoint for tool integration
- 🎯 **First-run setup** — guided WSL gateway install with permissions and onboarding

---

## For contributors

### Projects

| Project | What it is |
|---|---|
| **OpenClaw.Tray.WinUI** | System tray app (WinUI 3) |
| **OpenClaw.Connection** | Gateway registry and connection manager |
| **OpenClaw.Shared** | Gateway client, capabilities, MCP bridge |
| **OpenClaw.Chat** | Chat model and timeline reducer |
| **OpenClaw.WinNode.Cli** | `winnode` CLI for local node/MCP invocation |
| **OpenClaw.SetupEngine** | WSL gateway setup and setup-code pairing |
| **OpenClaw.SetupEngine.UI** | WinUI setup wizard pages |
| **OpenClaw.Cli** | CLI WebSocket validator |
| **OpenClawTray.FunctionalUI** | Declarative WinUI helpers |

### Prerequisites

```powershell
.\scripts\setup-dev.ps1                # Install missing prerequisites (winget, .NET, etc.)
.\scripts\setup-dev.ps1 -CheckOnly     # Verify without installing
.\scripts\setup-dev.ps1 -RunValidation # Install + run full build/test validation
```

### Build

```powershell
.\build.ps1                            # Build all projects
.\build.ps1 -Project WinUI            # Build only the tray app
.\build.ps1 -CheckOnly                # Check prerequisites without building
```

Or build directly with `dotnet` (note: WinUI requires a runtime identifier):

```powershell
dotnet build src/OpenClaw.Tray.WinUI -r win-x64     # x64
dotnet build src/OpenClaw.Tray.WinUI -r win-arm64   # ARM64
dotnet build src/OpenClaw.Tray.WinUI -r win-x64 -p:PackageMsix=true  # MSIX package
```

### Run

```powershell
.\run-app-local.ps1                    # Build and launch
.\run-app-local.ps1 -NoBuild          # Launch existing build (skip rebuild)
.\run-app-local.ps1 -Isolated         # Separate settings per worktree
.\run-app-local.ps1 -Dev -Isolated    # Side-by-side dev identity (own mutex, port, distro)
.\run-app-local.ps1 -Configuration Release -Isolated -UpdateChannel alpha  # Test updates
```

### Test

```powershell
dotnet test tests/OpenClaw.Shared.Tests
dotnet test tests/OpenClaw.Tray.Tests
```

### Docs

| Topic | Link |
|---|---|
| Connection architecture | [docs/CONNECTION_ARCHITECTURE.md](docs/CONNECTION_ARCHITECTURE.md) |
| Onboarding wizard | [docs/ONBOARDING_WIZARD.md](docs/ONBOARDING_WIZARD.md) |
| WSL gateway admin | [docs/WSL_GATEWAY_ADMIN.md](docs/WSL_GATEWAY_ADMIN.md) |
| Development | [DEVELOPMENT.md](DEVELOPMENT.md) |

**User-facing docs** (also linked above): [Setup](docs/SETUP.md) · [Node Concepts](docs/OPERATOR_NODE_CONCEPTS.md) · [Windows Node Testing](docs/WINDOWS_NODE_TESTING.md) · [MCP Mode](docs/MCP_MODE.md)

---

## License

[MIT](LICENSE)

*Made with 🦞 by Scott Hanselman, Molty, and contributors*
