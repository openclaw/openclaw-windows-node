# Onboarding Wizard

The onboarding wizard can install an app-owned WSL gateway, acquire a native
Gateway MSIX through Microsoft Store and configure it, or connect to an existing gateway.

Native Local AI setup displays the current phase and completed steps while checking
GPU readiness, verifying cached runtime/model files, starting the model, publishing
the provider, reconnecting and testing real Gateway inference. File verification
can take a minute or more for large models; it is not a download. Artifact acquisition
retains the installation progress page and its actual download progress.

Reopening an incomplete native Local AI setup uses this Companion profile's saved
Gateway ownership to offer **Recover and use Local AI**, including pending
configuration writes not yet reflected in runtime status. Recovery preserves the
healthy managed listener and uses the existing exact-model verification and
conditional configuration guards, rather than reinstalling it.

An existing provider without matching saved ownership remains unmanaged by this
profile. Setup explains how to reopen the original Companion profile and reconnect
its original Gateway. Keep the ownership receipt; do not copy credentials or
replace the provider. An independently detected **Use this model** choice remains
separate and does not claim or restore Companion management.

After setup restarts Companion, destination handoff waits for native connection
startup and any already-admitted Local AI recovery before fresh model verification.
An unused receipt must still be acquired within five minutes. First exclusive
acquisition persists one 13.5-minute execution window for the bounded phases below;
neither restart nor retry renews it. A failed attempt remains available for explicit Retry,
but automatic reactivation does not show the same failure dialog again. Successful
runtime checks alone do not prove that a full, potentially longer chat turn has finished.

Native installation keeps Back at the left and Cancel setup (or Retry setup) at
the right, using the same content-sized, 100 px minimum buttons as earlier setup
screens. Progress stays centered on its own row and action labels can wrap.

The Gateway chooser shows Local AI availability in a subtle, transparent status row
below all three choices and above "Remote, local MCP or set up later", not inside
the WSL choice. Its GPU details and accessible
announcement describe the PC; existing hardware and WSL eligibility checks still
apply. A successful native-support check enables the native choice without a
redundant success line. The accent icon tile follows native availability: native
uses it when available, otherwise WSL does. Unavailable/checking native support
and Windows Update recovery remain visible in their existing support card.

### Shortened local onboarding (native and WSL)

The shared `WizardOnboardingPolicy` removes optional setup cards from both WinUI
paths and the headless WSL runner. Security consent, telemetry opt-in, agent name,
AI provider/authentication/model selection, permissions and actionable errors
remain explicit.

- Existing config detected, QuickStart, Model check, How channels work, Web
  search, Skills status and Gateway notes are acknowledged without rendering.
- Setup mode chooses the offered keep-existing-model mode, otherwise QuickStart.
  Config handling keeps current values. Channel and search selectors choose the
  offered skip value. Skill configuration/dependency installation is deferred.
- Gateway service prompts are not part of onboarding. `wizard.start` requests
  `installDaemon: false`; Companion's existing WSL service installation and native
  runtime ownership remain unchanged. Older WSL gateways can use the existing
  parameter-compatibility fallback.
- Before acknowledging Optional apps, setup explicitly cancels the optional tail.
  It requires a confirmed `cancelled` result, valid saved configuration from
  `config.get`, and authenticated `health` success. This is a deliberate handoff,
  not a claim that the upstream wizard completed. Arbitrary errors never qualify.

Native setup then restores reload and performs its own final config, listener
ownership and health checks before publishing the staged record. WSL setup
continues its existing Windows-node context step; the headless runner restores
reload through its existing completion/cleanup wrapper. A user's Cancel remains
an abort, not this validated handoff.

Defaults match audited English prompt kinds, labels and raw option values, never
random step IDs or option positions. Unknown/localized prompts and missing skip
options stay visible (or require a headless answer) rather than receiving guessed
answers. Configured channels/search are deferred, not intentionally disabled.

#### Headless answers and Companion-managed steps

The shortened experience is product policy, not merely a set of overridable
headless defaults. `WizardAnswers` still supplies and validates answers for
unmanaged prompts. For each encountered Companion-managed choice, an absent
answer uses the policy; an explicit answer must produce the same protocol value.
Equivalent encodings, such as `FALSE` and `false` for a confirmation, are accepted.
A conflicting or invalid answer fails setup before answering that step, rather
than silently overriding the caller or restoring an intentionally omitted screen.
The error identifies the step and asks the caller to remove the conflicting entry
and configure optional features after setup. It does not echo the configured value.

Managed informational notes accept an absent answer or an explicit `true`
acknowledgment. This also applies to the Optional apps handoff: a contradictory
answer fails before the handoff begins. Security and other unmanaged prompts
retain their existing answer handling. Only encountered steps are checked;
entries for steps beyond the deliberately cancelled optional tail are not executed
or validated.

Recovery templates list encountered unmanaged prompts that the caller can act
on, not overrides for managed/deferred steps. On a policy conflict, remove the
entry from the original configuration; the generated template does not reproduce
that entry or its value. The bundled configuration leaves setup mode and search
provider unspecified so policy can choose among the actual offered values.

### Native Gateway MSIX (isolated or legacy)

For an authenticated isolated native Gateway, **Install and use Local AI** captures
one in-memory consent for the reviewed Gateway, endpoint binding, model and port.
Native acquisition review does not inspect `.wslconfig` or require consent to
mirrored WSL networking. WSL review retains its existing inspection and consent.
The Windows-only artifact pipeline then continues directly into startup, provider
publication and exact-model verification under **Setting up Local AI**, without
returning to provider discovery or requiring a second Use click. Acquisition itself
still never starts inference or changes the primary; the continuation uses the same
guarded Use owner. Changed targets or files fail closed, and an uncertain mutation
can only be reconciled, not replayed. Consent is not persisted across app restarts.
Confirmed startup failures retain their runtime diagnostics and immediately restore
repair/provider choices with the normal **Connect your AI** heading. Failure before
mutation admission drops automatic continuation, so recovering the connection or
permissions requires a new explicit Use action. Ordinary WSL setup keeps its
read-only Local AI check independent of Gateway connection availability.
Existing installations retain the explicit **Start and use** action. Both paths
require operator administration scope; legacy same-user native Gateways remain unsupported.

Use starts the human-owned authenticated loopback server, publishes only to the
selected Gateway, then verifies inference from that isolated Gateway with the
exact primary model. The setup record remains staged until normal verified
completion. Settings uses the same flow for an existing native Gateway.
That Settings entry binds the existing Gateway ID and endpoint before opening AI
setup. Its completion does not run WSL workspace finalization or rewrite existing
permissions, sharing choices, or startup preferences.
Native installation and recovery never create a WSL distro, change WSL
networking, or run WSL configuration commands. Stop an existing owned runtime
before repairing its files. Unconfirmed writes or external provider changes
require reconciliation with the original Gateway, not an automatic overwrite.

Isolated Gateway console reads retry transient I/O and timeout failures twice,
including the initial cursor anchor before `wizard.start`. Polling retries use
the same cursor; initial reads never display historical instructions. A gap or
failure also shows persistent terminal
recovery outside the normal wizard-error controls, so continuing to another
question cannot hide lost OAuth guidance. Starting a new wizard resets this
warning. Explicit **Restart gateway** restarts the verified package service
even if it was already running before Companion attached; merely preparing a
wizard or disconnecting does not restart or stop that pre-existing service.

**Historical package-aware proof (2026-09-16, package 0.0.0.1 ARM64):** the original
listener-job mismatch is resolved. Companion creates the launcher suspended,
assigns its lifecycle job, retains its process handle and resumes it. A listener
may belong to that job or be a verified live, same-user descendant of the
package-identified launcher. Every ancestor handle is retained during inspection,
creation times must be ordered, and TCP ownership is checked again before
credential handoff. This is local process supervision, not MXC isolation or a
defense against malicious code with the same user's process-access rights.

The disposable-profile proof reached authenticated `hello-ok`, received the
initial `wizard.start` note and cancelled without publishing a Gateway record.
It used production setup code to automatically approve only its own test device
through the package CLI, not security or provider prompts. For a fresh Companion
identity, setup verifies that the handshake request matches its device ID and
public key, approves that exact request in the dedicated profile, then reconnects
once. Listener ownership and local-profile configuration are checked before both
CLI calls. The token stays in the process environment, not command-line arguments.
There is no `--latest` approval or remote-record exemption. The profile-specific
terminal remains a recovery option, not a required onboarding step.
Pairing CLI calls have a two-minute total budget for packaged runtime startup and
the request. A failed terminal wizard response displays the Gateway's error detail
before its status. The installed Gateway's full optional tail previously failed
with `PreparedModelCatalogConfigReplacedError` after Optional apps. Shortened
setup deliberately ends before that tail and uses the validated handoff above.
It does not suppress that exception or repair the upstream full-wizard finalizer.
See the [implementation results and limitations](GATEWAY_SETUP_RESPONSIBILITIES.md#package-aware-implementation-results)
for launch, shutdown, verification details and the pre-assignment crash window.

**Install a local native gateway** is the first Welcome choice and retains its
**Recommended** badge even while disabled, with WinUI disabled brushes for the
title, description, badge and icon instead of active accent colors. Only a successful native capability
check enables it. The separate isolation warning and acknowledgment checkbox
remain removed; the general security notice and provider/onboarding consent
remain explicit. With an isolated-session Gateway package, the package provisions
and runs the agent account; the known legacy proof package retains its original
same-user runtime. The UI capability gate alone never proves isolation.
Native and WSL use the **same focused `AiSetupPage` and three-choice `AiReadyPage`**,
not separate normal provider/model wizards. The classic `WizardPage` is an explicit
compatibility option only when the required setup methods are unavailable.
WSL is always shown as the second Welcome choice after native,
followed by **Connect to an existing gateway**.

Companion checks current-user registration for the Store package
`OpenClawFoundation.OpenClawGateway` and publisher
`CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC`, package health, and the package-qualified
`clawctl.exe` and `openclaw.exe` aliases. It does not resolve an npm installation
from `PATH`. Native setup uses the same Windows capabilities and permission
selection as WSL, with native package installation consent on that same page.
Selecting **Set up gateway** starts native progress without WSL, Local AI or
Tailscale provisioning and without an additional review page.
A missing package is installed automatically using
`winget install --id 9NV70LV3D6XC --source msstore --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --no-upgrade`;
unhealthy registration or unavailable aliases show an explicit repair error.
The fixed product is [OpenClaw Gateway in Microsoft Store](https://apps.microsoft.com/detail/9NV70LV3D6XC).
No MSIX version or download URL is pinned; Store selects the current compatible
release. Existing healthy installations are not silently upgraded during setup.
To explicitly refresh one, run
`winget install --id 9NV70LV3D6XC --source msstore --accept-package-agreements --accept-source-agreements`
without `--no-upgrade`. WinGet may report that no newer package is available.

An unfinished draft saved against a different package family (for example, the
older development Gateway) offers **Discard and set up again**, just like a
runtime-contract change. Keeping the draft changes nothing. Confirming replaces
only its descriptor with a new profile for the installed package; old workspace
files, configuration and credentials are preserved. Published profiles cannot
be discarded through this recovery.
No local MSIX path or environment-variable configuration is required.
The original `OpenClaw.Gateway` / OpenClaw Foundation development publisher pair
is still accepted for existing installations. If both identities are installed,
new setup reports duplicate registrations instead of guessing which to use. Existing
profiles resolve their original package family even when both packages are installed;
there is no implicit migration. Companion additionally checks the qualified
`clawctl status --json` integration kind and version before choosing a runtime.
An unversioned package already reporting a session is unsupported and requires
an MSIX update; it never falls back to same-user execution.
If the package reports a healthy Gateway but omits listener ownership because
Windows lacks process-sequence support, Gateway lifecycle remains available
but Companion will not send credentials. Update to Windows 11 build 26100.4770
or later before retrying; `clawctl setup` cannot add the missing OS API.

The capabilities-page disclosure explains that **Set up gateway** authorizes WinGet installation and
accepts the package and Store source agreements. Provider sign-in remains interactive.
Microsoft Store owns architecture/package selection, signature validation and
deployment. WinGet runs through the signed-in user's App Installer execution alias,
without a shell or elevation. Companion verifies actual package registration and aliases, then
automatically prepares the profile and opens **Connect your AI**.
Installation and verification share a cancellable five-minute deadline. Cancellation
stops the WinGet request, but Windows may still finish an in-progress deployment.
If WinGet reports stale Microsoft Store certificate pins (`0x8A15005E`), Companion
runs one current-user `Repair-WinGetPackageManager -Force -Latest` bootstrap and retries once.
The repair installs Microsoft's WinGet client module; it never bypasses certificate validation.
Other failures show explicit repair guidance and **Retry setup**. The normal
path has no separate install, availability-check or wizard-launch buttons.
An already installed healthy package skips installation. Retry checks registration
again before invoking WinGet. Companion does not download Gateway packages directly,
change certificate trust, or uninstall packages on cancellation.

Progress reuses the WSL spinner/checkmark rows. Normal completion uses the same
verified-model heading and three destination choices as WSL, without a native-only
Gateway/capabilities summary. Gateway details and Windows permissions remain in
their existing Connection and Permissions settings. AI readiness does not imply
that a Windows node is paired or that command approvals have been granted.

The native path:

1. Saves a credential-free setup draft and Companion device identity. For a
   versioned isolated package, `clawctl setup --json` must report a ready session,
   then `clawctl companion prepare --port <preferred> --json` uses the agent's
   default configuration and packaged Node.js. An existing agent port, token and
   provider settings are preserved; incompatible configuration fails explicitly.
   No host profile path is forwarded. The known legacy proof package alone
   retains a separate Companion-owned profile and same-user runtime.
2. Validates the active Gateway configuration and starts via the package's
   `gateway-service` lifecycle (or the legacy runtime). Before every credential
   handoff, including reconnects with a saved device identity, setup verifies
   the package-owned listener and its isolated-session process sequence
   against fresh Windows snapshots. The staged record is **not** made active
   in the registry.
3. Automatically pairs the setup's own Companion identity if required, then
   opens the shared focused AI page. Authenticated method advertisements gate
   discovery, provider authentication and model activation. Provider prompts stay
   explicit; confirmed activation must pass exact primary-model verification.
   The isolated path uses package-qualified `openclaw devices list` and
   `openclaw devices approve <request-id>`, which run as the agent user and read
   its config. Before each command, Companion checks that the effective port
   and token still match its setup record, then approves only the verified
   request. No host token or profile path is forwarded. No consent or provider
   answer is supplied automatically. Legacy console
   output is tailed from its dedicated profile. The isolated path uses
   upstream's authenticated `logs.tail` RPC after verifying the Gateway
   listener, then displays only root-logger `console.log` messages. It anchors
   at the current log size before provider setup (or classic `wizard.start`), polls bounded redacted
   batches, and reports skipped output or RPC failure with recovery-terminal
   guidance. It never tails WSL or a host profile for agent-side messages.
   Native console output and scoped terminal/restart/cancel recovery appear only
   for errors, skipped/unavailable console output or uncertain outcomes in the
   shared page/provider dialog. Healthy provider selection has no extra native controls.
4. The verified chooser offers **Talk to my agent**, **Connect channels**, and
   **Explore skills**. Choosing a destination rechecks the same Gateway, package
   family and runtime contract, signing identity, agent, session and primary model.
   After draining the operator, setup retains isolated stop ownership through
   the explicit package restart. The legacy path first stops its own runtime
   and restores its reload setting. Both check the selected
   local/loopback/token configuration, runs `config validate --json`, restarts
   with owned-listener proof and runs authenticated `gateway health --json`.
   It also verifies the exact primary model on the restarted runtime. A failed
   gate remains retryable and does not publish the staged record.
5. Stops the setup-owned runtime before reloading and updating the registry.
   The isolated runtime leaves a pre-existing service running on detach.
   The selected destination uses the same protected restart receipt as WSL.
   The normal connection manager owns the native runtime after restart and
   supplies the authorized connection for another fresh verification before
   opening the selected page. This does not approve the separate Windows node
   role. Current Windows node permissions are preserved.

If the required AI setup methods are absent, the page explains the unsupported
contract and offers classic setup explicitly. That compatibility path retains
`wizard.start` with `mode: "local"` and `installDaemon: false`, its validated
optional-tail handoff and native configured-Gateway summary. It is not entered
for authentication errors, malformed responses or uncertain provider writes.

For the isolated package, `clawctl gateway-service` owns the Gateway process,
sign-in recovery and isolated session. Companion starts it through the package
when connecting and stops it only when this Companion runtime started it; an
already-running package service is left alone on detach. Legacy proof packages
retain Companion-owned same-user process supervision. Companion never calls
`openclaw gateway install` or registers a second service.

Cancelling setup, returning from the wizard, or closing setup stops only a
Gateway started by the setup runtime and closes its recovery terminal. Legacy
setup restores its reload setting. Cancel is not successful setup.
**Restart gateway** controls the selected runtime; **Open terminal** opens
`clawctl pwsh` in the agent for an isolated package, or the dedicated legacy
profile's shell, not a second onboarding TUI.
Its lifetime ends with setup or a restart. Configuration
already entered is retained in the agent's profile (or the legacy dedicated
profile) so it is not lost on retry. A
credential-free draft descriptor under `gateways\native-setup-draft.json`
resumes the same setup until successful publication. Only the legacy path has
a credential-free reload backup. Existing WSL distributions and remote
gateways are not replaced. Isolated setup reuses the agent account's default
OpenClaw configuration instead of touching the invoking user's `.openclaw`.
The native path does not enter the WSL
cleanup, Local AI installation, or WSL repair pipelines.

The legacy proof path was based on the packaging contract at
[`9a8cd4a`](https://github.com/openclaw/openclaw-windows-packaging/tree/9a8cd4af139513c21d290a01a8a1f2be19b602bc).
The current source's isolated path instead requires the versioned
`clawctl status --json` integration, agent-side configuration and
`gateway-service status --json` listener attribution. MSIX registration or
the presence of `IsolationProxy.exe` alone is not sufficient.

### Historical MXC recommendation requirements

The requirements below were recorded on 2026-09-16 for an earlier design.
They are historical context, not the current lifecycle contract: the Gateway
package now owns its isolated agent session, while Companion handles setup
and verifies endpoint ownership. See [Welcome](#welcome) for the current UI.

- **Lifecycle owner (superseded proposal):** Companion would have provisioned
  and supervised the MXC session. The implemented package instead provisions
  and records that session; Companion never creates a second one. See the
  [verified MXC 0.8 contract and blockers](GATEWAY_SETUP_RESPONSIBILITIES.md#verified-mxc-08-contract-and-implementation-blockers).
- **Distribution:** Gateway packages are available as x64 MSIX, ARM64 MSIX,
  and an MSIX bundle. The future Store DLO is expected to point to the bundle,
  subject to confirmation when the link is available. Let Windows select the
  matching architecture from the bundle. The configured ARM64 development file
  is not a product-wide architecture restriction.
- **Primary eligibility check:** Windows version and enabled OS session
  capabilities determine MXC native Gateway eligibility, not GPU or Local AI
  eligibility. Evaluate this before recommending a local gateway path.
  The shipped MXC 0.8 `wxc-exec --probe` exposes
  `probes.isolationSessionAvailable`; the read-only local probe returned `true`.
  A `false` result conflates native API errors with lack of support, so a richer
  diagnostic contract is still needed. Read the OS build/revision separately if the probe
  does not expose them. A process-containment tier alone is not session support:
  require the session-specific capability result, not merely a high build
  number or the presence of `IsolationProxy.exe`.
- **Recommendation order:** Recommend the MXC native Gateway when the OS
  supports sessions and the Gateway session integration is available. If the
  OS is unsupported, first recommend updating Windows to a supported version,
  with a capability recheck after updating. Present WSL as the secondary
  fallback, not the initial recommendation. Do not automatically change the
  Windows update channel or enable preview features.
- **Actionable failures:** Distinguish an unsupported OS from a failed probe,
  disabled/unavailable session features, and a missing Gateway package/runtime.
  A probe error offers retry and diagnostics rather than asserting that an OS
  update is required. Meeting a version floor does not guarantee that a
  feature-gated OS API is enabled.

The implemented path must actually run the Gateway in the package's recorded
isolated session. Do not relabel the historical ordinary-process proof as MXC,
or recommend any MSIX as isolated based only on its registration.

The onboarding wizard installs a new app-owned local WSL gateway on Windows,
connects an AI provider, and opens a selected native app page after live
primary-model verification. It uses focused Gateway-hosted AI setup when
supported and retains the classic OpenClaw onboard wizard for older gateways.

## Overview

### Native presentation

The shared Setup window starts at 720 x 820 DIPs and has a native presenter
minimum of 720 x 560 DIPs. `SetupWindowSizing` rounds those dimensions up to
physical pixels using the actual HWND DPI. `SetupWindow` reapplies the minimum
when its XamlRoot, monitor position or presenter changes and detaches the
subscriptions on close. DPI updates do not call Resize or reset the user's size.
The window remains movable, resizable and maximizable; page content scrolls
between its fixed header/footer at the minimum. This is a window constraint,
not a root-Grid minimum that would merely clip a smaller HWND.

The preferred minimum is not silently weakened for a smaller monitor. At 100%
it is 720 x 560 physical pixels; at 150% it is 1080 x 840; at 200% it is
1440 x 1120. Actual monitor/work-area constraints remain Windows-owned.

Setup uses a 720-by-820 window with a scrolling body, centered vector mascot
and wrapping heading. The 180-DIP mascot frame includes its motion/glow gutter;
the title-bar mark is 24 DIPs. Theme-aware Mica/Fluent surfaces retain solid
background and high-contrast fallbacks.

Capabilities, installation review and AI provider setup use native controls.
The [screen details](#screen-details) below own their behavior and consent rules;
the [artwork section](#artwork-and-motion) covers motion and asset packaging.

### Verified native completion

`AiReadyPage` is mounted only through the setup window's current verified
completion coordinator. Its celebrating mascot and "Your AI is ready" heading
follow real primary-model verification, not discovery or browser sign-in.
Showing the page creates no pending handoff and performs no finalization.

The final screen has exactly three choices: **Talk to my agent**, with a visible
Recommended badge, opens Chat; **Connect channels** opens Channels (WhatsApp,
Telegram, and more); **Explore skills** opens Skills for the verified agent.
There are no bottom Return or Skip buttons; the progress dots remain. Selecting
a card is the final action, with no extra Finish button. These are navigation
choices, never automatic sign-in, channel configuration, or skill installation.
Legacy WhatsApp/Telegram receipt destinations retain their numeric mappings and
channel focus behavior, but are not separate choices on this screen.

Each explicit choice drains the prior AI page, rechecks the same Gateway,
endpoint, agent, model and main session through bounded verification,
then finalizes Windows choices once. The prior page drain has its own 30-second
deadline. Fresh proof has a separate six-minute ceiling, allowing the
native owner's 210-second reconnect budget, the existing 120-second exact-model
RPC budget and authority-check overhead. Neither deadline runs across finalization
or destination publication: those retain caller cancellation and their underlying
operation budgets, without abandoning or automatically replaying mutations.
Verification does not publish provider configuration or finalize setup, but its
authorization may start the selected owned Gateway and roll that start back.
Failures stay on the chooser with retry or
return-to-AI guidance. **Back to AI setup** is available only inside the error
message, so changed verification has an actionable recovery without a permanent
footer escape. Returning invalidates its admission and requires another
verification. MCP-only/deferred routes do not claim verified AI.

### Native startup and completion deadlines

A reported slow native cold start took about 136 seconds. Conditional on a
successful package acknowledgement, Companion uses a three-minute startup
budget, with ten seconds for confirmation after a late acknowledgement, subject
to the caller deadline (at most 190 seconds for start/status), then still requires fresh selected-port and process-sequence ownership
proof. It never treats an `unhealthy`/`starting` observation as ready, waits for
an unrelated pre-existing start, or retries the start command while polling.
If a listener appears between a pending status and the host snapshot, one immediate
status recheck may admit only a fresh `running` attestation. The full selected-port,
listener and process-sequence checks still run; another pending status or failed
attribution rejects the start.
The published native connection borrow gets up to 210 seconds for startup,
authenticated handshake and authorization before exact-model verification.
Preflight and final inspection also consume that outer budget; the confirmation
allowance never extends it. Slow surrounding work can therefore cancel confirmation.
Native credential reauthorization also has a bounded 210-second allowance instead
of the unrelated five-second SSH/listener-check budget. Non-native authorization
keeps its existing deadline. Caller cancellation, superseded attempts, record drift
and connection error states still reject admission.
Page drain, selection
verification, runtime startup and published-owner handoff are distinct lifetimes.

`SetupNativeCompletionTiming` defines the published path's enforced phase ceilings:
210 seconds for the first borrow, 210 seconds to join already-admitted Local AI
recovery, 210 seconds for a fresh post-recovery borrow, 150 seconds for exact-model
verification (including the existing 120-second RPC and authorization), and
30 seconds for navigation. Their 810-second total bounds the exclusive execution
lease. A phase can fail earlier due to its underlying owner or caller cancellation;
unused phase time is not a guarantee that a different phase may overrun its ceiling.
Recovery waits never initiate or replay a provider mutation.
Timeouts retain a typed, finite phase (page drain, connection, Local AI recovery,
model verification, or native startup). The aggregate pre-publication verification
deadline uses neutral readiness guidance because it includes both connection and model
work; it does not incorrectly attribute a connection stall to model inference.
The chooser displays localized phase-specific
guidance and local diagnostics record only the phase/type, never raw package output,
model content or credentials.

The five-minute unused-handle admission window is unchanged. The first acquisition
atomically stores its execution start and deadline under the existing file lock. Retries,
reacquisition by another process and app restarts use that same absolute deadline.
Expired, malformed, abandoned in-flight and legacy retry records lacking execution
provenance cannot grant a new window. Legacy unused records retain their original
five-minute admission deadline. No Gateway credential lifetime changes.
Fresh route/device/session proof and single-use consumption still fence navigation,
and deadline cancellation reaches the active navigation request before settlement.

**Installed package limitation:** package commit
[`133deeb`](https://github.com/openclaw/openclaw-windows-packaging/tree/133deeb1a5efac2bcbc6564c90c038a999e6e842)
(MSIX 2026.9.700.0, payload 2026.9.7) cannot safely expose its pending start:
[`GatewayController`](https://github.com/openclaw/openclaw-windows-packaging/blob/133deeb1a5efac2bcbc6564c90c038a999e6e842/src/OpenClaw.Launcher/Gateway/GatewayController.cs)
returns `Starting` after its 90-second listener wait;
[`Program.WriteGatewayStartResult`](https://github.com/openclaw/openclaw-windows-packaging/blob/133deeb1a5efac2bcbc6564c90c038a999e6e842/src/OpenClaw.Launcher/Program.cs)
maps it to exit 1; and
[`ClawCtlJson.FromGateway`](https://github.com/openclaw/openclaw-windows-packaging/blob/133deeb1a5efac2bcbc6564c90c038a999e6e842/src/OpenClaw.Launcher/ClawCtlJson.cs)
omits the Gateway object for that failed start. Only `ok:false` and a generic
`cli_error` with human text remain. Its subsequent `unhealthy` status supplies no
pending sandbox/process ownership proof. Companion intentionally rejects that
ambiguous failure and preserves rollback rather than parsing prose or accepting
arbitrary failures. The bounded polling change does **not** fix this package's
90-second failure path.
The successful pending-start fixtures describe conditional client behavior, not
an acknowledged pending-start contract supported by that installed package.

The packaging contract needs an explicit, versioned pending-start acknowledgement
with defined exit/`ok` semantics and stable session/launch ownership identity,
available on subsequent pending status checks and guarded stop operations.
That must distinguish this launch from another start, exited/replaced processes,
port changes and inspection failures. No guessed additional protocol fields are
accepted by Companion here.

A successful setup on payload 2026.9.4 followed by these failures on 2026.9.7 is
a concrete version difference, not a controlled regression measurement. Package
launcher timing, payload startup work and Companion's former combined 30-second
selection deadline are separate contributors. The reported 22.5-second successful
verification RPC plus authority subprocesses can exceed that former deadline;
fixture tests reproduce that budget collision, but do not prove the exception
shown by an installed UI whose original exception was not persisted.

The original verification retains the exact main session and a SHA256 binding
over the normalized identity directory and the local device ID that signed the
accepted connect request. A current-generation authenticated signing snapshot,
not an optional server device-ID echo, supplies that identity. The standard
Gateway hello schema does not declare such an echo. Current disk identity is
validated only to detect rotation, never to relabel an already connected client;
verified reconnects cannot generate a missing identity.
Receipts contain the hash, not the identity path or credentials. Fresh clients
must match those stable fields before model verification and finalization;
their independent connection-generation counters need not match. Missing
authority fields in earlier development receipts fail closed and require new
verification. The persisted endpoint binding includes the effective SSH local
forwarding endpoint, which is checked before a fresh connection is attempted.

The Skills handoff ignores unscoped cached status, loads a response-bound
`skills.status` for the verified agent, and rechecks the same connected client
before presentation. Its agent filter stays bound to that agent. An unavailable
or changed Gateway does not consume the receipt as a successful page launch.

Native setup offers the existing **Launch OpenClaw at sign-in** choice on
**PC capabilities**, before package installation. Managed WSL keeps that choice
on its Gateway setup review; Existing/Remote keep it on capabilities. Native
finalization persists and applies the visible selection, including an explicit
off choice. A failed application retains that selection for retry.

Isolated app hosts do not offer Windows startup registration. The host passes
that availability into setup, which hides the startup preference, keeps it off
even if an old isolated setting was true, and saves `AutoStart=false` before
native completion. Isolated and recovery completion do not invoke OS startup
registration. An ordinary explicit choice applies both enable and disable;
recovery preserves the saved preference. Registration failures remain visible
and retryable rather than being swallowed. `AutoStartManager`'s isolation refusal
remains unchanged.

Setup requests `schtasks /Create /HRESULT` so completion can classify the
documented numeric HRESULT instead of localized error text. A completed
`E_ACCESSDENIED` (`0x80070005`) is a rejection; the existing policy may create a
Run-key fallback only after strict inspection confirms the task is absent.
An exact existing enabled task prevents duplication; a different or unknown
task refuses fallback. Timeouts, generic nonzero exits and unclassified failures
remain Unknown. No real non-elevated registration failure or universal failure
on ordinary installations is inferred from the controlled tests.
See Microsoft's [`schtasks create` reference](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-create).

Native completion uses strict startup application: task removal must succeed
when the task exists, unknown task state is an error, and Run-key failures are
not swallowed. Existing best-effort callers retain their prior API. Classic
completion instead acknowledges a startup-specific warning and continues the
established restart path, so an optional startup failure cannot strand durable
setup or masquerade as a failed application restart.

Strict task registration distinguishes acknowledged success, a request that
did not start, and uncertain completion. An uncertain result cannot create a
Run-key fallback. It may complete only if Task Scheduler confirms the exact
enabled executable/action, principal, task path and logon trigger. A definite
rejection with confirmed absence retains the legitimate Run-key fallback.

The trusted profile-local handoff retains exclusive leasing, five-minute unused
admission and a non-renewable bounded execution deadline,
consumption on successful native presentation and explicit failure retry. New
`ai-v3:` opaque handles carry a typed native destination and exact verified
session in the protected record, not in public activation JSON. The experimental
`ai-v2:` browser-completion path is removed; those handles fail visibly and are
never reinterpreted as native receipts or ordinary Dashboard requests. The public
`setup-dashboard` activation route and profile-local storage name remain stable
for current native receipts. Ordinary Dashboard actions and headless setup are unchanged.

Failures after acquiring a receipt are settled before reporting: changed or
unreadable identity consumes it as Changed; an unavailable verification API or
malformed verification response retains explicit retry. Malformed stored
receipts, including oversized files rejected before acquisition, remain Invalid.
Acquisition distinguishes a busy exclusive lease from invalid input and unavailable
storage. A duplicate while a first launch owns the lease is ignored without a
failure notification, Connection navigation, or another queued launch. Storage
failures offer explicit Retry. That retry can admit an unstarted `ready` receipt
after pre-acquisition I/O failure or a settled `retry` receipt after a failed
presentation; it cannot replay an abandoned `inflight` receipt. Ordinary
activation of a `retry` receipt only offers recovery, never automatic execution.

A startup activation with a well-formed native handle bypasses the ordinary
update prompt for that launch. Receipt validation still runs normally; handle
shape is not verification authority. This prevents an unattended update prompt
or accepted installer from expiring or skipping the selected destination.

Native post-setup children never forward to the shutting-down parent. They
acquire the instance mutex synchronously on the same thread, with a 60-second
wait per attempt followed by visible Retry/Cancel recovery. The unadmitted
handle is retained in a protected, profile-local `setup-dashboard-handoff/restart.json`
file, not logs or UI text. A newly owned ordinary launch can resume it; ordinary
secondary forwarding is unchanged. Busy or unavailable handoff attempts retain
the matching recovery record. Successful receipt consumption after page mounting,
or definitive invalid/changed rejection, clears only that matching record.
Retention does not extend receipt expiry or replace normal verification.

An invalid regular `restart.json` is rechecked and removed under its writer lease
before reporting the error once. A newer valid handle, other handoff files and
reparse paths are never removed by this recovery cleanup.

After restart, native launch re-verifies ownership before mounting Chat,
Channels or Skills. Legacy targeted Channels uses a fresh response from the bound Gateway,
never generic fallback rows as evidence of support. Missing authoritative
metadata produces an unconfirmed/retry state; an offered-channel list that
omits the target produces an unavailable state. Native Chat selects the verified
session without changing the user's persistent chat-surface preference.

On first launch, the wizard appears only when there is no usable saved gateway connection. Users with existing gateways manage connections from the tray app's Connections tab. The local WSL setup affordance in Connections is shown only when setup has not already created an app-owned WSL gateway on this device.

The setup flow walks users through:

1. **Welcome and trust** - Three feature rows and an inline device-trust notice
2. **Gateway** - Capability-gated native Gateway recommendation (with WSL fallback), existing, remote, local MCP only, or deferred setup
3. **PC capabilities** - Strict, Balanced (Recommended), Open, and inline Fine-tune; Node mode, local MCP and Ollama sharing stay independent. No Windows-access advisory panel or probe runs here.
4. **Gateway setup** - Managed WSL reviews installation and replacement consent; native Gateway checks its package and prepares a dedicated profile without WSL or Local AI installation.
5. **AI setup** - Native Gateway, WSL and existing/remote gateways use focused AI setup and live model verification, followed by the same three destination choices. Native uses its setup-owned runtime until finalization; WSL workspace integration remains WSL-only.
6. **Completion** - Both verified focused AI flows open Chat, Channels or Skills. Only the explicit classic compatibility fallback retains its setup-complete and connection handoff.

`SetupWindow` owns one `SetupAccessDraft` over the same `SetupConfig` for the
entire flow. The native connection contract separates verify-only **Check** from
**Next**, which revalidates and commits that same editor request. A successful
Next is the commit boundary: later Back/close preserves that committed choice;
cancelling before successful commit restores the prior active connection.
Existing/Remote then review capabilities and enter focused AI setup
using the committed registry. They never run WSL workspace finalization.
MCP-only/Deferred persist reviewed settings/startup and complete without AI or
WSL. MCP-only opens Companion Settings; deferred setup opens Connection settings.
Verified focused AI routes use the native chooser. `AdvancedSetupRequested` is only the explicit classic Settings fallback,
not the normal native route. Connection verification, credentials and commit
remain native host responsibilities. Progress indicators
come from `OnboardingFlowPolicy`, not a fixed six-page count. Local AI recovery
does not replay the introductory pages.

The injected `ISetupNativeConnectionHost` owns connection checks and commits.
The editor draft is not serialized into `SetupConfig`; see
[native connection checks and cancellation](#native-connection-checks-and-cancellation)
for the identity, drain and rollback contracts.

The legacy milestone and completion pages remain available for resume, classic
wizard compatibility and errors. They are not gates in the new successful path.

After the install pipeline saves its Gateway, the typed Local AI host reconciles
that exact active record into the canonical registry before AI discovery. This
read-only handoff compares disk against the expected snapshot produced by the
owning cleanup/pairing/bootstrap-clear writes. It refuses intervening authority
edits, unrelated record changes, or invalid saved data instead of adopting a
freshly reread, unbound record.
The expected recovery reconnect may update `LastConnected`; newer canonical
timestamps are retained without rewriting the registry file or emitting events.
The runtime's initially unbound owner can bind once to this canonical record; an
already bound owner cannot switch to another Gateway.

Local AI use retains the selected Gateway ID with its model across reconnects
and recovery returns. A different active Gateway is rejected before credential
lookup, and again before verification/completion. Closing cancels and drains the
actual runtime mutation and rollback before releasing the setup lock; only
observations and transport cleanup have bounded deadlines. A failed start with
confirmed terminal cleanup restores provider choices and Local AI repair. An
uncertain publication retains exact-target verification only, without replaying
the mutation.

### Verification provenance

`GatewayAiSetupClient` records intent from the explicit activation kind, never
from `setupComplete`. This provenance remains in the verification record;
the user's native destination choice, not that intent, determines the app page.

Only successful exact-model, main-role verification on the same authority and
connection generation produces a `GatewayAiSetupCompletion`. `SetupWindow`
checks the saved Gateway binding before and after workspace finalization.
`SetupDashboardHandoffStore` writes one atomic, credential-free pending record in
the current profile's data directory after verified completion. Restart and
activation carry only a random opaque handle, not serialized verification JSON.
The pending record binds the run, Gateway, endpoint, agent, model and intent.
A new completion supersedes the previous pending run. Only native `ai-v3:`
receipts can be issued or consumed.

`SetupGatewaySessionBinding` captures the exact record and resolved endpoint
before the temporary client is constructed. Registry revalidation before
credentials/connect, at handshake/reconnect, after connect and at request/route
boundaries can reject a changed Gateway, but cannot relabel the existing socket.
`LastConnected` bookkeeping does not change this binding; changes to Gateway ID,
endpoint or SSH intent do.

`WindowManager.ShowNativeSetupAsync` rechecks current Gateway, agent and
provider-qualified model observations after page readiness. A bare display model
ID is not treated as an exact provider route.

Ordinary Dashboard actions still use `GatewayDashboardLauncher` and the normal
credential resolver, with no completion receipt or automatic onboarding query.
Device/bootstrap tokens are never exported to browser URLs. Explicit error-dialog
retry remains available. Legacy `chat`, `settings` and `connection` restart
arguments remain supported.

### Store migration preview

The gated Inno-to-Store migration window reuses the wizard's visual design:
Mica backdrop, shared native vector `OnboardingMascot`, centered heading, themed content card, and persistent
footer actions. Its content scrolls independently so consent and recovery
actions remain available in smaller windows or with enlarged text.
The hero retains the shared 180-DIP frame and reduced-motion/theme behavior;
the small title-bar mascot is static and non-interactive. No legacy PNG artwork
or separate mascot implementation is used by this window.
Consent uses three short titled sections and a separate, non-dismissible warning.
Manual close and removal use step-specific headings and short instructions;
warnings stay in the same native InfoBar style as setup. Buttons retain the
wizard's 100-DIP minimum width, neutral left-hand dismissal, accent right-hand
primary action, and standard content/footer spacing. Recovery shortcuts are also
content-sized, and every action label wraps without ellipsis. The window uses
setup's `40,4,40,24` outer padding. Unlike setup's full-width footer, this
standalone window aligns its footer to the body's 560-DIP reading width. The title bar stays
"Move to the Store version"; retry/recovery states do not use setup step dots.
The window also sets the native OpenClaw icon for taskbar previews and the
window switcher; the custom title-bar image alone does not supply that icon.
The migration-capable Inno Settings page promotes this handoff with a
theme-aware recommended card that reuses the same card padding, typography,
accent badge, and primary-button styling as the rest of the app. The card
carries no decorative icon, so its text aligns with every other settings row,
and its action shares that single row. Its confirmation dialog
asks a question rather than repeating the button label, and states the handoff
grant and preserved data; detailed removal and finalization guidance stays in
the Store wizard, where the user can act on it.

This is a separate pre-start workflow, not a page in the setup pipeline.
`StoreMigrationStartupGuard` and `StoreMigrationWorkflow` retain migration
ownership; displaying the window does not construct `SetupWindow`, install a
gateway, or start normal app services. Consent, retry, and uninstall verification
are unchanged. The same visual workflow serves configured production Release
builds and explicit Debug previews. The first supported Inno release is pinned
to `2026.9.5.0`, and `MigrationProductionEnabled` is checked in as `true`, so
non-Dev Release builds for `win-x64` and `win-arm64` carry the migration
surfaces. Enablement is gated at tag time, not by the checked-in default:
publication is blocked until that release's artifacts and acceptance are
verified, and the switch is set to `false` and retagged if acceptance fails.
See [Release migration gates](RELEASING.md) for the shared build contract and
the two-PR coordinated-release requirements.

`StoreMigrationWindowProofTests` mounts this production window with in-memory
operations only. It covers light/dark consent, busy/manual-close/removal and
unreadable-record recovery, plus work-area-height and small-window enlarged-text
layouts that scroll to the complete consent warning. With
`OPENCLAW_VISUAL_TEST=1` and the existing `OnboardingNativeProof` environment
(`OPENCLAW_UI_PROOF_DIR`, `OPENCLAW_UI_PROOF_FREEZE_DIR`, and
`OPENCLAW_UI_PROOF_MANIFEST_SHA256`), it captures complete test-owned windows
against the reviewed source/build freeze. Run a fresh testhost for each theme,
setting `OPENCLAW_UI_TEST_APP_THEME=Light` or `Dark` before launch and selecting
matching theory rows with `DisplayName!~Dark` or `DisplayName~Dark`. The app theme
must be selected in the `TestApp` constructor; changing only the root theme after
window creation can leave Mica light beneath dark-theme text. Capture verifies
that app/content themes agree, as well as visible action/status
labels and mascot pixels, including the composed artwork rather than a
`RenderTargetBitmap` approximation. This is presentation proof, not real migration or installer
proof: it never runs production migration operations or opens Installed apps.

## Screen Details

### Welcome
The page checks `wxc-exec --probe` asynchronously before recommending the first
**Install a local native gateway** card. It requires the reported
`probes.isolationSessionAvailable` boolean, not a process sandbox tier, build
comparison or `IsolationProxy.exe` file. On success the native card is enabled,
selected with the accent highlight and marked **Recommended**. An explicit
WSL or existing-gateway selection is not overridden by a late probe result.
Back navigation preserves those explicit choices even when native is supported.
The badge sits to the right of the title. Successful capability status appears
inside the card below its description, with a decorative green checkmark and a
screen-reader announcement. The description says **Install and set up an OpenClaw
gateway on this device**; it does not claim isolation based on a capability
probe alone. An older recognized proof package still runs same-user; a
non-proof package without the versioned isolation contract fails during
setup rather than pretending to be isolated.
If a resumed native setup profile's port has been taken by another process,
Retry selects a new port without replacing the profile, credentials or identity.
Unexpected launch/cleanup failures show an explicit failure and Retry action;
Companion never takes over or stops the conflicting process.
The WSL title is **Install a local WSL gateway**. Checking, unavailable and error
messages appear in a compact, bordered support card below the gateway choices,
outside the disabled native option so the Windows Update action stays usable.
The support card is hidden when native capability is available.

When capability is unavailable, **Open Windows Update** opens
`ms-settings:windowsupdate`. Guidance names Insider build **26340.9212**, the
baseline documented by the pinned MXC SDK, or a newer supported build. Reopening
the page reruns the check; the Welcome page has no **Check again** button.
Feature rollout varies; a negative native API result is not proof
that the build alone is the cause. Probe failures or missing/invalid metadata
offer retry/Companion repair rather than misleading update advice. Windows Server
remains unsupported without invoking the native probe. No update-channel or
feature-policy changes are automatic.

The single-selection list always shows native first, WSL second and
**Connect to an existing gateway** third. WSL is visible and selectable while
native capability is being checked, when it succeeds, and when it fails or is
unavailable. There is no **Other gateway options** expander. Page load starts
WSL/Local AI discovery; choosing WSL retains the fresh readiness gate before
Capabilities, then reviews replacement consent on the WSL setup page. Native
package setup independently rechecks capability before configuration.

Choosing local Gateway setup runs read-only WSL viability and existing-config
inspection before PC capabilities. Failures appear inline with a fresh-inspection
retry, not a modal. Fresh installation has no redundant confirmation. A detected
replacement is reviewed on the WSL setup page before Install; explicit
consent is bound to the exact distro name, and unproven ownership still requires
the engine's destructive confirmation. External saved Gateways stay untouched.

The gateway-choice scroll viewport owns the 560-DIP maximum width and stretches
its list content. Keep the width constraint on the viewport, not on the nested
ListView, so the choices share the header's center line as the window resizes.
Back, Next, and the step indicator remain outside the scrolling area.

### Local setup progress
`SetupInstallationProgress` maps real pipeline events to prepare, install and
connect/check phases. Local AI recovery uses its own install label. Counts include
completed and skipped steps, not estimated percentages; only a running phase
spins. Unknown step IDs require an explicit phase mapping and test coverage.
Detailed activity is collapsed by default; actionable authorization and real
download progress remain visible outside it.

Installs and connects a new app-owned `OpenClawGateway` WSL instance from a clean WSL baseline. If the WSL platform is missing or its optional component is not initialized, setup requests administrator approval to install it, re-inspects readiness, and reports when a Windows restart is required. Setup does not export from or mutate an existing user Ubuntu distro; if WSL cannot create the named app-owned distro directly, setup fails with an actionable update message. Cleanup automatically unregisters a distro only when durable OpenClaw evidence is paired with exactly one readable current-user WSL registration whose canonical base path matches the expected managed install path. Automatic orphan-directory cleanup requires a marker bound to that exact path. An unproven same-named distro or leftover data directory is preserved unless the user explicitly confirms its permanent replacement in the setup UI or passes `--confirm-destructive`. When replacing an app-owned local gateway, the removal step is shown as part of progress and can be retried on failure.

The managed distro is locked down and is not intended to be a normal interactive Ubuntu profile. For editing `openclaw.json` as the `openclaw` user and using root for protected-file administration, see [Managing the locked-down WSL gateway](WSL_GATEWAY_ADMIN.md).

### Capabilities and access presets

The draft updates setup capability flags and runtime `Node*` values in memory
only. Its exact profile order is System, Canvas, Screen, Camera, Location,
Browser, Tts, Stt. Strict (`ReadOnly`) enables Canvas and Screen; Balanced
(`Standard`) adds System, Tts and Stt; Open (`Full`) enables all eight. Only an
implicit bundled all-on placeholder defaults to Balanced at draft creation.
Explicit Open and custom choices survive
navigation. Device information is fixed, not a toggle. System controls
`system.run` and `system.run.prepare`, not file/clipboard access.

Profiles use vertical, full-width native single-selection rows with the system
selection indicator, selected background and keyboard behavior. Strict, Balanced
(Recommended) and Open appear without a redundant section heading. The adjacent
**Fine-tune** expander shows the selected-capability summary.
Gateway choices, the Balanced preset and the final Chat choice share the
`RecommendedBadge` control: a localized caption with an accent outline, 8 px
horizontal and 4 px vertical padding, and disabled-state brushes inherited from
the choice's enabled state. The Balanced label has no parenthetical suffix;
its accessible choice name still includes the recommendation.
Opening it only inspects existing flags; it does not select Custom. Editing any
flag shows a **Custom capabilities** badge with no preset falsely selected, even
if the flags later match a preset. Imported arbitrary flags have no invented base.
Explicit Custom intent and `FineTuneExpanded` live only in `SetupAccessDraft` and
survive page recreation. Selecting a preset reapplies exactly its eight flags
without changing disclosure state. The Toolkit `SettingsExpander.Items` contain
eight real `SettingsCard` rows; explanatory notes use `ItemsFooter` with a
16 px horizontal and 12 px vertical inset, keeping both paragraphs off the card
edges while preserving wrapping. Browser prerequisites remain in the Browser row
description.

Node mode, local MCP and Ollama sharing are independent of profiles. When both
transports are off, profile/capability controls are disabled and dimmed without
clearing choices; Fine-tune remains inspectable, with an explanation to enable
either transport. Custom never
bypasses these gates. Browser requires Node mode and a genuinely available Gateway,
not the default loopback URL; a pending selection remains visible.

Onboarding does not probe Windows privacy settings or grant OS access.
Remembered capture consent, execution approvals, sandbox grants, MCP tokens
and voice configuration remain in native Companion Settings. Runtime screen
capture uses monitor capture, not a guaranteed picker on each request.

Next routes directly to WSL review (managed), AI setup (committed existing/remote),
or asynchronous completion (MCP-only/deferred). Review Back returns to capabilities.
There is no ordinary permissions page or progress dot: the standard managed route
has seven stages, existing/remote five, MCP-only/deferred three. Headless
`SkipPermissions` is unchanged. The obsolete Windows-access preview and its
dedicated probing code have been removed; the inert gallery covers current
capabilities without probing Windows or opening Settings.

Local AI uses the existing managed Windows llama-server eligibility coordinator.
Unknown readiness and unsupported pinned recovery block installation only when
Local AI is required. Users may turn it off outside recovery. Model selection,
networking consent and the original wizard-skip preference survive Back/Next.
Consent authorizes installation to change global `.wslconfig` and stop all WSL
distributions once; the review itself never performs those actions.
Selected Tailscale requires a successful bounded Windows signed-in/MagicDNS
probe. WSL browser/auth-key sign-in is distinct from Windows sign-in and from the
separate, default-off identity-trust toggle. Auth keys remain session-only.
Serve is private tailnet access, not Funnel.

The WSL review shows its current installation blocker directly below the
centered heading, before optional choices. `SetupAccessDraft.GetInstallRequirements`
is the shared source for both `CanInstall` and this presentation, including
incomplete or stale inspection, exact-target replacement consent, Local AI,
networking consent and Tailscale readiness. When replacement is required, the
same page shows the full inspected consequences and an exact-distro checkbox near
the bottom, above the fixed footer. It starts unchecked and never installs or
navigates when changed. **Install** stays disabled while consent is outstanding.
Checking it enables Install only when all requirements are satisfied; other
requirements keep their specific recovery actions. Fresh installs and Local AI
recovery do not show replacement consent. The host rechecks `CanInstall` before progress.
A changed draft cannot turn a click on an already displayed review action into
installation. Users who want to keep an existing setup are directed back to the
existing-Gateway route, not encouraged to approve replacement.

Networking review returns to its caller: review to networking to review, or
Local AI to networking to Local AI. Recovery mode, pinned model and explicit
networking consent remain intact. Reviewing a requirement performs no WSL
changes; installation remains the separate mutation boundary.

### Connect your AI

The focused client negotiates authenticated gateway method advertisements.
`openclaw.setup.detect` only presents available choices. Selecting a candidate,
provider login, local-provider preparation or manual key starts the corresponding
`openclaw.setup.*` operation. Detection never silently chooses or tests another
provider. Provider choices come from the gateway. Bundled brand aliases, bounded
public HTTPS metadata logos and native Fluent fallbacks supply their visual
identity without changing authentication. Metadata action labels are preserved.
See [Provider artwork](PROVIDER_ARTWORK.md) for trust boundaries and format differences.

Returning from a provider dialog preserves the selected provider, chooser scroll
offset and original focus target. Restoration remains bound to its page
generation and captured operation. Deferred focus/caret bring-into-view requests
are intercepted inside the chooser before the outer scroll viewer handles them.
That restoration scope ends on new pointer/keyboard input, focus departure,
another provider operation or page close, so subsequent user scrolling is normal.

The grouped screen uses native WinUI `SettingsCard` commands and
`SettingsExpander` controls. Only this task page uses a compact, centered header
band with an 80-DIP mascot beside the wrapping title/status; earlier setup
pages retain their large centered heroes. Each provider has one native command
surface, not a selectable ListViewItem wrapped around another clickable card.
The page-local compact row style does not alter global Toolkit resources.

1. **Local AI on this PC** is a concise native choice row beside the detected
   Gateway choices, with actual GPU/model facts and its trailing action. It has
   no separate introductory heading or always-visible technical paragraph.
   Accessible help distinguishes managed Windows Local AI from NVIDIA's hosted API.
2. **Available on your Gateway** presents server-returned candidates, recommended
   website links when needed, and unavailable discoveries.
3. **Set up a local model** presents Gateway preparation choices before providers.
   The catalog and action labels come from Gateway metadata, not a Windows list
   of installed services. The fallback action is **Connect / Set up**.
4. **Connect an AI provider** retains featured sign-in, a full **API Keys** row
   and auth-only **More sign-in options**. Auth kind determines the fallback
   **Pair**, **Set up…**, **Configure…** or **Sign in** action; metadata wins.
5. **Connect with an API key or token** is the separate expanded form. The
   API Keys command uses `FluentIconCatalog.Key` and opens the form without
   selecting or activating a provider. Its picker, PasswordBox and inline
   **Connect** are horizontal when the actual form width and current text scale
   allow, and stack otherwise. Empty keys cannot submit. The selected provider
   is revalidated at the explicit Connect boundary; secrets clear on submission
   and close. There is no duplicate page-footer Continue.

`AiSetupPresentationModel` suppresses a managed-local duplicate only when the
exact model reference and Gateway identity both match. Native card actions are
explicit; discovery and keyboard focus alone never start a provider. Enter/Space
use that same command, carrying its exact kind, identifier and model reference.
The inline API form is already visible when there are
no Gateway candidates or visible Local AI choice, without selecting its provider. Website links do not run
installers. **Check again** repeats discovery explicitly; a failed scan is not
an empty catalog. Native-conversation discovery is a visible, default-off opt-in
when the Gateway requests it. The current Boolean is sent only with an explicit
provider action; unchecked means false, never automatic permission. When the
Gateway does not request a preference, the parameter remains absent. Refreshing
presentation retains the user's current checkbox value.

#### One provider operation, one dialog

Provider startup, noninteractive server progress, preparation, activation and
exact verification stay inline on **Connect your AI**, with the shared mascot,
visible status and a compact Cancel action whenever the operation can be
cancelled. They do not open a loading-only popup. The page-owned
`ProviderSetupDialog` opens for typed input, consent, acknowledgments, device
codes/browser sign-in instructions, or an explicit recovery decision. Device
codes and sign-in links remain visible while the Gateway polls for completion.
An input prompt stays open, disabled, while its answer is being submitted so
validation retries preserve the dialog and selection context. Submitted secrets
are cleared immediately. The page tracks answer submission separately from the
client's `Uncertain` protocol phase, so recovery does not expose a stale prompt.
Inline errors remain visible during loading. Cancel is available once the client
has allocated its owned wizard session ID, including while the start reply is
pending; cancellation still requires the Gateway's acknowledgement.
The page retains the operation lifetime and locks the existing choices even
while the popup is hidden. The client still owns protocol state; the dialog
never acquires another Gateway client or persistence store.
Confirmed rejection returns to the same cleared key field; confirmed cancellation
returns to usable choices. An uncertain outcome stays available for reconciliation
and is never replayed as another activation.

Server-owned client notes/actions still require **Continue**, typed inputs use
**Submit**, and a client-owned device-code acknowledgment uses **I've signed in**.
Gateway-owned progress has no answer button and is polled without an answer.
Device codes remain selectable and have an explicit Copy action. Structured
device-code prompts show the formatted code card, instructions and expiry instead
of also repeating the Gateway's plain-text prompt. Prompts without structured
device codes retain their messages, and errors remain visible separately. The fresh HTTPS
URL from a user-started provider operation may open once; discovery, rerenders,
reconnects and uncertain reconciliation never auto-open tabs. The manual sign-in
link remains available, and opening a browser never counts as successful login.

`GatewayAiSetupController` retains the explicit preparation choice and catalog
preference, then automatically activates only its authoritative
`preparedModelRef` on the unchanged Gateway connection. Inline loading does not
introduce an extra page-level Activate button. Server download, plugin review
and promotion confirmations remain mandatory. Missing receipts, changed
connections or unsupported activation fail visibly without choosing another
model. If an uncertain preparation is later reconciled to an authoritative
prepared receipt, automatic continuation stays disarmed; an explicit
**Activate model** action in the same dialog can use that exact receipt without
replaying preparation. It cannot bypass a changed Gateway or generation.
This Gateway preparation path does not auto-install managed NVIDIA Local
AI or bypass its separate review/consent. A restart-required activation waits
up to 30 seconds for the existing connection's fresh same-authority handshake
before exact verification; timeout offers reconciliation without mutation replay.

This interaction comparison is pinned to
`openclaw/openclaw@d69a5e74895cf64109eca0d3a172c17b74ddbe7b`,
`OnboardingAISetup.swift:1181-1212,1474-1506` and
`OnboardingAISetupSheet.swift:52-57,127-137,183-212`.
The compact Windows header and continuous dialog are intentional density/lifetime
improvements, not claims that Mac has identical geometry or never internally
reopens its sheet.

#### Managed Local AI in the same setup window

`LocalAiOnboardingObservation` observes independently of Gateway discovery and
fences cancellation, refresh and late results. `SetupLocalAiHost` reuses the
eligibility policy, canonical receipt/file inspection, runtime snapshot and
`LocalAiSetupRouteResolver`. Observation never calls runtime `RefreshAsync`:
that runtime API may publish or withdraw a Gateway route. Observation does not
install, migrate receipts, start inference, select a default or grant consent.

The row distinguishes checking, eligible but absent (**Set up Local AI**),
installed/stopped (**Start and use**), healthy (**Use this model**), damaged
(**Repair Local AI**), busy GPU, unknown facts, unsupported hardware and an
unsupported Gateway. A loaded managed model's own GPU allocation is not treated
as another workload. Installed, healthy, reachable and Gateway-verified are
separate facts.

`LocalAiOnboardingSnapshot.ShowLocalChoice` hides only a confirmed unsupported
fresh device: no NVIDIA GPU, insufficient total GPU memory, or CUDA capability
below the runtime requirement. It uses the existing eligibility failure code,
not another hardware probe or a production test override. Unknown/checking
facts, busy GPUs, old drivers, missing runtime/catalog entries and unknown
models stay visible. Receipt or damaged-receipt evidence, a pinned installation
or retained runtime attention also keeps the row visible, without promoting its
action or changing repair/admission rules. Unsupported remote Gateway attention
is unchanged. Hiding the local row collapses its empty container, not independently
detected Gateway choices.

The layout comparison is pinned to
[`OnboardingAISetupView.swift` at `fef6b1290e412761888865da5b61ee1c0ce29586`](https://github.com/openclaw/openclaw/blob/fef6b1290e412761888865da5b61ee1c0ce29586/apps/macos/Sources/OpenClaw/OnboardingAISetupView.swift#L191-L674):
results at 191-254, candidate rows at 313-350, preparation at 440-480,
API Keys at 543-571, auth at 574-613 and manual entry at 616-674. Windows keeps
its managed local primary-model lifecycle and explicit consent, not Mac credential
reuse or utility-model semantics.

The inert native gallery now inventories 93 scenes in both themes (186 scene/theme
states), including a distinct fresh-unsupported-hidden variant, installed
unsupported attention, preparation before providers and the expanded manual form.
The native proof inventory adds two mounted tests and the Working readiness case
to the previous 108-case selection (111 expected when the same selection is used).
These are source inventory counts, not claims of current rendered captures.
Existing source-immutability, owned-input, DPI and capture guards remain required.

Set up and Repair enter the existing `LocalAiSetupControl` and networking review
inside this `SetupWindow`. **Install and use** is a separate consent boundary.
The host rechecks the same active, uniquely app-managed local Gateway and receipt
before selecting `BuildLocalAiRecoverySteps`, including on a first installation
with no receipt. These steps do not create/delete a distro, reinstall the Gateway,
mint tokens or pair devices. The existing global mirrored-networking warning
and explicit consent remain required. Back restores the prior configuration
draft; capabilities, route and startup preference are retained.

Start and Use recheck ownership, receipt/model identity, eligibility and canonical
provider publication admission before calling the existing runtime. A Gateway
switch during the read-only admission check is a rejected selection, restoring
choices without starting the runtime. A target change after startup/publication
remains uncertain and retains the exact-target reconciliation boundary. Publication
preserves the recorded fallback and fails closed if the primary model or managed
provider drifted outside that contract; health alone cannot overwrite a newer
Gateway choice. A fresh setup-owned connection then verifies the exact returned
model. An uncertain local action retries verification, not a hidden activation.
Provider sessions must settle or confirm cancellation before local review.
Closing drains page observations and the recovery pipeline before releasing the
setup lock. Settings and headless entry points retain their existing contracts.

#### Setup and utility metadata compatibility

The pinned upstream contract is
[`openclaw/openclaw@fef6b1290e412761888865da5b61ee1c0ce29586`](https://github.com/openclaw/openclaw/tree/fef6b1290e412761888865da5b61ee1c0ce29586).
The same setup schema blob (`dbd8461f87f034b675b874313d2f401ac7c682c9`)
is present in release `v2026.9.6` at
`eb377ac59e6c9fd6c7705028034812becf00271b`.

- [Schema lines 255-374](https://github.com/openclaw/openclaw/blob/fef6b1290e412761888865da5b61ee1c0ce29586/packages/gateway-protocol/src/schema/openclaw.ts#L255-L374):
  optional `modelTarget: "utility"` decorates the same choice kinds;
  `setupModel` and `utilityModel` are preserved alongside `configuredModel`.
- [Role validation](https://github.com/openclaw/openclaw/blob/fef6b1290e412761888865da5b61ee1c0ce29586/src/system-agent/setup-inference-core.ts#L583-L594)
  (blob `6b8cc6e07ee6fb5367c7fd050fc4a53fd63bfc5b`) requires exact role matching.
  [Provider staging](https://github.com/openclaw/openclaw/blob/fef6b1290e412761888865da5b61ee1c0ce29586/src/system-agent/setup-inference-credentials.ts#L376-L396)
  (blob `9d93a352ed2342a695d9d189c4d1a8eefe7e016f`) enforces it too.
- [Existing-model activation](https://github.com/openclaw/openclaw/blob/fef6b1290e412761888865da5b61ee1c0ce29586/src/system-agent/setup-inference-activate.ts#L121-L148)
  (blob `b3abbf8dbf42b8a84d8fb06702eaba027edb2aca`) checks exact model and role.
- [Utility route selection](https://github.com/openclaw/openclaw/blob/fef6b1290e412761888865da5b61ee1c0ce29586/src/agents/utility-model.ts#L17-L50)
  (blob `666784e3cd5e8a689fbc287e692f5f13628b36ba`) can use a utility route when
  no primary exists.
- [Verification result](https://github.com/openclaw/openclaw/blob/fef6b1290e412761888865da5b61ee1c0ce29586/src/system-agent/setup-inference-turn.ts#L582-L694)
  (blob `1ce3a7737950f89798e655c7167e29bb72dbe535`) includes that role.

Windows hides utility-only choices from the main-assistant list with a visible
explanation, rejects utility-marked activation/verification as main-assistant
success, and preserves absent-field behavior for older Gateways. Verify requests
send only the existing optional `agentId`, not `modelRef` or an invented
`"primary"` role. Full utility onboarding and a Gateway protocol upgrade are not
part of this change.

Interactive authentication and activation retain the shared `wizard.next` and
`wizard.cancel` contract, including sensitive fields, device codes, browser
actions and gateway-executed progress. A generic terminal wizard result or a
prepared model is not proof of working inference. Activation receipts, exact
model verification and restart reconciliation remain distinct.

An explicitly selected Local AI installation passes its resolved gateway model
reference to verification after the installation pipeline succeeds. The UI does
not guess that reference from a catalog ID. Cancellation or an uncertain reply
does not replay a mutation. Insufficient operator scopes remain an explicit
error, not a reason to fall back or use node credentials.

After verification, the native chooser finalizes Windows-node workspace guidance
and the reviewed startup preference before restart. Native Chat and the flyout
retain their independent Dashboard action. Windows capability consent remains
in native Permissions, not in the web dashboard.

### Classic OpenClaw onboard (compatibility)

After OpenClaw onboard completes-or when the user explicitly skips it-local setup runs the installed gateway CLI's non-interactive baseline initializer against the final runtime workspace, then writes fixed Windows-node guidance into a setup-owned managed section of that workspace's `AGENTS.md`. The section is replaced idempotently between markers, preserves user-authored `AGENTS.md` content and file permissions outside those markers, and does not modify OpenClaw source files. This helps the initial companion-app OpenClaw session know to use the Windows node / `nodes` tool for Windows desktop, files, screenshots, camera, notifications, browser proxy, and Windows command tasks.

Renders server-defined setup steps via RPC (`wizard.start` / `wizard.next`). The gateway controls the flow - steps can be:
- **Note** - informational messages
- **Confirm** - yes/no decisions
- **Text** - free-form input (with PasswordBox for sensitive fields like API keys)
- **Select** - radio button choices (e.g., AI provider selection)
- **Progress** - loading indicator for background operations

If the gateway doesn't support the wizard protocol or is unreachable, this screen shows an "offline" message and can be skipped.

The wizard keeps recovery choices visible while setup steps are running so users can start the wizard again or skip it for now if an auth flow stalls. If the gateway restarts or the wizard connection is lost while setup is running, the same recovery choices are presented in the error state so the user is not trapped retrying a broken session.

Gateway-driven onboarding does not show a Back action. There is no dedicated `wizard.back` RPC. Gateways can instead provide in-band `__back` or `back` options, which protocol clients submit through `wizard.next`; Companion intentionally filters those options because its former local payload replay displayed stale state while the authoritative Gateway session remained on a later step. Users can restart onboard or skip and exit from **More options** instead.

Exact Gateway 2026.7.1 has a terminal compatibility path for an app-managed local WSL gateway. When the final `model-check` answer produces WebSocket close 1012 before the gateway can return `done`, setup retries the temporary `NoListener` state and the typed snapshot-changed race that can occur while the listener is restarting. Other unknown or conflicting endpoint ownership fails immediately, and no credential is sent until the managed endpoint is verified again. A retryable startup close 1013 remains inside the existing reconnect timeout. Setup completes only after a fresh authenticated `hello-ok` handshake. Other versions and steps keep the normal managed-local wizard replay behavior with the same bounded ownership wait; remote gateways and other disconnects do not enter this recovery path.

The headless setup engine also treats one terminal wizard payload as completion instead of failure. When the answers applied by the wizard restart the gateway, the gateway can tear down its own hosted wizard TUI and return a terminal payload whose error is exactly `Error: TUI exited from signal SIGTERM`. Setup accepts that result only when the payload is terminal and the request it just sent answered the authoritative final step, so the wizard is not cancelled after it already finished. The final step must be a plain acknowledgement note with no options whose id or title normalizes to `done`, and when the gateway supplies step position metadata it must also be the last step. An earlier `SIGTERM`, a progress poll, a replayed wizard session, any answerable step, any other step id or title, a non-terminal payload, and any other message (different signal, extra text, or different casing) all keep the wizard failure. Only surrounding whitespace is tolerated in the message. Reload-mode restoration, the one-shot managed restart, health verification, and provenance checks are unchanged and still fail closed.

When the gateway config wizard surfaces an error and the active gateway is an app-managed WSL distro, the error state also offers **Open terminal** and **Restart gateway**. The wizard does not parse or classify the gateway's error text; it leaves the message visible and selectable so the user can copy any command the gateway reports. The buttons reuse the shared `GatewayTerminalLauncher` and `WslGatewayController` (in `OpenClaw.Connection`, also used by the Connections tab). Restart re-enters the gateway config wizard (the provider/model onboarding step - not the whole V2 onboarding, and without re-installing the WSL distro) so fixes such as newly-installed tools are picked up on `PATH`. Because the gateway restart clears its wizard session, this resumes at the first config question rather than the exact step that failed. Detection is gated on `GatewayRecord.SetupManagedDistroName`, so it never appears for remote/SSH gateways.

### Completion and recovery
Focused setup finishes through the three-choice native page. Startup is reviewed
before completion and defaults on for a fresh, non-isolated setup. Recovery
preserves that preference; isolated hosts keep it off. The classic completion
page retains its summary and Open chat action. Errors retain diagnostics and
recovery instead of showing success.

### Artwork and motion

Onboarding uses the native vector `OnboardingMascot` control, adapted from
OpenClaw's Mac character at upstream commit
`9afae26e080602bf1e330bfd88fcbd39a2bf22c0`. Idle, curious, thinking, working,
happy, sad and celebrating poses reflect the current task. Working includes the
hard hat and hammer animation. Windows animation preferences select static
poses, and hidden/unloaded controls stop ticking. Navigation also respects the
Windows animation setting. Motion never adds a completion delay.

Provider artwork is bundled under `Assets/Setup/ProviderIcons` with its upstream
notices. Keep asset identity separate from the gateway-provided provider catalog.
Check light, dark and high-contrast rendering and the actual published
library-qualified paths, not only loose development assets.

## Security

### Native connection checks and cancellation

The tray-hosted native connection editor is a main-window page. It supports a
gateway address, a setup code or shared token, and optional SSH host, user and
ports. SSH uses existing OpenSSH keys/configuration, not a separate key store.
The browser-only profile route is not implemented through native credentials.

The connection editor shows the Gateway-stage progress indicator above a
separate row of wrapping Back, Cancel, Check and Next actions.

Check connection authenticates with an isolated identity copy and optional
temporary owned SSH listener, without saving a gateway or changing the active
connection. Editing invalidates the displayed check result. Next checks the
current draft again and transactionally commits before the PC capabilities page.
Failed checks, editor cancellation and cancellation during
Next leave or restore the previously active gateway. If rollback cannot be
confirmed, setup shows an error and does not advance. After Next has succeeded,
closing setup retains that explicitly committed gateway. Pairing-pending is not
shown as a successful operator connection or as permission to enter AI setup.

The editor retains a temporary key for the same draft while gateway approval is
pending. Successful bootstrap handoff tokens remain in memory until Next, allowing
revalidation without reusing a consumed bootstrap code. Draft changes and editor
close discard this staging state. No received token is written into the temporary
key file during Check.

The onboarding wizard follows these security practices:

- **Input validation**: Setup codes limited to 2KB, decoded JSON validated, gateway URLs checked via `GatewayUrlHelper`
- **URI scheme whitelists**: Only `ms-settings:` for permissions and `http/https` for browser-launch links
- **Token protection**: Query params stripped from all log output
- **Gateway-owned pairing**: Device approval uses the gateway CLI/API path so scope checks, token issuance, audit, and broadcasts stay centralized
- **Error sanitization**: Exception details logged but not shown to users

## Credential Storage

Gateway credentials are registry-backed. Setup codes and QR payloads create or update a `GatewayRecord`; bootstrap credentials live in `GatewayRecord.BootstrapToken`, long-lived manual tokens live in `GatewayRecord.SharedGatewayToken`, and post-pairing device tokens are saved in the per-gateway identity directory. `SettingsManager` may read legacy `Token` / `BootstrapToken` JSON fields for migration, but it does not write them back.

## Localization

All user-visible strings use localization helpers with the `Onboarding_*` key namespace. Setup library pages use `SetupLocalization`; tray surfaces use `LocalizationHelper`. Supported languages are discovered from the `Strings/<locale>/Resources.resw` directories; the current locales are English, French, Dutch, Brazilian Portuguese, Chinese Simplified, and Chinese Traditional.

Translations are AI-generated following the repo convention. Technical terms (Gateway, Token, Node Mode) are kept in English across all locales.

## Developer Guide

See [DEVELOPMENT.md](../DEVELOPMENT.md#developing--testing-the-onboarding-wizard) for build instructions, environment variables, and testing workflow.

### Test Isolation

`SettingsManager` loads `%APPDATA%\OpenClawTray\settings.json` by default. Onboarding tests must not use `new SettingsManager()` without an isolated settings directory, because local user settings such as `EnableNodeMode=true` change setup behavior.

Use a temp settings directory for tests that construct `SettingsManager`, or set `OPENCLAW_TRAY_DATA_DIR` before the test process starts.

Real tray and setup proof launches must set all three isolation variables:
`OPENCLAW_TRAY_DATA_DIR` is the direct data folder;
`OPENCLAW_TRAY_APPDATA_DIR` and `OPENCLAW_TRAY_LOCALAPPDATA_DIR` are separate
roaming and local roots. The product may append `OpenClawTray` to those roots.
`OPENCLAW_TRAY_LOCAL_DATA_DIR` is a legacy direct-folder override, not a
replacement for the canonical local root.

Data directories alone do not isolate Windows registration. When the direct
data override is present, `AppIdentity.IsIsolated` suppresses Toolkit toast
activation subscription/unsubscription, toast display, and URI registration.
Startup mutations are explicitly refused. WSL keepalive lifecycle actions
require an explicitly managed gateway record, rather than adopting the normal
user's default distro. These guards do not authorize using an existing real
gateway or profile in a test. Use only owned disposable resources and compare
protected profile metadata and notification/COM, URI, and startup registration
fingerprints before and after native runs.

### Setup image packaging

Setup images use `ms-appx:///OpenClaw.SetupEngine.UI/Assets/Setup/...` URIs.
Published installer and portable ZIP payloads must include that library-qualified
directory, not just the tray's loose `Assets/Setup` copies. The tray publish target
preserves both layouts; `SetupAssetPublishTests` executes that target against a
clean directory and checks every setup asset, including nested SVGs and notices.

### Key Files

| Path | Purpose |
|------|---------|
| `src/OpenClaw.SetupEngine.UI/SetupWindow.xaml(.cs)` | Tray-hosted setup shell, run lock, preview routing, and page navigation |
| `src/OpenClaw.SetupEngine.UI/Pages/SecurityNoticePage.xaml(.cs)` | First-run device-trust warning before setup choices |
| `src/OpenClaw.SetupEngine.UI/Pages/WelcomePage.xaml(.cs)` | Native Gateway, WSL, and connect-existing choice with capability/readiness checks |
| `src/OpenClaw.SetupEngine.UI/Pages/AdvancedSetupPage.xaml(.cs)` | Connect-existing handoff to Connection settings |
| `src/OpenClaw.SetupEngine.UI/Pages/CapabilitiesPage.xaml(.cs)` | Shared capability profile and transport settings draft |
| `src/OpenClaw.SetupEngine.UI/Pages/ProgressPage.xaml(.cs)` | WSL gateway install progress and gateway-installed handoff |
| `src/OpenClaw.SetupEngine.UI/Pages/WizardPage.xaml(.cs)` | OpenClaw onboard provider/model/key wizard driven by gateway `wizard.*` frames |
| `src/OpenClaw.SetupEngine.UI/Pages/AiSetupPage.xaml(.cs)` | Focused provider choices, authentication, verification and bounded page-owned cleanup |
| `src/OpenClaw.SetupEngine/OnboardingFlowPolicy.cs` | Interactive stage list and installation subset; preserves the headless pipeline |
| `src/OpenClaw.SetupEngine/GatewayAiSetupClient.cs` | Typed route-bound discovery, explicit selection, activation and reconciliation |
| `src/OpenClaw.SetupEngine/GatewayAiSetupController.cs` | Bounded gateway-executed wizard progress polling |
| `src/OpenClaw.SetupEngine/SetupGatewaySession.cs` | Shared temporary setup operator session with registry identity and endpoint provenance |
| `src/OpenClaw.SetupEngine.UI/Controls/OnboardingMascot.cs` | Native vector control, animation preferences, theme updates and unload lifetime |
| `src/OpenClaw.SetupEngine/GatewayWizardRestartRecoveryPolicy.cs` | Exact terminal-restart classification and bounded restart provenance/reconnect retry policy |
| `src/OpenClaw.SetupEngine.UI/Pages/CompletePage.xaml(.cs)` | Success, failure, log/help, and startup preference summary |
| `src/OpenClaw.Connection/GatewayRegistry.cs` | Persistent gateway records and migration target |
| `src/OpenClaw.Connection/GatewayConnectionManager.cs` | Operator/node connection lifecycle used by onboarding |
| `src/OpenClaw.Tray.WinUI/Services/SetupExistingGatewayClassifier.cs` | Existing gateway classification for Welcome and startup gating |

### Focused validation

Run the repository-required build, Shared and Tray suites, plus
`OpenClaw.SetupEngine.Tests` for the flow and AI protocol contracts.
`AiReadyPageRenderingTests` covers the three destinations and isolated startup.
`ApprovedMock_FivePagesAndProviderPopup_LightAndDark` provides an opt-in native
comparison without installation. Building fixtures alone is not rendered proof;
high contrast and Windows text scaling need authorized visible validation.
`OnboardingAiPageTests` mounts the production page with a scoped transport
double and checks explicit selection, masked input, conversation-discovery
consent, exact-model retry, uncertain replies, and cancellation before handoff.
`OnboardingArtworkRenderingTests` decodes the bundled SVGs through WinUI,
checks library-qualified URIs, and renders all static mascot moods in light and
dark themes. Set `OPENCLAW_UI_PROOF_DIR` to an isolated artifact directory to
save the current rendered scenes.

Mounted tests are not real-provider or gateway-to-node proof. Keep those
claims separate, run the required WSL/MXC validation path for setup/connect
changes, and report unavailable desktop or provider dependencies explicitly.
The real setup fixture uses separate data, roaming-root and local-root paths.
Its uninstall passes run-specific startup registry/task identities so teardown
cannot remove the normal Companion's startup registration.
The real Gateway proof also calls `openclaw.setup.detect` through the production
focused client, asserting advertised support, operator scope and typed discovery
without selecting a provider or starting a wizard. Provider-specific sign-in and
billing-dependent inference remain separate from that read-only contract proof.
