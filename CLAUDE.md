# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

OpenClaw Windows Hub — a Windows companion suite for the OpenClaw AI assistant. It is a .NET 10 / WinUI 3 monorepo targeting Windows 10/11 that puts an AI-connected system tray icon on the user's taskbar, runs an embedded WebView2 chat panel, and communicates with a local OpenClaw gateway over WebSocket.

## Build Commands

```powershell
# Check prerequisites and build all projects
.\build.ps1

# Build a specific project (handles WinUI platform requirements)
.\build.ps1 -Project WinUI

# Or build directly with dotnet (Shared library only — cross-platform safe)
dotnet build src/OpenClaw.Shared

# Build WinUI on Linux (CI cross-compile)
dotnet build -p:EnableWindowsTargeting=true
```

> Direct `dotnet build` on the WinUI project without `build.ps1` (or `-p:EnableWindowsTargeting=true`) will fail with "WindowsAppSDKSelfContained requires a supported Windows architecture".

## Testing

```bash
# Run all unit tests (excludes E2E, which is CI-only)
dotnet test

# Run with verbose output
dotnet test --verbosity detailed

# Filter to a specific class
dotnet test --filter "FullyQualifiedName~AgentActivityTests"
```

Tests are pure unit tests — no network, no filesystem, no external dependencies required. There are ~1570 tests across `OpenClaw.Shared.Tests` and `OpenClaw.Tray.Tests`.

## Publishing & Installer Iteration

```powershell
# Self-contained release build
dotnet publish src/OpenClaw.Tray.WinUI -c Release -r win-x64 --self-contained -o publish

# Fast local Inno installer (good for smoke tests in Windows Sandbox)
.\scripts\build-inno-local.ps1 -Arch x64 -Fast

# Recompile Inno only (after editing installer.iss)
.\scripts\build-inno-local.ps1 -Arch x64 -Fast -NoPublish
```

## Architecture

### Project Map

| Project | Role |
|---------|------|
| `OpenClaw.Shared` | WebSocket gateway client, models, logging interface — cross-platform, no Windows dependency |
| `OpenClaw.Chat` | Chat model (`ChatThread`, `ChatTimelineState`) and pure reducer (`ChatTimelineReducer`) — no UI |
| `OpenClawTray.FunctionalUI` | Minimal declarative WinUI helper (components, hooks, virtual element tree) — used by onboarding and chat surface |
| `OpenClaw.Tray.WinUI` | Main application: tray icon, windows, settings, notifications, WebView2 chat |
| `OpenClaw.SetupEngine` | Guided install/repair/uninstall engine for the WSL gateway |
| `OpenClaw.Cli` / `OpenClaw.WinNode.Cli` | CLI utilities for WebSocket diagnostics |

Dependency direction: `OpenClaw.Tray.WinUI` → `OpenClaw.Shared` ← tests.

### Gateway WebSocket Protocol

`OpenClaw.Shared/OpenClawGatewayClient.cs` owns the connection lifecycle:
1. Connect to `ws://localhost:18789` (or configured URL)
2. Receive `challenge` → respond with auth token
3. Receive `connected` → start receiving typed events
4. Reconnect with exponential backoff: 1s → 2s → 4s → 8s → 15s → 30s → 60s

Key event types handled: `agent` (job/tool streams), `chat`, `health`, `session`, `usage`.

### Native Chat Surface (FunctionalUI + OpenClaw.Chat)

The chat surface is rendered with native WinUI 3 controls, not WebView2 (WebView2 remains a settings-controlled fallback).

Layering:
```
OpenClaw.Tray.WinUI/Chat/   OpenClawChatTimeline · OpenClawComposer · OpenClawSessionHeader
                             OpenClawChatDataProvider  (adapts GatewayClient → IChatDataProvider)
                             OpenClawChatRoot           (FunctionalUI component tree)
        ▲
OpenClaw.Chat/               ChatThread · ChatTimelineState · IChatDataProvider · ChatTimelineReducer
        ▲
OpenClawTray.FunctionalUI/   Component · RenderContext · FunctionalHostControl
```

One `OpenClawChatDataProvider` lives on `App` (`App.ChatProvider`) and is shared by both the Hub Chat tab and the tray ChatWindow — both surfaces show identical state. Provider events are marshalled to the UI thread via `DispatcherQueue.AsPost()`.

**Adding new chat behavior:** add events to `ChatEvent` in `OpenClaw.Chat`, handle them in `ChatTimelineReducer`, emit them from `OpenClawChatDataProvider`.

### GDI Handle Pattern

When creating tray icons, always follow Create → Clone → DestroyIcon to avoid GDI handle leaks (limit: ~10 000 per process):
```csharp
var hIcon = bitmap.GetHicon();
var icon = Icon.FromHandle(hIcon);
var result = (Icon)icon.Clone();
DestroyIcon(hIcon);   // ← required
return result;
```

### Build Policies

- `TreatWarningsAsErrors=true` for all product projects (set in `src/Directory.Build.props`)
- `NuGetAuditMode=all` — NuGet restore audits transitive dependencies for CVEs

## Key Environment Variables (Dev/Testing)

| Variable | Effect |
|----------|--------|
| `OPENCLAW_FORCE_ONBOARDING=1` | Show onboarding wizard even when a token exists |
| `OPENCLAW_SKIP_UPDATE_CHECK=1` | Skip update dialog |
| `OPENCLAW_LANGUAGE=fr-fr` | Override UI language (valid: en-us, fr-fr, nl-nl, zh-cn, zh-tw) |
| `OPENCLAW_GATEWAY_PORT=19001` | Override default gateway port |
| `OPENCLAW_VISUAL_TEST=1` | Capture screenshots on wizard page transitions |
| `OPENCLAW_VISUAL_TEST_DIR=path` | Output directory for those screenshots |

## Log Locations

- Tray app log: `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` (rotates at 5 MB)
- Setup summary: `%LOCALAPPDATA%\OpenClawTray\Logs\Setup\easy-setup-latest.txt`
- Setup traces: `%LOCALAPPDATA%\OpenClawTray\Logs\Setup\setup-*.jsonl`

## CI

CI runs on `windows-latest` and builds both `win-x64` and `win-arm64`. Tests run via `dotnet test tests/OpenClaw.Shared.Tests` and `dotnet test tests/OpenClaw.Tray.Tests`. Code signing happens only on tag releases via Azure Trusted Signing. There is no configured linter.

The pinned gateway LKG version lives in `src/OpenClaw.SetupEngine/GatewayLkgVersion.cs`. A standing automation workflow (`gateway-lkg-update.yml`) keeps a draft PR open on `automation/gateway-lkg-update` to track upstream drift.
