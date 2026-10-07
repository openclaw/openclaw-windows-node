# Setup Engine - Architecture & Reference

## Overview

The Setup Engine is a **config-driven system** for provisioning an OpenClaw WSL gateway from scratch. It consists of two setup projects plus the tray host:

1. **`OpenClaw.SetupEngine`** - Headless pipeline library. Runs 24 steps sequentially with full JSONL logging, transaction journal, and rollback support.
2. **`OpenClaw.SetupEngine.UI`** - WinUI3 setup window/pages that wrap the same pipeline with a fluent wizard UI.
3. **`OpenClaw.Tray.WinUI`** - The only shipped WinUI executable. It hosts `SetupWindow` directly and self-restarts after successful setup.

The bundled `default-config.json` ships with the tray executable and provides secure defaults (loopback bind, WSL isolation, systemd enabled). Defaults can be overridden via config file or environment variables.

WSL wizard completion restores `gateway.reload.mode` before explicitly restarting
the Gateway. Gateway 2026.9.6 may refuse the guarded restart when it cannot verify
a live serving owner; the rejected owner-lease predicate is not exposed by the
public Gateway CLI. A reload-triggered supervisor transition is one possible
timing explanation, not an established cause of every refusal. Observed service
states differ: local diagnostics captured `activating/auto-restart` with no
MainPID, while hosted generic refusals captured an `active/running` unit and a
live PID. Neither snapshot establishes the admission-time owner-lease predicate.
`SetupWizardRunner` recognizes only the exact serving-owner refusal or the
combination of typed state-database coordinator contention and the exact
restart-intent-recording refusal. It waits for verified managed endpoint
ownership using the existing bounded provenance probe, then retries the normal
guarded CLI restart once.
The probe allows up to 30 one-second retry delays, plus probe duration, for
`NoListener` and `UnknownListener` tagged `ListenerSnapshotChanged`. Other
unknown/conflicting listeners, other restart errors, and a repeated refusal still
fail setup. Listener provenance does not prove owner-lease or coordinator
readiness; the retried CLI command retains those guards. There is no direct
systemd restart fallback or ownership bypass.

### WSL start/restart deadlines and evidence

`StartGatewayStep` gives `gateway start` 90 seconds and `gateway restart`
420 seconds. The latter covers the pinned Gateway 2026.9.6 service-stop
allowance (330 seconds), replacement-health window (60 seconds), and 30 seconds
of CLI preparation margin. This is a bounded Companion allowance, not a
guarantee that an overloaded Gateway will finish. Increasing only the later
HTTP timeout cannot extend the CLI deadline.

After reload restoration, `SetupWizardRunner` applies one shared deadline of
`420 + Gateway.HealthTimeoutSeconds + 60` seconds (570 seconds by default).
It includes the initial CLI attempt, any start-limit recovery, the single
exact-marker guarded retry, contention delay, provenance probes, HTTP
reachability and final provenance check. The second CLI attempt gets at most
the remaining budget, never a fresh lifecycle allowance. The preceding reload
write retains its separate 15-second bound. Cleanup still runs independently
of wizard cancellation; the deadline cancels its owned commands/probes, not
the user's Gateway service directly.
This is a cooperative execution deadline; OS process teardown and scheduling
can add wall-clock overhead. Standalone start/restart steps retain their
existing finite pipeline retry policy for non-timeout failures; the shared
lifecycle deadline described here is specific to post-wizard restoration.
Retaining that standalone policy is an intentional operator wait-time tradeoff:
three completed non-timeout restart failures can consume nearly 1,260 seconds
of CLI time, or 2,520 seconds if each attempt also takes the existing start-limit
reset/retry path, plus reset, health and backoff time. A timed-out CLI remains
terminal on the first timeout. This change does not add a standalone shared budget.

A CLI timeout is a terminal unknown outcome, even if captured output also
contains a retry marker. It does not trigger reset-failed, a guarded restart
retry, or an enclosing pipeline retry. Failures retain CLI phase, exit code,
timeout flag, elapsed time, limit, and both output streams, each sanitized
before truncation to 2,048 characters. A provenance refusal during recovery
also retains the original restart failure. Internal `StepResult` recovery facts
are classified from complete command streams using the exact markers before
display truncation, so long diagnostic prefixes cannot suppress guarded recovery.
These facts carry no raw output and never authorize recovery after a timeout.

If the guarded retry fails, its outcome and diagnostics retain the initial
restart refusal as context. If the deadline expires during final ownership
verification, diagnostics distinguish completed CLI restart and HTTP
reachability from the final ownership stage exceeding its deadline, even if
the ownership probe returned success before the final deadline check.
The result remains terminal and does not authorize another restart.

HTTP 200/401/403 still means only endpoint reachability. Neither this HTTP
probe nor listener provenance establishes an authenticated ready Gateway.
Authentication and pairing retain their existing owners and gates.

**Why the order is unchanged:** at upstream commit
[`eb377ac59e6c9fd6c7705028034812becf00271b`](https://github.com/openclaw/openclaw/tree/eb377ac59e6c9fd6c7705028034812becf00271b),
[`config-reload.ts`](https://github.com/openclaw/openclaw/blob/eb377ac59e6c9fd6c7705028034812becf00271b/src/gateway/config-reload.ts)
commits the comparison baseline with `runtimeApplied: false` when the next
mode is `off`.
[`config-reload-plan.ts`](https://github.com/openclaw/openclaw/blob/eb377ac59e6c9fd6c7705028034812becf00271b/src/gateway/config-reload-plan.ts)
classifies `gateway.reload` as `none`. Restoring `hybrid` alone is therefore
not proof that earlier wizard settings were applied. A coalesced observation
can include other changes, so it is also not a synchronization barrier against
automatic restart. Restarting before restoration would leave a subsequent
write to reconcile with the replacement runtime and does not provide a
documented ownership/coordinator-readiness handshake. No new public Gateway
API, sleep, direct systemd restart, or sequencing assumption is introduced.
The timing constants come from
[`gateway-shutdown-budget.mjs`](https://github.com/openclaw/openclaw/blob/eb377ac59e6c9fd6c7705028034812becf00271b/gateway-shutdown-budget.mjs).

`GatewayRestartLifecycleTests` reproduces the Companion command-boundary
transition with injected serving-owner and typed-contention refusals, slow CLI
completion, timeout output, and virtual deadline exhaustion/cancellation.
These are deterministic orchestration tests, not execution of the upstream
watcher or proof of the cause of a live owner-lease refusal. Companion-only
reordering is not established safe by this evidence; real WSL Gateway/MXC
validation remains required.

The separate **native Gateway MSIX** Welcome path does not use
`SetupStepFactory.BuildDefaultSteps()`. `NativeGatewaySetupService` owns its
package-contract and profile preparation. `NativeGatewaySetupSession` owns
staged-record runtime authorization, legacy-only reload suspension/restoration, retry/cancel,
authenticated health/config gates, and final registry publication.
Both installations use the shared focused `AiSetupPage` and verified
three-destination `AiReadyPage`. `WizardPage` remains an explicit compatibility
fallback when focused setup methods are unavailable. Its native requests retain
the owned `NativeGatewaySetupConnection` authorization and upstream
`installDaemon: false` contract.
`NativeGatewayPackageResolver`
checks Windows package registration and package-qualified aliases.
`NativeGatewayMsixInstaller` installs the fixed Store product using the current-user
App Installer alias and `CommandRunner`:
`winget install --id 9NV70LV3D6XC --source msstore --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --no-upgrade`.
The shared capabilities page shows the native installation and agreement
disclosure before its **Set up gateway** action; other routes retain **Next**.
This preserves consent without adding a separate native review step.
Microsoft Store owns architecture selection,
signature validation and deployment; no local MSIX path is required. Missing
WinGet, Store access failures and nonzero exit codes surface bounded, sanitized
diagnostics and retry guidance instead of opening a manual Store page.
`NativeGatewayPackageIdentity` pins the exact Store name/publisher pair and retains
the original development identity for existing installations. Multiple matching
registrations fail explicitly for new setup. Existing runtime profiles resolve only
their saved family, allowing both packages to coexist without an implicit migration.
`NativeGatewayPackageAcquisition` invokes installation once only for missing
registration. Installation and verified package readiness share a five-minute
deadline. Cancellation stops the WinGet request, but Windows deployment may
continue. Repair errors and timeouts stay visible, with explicit retry rather
than repeated installer launches; retries recheck registration before installing.
Native setup shares the `SetupAccessDraft` capability profiles with WSL but
skips WSL/Local AI/Tailscale installation review and probes. The native
progress page uses `SetupPhaseStatus` rows and automatically enters **Connect
your AI** after preparing its runtime. Finalization applies the selected Gateway
command allowlist before config/health gates, verifies the exact primary model
after the owned runtime restart, then persists reviewed Companion settings
without overwriting unrelated settings or startup preferences.
Completion does not claim node pairing.
`NativeGatewaySetupHost` runs captured `clawctl setup`, config validation, and
health commands, plus an explicitly requested profile-scoped recovery terminal.
It never launches `openclaw onboard` or WSL.
`NativeGatewayRuntimeRouter` selects the package-owned `IsolatedGatewayRuntime`
or the recognized legacy `NativeGatewayRuntime`. Isolated configuration, credentials
and workspace stay under the agent account; Companion does not read a host-side
config or forward host profile overrides. Shared AI and classic fallback console
output use authenticated `logs.tail` for isolated sessions, with per-request
authorization and explicit gap/failure recovery. Legacy console output retains
its profile log. This path is UI-only. Companion never downloads an MSIX itself
or bypasses Microsoft Store installation.
Existing headless setup arguments continue to select the WSL pipeline.
See [Native Gateway MSIX](ONBOARDING_WIZARD.md#native-gateway-msix-isolated-or-legacy)
for consent, lifecycle, retry, and acquisition boundaries.

See [Gateway setup responsibilities](GATEWAY_SETUP_RESPONSIBILITIES.md) for the
Gateway packaging responsibility matrix, its comparison with WSL provisioning,
and the decided Companion-owned MXC lifecycle. The required isolated path
preserves identity/configuration across restarts, stops on exit, and deprovisions
only on explicit removal. Its package activation and listener-provenance contracts
remain blocked pending integration proof; the non-isolated runtime is not a
substitute.

The [Welcome recommendation policy](ONBOARDING_WIZARD.md#welcome) now checks
`wxc-exec --probe` session capability before recommending the existing native
Gateway. `NativeGatewaySetupEligibility` owns admission and selection policy.
Unavailable capability offers Windows Update with the pinned SDK's Insider
baseline (26340.9212); failed probes offer retry/repair instead. WSL is always
visible as the second option after native, with existing-gateway connection third.
Explicit WSL and existing-gateway choices survive late native probe results. Welcome has no
manual recheck button; reopening the page checks again. The separate isolation warning/checkbox is removed by the
2026-09-18 product decision; general security consent remains. This is not
session provisioning. Gateway distribution includes x64, ARM64 and MSIX bundle
artifacts, but the temporary development installer remains ARM64-only.

Interactive WSL setup uses `OnboardingFlowPolicy` to select the installation
subset. It defers the classic wizard and Windows workspace finalization until
after the focused AI flow. The separate native MSIX route retains the hosted
classic wizard. `GatewayAiSetupClient` owns typed provider discovery,
selection, activation and recovery; the native AI page renders that state.
`AiSetupPresentationModel` owns presentation-only grouping. A single
page-owned `ProviderSetupDialog` owns prompt controls and secret clearing,
without another client, persistence store or top-level setup page.
`SetupWindow` remains the completion host. Verified AI opens the native chooser;
an explicit choice restarts into Chat, Channels or Skills with a bound receipt.
The experimental browser-completion path is removed. See the completion handoff
in `ONBOARDING_WIZARD.md`.
Headless setup keeps `SetupStepFactory.BuildDefaultSteps()` and the
classic wizard contract. See [Onboarding Wizard](ONBOARDING_WIZARD.md) for current
page composition, compatibility behavior and artwork.

AI provider commands admit one page-owned dialog lifetime immediately, before
awaiting Gateway replies. The controller continues an explicitly chosen Gateway
preparation into its exact returned model, preserving conversation-discovery
consent, route and generation fences; it never answers client-owned server
prompts automatically. It also owns fresh HTTPS auth-link admission and the
bounded same-authority restart wait. Native input/secret clearing stays in
`ProviderSetupDialog`; exact activation/verification stays in the focused client.
No provisioning or earlier setup-page ownership moves into this presentation.

Managed Local AI can be chosen after Gateway installation on **Connect your AI**.
`ISetupLocalAiHost` is the typed bridge to the existing tray-owned runtime and
canonical provider coordinator. `LocalAiSetupRouteResolver` shares Settings'
unique app-owned Gateway admission with this same-window path. The read-only
`LocalAiOnboardingObservation` does not use runtime refresh or receipt
reconciliation because those owners can mutate state. Explicit setup/repair
reuses `BuildLocalAiRecoverySteps`, even when no prior Local AI receipt exists.
`SetupWindow` retains its lock, access draft and startup choice throughout review,
pipeline execution and exact-model Gateway verification. Interactive normal
new-setup review no longer offers a competing Local AI toggle; explicit config,
Settings recovery and headless contracts remain supported.

> **Status note (2026-07-06):** Current default setup includes `WindowsNodeBootstrapContextStep`, which injects Windows-node context into the WSL workspace `AGENTS.md` after onboarding.

---

## Architecture

![Setup Engine architecture layering](diagrams/setup-engine-layering.svg)

[Edit the setup-engine layering diagram](diagrams/setup-engine-layering.excalidraw).

---

## Project Structure

```
src/OpenClaw.SetupEngine/
├── OpenClaw.SetupEngine.csproj    # net10.0 library
├── Program.cs                     # callable entry: --config, --headless, --dry-run, --rollback-on-failure
├── SetupPipeline.cs               # Sequential step orchestrator (132 lines)
├── SetupContext.cs                # Config model + shared state bag (217 lines)
├── SetupSteps.cs                  # Shared setup-engine helpers (WslConstants, WslInstallSupport,
│                                   #   SetupOpenClawLogger, SetupPairingCredentialPolicy,
│                                   #   WindowsGatewayReachability); one file per step class lives
│                                   #   alongside it (e.g. CreateWslInstanceStep.cs,
│                                   #   ConfigureGatewayStep.cs, StartKeepaliveStep.cs, ...)
├── KeepaliveProcessManager.cs      # Setup-time WSL keepalive process/marker/rollback owner
├── TailscaleSetupSteps.cs         # The 4 Tailscale setup steps, grouped
├── TransactionJournal.cs          # Append-only JSONL journal (77 lines)
├── SetupLogger.cs                 # Structured JSONL logger (112 lines)
├── CommandRunner.cs               # Concrete WSL/process command runner
├── RetryExecutor.cs               # Exponential backoff retry
├── StubNodeCapability.cs          # Minimal capability stubs for pairing
└── default-config.json            # THE source of truth for all config values

src/OpenClaw.SetupEngine.UI/
├── OpenClaw.SetupEngine.UI.csproj # WinAppSDK library referenced by tray
├── SetupWindow.xaml / .xaml.cs    # 720×820 window, Mica, title bar, navigation, setup events
└── Pages/
    ├── SecurityNoticePage.xaml / .cs # Device-trust warning
    ├── WelcomePage.xaml / .cs        # Install WSL gateway vs connect existing
    ├── CapabilitiesPage.xaml / .cs   # Typed capability profile and transport choices
    ├── GatewaySetupPage.xaml / .cs   # Generated WSL installation review
    ├── GatewaySetupDetailPage.xaml / .cs # Full-window Local AI, Tailscale and consent views
    ├── ProgressPage.xaml / .cs       # Live step rows + gateway-installed handoff
    ├── WizardPage.xaml / .cs         # OpenClaw onboard transcript
    └── CompletePage.xaml / .cs       # Mascot status badge, summary, startup toggle
```

The pipeline runs 24 steps (see `SetupStepFactory.BuildDefaultSteps()` in `SetupPipeline.cs` for
the authoritative order. This doc's step table below predates the 4 Tailscale steps and is not
fully current). UI adds ~10 more files.

---

## Config File (`default-config.json`)

**Config is required.** Neither the headless exe nor the UI will run without one. The bundled `default-config.json` is auto-loaded from `AppContext.BaseDirectory` if no `--config` is specified.
If the setup UI cannot find, read, or deserialize the selected configuration,
it opens on the setup failure page with the load error and does not start setup.

New WSL distros use a 1-64 character name containing ASCII letters, digits,
periods, underscores, or hyphens, beginning and ending with a letter or digit.
Uninstall also accepts older names with spaces or Unicode when the name is one
safe Windows path segment and resolves to an immediate child of the app-owned
`LocalDataDir\wsl` root. Teardown rejects filesystem aliases, case or Unicode
normalization collisions, and reparse points at either the root or managed
child. It also preserves the VHD directory unless WSL confirms the distro is
absent or unregister succeeds. To replace such a legacy distro, uninstall it
first, using `--uninstall --confirm-destructive` and the same distro name, then
rerun setup with a supported new name.

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
    "existing-config-detected": "true",
    "config-handling": "keep",
    "quickstart": "true",
    "model-auth-provider": "skip",
    "default-model": "__keep__",
    "select-channel-quickstart": "__skip__",
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
    "FallbackVersion": null,
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

## Pipeline Steps (24 total)

> Note: this table predates the 4 Tailscale setup steps; the current pipeline runs 24 steps
> total. See `SetupStepFactory.BuildDefaultSteps()` in `SetupPipeline.cs` for the authoritative,
> current order. Fixing this table fully is out of scope for the E0 file-split PR.

Executed sequentially. Each step is a small class (30–120 lines) in its own file under
`src/OpenClaw.SetupEngine/` (e.g. `PreflightOsStep.cs`).

| # | Step Class | What It Does |
|---|-----------|-------------|
| 1 | `ValidateDistroInstallPathStep` | Validate the configured WSL install path before destructive setup |
| 2 | `PreflightOsStep` | Validate Windows 64-bit, version ≥ 22H2 |
| 3 | `PreflightWslStep` | Verify WSL is installed and supports direct named clean installs |
| 4 | `PreflightWindowsTailscaleStep` | Validate optional Windows Tailscale prerequisites |
| 5 | `CleanupStaleDistroStep` | Unregister a leftover WSL distro only when durable app ownership and the live current-user registration base path agree, and remove an orphaned VHD directory automatically only with a path-bound marker; explicit destructive confirmation may override |
| 6 | `CleanupStaleGatewayStep` | Stop orphaned gateway service, remove config |
| 7 | `PreflightPortStep` | Check gateway port is available |
| 8 | `CreateWslInstanceStep` | Directly install a fresh app-owned WSL distro; never export a user's Ubuntu distro |
| 9 | `ConfigureWslInstanceStep` | Write wsl.conf, create user, set dirs |
| 10 | `ValidateWslLockdownStep` | Verify WSL isolation settings are applied |
| 11 | `InstallCliStep` | Run install script inside WSL |
| 12 | `InstallTailscaleStep` | Install optional Tailscale support inside the managed WSL instance |
| 13 | `AuthorizeTailscaleStep` | Authorize the configured Tailscale identity and trust mode |
| 14 | `ConfigureGatewayStep` | Write gateway config (bind, port, auth) |
| 15 | `InstallGatewayServiceStep` | `openclaw gateway install --force` |
| 16 | `StartGatewayStep` | Start service, poll health endpoint (90s timeout) |
| 17 | `FinalizeTailscaleServeStep` | Apply the final Tailscale Serve endpoint after gateway startup |
| 18 | `MintBootstrapTokenStep` | Generate bootstrap token via CLI |
| 19 | `PairOperatorStep` | WebSocket operator connection + device approval |
| 20 | `PairNodeStep` | WebSocket node connection + capability registration |
| 21 | `VerifyEndToEndStep` | End-to-end health check (operator → node round trip) |
| 22 | `RunGatewayWizardStep` | Run/configure the gateway wizard unless skipped |
| 23 | `WindowsNodeBootstrapContextStep` | Inject Windows-node context into the WSL workspace `AGENTS.md` |
| 24 | `StartKeepaliveStep` | Background WSL keepalive to prevent VM shutdown |

The Windows port preflight requires a free port before installation. Installing
the gateway service can start it immediately, so the subsequent WSL port check
accepts listeners only when every reported owner PID matches the installed
`openclaw-gateway.service` systemd `MainPID` in the configured distro. A process
name such as `node` or `openclaw` alone is insufficient. Conflicts retain the
port-in-use error and include owning process names when available. Missing
listener ownership or a failed listener inspection does not bypass the check.

Setup operator and node sockets share the gateway record's full device identity.
Before the first operator or node pairing attempt, setup snapshots the pending
request IDs and retains that baseline across step retries. A gateway may refresh
the same request ID when the socket reconnects, so a fresh baseline on retry
would incorrectly classify setup's request as pre-existing. Finalization and
wizard sockets still take their own pre-connect snapshots. If a socket omits a
pairing request ID, setup selects exactly one request absent from its baseline
and matching the full identity, not a request already pending before setup, the
shortened display ID, or the only request in the queue. Missing identity, a
failed baseline on that attempt, no new match, or multiple new matches fail
closed. If the first baseline capture fails, retries take a new snapshot that
excludes any requests already pending, including requests from earlier setup
attempts. Later cleanup excludes requests that predate the retained successful
baseline. A socket-provided request ID still uses the exact device-approval path.

### Native Local AI integration

`BuildNativeLocalAiAcquisitionSteps` is an artifact-only pipeline: Windows OS/GPU
preflight, verified receipt reconciliation, pinned runtime/model acquisition and
receipt persistence. Unlike WSL recovery, it never installs WSL, changes mirrored
networking, starts a listener, forces inference, publishes a provider, or restarts
a Gateway. Its caller must retain exact native-session admission and return to an
explicit **Use Local AI** action after acquisition.

Native onboarding and Settings select this pipeline only after exact native
target admission. It does not participate in WSL registry settlement. Acquisition
returns to AI setup without claiming that a model is configured. Explicit Use
creates the durable native binding and starts the single app-owned authenticated
runtime. Existing setup verification proves the exact Gateway primary before
completion; the native session reconciles its Local AI revision after capability
configuration and before registry publication. Cancellation drains mutations and
withdraws the selected Local AI route before releasing the native session.
A damaged or foreign ownership receipt is never adopted implicitly.

### Local AI GPU admission

Local AI uses the CUDA driver's `cuMemGetInfo` total and free memory directly
for model qualification. DXGI and NVML dedicated-memory figures are not admission
caps: on the 48 GB RTX Spark SKU they can describe only the 16 GB carveout,
incorrectly excluding a supported unified-memory device. No separate shared or
host-memory estimate is added to the CUDA readings. Missing CUDA facts remain
retryable rather than becoming a definitive no-GPU verdict.

This qualification is not a guarantee of successful inference. Default setup
does not run inference to validate the selected model; the explicit inference
proof and recovery pipelines still do. Runtime failures remain runtime errors.

### Local AI Hugging Face cache rollout

Normal Local AI model acquisition writes verified GGUF files to the standard
Hugging Face hub cache selected by `HF_HUB_CACHE`,
`HUGGINGFACE_HUB_CACHE`, or the platform default. The installer reuses a
snapshot or content-addressed blob only through
`HuggingFaceHubCache.TryOpenVerifiedCacheFileAsync`, and cross-volume
materialization copies from that same verified open handle. A configured cache
root equal to or below the app-owned `LocalAI` directory is rejected before
mutation because uninstall removes that managed tree recursively.

### Local AI runtime archive cache

Verified llama.cpp runtime zips are kept in
`<LocalDataDir>\LocalAICache\archives\<sha256>\<file>`, next to the
uninstall-owned `LocalAI` tree, so reinstalls hash-verify and extract without
downloading. A cached zip is used only after a full SHA-256 check against the
compiled-in pin over the same open handle that extraction reads. A set is every
archive a runtime install pinned together (the llama.cpp binary zip and its CUDA
dependency zip). Only after the installed runtime passes inspection, and the
install was not cancelled, setup records the current pins as a completed set in
`<LocalDataDir>\LocalAICache\sets\<set-id>.json` and prunes: it keeps the current
pins plus every archive of the 3 most recently used older completed sets, and
deletes other entries. A rejected or cancelled install never deletes cache
entries. Archives left by a failed acquisition stay available for a retry of the
same pins, never count as a set, and are deleted by the next successful install
of different pins. An archive shared by several sets stays while any kept set
uses it. Set records are bookkeeping only: reuse is still gated by the SHA-256
pin check. Set `OPENCLAW_SETUP_LOCAL_AI_CACHE_RETAIN_SETS` to a non-negative
integer to change the number of older sets kept (`0` keeps only the current
pins); other values are ignored with a warning. Uninstall keeps the cache, which is bounded
by this retention and logged with its path so users can delete it to reclaim
space. Set `OPENCLAW_SETUP_DISABLE_LOCAL_AI_CACHE` to any value other than `0`
or `false` to skip all cache reads, writes, and pruning.

Manifest schema 3 remains the compatibility format for existing app-owned
model paths. Passive manifest loads, status refresh, recovery inspection, and
uninstall reads do not migrate it. Setup reconciliation is the explicit
promotion gate: it verifies and copies the legacy model, atomically records a
schema-4 cache receipt, then selects the verified snapshot path as active.
Fresh installs write schema 4 directly while retaining the legacy relative
`ModelPath` and a verified app-owned compatibility copy for recovery and for
rollback to schema-4-aware transitional builds containing #1388
(feat(local-ai): add verified legacy model cache migration). Schema-3-only
releases do not understand a schema-4 `state.json`; the retained model bytes
alone do not make a direct downgrade to those releases compatible. Rollback
may remove a compatibility copy created by the current transaction, but it
never removes the verified shared-cache source.

Non-destructive Local AI recovery keeps the exact pre-recovery receipt as its
rollback baseline. A successful repair always writes schema 4 with the verified
hub-cache snapshot as the active model path, while preserving the legacy
compatibility path and the prior gateway fallback, install time, and rollback
metadata.

Before restoring the original endpoint receipt, recovery resolves its model
through the same existing-file handle resolver used for native runtime launch.
The health probe compares physical model paths; receipts and cleanup ownership
remain logical. If resolution fails, rollback warns and keeps the replacement
receipt rather than probing with the unresolved alias.

Runtime upgrades validate the installed executable against its recorded runtime
release, not the current catalog release. A verified schema-3 model is migrated
to the hub cache before reuse by the new runtime, without downloading it again.
Normal setup keeps the pre-upgrade receipt separate from gateway recovery state.
If a later step fails, receipt persistence restores that baseline before runtime
acquisition removes the newly installed runtime. Reconciliation also restores
the baseline when setup fails after migration but before receipt persistence.
The old runtime, compatibility model, and verified shared-cache copy are retained.
Superseded runtime directories remain until explicit uninstall; upgrades do not
prune the rollback baseline.

Completed cache files and pre-existing resumable partials are shared state.
Setup rollback and uninstall do not delete them. Unsafe links, reparse points,
hard-linked partials, destination conflicts, receipt mismatches, and concurrent
manifest changes fail closed.

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

### Headless E2E guarded-restart diagnostics

The disposable `E2ESetupFixture` runs the same CLI entry point with an internal
failure observer. If the wizard step fails specifically at the guarded
post-wizard Gateway restart, the observer awaits a fixed, read-only
`systemctl --user show` probe **before** the normal owned-fixture rollback.
The resulting `gateway-restart-diagnostic.json` contains only an allowlisted
refusal category and coarse unit/state/PID-presence/start-identity-availability
facts, plus allowlisted service result and bounded exit code/status. Failed or
timed-out probes produce an explicit probe status, not raw
command output. The original setup failure and rollback are unchanged.

The public Gateway CLI does not expose the rejected owner-lease predicate,
so `ownerPredicate=not_exposed_by_gateway_cli` is intentional. An
`unverified` serving owner must not be interpreted as coordinator contention
without the separately observed typed contention error. This fixture-only
diagnostic neither retries the guarded restart nor preserves the distro after
rollback.

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
- `Config` - the loaded `SetupConfig`
- `Logger` - structured JSONL logger
- `Journal` - transaction journal
- `Commands` - `CommandRunner` for executing WSL/process commands
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

Log path defaults to `%APPDATA%\OpenClawTray\Logs\Setup\setup-engine-<yyyyMMdd-HHmmss>.jsonl` for setup and `uninstall-engine-<yyyyMMdd-HHmmss>.jsonl` for uninstall.

### Uninstall onboarding reset

The Inno uninstaller invokes `scripts\Uninstall-LocalGateway.ps1` directly,
not the C# uninstall engine. Both onboarding reset paths remove `GatewayUrl`
and legacy `Token` / `BootstrapToken` properties from `settings.json`, even
when node mode and autostart settings are preserved for remaining gateways.
Other preferences and external gateway records are preserved. The focused
`UninstallOnboardingSettingsTests` execute only the production PowerShell
reset and its JSON/logging helpers under Windows PowerShell 5.1 with temporary
files; they do not prove the full signed-installer uninstall path.
On a later launch, a URL-less legacy root identity is not migrated or used
against the default loopback URL. The profile must be reconnected explicitly.

---

## UI Flow

The WinUI app is a **thin shell** - no business logic, just rendering pipeline state. End-user UI runs default to `RollbackOnFailure=true`; `--no-rollback-on-failure` preserves an explicit debugging opt-out.

### Interactive flow: Welcome/trust → Gateway → PC capabilities and Windows access → WSL review → Installation → AI → native Chat

**SecurityNoticePage**
- Three feature rows and an inline trust notice using native SettingsCard controls

**WelcomePage**
- OpenClaw icon + "OpenClaw Setup" title bar
- Capability-checked native Gateway first and recommended; Windows Update/retry guidance when unavailable
- WSL and existing Gateway choices remain visible regardless of native availability
- The native choice enters PC capabilities before separate package preparation and its hosted wizard
- Read-only WSL readiness and existing-config checks, with inline error/retry
- Replacement confirmation occurs in the main-window review, not a modal

**CapabilitiesPage**
- `SetupWindow` owns one `SetupAccessDraft` tied to the same `SetupConfig`
- Only bundled all-on placeholders default to Standard, once; explicit Full/custom intent survives
- Three full-width presets; inline Fine-tune inspection preserves profile, edits mark Custom with no preset selected
- Draft retains Fine-tune disclosure and all eight flags across Back/Next
- Node mode, local MCP and Ollama sharing remain independent; no early persistence
- Windows privacy checks and runtime consent remain outside onboarding, in Companion Settings
- Managed Next goes directly to review; existing/remote to AI; MCP-only/deferred complete asynchronously here
- No separate permissions page, preview or progress dot; headless SkipPermissions is unchanged

**GatewaySetupPage / GatewaySetupDetailPage**
- Generated WSL, CLI and Gateway review plus the retained startup preference
- Focused Local AI and Tailscale controls retain generation-fenced probes and session-only options
- Separate full-window global networking consent and exact-distro replacement confirmation
- Only ManagedWsl can navigate to installation; alternate routes return the same config to the native connection host

**ProgressPage**
- Step rows with spinning ProgressRing → ✓/✗ badges
- Live activity ledger collapsed by default
- On success → focused AI setup, then native Chat without an extra success page
- On failure → navigates to Complete(success=false)

**WizardPage**
- Transcript-style gateway `wizard.*` flow for provider/model/key setup
- Error state uses More options plus gateway recovery actions when available

**CompletePage**
- OpenClaw mascot with corner status badge
- "All set!" / error heading
- Native InfoBar for node mode
- "Launch OpenClaw at startup" toggle defaults on and is persisted before restart
- Compatibility/resume and error surface; not an extra gate on successful focused setup

### Window Properties
- 720×820 logical pixels (DPI-scaled)
- Mica backdrop
- Custom title bar with OpenClaw icon

---

## CLI Usage

### Headless runner

```
OpenClaw.SetupEngine.Program.Main(args)                    # uses bundled default-config.json
OpenClaw.SetupEngine.Program.Main(["--config", "custom.json"])
OpenClaw.SetupEngine.Program.Main(["--headless"])
OpenClaw.SetupEngine.Program.Main(["--dry-run"])           # validate config, don't execute
OpenClaw.SetupEngine.Program.Main(["--rollback-on-failure"])
OpenClaw.SetupEngine.Program.Main(["--no-rollback-on-failure"])
OpenClaw.SetupEngine.Program.Main(["--log-path", "./trace.log"])
```

Common flags include `--config`, `--headless`, `--dry-run`, `--rollback-on-failure`, `--no-rollback-on-failure`, `--log-path`, `--gateway-port`, and uninstall safety flags such as `--uninstall` plus `--confirm-destructive`.
Normal setup uses npm `latest`. `Gateway.Version` may select an upstream npm
channel tag or an exact OpenClaw package version. `Gateway.FallbackVersion` may
name an exact stable release to offer after a typed compatibility failure.
Legacy `recommended`, `exact`, and `fallback` selections are migrated when the
configuration is loaded. Legacy recommendation and fallback configurations
without a recorded version follow the upstream `latest` and `extended-stable`
tags. Explicit legacy versions remain exact. Custom installers still require an
explicit exact version because they cannot resolve npm tags. The cross-repository
release gate may pass
`--gateway-candidate-package <absolute-tgz>` with
`--validate-gateway-candidate`, headless mode, and rollback-on-failure. The
package input is runtime-only and does not change normal npm setup.

SetupEngine option names are case-insensitive. Value options accept either separated
syntax (`--config custom.json`) or equals syntax (`--config=custom.json`). Unknown
options, bare `--`, and positional arguments are rejected with exit code 2.
Boolean flags do not accept values, and duplicate value options are rejected;
duplicate bare flags remain idempotent.

Duplicate value rejection is an intentional compatibility break from the legacy
first-value-wins behavior. Scripts that repeat a value option must remove the
duplicate before upgrading.

The same parser enforces the tray-hosted setup window's narrower command-line
contract: `--config` and `--no-rollback-on-failure`. The tray projects recognized
restart and deep-link host arguments out first. A restart PID must be a positive
integer other than the current process, and the post-setup launch target must be
`chat`; malformed host values remain for strict rejection. All remaining unknown options,
positionals, missing values, and duplicates render the setup failure page before
the setup lock is acquired. The tray executable's uninstall arguments are parsed
by `CliUninstallHandler` and currently use separated syntax for values such as
`--json-output <path>`.

Exit codes: 0 = success, 1 = pipeline failure, 2 = bad arguments or setup lock/safety failure, 3 = cancelled

### UI (hosted by tray)

``` 
OpenClaw.Tray.WinUI.exe openclaw://setup                   # opens/focuses hosted setup window
OpenClaw.Tray.WinUI.exe --post-setup-restart --wait-for-pid <oldPid> --post-setup-launch chat
```

The tray hosts `SetupWindow` from `OpenClaw.SetupEngine.UI`. After successful setup it starts a fresh tray process and exits, preserving clean post-setup state without shipping a second WinUI app.

---

## Build & Run

```powershell
# Build headless engine
dotnet build src\OpenClaw.SetupEngine\OpenClaw.SetupEngine.csproj

# Build tray-hosted UI
dotnet build src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -r win-x64

# Run hosted setup
& "src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\win-x64\OpenClaw.Tray.WinUI.exe" openclaw://setup

# Run headless uninstall through the tray executable
& "src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\win-x64\OpenClaw.Tray.WinUI.exe" --uninstall --dry-run

```

---

## Design Principles

1. **Config is explicit** - secure bundled defaults can be overridden by config file, environment, or flags
2. **Log everything** - every command, decision, and state change in structured JSONL
3. **Steps are small** - each step is a focused class, 30–120 lines
4. **Fail closed on approval** - setup validates approval request IDs and avoids ambiguous node approvals
5. **Clean-start guarantee** - stale state from prior runs is cleaned before proceeding
6. **UI is optional** - engine works identically without UI; UI is a passive observer
7. **Direct code-behind** - no MVVM, no ViewModels, no framework abstractions in UI
8. **Transactional** - journal + rollback on failure, enabled by default for the UI

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
