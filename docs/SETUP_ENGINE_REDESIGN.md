# Setup Engine Redesign — Detailed Plan

## Problem Statement

The current onboarding/setup system (`LocalGatewaySetup.cs` at 4,092 lines) works but has accumulated complexity:
- Deep nesting, complex branching, ad-hoc error plumbing
- 16 phases baked into one monolithic orchestrator
- UI tightly coupled to engine internals
- Hard to automate/script — requires WinUI window
- Debugging requires correlating multiple log sources
- No transactional semantics (partial failures leave ambiguous state)

## Proposed Approach

Build a **standalone setup engine** as a new project (`OpenClaw.SetupEngine`) with:
- A **pipeline of discrete steps** (not a monolithic switch/case)
- **Transactional semantics** — each step declares preconditions, execution, and rollback
- **Pervasive structured logging** (JSONL) — every decision, command, and state transition logged
- **Config-file driven** — entire setup can run headless from a YAML/JSON config
- **Thin WinUI shell** — optional UI that observes engine events (doesn't drive logic)
- **Reuses** `OpenClaw.Connection` and `OpenClaw.Shared` for gateway/credential/WebSocket code

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  OpenClaw.SetupEngine (net10.0, no UI dependency)       │
│                                                         │
│  ┌──────────┐  ┌──────────┐  ┌────────────────────┐    │
│  │ Pipeline │──│  Steps   │──│ TransactionJournal  │    │
│  └──────────┘  └──────────┘  └────────────────────┘    │
│       │              │               │                  │
│  ┌──────────┐  ┌──────────┐  ┌────────────────────┐    │
│  │ Config   │  │ Runners  │  │ StructuredLogger    │    │
│  │ (YAML)   │  │ (WSL,Cmd)│  └────────────────────┘    │
│  └──────────┘  └──────────┘                             │
│       │                                                  │
│  refs: OpenClaw.Connection, OpenClaw.Shared              │
└─────────────────────────────────────────────────────────┘
         ▲ events (IProgress<T>, IObservable)
         │
┌─────────────────────────────────────────────────────────┐
│  OpenClaw.SetupEngine.UI (net10.0-windows, WinUI3)      │
│  Thin shell: renders pipeline progress, no logic        │
└─────────────────────────────────────────────────────────┘
```

## Core Design Principles

### 1. Steps as Small Classes (Abstract Base)

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

No interfaces. Abstract base with sensible defaults — most steps only override `ExecuteAsync`. Each step is a small focused class (~30-80 lines), all living in `SetupSteps.cs`.

### 2. Transactional Pipeline (Clean-Start Guarantee)

```csharp
public sealed class SetupPipeline
{
    // ALWAYS starts clean. Before running steps:
    //   1. Detect leftover state (stale distro, partial config, orphan services)
    //   2. Clean it up (unregister distro, stop services, delete config)
    //   3. Then run steps sequentially
    //
    // On failure:
    //   - If step.CanRetry: retry up to N times with backoff
    //   - If terminal failure + rollbackOnFailure: rollback completed steps in reverse
    //   - Journal records every transition for forensic replay
    
    public async Task<PipelineResult> RunAsync(SetupConfig config, CancellationToken ct);
    public async Task<PipelineResult> ResumeAsync(JournalSnapshot snapshot, CancellationToken ct);
}
```

**Clean-start guarantee**: Every run begins by ensuring a clean slate. If a previous run left a half-installed distro, a stale gateway service, or orphaned config files, the pipeline detects and cleans them *before* proceeding. This eliminates the "works on second try" class of bugs.

The pipeline is also **resumable** — if the process crashes mid-run, the journal enables an explicit `--resume` that picks up from last good state (opt-in, not default — default is clean start).

### 3. Structured Logging (Everything)

```csharp
public interface ISetupLogger
{
    IDisposable BeginStep(string stepId, IReadOnlyDictionary<string, object>? properties = null);
    void Event(string eventName, LogLevel level, object? payload = null);
    void Command(string exe, string[] args, TimeSpan timeout);
    void CommandResult(int exitCode, string stdout, string stderr, TimeSpan elapsed);
    void Decision(string description, string chosen, string[] alternatives);
    void StateTransition(string from, string to, string reason);
}
```

Log format: JSONL with correlation IDs. Every single shell command, its full output, timing, and exit code logged. Secrets auto-redacted.

### 4. Config-File Driven

```json
{
  "mode": "local-wsl",
  "distroName": "OpenClawGateway",
  "gatewayPort": 18789,
  "baseDistro": "Ubuntu-24.04",
  "skipPermissions": false,
  "skipWizard": false,
  "headless": true,
  "autoApprovePairing": true,
  "rollbackOnFailure": false,
  "cleanBeforeRun": true,
  "logLevel": "trace",
  "logPath": "./setup-trace.jsonl",
  "gatewayUrl": "ws://localhost:18789",
  "bootstrapToken": null,
  "wizardAnswers": {
    "provider": "anthropic",
    "model": "claude-sonnet-4-20250514",
    "api_key": "sk-ant-..."
  }
}
```

**Headless mode**: Pipeline runs without UI. All decisions come from config. For CI/automation/fleet deployment.

**Interactive mode**: Thin WinUI shell subscribes to pipeline events and renders progress.

## Step Inventory

Each step is its own class file. Steps grouped by category:

### Category: Cleanup (runs first, always)
| Step ID | Purpose | Rollback |
|---------|---------|----------|
| `cleanup-stale-distro` | Detect & unregister leftover WSL distro from prior run | — |
| `cleanup-stale-service` | Stop & remove orphaned gateway systemd service | — |
| `cleanup-stale-config` | Remove stale gateway config, tokens, setup-state.json | — |

### Category: Preflight
| Step ID | Purpose | Rollback |
|---------|---------|----------|
| `preflight-os` | Validate Windows 64-bit, version ≥ 22H2 | — (check only) |
| `preflight-wsl` | Verify WSL installed and version ≥ 2 | — |
| `preflight-port` | Check gateway port (18789) available | — |
| `preflight-existing` | Detect existing distro, decide reuse/replace | — |

### Category: WSL
| Step ID | Purpose | Rollback |
|---------|---------|----------|
| `wsl-enable` | Ensure WSL feature enabled (may need reboot) | — |
| `wsl-create-instance` | Create WSL2 distro from base image | Unregister distro |
| `wsl-configure` | Write wsl.conf, create user, dirs | — (idempotent) |

### Category: Gateway Install
| Step ID | Purpose | Rollback |
|---------|---------|----------|
| `install-cli` | Run upstream install script in WSL | Remove /opt/openclaw |
| `configure-gateway` | Write gateway config + token | Remove config files |
| `install-service` | `openclaw gateway install --force` | `openclaw gateway uninstall` |
| `start-gateway` | Start service, wait for health (90s) | Stop service |

### Category: Pairing
| Step ID | Purpose | Rollback |
|---------|---------|----------|
| `mint-token` | Mint bootstrap token via CLI | — |
| `pair-operator` | Connect operator WebSocket + approve device | Disconnect |
| `pair-node` | Connect node WebSocket + register capabilities | Disconnect |
| `verify-e2e` | End-to-end health check | — |

### Category: Post-Setup
| Step ID | Purpose | Rollback |
|---------|---------|----------|
| `gateway-wizard` | Run provider/model wizard via RPC; answers from config (headless) or UI (interactive) | — (optional) |
| `permissions-check` | Verify Windows permissions | — (advisory) |
| `save-state` | Persist gateway record + settings | — |

## Key Components

### SetupContext (Shared State Bag)

```csharp
public sealed class SetupContext
{
    public SetupConfig Config { get; }
    public ISetupLogger Logger { get; }
    public TransactionJournal Journal { get; }
    
    // Accumulated state from steps
    public string? DistroName { get; set; }
    public string? GatewayUrl { get; set; }
    public string? BootstrapToken { get; set; }
    public string? GatewayRecordId { get; set; }
    public GatewayRegistry GatewayRegistry { get; }
    public IGatewayConnectionManager? ConnectionManager { get; set; }
}
```

### TransactionJournal

```csharp
public sealed class TransactionJournal
{
    // Append-only log of step executions
    // Enables: resume after crash, forensic replay, rollback decisions
    
    public void RecordStepStarted(string stepId, DateTimeOffset timestamp);
    public void RecordStepCompleted(string stepId, StepResult result, DateTimeOffset timestamp);
    public void RecordRollbackStarted(string stepId);
    public void RecordRollbackCompleted(string stepId, bool success);
    
    public JournalSnapshot GetSnapshot();
    public static TransactionJournal LoadFrom(string path);
    public void SaveTo(string path);
}
```

Persisted as JSONL alongside logs. If process dies mid-step, resume knows exactly where it stopped.

### CommandRunner (WSL Abstraction)

```csharp
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandSpec spec, CancellationToken ct);
}

public record CommandSpec(
    string Executable,
    string[] Arguments,
    TimeSpan Timeout,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool RedactOutput = false,
    string? StdinInput = null
);

public record CommandResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Elapsed,
    bool TimedOut
);
```

**WslCommandRunner** wraps this for `wsl.exe -d <distro> -- <cmd>` with the proven bounded-drain logic from the current implementation (5s drain timeout for orphan processes).

### RetryPolicy

```csharp
public record RetryPolicy(
    int MaxAttempts = 3,
    TimeSpan InitialDelay = default,  // 2s
    double BackoffMultiplier = 2.0,
    TimeSpan MaxDelay = default       // 30s
);
```

Steps declare their own retry policy. Pipeline handles retry orchestration — steps don't implement retry loops internally.

## UI Design

The WinUI shell is a **thin observer** for progress steps, but has an **interactive role** during the gateway wizard phase — the gateway sends dynamic page definitions (select, text, confirm, note) via RPC and the UI must render them and relay user selections back.

### Two Interaction Models

**1. Automated (headless)**: Wizard step answers come from the config file's `wizardAnswers` map:
```json
{
  "wizardAnswers": {
    "provider": "anthropic",
    "model": "claude-sonnet-4-20250514",
    "api_key": "sk-..."
  }
}
```
When a wizard step arrives, the engine looks up the answer in config → sends it back via RPC → no UI needed.

**2. Interactive (UI)**: The engine emits a `WizardStepReceived` event with the step definition. The UI renders the appropriate control (radio buttons for Select, text box for Text, etc.) and relays the user's choice back to the engine, which forwards it via `wizard.next(answer)`.

```csharp
// Engine exposes a channel for wizard interaction
public interface IWizardInteraction
{
    event EventHandler<WizardStep> StepReceived;      // engine → UI
    void SubmitAnswer(string stepId, object answer);  // UI → engine
}
```

This keeps the engine in control of flow while the UI is a pluggable input/output adapter.

### UI Modes:
1. **Full wizard** — multi-page flow with progress, interactive wizard, permissions
2. **Compact** — single-page progress view (for "re-run setup" scenarios)
3. **None** — headless, answers from config

### UI doesn't drive orchestration logic:
- No `if (phase == X && status == Y)` in UI code
- UI maps step states to visual rows (1:1 step→row or N:1 via display groups)
- Wizard pages are dynamically rendered from step definitions — no hardcoded page layouts

## Project Structure

```
src/
  OpenClaw.SetupEngine/
    OpenClaw.SetupEngine.csproj       # net10.0-windows, WinUI3, standalone exe
    Program.cs                        # Entry point: --headless or UI mode
    SetupPipeline.cs                  # Orchestrator + abstract SetupStep base + StepResult
    SetupContext.cs                   # Shared state bag + SetupConfig model (JSON)
    SetupSteps.cs                     # ALL step implementations in one file
    TransactionJournal.cs             # Append-only JSONL journal for crash recovery
    SetupLogger.cs                    # Structured JSONL logger + secret redactor
    CommandRunner.cs                  # WslCommandRunner + ProcessCommandRunner
    RetryExecutor.cs                  # Retry with exponential backoff
    SetupWindow.cs                    # WinUI host window + progress + log tail
    WizardPage.cs                     # Dynamic gateway wizard renderer
```

**11 source files. One project. One exe.**

- No interfaces — abstract base class `SetupStep` with `ExecuteAsync`, `RollbackAsync`, `CanSkip`
- All steps live in `SetupSteps.cs` (each step is a small class, ~30-80 lines; file stays under 800 lines)
- Single exe: launches UI by default, `--headless` for automation
- References `OpenClaw.Connection` and `OpenClaw.Shared` for WebSocket/credential/protocol code only
- Does NOT reference any existing setup/onboarding code — clean implementation

## Scope

This session builds a **standalone setup engine**. Replacing the current tray setup flow is out of scope. The engine will:
- Install WSL, configure it, install the gateway, pair, and verify
- Work headless from a JSON config or interactively via UI
- Log everything in structured JSONL
- Be transactional with clean-start and rollback support

It will NOT:
- Integrate into the existing tray app
- Replace `LocalGatewaySetup.cs`
- Modify existing projects (ask first if changes are needed)

## Automation & Config

### CLI Usage (Headless)

```
openclaw-setup.exe --config setup.json --headless
openclaw-setup.exe --config setup.json --headless --log-path ./trace.jsonl
openclaw-setup.exe --resume ./journal.jsonl              # resume from crash
openclaw-setup.exe --dry-run --config setup.json         # validate config only
openclaw-setup.exe --config setup.json --rollback-on-failure  # clean up on failure
```

Exit codes: 0 = success, 1 = retryable failure, 2 = terminal failure, 3 = cancelled

### Config Layering

Priority (highest wins):
1. CLI flags (`--gateway-port 18790`)
2. Config file (`setup.json`)
3. Environment variables (`OPENCLAW_SETUP_*`)
4. Built-in defaults

## Migration Strategy (Future — Out of Scope)

This session builds the standalone engine only. Future integration:
1. **Phase 1** (this session): Build engine + standalone exe. Validate it works end-to-end.
2. **Phase 2** (future): Wire into Tray app as an alternative to `LocalGatewaySetup`. Feature-flag switchover.
3. **Phase 3** (future): Remove old code once stable.

## What We Reuse from Existing Code

| Component | Source | How |
|-----------|--------|-----|
| WebSocket protocol | `OpenClaw.Shared` | Project reference |
| Gateway registry/credentials | `OpenClaw.Connection` | Project reference |
| Credential resolver | `OpenClaw.Connection` | Direct use |
| Node connector | `OpenClaw.Connection` | Direct use |
| Setup code decoder | `OpenClaw.Connection` | Direct use |
| Secret redaction | Current `SecretRedactor` | Copy/adapt |
| Bounded WSL drain | Current `WslExeCommandRunner` | Reimplement cleanly |
| Permission checker | Current `PermissionChecker.cs` | Copy/adapt to step |
| Wizard protocol | Current `WizardFlowController` | Adapt to step interface |

## Design Decisions (Resolved)

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| 1 | Config format | **JSON** | No extra dependency; simpler parsing; config is machine-authored more often than human-edited |
| 2 | Log viewer | **Yes — real-time JSONL tail** in collapsible panel | Essential for debugging; makes iteration fast |
| 3 | Rollback scope | **Configurable** — leave artifacts by default, `--rollback-on-failure` flag to clean up | Debugging needs artifacts; CI/fleet needs clean slate |
| 4 | Gateway wizard | Keep RPC wizard protocol as-is (adapt to step interface) | Proven protocol, gateway owns the flow |
| 5 | Packaging | **Both** — standalone exe that tray app can also invoke | Independent updates + tray integration |
| 6 | Step parallelism | **No** — sequential only | Preflight is fast; simplicity wins; design doesn't preclude future parallelism |

## Success Criteria

- [ ] Setup runs headless from config file with zero UI interaction
- [ ] Every shell command and its output appears in JSONL trace
- [ ] Crash mid-setup → resume from journal picks up where it left off
- [ ] Each step file is < 200 lines (maintainable, focused)
- [ ] Total engine code (excluding tests) < 3000 lines
- [ ] Existing connection/pairing tests pass with new engine
- [ ] UI is purely reactive — removing it doesn't break engine
