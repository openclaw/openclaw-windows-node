# Onboarding Wizard

The onboarding wizard can install an app-owned WSL gateway, acquire a native
Gateway MSIX through Microsoft Store and configure it, or connect to an existing gateway.

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

### Native Gateway MSIX (not isolated)

**Package-aware verification (2026-09-16, package 0.0.0.1 ARM64):** the original
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
check enables it. By the 2026-09-18 product decision, this
continues to run the existing Gateway MSIX with the signed-in Windows user's
access. The separate isolation warning and acknowledgment checkbox are removed;
the general security notice and provider/onboarding consent remain explicit.
This UI gate does not provision an MXC session or change the runtime identity.
Native and WSL use the **same WinUI `WizardPage`**, not separate provider/model
wizards. WSL is always shown as the second Welcome choice after native,
followed by **Connect to an existing gateway**.

Companion checks current-user registration for the Store package
`OpenClawFoundation.OpenClawGateway` and publisher
`CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC`, package health, and the package-qualified
`clawctl.exe` and `openclaw.exe` aliases. It does not resolve an npm installation
from `PATH`. Native setup uses the same Windows capabilities and permission
selection as WSL, followed by a native-specific review without WSL, Local AI or
Tailscale provisioning. After confirming the review, progress runs automatically.
A missing package opens the
[OpenClaw Gateway Microsoft Store listing](https://apps.microsoft.com/detail/9nv70lv3d6xc?hl=en-US&gl=US);
unhealthy registration or unavailable aliases show an explicit repair error.
No local MSIX path or environment-variable configuration is required.
The original `OpenClaw.Gateway` / OpenClaw Foundation development publisher pair
is still accepted for existing installations. If both identities are installed,
new setup reports duplicate registrations instead of guessing which to use. Existing
profiles resolve their original package family even when both packages are installed;
there is no implicit migration.

Microsoft Store owns architecture/package selection, signature validation, user
consent, and deployment. Opening the listing is not reported as a successful install:
Companion waits for actual package registration and verified aliases, then
automatically prepares the profile and opens the shared Gateway wizard.
The wait is cancellable and limited to five minutes. Cancelling in Companion does
not cancel Windows installation. Errors/timeouts offer **Retry setup**; the normal
path has no separate install, availability-check or wizard-launch buttons.
An already installed healthy package skips this handoff.
Companion never downloads a package or changes certificate trust.

Progress reuses the WSL spinner/checkmark rows. Completion lists the configured
native Gateway and saved Windows capability choices, with a reminder that node
pairing, Windows permissions and command approvals still apply. It does not
claim the stopped setup runtime or a not-yet-paired Windows node is running.

The native path:

1. Creates a dedicated configuration and workspace under
   `gateways\<gateway-id>\native-gateway` in the Companion data directory.
   `OPENCLAW_STATE_DIR` and `OPENCLAW_CONFIG_PATH` keep this separate from the
   user's default `.openclaw` profile. A different profile is not a security
   sandbox.
2. Runs the installed package's `clawctl setup` with captured progress/errors in
   Companion, without opening a TUI. The current package may extract its bundled Node runtime.
   Older proof packages require a separately installed compatible Node runtime
   (the supplied `0.0.0.0` proof rejects Node 22.19.0). Companion does not install
   missing prerequisites.
3. Validates the dedicated configuration, suspends config reload, and starts the
   Gateway through the native runtime owner. Before every credential handoff,
   including reconnects with a saved device identity, setup verifies package-owned
   listener provenance. The staged record is **not** made active in the registry.
4. Automatically pairs the setup's own Companion identity if required, then
   opens the shared `WizardPage` using `wizard.start` with `mode: "local"` and
   `installDaemon: false`. The same `wizard.next` transport and cards render the
   upstream security acknowledgement, provider, authentication, and model steps.
   No consent or provider answer is supplied automatically. Native console output
   is tailed from the dedicated profile, never through WSL.
5. Error-free wizard completion or the validated optional-tail handoff permits
   finalization. Setup stops its
   runtime, restores the original reload setting, checks the selected
   local/loopback/token configuration, runs `config validate --json`, restarts
   with owned-listener proof and runs authenticated `gateway health --json`.
   A failed gate remains retryable and does not publish the staged record.
6. Stops the setup-owned runtime before reloading and updating the registry.
   **Open Companion to connect** restarts Companion into the existing
   connection flow with the paired operator identity. This does not approve the
   separate Windows node role. Current Windows node permissions are preserved.

Companion owns the native gateway process lifetime after this handoff. It starts
the selected native gateway when connecting, can start it again after an exit,
and stops its owned process when disconnecting, switching away, or shutting down.
The MSIX package itself owns updates. Companion does not install an OS service
or call `openclaw gateway install`.

Cancelling setup, returning from the wizard, or closing setup stops only its own
recovery terminal/runtime and restores reload. Cancel is not successful setup.
**Restart gateway** controls the native owner; **Open terminal** opens a shell
scoped to the dedicated profile and package aliases, not a second onboarding TUI.
Its lifetime ends with setup or a restart. Configuration
already entered is retained in the dedicated native profile so it is not lost
on retry, including after returning to Welcome or reopening Companion. A
credential-free draft descriptor under `gateways\native-setup-draft.json`
resumes the same profile until successful publication.
The credential-free reload backup survives an interrupted process so a retry
does not mistake the temporary `off` mode for the user's preference.
Existing WSL distributions, remote gateways, and the default native
OpenClaw profile are not replaced. The native path does not enter the WSL
cleanup, Local AI installation, or WSL repair pipelines.

This integration targets the packaging contract at
[`9a8cd4a`](https://github.com/openclaw/openclaw-windows-packaging/tree/9a8cd4af139513c21d290a01a8a1f2be19b602bc).
Its README explicitly reserves isolated agent sessions for future work. A future
MXC option needs a real session provisioning, eligibility, and lifecycle contract;
MSIX registration or the presence of `IsolationProxy.exe` is not sufficient.

### Planned MXC native Gateway recommendation policy

The lifecycle/session-provisioning requirements below were recorded on 2026-09-16
and remain future work. The 2026-09-18 UI decision implements capability-first
recommendation and Windows-update guidance for the existing signed-in-user native
Gateway without claiming session isolation. See [Welcome](#welcome) for the
implemented recommendation behavior.

- **Lifecycle owner:** Companion provisions and supervises the MXC session,
  delegates preparation to packaging and onboarding to upstream OpenClaw, and
  runs both inside the isolated agent identity. Preserve that identity and its
  configuration across Companion restarts; stop on exit and deprovision only
  on explicit removal. Do not re-provision or re-onboard on restart. See the
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

The final MXC path must actually provision and run the Gateway inside an
isolated session. Do not relabel the current ordinary-process MSIX path as MXC,
or recommend it as isolated based only on a successful eligibility check.

## Overview

On first launch, the wizard appears only when there is no usable saved gateway connection. Users with existing gateways manage connections from the tray app's Connections tab. The local WSL setup affordance in Connections is shown only when setup has not already created an app-owned WSL gateway on this device.

The setup flow walks users through:

1. **Security notice** - Device-trust warning before setup choices
2. **Welcome / Advanced** - Capability-gated native Gateway recommendation, optional WSL fallback, or connect existing gateway from Settings
3. **Capabilities** - Recommended profile, inline Windows permission status, and install review
4. **Local setup progress** - Fresh app-owned `OpenClawGateway` WSL installation
5. **Gateway installed** - Explicit handoff from infrastructure setup to OpenClaw onboard
6. **OpenClaw onboard** - Gateway-driven provider/model/key configuration
7. **All set** - Feature summary, startup preference, and completion

The setup flow no longer configures remote/manual gateways inline. The Welcome page's **Connect to an existing gateway** option routes through `AdvancedSetupPage`, closes setup, and opens the tray app's Connections tab.

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
screen-reader announcement. The requested description is **Install a local,
MXC contained OpenClaw gateway**; this copy change does not implement MXC
session containment, which remains outstanding for the current signed-in-user runtime.
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
unavailable. There is no **Other gateway options** expander. Page load starts the
existing WSL/Local AI discovery; choosing WSL retains the fresh readiness gate
and destructive-replacement confirmation before Capabilities. Native package
setup independently rechecks capability before configuration.

The gateway-choice scroll viewport owns the 560-DIP maximum width and stretches
its list content. Keep the width constraint on the viewport, not on the nested
ListView, so the choices share the header's center line as the window resizes.
Back, Next, and the step indicator remain outside the scrolling area.

### Local setup progress
Installs and connects a new app-owned `OpenClawGateway` WSL instance from a clean WSL baseline. If the WSL platform is missing or its optional component is not initialized, setup requests administrator approval to install it, re-inspects readiness, and reports when a Windows restart is required. Setup does not export from or mutate an existing user Ubuntu distro; if WSL cannot create the named app-owned distro directly, setup fails with an actionable update message. Cleanup automatically unregisters a distro only when durable OpenClaw evidence is paired with exactly one readable current-user WSL registration whose canonical base path matches the expected managed install path. Automatic orphan-directory cleanup requires a marker bound to that exact path. An unproven same-named distro or leftover data directory is preserved unless the user explicitly confirms its permanent replacement in the setup UI or passes `--confirm-destructive`. When replacing an app-owned local gateway, the removal step is shown as part of progress and can be retried on failure.

The managed distro is locked down and is not intended to be a normal interactive Ubuntu profile. For editing `openclaw.json` as the `openclaw` user and using root for protected-file administration, see [Managing the locked-down WSL gateway](WSL_GATEWAY_ADMIN.md).

### Capabilities and Windows permissions

The Capabilities page applies the selected profile to both setup config and runtime `Node*` settings. Inline Windows permission rows are shown only for capabilities that need OS-level state (camera, microphone, location, screen capture). Notifications are always shown as an app-level permission. Screen capture is passive: Windows asks what to share each capture through the Graphics Capture picker.

### OpenClaw onboard

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

### All set
Displays a completion summary, a Launch at startup toggle, and a Finish button that saves the startup preference before restarting the tray. Launch at startup defaults on so OpenClaw is ready after reboot.

## Security

The onboarding wizard follows these security practices:

- **Input validation**: Setup codes limited to 2KB, decoded JSON validated, gateway URLs checked via `GatewayUrlHelper`
- **URI scheme whitelists**: Only `ms-settings:` for permissions and `http/https` for browser-launch links
- **Token protection**: Query params stripped from all log output
- **Gateway-owned pairing**: Device approval uses the gateway CLI/API path so scope checks, token issuance, audit, and broadcasts stay centralized
- **Error sanitization**: Exception details logged but not shown to users

## Credential Storage

Gateway credentials are registry-backed. Setup codes and QR payloads create or update a `GatewayRecord`; bootstrap credentials live in `GatewayRecord.BootstrapToken`, long-lived manual tokens live in `GatewayRecord.SharedGatewayToken`, and post-pairing device tokens are saved in the per-gateway identity directory. `SettingsManager` may read legacy `Token` / `BootstrapToken` JSON fields for migration, but it does not write them back.

## Localization

All user-visible strings use `LocalizationHelper.GetString()` with the `Onboarding_*` key namespace. Supported languages are discovered from the `Strings/<locale>/Resources.resw` directories; the current locales are English, French, Dutch, Chinese Simplified, and Chinese Traditional.

Translations are AI-generated following the repo convention. Technical terms (Gateway, Token, Node Mode) are kept in English across all locales.

## Developer Guide

See [DEVELOPMENT.md](../DEVELOPMENT.md#developing--testing-the-onboarding-wizard) for build instructions, environment variables, and testing workflow.

### Test Isolation

`SettingsManager` loads `%APPDATA%\OpenClawTray\settings.json` by default. Onboarding tests must not use `new SettingsManager()` without an isolated settings directory, because local user settings such as `EnableNodeMode=true` change setup behavior.

Use a temp settings directory for tests that construct `SettingsManager`, or set `OPENCLAW_TRAY_DATA_DIR` before the test process starts.

### Setup image packaging

Setup images use `ms-appx:///OpenClaw.SetupEngine.UI/Assets/Setup/...` URIs.
Published installer and portable ZIP payloads must include that library-qualified
directory, not just the tray's loose `Assets/Setup` copies. The tray publish target
preserves both layouts; `SetupAssetPublishTests` executes that target against a
clean directory and checks every setup PNG, including nested assets.

### Key Files

| Path | Purpose |
|------|---------|
| `src/OpenClaw.SetupEngine.UI/SetupWindow.xaml(.cs)` | Tray-hosted setup shell, run lock, preview routing, and page navigation |
| `src/OpenClaw.SetupEngine.UI/Pages/SecurityNoticePage.xaml(.cs)` | First-run device-trust warning before setup choices |
| `src/OpenClaw.SetupEngine.UI/Pages/WelcomePage.xaml(.cs)` | Install-new-WSL vs connect-existing choice and existing-gateway replacement prompt |
| `src/OpenClaw.SetupEngine.UI/Pages/AdvancedSetupPage.xaml(.cs)` | Connect-existing handoff to Connection settings |
| `src/OpenClaw.SetupEngine.UI/Pages/CapabilitiesPage.xaml(.cs)` | Capability profile, inline Windows permission status, and install review |
| `src/OpenClaw.SetupEngine.UI/Pages/ProgressPage.xaml(.cs)` | WSL gateway install progress and gateway-installed handoff |
| `src/OpenClaw.SetupEngine.UI/Pages/WizardPage.xaml(.cs)` | OpenClaw onboard provider/model/key wizard driven by gateway `wizard.*` frames |
| `src/OpenClaw.SetupEngine/GatewayWizardRestartRecoveryPolicy.cs` | Exact terminal-restart classification and bounded restart provenance/reconnect retry policy |
| `src/OpenClaw.SetupEngine.UI/Pages/CompletePage.xaml(.cs)` | Success, failure, log/help, and startup preference summary |
| `src/OpenClaw.SetupEngine.UI/Pages/SetupPermissionHelper.cs` | Passive Windows permission checks and inline permission rows |
| `src/OpenClaw.Connection/GatewayRegistry.cs` | Persistent gateway records and migration target |
| `src/OpenClaw.Connection/GatewayConnectionManager.cs` | Operator/node connection lifecycle used by onboarding |
| `src/OpenClaw.Tray.WinUI/Services/SetupExistingGatewayClassifier.cs` | Existing gateway classification for Welcome and startup gating |
