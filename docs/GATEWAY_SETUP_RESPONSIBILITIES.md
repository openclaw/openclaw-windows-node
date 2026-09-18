# Gateway setup responsibilities

Research snapshot: **2026-09-16**. This records the Gateway packaging assessment
and its comparison with WSL provisioning, so future integration work does not
duplicate existing owners.

The native Companion integration described here is work in this branch, not a
claim that it has shipped. It runs ordinary Windows user processes, not MXC
isolated sessions. Packaging findings refer to
[`openclaw-windows-packaging` at `3a0491c`](https://github.com/openclaw/openclaw-windows-packaging/tree/3a0491cf8790a3336d118486b66aeaa04dba7ba7).
The supplied `0.0.0.0` ARM64 proof package is older and must not be assumed to
provide current source's bundled-Node preparation behavior. This assessment used
source inspection and a read-only package-manifest inspection, not a live
isolated-session test.

## What Gateway packaging already provides

| Responsibility | Provided by Gateway packaging? |
|---|---|
| Prepare Node.js | **Yes, in the inspected source.** `clawctl setup` extracts or repairs the bundled architecture-specific Node runtime in package LocalState and verifies the OpenClaw entry point. |
| Configure models/providers and Gateway | **Through bundled OpenClaw.** `openclaw onboard` invokes upstream onboarding. `clawctl setup` itself does not perform onboarding. |
| Launch Gateway | **Yes.** `openclaw` forwards arguments to the packaged CLI, including Gateway commands. |
| Clean up launched descendants | **Yes.** The launcher owns a kill-on-close Windows job and waits for its Node child. |
| Persistent supervision/restart | **No.** The launcher sets external-supervisor flags and expects another owner to manage the overall lifecycle. |
| Provision isolated Windows agent identity/session | **No.** |
| Run onboarding and Gateway inside that session | **No.** |
| Stop/deprovision an MXC session | **No.** |

The packaging README explicitly reserves isolated agent sessions for future work.
MSIX integrity and read-only package files do not make the running Gateway an
isolated session.

Packaging-owned surfaces to reuse:

- [`Program.RunSetupAsync`](https://github.com/openclaw/openclaw-windows-packaging/blob/3a0491cf8790a3336d118486b66aeaa04dba7ba7/src/OpenClaw.Launcher/Program.cs):
  prepares the runtime and checks packaged application readiness.
- [`ClawCtlCommandLine`](https://github.com/openclaw/openclaw-windows-packaging/blob/3a0491cf8790a3336d118486b66aeaa04dba7ba7/src/OpenClaw.Launcher/ClawCtlCommandLine.cs):
  package readiness and launcher-version commands, not a second onboarding engine.
- [`GatewayLauncher`](https://github.com/openclaw/openclaw-windows-packaging/blob/3a0491cf8790a3336d118486b66aeaa04dba7ba7/src/OpenClaw.Launcher/GatewayLauncher.cs):
  ordinary process launch, upstream argument forwarding, external-supervisor
  environment and child lifetime.

Release `v0.0.0.1` was available at the time of inspection with x64 MSIX, ARM64
MSIX, and MSIX bundle assets. The future Store DLO is expected to target a bundle,
subject to confirmation when available. Windows selects its matching architecture;
the temporary ARM64 development input is not a product-wide restriction.

## Comparison with current WSL provisioning

### Implementation plan: package-aware native setup verification

Scope: make fresh-profile native setup reach the existing WinUI wizard using the
installed package's launcher and Node lifecycle. Preserve the manually paired
proof Gateway and all existing WSL behavior. Do not add MXC provisioning, change
machine deployment policy, or implement another onboarding UI.

1. Record the baseline and isolate proof. Use a new profile and free loopback
   port for each attempt. Never read provider credentials from, restart, or
   replace the user's working proof Gateway on port 18791.
2. Prove a supported Windows ownership mechanism before changing authorization.
   First test whether documented desktop-app process creation policy can
   preserve job inheritance with the package's existing Node supervisor. Prefer
   retaining the current strong job-membership invariant over inventing a
   weaker parent-PID rule. If it does not work, evaluate retained-handle
   attribution, with an explicit proof gate. Stop and identify the missing
   package capability if no trustworthy mechanism can be established.
3. Exercise real process lifetime and negative cases: unrelated listener,
   mismatched creation time, listener replacement, cancellation, launcher
   shutdown and child shutdown. A listening port or successful connection alone
   does not authorize credential handoff.
4. Integrate the proven mechanism in `WindowsNativeGatewayProcessHost` and
   `NativeGatewayRuntime`. Keep the package-qualified alias and the package's
   Node supervision. Share the verification result through setup, health checks,
   app connections and reconnects; do not relabel native records as remote to
   use a credential exemption.
5. Make native setup progress reflect package preparation, config validation,
   Gateway startup, endpoint verification and connection/pairing. Preserve
   bounded waits, actionable errors and cancellation. Keep profile state for
   retries; publish the Gateway only after wizard completion and final checks.
6. Prove `wizard.start` and cancellation on a fresh profile without answering
   security/provider prompts. Collect current-build visible shared-wizard
   evidence, or report the exact remaining blocker. Full interactive onboarding
   and provider consent remain user-owned.
7. Add focused regression tests and run the full build, Shared, Tray,
   SetupEngine and Connection suites plus strict MXC E2E regression validation.
   Run structured review and report any review-tool blocker without bypassing
   its protections.
8. Update this document, onboarding documentation and the architecture ledger
   with the actual mechanism, preserved invariants, exact proof and remaining
   limitations. No commit or PR publication is included in this request.

### Original native shared-wizard validation blocker

The 2026-09-16 disposable-profile proof with installed
`OpenClaw.Gateway_0.0.0.1_arm64__kaa03rpbbqef6` ran `clawctl setup` and
`openclaw config validate --json` successfully. Native startup then rejected the
loopback listener before any credential handoff:

- Listener PID/start time matched the live process, and its handle was readable.
- `IsProcessInJob(listener, companionJob)` returned `true`, with membership
  `false` and Win32 error `0`.
- The setup owner cleaned up; the proof port had no remaining listener, and
  no gateway record was published.

The original `WindowsNativeGatewayProcessHost.StartAsync` assigned the **alias launch** to a
kill-on-close job via `PROC_THREAD_ATTRIBUTE_JOB_LIST`.
`JobProcess.Owns` requires the actual listener to belong to that job. The
packaging source's `GatewayLauncher.RunAsync` creates its own
`WindowsKillOnCloseJob` for Node; this is not a documented handoff of listener
ownership to Companion. The observed membership result is the blocker, not
proof that the Gateway is isolated or that arbitrary loopback is safe.

#### Launcher ownership and shutdown comparison

A follow-up on 2026-09-16 used the installed release
[`v0.0.0.1`](https://github.com/openclaw/openclaw-windows-packaging/tree/3e5b1fff078e3ed46e9d2598835870a4e456717a),
fresh dedicated profiles and free loopback ports, without sending credentials or
wizard answers. It exercised Companion's actual process host and queried both
the job's process list and the listener's process identity:

| Launch path | Launcher in Companion job | Listener in Companion job | After stopping Companion job |
| --- | --- | --- | --- |
| Registered `openclaw.exe` execution alias | Yes, PID 45876 | No, Node PID 51588; parent PID 45876 | Port 61716 closed; launcher and Node exited |
| Physical installed `openclaw.exe` (diagnostic comparison only) | Yes, PID 19968 | No, Node PID 18904; parent PID 19968 | Port 62312 closed; launcher and Node exited |

The alias is not losing the launcher: Companion owns that launcher in both
cases. The package's `GatewayLauncher.RunAsync` keeps a
`WindowsKillOnCloseJob` alive while waiting for Node, so terminating the launcher
also terminates its workload. This confirms shutdown for both tested launches,
but does not make the listener a member of Companion's job or authorize sending
credentials based on parent PID alone. The exact Windows job-inheritance
mechanism behind the separation has not been established.

The integration gap at that point was a supported way for Companion to verify the package-owned
workload. Prefer an explicit, authenticated supervision handoff that retains the
package as Node lifecycle owner, rather than duplicating runtime resolution,
walking private launcher handles, trusting a reported PID, or bypassing package
activation. Current release and inspected `main` launcher source expose no
external job/process-handle handoff. An explicit handle-handoff contract would require coordinated package and
Companion changes. The implementation below instead uses local, retained-handle
process attribution with the existing package. Neither direct executable launch
nor a longer startup timeout alone fixes the original membership check.

Local proof outputs: `TestResults\launch-contract-proof\alias-result.log` and
`TestResults\launch-contract-proof\direct-result.log`. Both proof process trees
were confirmed exited. The existing interactive setup process was left untouched.

The installed upstream `app\dist\wizard-Df2L6HWQ.js` does expose
`wizard.start/next/cancel` and accepts `mode` and `installDaemon`; there is no
need for a second wizard or undocumented flags. A supported package activation
and process-ownership handoff is required before proving that RPC from
Companion. Direct executable launch or adopting a PID is not an approved
production fallback. Shared-page rendering and provider authorization
remain unverified; no onboarding/security answer was sent by the proof.

#### Package-aware implementation results

The implementation uses the existing package without adding a cross-repository
protocol or another Node supervisor:

- `WindowsNativeGatewayProcessHost` creates the launcher suspended, assigns its
  kill-on-close job, retains a process handle while the original creation handle
  is still held, then resumes the launcher. Failures terminate the created
  process rather than continuing without job ownership.
- `PROC_THREAD_ATTRIBUTE_JOB_LIST` creation failed with access denied while the
  user's package was already active. Both the original implementation and the
  desktop-app-policy experiment failed under those conditions. The policy
  experiment therefore does not establish a policy-specific failure. Suspended
  creation followed by job assignment succeeded alongside the existing Gateway.
- `WindowsPackagedProcessAncestry` accepts a live same-user descendant only when
  anchored to that exact, still-job-owned launcher and its expected package
  family. It retains all traversed handles, rejects cycles and excessive depth,
  and checks parent/child creation ordering. It does not trust a cached parent
  PID, executable name or unrelated listener. The runtime still checks listener
  creation time, loopback-only binding and two consistent TCP snapshots.
- Setup, health checks and reconnect authorization continue to use the same
  runtime. Native records do not use the remote credential exemption.
- Startup/verification progress is explicit and dispatched to the WinUI thread.
  A fresh setup identity is paired automatically: `NativeGatewaySetupSession`
  checks the handshake request ID against the pending request's device ID and
  public key, then approves only that exact request through the package CLI.
  It verifies package listener ownership and local-profile configuration before
  listing and approving. The failed client is disposed to cancel its reconnect
  loop; `WizardPage` creates a new client for one bounded retry. Unrelated devices
  and onboarding consent are never approved. The profile-specific terminal is
  retained for recovery only.

The installed CLI rejects `--url` when credentials are supplied only through the
environment. Pairing therefore uses `devices list --json` and
`devices approve <requestId> --json` against the verified dedicated local profile.
Setup clears inherited `OPENCLAW_GATEWAY_URL` and pins `OPENCLAW_GATEWAY_PORT`;
`OPENCLAW_GATEWAY_TOKEN` is supplied only in the child environment. No token is
placed on argv, and no private Gateway pairing-storage format is assumed.

| Proof | Result |
| --- | --- |
| Package-aware listener attribution | Passed with installed ARM64 0.0.0.1 |
| Unrelated listener and mismatched creation time | Rejected |
| Replacement listener after shutdown | Rejected |
| Supervisor exits without running disposal | Launcher PID 67480 and Node PID 55832 exited when Windows closed the job handle |
| Existing paired proof Gateway | Port 18791 remained on PID 17788 |
| Fresh-profile ownership, automatic pairing, authenticated connection and wizard RPC | Production setup automatically approved the exact disposable identity request after device ID/public-key matching; `wizard.start` returned `type=note`, `title=OpenClaw setup`. No manual terminal approval. |
| Wizard cancellation | Runtime stopped; no Gateway registry publication |
| Full repository build | Passed |
| Shared tests | 3,983 passed, 33 skipped |
| Tray tests | 2,995 passed, including terminal-error display regression cases |
| SetupEngine tests | 1,238 passed, 1 skipped, including 14 automatic-pairing regression cases |
| Connection tests | 870 passed, 1 skipped |
| Strict WSL-to-Windows-node MXC E2E | 17 passed |
| Full upstream optional tail | Not repaired: developer captured `PreparedModelCatalogConfigReplacedError` after Optional apps. Shortened setup now ends explicitly before this tail; see below. |
| `python .agents\skills\autoreview\scripts\autoreview --mode local` | Blocked by the helper's secret-like-content guard on existing untracked setup/test files; no clean review claimed |

Evidence is local under `TestResults\launch-contract-proof\`,
`TestResults\native-auto-pair-proof.log` and the
`TestResults\native-auto-pair-*-tests.log` validation logs. The fresh-profile
proof invokes the production setup host/session, verifies authenticated
`hello-ok`, opens and cancels the first wizard step, and checks no registry
publication. It does not submit security, provider or model answers.
One Shared in-flight MCP disposal test failed on the first suite run, then passed
both its focused rerun and the full required-validation rerun.

The subsequent interactive run also exceeded the original 30-second pairing CLI
budget. A same-profile read-only probe later succeeded in 4.6 seconds, and the
developer's retry paired successfully. Pairing commands now use the same
two-minute total process budget as configuration/health checks. This includes
packaged runtime startup, not just RPC execution. Current diagnostic-fix
validation logs use the `TestResults\native-terminal-` prefix. An attempted
recovery of the old masked error found an overwritten response buffer; the
temporary process dump was removed.

### Shortened onboarding implementation and proof

The same `WizardOnboardingPolicy` now drives native and WSL `WizardPage` plus
the headless WSL runner. It acknowledges the audited informational notes and
selects offered skip/keep values for optional configuration. Security and telemetry
consent, agent name, provider/auth/model choices, permissions and errors remain
explicit. Unknown prompts remain visible rather than receiving guessed answers.

`WizardOptionalSetupHandoff` owns the Optional apps checkpoint: do not acknowledge
that note, explicitly cancel the remaining upstream wizard, require cancellation
confirmation, then require valid saved config and authenticated Gateway health.
Native setup records a separate deferred-optional state, not `wizardCompleted`,
and still performs reload restoration, CLI config validation, owned listener
verification and health before registry publication. WSL retains its existing
service ownership, reload restoration and Windows-node context lifecycle.
User cancellation and terminal errors cannot take this handoff.

Historical disposable production-host/session proofs from the original native
integration, before the subsequent onboarding UI updates:

| Proof | Result |
| --- | --- |
| Fresh ARM64 MSIX 0.0.0.1 profile | Automatic exact-identity pairing, authenticated wizard RPC, all requested optional steps bypassed, validated optional-tail cancellation, final config/owned-health checks and disposable registry publication passed |
| Existing configuration | Repeated the complete proof with enabled web search (`maxResults: 3`) and existing disabled Telegram/pairing configuration; both subtrees remained exactly equal |
| Safety/provider answers | Test-only authorization accepted safety and skipped provider credentials/integrations; real UI consent remains explicit |
| Historical full build | Passed |
| Historical Shared / Tray suites | 3,983 passed / 33 skipped; 2,996 passed |
| Historical SetupEngine / Connection suites | 1,285 passed / 1 skipped; 870 passed / 1 skipped |
| Historical strict WSL-to-Windows-node MXC E2E | 17 passed, including real Gateway `system.run` execution and denied writes to tray data |
| Native and WSL UI/headless wiring | Shared-policy and handoff source contracts passed, plus behavioral policy, failed-gate, cancellation and native publication tests |
| Visual UI and live shortened WSL wizard | Not yet verified by these RPC proofs; native production lifecycle was exercised without clicking the WinUI page |
| Structured autoreview | Still blocked by the existing untracked-file secret-like-content guard; no clean review claimed |

Two earlier 2026-09-18 strict MXC attempts were blocked by WSL distro import
failure and Gateway restart failure during WSL wizard setup. After source-path
sanitization and native handshake/finalization fixes, the current-source Release
strict MXC run passed all 17 tests with no skips. The historical table above is
not the evidence for that rerun. The updated native UI has visibly advanced
through package/profile preparation into the shared wizard, but full interactive
native completion and normal-owner handoff remain unverified.

Logs: `TestResults\short-wizard-live-proof.log`,
`TestResults\short-wizard-preservation-proof.log`,
`TestResults\short-wizard-build.log`, and `TestResults\short-wizard-*-tests.log`.
Strict Gateway-to-node proof is in `TestResults\short-wizard-mxc.log`.
The first Shared run hit the existing in-flight MCP disposal race; the focused
rerun and subsequent full build/all-suite rerun passed. The first preservation
fixture selected unavailable Brave integration and correctly failed config
validation; the successful fixture uses valid provider-independent search config.

### Local chat smoke and remaining setup gaps

The developer's native Gateway was verified as a Windows process listening on
loopback, with its own configuration, agent workspace and `SOUL.md`. The browser
Control UI and Companion connected to the same Gateway. After explicit GitHub
Copilot device authorization, the profile selected
`github-copilot/claude-sonnet-5`; a real `chat.send` turn completed successfully
and `chat.history` contained the requested `READY` reply.

These local recovery steps are **not automated by the source changes**:

- The selected OpenAI model initially needed the separate
  `@openclaw/codex@2026.8.2` runtime. Installation required explicit capability
  consent. Plugin registration and authenticated Gateway health then passed.
- The installed Codex executable existed, but its 285-character development
  profile path caused Node's ordinary Windows spawn to return `ENOENT`. The
  namespaced Windows path launched successfully and completed an app-server
  `initialize` handshake. The local profile used the documented
  `plugins.entries.codex.config.appServer.command` override, without editing
  installed package files. This is a machine-local workaround, not a general
  launcher fix; an install-location change requires revisiting the override.
- A subsequent OpenAI turn reached the provider but lacked authentication.
  GitHub Copilot was then selected and authorized for this profile. Gateway
  pairing tokens are not AI-provider credentials.

First-run chat selection still needs a source fix. `ChatSnapshotProjector` can
restore a remembered background session, and `ChatPresentationState` can persist
the first listed session when the main chat is unavailable. This left the
automation "Memory Dreaming Promotion" selected instead of a normal conversation.
Creating a regular conversation avoids that placement error, but setup should
select or initialize a normal default conversation itself.

Follow-up work must validate the selected model's required runtime/authentication
and a successful first conversation, not just Gateway health. The working local
profile, provider credentials, installed plugins, launch override, MSIX payloads
and raw proof artifacts are intentionally excluded from the commit.

**Limits:** This is same-user supervision, not a security boundary against
malicious code holding that user's process-creation/injection rights. There is
also a narrow pre-assignment crash window: forced Companion termination between
successful suspended creation and job assignment can leave a suspended launcher.
No launcher code has been resumed in that window. Kill-on-close cleanup is
proven after assignment, not before it. No claim of atomic job-list creation is
made. The observed host could not tighten the proof identity's ACL through the
existing best-effort helper; its warning is retained in the proof log. No real
provider credentials were placed in the disposable profile.

Rubber-duck review found a background-thread progress callback that could reject
otherwise healthy reconnects. The callback now marshals through the captured
WinUI dispatcher and ignores updates after its page unloads; source-contract
coverage guards that wiring. The full build and four suites above were rerun
after the fix. Structured review command
`python .agents\skills\autoreview\scripts\autoreview --mode local` remains blocked
by the review helper's secret-like-content guard on existing untracked setup and
test sources. No clean autoreview result or guard bypass is claimed.

Companion owns the WSL environment's lifecycle end to end. It delegates Gateway
installation, onboarding and service commands to upstream OpenClaw, while WSL
and systemd provide execution and service management.

| Responsibility | Current WSL path | Native Gateway MSIX path in this branch |
|---|---|---|
| Check host prerequisites | **Companion:** OS, WSL readiness, virtualization and port checks. | **Companion:** actual MXC session-capability probe for recommendation, then package/alias checks. This does not provision an isolated session. |
| Provision isolated environment | **Companion:** creates the app-owned WSL distro. | **Not implemented:** MSIX installation does not create an isolated session. |
| Create workload identity | **Companion:** creates a Linux user inside the distro. | **Not implemented:** runs as the signed-in Windows user. |
| Configure environment restrictions | **Companion:** writes `wsl.conf`, configures automount/interop and validates lockdown. | **Not implemented for MXC sessions.** |
| Install Node and Gateway payload | **Companion orchestrates the upstream installer** inside WSL. | **Packaging owns payload delivery; `clawctl setup` prepares bundled Node** in current packaging source. |
| Acquire missing Gateway package | Not applicable. | **Companion:** automatically opens Windows App Installer once, then waits up to five minutes for validated current-user registration. Windows owns consent/signature/deployment; cancellation only stops Companion's wait. |
| Select Windows capabilities and permissions | **Shared `CapabilitiesPage`:** profiles, toggles and Windows permission checks. | **The same page**, with native review instead of WSL/Local AI/Tailscale provisioning. Selected Gateway commands are applied before validation; only node/capability flags are merged into Companion settings. |
| Prepare Gateway configuration | **Companion:** configures endpoint/auth and optional integrations. | **Companion:** prepares a dedicated profile; packaged CLI consumes it. |
| Provider/model onboarding | **Upstream OpenClaw wizard**, rendered by the shared WinUI `WizardPage`. | **The same `WizardPage` and upstream `wizard.start/next/cancel`**, over a verified native runtime endpoint. No separate TUI or provider UI. |
| Install persistent Gateway service | **Companion calls `openclaw gateway install --force`; upstream installs the systemd user service.** | **No service installation:** launcher declares external supervision. |
| Start/stop/restart Gateway | **Companion requests actions; upstream CLI/systemd execute them.** | **Companion runtime manages launcher lifetime; package manages its Node descendants.** |
| Detect failure and recover | **systemd service management plus Companion's managed-local repair monitor.** | **Companion reconnect can restart its owned runtime; packaging has no persistent restart supervisor.** |
| Keep hosting environment available | **Companion's WSL keepalive service.** | No separate environment in the current non-isolated path. |
| Connect, pair and verify | **Companion:** bootstrap/operator/node pairing and end-to-end checks. | **Companion:** health verification and connection/pairing handoff. |
| Remove hosting environment | **Companion:** ownership-guarded distro rollback/removal. | Windows removes the package; **MXC session deprovisioning does not exist yet.** |

The distinction is **orchestrates versus implements**. Companion does not
reimplement OpenClaw's onboarding or systemd service installer. It invokes those
inside the environment it provisioned.

Local source references:

- `src\OpenClaw.SetupEngine\NativeGatewaySetupSession.cs`: staged native runtime,
  reload restoration, config/health gates and registry publication. Native
  cancellation does not finalize setup; original startup preference is preserved.
- `src\OpenClaw.SetupEngine\WizardOnboardingPolicy.cs` and
  `WizardOptionalSetupHandoff.cs`: shared deferred-feature defaults and the
  explicit cancellation/config/health checkpoint, not an upstream completion claim.
- `src\OpenClaw.SetupEngine.UI\Pages\WizardPage.xaml.cs`: shared hosted RPC and
  provider/auth/model rendering. Native branches delegate lifecycle to the native
  session and do not call WSL recovery or Windows-node WSL context injection.
- `src\OpenClaw.SetupEngine\SetupPipeline.cs`: overall WSL provisioning sequence.
- `src\OpenClaw.SetupEngine\ConfigureWslInstanceStep.cs`: Linux user,
  directories and WSL configuration.
- `src\OpenClaw.SetupEngine\InstallCliStep.cs` and
  `InstallGatewayServiceStep.cs`: upstream installation commands.
- `src\OpenClaw.SetupEngine\RunGatewayWizardStep.cs`: Gateway onboarding delegation.
- [Connection architecture](CONNECTION_ARCHITECTURE.md): ongoing managed-local
  WSL supervision and repair.
- [Native setup documentation](ONBOARDING_WIZARD.md#native-gateway-msix-not-isolated):
  the branch's non-isolated native implementation.

## Decided ownership and persistence for MXC sessions

**2026-09-18 UI decision:** native setup now recommends the existing signed-in-user
Gateway after a successful session-capability probe, with WSL under collapsed
alternatives. The separate isolation warning/acknowledgment is removed by design.
`NativeGatewaySetupEligibility` uses the actual `probes.isolationSessionAvailable`
field, replacing the `IsolationProxy.exe` heuristic for this capability signal.
Negative results offer Windows-update guidance; failed or incomplete checks offer
retry/repair. This does not implement the future isolated-session lifecycle below.
See [the current architecture](ARCHITECTURE.md#capability-recommendation-and-windows-update).

The product decision on **2026-09-16** is that **Companion owns the full MXC
Gateway lifecycle**, matching its WSL orchestration role. Packaging remains the
runtime-preparation and CLI launcher owner, and upstream OpenClaw remains the
onboarding owner. This is a required boundary, not an implemented session path.

Companion must:

- Provision the isolated Windows agent identity/session through MXC, then run
  package preparation, upstream onboarding, and Gateway inside that identity.
- Preserve the same agent identity and configuration across Companion restarts.
  A restart must neither re-provision nor re-onboard.
- Supervise Gateway with a bounded crash-restart budget. Explicit stop must
  cancel recovery so a pending retry cannot resurrect the workload.
- Stop the session on Companion exit without deleting the account/profile.
- Deprovision only on explicit removal. Retain the saved identity and recovery
  information when removal fails or its outcome is unknown.
- Keep listener ownership verification before sending Gateway credentials.
  A loopback response or a live `wxc-exec` process is not ownership evidence.

Do not independently implement account creation or session teardown in both
repositories. OS eligibility alone does not make the Gateway session-isolated. The
[planned setup recommendation policy](ONBOARDING_WIZARD.md#planned-mxc-native-gateway-recommendation-policy)
requires a working session integration before recommending it as isolated.

## Verified MXC 0.8 contract and implementation blockers

The installed `node_modules\@microsoft\mxc-sdk\package.json` reports **0.8.0**.
The matching upstream release is
[`7dac1a9`](https://github.com/microsoft/mxc/tree/7dac1a952f0c9ad13f0a4cb089c4e0e8b3e0013a),
not the newer `0.9.0-alpha` contract on current main.

### Persistence is supported by the state-aware contract

The pinned [state-aware runtime specification](https://github.com/microsoft/mxc/blob/7dac1a952f0c9ad13f0a4cb089c4e0e8b3e0013a/docs/isolation-session/state-aware-rust.md)
explicitly states that the session/account outlive the executor process.
`stop` ends the session but preserves the agent user; `deprovision` removes it.
The implementation creates a fresh manager for each phase in
[`state_aware.rs`](https://github.com/microsoft/mxc/blob/7dac1a952f0c9ad13f0a4cb089c4e0e8b3e0013a/src/backends/isolation_session/common/src/state_aware.rs).
This supports the chosen persistence semantics at the API level. The initial
read-only review did not create an account or session. The authorized live
follow-up below verifies identity lifecycle, but remains blocked on activation.

The shipped `dist\state-aware-helper.js` defaults IsolationSession requests to
**`0.6.0-alpha`**. Its provision config requires
`network: { defaultPolicy: 'allow', allowLocalNetwork: true }`, not the newer
directional network schema. `appId` identifies the calling application
(`PFN:<packageFamilyName>` for a packaged caller); it is not a documented
installation request for a different package. Persist the returned opaque
`sandboxId`; do not reconstruct it from a username or SID.

Important recovery limits:

- Provision is not idempotent: every call creates a new identity. Never repeat
  it automatically after losing the response.
- Repeated start/stop have OS-dependent errors reported as `backend_error`.
  There is no state-query/enumeration phase in the shipped MXC lifecycle API.
  Do not interpret every failed start as "already running", or every failed stop
  as "already stopped".
- The state-aware account/session can outlive the supervisor. Putting only
  `wxc-exec` in the existing kill-on-close job does not establish session
  cleanup on an abrupt Companion crash.

### Capability evidence is not readiness evidence

The read-only host-architecture `wxc-exec.exe --probe` invocation on 2026-09-16
returned exit code 0 with `probes.isolationSessionAvailable: true`,
`tier: "base-container"`, and `needsDaclAugmentation: false`. No feature flags
were changed. The session field, not the process tier, is the relevant signal.

The pinned SDK's `dist\platform.js` consumes that boolean. The current
`MxcAvailability` C# implementation instead combines AppContainer support with
`IsolationProxy.exe` file presence; that heuristic must not gate the new path.
Moreover, upstream
[`availability.rs`](https://github.com/microsoft/mxc/blob/7dac1a952f0c9ad13f0a4cb089c4e0e8b3e0013a/src/backends/isolation_session/common/src/availability.rs)
maps both a failed native API probe and a zero feature level to `false`.
Process-launch/JSON failures can be distinguished locally, but that boolean
alone cannot distinguish all OS API failures from unsupported Windows. A richer
probe result is needed before promising that distinction or recommending an OS
update for every negative result.

### Blocking package and ownership contract

The existing `NativeGatewayPackageResolver` resolves **current-user**
registration and aliases under that user's LocalAppData. Those paths are not
proof of registration or activation under a newly provisioned agent identity.
The Gateway manifest declares both aliases against one `openclaw.exe`;
[`HostEntrypointResolver`](https://github.com/openclaw/openclaw-windows-packaging/blob/3a0491cf8790a3336d118486b66aeaa04dba7ba7/src/OpenClaw.Launcher/HostEntrypoint.cs)
selects `clawctl` from the invoked name. Running the physical `openclaw.exe`
with `setup` is therefore not equivalent to invoking `clawctl setup`.

Neither inspected project's documented contract establishes how this separate
Gateway package is registered/activated for an MXC agent user. MXC's
`RunProcessWithOptionsAsync` wrapper launches `cmd.exe /c` in that session; it
does not expose a package deployment operation. Do not invent cross-user alias
paths, copy launcher binaries, or grant access to the signed-in user's runtime
and credentials as a workaround.

The provided `0.0.0.0` ARM64 MSIX was inspected read-only: its manifest declares
the aliases, but it contains no bundled Node executable/archive. It is not
evidence that the latest packaging preparation contract works in an agent
profile. No package was installed, upgraded, or downloaded during this review.

Listener provenance is another required proof: the current native runtime uses
membership in its own job. MXC launches the workload through an OS service in
another identity/session, so that same test cannot simply be retained or
replaced by a successful health request.

**Implementation is blocked, not proven impossible.** The initial review called
for approval of a bounded, disposable integration proof on an eligible test host,
using an approved bundled-runtime Gateway artifact: register/activate the package
for the agent identity, run `clawctl setup`, verify profile persistence across
stop/start and supervisor restart, and establish listener attribution without
real provider credentials. Explicit approval must cover account/session creation,
package deployment, and removal of that disposable identity. That approval was
subsequently granted and the bounded proof was executed below. Companion session
ownership and the preservation policy do not need to be reconsidered.

### Authorized live proof: 2026-09-16

**Outcome: do not implement the isolated Gateway path yet.** The read-only MXC
probe and disposable identity lifecycle work, but registration of the actual
Gateway package for that identity is rejected by Windows policy. This is no
longer a user-permission blocker, and it is not a missing bundled runtime.
Existing native Gateway source remains the previous nonisolated implementation;
none was replaced or promoted to an isolated implementation in this proof.

Evidence is retained locally under `TestResults\mxc-disposable-proof\`
(ignored, not committed). The exact lifecycle requests and responses are JSON
files alongside the bounded runner `lifecycle.mjs`. Each invocation uses a fresh
Node process and the shipped SDK helper from 0.8.0, emitting the
`0.6.0-alpha` IsolationSession envelope. Provision ran exactly once, with a
write-once attempt record before the call and the returned opaque ID saved in
`provision-result.json` before start.

#### Artifact verification and preservation

- Release: [`v0.0.0.1`](https://github.com/openclaw/openclaw-windows-packaging/releases/tag/v0.0.0.1),
  packaging commit `3e5b1fff078e3ed46e9d2598835870a4e456717a`.
- Artifact: `OpenClawGateway-0.0.0.1-arm64.msix`.
- SHA-256: `b5d7241a2a1b4870eb67dffadfedd04b71a29bfde92e6366827b3dd67444ae17`,
  matching GitHub's release asset digest.
- `Get-AuthenticodeSignature`: `Valid`, `Signature verified`.
  Signer: `CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US`.
  Signer thumbprint: `D2767C9CA44650FD3D257D881CFF40240C97EF94`.
  The signature includes a Microsoft timestamp; no certificate was trusted or
  installed by this proof.
- ZIP inspection confirms `runtime/node-v24.20.0-win-arm64.zip` (33,621,271 bytes).
- Contrary to the earlier local-artifact observation, the current-user package
  was already `OpenClaw.Gateway_0.0.0.1_arm64__kaa03rpbbqef6` when this live proof
  began. No current-user install/update/uninstall was necessary or performed.
  The installed launcher SHA-256 matched the verified release's launcher:
  `c38bb6a4a6ed64306631e430df8276112cbd70b2497fc9f7d2000e76302aafad`.
  The same package full name and install location remained after cleanup.

#### Live results and limits

| Check | Actual result |
| --- | --- |
| `bin\arm64\wxc-exec.exe --probe` | Exit 0, `isolationSessionAvailable=true`; no feature or policy changes |
| Provision | Exit 0, account `D5-Y4`, SID `S-1-5-21-3948813628-2141399506-1640693369-1002`; shared workspace returned by MXC |
| Start and independent exec caller | Exit 0, agent SID matches provision; workload runs in session 2 rather than controller session 1 |
| Package/alias discovery inside agent | No `OpenClaw.Gateway` registration; neither `clawctl.exe` nor `openclaw.exe` resolved with `Get-Command` |
| Exact package registration inside agent | Failed with `0x80073D23`, special-profile deployment policy; details below |
| `clawctl setup`, bundled Node extraction, Gateway activation | Not run: no registered alias, so the required activation prerequisite failed |
| Caller restart plus stop/start | Separate controller processes successfully addressed the saved ID; after stop/start the same account, SID and profile path ran in session 3 |
| Profile content persistence | Not conclusively proven: the diagnostic marker helper creates a missing marker, so equal marker text alone cannot establish survival. Repeat with a read-only verification phase once activation is unblocked |
| Credential-free TCP listener | `127.0.0.1:57227`, host TCP owner PID 35876 and host process session 2 matched the agent report; loopback HTTP responded |
| Host-side SID attribution | **Not established**: `Win32_Process.GetOwnerSid` returned 2 (access denied), and host `ExecutablePath` was unavailable. Do not substitute the workload's self-reported SID or a successful HTTP response for trusted ownership |
| Stop while listener active | Stop exit 0; PID 35876 disappeared, no listener remained on 57227. Attached workload exited `3221225786` (`0xC000013A`) |
| Explicit cleanup | Stop exit 0, deprovision exit 0; post-cleanup exact-SID queries found zero accounts and zero profiles, and the returned shared directory no longer existed |

The exact disposable opaque ID, retained for audit rather than reuse, is:

```text
iso:eyJ2ZXJzaW9uIjoxLCJhZ2VudFVzZXJOYW1lIjoiRDUtWTQiLCJhcHBJZCI6Ik9wZW5DbGF3LkNvbXBhbmlvbi5EaXNwb3NhYmxlUHJvb2YuYzQ5NjZlYjMifQ
```

Only that returned ID was passed to stop/deprovision. No other account, profile,
package, running user workload, provider setup, or user settings were removed or
modified by the proof. The listener had its own 90-second deadline in addition
to the MXC exec timeout. No provider credentials or user environment secrets were
passed to the workload.

#### Exact activation blocker and upstream action

The documented [Add-AppxPackage](https://learn.microsoft.com/en-us/powershell/module/appx/add-appxpackage?view=windowsserver2025-ps)
`RegisterByPackageFullNameSet` was used from inside the agent identity, targeting
the existing signed package by its actual full name:

```powershell
Add-AppxPackage -Register -MainPackage 'OpenClaw.Gateway_0.0.0.1_arm64__kaa03rpbbqef6'
```

It returned:

```text
Deployment failed with HRESULT: 0x80073D23
The deployment operation was blocked because Special profile deployment is not allowed.
The package deployment operation is blocked by the "Allow deployment operations in special profiles" policy.
ActivityId: 6e5a4865-454d-0004-3203-54704d45dd01
```

`Get-AppPackageLog -ActivityID 6e5a4865-454d-0004-3203-54704d45dd01`
confirms `RegisterByPackageFullName`, the exact test SID, and failure in
`BlockDeploymentIfNeeded` (events 441/605/401/404). This is registration of a
staged signed package, not a fabricated alias path or launcher copy.
The [special-profile deployment policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-appxpackagemanager#allowdeploymentinspecialprofiles)
is machine policy; this proof did not change it.

The pinned MXC
[`IsoSessionOperations` bindings](https://github.com/microsoft/mxc/blob/7dac1a952f0c9ad13f0a4cb089c4e0e8b3e0013a/src/backends/isolation_session/bindings/src/bindings.rs#L1577-L1755)
expose add/remove user, start/stop session, process execution and feature queries.
They expose no package-registration or package-activation operation.
`IsAppScopedRegistrationSupported` and `AddUserAsync2` concern agent-user
association with the calling app; this evidence does not establish registration
of the separate Gateway MSIX. No other documented activation path was established.

Required upstream action before implementation:

1. MXC/Windows and packaging owners must supply a supported registration and
   alias-activation contract for a separate signed Gateway MSIX in a local agent
   special profile, without machine-wide policy/ACL relaxation. If policy
   configuration is inherently required, document that prerequisite and its
   security implications explicitly; this proof's authorization does not cover
   changing it.
2. Supply or prove a trusted, non-elevating workload-ownership query usable by
   Companion across the agent session (stable process identity, session/SID and
   creation/lifetime checks). The existing same-job provenance test cannot be
   carried over, and the tested CIM owner query is denied on this host.
3. Then repeat current-head proof of actual `clawctl` activation and setup,
   read-only profile-content verification after stop/start and controller
   restart, bounded supervision, and explicit-only removal.

No product code or UI was changed in this proof follow-up. Required repository
build/test suites were not rerun for this documentation-only product change;
the live command results above are integration evidence, not substitutes for
the required build, Shared, Tray, SetupEngine and Connection suites when the
implementation proceeds. No commit or push was performed.
