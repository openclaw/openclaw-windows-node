# Setup Engine — Architecture & Reference

## Overview

The Setup Engine is a **standalone, config-driven system** for provisioning an OpenClaw WSL gateway from scratch. It consists of two projects:

1. **`OpenClaw.SetupEngine`** — Headless pipeline (console exe). Runs 16 steps sequentially with full JSONL logging, transaction journal, and rollback support.
2. **`OpenClaw.SetupEngine.UI`** — WinUI3 app that wraps the same pipeline with a 5-page fluent wizard UI.

Both require a JSON config file to run — there are **no hardcoded defaults** anywhere. A bundled `default-config.json` ships with each exe.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  OpenClaw.SetupEngine (net10.0, console exe)                │
│                                                             │
│  SetupPipeline ──→ 16 SetupStep classes ──→ StepResult      │
│       │                    │                                │
│  SetupContext         CommandRunner (WSL + Process)          │
│  SetupConfig          TransactionJournal (JSONL)            │
│  SetupLogger          RetryExecutor                         │
│                                                             │
│  refs: OpenClaw.Connection, OpenClaw.Shared                 │
└─────────────────────────────────────────────────────────────┘
         ▲ callback: Action<string, StepStatus>
         │
┌─────────────────────────────────────────────────────────────┐
│  OpenClaw.SetupEngine.UI (net10.0-windows10.0.22621, WinUI3)│
│  5 pages, direct code-behind, no MVVM                       │
│  Welcome → Capabilities → Progress → Permissions → Complete │
└─────────────────────────────────────────────────────────────┘
```

---

## Project Structure

```
src/OpenClaw.SetupEngine/
├── OpenClaw.SetupEngine.csproj    # net10.0, console exe
├── Program.cs                     # CLI entry: --config, --headless, --dry-run, --rollback-on-failure
├── SetupPipeline.cs               # Sequential step orchestrator (132 lines)
├── SetupContext.cs                # Config model + shared state bag (217 lines)
├── SetupSteps.cs                  # All 16 step implementations (1222 lines)
├── TransactionJournal.cs          # Append-only JSONL journal (77 lines)
├── SetupLogger.cs                 # Structured JSONL logger (112 lines)
├── CommandRunner.cs               # WslCommandRunner + ProcessRunner (122 lines)
├── RetryExecutor.cs               # Exponential backoff retry
├── StubNodeCapability.cs          # Minimal capability stubs for pairing
└── default-config.json            # THE source of truth for all config values

src/OpenClaw.SetupEngine.UI/
├── OpenClaw.SetupEngine.UI.csproj # WinAppSDK 2.0.1, self-contained
├── App.xaml / App.xaml.cs         # Application entry
├── Program.cs                     # Main with WinUI activation
├── SetupWindow.xaml / .xaml.cs    # 720×820 window, Mica, title bar, navigation
└── Pages/
    ├── WelcomePage.xaml / .cs     # Logo, info card, Install button + ContentDialog
    ├── CapabilitiesPage.xaml / .cs # 2-column grid with icons + descriptions
    ├── ProgressPage.xaml / .cs    # Live step rows + streaming log viewer
    ├── PermissionsPage.xaml / .cs # 5 permission checks + Open Settings buttons
    └── CompletePage.xaml / .cs    # Party popper, amber banner, startup toggle
```

**Total engine code: ~1,882 lines across 8 files.** UI adds ~10 more files.

---

## Config File (`default-config.json`)

**Config is required.** Neither the headless exe nor the UI will run without one. The bundled `default-config.json` is auto-loaded from `AppContext.BaseDirectory` if no `--config` is specified.

```json
{
  "DistroName": "OpenClawDev",
  "GatewayPort": 18899,
  "BaseDistro": "Ubuntu-24.04",
  "Headless": true,
  "AutoApprovePairing": true,
  "CleanBeforeRun": true,
  "SkipPermissions": true,
  "SkipWizard": true,
  "LogLevel": "trace",
  "LogPath": null,
  "GatewayUrl": null,
  "BootstrapToken": null,
  "RollbackOnFailure": false,

  "Wsl": {
    "User": "openclaw",
    "Systemd": true,
    "Interop": false,
    "AppendWindowsPath": false,
    "Automount": false,
    "MountFsTab": false,
    "UseWindowsTimezone": true,
    "Memory": null,
    "Swap": null
  },

  "Gateway": {
    "Bind": "lan",
    "InstallUrl": null,
    "Version": null,
    "HealthTimeoutSeconds": 90,
    "ReloadMode": "hot",
    "AuthMode": "token",
    "ExtraConfig": null
  },

  "Capabilities": {
    "System": true, "Canvas": true, "Screen": true,
    "Camera": true, "Location": true, "Browser": true,
    "Device": true, "Tts": false, "Stt": false
  },

  "Settings": {
    "EnableNodeMode": true,
    "AutoStart": false,
    "NodeSystemRunEnabled": true,
    "NodeCanvasEnabled": true,
    "NodeScreenEnabled": true,
    "NodeCameraEnabled": true,
    "NodeLocationEnabled": true,
    "NodeBrowserProxyEnabled": true,
    "NodeTtsEnabled": false,
    "NodeSttEnabled": false
  },

  "Pairing": {
    "OperatorScopes": "operator.read,operator.write,operator.pairing",
    "NodeScopes": "node.read,node.write",
    "CliScopes": "operator.read,operator.write,operator.pairing",
    "TimeoutSeconds": 60
  }
}
```

### Config Layering (priority, highest wins)

1. CLI flags (`--headless`, `--log-path`, `--rollback-on-failure`)
2. Config file (explicit `--config` or bundled `default-config.json`)
3. Environment variables (`OPENCLAW_SETUP_DISTRO_NAME`, etc.)

---

## Pipeline Steps (16 total)

Executed sequentially. Each step is a small class (30–120 lines) in `SetupSteps.cs`.

| # | Step Class | What It Does |
|---|-----------|-------------|
| 1 | `CleanupStaleDistroStep` | Unregister leftover WSL distro if `CleanBeforeRun` |
| 2 | `CleanupStaleGatewayStep` | Stop orphaned gateway service, remove config |
| 3 | `PreflightOsStep` | Validate Windows 64-bit, version ≥ 22H2 |
| 4 | `PreflightWslStep` | Verify WSL installed and version ≥ 2 |
| 5 | `PreflightPortStep` | Check gateway port is available |
| 6 | `CreateWslInstanceStep` | Export base distro → import as new instance |
| 7 | `ConfigureWslInstanceStep` | Write wsl.conf, create user, set dirs |
| 8 | `InstallCliStep` | Run install script inside WSL |
| 9 | `ConfigureGatewayStep` | Write gateway config (bind, port, auth) |
| 10 | `InstallGatewayServiceStep` | `openclaw gateway install --force` |
| 11 | `StartGatewayStep` | Start service, poll health endpoint (90s timeout) |
| 12 | `MintBootstrapTokenStep` | Generate bootstrap token via CLI |
| 13 | `PairOperatorStep` | WebSocket operator connection + device approval |
| 14 | `PairNodeStep` | WebSocket node connection + capability registration |
| 15 | `VerifyEndToEndStep` | End-to-end health check (operator → node round trip) |
| 16 | `StartKeepaliveStep` | Background WSL keepalive to prevent VM shutdown |

### Step Base Class

```csharp
public abstract class SetupStep
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct);
    public virtual Task RollbackAsync(SetupContext ctx, CancellationToken ct) => Task.CompletedTask;
    public virtual bool CanSkip(SetupContext ctx) => false;
    public virtual bool CanRetry => true;
    public virtual RetryPolicy Retry => RetryPolicy.Default;
}
```

### StepResult

```csharp
public record StepResult(bool Success, string? Error = null, bool Skipped = false);
```

---

## Key Components

### SetupPipeline

Sequential orchestrator. For each step:
1. Check `CanSkip` → skip if true
2. Execute with retry (via `RetryExecutor`)
3. On failure + `RollbackOnFailure` → rollback completed steps in reverse
4. Journal records every start/complete/rollback

### SetupContext

Shared state bag passed to all steps. Contains:
- `Config` — the loaded `SetupConfig`
- `Logger` — structured JSONL logger
- `Journal` — transaction journal
- `Commands` — `ICommandRunner` for executing WSL/process commands
- Accumulated runtime state: `DistroName`, `GatewayUrl`, `BootstrapToken`, `GatewayRecordId`

### CommandRunner

Two implementations:
- **WslCommandRunner**: `wsl.exe -d <distro> -- <cmd>` with 5s bounded drain
- **ProcessCommandRunner**: Direct Windows process execution

Every command is fully logged: exe, args, timeout, exit code, stdout, stderr, elapsed time.

### TransactionJournal

Append-only JSONL file (`.journal.jsonl`) recording step transitions. Enables:
- Forensic replay of what happened
- Future `--resume` from last good state
- Rollback decision tracking

### SetupLogger

Structured JSONL logger. Records:
- Step start/complete with timing
- Every shell command and its full output
- Decisions made (e.g., "chose to clean existing distro")
- State transitions
- Errors with stack traces

Log path defaults to `%APPDATA%\OpenClawTray\Logs\Setup\setup-<timestamp>.log`

---

## UI Flow

The WinUI app is a **thin shell** — no business logic, just rendering pipeline state.

### Page Flow: Welcome → Capabilities → Progress → Permissions → Complete

**WelcomePage**
- Lobster icon + "OpenClaw Setup" title bar
- Info card explaining what will be installed
- "Install new WSL Gateway" button with ContentDialog confirmation
- "Advanced setup" link → launches tray with `--page connection`

**CapabilitiesPage**
- 2-column grid showing capabilities from config
- Icons + descriptions for each (System, Canvas, Screen, Camera, etc.)
- "Continue" proceeds to Progress

**ProgressPage**
- Step rows with spinning ProgressRing → ✓/✗ badges
- Live streaming log viewer (monospace, auto-scroll)
- On success → navigates to Permissions
- On failure → navigates to Complete(success=false)

**PermissionsPage**
- 5 permission rows: Notifications, Camera, Microphone, Location, Screen Capture
- Live status checks (registry, DeviceAccessInformation, GraphicsCaptureSession)
- "Open Settings" buttons launch `ms-settings://` URIs
- "Refresh status" button, "Continue" proceeds to Complete

**CompletePage**
- Party popper image
- "All set!" / error heading
- Amber "Node Mode Active" warning banner
- "Launch OpenClaw at startup?" toggle (writes HKCU Run registry)
- "Finish" button launches tray and closes

### Window Properties
- 720×820 logical pixels (DPI-scaled)
- Mica backdrop
- Custom title bar with lobster icon

---

## CLI Usage

### Headless (Console Exe)

```
OpenClaw.SetupEngine.exe                                    # uses bundled default-config.json
OpenClaw.SetupEngine.exe --config custom.json               # explicit config
OpenClaw.SetupEngine.exe --config custom.json --headless    # force headless
OpenClaw.SetupEngine.exe --dry-run                          # validate config, don't execute
OpenClaw.SetupEngine.exe --rollback-on-failure              # clean up on failure
OpenClaw.SetupEngine.exe --log-path ./trace.log             # override log location
```

Exit codes: 0 = success, 1 = failure

### UI (WinUI Exe)

```
OpenClaw.SetupEngine.UI.exe                                 # uses bundled default-config.json
OpenClaw.SetupEngine.UI.exe --config custom.json            # explicit config
```

Exits with error to stderr if no config file found.

---

## Build & Run

```powershell
# Build headless engine
dotnet build src\OpenClaw.SetupEngine\OpenClaw.SetupEngine.csproj

# Build UI app (requires Platform specification)
dotnet build src\OpenClaw.SetupEngine.UI\OpenClaw.SetupEngine.UI.csproj -p:Platform=x64

# Run UI
Start-Process "src\OpenClaw.SetupEngine.UI\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\OpenClaw.SetupEngine.UI.exe"

# Run headless
& "src\OpenClaw.SetupEngine\bin\Debug\net10.0\OpenClaw.SetupEngine.exe"
```

---

## Design Principles

1. **Config is required** — no hardcoded defaults for port, distro, or any operational parameter
2. **Log everything** — every command, decision, and state change in structured JSONL
3. **Steps are small** — each step is a focused class, 30–120 lines
4. **No deep nesting** — pipeline is flat sequential; steps don't branch internally
5. **Clean-start guarantee** — stale state from prior runs is cleaned before proceeding
6. **UI is optional** — engine works identically without UI; UI is a passive observer
7. **Direct code-behind** — no MVVM, no ViewModels, no framework abstractions in UI
8. **Transactional** — journal + optional rollback on failure

---

## What We Reuse

| Component | Source | How |
|-----------|--------|-----|
| WebSocket protocol | `OpenClaw.Shared` | Project reference |
| Gateway registry/credentials | `OpenClaw.Connection` | Project reference |
| Credential resolver | `OpenClaw.Connection` | Direct use |
| Node connector | `OpenClaw.Connection` | Direct use |
| Setup code decoder | `OpenClaw.Connection` | Direct use |
| Bounded WSL drain logic | Reimplemented cleanly | 5s timeout pattern |

---

## Future Work

| Item | Status | Notes |
|------|--------|-------|
| Interactive gateway wizard in UI | Not started | RPC wizard protocol exists; needs dynamic page renderer |
| Resume from journal (`--resume`) | Designed, not implemented | Journal records state; pipeline can skip completed steps |
| Retry button in Progress UI | Not started | Pipeline supports retry; UI needs "Retry" affordance |
| Tray integration (invoke engine from tray) | Not started | Engine is standalone exe; tray could spawn it |
| Replace `LocalGatewaySetup.cs` | Out of scope | Requires feature-flag switchover in tray |

---

## Design Decisions

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| 1 | Config format | JSON | No extra dependency; commented JSON for readability |
| 2 | Config requirement | Required (no built-in defaults) | Prevents port/distro confusion across environments |
| 3 | Log viewer | Real-time streaming in Progress page | Essential for debugging; makes iteration fast |
| 4 | Rollback scope | Opt-in via `RollbackOnFailure` | Debugging needs artifacts; clean slate for CI |
| 5 | UI framework | Direct code-behind, no MVVM | Minimal code; setup UI is write-once, low-churn |
| 6 | Two projects | Engine (console) + UI (WinUI) | Engine testable/automatable independently |
| 7 | Step parallelism | Sequential only | Simplicity; steps have ordering dependencies |
| 8 | Gateway bind | LAN (`0.0.0.0`) by default | Required for reliable WSL2 networking |
