# Setup Engine — Architecture & Reference

## Overview

The Setup Engine is a **standalone, config-driven system** for provisioning an OpenClaw WSL gateway from scratch. It consists of two projects:

1. **`OpenClaw.SetupEngine`** — Headless pipeline (console exe). Runs 18 steps sequentially with full JSONL logging, transaction journal, and rollback support.
2. **`OpenClaw.SetupEngine.UI`** — WinUI3 app that wraps the same pipeline with a 5-page fluent wizard UI.

Both accept a JSON config file to customize behavior. A bundled `default-config.json` ships with each exe and provides secure defaults (loopback bind, WSL isolation, systemd enabled). All defaults can be overridden via config file or environment variables.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  OpenClaw.SetupEngine (net10.0, console exe)                │
│                                                             │
│  SetupPipeline ──→ 18 SetupStep classes ──→ StepResult      │
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
├── SetupSteps.cs                  # All setup step implementations
├── TransactionJournal.cs          # Append-only JSONL journal (77 lines)
├── SetupLogger.cs                 # Structured JSONL logger (112 lines)
├── CommandRunner.cs               # Concrete WSL/process command runner
├── RetryExecutor.cs               # Exponential backoff retry
├── StubNodeCapability.cs          # Minimal capability stubs for pairing
└── default-config.json            # THE source of truth for all config values

src/OpenClaw.SetupEngine.UI/
├── OpenClaw.SetupEngine.UI.csproj # WinAppSDK 2.1.3, self-contained
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
  "DistroName": "OpenClawGateway",
  "GatewayPort": 18789,
  "BaseDistro": "Ubuntu-24.04",
  "Headless": true,
  "AutoApprovePairing": true,
  "CleanBeforeRun": true,
  "SkipPermissions": false,
  "SkipWizard": false,
  "WizardAnswers": {
    "openclaw-setup": "true",
    "security-disclaimer": "true",
    "i-understand-this-is-personal-by-default-and-shared-multi-user-use-requires-lock-down-continue": "true",
    "setup-mode": "quickstart",
    "existing-config-detected": "true",
    "config-handling": "keep",
    "quickstart": "true",
    "model-auth-provider": "skip",
    "default-model": "__keep__",
    "select-channel-quickstart": "__skip__",
    "search-provider": "__skip__",
    "configure-skills-now-recommended": "false"
  },
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
    "Bind": "loopback",
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
    "Device": true, "Tts": true, "Stt": true
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
    "NodeTtsEnabled": true,
    "NodeSttEnabled": true
  },

  "Pairing": {
    "TimeoutSeconds": 60
  }
}
```

### Config Layering (priority, highest wins)

1. CLI flags (`--headless`, `--log-path`, `--rollback-on-failure`, `--no-rollback-on-failure`)
2. Config file (explicit `--config` or bundled `default-config.json`)
3. Environment variables (`OPENCLAW_SETUP_DISTRO_NAME`, etc.)

---

## Pipeline Steps (18 total)

Executed sequentially. Each step is a small class (30–120 lines) in `SetupSteps.cs`.

| # | Step Class | What It Does |
|---|-----------|-------------|
| 1 | `PreflightOsStep` | Validate Windows 64-bit, version ≥ 22H2 |
| 2 | `PreflightWslStep` | Verify WSL is installed and supports direct named clean installs |
| 3 | `CleanupStaleDistroStep` | Unregister leftover app-owned WSL distro and remove its VHD directory if `CleanBeforeRun` |
| 4 | `CleanupStaleGatewayStep` | Stop orphaned gateway service, remove config |
| 5 | `PreflightPortStep` | Check gateway port is available |
| 6 | `CreateWslInstanceStep` | Directly install a fresh app-owned WSL distro; never export a user's Ubuntu distro |
| 7 | `ConfigureWslInstanceStep` | Write wsl.conf, create user, set dirs |
| 8 | `ValidateWslLockdownStep` | Verify WSL isolation settings are applied |
| 9 | `InstallCliStep` | Run install script inside WSL |
| 10 | `ConfigureGatewayStep` | Write gateway config (bind, port, auth) |
| 11 | `InstallGatewayServiceStep` | `openclaw gateway install --force` |
| 12 | `StartGatewayStep` | Start service, poll health endpoint (90s timeout) |
| 13 | `MintBootstrapTokenStep` | Generate bootstrap token via CLI |
| 14 | `PairOperatorStep` | WebSocket operator connection + device approval |
| 15 | `PairNodeStep` | WebSocket node connection + capability registration |
| 16 | `VerifyEndToEndStep` | End-to-end health check (operator → node round trip) |
| 17 | `RunGatewayWizardStep` | Run/configure the gateway wizard unless skipped |
| 18 | `StartKeepaliveStep` | Background WSL keepalive to prevent VM shutdown |

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
public sealed record StepResult(StepOutcome Outcome, string? Message = null, Exception? Exception = null);
```

---

## Key Components

### SetupPipeline

Sequential orchestrator. For each step:
1. Check `CanSkip` → skip if true
2. Execute with retry (via `RetryExecutor`)
3. On failure + `RollbackOnFailure` → try failed-step cleanup, then rollback completed steps in reverse
4. Journal records every start/complete/rollback

### SetupContext

Shared state bag passed to all steps. Contains:
- `Config` — the loaded `SetupConfig`
- `Logger` — structured JSONL logger
- `Journal` — transaction journal
- `Commands` — `CommandRunner` for executing WSL/process commands
- Accumulated runtime state: `DistroName`, `GatewayUrl`, `BootstrapToken`, `GatewayRecordId`

### CommandRunner

A single concrete runner executes Windows processes and WSL scripts (`wsl.exe -d <distro> -- bash -lc ...`) with timeouts, bounded output collection, and environment injection.

Every command is logged with exe, sanitized args, timeout, exit code, sanitized stdout/stderr, and elapsed time.

### TransactionJournal

Append-only JSONL file (`.journal.jsonl`) recording step transitions. Enables:
- Forensic replay of what happened
- Future `--resume` from last good state
- Rollback decision tracking

### SetupLogger

Structured JSONL logger. Records sanitized entries for:
- Step start/complete with timing
- Every shell command and bounded output
- Decisions made (e.g., "chose to clean existing distro")
- State transitions
- Errors with stack traces

Log path defaults to `%APPDATA%\OpenClawTray\Logs\Setup\setup-<timestamp>.log`

---

## UI Flow

The WinUI app is a **thin shell** — no business logic, just rendering pipeline state. End-user UI runs default to `RollbackOnFailure=true`; `--no-rollback-on-failure` preserves an explicit debugging opt-out.

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
OpenClaw.SetupEngine.exe --no-rollback-on-failure           # explicit rollback opt-out
OpenClaw.SetupEngine.exe --log-path ./trace.log             # override log location
```

Exit codes: 0 = success, 1 = failure

### UI (WinUI Exe)

```
OpenClaw.SetupEngine.UI.exe                                 # uses bundled default-config.json
OpenClaw.SetupEngine.UI.exe --config custom.json            # explicit config
OpenClaw.SetupEngine.UI.exe --no-rollback-on-failure        # debug opt-out from UI cleanup
```

Uses the bundled default config when no explicit `--config` is supplied.

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

1. **Config is explicit** — secure bundled defaults can be overridden by config file, environment, or flags
2. **Log everything** — every command, decision, and state change in structured JSONL
3. **Steps are small** — each step is a focused class, 30–120 lines
4. **Fail closed on approval** — setup validates approval request IDs and avoids ambiguous node approvals
5. **Clean-start guarantee** — stale state from prior runs is cleaned before proceeding
6. **UI is optional** — engine works identically without UI; UI is a passive observer
7. **Direct code-behind** — no MVVM, no ViewModels, no framework abstractions in UI
8. **Transactional** — journal + rollback on failure, enabled by default for the UI

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
| 2 | Config source | Bundled default config plus overrides | Provides secure defaults while preserving explicit environment-specific overrides |
| 3 | Log viewer | Real-time streaming in Progress page | Essential for debugging; makes iteration fast |
| 4 | Rollback scope | UI default on; headless/config opt-in or explicit opt-out | End-user setup should clean partial installs; debugging can preserve artifacts |
| 5 | UI framework | Direct code-behind, no MVVM | Minimal code; setup UI is write-once, low-churn |
| 6 | Two projects | Engine (console) + UI (WinUI) | Engine testable/automatable independently |
| 7 | Step parallelism | Sequential only | Simplicity; steps have ordering dependencies |
| 8 | Gateway bind | Loopback by default, LAN explicit opt-in | Secure default; LAN mode must be deliberate |
