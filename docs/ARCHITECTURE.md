# OpenClaw Windows node - architecture ledger

This document is the **living source of truth** for the architecture refactor
that decomposes the repository's god objects. It is required reading before you
touch any file listed in the ledger below.

Its job is to stop the refactor from silently regressing: when a PR moves a
responsibility out of a god object, it records the move here and (for
high-regression closures) adds a guard test. A later PR that tries to move the
work back then shows up as either a visible ledger edit or a failing test.

See `AGENTS.md` → "Architecture Guardrails" for the hard rules, and the full
multi-PR refactor plan for the reasoning behind each boundary.

## How to use this document

1. **Before editing** a file named in the ledger, read its row(s). Do not add
   back anything a row marks `closed`.
2. **When you extract** a responsibility, in the same PR:
   - Flip/add the ledger row for the new owner to `authoritative`.
   - Mark the vacated responsibility in the old owner as `closed`.
   - Update the "when you touch file X, extract toward Y" guidance below.
   - Add a guard test for the closure when a silent revert would be dangerous.
3. **Prefer behavioral/golden guards.** Use `source-shape` guards only for a
   concrete prohibited pattern (a banned helper signature, a forbidden direct
   constructor call), never for broad architectural wishes, and always with a
   `retirement_condition`.

## Ownership rules

- **View** (XAML + code-behind): layout, named-control wiring, lifecycle event
  forwarding, minimal WinUI-only adapters. No gateway JSON parsing, no polling
  loops, no settings mutation, no imperative row factories.
- **ViewModel / Presenter** (`OpenClaw.Tray.WinUI/ViewModels`, `.../Presentation`):
  observable state, commands, pure projection. WinUI-free where practical - no
  `Microsoft.UI.Xaml`, no `Application.Current`, no `Window`/`Frame`/`Brush`/`Color`,
  no concrete `SettingsManager`. Unit-tested.
- **Service**: IO, gateway calls, registry/settings persistence, timers, process
  execution, WebSocket/MCP hosting. No UI types. No background work started from
  constructors.
- **App** (`App.xaml.cs`): composition root and top-level lifecycle only.
- **Shared mutable domains**: one observable service/store owns each persisted
  domain. View models consume snapshots and field-scoped or compare-and-swap
  mutations; they never own backing files, concrete managers, file observers, or
  parallel mutable caches.

## Single-source owners

These are the canonical homes. Do not reintroduce private copies elsewhere.

The tray icon's primary activation opens or focuses Workspace chat for configured
profiles, including while disconnected. `TrayController` invokes its chat callback and
`App` composes that callback with the `WindowManager` chat route. Explicit
Connection menu actions retain their settings route; opening Workspace does
not bypass chat authorization or pairing requirements.

`StartupSetupState` owns first-run eligibility from saved gateway configuration
or local MCP mode, independently of current connectivity and node pairing.
`WindowManager` applies the same eligibility to Workspace activation, including
forwarded launches and tray clicks: an unconfigured profile opens or refocuses
setup instead of creating Workspace behind it. Explicit companion routes remain
available for advanced connection setup.

Workspace footer text follows the macOS-hosted Control UI at
[`bda22f8`](https://github.com/openclaw/openclaw/blob/bda22f818d967ffa3551b3a729d5bc43863844c7/ui/src/components/app-sidebar-render.ts):
the current user's name, then email, then localized Owner; the second line is
connection status. `WorkspaceIdentitySource` reads `users.self` profile fields
(`displayName`, first `emails` entry), never an agent identity or Windows account.
`sessions.changed` with reason `profile-identity` invalidates the profile through
`GatewayService`/`AppState`; ordinary session updates do not trigger profile RPCs.
The window owns the display source lifetime, and stale results cannot survive
disconnect, replacement, or close. This cache is not used for authorization.

| Concern | Canonical owner | Status |
| --- | --- | --- |
| Test temp directories | `OpenClaw.TestSupport.TempDirectory` | authoritative |
| Test env var save/restore | `OpenClaw.TestSupport.EnvironmentScope` | authoritative |
| CLI stdout/stderr/env capture | `OpenClaw.TestSupport.CliHarness` | authoritative |
| Loopback MCP server for tests | `OpenClaw.TestSupport.FakeMcpServer` | authoritative |
| Authenticated MCP HTTP client for app fixtures | `OpenClaw.TestSupport.McpClient` | authoritative |
| Synthetic Gateway protocol/scenarios | `OpenClaw.TestSupport.Gateway.FixtureGatewayServer` + `GatewayScenario` | authoritative |
| Fixture-backed app profile/process lifetime | `OpenClaw.GatewayFixtureHost.GatewayFixtureProfile` + `GatewayFixtureRun` | authoritative |
| Explicit fixture context and host-effect isolation gate | `OpenClaw.Shared.GatewayFixtureIsolation` | authoritative |
| Passive fixture chat-render acknowledgement | `GatewayFixtureRenderObservation` (pure metadata) + `ReactorChatComposer` (UI applicator) | authoritative |
| External chat session selection without remounting | `MountedReactorChat` forwards to `ChatComposerController`'s existing root selection handoff; `ChatPage` retains initial-mount fallback for an unready or replaced provider | authoritative |
| Workspace agent creation | `AgentCreationDialog` owns inputs and feedback; `AgentCreationService` owns permission checks and response-aware gateway creation | authoritative |
| Workspace owner display identity | `WorkspaceIdentitySource` reads the current operator's `users.self` profile; `WorkspaceWindow` applies name/email/Owner fallback and live connection status | authoritative |
| Shared native command-catalog inputs | `HubCommandCatalog` adapts app state/settings/localization for `HubPageRegistry`; HubWindow and MCP search share it without requiring a companion window | authoritative |
| Companion-only command-catalog input adaptation | Closed in `HubWindow`; delegate to `HubCommandCatalog` so Workspace-first MCP searches use the same inputs | closed |
| Gateway record test data | `OpenClaw.Connection.Tests.GatewayRecordBuilder` | authoritative |
| Settings test data | `OpenClaw.TestSupport.SettingsDataBuilder` | authoritative |
| JSON `JsonElement` coercion (non-nullable fallback family) | `JsonReadHelpers` | authoritative |
| Ollama node command risk taxonomy | `OllamaNodeCommandPolicy` | authoritative |
| WSL/POSIX shell quoting | `WslShellQuoting` | authoritative |
| UI-thread marshaling for presentation code | `IUiDispatcher` | authoritative |
| Page view-model activation/deactivation + disposal lifetime | `NavigationScopeManager` | authoritative |
| Presentation-layer DI composition root | `AppServiceRegistration` (root `ServiceProvider`, owned by `App`) | authoritative |
| Settings snapshot read + field-scoped save + origin-aware change notification | `ISettingsStore` | authoritative |
| Hosted setup settings writes | `SetupSettingsWriter` through `ISettingsStore`; SetupWindow and the pipeline supply only reviewed field patches | authoritative |
| Cooperating JSON persistence coordination | `PersistenceFileLease`; registry expected-snapshot Save and settings loaded-JSON CAS hold it through atomic replacement | authoritative |
| Settings persistence conflict state and visible recovery | `SettingsManager` owns typed CAS rejection; `SettingsPersistenceNotification` owns dispatched, deduplicated restart guidance; App composes/disposes it | authoritative |
| V2 exec-approvals snapshot/CAS persistence + observation | `ExecApprovalsStore` through `IExecApprovalsPresentationStore` | authoritative |
| Settings page load/persist view logic | `SettingsPageViewModel` | authoritative |
| Native tool identity, display arguments, payload extraction, and flattened-history projection | `NativeToolProjector` | authoritative |
| Managed-local listener provenance and strong-credential authorization | `ManagedLocalGatewayPortProvenanceService` | authoritative |
| Native Gateway fixed-product WinGet installation from Microsoft Store and bounded App Installer bootstrap | `NativeGatewayMsixInstaller` | authoritative |
| Trusted Store and existing development Gateway registration identities | `NativeGatewayPackageIdentity` | authoritative |
| Current-user Gateway package registration, health and package-qualified alias discovery | `NativeGatewayPackageResolver` | authoritative |
| Missing-package acquisition, one installation attempt and bounded registration verification | `NativeGatewayPackageAcquisition` | authoritative |
| Shared Windows capability and permission selection, with runtime-specific install review | `CapabilitiesPage` | authoritative |
| Native profile draft creation and canonical state/config launch paths | `NativeGatewaySetupService` + `NativeGatewayPaths` | authoritative |
| Native onboarding capability admission and default/remembered gateway choice policy | `NativeGatewaySetupEligibility`, consuming `MxcAvailability` session probe metadata | authoritative |
| Package-contract runtime selection and lifetime | `NativeGatewayRuntimeRouter` selects `IsolatedGatewayRuntime` or the recognized legacy `NativeGatewayRuntime` | authoritative |
| Isolated package control and listener/process-sequence verification | `NativeGatewayPackageClient`, `IsolatedGatewayRuntime` and `WindowsProcessSequenceSnapshot` | authoritative |
| Retained package-launcher identity, live same-user ancestry and lifetime attribution | `WindowsPackagedProcessAncestry`, anchored by `WindowsNativeGatewayProcessHost` | authoritative |
| Native setup staged-record runtime, reload restoration, config/health and exact-AI gates, publication | `NativeGatewaySetupSession` | authoritative |
| Staged native setup operator connection, pairing, per-handshake/request provenance and bound AI transport | `NativeGatewaySetupConnection` borrowing the runtime from `NativeGatewaySetupSession` | authoritative |
| Setup completion authority across native runtime contracts | `GatewayDashboardBinding` includes the package family and non-null runtime contract; `NativeGatewaySetupSession` rechecks agent configuration without reading a host config for isolated sessions | authoritative |
| Native operator connection construction inside the classic wizard page | `WizardPage.ConnectNativeClientAsync` delegates to `NativeGatewaySetupConnection` | closed |
| Published native AI transport and restart verification | `GatewayAiSetupTransport.BorrowNativeAsync` + `GatewayConnectionManager.RequireNativeSetupClientAsync` + `SetupNativeCompletionVerifier` | authoritative |
| Reviewed native Local AI installation continuation | `LocalAiInstallAndUseIntent` binds single-use consent to Gateway/endpoint/model/port; `LocalAiOnboardingUse` retains mutation outcome and drain ownership. `SetupWindow` transfers the intent after artifact acquisition; `AiSetupPage` applies progress and exact-model verification without provider rediscovery | authoritative |
| Native Local AI explicit running intent | `LocalAiNativeBindingStore` persists automatic-recovery intent separately from ownership; `LlamaServerRuntimeService` serializes explicit Start/Stop, guarded Resume, withdraw-only reconciliation and explicit release under its operation gate. `LocalAiGatewayLifecycle` never converts connection notifications into explicit Start; it drains recovery before shutdown withdraws through the still-authorized manager | authoritative |
| Existing-native Local AI Settings entry | `SetupAccessDraft.SelectExistingNativeGateway` binds the existing record and preserves Settings ownership; `WindowManager` initializes only a newly created setup window, and `SetupWindow` applies the native route without WSL finalization or unrelated settings writes | authoritative |
| Hosted Gateway onboarding RPC and provider/auth/model rendering for WSL and native | `WizardPage` | authoritative |
| Audited optional onboarding defaults shared by native, WSL and headless setup | `WizardOnboardingPolicy` | authoritative |
| Optional-tail cancellation acknowledgement and saved-config/authenticated-health gates | `WizardOptionalSetupHandoff` | authoritative |
| Native terminal TUI onboarding and pre-wizard registry publication | `NativeGatewaySetupHost` / `NativeGatewaySetupService` | closed |
| Native Gateway credential preflight and retry authorization | `NativeGatewayEndpointSecurity` owns the bounded native readiness allowance and sanitized startup-timeout classification; `GatewayConnectionManager` applies that allowance without changing non-native handoff deadlines or authority fences | authoritative |
| HTTP/dashboard/web-chat credential handoff routing and fresh native inspection | `InteractiveGatewayEndpointAuthorizer`, borrowing the manager-owned runtime | authoritative |
| Local AI gateway-record ownership and WSL distro binding | `LocalAiGatewayDistroResolver` | authoritative |
| Local AI provider policy and configuration execution | `LocalAiGatewayProviderCoordinator` owns publication/fallback; `ILocalAiGatewayConfigurationTransport` separates execution, with `WslLocalAiGatewayConfigurationTransport` retaining pinned-distro commands | authoritative |
| Native Local AI admission, publication and reconnect ownership | `NativeLocalAiGatewayTarget` admits the exact authenticated isolated record; `LocalAiGatewayLifecycle` binds the single runtime to that owner, borrows authorized connections and journals guarded RPC changes through `LocalAiNativeBindingStore`; the provider coordinator retains publication/fallback policy | authoritative |
| Native Local AI recovery discovery | `LocalAiGatewayLifecycle.ObserveOwnershipAsync` reads current-profile binding and authenticated configuration without mutation or credential access; `SetupLocalAiHost` supplies evidence to `LocalAiOnboardingSnapshot`. Only a usable same-owner managed choice replaces a detected model; `AiSetupPage` renders guidance and retains the existing explicit Use path | authoritative |
| Native Local AI setup, cancellation and staged handoff | `SetupLocalAiHost` admits explicit Use; `LocalAiOnboardingUse.Expected` supplies the explicit managed-use requirement carried by `GatewayAiSetupCompletion` through fresh verification and restart. `NativeGatewaySetupSession` retains its verification/publication gate; `SetupWindow` wires Local AI reconciliation only for that managed choice. Ordinary detected use never gains runtime/reconciliation requirements from matching receipts; setup cancellation withdraws only its selected route | authoritative |
| Optional managed llama API authentication substrate | `LocalAiApiCredentialStore` protects a stable key with current-user DPAPI; `LlamaServerRuntimeService` supplies `LLAMA_API_KEY` only in the child environment; health/inference clients use Bearer headers and the process host redacts echoed keys | authoritative |
| Native Local AI artifact-only acquisition | `SetupStepFactory.BuildNativeLocalAiAcquisitionSteps` reuses Windows hardware, receipt, runtime and model owners without WSL, inference, provider publication or Gateway restart; caller-owned target admission and explicit Use remain required | authoritative |
| Local AI model cache acquisition, explicit legacy migration, and active-path receipt selection | `HuggingFaceModelInstaller` + `LocalAiManifestStore` + `LocalAiInstallReconciler` | authoritative |
| Exact Gateway wizard terminal-restart compatibility and bounded retry policy | `GatewayWizardRestartRecoveryPolicy` | authoritative |
| Interactive onboarding routes and installation-step selection | `OnboardingFlowPolicy` | authoritative |
| Setup-lifetime capability/profile/explicit Custom intent/consent draft and ordered installation requirements | `SetupAccessDraft` + `SetupCapabilityProfiles`, owned by `SetupWindow` | authoritative |
| Setup provider artwork resolution and bounded page-owned download lifetime | `GatewayAiSetupPresentation` + `ProviderArtworkSession` + `ProviderArtworkLoader`, rendered by `ProviderArtwork` | authoritative |
| Native setup editor mounting and committed-result routing | `SetupWindow` through `ISetupNativeConnectionHost` + `SetupNativeConnectionPage` | authoritative |
| Setup-only Windows privacy preview and probing | Retired; current Permissions settings and runtime consent retain their existing owners | closed |
| Setup Local AI and Tailscale control lifetimes | `LocalAiSetupControl` in `GatewaySetupDetailPage`; `TailscaleSetupControl` inline in `GatewaySetupPage` with a compatibility detail route | authoritative |
| Per-setup-window CUDA probe reuse and incomplete/faulted-result refresh | `LocalAiHardwareProbeCache`; `SetupWindow` composes it for Welcome and `LocalAiSetupControl` | authoritative |
| Temporary setup operator connection, captured identity and endpoint provenance | `SetupGatewaySession` + `SetupGatewaySessionBinding` | authoritative |
| Expected AI restart admission during missing live handshake | `GatewayAiSetupController` bounds the wait; transports use the existing binding owner to validate persisted authority, then the client requires a fresh exact authenticated route | authoritative |
| Native setup verify-only connection and isolated validation identity/tunnel | `GatewayConnectionValidator` + `GatewayValidationIdentity` | authoritative |
| Native setup connection input and host transaction adapter | `SetupNativeConnectionInputResolver` + `SetupNativeConnectionHost` through `GatewayDirectConnectService` | authoritative |
| Committed Existing/Remote Gateway through capability review and AI admission | `GatewayDirectConnectService` captures endpoint binding; `SetupAccessDraft` retains it; `SetupGatewaySession` / borrowed native transport reject drift before credentials or RPC | authoritative |
| Focused AI setup protocol state and provider progress polling | `GatewayAiSetupClient` + `GatewayAiSetupController` | authoritative |
| Verified AI completion intent and opaque pending restart handoff | `GatewayAiSetupClient` + `GatewayAiSetupCompletion` + `SetupDashboardHandoffStore` + `SetupDashboardHandoff` | authoritative |
| Native verified destination choice and selection-time read-only verification | `SetupNativeCompletionCoordinator` + `SetupNativeCompletionVerifier`; `AiReadyPage` renders, `SetupWindow` composes finalization | authoritative |
| Native pending launch and bound Chat/Channels/Skills entry | `SetupNativeHandoffLauncher` + `SetupNativeNavigationRequest`; `SetupNativeSkills` owns response-bound read-only skills loading; `WindowManager` and pages apply the selected route | authoritative |
| Verified native setup Chat window boundary | `SetupNativeNavigationRequest` projects the exact Workspace session; `WindowManager` awaits `WorkspaceWindow` and its retained `ChatPage` before activation and receipt consumption. Channels/Skills remain typed companion routes | authoritative |
| Verified setup Chat hosting in the Settings companion | Closed in `HubWindow`; typed native Chat requests must use Workspace without dropping endpoint, identity, agent or session verification | closed |
| Settings Chat rail action | `HubWindow` forwards a non-selecting item invocation through `WorkspaceNavigation` to the existing Workspace; Settings never mounts Chat | authoritative |
| Gateway dashboard management card | `ConnectionPage` owns the visible card and forwards to the existing `GatewayDashboardLauncher` path; `ChatPage` has no management banner | authoritative |
| In-flight native chat navigation identity | `SetupNativeChatBinding` holds the exact request reference; `WorkspaceWindow` invalidates it at admitted agent/session navigation intent, before asynchronous creation. `ChatPage` checks identity and cancellation on ready and waiting paths; `SetupNativeHandoffLauncher` fences receipt consumption with the linked timeout | authoritative |
| Native receipt acquisition classification and restart recovery settlement | `SetupDashboardHandoffStore` distinguishes acquired/busy/invalid/unavailable; `SetupNativeHandoffLauncher` retains recovery on busy/unavailable and clears it only after consumption or definitive rejection | authoritative |
| Native completion startup readiness | `SetupNativeCompletionTiming` defines finite phase budgets; `SetupNativeCompletionVerifier` enforces both borrows, the existing Local AI recovery join and exact-model proof; `SetupDashboardHandoffStore` keeps five-minute unused admission and persists one non-renewable execution start/deadline under its exclusive lease; `SetupNativeHandoffLauncher` enforces navigation and total execution deadlines and does not redisplay settled retry failures on automatic activation | authoritative |
| Pre-acquisition restart recovery deletion | `App.OpenNativeSetupCompletion`; deletion is delegated to the receipt outcome owner | closed |
| Setup startup availability | `WindowManager` supplies app identity availability; `SetupWindow` gates presentation and persisted preference | authoritative |
| Setup registration outcome and fallback admission | `WindowsStartupTaskRegistration` classifies completed numeric HRESULT; `SetupStartupPolicy` still requires strict task absence for rejected-operation Run-key fallback | authoritative |
| Pipeline failure plus failed registry settlement | `SetupPipeline.RunWithSettlementAsync` and `SetupPipelineSettlementException` retain both outcomes; `ProgressPage` renders/logs them without declaring reconciliation success | authoritative |
| Setup HWND sizing and DPI-aware minimum | `SetupWindow` applies `OverlappedPresenter` constraints; `SetupWindowSizing` projects DIP dimensions to physical pixels | authoritative |
| Verified setup authority across fresh clients | `OpenClawGatewayClient.AuthenticatedSigningDeviceId` + `SetupCompletionAuthority` + `SetupGatewaySessionBinding`; accepted signing identity and exact session survive completion, disk reads only detect drift | authoritative |
| Native startup versus ordinary update prompt | `ActivationRouter.CheckOrdinaryStartupUpdateAsync`; App retains startup composition and receipt dispatch | authoritative |
| Credential-recovery transport admission | `GatewayCredentialRecoveryPolicy`; normal connection recovery and disposable native validation retain their endpoint-provenance checks | authoritative |
| User-requested Dashboard launch and visible retry | `GatewayDashboardLauncher`; dialog lifetime remains in `WindowManager`; no setup receipt or intent | authoritative |
| Experimental browser setup-completion handoff | Removed; all completion activation goes through the native receipt owner, including visible rejection of obsolete handles | closed |
| Explicit AI preparation continuation, fresh auth-URL admission and bounded restart wait | `GatewayAiSetupController` | authoritative |
| Continuous AI provider dialog visibility and exact row-command admission | `AiSetupPage` | authoritative |
| Focused AI discovery display grouping | `AiSetupPresentationModel` | authoritative |
| Setup installation three-phase overview and exact step-count projection | `SetupInstallationProgress`; `SetupPhaseStatus` renders native status icons/text; `ProgressPage` retains logs and real download progress | authoritative |
| Onboarding Local AI readiness, fresh-unsupported visibility projection and cancellable read-only observation | `LocalAiOnboardingSnapshot` + `LocalAiOnboardingObservation` | authoritative |
| Same-window Local AI admission and runtime action bridge | `ISetupLocalAiHost` + `SetupLocalAiHost`; route inspection shared with Settings through `LocalAiSetupRouteResolver` | authoritative |
| Explicit Local AI mutation drain and retained Gateway/model verification binding | `LocalAiOnboardingUse`; `SetupWindow` retains the setup lock through its drain | authoritative |
| Focused provider prompt controls and input clearing | `ProviderSetupDialog`, owned by `AiSetupPage` | authoritative |
| Inline provider wizard rendering in `AiSetupPage` | `ProviderSetupDialog` replaces the inline WizardPanel; page retains request/lifetime ownership | closed |
| AI provider list selection plus a duplicate page-footer Continue | Explicit native row command or inline API Connect, bound to the exact choice | closed |
| Managed-local automatic repair eligibility and orchestration | `ManagedLocalGatewayAutoRepairMonitor` + `ManagedLocalGatewayRepairCoordinator` | authoritative |
| Permissions page state, settings commands, and exec-approvals presentation | `PermissionsPageViewModel` | authoritative |
| Permissions runtime status projection | `PermissionsPageRuntimeSource` | authoritative |
| Hub navigation tags, page mapping, command catalog/search, and gateway-page classification | `HubPageRegistry` | authoritative |
| Workspace Home/Notifications and exact session-key identity, deprecated-link fallback, back/forward history, and companion boundaries | `WorkspaceNavigation` + `WorkspaceNavigationHistory`; `WorkspaceWindow` restores the selected agent/session on the retained chat host; `WindowManager` routes pending session links directly without an intermediate Home entry | authoritative |
| Workspace-versus-companion dispatch and rejection of unknown prefixed routes before window side effects | `WorkspaceNavigation.Dispatch`; `WindowManager` and `HubWindow` supply native window actions; `AppCapability` propagates navigation error payloads as tool errors | authoritative |
| Unvalidated Workspace-prefix forwarding and companion fallback | Closed in `HubWindow.NavigateTo` and `WindowManager.ShowHub`; delegate boundary dispatch to `WorkspaceNavigation` | closed |
| Workspace agent/session identity, background-session filtering, and explicit assistant-selection readiness for conversation creation | `WorkspaceProjection`; `WorkspaceWindow` applies readiness to Sessions + and guards the mutation | authoritative |
| Canonical background-session classification for Workspace sidebar and latest agent session | `SessionDisplayResolver.IsBackground`, consumed by `WorkspaceProjection`; nullable gateway flags must not bypass classification/key fallback | authoritative |
| Native Workspace pane visibility, non-overlapping reopen row, and toggle focus handoff | `WorkspaceWindow` | authoritative |
| Speculative Workspace management cards and responsive grids | removed with Home/Sessions-only navigation | closed |
| Foreground Workspace and separate Settings companion lifetime | `WindowManager` | authoritative |
| Chat-visible notification suppression | `ChatVisibilityPolicy` owns the pure visibility decision; `WorkspaceWindow` supplies current destination, AppWindow visibility and minimized state; `WindowManager.IsChatVisible` includes compact chat | authoritative |
| Inferring chat visibility from any existing main window | Closed in `App.ShouldShowNotification`; use `IWindowManager.IsChatVisible`, retaining chat/per-type notification toggles | closed |
| Readiness-gated, single-use native chat voice launch | `PendingVoiceActivation` | authoritative |
| Hub notification banner severity and action projection | `AppNotificationInfoBarPresenter` | authoritative |
| Compact notification list reconciliation and dismissal | `NotificationFlyoutContent` | authoritative |
| Gateway/operator/node status flyout controls | `GatewayStatusContent` | authoritative |
| Tray-menu semantic composition and connection-toggle state | `TrayMenuPresenter` + `ConnectionTogglePresenter` | authoritative |
| App-owned non-tray window creation, reuse, focus, theme, and lifetime | `IWindowManager` + `WindowManager` | authoritative |
| Tray icon, popup coordination, live status, and callback lifetime | `ITrayController` + `TrayController` | authoritative |
| Deep-link/protocol/toast/forwarded activation normalization, current-user IPC, and semantic activation plans | `ActivationRouter` | authoritative |
| Packaged activation-kind preservation | `App.GetLaunchActivation` adapts Windows AppLifecycle metadata into `LaunchActivationInput`; `ActivationRouter` shares candidate selection for initial/secondary launches and reserves implicit foreground navigation for interactive Launch | authoritative |
| Post-save settings change effect ordering, detached snapshot comparison, and concurrent save serialization | `SettingsChangeCoordinator` | authoritative |
| Exactly-once ordered app shutdown sequencing | `AppShutdownCoordinator` | authoritative |
| App composition-root startup sequencing | `AppBootstrapper` (planned) | planned |
| Inno migration records, preparation, and pre-start completion guard | `MigrationRecordCodec`, `MigrationPreparation`, `MigrationInventory`, `InnoMigrationStartupGuard` | authoritative |
| Cross-session migration exclusion and source activity inspection | `MigrationOperationLock`, `InnoSourceActivityVerifier` (App only retains the runtime handle through process exit) | authoritative |
| Store migration startup admission and finalization | `InnoInstallationDetector`, `InnoSourceRemovalVerifier`, `MigrationStartupRecordReader`, `MigrationInventoryCapture`, `StoreMigrationStartupCoordinator`, `StoreMigrationFinalizationCoordinator`, `StoreMigrationStartupGuard` | authoritative |
| Windows node connection generation, cancellation, start ordering, recovery, events, and telemetry | `NodeConnectionCoordinator` | authoritative |
| Bootstrap/shared/device credential handoff, durable clear gate, and operator token recovery timing | `BootstrapTokenLifecycle` | authoritative |
| Device role-upgrade approval, confirmation, and bounded node reconnect queue | `DevicePairApprovalCoordinator` | authoritative |
| Gateway wire protocol range, minimal `hello-ok` validation, and sanitized compatibility state | `GatewayProtocolContract` + `GatewayProtocolCompatibility` | authoritative |
| Capability UI metadata | `NodeCapabilityUiCatalog` (planned) | planned |
| Capability registration/gating | `NodeCapabilityRegistrationPolicy` (planned) | planned |
| Local MCP exposure policy | `McpCapabilityPolicy` (planned) | planned |
| Gateway connect envelope | `ConnectEnvelopeBuilder` | authoritative |
| Gateway request tracking | `PendingRequestRegistry` | authoritative |
| Chat atomic runtime transaction lock and cross-domain commits | `ChatConversationState` | authoritative |
| Chat queue collections, echo correlation, drain and retry commit mechanics | `ChatQueueState` under the `ChatConversationState` lock | authoritative |
| Chat reset generations, gates, echoes and backfill state | `ChatResetState` under the `ChatConversationState` lock | authoritative |
| Chat history identity, revisions and connection-generation tokens | `ChatHistoryState` under the `ChatConversationState` lock | authoritative |
| Chat sessions, models, catalog and snapshot projection inputs | `ChatPresentationState` under the `ChatConversationState` lock | authoritative |
| Chat run, abort and terminal lifecycle state | `ChatLifecycleState` under the `ChatConversationState` lock | authoritative |
| Chat approval identity correlation and dedupe state | `ChatApprovalState` under the `ChatConversationState` lock | authoritative |
| Chat send admission/retry decision policy | `ChatSendQueuePolicy` with atomic commits coordinated by `ChatConversationState` | authoritative |
| Chat history request/retry/rebuild mechanics | `ChatHistoryLoader` with token acceptance coordinated by `ChatConversationState` | authoritative |
| Gateway agent event to chat event mapping | `ChatEventMapper` | authoritative |
| Chat snapshot projection | `ChatSnapshotProjector` | authoritative |
| Tool and attachment metadata cache lifecycle | `ChatMetadataStore` | authoritative |
| Aborted IDs and last-chat-state persistence | `ChatStatePersistence` | authoritative |
| Sensitive instant-capture ordering | `SensitiveCaptureExecutor` + `SensitiveCapturePlans` | authoritative |

## Native Gateway MSIX lifecycle and shared-wizard handoffs

Windows deploys the MSIX. The current Gateway package owns its isolated session
and Gateway service; Companion owns setup, connection and credential handoff;
upstream OpenClaw supplies onboarding over RPC. The known legacy proof package
instead uses Companion's same-user process supervisor. Package installation,
a listening port, and successful onboarding are not
interchangeable readiness signals. `App` remains the composition root; do not
move package resolution, process inspection or setup finalization back into it.

### Package installation and discovery

Isolated runtime inspection and lifecycle ownership are separate. Verification
is cached per record and is always checked against fresh listener snapshots;
probing another package cannot transfer stop ownership. Only starts issued by
Companion are stopped on detach, and failed authorization rolls back only a
start issued by that same call. Explicit wizard restart uses a separate runtime
operation and preserves a pre-existing service's leave-running policy.
Ownership inspection, including the fresh check before credential handoff, has
a five-second deadline. Package inspection failures, including WinRT deployment
errors and an explicit unknown service state, deny handoff as unavailable
inspection rather than claiming a conflicting listener. Authentication recovery
classifies these unavailable probes as network failures.

`NativeGatewayMsixInstaller.InstallAsync` invokes the signed-in user's App Installer
alias (`%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe`) through the existing
`CommandRunner`, without a shell or elevation. The fixed command is
`install --id 9NV70LV3D6XC --source msstore --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --no-upgrade`.
The native review explains that selecting **Set up gateway** authorizes installation
and accepts the package and Store source agreements. Microsoft Store still owns
architecture/package selection, signature validation and deployment. There is no
automatic Store-page fallback: missing WinGet, policy/source failures and nonzero
exit codes produce explicit retry/repair guidance with sanitized, bounded output.
Command success is not package readiness. There is no local source path,
environment override, direct download, certificate-trust change, or ARM64-only gate.

`NativeGatewayPackageResolver.ResolveAsync` subsequently requires exactly one
matching current-user package registration, verifies package health, and resolves
its package-qualified `openclaw.exe` and `clawctl.exe` execution aliases. Never
substitute a generic PATH/npm command, copied executable or guessed WindowsApps
installation path. `NativeGatewayPackageClient` probes `clawctl status --json`
for `integration.kind: "isolated-session"` and version `1`. An unversioned
response that already describes a session is unsupported, not a legacy
fallback. Only the known `0.0.0.0` and `0.0.0.1` proof packages retain the
same-user path.
`NativeGatewaySetupHost` invokes `clawctl setup --json` and requires a ready
isolated session before configuring it; that command does not perform
Gateway onboarding.

`NativeGatewayPackageIdentity` accepts the Store manifest's exact pair:
`OpenClawFoundation.OpenClawGateway` and
`CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC`
([packaging manifest](https://github.com/openclaw/openclaw-windows-packaging/blob/96770f14d73edfcba41964c08cd2f64f39420278/src/OpenClaw.Launcher/Package.appxmanifest)).
The original `OpenClaw.Gateway` / OpenClaw Foundation development publisher pair
remains accepted for already installed packages and saved profiles. Names and
publishers cannot be mixed. New setup with both identities installed produces an
explicit duplicate-registration error, not an implicit migration or preferred-package
fallback. The runtime resolver selects the saved profile's exact family, so an
existing Gateway remains usable when both packages are installed. Runtime records
remain pinned to their saved package family; Store
installation does not rewrite a development profile's identity. Package-family
syntax checks admit both names, while registration and exact family matching
remain mandatory before launching. An existing same-user record is not silently
migrated to a newly installed isolated package; it requires new setup.

`scripts\NativeGatewaySourceBuild.psm1` owns the source-build safety boundary:
machine-wide build/unregister serialization, protected creation of work directories,
read-only validation of existing tree and ancestor ACLs, and prebuilt metadata/hash
validation before cache selection. Existing unsafe permissions are rejected, not
silently repaired. This protects loose-package code without changing Store resolution.

Developers opt in to a source-built Gateway with
`OPENCLAW_NATIVE_GATEWAY_DEV_PATCH=<patch>`. `scripts\Build-NativeGatewayFromSource.ps1`
builds an `openclaw/openclaw` ref and registers it with the packaging repo's
`Deploy-LocalPackage.ps1 -Patch` as the side-by-side loose registration
`OpenClawFoundation.OpenClawGateway-<patch>` under the Store publisher, with
package-qualified aliases `openclaw-<patch>.exe` and `clawctl-<patch>.exe`. While the
variable is set, new setup selects only that patched package
(`NativeGatewayPackageIdentity.IsSelectable`) and a missing patch is an explicit error,
never `NativeGatewayPackageNotInstalledException`, so WinGet never runs. Saved profiles
bound to a patched family resolve only while the variable names that patch
(`IsResolvable`); otherwise the resolver reports which value to set. With the variable
unset, selection, resolution and aliases are identical to the Store path.

After native capability/permission review, `NativeGatewaySetupPage` starts
automatically. It rechecks device support, then calls
`NativeGatewayPackageAcquisition.EnsureAsync`. Only the typed
`NativeGatewayPackageNotInstalledException` starts WinGet installation, once per attempt.
Healthy registration skips installation; duplicate registration, unhealthy packages
and missing aliases fail explicitly instead of triggering reinstall loops.
The cancellable acquisition deadline is five minutes including installation and
registration verification, with one-second polling after WinGet completes.
Cancellation reaches `CommandRunner`, which stops its WinGet process tree;
Windows may still finish an already submitted deployment. No installed package
is removed, and retry starts by resolving registration again.

The page uses the shared `OnboardingMascot`, native `SettingsCard` rows with
`SetupPhaseStatus`, and `SetupProgressIndicator` for support, package readiness,
profile preparation and verified runtime startup. Completed rows get checkmarks.
Its Install stage leads to the same focused AI and Ready stages as WSL, with six
native stages (WSL has an additional installation review). Progress sits above wrapping navigation actions on
both pages so narrow windows do not overlay buttons on the indicator.
It automatically transfers the staged session to `AiSetupPage`; no separate
Install, Check again or Open Gateway setup actions remain. Retry is error/cancel
recovery only. Preview never installs or starts a Gateway.

Native and WSL use the same `CapabilitiesPage` profiles and toggles. Native
installation consent stays on that page, without a separate review or
WSL/Local AI/Tailscale probes. Cancelling returns to capabilities without
automatically restarting a runtime.
On finalization, the session applies selected command IDs to the Gateway's
`gateway.nodes.commands.allow` through the upstream CLI before config/health
verification. The isolated path applies this inside the agent account, not
under Companion's Windows profile; legacy profiles retain the local writer.
Focused completion explicitly restarts through the selected runtime owner and
reverifies the exact model before publication. The isolated runtime preserves
whether a pre-existing service should be left running on detach. Focused setup
drains its operator before finalization and does not call isolated Stop before
Restart, which would otherwise discard stop ownership for a setup-started service.
The shared setup settings owner then saves the reviewed capability, transport
and startup choices without overwriting unrelated settings. Settings write
failure stays retryable before releasing the session. Completion shows the
verified model and three destinations, not a running or paired Windows node.
The normal connection owner still performs node connection/pairing; Windows
permission and exec-approval gates are unchanged.

Native package setup uses the shared `AiSetupPage` and `AiReadyPage` flow.
Only an explicit unsupported-method result offers `WizardPage` compatibility
setup. Authentication failures, timeouts and uncertain writes do not enter that
fallback. Healthy native and WSL AI/Ready screens use the same presentation:
providers, verified model and the three destination choices, without a native-only
summary. Gateway and permission details remain in Connection and Permissions.
Native recovery actions and output appear only for actionable errors or uncertain
outcomes on the AI page/provider dialog, and hide again during normal progress or
after recovery. Their native ownership and cancellation guards are unchanged.

### Capability recommendation and Windows Update

The 2026-09-18 onboarding decision removed the separate "not isolated"
warning/checkbox. General security consent and exact-identity pairing remain.
Capability eligibility alone does not establish the Gateway's runtime account:
the package-qualified versioned integration check selects the isolated
package path, while the known legacy proof remains same-user.

On Welcome, `NativeGatewaySetupEligibility` consumes the actual
`wxc-exec --probe` result `probes.isolationSessionAvailable` exposed by
`MxcAvailability.IsolationSessionCapability`. It must not infer session capability
from `IsolationProxy.exe`, a process-containment tier or a Windows build alone.
Windows Server/unknown SKU suppression remains in the shared probe. Missing or
invalid session metadata does not invalidate a usable process sandbox, but it
cannot authorize native onboarding.

A positive result enables and initially selects the first **Install a local native
gateway** card with the accent highlight and **Recommended** badge. A negative
capability result leaves that recommended choice visible but disabled after WSL and
**Connect to an existing gateway**, with Windows Update guidance directly below the
choice list; reopening the page rechecks support. The pinned SDK documents
Insider build **26340.9212** as its baseline. This is update guidance, not a
hardcoded admission floor or a promise that a particular feature is enabled.
The native boolean cannot distinguish every OS API failure from missing support.
Missing executables, malformed results and probe errors instead offer retry or
Companion repair, not an assertion that Windows must be updated.

The Welcome page presents WSL, **Connect to an existing gateway**, then disabled native
Gateway while support is being checked or unavailable. A positive result moves native
to the first position, followed by WSL and the existing-Gateway choice. WSL is visible
and selectable during the native probe and for every probe outcome, without an expander.
There is no Welcome-page **Check again** button.
WSL/Local AI discovery starts on page load; fresh WSL readiness and
destructive-replacement confirmation still run before its capabilities page.
WSL is never selected implicitly after a failed native probe. A late probe
result preserves explicit WSL and existing-gateway selections, including choices
restored on Back navigation. Re-entering the
page rechecks support, stale results cannot mutate an unloaded page, and native
package setup rechecks capability before preparing a profile.
`ms-settings:windowsupdate` only opens Settings; Companion does not enroll the
device in an Insider channel or change Windows feature flags.

### Draft and configuration ownership

`NativeGatewaySetupService` creates or resumes a credential-free draft descriptor
containing a Gateway ID, preferred loopback port, package family, and runtime
contract. The draft is not yet a published `GatewayRegistry` record. Companion
keeps its device identity under its own data directory, but an isolated package
keeps OpenClaw configuration, credentials and workspace under the agent account.

For an isolated package, `clawctl companion prepare --port <preferred> --json`
reads the agent's default `openclaw.json` and invokes upstream
`openclaw config patch` inside the recorded session. It preserves an existing
local port and token plus unrelated settings and rejects incompatible mode,
bind and authentication settings. Companion records the returned effective
port and token; it does not create a host-side `openclaw.json`, forward its
profile paths or install its own Gateway supervisor. Package
`gateway-service start/status/stop` owns the service lifetime. A cancelled
draft can resume without replacing the agent's credential. Finalization
checks that the returned port and token still match before publishing.

If an unpublished same-user draft survives an upgrade to an isolated package,
Companion shows an explicit replacement choice. Discarding removes only the
Companion draft descriptor, then starts isolated setup with a new identity.
Existing configuration, credentials and workspace files remain untouched, even
if a previously published connection was removed from the registry. Like
`StoreMigrationRecoveryDiscard`, native recovery discards intent, not user data;
the Inno-specific receipts and installation checks are not shared with native setup.
It never reuses host configuration as agent configuration. A published
same-user profile remains blocked and directs the user through Connections to
remove it and create a new isolated profile. It never adopts a foreign listener
or session, and it does not modify WSL or other package paths.

The following same-user profile and port-rotation path applies only to the
recognized legacy proof package. It creates a separate profile at
`<Companion data>\gateways\<gateway-id>\native-gateway`, with its own
`openclaw.json`, generated authentication token and workspace.

Retry re-reads the draft instead of keeping a stale in-memory port. If an
unpublished draft's port is occupied, `NativeGatewaySetupService` selects another
loopback port and updates only the port in the descriptor and configuration.
Gateway ID, identity, authentication token and provider settings are preserved.
A durable `PreviousPort` intent in the descriptor allows either interrupted
write to finish on the next attempt. Published records are never rotated by
this recovery. Runtime ownership checks still reject listeners that race startup;
no conflicting process is adopted or terminated.

For that legacy path, `NativeGatewayPaths` supplies explicit `OPENCLAW_STATE_DIR` and
`OPENCLAW_CONFIG_PATH` for package commands, rather than using the user's default
Gateway profile. Launch paths are mapped through `ResolveDataPath` to physical
locations because Companion and Gateway can have different MSIX filesystem
views; canonical registry paths are unchanged. External-supervisor/service-repair
flags and disabled automatic updates preserve Companion's lifecycle ownership.

`NativeGatewaySetupSession` owns cancellation, pairing and final publication
gates for both paths. Only legacy setup backs up and suspends the reload setting
in its host-owned profile; the isolated path leaves the agent's existing
reload setting intact. Stopping either runtime does not delete its configuration.

### Isolated listener proof and legacy process ownership

After a successful package start acknowledgement, `IsolatedGatewayRuntime`
allows up to three minutes, including the start command, for its own start to
become ready. A late successful acknowledgement retains ten seconds for status
confirmation, extending the start/status ceiling to at most 190 seconds. This
allowance remains subordinate to caller deadlines; the outer 210-second connection
budget also includes preflight, inspection and handshake work. It polls status
every two seconds without issuing another start.
`starting` or `unhealthy` with no listener is only a pending observation, never
credential authority. A listener racing the pending observation permits one fresh
status recheck, but only `running` plus full port/process attribution can pass.
Unknown/terminal states, persistently unattributed listeners and failed
ownership checks fail closed. Cancellation/deadline failure retains the existing
owned-start rollback rules; pre-existing pending services are not adopted.
See [native startup contract limitations](ONBOARDING_WIZARD.md#native-startup-and-completion-deadlines)
for the older package's ambiguous failed-start response, which is not admitted.
The pending-start fixtures are conditional client-contract tests, not proof that
the installed package can acknowledge a pending launch.

`IsolatedGatewayRuntime` accepts a running Gateway only after a fresh
package-qualified `clawctl gateway-service status --json` attributes its
listener process IDs, creation times, and OS sequence numbers to the
recorded isolated session and agent SID. Companion compares that
attribution to two complete IPv4/IPv6 loopback snapshots and
`SystemBasicProcessInformation` process-sequence snapshots. It does not
open the isolated agent's process handle: Windows denies that cross-account
query. Windows 11 build 26100.4770 or newer is required for that
process-sequence API; unsupported builds fail closed with update guidance.
A port alone, a stale process ID, an unrelated listener, or a
replaced listener is denied. Its
synchronous browser credential callback uses the last attributed identity
plus fresh OS snapshots; it never starts a Gateway on the UI thread.

The process-job and ancestry checks below describe only the legacy same-user
runtime. They are not an MXC session ownership proof.

The installed package's launcher owns a separate kill-on-close job for Node.
Observed ownership with the development package was:

```text
Companion-owned Windows lifecycle job
  Packaged Gateway launcher
    Package-owned Windows lifecycle job
      Node process hosting the Gateway TCP listener
```

The original requirement that the TCP listener itself belong to Companion's job
rejected this valid arrangement: the launcher was in Companion's job, but Node
was not. A longer timeout, direct executable launch or an open loopback port
could not establish ownership. The exact Windows job-inheritance mechanism behind
that separation was not established.

`WindowsNativeGatewayProcessHost` now creates the package launcher suspended,
assigns it to Companion's kill-on-close job, retains its process handle, then
resumes it. Assignment/resume failures terminate the created process. Atomic
job-list creation had failed while the package was already active; suspended
creation and assignment worked with the existing Gateway still running.

`NativeGatewayRuntime` accepts the listener through direct job membership or
`WindowsPackagedProcessAncestry`: a live same-user descendant chain anchored to
that exact, still-job-owned launcher with the expected package family. Retained
process handles, parent/child creation ordering and bounded ancestry checks
prevent cached PIDs or executable names from being treated as ownership proof.
The runtime also requires complete IPv4/IPv6 inspection, loopback-only listeners,
the requested address, matching process creation times and two consistent TCP
snapshots. Unknown or replaced listeners are rejected, not adopted.

No new cross-package process-handle protocol was added to the packaging repo.
An explicit authenticated handle-handoff contract was considered; the implemented
solution uses local retained-handle attribution with the existing package.
Companion owns the launcher lifetime, while the launcher owns Node cleanup.
`NativeGatewayEndpointSecurity` and setup authorization use this verification
before credential-bearing connections, including reconnects.

`InteractiveGatewayEndpointAuthorizer` routes dashboard and web-chat HTTP
credential handoffs to the same runtime's fresh, double-snapshot inspection.
Its synchronous UI callback never starts a Gateway or waits for a busy lifecycle
gate; busy, stopped, disposed or replaced workloads fail closed. It does not
trust cached proof or connected status. App only composes this non-owning adapter;
process inspection stays in `NativeGatewayRuntime`. Non-native handoffs retain
`ManagedLocalGatewayPortProvenanceService` authorization. Typed native listener
conflicts retain `LocalPortConflict` classification instead of becoming generic
network failures.

The legacy path is same-user supervision, not an MXC sandbox or a security boundary against
malicious same-user code. There is a narrow crash window between suspended
creation and job assignment that can leave a suspended launcher; kill-on-close
cleanup applies after assignment, and no launcher code has resumed before then.

### RPC handoff to the existing wizard

This section describes the explicit compatibility path only. The normal native
path uses the same detected providers, activation, exact-model verification and
Chat/Channels/Skills chooser as WSL. `NativeGatewaySetupConnection` owns the
temporary operator socket in both paths; it never owns or replaces the native
runtime. `SetupWindow` retains `NativeGatewaySetupSession` across AI/Ready navigation.

The compatibility page retains the native wrapper with its exact client binding.
All wizard RPCs, including progress, cancellation and the optional-policy
config/health checks, pass through the generation-fenced request helper and native
per-request authorization. Replaced bindings cannot send through a newer client.
Page teardown cancels/drains requests and disposes the captured socket wrapper;
only the window releases the native session/runtime after page cleanup.

`NativeGatewaySetupPage` prepares the session and passes it to the shared
`WizardPage`. The page owns RPC/rendering; the session owns the profile/runtime.
There is no second provider UI or normal terminal-based onboarding path.

1. Validate initial configuration, start the package-owned service or legacy
   runtime as appropriate, and verify its listener before sending credentials.
2. Connect using the per-Gateway identity and prefer its stored device token.
   When pairing is required, `ApproveWizardPairingAsync` matches the handshake
   request ID against that identity's device ID and public key, rechecks endpoint
   ownership/configuration, and approves only the exact request via the package
   CLI. Never approve the latest unrelated request or bypass onboarding consent.
   The package omits listener ownership details if Windows cannot supply
   process-sequence evidence. Gateway health remains independent, but Companion
   refuses credential handoff until running on Windows 11 build 26100.4770 or
   later. Re-running `clawctl setup` cannot add this OS capability.
3. For the legacy profile, clear inherited URL overrides, pin the port and
   supply the token in the child environment, not argv. The isolated path
   uses package-qualified `openclaw devices list` and
   `openclaw devices approve <request-id>`. These commands run in the agent
   context and use its Gateway config; Companion checks that its saved port
   and token still match the agent config before each command. It does not pass
   the token or host profile paths to the CLI. The CLI budget includes slow
   package startup. Dispose the failed-handshake client
   before one bounded retry; disconnect alone does not stop its reconnect loop.
4. Call `wizard.start` with `mode=local` and `installDaemon=false`; render upstream
   steps and submit answers through `wizard.next`. `wizard.cancel` ends the
   session. Companion does not install a competing Gateway service.

`WizardOnboardingPolicy` supplies the same audited optional skip/keep answers to
native, WSL and headless onboarding. Consent, provider/auth/model choices,
permissions, unknown prompts and errors are not silently answered.
For isolated setup, `WizardConsoleTail` uses the authenticated operator's
upstream `logs.tail` RPC to project only root-logger `console.log` entries
after listener verification. The initial cursor is captured before
`wizard.start`, and later polls are byte/line bounded and already redacted
by OpenClaw. A gap or failure surfaces recovery-terminal guidance rather
than silently discarding an OAuth prompt. No package log-file path or host
profile is passed to the agent; legacy profile and WSL tail modes remain
separate.

### Finalization and transfer to normal connection management

The focused path admits only `GatewayAiSetupCompletion` bound to the same
Gateway endpoint/package family, persisted signing identity, agent, canonical
session and verified primary model. Showing the chooser publishes nothing.
Each destination freshly verifies through the staged native owner, then
`CompleteVerifiedAsync` stops the setup runtime, restores reload, applies selected
capabilities, validates configuration and health, and performs another exact-model
verification on the restarted runtime before publishing the record. It does not
set the classic wizard-completed flag. Publication reloads unrelated Gateway edits
and uses an expected registry snapshot; an already occupied draft ID is rejected.

After Companion restarts, verification borrows the normal connection manager's
native-authorized operator connection. It rechecks provenance on every request
and never starts a parallel native runtime or disposes that borrowed connection.
The generic `SetupGatewaySession` rejects native records. Windows-node WSL
workspace finalization never runs for this route.

At the Optional apps checkpoint, `WizardOptionalSetupHandoff` explicitly cancels
the remaining optional wizard tail, requires cancellation acknowledgement, and
checks `config.get` validity and authenticated `health`. The native session records
optional setup as deferred, not upstream wizard completion. Arbitrary errors or
user cancellation cannot take this success path.

After real wizard completion or the validated optional handoff,
`NativeGatewaySetupSession.CompleteAsync` performs:

```text
Stop setup-owned Gateway -> apply capability policy (and restore legacy reload)
-> validate endpoint/auth and config -> restart with verified ownership
-> authenticated health -> stop setup-owned runtime
-> save and activate GatewayRegistry record -> release setup ownership
```

Only then does the existing setup-completion restart path hand normal operation
to `GatewayConnectionManager` and its App-composed
`NativeGatewayRuntimeRouter`. The isolated branch delegates lifecycle to
`clawctl gateway-service`; it leaves a pre-existing package service running
when Companion detaches. The legacy branch retains Companion-owned process
supervision. Reconnect verifies package/agent listener identity again rather
than adopting an unrelated port. Failed/cancelled setup
must not publish an unverified Gateway record.

### Removal and remaining boundaries

Disconnect stops a Gateway that Companion started, but preserves its data;
an already-running package-owned service is not stopped merely because
Companion detached. Removing the saved Gateway in Connection settings removes
the registry entry, not the agent's OpenClaw configuration or a legacy
Companion-owned profile. Windows Installed apps owns MSIX uninstallation and
package-managed agent data; Companion's device identity is separate. Back up
configuration, credentials, workspace and conversation state before destructive
cleanup. The broader Companion/WSL uninstall flow is not a native-Gateway-only
uninstaller.

Gateway health alone does not prove AI-provider authentication, required
model-runtime availability or a usable default chat. The focused native path
requires live exact-model verification as well; the compatibility wizard retains
its narrower configuration/health summary. Real-package/provider E2E proof and
MXC isolation remain separate gates. See
[Gateway setup responsibilities](GATEWAY_SETUP_RESPONSIBILITIES.md) for the
investigation, production-backed proof and local-only recovery workarounds, and
[onboarding wizard](ONBOARDING_WIZARD.md) for the user flow. Regression coverage
lives in `NativeGatewayRuntimeTests`, `NativeGatewayWindowsProcessHostTests`,
`NativeGatewaySetupTests`, `WizardOptionalSetupHandoffTests` and
`NativeGatewaySetupUxContractTests`.

## When you touch file X, extract toward Y

| If you are editing… | Do not grow it. Extract toward… |
| --- | --- |
| `src/OpenClaw.Tray.WinUI/App.xaml.cs` | use the authoritative `IWindowManager`, `ITrayController`, `ActivationRouter`, `SettingsChangeCoordinator`, and `AppShutdownCoordinator`; the remaining A3 extraction target is startup sequencing into `AppBootstrapper` (planned/deferred) |
| `src/OpenClaw.Tray.WinUI/Windows/HubWindow.xaml.cs` | navigation/catalog policy → `HubPageRegistry`; notification banner projection → `AppNotificationInfoBarPresenter`; keep Frame, NavigationView, back-stack mutation, control application, and route side effects in the view |
| `src/OpenClaw.Tray.WinUI/Windows/WorkspaceWindow.xaml.cs` | route/history policy → `WorkspaceNavigation`; agent/session projection → `WorkspaceProjection`; window lifetime → `WindowManager`; keep named-control application and existing page hosting in the view |
| `src/OpenClaw.Tray.WinUI/Services/TrayMenuRenderer.cs` | semantic composition → `TrayMenuPresenter`; connection toggle projection → `ConnectionTogglePresenter`; keep WinUI control construction and callback application in the renderer |
| `src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs` | Keep as the `IChatDataProvider` facade; atomic runtime coordination → `ChatConversationState`, lock-internal state mechanics → its queue/reset/history/presentation/lifecycle/approval substates, queue decisions → `ChatSendQueuePolicy`, history IO → `ChatHistoryLoader`, mapping → `ChatEventMapper`, native tool projection → `NativeToolProjector`, snapshots → `ChatSnapshotProjector`, metadata → `ChatMetadataStore`, persistence → `ChatStatePersistence` |
| `src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs` | `ChatBubbleRenderer`, `PermissionRequestCard`, `AttachmentBubbleRenderer`; tool rendering stays in `ToolCallCardRenderer` |
| `src/OpenClaw.Tray.WinUI/Chat/OpenClawReactorChatRoot.cs` | Keep as the provider-subscription/selection/timeline-composition root only; composer state → `ChatComposerViewModel`, composer workflow → `ChatComposerController`, composer view → `ReactorChatComposer.cs` |
| `src/OpenClaw.Tray.WinUI/Chat/ReactorChatComposer.cs` | Declarative view only; workflow/state changes go in `ChatComposerViewModel`/`ChatComposerController`, not new Reactor `UseState`/refs here |
| `src/OpenClaw.Tray.WinUI/Pages/ConnectionPage.xaml.cs` | `ConnectionPagePlan` (pure), `ConnectionPageViewModel`, `GatewayDirectConnectService`, gateway row models |
| `src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml.cs` | settings read/persist → `SettingsPageViewModel` + `ISettingsStore`; keep gateway-uninstall, uptime timer, saved-indicator, and app-info in the view |
| `src/OpenClaw.Tray.WinUI/Pages/PermissionsPage.xaml.cs` | state/commands → `PermissionsPageViewModel`; runtime projection → `PermissionsPageRuntimeSource`; persistence → `ISettingsStore` and `IExecApprovalsPresentationStore`; keep exact WinUI rendering, clipboard/privacy actions, and save-hint timer in the view |
| `src/OpenClaw.Tray.WinUI/Services/NodeService.cs` | `McpServerHost`, `CanvasWindowManager`, `MediaCapabilityHost`, `RecordingConsentService`, `SensitiveCaptureExecutor`, `NodeCapabilityRegistry` |
| `src/OpenClaw.Shared/OpenClawGatewayClient.cs` | `GatewayMessageRouter`, per-domain API facades |
| `src/OpenClaw.Shared/Models.cs` | per-domain model files + `*Mapper` classes |
| `src/OpenClaw.Shared/Capabilities/SystemCapability.cs` | `ExecApprovalService` |
| `src/OpenClaw.SetupEngine/SetupSteps.cs` | one file per step (done for the steps still referencing this file); `WslShellClient` and `GatewayConfigScriptBuilder` remain pending. WSL/POSIX quoting is done - use `WslShellQuoting`, never a local `ShellEscape`. Setup-time keepalive process ownership is authoritative in `KeepaliveProcessManager` (see `setup-keepalive-process-manager`). |
| `src/OpenClaw.Connection/GatewayConnectionManager.cs` | The three connection-domain owners are authoritative. Keep only the public lifecycle facade, manager-owned operator/state/tunnel orchestration, typed source/sink/security ports, and event forwarding. |
| Any test hand-rolling a temp dir / env save-restore / CLI capture | `OpenClaw.TestSupport` fixtures |

## Ledger

The ledger is machine-readable and validated by
`OpenClaw.Shared.Tests/Architecture/ArchitectureLedgerConsistencyTests.cs`.
Rows live between the BEGIN/END markers, one per line, pipe-delimited, with a
leading and trailing pipe. Columns, in order:

`id | status | old_owner | closed_responsibility | new_owner | allowed_residue | invariant | guard_test | guard_type | retirement_condition`

- `status`: `planned` | `authoritative` | `closed`
- `guard_type`: `behavioral` | `golden` | `source-shape` | `review-only`
- For `authoritative`/`closed` rows, `guard_test` must name a test as `Type.Method`
  (validated for format), OR `guard_type` must be `review-only` with a real
  rationale in `guard_test` (placeholders like `-`/`none` are rejected).
- For `behavioral`/`golden` rows, the named `guard_test` must actually exist in
  the `tests/` source tree - the consistency test scans for it, so renaming or
  deleting a guard without updating the ledger fails CI.
- `source-shape` rows must set a concrete `retirement_condition`.
- No literal `|` characters inside a cell (they break the pipe-delimited parse).
- Use `-` for a genuinely empty cell (except where a value is required above).

<!-- LEDGER:BEGIN -->
| id | status | old_owner | closed_responsibility | new_owner | allowed_residue | invariant | guard_test | guard_type | retirement_condition |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| setup-installation-overview | authoritative | src/OpenClaw.SetupEngine.UI/Pages/ProgressPage.xaml.cs | overview phase inference from visual row order | SetupInstallationProgress | page retains logs, download detail, dispatcher forwarding and pipeline lifetime; SetupPhaseStatus applies localized icon/text state | every real installation step has an explicit phase; failed state outranks running; skipped work is not claimed as installed | SetupInstallationProgressTests.EveryActualStep_HasAnExplicitPhaseInPipelineOrder | behavioral | - |
| setup-native-window-lifetime | authoritative | src/OpenClaw.SetupEngine.UI/SetupWindow.xaml.cs | implicit classic Settings handoff for native routes | SetupNativeConnectionPage + ISetupNativeConnectionHost | SetupWindow owns typed mounting, same-draft routing, cancellation and drain; AdvancedSetupRequested remains explicit classic fallback only | only committed native results advance to access/privacy then AI; departed native pages drain before the setup lock is released | OnboardingPresentationContractTests.NativeEditor_IsTypedAndDrainedBeforeTheRunLockIsReleased | source-shape | when native setup mounting and close ordering have mounted UI lifecycle tests |
| setup-access-draft | authoritative | src/OpenClaw.SetupEngine.UI/Pages/CapabilitiesPage.xaml.cs | capability preset detection and per-visit setup defaults | SetupAccessDraft + SetupCapabilityProfiles | page applies typed projections and forwards input; SetupWindow owns the draft lifetime | only bundled all-on placeholder defaults once; explicit profiles, consent and independent transports survive navigation without persistence | SetupAccessDraftTests.BundledPlaceholder_DefaultsOnlyAtDraftCreation | behavioral | - |
| setup-capabilities-review-closed | closed | src/OpenClaw.SetupEngine.UI/Pages/CapabilitiesPage.xaml.cs | Local AI probing, Tailscale probing, OS privacy probing and installation review | LocalAiSetupControl + TailscaleSetupControl + GatewaySetupPage; OS privacy stays outside setup | capability page owns transport/profile/Fine-tune controls only | ordinary capabilities must not reabsorb probe lifetimes or consent; only the window draft retains profile intent and disclosure | OnboardingPresentationContractTests.SetupOwnership_ClosesCombinedCapabilitiesReview | source-shape | when setup no longer presents capability and installation choices |
| setup-windows-access-preview-closed | closed | src/OpenClaw.SetupEngine.UI/SetupWindow.xaml.cs | obsolete permissions preview, dedicated control, probe helper and observation policy | removed; native Permissions settings retain their existing owners | no setup privacy probes, preview route or extra stage | removing the developer-only screen must not reintroduce OS probing in capabilities or change runtime consent | OnboardingPresentationContractTests.RetiredWindowsAccessPreview_CannotReintroduceProbesOrAnExtraSetupStage | source-shape | when setup no longer has a preview router |
| setup-browser-completion-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | experimental browser-completion issuer, event payload and activation fallback | SetupNativeHandoffLauncher + SetupDashboardHandoffStore | ordinary Dashboard opening and classic chat/settings/connection restart targets remain | only native receipts can complete setup; obsolete or invalid handles fail visibly without opening a browser | NativeCompletionPresentationTests.CompletionActivation_HasNoSupersededBrowserFallback | source-shape | when setup no longer uses restart receipts |
| setup-instance-admission | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | forwarding an expiring native handoff to a shutting-down primary | NativeRestartAdmission + NativeRestartRecoveryStore | App applies synchronous mutex admission before starting services; ordinary secondary forwarding remains | bounded same-thread waits retain the private handle until authoritative admission; no failed-forward exit discards it | NativeRestartAdmissionTests.DelayedOldOwnerIsNeverForwardedToAndMutexAcquisitionStaysOnOneThread | behavioral | - |
| direct-connect-rollback-observation | authoritative | src/OpenClaw.Tray.WinUI/Services/GatewayDirectConnectService.cs | reasserting stale candidate after rollback CAS conflict | GatewayRegistry.ReplaceSnapshotAndSave/AdoptPersistedSnapshot | only observed persisted active state can reconcile settings; unknown state reports attention | concurrent saved/live authority is preserved, candidate commit requires actual active equivalence, and later normal saves remain possible | GatewayDirectConnectServiceTests.RollbackConflictAdoptsActualNewerSavedSelectionNotStaleCandidate | behavioral | - |
| strict-startup-registration-proof | authoritative | src/OpenClaw.Tray.WinUI/Services/SetupStartupPolicy.cs | interpreting ambiguous registration failure as absence | WindowsStartupTaskRegistration.RegisterForSetup/InspectStrict | old best-effort API stays separate | uncertain registration cannot create duplicate Run-key fallback; only exact enabled task proof may complete it | SetupStartupPolicyTests.AmbiguousRegistrationNeverCreatesRunKey | behavioral | - |
| registry-shared-persistence | authoritative | src/OpenClaw.Connection/GatewayRegistry.cs | per-instance-only compare/write ordering | PersistenceFileLease + persisted snapshot CAS | instance lock precedes path lease; Changed events are outside both | all registry writers reject stale authority and coordinate final compare through replacement; LastConnected merges only for unchanged authority | GatewayRegistryPersistenceTests.OtherInstanceCannotWriteBetweenFinalCheckAndReplacement | behavioral | - |
| setup-settings-owner | authoritative | src/OpenClaw.SetupEngine.UI/SetupWindow.xaml.cs | hosted direct settings-file merging | SetupSettingsWriter + ISettingsStore | standalone merging remains under the shared path lease | background app.settings.set and setup serialize through one owner, preserve unrelated fields and reject same-field conflicts | SettingsStoreTests.HostedSetupSerializesWithBackgroundSettingsMutationWithoutLosingUnrelatedFields | behavioral | - |
| setup-strict-startup | authoritative | src/OpenClaw.Tray.WinUI/Services/AutoStartManager.cs | treating legacy best-effort return as setup success | SetupStartupPolicy + AutoStartSettingsApplier | native strict application uses the existing mutation gate; classic startup failure warns before restart continues | failed removal/fallback never marks native startup applied; classic optional failure does not lose durable setup | SetupStartupPolicyTests.DisableRequiresBothRunKeyRemovalAndSuccessfulTaskRemoval | behavioral | - |
| setup-completion-stable-authority | authoritative | src/OpenClaw.SetupEngine/GatewayAiSetupCompletion.cs | original signing identity and exact session ownership across fresh clients | OpenClawGatewayClient.AuthenticatedSigningDeviceId + SetupCompletionAuthority + SetupNativeVerification | domain-separated identity hash and exact session persist without path or token contents; server echo remains diagnostics only | accepted connect signing identity supplies authority; missing/rotated disk identity rejects without regeneration or relabeling the live client | OpenClawGatewayClientTests.AuthenticatedSigningIdentity_ComesFromConnectNotOptionalHelloEcho | behavioral | - |
| setup-completion-effective-endpoint | authoritative | src/OpenClaw.Connection/GatewayDashboardBinding.cs | stable persisted endpoint ownership including SSH forwarding port | GatewayClientEndpointResolver + GatewayDashboardBinding | temporary validation listener allocation remains separate | only-local-port drift rejects the original receipt before a new connection | SetupCompletionAuthorityTests.PersistedPortDrift_IsRejectedBeforeFreshSessionCanConnect | behavioral | - |
| setup-native-revoked-token-recovery | authoritative | src/OpenClaw.Connection/GatewayConnectionValidator.cs | typed, one-shot saved operator-token mismatch recovery | GatewayValidationIdentity + GatewayCredentialRecoveryPolicy | shared/bootstrap requires renewed endpoint authorization; normal device-token precedence remains | Check never changes saved identity; recovery preserves keypair and original CAS baseline; authenticated bootstrap replacement survives Next | GatewayConnectionValidatorTests.RevokedDeviceToken_RecoversOnceInCopyAndPreservesKeypairThroughCheckAndNext | behavioral | - |
| setup-startup-update-admission | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | ordinary startup update prompt ordering versus expiring native receipt | ActivationRouter.CheckOrdinaryStartupUpdateAsync | App composes startup and dispatch; ordinary startup update behavior is retained | a shaped native handle bypasses update prompting, not receipt verification; installer exit cannot skip that handoff | SetupDashboardHandoffTests.NativeStartup_DoesNotWaitForUpdatePromptOrExitForInstaller | behavioral | - |
| setup-snapshot-bookkeeping | authoritative | src/OpenClaw.Connection/GatewayRegistry.cs | setup baseline admission during LastConnected Update/Save gap | GatewayRegistry.CapturePersistedSnapshot | canonical memory snapshot is returned unchanged | only LastConnected differences are ignored; authority and configuration edits still reject | GatewayRegistryTests.CapturePersistedSnapshot_AcceptsOnlyPendingConnectionBookkeeping | behavioral | - |
| setup-native-progress | authoritative | src/OpenClaw.SetupEngine.UI/Pages/SetupNativeConnectionPage.xaml | route progress and nonoverlapping narrow-window actions | SetupProgressIndicator + SetupWindow.RefreshFlowProgress | separate progress and wrapping action rows | native connection has the Gateway stage announcement without overlaying Back/Cancel/Check/Next | NativeCompletionPresentationTests.NativeConnectionProgress_HasItsOwnRowAboveWrappableActions | source-shape | when the native editor footer is no longer XAML |
| setup-local-ai-review | authoritative | src/OpenClaw.SetupEngine.UI/Pages/CapabilitiesPage.xaml.cs | Local AI hardware/model readiness and generation-fenced recheck | LocalAiSetupControl | GatewaySetupDetailPage forwards lifecycle; SetupWindow keeps hardware probe cache | unknown is not unsupported; pinned recovery cannot silently opt out; global WSL consent is explicit and retained | SetupReviewOwnershipTests.LocalAiReview_PreservesGenerationEligibilityAndPinnedRecovery | source-shape | when the setup Local AI control has mounted hardware-probe tests |
| setup-tailscale-review | authoritative | src/OpenClaw.SetupEngine.UI/Pages/CapabilitiesPage.xaml.cs | Windows Tailscale read-only readiness probe and draft options | TailscaleSetupControl + SetupTailscaleReadiness | GatewaySetupPage binds inline and detaches on navigation/unload; GatewaySetupDetailPage retains compatibility hosting | bounded generation-fenced status probe precedes selected Tailscale installation; off does not probe; rebind cancels before changing drafts; auth key and identity trust remain separate | SetupReviewOwnershipTests.TailscaleReview_PreservesBoundedReadOnlyProbeAndGenerationFence | source-shape | when mounted injected-probe lifecycle tests have authorized native execution proof |
| setup-wsl-route-guard | authoritative | src/OpenClaw.SetupEngine.UI/SetupWindow.xaml.cs | installation eligibility for alternate routes and reviewed replacement | SetupAccessDraft | SetupWindow checks CanInstall before navigating; engine retains destructive ownership guard | only ManagedWsl installs and replacement consent binds to the exact inspected distro | SetupAccessDraftTests.AlternateRoutes_NeverInstallWsl | behavioral | - |
| workspace-navigation | authoritative | HubWindow.xaml.cs | main landing and chat route ownership | WorkspaceNavigation and WorkspaceWindow | HubPageRegistry remains the companion catalog and command search owner | Workspace is the landing page; companion destinations never replace it | WorkspaceNavigationTests.CompanionRoutes_DoNotReplaceWorkspace | behavioral | - |
| hub-chat-hosting | closed | HubWindow.xaml.cs | hosting Chat as a Settings navigation page | WorkspaceWindow with existing ChatPage | legacy chat route redirects to Workspace | Settings remains a separate window and cannot destroy the composer draft | WorkspaceWindowProofTests.DefaultLaunchAndCompanionRefocus_PreserveNativeComposerDraft | behavioral | - |
| workspace-window-lifetime | authoritative | HubWindow.xaml.cs | one shared foreground surface for chat and settings | WindowManager | App composes callbacks only; companion navigation scope stays independent | companion deep links reuse a separate window while Workspace survives | WorkspaceWindowProofTests.NativePagesAndOwnerLinks_KeepCompanionIndependent | behavioral | - |
| workspace-card-renderer | closed | WorkspaceContentPage.xaml.cs | speculative management-page cards and rows | removed per Home/Sessions-only Workspace correction | real management pages remain in the Settings companion | removed pages cannot return through navigation history or deprecated links | WorkspaceNavigationTests.DeprecatedWorkspaceLinks_ReturnHomeAndCannotResurrectRemovedPages | behavioral | - |
| workspace-agent-projection | authoritative | WorkspaceWindow.xaml.cs | ad hoc agent and session identity projection | WorkspaceProjection | view applies projected values | no fixture agents and no background-session selection | WorkspaceNavigationTests.Projection_DoesNotSelectBackgroundSessionsForAssistantChat | behavioral | - |
| chat-voice-readiness | authoritative | ChatPage.xaml.cs | timed retries for cold voice launch | PendingVoiceActivation | ChatPage forwards native readiness and cancels on unload or legacy surface | one explicit request is consumed once when ready, never after page exit | PendingVoiceActivationTests.DelayedComposer_ConsumesExactlyOnceWhenReady | behavioral | - |
| test-temp-dir | authoritative | scattered test files | hand-rolled Path.GetTempPath temp dirs in migrated tests | OpenClaw.TestSupport.TempDirectory | pre-existing un-migrated tests until adopted | temp dirs are created unique and best-effort deleted | TestSupportFixtureTests.TempDirectory_CreatesAndDeletes | behavioral | when all temp-dir tests are migrated |
| test-env-scope | authoritative | scattered test files | hand-rolled env var save/restore in migrated tests | OpenClaw.TestSupport.EnvironmentScope | pre-existing un-migrated tests until adopted | env vars set in a test are restored on dispose | TestSupportFixtureTests.EnvironmentScope_RestoresOriginal | behavioral | when all env-mutating tests are migrated |
| test-cli-harness | authoritative | CLI test projects | duplicated stdout/stderr/env capture tuples | OpenClaw.TestSupport.CliHarness | - | stdout/stderr/env lookup are captured consistently | TestSupportFixtureTests.CliHarness_CapturesAndLooksUp | behavioral | when CLI tests adopt the harness |
| test-fake-mcp | authoritative | OpenClaw.WinNode.Cli.Tests | private internal FakeMcpServer copy | OpenClaw.TestSupport.FakeMcpServer | - | one loopback MCP server captures method/body/auth and returns canned/timeout responses | TestSupportFixtureTests.FakeMcpServer_CapturesRequest | behavioral | when all MCP-round-trip tests share it |
| test-app-mcp-client | authoritative | tests/OpenClaw.Tray.IntegrationTests/McpClient.cs | authenticated MCP request/response helper | OpenClaw.TestSupport.McpClient | original app tests retain the same API through a shared namespace import | fixture-host and original MCP integration consumers share request correlation, bearer handling and tool-error parsing without changing original fixture defaults | GatewayFixtureAppTests.RealOperatorPopulatesSessionsWithoutEnablingNodeExecution | behavioral | - |
| gateway-fixture-isolation | authoritative | App startup and service boundaries | implicit isolated-profile safety assumptions | OpenClaw.Shared.GatewayFixtureIsolation | composition-root validation and narrow service checks | explicit fixture mode requires valid absolute profile and setup-local overrides; ordinary isolated mode is unchanged | GatewayFixtureIsolationTests.Get_MissingOrRelativeRoot_ThrowsWithoutFallback | behavioral | - |
| test-gateway-builder | authoritative | OpenClaw.Connection.Tests | per-file MakeRecord(id,url) helpers | OpenClaw.Connection.Tests.GatewayRecordBuilder | pre-existing MakeRecord until migrated | gateway record test data has one builder | TestSupportFixtureTests.GatewayRecordBuilder_BuildsRecord | behavioral | when MakeRecord helpers are removed |
| test-settings-builder | authoritative | scattered test files | ad hoc SettingsData construction in migrated tests | OpenClaw.TestSupport.SettingsDataBuilder | pre-existing un-migrated tests until adopted | settings test data starts from production defaults | TestSupportFixtureTests.SettingsDataBuilder_StartsFromDefaults | behavioral | when settings tests adopt the builder |
| json-read-helpers | authoritative | OpenClaw.Shared (multiple files) | duplicate non-nullable fallback-returning JsonElement getters | JsonReadHelpers | null-sentinel / non-negative / whitespace-absent / trimming variants stay separate | canonical non-nullable fallback JSON coercion; divergent-contract helpers are not blindly routed here | JsonReadHelpersTests.GetString_ReturnsNull_WhenPropertyMissing | behavioral | when the non-nullable fallback getters are all routed here |
| ollama-node-command-policy | authoritative | src/OpenClaw.Shared/Models.cs | Ollama read-only and sensitive command taxonomy | OllamaNodeCommandPolicy | Models consumes the focused command sets when projecting Command Center health | ollama.models remains optional read-only inventory, ollama.chat remains sensitive, and neither becomes a platform parity requirement | OllamaNodeCommandPolicyTests.ClassifiesCommands | behavioral | - |
| wsl-posix-quoting | authoritative | OpenClaw.SetupEngine/SetupSteps.cs | ad hoc ShellEscape with divergent wrap semantics | WslShellQuoting | - | WSL command lines use POSIX single-quote quoting via WslShellQuoting not cmd/PowerShell quoting | WslShellQuotingTests.QuotePosixSingleQuote_WrapsAndEscapesEmbeddedQuote | behavioral | when no code builds WSL command lines outside WslShellQuoting |
| setup-shellescape-closed | closed | src/OpenClaw.SetupEngine/SetupSteps.cs | private ShellEscape helpers with divergent wrap semantics | WslShellQuoting | - | OpenClaw.SetupEngine builds WSL command lines only via WslShellQuoting; no local ShellEscape helper anywhere in the project | SetupStepsShellEscapeClosureTests.SetupEngine_DoesNotReintroduce_PrivateShellEscape | source-shape | when no file under src/OpenClaw.SetupEngine builds any WSL command strings |
| setup-keepalive-process-manager | authoritative | src/OpenClaw.SetupEngine/SetupSteps.cs (StartKeepaliveStep) | setup-time WSL keepalive process discovery, start, marker read/write, command-line identity, and rollback cleanup | KeepaliveProcessManager (raw OS calls delegated to internal IKeepaliveProcessRuntime seam; StartKeepaliveStep is the only caller that reads SetupContext) | StartKeepaliveStep keeps Id/DisplayName and thin ExecuteAsync/RollbackAsync orchestration only | setup-time keepalive never hard-fails the pipeline on start failure (null PID or thrown exception both soft-fail identically); its marker path/JSON are the intentional handoff consumed by the tray keepalive service; rollback kills only wsl/wsl.exe processes whose command line matches this distro via WslCommandLineMatcher, leaves wrong-distro/unmatched command lines untouched, and deletes only its own marker/empty directory | KeepaliveProcessManagerTests.RollbackAsync_KillsOnlyMatchingDistroProcesses_LeavesOthersUntouched | behavioral | when StartKeepaliveStep contains no process/marker logic of its own |
| wsl-distro-install-path | authoritative | OpenClaw.SetupEngine/SetupSteps.cs | inline Path.Combine wsl distro install-path derivation | DistroInstallPathPolicy | - | new installs use the strict supported name grammar; teardown accepts only unambiguous single-segment names whose canonical path is an immediate child of LocalDataDir\wsl with no aliases, case or Unicode collisions, or reparse points at the root or child | SetupStepsTests.DistroInstallPathPolicy_ResolvesImmediateChild | behavioral | - |
| setup-managed-distro-ownership | authoritative | ExistingConfigDetector, CleanupStaleDistroStep, and CreateWslInstanceStep | durable ownership evidence parsing, current-user WSL registration binding, and marker lifecycle | ManagedDistroOwnership plus WindowsWslRegistrationInspector | detector and setup steps request ownership checks or scoped marker operations only | automatic unregister requires durable OpenClaw evidence plus exactly one readable current-user WSL registration whose canonical BasePath is the expected managed install path; automatic orphan-directory deletion requires a path-bound marker; missing, duplicate, malformed, unreadable, or mismatched registration metadata fails closed; exact UI or CLI destructive consent may override | SetupStepsTests.CleanupStaleDistro_PreservesUnownedRegisteredDistro | behavioral | - |
| managed-local-provenance | authoritative | scattered connection, setup, browser, and reconnect call sites | implicit loopback trust and duplicated strong-credential listener checks | ManagedLocalGatewayPortProvenanceService | callers request inspection, authorization, or conflict repair only | unknown, incomplete, conflicting, or changed Windows listener ownership never receives strong credentials or destructive remediation; relayless ownership requires a complete empty Windows snapshot, expected-distro systemd MainPID proof, and immediate complete empty revalidation | ManagedLocalGatewayPortProvenanceServiceTests.InteractiveCredentialGate_ExpectedCacheThenOwnerChanges_FailsClosed | behavioral | - |
| local-ai-gateway-distro-binding | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | hardcoded Local AI WSL distro selection | LocalAiGatewayDistroResolver | App loads the gateway registry and composes the resolver, provider coordinator, and runtime | the singleton Local AI installation binds to exactly one explicit setup-managed local no-SSH gateway record; its record ID and SetupManagedDistroName are pinned and revalidated before every WSL command, while missing, ambiguous, unavailable, or drifted ownership fails closed | LocalAiGatewayProviderCoordinatorTests.Quiesce_OwnerDriftsAfterInspection_BlocksFirstMutation | behavioral | - |
| gateway-wizard-restart-recovery | authoritative | WizardPage + SetupWizardRunner reconnect call sites | duplicated exact-version terminal-restart classification and bounded provenance retry orchestration | GatewayWizardRestartRecoveryPolicy | WizardPage and SetupWizardRunner apply hosted and headless lifecycle and consume provenance inspection results | only managed-local restart-like disconnects may retry NoListener or the typed snapshot-changed race; other unknown or conflicting ownership fails immediately, retryable startup close 1013 stays inside the existing reconnect bound, and exact Gateway 2026.7.1 final model-check close 1012 completes only after a fresh hello-ok, and a terminal hosted-wizard payload completes on the exact TUI SIGTERM termination only when the request just sent answered the authoritative final done acknowledgement step | GatewayWizardRestartRecoveryPolicyTests.Exact2026_7_1TerminalModelCheckServiceRestart_IsExpected | behavioral | when the 2026.7.1 terminal-restart compatibility path is removed |
| managed-local-repair | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs and direct reconnect callbacks | repair eligibility, restart budgets, port remediation, and reconnect verification | ManagedLocalGatewayAutoRepairMonitor + ManagedLocalGatewayRepairCoordinator | App composition and dependency callbacks only | explicit disconnect and gateway switches abort repair before restart or reconnect | ManagedLocalGatewayRepairCoordinatorTests.UserDisconnectedIntent_AbortsBeforeProbeOrRestart | behavioral | - |
| app-managed-local-repair-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | managed-local repair loops, probing, restart budgeting, and verification implementation | ManagedLocalGatewayAutoRepairMonitor + ManagedLocalGatewayRepairCoordinator | service construction, callback adapters, and lifetime wiring only | App remains the composition root and does not regain repair implementation | AppRefactorContractTests.ManagedLocalGatewayRepair_StaysDelegatedToDedicatedOwners | source-shape | when App no longer constructs the managed-local repair services directly |
| connection-page-direct-connect-closed | closed | src/OpenClaw.Tray.WinUI/Pages/ConnectionPage.xaml.cs | direct-connect registry, identity-token, settings, rollback, terminal-wait, and runtime-tunnel transaction | GatewayDirectConnectService | add-form control reads, input validation, result text, and post-success visual refresh only | the page delegates one request; rollback restores the durable registry before identity and settings, reconnects a previously live gateway, and a later credential writer wins | GatewayDirectConnectServiceTests.Connect_Failure_RestoresPreviousLiveConnection | behavioral | when the Connection page no longer contains any direct-connect persistence or rollback logic |
| connection-status-direct-connect-closed | closed | src/OpenClaw.Tray.WinUI/Windows/ConnectionStatusWindow.xaml.cs | direct-connect registry, settings, rollback, terminal-wait, and runtime-tunnel transaction | GatewayDirectConnectService | diagnostics control reads, input validation, and result text only | diagnostics direct connect delegates one request and cannot report success before a terminal manager state | AppRefactorContractTests.StatusWindowDirectConnect_WaitsForManagerStateBeforeReportingConnected | source-shape | when the status window no longer contains direct-connect persistence or rollback logic |
| app-window-manager | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | Hub, Chat, status, setup, canvas request, and runtime-anchor window creation, reuse, focus, theme, owner, and lifetime mechanics | IWindowManager + WindowManager | App owns composition, typed activation-plan application through WindowManager, service-policy callback adapters, setup restart policy, pairing approval dialog workflow and shell, and shutdown-plan callback construction | distinct window types and exact routes are preserved; Hub close resets navigation scope; setup replacement waits for cleanup; shutdown closes owned windows once before provider disposal | WindowManagerTests.CloseForShutdown_GatesCreationAndClosesOwnedWindowsOnce | source-shape | when App is replaced as the WinUI composition root |
| app-window-surface-ownership-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | concrete non-tray window fields, constructors, show/hide/focus/theme/close mechanics, and window event lifetime | IWindowManager + WindowManager | interface forwarding, immutable request construction, route and policy callbacks, and setup restart dialog policy only | App cannot regain a parallel Hub, Chat, status, setup, canvas-request, or runtime-anchor owner | AppSurfaceOwnershipContractTests.App_DelegatesConcreteTrayAndWindowOwnership | source-shape | when App is replaced as the WinUI composition root |
| app-tray-controller | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | tray icon creation, tray popup coordination, click routing, tooltip and live-toggle refresh, theme, callbacks, and disposal | ITrayController + TrayController | App captures immutable snapshots, implements semantic action callbacks, triggers refresh from authoritative state, preserves startup construction order, and constructs shutdown-plan callbacks | one tray icon and root menu are reused; A1 presenters retain semantics; TrayMenuWindow retains native popup mechanics; callbacks detach and resources dispose once | TrayControllerTests.Dispose_UnsubscribesAndDisposesEachResourceOnce | source-shape | when the WinUI tray surface is replaced |
| app-tray-surface-ownership-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | concrete tray icon, root menu, weak live-control state, event subscriptions, popup build coordination, and resource disposal | ITrayController + TrayController | immutable snapshot and action callbacks plus state-change triggers only | App cannot regain tray controls or popup lifetime and TrayController cannot duplicate A1 semantic projection or TrayMenuWindow native mechanics | AppSurfaceOwnershipContractTests.App_DelegatesConcreteTrayAndWindowOwnership | source-shape | when the WinUI tray surface is replaced |
| native-tool-projector | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | pure native tool identity, allowlisted display arguments, payload extraction, and flattened-history detection/classification/summary | NativeToolProjector | ChatEventMapper and ChatHistoryLoader call the projector; ChatConversationState supplies scoped correlation plans and ChatMetadataStore owns persistence | unknown identities remain truthful Tool; title aliases are strict; display arguments are allowlisted, redacted, and bounded; live/history projection stays consistent | NativeToolProjectorTests.ExtractToolIdentity_TitleRequiresExactTrustedAlias | behavioral | - |
| provider-native-tool-projection-closed | closed | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | private static copies of native tool identity, display argument, payload, flattened-history projection, and scoped metadata upsert | NativeToolProjector + ChatEventMapper + ChatHistoryLoader + ChatConversationState + ChatMetadataStore | provider forwards typed tool metadata writes while retaining bridge IO, telemetry, and event publication only | provider does not regain native tool JSON projection, identity policy, timeline correlation, or metadata persistence | review-only: pure projection, atomic correlation, and persistence are delegated to focused owners while the provider remains the IO facade | review-only | when OpenClawChatDataProvider is retired |
| chat-conversation-state | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | provider-owned runtime gate and cross-domain state transactions | ChatConversationState | sole lock, timeline and entry metadata, connection/disposal flags, and typed orchestration across lock-free substates; provider supplies bridge context and coordinates IO, telemetry, and events | one authoritative lock atomically commits reset, reconnect, dispose, queue, history, and event transitions without duplicate shared versions | ChatRuntimeOwnershipContractTests.Root_CoordinatesCrossDomainCommitsUnderSoleGate | source-shape | when the chat runtime is replaced by a different atomic transaction boundary |
| chat-provider-state-closed | closed | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | private runtime gate and mutable conversation/queue/reset/history collections | ChatConversationState | bridge subscription, telemetry, public API/events, composition, persistence coordination, and static image preview compatibility | provider cannot regain a private state lock or duplicate runtime collections | ChatRuntimeOwnershipContractTests.Provider_DelegatesRuntimeStateWithoutPrivateGate | source-shape | when OpenClawChatDataProvider is retired |
| chat-send-queue | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs and monolithic ChatConversationState | send admission, next-drain eligibility, local echo and run correlation, deferred-admission classification/backoff, retry decisions, and queue commit mechanics | ChatSendQueuePolicy + ChatQueueState | ChatConversationState coordinates queue commits with timeline, reset, and lifecycle state; provider executes typed bridge dispatch plans and records telemetry | queue collections and mechanics live in the lock-free substate under the sole conversation lock while pure policy decisions remain separately testable | ChatRuntimeOwnershipContractTests.RuntimeSubstates_AreLockFreeAndVersionOwnershipIsUnique | source-shape | when queue state and decision policy are replaced without a lock-internal substate |
| chat-reset-state | authoritative | monolithic ChatConversationState | reset versions and cutoffs, accepted and ignored runs, submitted echoes, no-run send proof, buffered starts, and remote-backfill gates | ChatResetState | ChatConversationState supplies queue and lifecycle facts and atomically applies returned typed lifecycle transitions | reset mechanics have no private lock or duplicate version and are invoked only under the conversation lock | ChatRuntimeOwnershipContractTests.RuntimeSubstates_AreLockFreeAndVersionOwnershipIsUnique | source-shape | when reset gating is replaced without a lock-internal substate |
| chat-history-state | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs and monolithic ChatConversationState | session identity, loaded/revision state, reset-cleared identity, connection generation, activation barrier, commit-token validation, and transcript merge reconciliation | ChatHistoryState | ChatConversationState supplies reset/status/disposal facts and atomically coordinates timeline commit; no history IO lives in the substate | one authoritative connection generation and reset-aware commit token accepts or drops history under the sole conversation lock | ChatConversationStateTests.HistoryGeneration_WaitsForLoaderActivation | behavioral | - |
| chat-history-loader | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | chat.history fetch lifetime, in-flight ownership, generation cancellation, retry budget/scheduling, ordered transcript rebuild plans, and stale-result delivery filtering | ChatHistoryLoader | ChatHistoryState owns authoritative identity and generation tokens; ChatConversationState coordinates commit acceptance; provider publishes typed completion results and notifications | stale connection/reset responses cannot commit, deliver, clear a newer in-flight owner, or carry retry work/budget across generations; authoritative reload coalescing remains generation-safe | OpenClawChatDataProviderTests.LoadHistoryAsync_DelayedRetryDoesNotCrossResetGeneration | behavioral | - |
| chat-checkpoint-history-replacement | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | checkpoint-restore timeline clearing, replacement generation fencing, and replacement-over-authoritative reload priority | ChatHistoryState + ChatHistoryLoader coordinated by ChatConversationState | provider starts the typed replacement transition, publishes its immutable snapshot, and delegates gateway IO | replacement atomically clears timeline metadata, invalidates stale results, preserves post-restore live entries, and suppresses stale retries and notifications | ChatConversationStateTests.HistoryReplacement_ClearsTimelineAndAdvancesOwnedTokenAtomically | behavioral | - |
| chat-presentation-state | authoritative | monolithic ChatConversationState | sessions, usage, models, choices, command catalog/fetch epoch, pending model patches, keyless diagnostics, remembered last state, and immutable projection inputs | ChatPresentationState | ChatConversationState supplies timeline/queue/reset/history snapshots and coordinates session identity and usage timeline updates | presentation mechanics have no private lock, IO, or mutable collection exposure and snapshot values remain byte-for-byte compatible | OpenClawChatDataProviderTests.RuntimeGolden_PublicSnapshotPreservesCrossDomainState | golden | - |
| chat-lifecycle-state | authoritative | monolithic ChatConversationState | active run IDs/start sequences, pending aborts, aborted runs/threads, terminal-run dedupe, and lifecycle sequence | ChatLifecycleState | ChatConversationState coordinates lifecycle changes with reset gates, queue state, and timeline reducer events | lifecycle mechanics have no private lock and reset/reconnect/dispose remain root-coordinated atomic transitions | ChatRuntimeOwnershipContractTests.Root_CoordinatesCrossDomainCommitsUnderSoleGate | source-shape | when run lifecycle is replaced without a lock-internal substate |
| chat-approval-state | authoritative | monolithic ChatConversationState | bounded seen-approval identity order/set and alternate-ID correlation | ChatApprovalState | ChatConversationState coordinates approval identity with permission timeline transitions; ChatEventMapper remains the pure payload mapper | approval identity mechanics have no private lock, IO, or timeline callbacks | ChatRuntimeOwnershipContractTests.RuntimeSubstates_AreLockFreeAndVersionOwnershipIsUnique | source-shape | when approval correlation is replaced without a lock-internal substate |
| chat-event-mapper | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | pure agent-stream payload to ChatEvent/content mapping and terminal approval decision classification | ChatEventMapper | provider retains stateful approval dedupe and telemetry orchestration through ChatConversationState | tool, reasoning, lifecycle, command-output, job, and permission payloads map without provider-owned JSON mapping branches | ChatEventMapperTests.Map_ApprovalRequestPreservesIdentityAndActions | behavioral | - |
| chat-snapshot-projector | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | thread/compose-target/status/model/catalog/timeline-generation/history-revision/queued-message snapshot projection including flattened gateway session classification | ChatSnapshotProjector | provider supplies bridge handshake context; ChatConversationState captures immutable projection input; SessionDisplayResolver owns flattened session display mapping | public snapshots preserve defensive dictionary copies, raw session keys, flattened agent/background classification, compose readiness, synthetic pending thread behavior, model order, and render identity generations | OpenClawChatDataProviderTests.RuntimeGolden_PublicSnapshotPreservesCrossDomainState | golden | - |
| chat-content-formatting | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | content text formatting, truncation, content-block seam repair, and trace hashing | ChatContentFormatting | thin provider forwarders (TruncateForChatEntry, LooksLikeSystemControlNote, RepairContentBlockSeams, TruncateChatEvent) kept for existing test call sites; system-note, native tool, and flattened-history projection belongs to NativeToolProjector | content truncation and seam repair output is preserved byte-for-byte while tool identity/classification has one canonical owner | ContentBlockSeamRepairTests.RepairsKnownSeams | behavioral | when provider forwarders are removed and callers use focused owners directly |
| chat-metadata-store | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | live tool/attachment metadata dictionaries, scoped native-tool identity upsert, save locks/timers/versions, atomic JSON persistence, session eviction, and attachment-marker build/escape/rehydration | ChatMetadataStore | ChatConversationState supplies a typed session/reset/correlation write plan; provider retains the public static image-preview cache | metadata persistence, identity-strength upgrade, normalization, bounded eviction, marker security, and generation-aware idempotent reset eviction are owned under the metadata lock without raw attachment bytes on disk | ToolMetaCacheTests.CacheToolMeta_SameToolCallId_UpgradesSpecificIdentityWithoutDuplicate | behavioral | - |
| chat-state-persistence | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | persisted aborted-message IDs plus last-chat-state debounce/version/atomic save lifecycle | ChatStatePersistence | provider retains the nested LastChatState compatibility type and owns bridge history fetch orchestration | corrupted state fails closed, reset removes aborted IDs, stale reset generations cannot persist, and selected/snapshot state writes remain atomic | ChatStatePersistenceTests.ResetFence_RejectsStaleAbortedIds | behavioral | - |
| app-activation-router | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | deep-link/protocol/toast/forwarded activation normalization, current-user IPC, input guards, and confirmation decisions | ActivationRouter | App.ActivationRouter.cs implements IActivationPlanSink only and applies exactly one typed semantic plan through existing A2 owners and services | launch, protocol, toast, and forwarded activation resolve to the same routes; a no-argument secondary launch retries the existing IPC boundary and forwards the default Hub route; current-user IPC, oversized-payload rejection, and confirmation/redaction semantics are preserved | ActivationRouterTests.ForwardLaunchToPrimaryAsync_ListenerStartsLate_RetriesAndDispatchesDefaultHub | behavioral | - |
| app-activation-router-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | concrete deep-link IPC, toast argument routing, and single-instance forwarding production logic | ActivationRouter | App.ActivationRouter.cs implements IActivationPlanSink only, dispatching one typed plan per activation | App does not regain a parallel activation production path outside ActivationRouter | AppRefactorContractTests.ToastActivation_RoutesOnUiThread | source-shape | when App is replaced as the WinUI composition root |
| app-settings-change-coordinator | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | detached snapshot comparison, SettingsChangeClassifier use, concurrent save serialization, and the full post-save effect order | SettingsChangeCoordinator | App supplies the existing effects as delegates and triggers synchronous Apply from one explicit post-save call | browser proxy sync, reconnect, MCP, hotkey, autostart, telemetry, and surface notification order is preserved; MCP-only behavior and credential precedence are unaffected | SettingsChangeCoordinatorTests.Apply_GatewayUrlChange_PreparesBeforeReconnect | behavioral | - |
| app-settings-change-coordinator-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | OnSettingsSaved impact classification, reconnect switch, and inline effect ordering | SettingsChangeCoordinator | App.SettingsChangeCoordinator.cs wires effect delegates only; OnSettingsSaved forwards to Apply | App does not regain a parallel settings-change orchestration path outside SettingsChangeCoordinator | PresentationSeamContractTests.App_AppliesToolCallVisibilityFromPersistedSettings | source-shape | when App is replaced as the WinUI composition root |
| autostart-settings-applier | authoritative | src/OpenClaw.Tray.WinUI/App.SettingsChangeCoordinator.cs | post-save auto-start preference read and Windows write | AutoStartSettingsApplier | App supplies its shared mutation gate, live preference reader, OS setter, and background fault observer | ordinary settings-save effects read the current preference only after acquiring the toggle and reconciliation gate and hold it until the OS write completes; explicit valid fixture runs skip this background host refresh | AutoStartSettingsApplierTests.ApplyLatestAsync_QueuedSave_ReadsPreferenceAfterGateAcquisition | behavioral | - |
| autostart-settings-direct-write-closed | closed | src/OpenClaw.Tray.WinUI/App.SettingsChangeCoordinator.cs | direct ungated auto-start write from a saved SettingsData snapshot | AutoStartSettingsApplier | effect delegate wiring only; startup reconciliation and explicit toggles retain their existing shared gate | post-save effects cannot replay stale snapshots over newer toggle or reconciliation results | MsixDevelopmentSigningTests.SettingsSaveAutoStart_UsesSharedGateAndLivePreference | source-shape | when the WinUI adapter is exercised directly by behavioral tests |
| app-shutdown-coordinator | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | first-wins shared shutdown task, ordered step execution, and per-step log/catch/continue | AppShutdownCoordinator | App builds the immutable step plan from services it owns, including activation null-before-await and failure-safe captured-resource nulling, and constructs the BeginShutdown/ExitApplication actions | shutdown steps run in the same order exactly once even under concurrent callers; each step logs and continues past failure; Exit is called exactly once after all steps | AppShutdownCoordinatorTests.ShutdownAsync_RunsBeginStepsThenExit_InOrder | behavioral | - |
| app-shutdown-coordinator-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | the _isExiting bool guard, SafeShutdownStep/SafeShutdownStepAsync helpers, and inline ExitApplicationAsync body | AppShutdownCoordinator | App.AppShutdownCoordinator.cs builds the step plan only; ExitApplicationAsync forwards to ShutdownAsync | App does not regain a parallel exactly-once shutdown guard or step-execution loop outside AppShutdownCoordinator | AppRefactorContractTests.Shutdown_Order_PreservesAwaitedTeardownBeforeExit | source-shape | when App is replaced as the WinUI composition root |
| gateway-pending-requests | authoritative | src/OpenClaw.Shared/OpenClawGatewayClient.cs | request-id to method/completion tracking | PendingRequestRegistry | callers create request ids, choose timeout policy, parse and route responses, and use the transport | request ids do not leak after disconnect; the registry remains thread-safe with exactly one terminal completion | PendingRequestRegistryTests.ResponseVersusDrain_ExactlyOneTerminalOutcomeWins | behavioral | - |
| connect-envelope | authoritative | src/OpenClaw.Shared/OpenClawGatewayClient.cs + src/OpenClaw.Shared/WindowsNodeClient.cs | connect envelope wire shape, auth field mapping, and v3/v2 signing arguments | ConnectEnvelopeBuilder | callers explicitly select role, scopes, credential profile, and lifecycle/fallback state | the builder cannot infer credential precedence; exact v3/v2 signing bytes and protocol 3/4 remain unchanged | ConnectEnvelopeBuilderTests.Build_CompleteProfileMatrix_PreservesWireShapeAndSigningArguments | golden | - |
| gateway-connect-inline-closed | closed | src/OpenClaw.Shared/OpenClawGatewayClient.cs + src/OpenClaw.Shared/WindowsNodeClient.cs | anonymous connect envelope, auth dictionary, and direct signature/payload construction | ConnectEnvelopeBuilder | explicit role/scope/credential profile selection, lifecycle/fallback state and persistence, redacted logging, transport send | both clients delegate connect construction without moving credential precedence into the builder | GatewayProtocolCoreClosureTests.GatewayClients_DoNotReintroduce_InlineConnectEnvelopeConstruction | source-shape | when both clients are removed or no longer initiate gateway connect handshakes |
| gateway-pending-inline-closed | closed | src/OpenClaw.Shared/OpenClawGatewayClient.cs | pending maps and locks plus Track-Take-Clear and chat-send helper families | PendingRequestRegistry | request-id creation, timeout policy, response parsing/routing, transport | the client delegates registration, take, removal, and disconnect drain so request ids cannot leak and only one completion wins | GatewayProtocolCoreClosureTests.OpenClawGatewayClient_DoesNotReintroduce_InlinePendingRequestTracking | source-shape | when OpenClawGatewayClient is removed or no longer issues correlated requests |
| gateway-protocol-contract | authoritative | ConnectEnvelopeBuilder + src/OpenClaw.Shared/OpenClawGatewayClient.cs + src/OpenClaw.Shared/WindowsNodeClient.cs | duplicated wire-version range, ad hoc successful hello acceptance, and raw mismatch interpretation | GatewayProtocolContract + GatewayProtocolCompatibility | the builder retains exact envelope construction; clients retain credential precedence, signature fallback, generation fencing, and role-specific success side effects | Windows advertises Gateway protocols 3 through 4 and validates the minimal hello-ok shape before success side effects; after the Gateway accepts that range, an integer hello-ok protocol at or above 3 is accepted because it reports the Gateway current constant rather than a negotiated selection; structured mismatch details remain finite and sanitized relative to the advertised range | GatewayProtocolContractTests.SupportedRange_IsThreeThroughFour | behavioral | - |
| gateway-protocol-literals-closed | closed | ConnectEnvelopeBuilder | private minProtocol and maxProtocol numeric literals | GatewayProtocolContract | the builder serializes its existing envelopes using the shared constants | operator and node connect envelopes advertise the same 3-through-4 protocol range | GatewayProtocolContractTests.Clients_use_shared_contract_for_protocol_range | source-shape | when connect envelopes no longer carry a Gateway protocol range |
| ui-dispatcher | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | UI-thread marshaling abstraction for presentation code | IUiDispatcher | App and existing WinUI code may call DispatcherQueue directly until the view-model migration | presentation view models depend on IUiDispatcher not a concrete DispatcherQueue | UiDispatcherContractTests.PageViewModel_ReceivesRegisteredDispatcher | behavioral | - |
| navigation-scope | authoritative | src/OpenClaw.Tray.WinUI/Windows/HubWindow.xaml.cs | page view-model activation/deactivation and disposal lifetime | NavigationScopeManager | HubWindow keeps frame navigation back-stack and rail selection | transient page view models are activated on navigation and deactivated then disposed on navigate-away | NavigationScopeManagerTests.NavigatingAway_DeactivatesAndDisposesPreviousViewModel | behavioral | - |
| composition-root | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | presentation-layer service construction and wiring | AppServiceRegistration | App remains the composition root and owns non-DI service lifetimes | one validated root ServiceProvider; App-owned singletons registered as instances are never disposed by the container | AppServiceRegistrationTests.Dispose_DoesNotDisposeAppOwnedInstanceSingletons | behavioral | - |
| node-summary-text | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | node-summary clipboard text formatting | NodeSummaryText | App keeps the clipboard side effect (building the DataPackage and setting clipboard content) | copied node-summary text is projected only by NodeSummaryText.Build (online/offline state, display-name fallback, short id, detail text, newline join) | NodeSummaryTextTests.Build_MultipleNodes_OneLinePerNodeJoinedByNewline | behavioral | - |
| reactor-chat-timeline | authoritative | removed legacy FunctionalUI chat timeline | production chat message virtualization, row realization, and imperative scroll follow | ReactorChatTimeline through OpenClawReactorChatRoot and ReactorHostControl | - | the default chat route mounts one direct ReactorHostControl per XAML chat target; Reactor owns stable-key ItemsView and ItemContainer realization without a custom native list, collection reconciler, or scroll-layout mutation | ChatTimelinePresentationTests.ReactorTimeline_UsesNonSelectableItemsViewContainersAndAnnotatedScrollBar | source-shape | when ReactorChatTimeline is replaced as the production virtualization owner |
| chat-tool-activity-renderer | authoritative | src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs | production standalone tool-call and grouped activity presentation, summaries, disclosures, and detail rendering | ChatToolActivityPresentation + ToolCallCardRenderer | ReactorChatTimeline projects rows and delegates realization only | consecutive invocation grouping preserves source chronology; stable group identity comes from session, generation, and first tool entry; selectable output remains capped at 240px | ChatToolActivityPresentationTests.Project_GroupsOnlyConsecutiveSpansOfAtLeastTwoTools | behavioral | - |
| chat-history-replay-projection | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | array-valued history content ordering projection | ChatHistoryReplayProjection | provider applies projected text and tool parts to the reducer | interleaved text, calls, and results replay in source order without clearing active tool correlation | OpenClawChatDataProviderTests.LoadHistoryAsync_InterleavedContentParts_PreserveChronologyAndCorrelation | behavioral | - |
| assistant-media-protocol-projection | authoritative | src/OpenClaw.Shared/OpenClawGatewayClient.cs | structured assistant media content parsing and assistant-only legacy MEDIA directive redaction/projection | AssistantMediaDirectiveParser + ChatMediaContentInfo | gateway client preserves ordered typed media while tray presentation receives only safe filenames and metadata | user text never activates media directives; accepted local sources never enter visible assistant text or notifications; media-only messages survive live and history parsing | AssistantMediaDirectiveParserTests.Project_AssistantAbsolutePath_ProducesMediaWithoutExposingPath | behavioral | - |
| assistant-media-resolver | authoritative | src/OpenClaw.Shared/OpenClawGatewayClient.cs | authenticated structured artifact and legacy assistant-media byte retrieval | OpenClawGatewayClient.AssistantMedia | chat bridge exposes only lease-bound typed resolution results; renderer never receives credentials, tickets, or arbitrary URLs | accepts only current-connection results, matching media MIME families, managed ticket paths, and payloads within the 12 MiB image or 16 MiB playback caps | OpenClawGatewayClientAssistantMediaTests.ResolveLegacyMedia_UsesBearerMetadataAndSourceBoundTicket | behavioral | - |
| provider-assistant-media-parsing-closed | closed | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | parsing legacy MEDIA directives or structured Gateway media blocks | AssistantMediaDirectiveParser + OpenClawGatewayClient | provider owns message identity, streaming reconciliation, timeline application, and safe presentation metadata orchestration | provider consumes typed content parts and never reparses model text or exposes raw media sources | ChatAssistantContentPresentationTests.Project_UsesSafeFilenameWithoutExposingLegacySource | behavioral | when assistant message ingestion leaves OpenClawChatDataProvider |
| assistant-media-renderer | authoritative | src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs | assistant media card presentation, bounded image decode, retry, and row cancellation | ChatAssistantMediaRenderer | ReactorChatTimeline owns row placement and delegates media realization | at most four images render inline per message; unsupported or unresolved typed media remains visible as an accessible safe unavailable card; raw Gateway sources are never rendered | ChatAssistantContentPresentationTests.BuildRenderPlan_CapsImagesWithoutReorderingOtherMedia | behavioral | - |
| gateway-media-message-projection | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | gateway media-envelope parsing, safe filename/MIME normalization, attachment signatures, and provenance-safe attachment descriptors | GatewayMediaMessageProjection + ChatAttachmentPresentation | provider applies the projection to live, reset, backfill, and history ingress and owns stateful echo correlation | gateway text never becomes a private marker; gateway descriptors have no preview key; only local opaque preview keys can access image bytes | GatewayMediaMessageProjectionTests.ValidEnvelope_ProjectsSafeDescriptorAndCleanProse | behavioral | - |
| provider-gateway-media-parsing-closed | closed | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | private gateway media-envelope parsing or descriptor construction | GatewayMediaMessageProjection | provider retains stateful pending-echo queues, reset gates, sidecar matching, and reducer application | all user ingress paths call the focused projection and do not independently parse gateway media text | review-only | review-only | when user-message ingestion leaves OpenClawChatDataProvider |
| reactor-tool-rendering-closed | closed | src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs | per-tool and grouped activity summary/detail rendering implementation | ToolCallCardRenderer | row projection, virtualization, hover state, assistant runs, and renderer delegation only | ReactorChatTimeline contains no tool detail renderer and delegates both standalone and grouped tool rows | ChatTimelinePresentationTests.ReactorTimeline_DelegatesToolAndActivityRenderingToFocusedOwner | source-shape | when ReactorChatTimeline is replaced as the production virtualization owner |
| functional-chat-default-mount | closed | src/OpenClaw.Tray.WinUI/Chat/ReactorChatHostExtensions.cs | mounting the FunctionalUI chat tree as the default ChatPage or ChatWindow surface | ReactorChatHostExtensions and OpenClawReactorChatRoot | - | ChatPage and ChatWindow mount the Reactor root directly into their existing ChatHost Borders through ReactorChatHostExtensions | ChatToolCallsToggleContractTests.ProductionChatSurfaces_MountReactorRoot | source-shape | when ReactorChatHostExtensions is replaced as the authoritative production chat mount owner |
| settings-store | authoritative | settings and permission UI surfaces | direct SettingsManager mutation and blanket self-write suppression | ISettingsStore | non-permission legacy surfaces may read SettingsManager until migrated; direct saves publish origin null | every save publishes one versioned event; only the matching writer ignores its own origin while all other active consumers refresh | SettingsSharedStateContractTests.TwoActiveSettingsPageViewModels_IgnoreOnlyOwnWrites_InBothDirections | behavioral | when every settings surface reads and writes through ISettingsStore |
| settings-page-vm | authoritative | src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml.cs | settings load, persist, echo-guard, and auto-save wiring | SettingsPageViewModel | code-behind keeps gateway-uninstall, gateway-info and uptime timer, saved-indicator visual, and app-info population | each settings control persists its field through the store preserving mutate-save-notify order and does not re-persist on external change | SettingsPageViewModelTests.ExternalChange_ReloadsWithoutRePersisting | behavioral | when the Settings page holds no settings persistence logic in code-behind |
| exec-approvals-store | authoritative | src/OpenClaw.Tray.WinUI/Pages/PermissionsPage.xaml.cs | direct exec-approvals.json snapshot, CAS persistence, file observation, and mutable policy cache | ExecApprovalsStore through IExecApprovalsPresentationStore | SystemCapability and NodeService consume the same App-owned concrete store for runtime enforcement | pure reads create nothing; CAS rejects stale hashes; one store-owned observer publishes each distinct external replacement once and retains the last valid presentation snapshot on typed failure | ExecApprovalsStoreTests.Changed_ExternalCorruptThenValid_RaisesFailureThenRecovery | behavioral | - |
| permissions-page-vm | authoritative | src/OpenClaw.Tray.WinUI/Pages/PermissionsPage.xaml.cs | permission settings state, exec-approvals mutations, node/MCP/voice status decisions, and lifecycle subscriptions | PermissionsPageViewModel plus PermissionsPageRuntimeSource | code-behind keeps exact WinUI row/card construction, localization application, colors, visibility, clipboard/token reads, privacy launch, and save-hint timer | activation is pure; field-scoped settings writes preserve save-then-notify; V2 mutations preserve unrelated fields through CAS retry; deactivate/dispose releases subscriptions | PermissionsPageViewModelTests.ExternalValidChange_UpdatesOnce_AndCorruptRetainsLastValidDisplay | behavioral | - |
| permissions-page-direct-owners-closed | closed | src/OpenClaw.Tray.WinUI/Pages/PermissionsPage.xaml.cs | direct SettingsManager, ConnectionManager, and ExecApprovalsStore ownership or subscriptions | PermissionsPageViewModel plus authoritative stores | WinUI-only rendering and platform actions listed in permissions-page-vm | the page applies semantic state only; the view model is WinUI/App/SettingsManager/file-IO free and never creates a parallel mutable domain cache | PermissionsPageContractTests.PermissionsPageViewModel_StaysWinUiAndAppFree | source-shape | when PermissionsPage is replaced by a different view technology |
| shared-mutable-domain-owner | authoritative | presentation pages and view models | backing-file or concrete-manager ownership, per-VM observers, and independent mutable copies of persisted domains | one observable service/store per shared mutable domain | immutable view state projected from authoritative snapshots | different active consumers converge through versioned origin-aware events or CAS snapshots without echo storms, stale whole-snapshot replay, or lost unrelated updates | SettingsSharedStateContractTests.PermissionsPageViewModel_ReceivesOneExternalUpdate_PerDistinctAppSurfaceOrigin | behavioral | - |
| exec-reusable-binding | authoritative | src/OpenClaw.Shared/ExecApprovals/ExecCommandResolution.cs | deriving durable allowlist identities and Allow Always patterns from multi-segment shell resolution | ExecReusableCommandBinder | ExecCommandResolver.Resolve stays the singular resolution used by the state machine and prompt display | at most one identity may be durably authorized per request and it is a fully qualified existing `.exe` image whose arguments are pinned by the generated rule | ExecReusableCommandBinderTests.MultiElementCarrierTail_Binds | behavioral | - |
| exec-multi-segment-allowlist-closed | closed | src/OpenClaw.Shared/ExecApprovals/ExecCommandResolution.cs | ResolveForAllowlist and ResolveAllowAlwaysPatterns feeding allowlist matching or Allow Always patterns | ExecReusableCommandBinder | the two methods remain compiled with their historical tests until removed but have no production callers | the approval pipeline derives AllowlistResolutions and AllowAlwaysPatterns only from ExecReusableCommandBinder.TryBind | ExecApprovalV2NormalizationPipelineOwnershipTests.Normalizer_DerivesDurableIdentity_OnlyFromReusableBinder | source-shape | when ResolveForAllowlist and ResolveAllowAlwaysPatterns are deleted |
| canonical-cmd-carrier | authoritative | src/OpenClaw.Shared/Mxc/MxcConfigBuilder.cs | recognizing the cmd.exe /d /s /c carrier and extracting its command payload | CanonicalCmdCarrier | MxcConfigBuilder keeps cmd command-mode switch detection and command-line construction | the approvals binder and the MXC command-line builder agree on which argv shapes are the canonical cmd carrier and what payload they carry | CanonicalCmdCarrierTests.BinderAndMxcBuilder_AgreeOnCarrierRecognition | behavioral | - |
| exec-carrier-transport-identity | authoritative | src/OpenClaw.Shared/ExecApprovals/ExecApprovalsCoordinator.cs | deciding what a trusted canonical cmd carrier executes once its inner payload is durably authorized | ExecReusableCommandBinder builds the execution argv; CanonicalCmdCarrier.PinnedCarrierMatchesRequest enforces it | the coordinator still owns prompt, policy, and persistence decisions | a durably approved carrier executes a reconstruction of the validated carrier so the MXC in-band PATH/TEMP bootstrap survives; exactly two tokens may differ from the request, argv[0] pinned to the resolved System32 or SysWOW64 cmd.exe and the payload executable token pinned to its resolved absolute path, with every other token and all interior spacing ordinal-identical so no metacharacter drift can be introduced | ExecReusableCommandBinderTests.TrustedCarrier_KeepsTransportSeparateFromIdentity | behavioral | when MXC accepts an explicit environment and the bound direct argv can be executed instead |
| cmd-payload-tokenization | authoritative | src/OpenClaw.Shared/ExecApprovals/ExecReusableCommandBinder.cs | parsing a cmd payload into tokens and rewriting its executable token | CmdPayloadTokenizer | ExecReusableCommandBinder.TryTokenizeStaticCmdPayload remains as a delegating wrapper for existing callers and tests | a payload rewrite is built from parsed token spans and is accepted only after re-parsing proves the argument list is unchanged except for the pinned executable | ExecReusableCommandBinderTests.PinnedCarrier_DoesNotRewriteArgumentsThatRepeatTheExecutableText | behavioral | - |
| exec-carrier-cwd-ambiguity-check | closed | src/OpenClaw.Shared/ExecApprovals/ExecReusableCommandBinder.cs | deciding whether a carrier payload may be durably approved when the working directory could shadow it | CanonicalCmdCarrier.TryBuildPinnedCarrier (payload executable pinning) | - | the approval-time working-directory check is deleted, not merely bypassed: ExecCommandResolver exposes no HasCurrentDirectoryCandidate, a trusted carrier's payload executable is pinned to its resolved absolute path so cmd has nothing to search for, and a post-approval shadow cannot win | ExecReusableCommandBinderTests.PinnedCarrier_IgnoresShadowInsertedAfterApproval | behavioral | - |
| exec-legacy-host-quarantine | authoritative | src/OpenClaw.Shared/ExecApprovals/ExecCommandToken.cs | deciding whether a provenance-less path-only allowlist entry authorizes an interpreter or code host | ExecAllowlistMatcher.MatchInternal via ExecCommandToken.IsLegacyQuarantinedHost | argument binding remains the security boundary for every rule this node generates | an allowlist entry with no source and no argPattern is inert when its resolved target is a command host the previous model refused, is never deleted or migrated, and is superseded only by an explicit allow-always sibling carrying source and argPattern | ExecAllowlistArgBindingTests.LegacyPathOnlyEntryForACommandHost_IsInert | behavioral | - |
| app-ssh-restart-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs and ConnectionPage.xaml.cs | stopping, starting, reconnecting, and declaring success for a user-requested SSH tunnel restart | GatewayConnectionManager.RestartSshTunnelAsync | App and ConnectionPage invoke the manager and present the result | a restart succeeds only after a fresh generation-bound hello-ok and current registry, config, tunnel generation, and owned listener verification | AppRefactorContractTests.UserSshRestart_StaysDelegatedToConnectionManager | source-shape | when App no longer owns any SSH tunnel UI actions |
| hub-page-registry | authoritative | src/OpenClaw.Tray.WinUI/Windows/HubWindow.xaml.cs and GatewayNavVisibilityDebouncePolicy | navigation aliases, page mapping, command metadata and search, and gateway-page classification | HubPageRegistry | HubWindow keeps Frame and NavigationView application, back-stack mutation, command cache lifetime, and semantic action execution; GatewayNavVisibilityDebouncePolicy keeps disconnect timing | every current direct, legacy, and agent-scoped tag resolves identically; command order, titles, actions, search caps, and gateway prune set remain stable | HubPageRegistryTests.BuildCommands_PreservesBaseOrderActionsIconsAndResourceKeys | behavioral | - |
| hub-page-registry-closed | closed | src/OpenClaw.Tray.WinUI/Windows/HubWindow.xaml.cs and GatewayNavVisibilityDebouncePolicy | private tag/page switches, command catalogs or search predicates, and gateway-page tag lists | HubPageRegistry | view-only navigation application and debounce timing listed in hub-page-registry | HubWindow and the debounce policy do not regain catalog or page-classification copies | HubPresentationContractTests.HubPageRegistry_OwnsMappingsCommandsAndGatewayClassification | source-shape | when HubWindow is replaced by a different shell and GatewayNavVisibilityDebouncePolicy is retired |
| app-notification-infobar-presentation | authoritative | src/OpenClaw.Tray.WinUI/Windows/HubWindow.xaml.cs | banner severity filtering, selected-banner fallback, notification action versus Show more, and action enabled state | AppNotificationInfoBarPresenter | HubWindow keeps banner subscription, WinUI control assignment, navigation, and dismissal side effects | Warning and Error banners retain priority and hiding semantics while action projection stays WinUI-free | AppNotificationInfoBarPresenterTests.Present_ActionableNotificationWinsOverShowMore | behavioral | - |
| shell-flyout-content | authoritative | src/OpenClaw.Tray.WinUI/Windows/HubWindow.xaml.cs | compact notification list reconciliation and gateway/operator/node flyout control application | NotificationFlyoutContent + GatewayStatusContent | windows own popup lifetime, badge/status button, and route/reconnect callbacks | Workspace bell preserves chat/history; both status entry points share the existing connection projection | WorkspaceWindowProofTests.SidebarSessions_SelectOriginalKeys_AndKeepCompanionDraft | behavioral | - |
| shell-flyout-content-closed | closed | src/OpenClaw.Tray.WinUI/Windows/HubWindow.xaml.cs and WorkspaceWindow.xaml.cs | bell list reconciliation and duplicate gateway flyout row application | NotificationFlyoutContent + GatewayStatusContent | banner and sidebar application; popup lifetime and action callbacks | windows do not regain flyout lists, row projection, or independent gateway clients | DiagnosticsPageContractTests.ShellFlyoutContent_HasFocusedOwners | source-shape | when both shells retire these flyout entry points |
| tray-menu-presentation | authoritative | TrayMenuStateBuilder and src/OpenClaw.Tray.WinUI/App.xaml.cs | tray row and flyout presence, ordering, text, formatting, icon identity, action, checked and enabled state, accelerator, accessibility names, and connection-toggle projection | TrayMenuPresenter + ConnectionTogglePresenter | App captures immutable input and owns semantic callbacks, persistence, and reconnect policy; TrayController applies live projection; TrayMenuRenderer builds WinUI controls; TrayMenuWindow owns popup mechanics | equal immutable snapshots project equal complete menus; connected and disconnected compositions, all nine permission toggles, and transient connection states preserve behavior | TrayMenuPresenterTests.Connected_ProjectsExactTopLevelAndNestedOrder | behavioral | - |
| tray-menu-state-builder-closed | closed | TrayMenuStateBuilder and src/OpenClaw.Tray.WinUI/App.xaml.cs | snapshot interpretation, semantic menu construction, and duplicated connection-toggle decisions | TrayMenuPresenter + ConnectionTogglePresenter | mechanical rendering, immutable snapshot capture, action dispatch, persistence callbacks, TrayController weak control references, and TrayMenuWindow native popup behavior | presentation owners stay WinUI/App/concrete-settings free and the renderer and controller do not interpret runtime snapshots | TrayMenuPresentationContractTests.PresentationFiles_AreWinUiAppAndConcreteSettingsFree | source-shape | when the tray menu no longer uses WinUI rendering |
| node-connection-coordinator | authoritative | src/OpenClaw.Connection/GatewayConnectionManager.cs | node generation, cancellation, start guard, connect ordering, classified token recovery, connector events, and node telemetry | NodeConnectionCoordinator | manager public node façade; node-only operator/lifecycle/tunnel preparation; typed lifecycle/state/security ports; one event-forwarding subscription set | a superseded lifecycle or node generation cannot write node snapshot state | NodeConnectionCoordinatorTests.SupersededGeneration_DoesNotWriteSnapshot | behavioral | - |
| native-gateway-runtime | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs and src/OpenClaw.Connection/GatewayConnectionManager.cs | native Gateway package resolution, process launch, job ownership, and listener verification | NativeGatewayRuntime and NativeGatewayEndpointSecurity | App composes one runtime with its registry and installed-package resolver; manager delegates start/retry authorization, explicit disconnect and switch stop, and shutdown disposal | native credentials require runtime-owned endpoint proof for both roles; no WSL or remote exemption; reconnect preserves healthy native runtime and starts a crashed owned runtime | GatewayConnectionManagerTests.NativeGateway_ReconnectRestartsCrashWithoutStoppingHealthyRuntime | behavioral | - |
| gateway-manager-node-owner-closed | closed | src/OpenClaw.Connection/GatewayConnectionManager.cs | private node generation/CTS/start workflow/recovery/telemetry implementation | NodeConnectionCoordinator | public node façade; node-only operator/lifecycle/tunnel preparation; typed lifecycle/state/security ports; one event-forwarding subscription set | the manager has no node generation, node CTS, combined node-attempt predicate, node connect core, or node telemetry names | ConnectionDomainOwnerClosureTests.GatewayConnectionManager_DoesNotReintroduceNodeGenerationOrTelemetryOwnership | source-shape | when GatewayConnectionManager no longer composes NodeConnectionCoordinator directly |
| bootstrap-token-lifecycle | authoritative | src/OpenClaw.Connection/GatewayConnectionManager.cs | bootstrap selection and durable clear timing, device-token persistence handoff, post-bootstrap reconnect, and operator token recovery | BootstrapTokenLifecycle | manager public setup/shared-token façade and save-failure rollback; operator event forwarding; typed lifecycle lease, endpoint-security, reconnect, and v2 persistence ports | bootstrap clears only after canonical operator and node role tokens are both durably readable | BootstrapTokenLifecycleTests.ClearsBootstrap_OnlyWhenBothRoleTokensDurable | behavioral | - |
| gateway-manager-bootstrap-owner-closed | closed | src/OpenClaw.Connection/GatewayConnectionManager.cs | bootstrap timing flags, durable-token clear helper, post-bootstrap scheduling, and operator mismatch recovery | BootstrapTokenLifecycle | public setup/shared-token façade and save-failure rollback; one-shot shared-token validation; operator event forwarding; typed lifecycle/reconnect/v2 ports | stale token events cannot restore timing flags, clear a newer record, or schedule an untyped reconnect callback | ConnectionDomainOwnerClosureTests.GatewayConnectionManager_DoesNotReintroduceBootstrapTimingOwnership | source-shape | when GatewayConnectionManager no longer composes BootstrapTokenLifecycle directly |
| device-pair-approval-coordinator | authoritative | src/OpenClaw.Connection/GatewayConnectionManager.cs | device role-upgrade approval, confirmation, dedupe, and one-in-flight plus one-queued bounded reconnect | DevicePairApprovalCoordinator | manager pairing-event forwarding and generation-bound operator gateway lease source | post-approval node reconnect is bounded to two attempts per request and reacquires the current operator gateway | DevicePairApprovalCoordinatorTests.PostApproveReconnect_IsBounded | behavioral | - |
| gateway-manager-device-pair-owner-closed | closed | src/OpenClaw.Connection/GatewayConnectionManager.cs | device-pair approve RPC, success dedupe, reconnect attempts, and queued retry state | DevicePairApprovalCoordinator | pairing-event forwarding, node snapshot application, and generation-bound operator gateway lease source | manager cannot regain device-pair workflow fields or approve/reconnect methods | ConnectionDomainOwnerClosureTests.GatewayConnectionManager_DoesNotReintroduceDevicePairWorkflowOwnership | source-shape | when GatewayConnectionManager no longer composes DevicePairApprovalCoordinator directly |
| chat-composer-view-model | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawReactorChatRoot.cs (nested ReactorChatComposer) | draft text/revision, pending attachment identities/presentation, slash UI state, composer busy flags, selector/queue projections, and derived enablement in the root/nested composer Reactor hooks | ChatComposerViewModel | ReactorChatComposer (view) reads projected values and applies immutable ChatComposerInputs from the root in a stable-source post-commit effect; ChatComposerViewModel never subscribes to IChatDataProvider | every observable mutation is dispatched through IUiDispatcher, ChatComposerInputs accepts only increasing revisions and semantically changed projections, rejected inputs do not notify, and no mutation is accepted after Dispose | ChatComposerViewModelTests.ApplyInputs_RejectsOutOfOrderRevision | behavioral | - |
| chat-composer-controller | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawReactorChatRoot.cs (root SendAsync/OnStop and composer callback closures) | send/stop/reset-confirmation/queue-cancel/model-set-clear/thinking/catalog/attachment-ingress-remove/paste-image/voice workflow and cancellation | ChatComposerController over IChatComposerRuntimePort and ChatComposerHostActions | root session selection (SelectThread) and view event forwarding; D1 provider remains authoritative for send admission and queue mechanics | draft revision and attachment reference identities are snapshotted at operation start and cleared only when the accepted result still matches; operations are fenced by a generation bumped on Dispose so late completions cannot mutate a disposed/superseded controller | ChatComposerControllerTests.SendAsync_EditDuringDelayedSend_DoesNotClearTheEditedDraft | behavioral | - |
| chat-composer-host-lifetime | authoritative | src/OpenClaw.Tray.WinUI/Chat/ReactorChatHostExtensions.cs | ad hoc per-render HostCallbacks assignment and no explicit composer session lifetime | IChatComposerFactory + ChatComposerSession, owned/disposed exactly once by MountedReactorChat | ChatPage and ChatWindow each receive a separate session over the same provider; the factory is a stateless singleton with no constructor-started work | disposing a MountedReactorChat disposes its ChatComposerSession (controller then view model) exactly once, and repeated Dispose calls are a no-op | ChatComposerSessionTests.Dispose_DisposesViewModelAndControllerExactlyOnce | behavioral | - |
| reactor-chat-root-composer-closed | closed | src/OpenClaw.Tray.WinUI/Chat/OpenClawReactorChatRoot.cs | composer draft/attachment/slash/voice/send mutable state and direct composer send/model/thinking/catalog/queue-cancel provider calls | ChatComposerViewModel + ChatComposerController | provider subscription, initial load, immutable snapshot, selected/materialized/compose-only thread selection, timeline/generation/metadata projection, permission-card forwarding, checkpoint routing, #1089 scroll/follow tokens, root composition, and construction of one immutable ChatComposerInputs projection per render | the root holds no composer UseState/refs and calls no composer provider API directly; it builds ChatComposerInputs, forwards them to ReactorChatComposer for post-commit application, and binds the SelectThread handoff | ChatRootComposerClosureTests.Root_DoesNotReintroduceComposerMutableState | source-shape | when OpenClawReactorChatRoot is replaced by a different root/composer boundary |
| sensitive-capture-guard | authoritative | src/OpenClaw.Tray.WinUI/Services/NodeService.cs | ordering of consent, visible per-use indication, and sensor access for instantaneous screen, camera, and location captures | SensitiveCaptureExecutor + SensitiveCapturePlans | NodeService supplies the consent prompt, localized notification, and platform sensor delegates | every sensitive instant capture completes consent and visible indication before its sensor delegate can run; denied consent never reaches the sensor | SensitiveCaptureExecutorTests.ExecuteAsync_RequiresConsentAndIndicatorBeforeSensorAccess | behavioral | - |
| inno-migration-startup | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | migration record parsing and completed-handoff admission | MigrationRecordCodec + InnoMigrationStartupGuard | App invokes the guard before ordinary startup; explicit destructive CLI uninstall remains separate | completed migration prevents normal unpackaged startup before settings or activation; development and packaged apps are unaffected | InnoMigrationContractTests.CompletedMigrationGuard_PrecedesSettingsAndActivation | source-shape | when packaged/unpackaged migration startup is fully hosted outside App |
| store-migration-startup | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | Inno discovery, pending-record inspection, and Store migration admission policy | InnoInstallationDetector + MigrationStartupRecordReader + StoreMigrationStartupCoordinator + StoreMigrationStartupGuard | App calls the compile-time-gated adapter before instance forwarding and normal services | disabled builds perform no migration inspection; preview admission never mutates source state or enables normal startup for pending migration | StoreMigrationStartupCoordinatorTests.Disabled_DoesNotInspectInstallationOrRecords | behavioral | - |
| store-migration-startup-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | direct registry discovery and migration startup policy | StoreMigrationStartupGuard + StoreMigrationStartupCoordinator | one adapter invocation after explicit CLI uninstall and before protocol processing and instance forwarding | App does not regain installation discovery, record parsing, or admission decisions | InnoMigrationContractTests.StorePreviewGuard_PrecedesInstanceForwardingAndNormalServices | source-shape | when packaged migration bootstrap is hosted outside App |
| store-migration-workflow | authoritative | StoreMigrationStartupGuard | native message-box consent loop and inline adoption composition | StoreMigrationWorkflow + StoreMigrationOperations + StoreMigrationWindow | guard gates bootstrap and owns the pre-services window lifetime only | UI actions are serialized; explicit consent precedes shutdown and validation; only verified finalization permits normal startup | StoreMigrationWorkflowTests.ExplicitConfirmation_RecordsConsentBeforeGracefulShutdownAndCompletion | behavioral | - |
| inno-migration-handoff | authoritative | SettingsPage + App activation composition | explicit consent, Store listing policy, and migration shutdown authorization | InnoMigrationHandoff + InnoMigrationConsentStore + StoreMigrationListing | Settings forwards the action and displays its result; App supplies its existing shutdown callback | only configured production Release or explicit preview builds expose handoff; Dev stays isolated; exact same-user Inno and protected consent precede canonical graceful exit; inventory never means consent | InnoMigrationContractTests.InnoPreview_GatesHandoffAndUsesCanonicalShutdown | source-shape | when migration handoff is retired |
| migration-build-policy | authoritative | OpenClaw.Tray.WinUI.csproj | shared Inno/Store compile-time activation and fixed source floor | Migration.Build.props | the tray project imports one policy for packaged and unpackaged publishing | production requires a pinned nonzero source floor and supported Release RID; Debug and Dev cannot inherit production activation | MigrationBuildConfigurationTests.Production_InnoAndStoreShareThePinnedReleasePolicy | behavioral | - |
| migration-consent-locking | authoritative | InnoMigrationConsentStore | protected consent publication and inspection while source is running | MigrationOperationLock + InnoMigrationConsentStore | consent uses a shared preparation lease and a separate exclusive writer lock | consent remains compatible with Inno runtime readers but cannot overlap exclusive Store mutation; finalization removes the writer lock before deleting the completion receipt | InnoMigrationConsentStoreTests.RuntimeReadLease_AllowsExplicitGrantAndInspection | behavioral | - |
| chat-markdown-presentation | authoritative | src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs | paragraph and heading typography, literal code-frame presentation and Copy | ChatMarkdownPresentation | timeline keeps sanitization, parser flags, inert links/images and the existing list-wrapping callback | presentation uses public MarkdownOptions callbacks; code content is never truncated or parsed as markup | ReactorChatLayoutProofTests.CodeBlock_PreservesLiteralContentAndCopiesExactText | behavioral | - |
| chat-model-picker-presentation | authoritative | src/OpenClaw.Tray.WinUI/Chat/ReactorChatComposer.cs, then custom Button rows in ChatModelPicker | catalog search, provider groups, native row interaction states and model metadata presentation | NativeChatModelPicker through a generated Reactor wrapper | ChatModelPicker owns flyout lifetime; composer controller owns model mutation and authorization | native single-selection navigation never writes a model until explicit activation; filtering and resizing preserve raw provider-qualified identities | ReactorChatLayoutProofTests.ModelMenu_UsesNativeSelectionStatesAndPreservesNavigationDuringResize | behavioral | - |
| chat-model-picker-button-rows-closed | closed | src/OpenClaw.Tray.WinUI/Chat/ChatModelPicker.cs | hand-built Button rows, custom selected fill/checkmark and search-box styling | NativeChatModelPicker | declarative trigger and flyout lifetime only | native AutoSuggestBox/ListView own search and row interaction visuals; no duplicate hand-styled selection path | ReactorChatLayoutProofTests.ModelMenu_UsesNativeSelectionStatesAndPreservesNavigationDuringResize | behavioral | - |
| chat-thinking-profile | authoritative | static levels in ReactorChatComposer | advertised reasoning-profile precedence and exact identity/runtime matching | ChatThinkingProfile over immutable ThinkingContext/ThinkingProfile values | ThinkingMetadata parses and scope-fences gateway metadata; client retains session-cache ownership; ChatSnapshotProjector carries it; composer controller gates selections and clears overrides | no provider/model heuristics, missing differs from empty, unknown saved values remain truthful, and Default is explicit null | ChatThinkingProfileTests.Priority_FirstCandidateWithAnyMetadataOwnsEvenEmptyOrDefaultOnly | behavioral | - |
| reactor-markdown-presentation-closed | closed | src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs | inline custom typography and code-frame construction | ChatMarkdownPresentation | timeline owns sanitizer and parser safety options, the existing list-wrapping callback and public callback registration | custom block presentation is delegated, with no parser or list-measurement fork | ReactorChatMarkdownContractTests.AssistantMessages_UseSanitizedGitHubFlavoredMarkdown | source-shape | when the public MarkdownOptions presentation seam is replaced |
| chat-copy-feedback | authoritative | src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs and ChatMarkdownPresentation.cs | copy invocation status, temporary success/failure announcement and reset lifetime | ChatCopyButton with ClipboardHelper.TryCopyText | renderers pass immutable text/identity and keep parser, metadata and workflow ownership | only confirmed clipboard writes show Copied; content changes, repeated clicks and unmount cancel stale resets | ReactorChatLayoutProofTests.CopyFeedback_ResetsRepeatedClicksContentIdentityAndDisposal | behavioral | - |
| chat-effort-picker-presentation | authoritative | ReactorChatComposer's native reasoning menu | effort popover, discrete slider/single-stop presentation and Default affordance | ChatReasoningPicker | ChatThinkingProfile resolves advertised options; ChatComposerController validates and performs mutations; composer owns responsive trigger placement | opening a popover never writes a setting, only advertised IDs are selected, Default stays an explicit clear, and compact icons retain textual automation names | ReactorChatLayoutProofTests.ReasoningPicker_UsesConcreteWireValuesAndExplicitDefaultClear | behavioral | - |
| chat-composer-button-chrome | authoritative | ReactorChatComposer's duplicated per-button resource overrides | shared idle, hover and pressed toolbar resource overrides | ChatVisuals.ToolbarButtonResources | composer constructs the controls and applies the shared resources; native templates retain disabled and keyboard-focus behavior | compact effort stays transparent at rest and matches neighboring toolbar states across resize without replacing native templates | ReactorChatLayoutProofTests.EffortTrigger_PreservesTransparentChromeAndCenteredContent | behavioral | - |
| setup-interactive-flow | authoritative | src/OpenClaw.SetupEngine.UI/Pages/ProgressPage.xaml.cs | interactive installation-step selection and required AI setup decision | OnboardingFlowPolicy | ProgressPage applies progress events and navigation; SetupWindow supplies the selected route to the progress indicator | interactive install never runs the classic wizard or workspace finalization; Local AI still requires exact-model verification when SkipWizard is set; headless step order is unchanged | OnboardingFlowPolicyTests.InteractiveInstallation_DefersWizardAndWorkspaceFinalization | behavioral | - |
| setup-page-flow-closed | closed | src/OpenClaw.SetupEngine.UI/Pages/ProgressPage.xaml.cs | private copy of default installation filtering and implicit post-install milestone gating | OnboardingFlowPolicy | BuildSteps delegates to the policy and success applies its AI setup decision | UI must not recreate the default pipeline selection or require an extra milestone click on successful modern setup | OnboardingPresentationContractTests.SuccessfulInstallation_UsesAiSetupWithoutAMilestoneClick | source-shape | when ProgressPage no longer hosts the interactive installation pipeline |
| setup-gateway-session | authoritative | src/OpenClaw.SetupEngine.UI/Pages/WizardPage.xaml.cs | temporary setup operator client construction, registry identity, endpoint resolution, and reconnect provenance | SetupGatewaySession | setup pages own their session lifetime and render protocol state; GatewayConnectionManager still owns the normal app connection | setup uses the active registry record and SSH-resolved endpoint with device-token precedence and managed-local provenance before sending strong credentials | AppRefactorContractTests.WizardConnect_UsesActiveGatewayRecordUrl | source-shape | when setup reuses the normal connection manager rather than a temporary operator session |
| setup-page-client-construction-closed | closed | src/OpenClaw.SetupEngine.UI/Pages/WizardPage.xaml.cs | private gateway-client constructor and duplicated endpoint/credential resolution | SetupGatewaySession | ConnectClientAsync forwards to the shared setup owner and keeps the returned host-access plan | classic and focused setup cannot diverge in endpoint or credential selection | AppRefactorContractTests.WizardConnect_UsesActiveGatewayRecordUrl | source-shape | when the classic gateway wizard is removed |
| connection-validation-client | authoritative | src/OpenClaw.Connection/GatewayConnectionManager.cs | one-shot validation client construction and credential persistence policy | GatewayConnectionValidator | manager retains shared-token replacement workflow and endpoint authorization callback | validation denies reconnect, pins SSH ownership, and cannot persist handshake tokens | GatewayConnectionValidatorTests.ValidationClient_DisablesHandshakeTokenPersistence | behavioral | - |
| connection-validation-construction-closed | closed | src/OpenClaw.Connection/GatewayConnectionManager.cs | private validation-client constructor policy | GatewayConnectionValidator | CreateSharedTokenValidationClient delegates to the focused owner for compatibility | native setup and shared-token replacement share the same one-shot client policy | GatewayConnectionValidatorTests.ValidationClient_DisablesHandshakeTokenPersistence | behavioral | - |
| setup-native-transaction | authoritative | src/OpenClaw.SetupEngine.UI/Pages/AdvancedSetupPage.xaml.cs | native connection checks and commit orchestration | SetupNativeConnectionHost + GatewayDirectConnectService | SetupNativeConnectionPage edits immutable draft and renders results; WindowManager injects the existing transaction owner | Check cannot write saved gateway or live connection state; Next revalidates and cancellation restores previous state | GatewayDirectConnectServiceTests.NativeNext_CancelAfterHandshakeRestoresPreviousIdentityAndLiveConnection | behavioral | - |
| setup-native-identity-promotion | authoritative | src/OpenClaw.Tray.WinUI/Services/GatewayDirectConnectService.cs | native setup identity promotion and compare-and-swap rollback without gateway-ID replacement | DeviceIdentity.ReplaceValidatedIdentity + DeviceIdentity.RestoreValidatedIdentity | direct-connect owner sequences disconnect, promotion, registry/settings commit and rollback | same-realm setup retains logical gateway ID and sidecars; a newer credential writer is preserved and surfaced as incomplete rollback | GatewayDirectConnectServiceTests.NativeNext_ManagedGatewayRetainsLogicalIdAndLocalAiOwnership | behavioral | - |
| setup-local-ai-route | authoritative | src/OpenClaw.Tray.WinUI/Services/WindowManager.cs | Local AI Gateway inspection and first-install or recovery route resolution | LocalAiSetupRouteResolver + LocalAiSetupRoutePolicy | WindowManager composes the resolver and retains Settings window lifetime; SetupLocalAiHost consumes its same typed target | first install without a receipt uses recovery only after unique app-owned Gateway proof; ambiguous and remote targets cannot receive Windows loopback configuration | LocalAiOnboardingTests.Host_FirstInstallWithoutReceipt_AdmitsSameGatewayWithoutRuntimeMutation | behavioral | - |
| setup-local-ai-mutation-drain | authoritative | src/OpenClaw.SetupEngine.UI/Pages/AiSetupPage.xaml.cs | treating local mutation as a bounded observation request | LocalAiOnboardingUse | page cancels requests and awaits actual local mutation drain before CloseAsync completes | runtime rollback must finish before the setup lock is released; uncertain outcomes retain the exact Gateway and model without replay | LocalAiOnboardingUseTests.CancelledUse_DrainRetainsOwnershipUntilActualRollbackEnds | behavioral | - |
| setup-local-ai-registry-handoff | authoritative | src/OpenClaw.SetupEngine.UI/Pages/ProgressPage.xaml.cs | stale live registry after separate-instance pipeline writes and rollback | SetupPipeline.RunWithSettlementAsync + SetupLocalAiHost + GatewayRegistry.ReconcileSetupOutcome | settlement precedes closed-page early return and setup-lock release; notifications are outside locks | all outcomes adopt only known operation output against admitted memory; concurrent edits require explicit recovery, and stale connections are conditionally disconnected | SetupPipelineSettlementTests.FailedOrCancelledPipelineSettlesBeforeClosedOwnerFinishes | behavioral | - |
| setup-local-ai-route-inline-closed | closed | src/OpenClaw.Tray.WinUI/Services/WindowManager.cs | private Local AI route detection algorithm | LocalAiSetupRouteResolver | constructor/delegate composition and failure notification only | Settings and onboarding cannot grow divergent Gateway ownership admission | WindowManagerTests.LocalAiSetup_ChoosesRecoveryOnlyAfterManagedGatewayProof | source-shape | when both Settings and onboarding route admission have mounted host tests |
| setup-local-ai-observation | authoritative | src/OpenClaw.SetupEngine.UI/Pages/AiSetupPage.xaml.cs | hardware, receipt, runtime and ownership observation or mutation policy | LocalAiOnboardingObservation + LocalAiOnboardingSnapshot + SetupLocalAiHost | page applies localized state and forwards explicit actions; SetupWindow alone navigates existing review and recovery pipeline | observation is independent from Gateway discovery and never starts, publishes, grants consent, migrates or writes; stale and closed callbacks are discarded | LocalAiOnboardingTests.Observation_CancelsRefreshAndDiscardsStaleCallbacks | behavioral | - |
| setup-local-ai-host-boundary | closed | src/OpenClaw.SetupEngine.UI/Pages/AiSetupPage.xaml.cs | opening a second setup window or constructing a parallel Local AI runtime and provider-registration owner | ISetupLocalAiHost + SetupWindow | explicit review callback, cancellation and exact-model verification | Local AI review stays under the existing setup lock, preserves access/startup choices and verifies the exact model before completion | LocalAiOnboardingOwnershipTests.AiPage_UsesTypedSameWindowHostAndNeverOwnsRuntimeOrGatewayRegistration | source-shape | when mounted same-window Local AI navigation tests replace source guards |
| setup-ai-completion-intent | authoritative | src/OpenClaw.SetupEngine.UI/Pages/AiSetupPage.xaml.cs | implicit destination from discovery or authentication success | GatewayAiSetupClient + GatewayAiSetupCompletion | page forwards a current verified receipt; intent remains activation provenance, not the native destination | only exact main-model verification yields a completion; native destinations require explicit choice and cannot fall back to browser completion | GatewayAiSetupClientTests.Completion_UsesExplicitActivationKind_NotSetupComplete | behavioral | - |
| setup-native-final-choice | authoritative | src/OpenClaw.SetupEngine.UI/SetupWindow.xaml.cs | automatic finalization and web launch on modern AI verification | SetupNativeCompletionCoordinator + SetupNativeCompletionVerifier | window owns page mounting, existing finalization hooks and prior page drain; AiReadyPage only renders and forwards choices | showing the chooser issues no nonce and performs no finalization; every explicit choice freshly verifies the same primary-model ownership before once-only finalization and publication | SetupNativeCompletionCoordinatorTests.ShowingChooserDoesNothing_ExplicitChoiceVerifiesThenFinalizesAndPublishes | behavioral | - |
| setup-native-operator-connection | authoritative | src/OpenClaw.SetupEngine.UI/Pages/WizardPage.xaml.cs | native operator construction, exact pairing retry and credential handoff authorization | NativeGatewaySetupConnection + NativeGatewaySetupSession | pages drain their operator socket; the window retains the staged runtime and profile | focused and compatibility setup share per-handshake/request provenance, pinned signing identity and canonical agent/session; lost identities cannot be recreated mid-flow | NativeGatewaySetupConnectionTests.EveryRequestAndReconnect_RechecksNativeOwnership | behavioral | - |
| setup-native-page-client-closed | closed | src/OpenClaw.SetupEngine.UI/Pages/WizardPage.xaml.cs | native client construction and parallel handshake policy | NativeGatewaySetupConnection | classic wizard retains only compatibility RPC/rendering | pages cannot bypass the native owner with a generic loopback session or duplicate pairing policy | NativeGatewaySetupUxContractTests.NativeWizard_UsesSharedPageAndRpc_WithFailClosedStagedAuthorization | source-shape | when compatibility onboarding is removed |
| setup-native-ai-finalization | authoritative | src/OpenClaw.SetupEngine/NativeGatewaySetupSession.cs | classic-wizard-only admission to registry publication | CompleteVerifiedAsync + SetupNativeCompletionCoordinator | classic completion stays separate; focused completion requires real verification without marking the wizard complete | exact model/agent/session/identity is checked after the owned runtime restart and before registry publication; unrelated records survive, occupied draft IDs fail | NativeGatewaySetupConnectionTests.FocusedAi_AllDestinationsReverifyAfterNativeRestartBeforePublication | behavioral | - |
| setup-native-restart-verification | authoritative | src/OpenClaw.SetupEngine/SetupNativeCompletionVerifier.cs | generic native loopback verification after restart | GatewayAiSetupTransport.BorrowNativeAsync + GatewayConnectionManager.RequireNativeSetupClientAsync | App only supplies the manager; verifier never disposes the borrowed client or owns a runtime | native provenance is inspected for every request and receipt drift is rejected before navigation | NativeGatewaySetupConnectionTests.PostRestartVerification_BorrowsNormalOwnerAndRejectsUnownedOrReplacedAuthority | behavioral | - |
| setup-native-pending-launch | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | implicit browser destination for new verified onboarding | SetupNativeHandoffLauncher + SetupNativeNavigationRequest + SetupDashboardHandoffStore | App supplies composition callbacks; WindowManager mounts typed native pages; page-level metadata loading remains in ChannelsPage | native versioned records retain exact Gateway/agent/model/session and exclusive lease; failed opens require explicit retry; successful opens consume | SetupNativeHandoffTests.NativeOpenVerifiesBeforeNavigationAndKeepsFailedLaunchForExplicitRetryOnly | behavioral | - |
| dashboard-launch-owner | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | ordinary Dashboard endpoint construction, credential export policy and browser result handling | GatewayDashboardLauncher + GatewayDashboardUrlBuilder | App supplies credential, tunnel, browser and failure delegates; WindowManager owns visible error/retry lifetime | only shared credentials enter fragment auth; browser failure is visible and never automatically replayed; setup completion cannot enter this path | SetupDashboardHandoffTests.DeviceAndBootstrapTokens_NeverEnterBrowserUrl | behavioral | - |
| app-dashboard-launch-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | inline Dashboard URL construction and silent Process.Start failure handling | GatewayDashboardLauncher | composition delegates and normal Dashboard entry forwarding only | App does not regain a parallel Dashboard URL or credential policy; explicit retries keep the requested path | AppRefactorContractTests.Dashboard_SurfacesSshTunnelConfigurationFailure | source-shape | when the WinUI Dashboard adapter has injected mounted lifecycle coverage |
| setup-session-captured-authority | authoritative | src/OpenClaw.SetupEngine/SetupGatewaySession.cs | deriving client authority from a later active registry record | SetupGatewaySessionBinding | session reloads registry only to validate captured identity at admission, handshake, reconnect and request boundaries | a socket opened for Gateway A cannot be reported as Gateway B; same-ID endpoint and SSH changes fail while connection timestamps remain valid | SetupGatewaySessionBindingTests.ChangedRegistryDuringConnect_CannotRelabelAlreadyCreatedClient | behavioral | - |
| setup-dashboard-pending-proof | authoritative | src/OpenClaw.Tray.WinUI/Services/SetupDashboardHandoff.cs | accepting external serialized completion JSON as verified proof | SetupDashboardHandoffStore + SetupNativeHandoffLauncher | activation parser admits only an opaque native handle; WindowManager rechecks current observations with SetupDashboardLiveFacts | a short-lived local pending record is exclusively leased, consumed on successful native presentation and retained only for explicit failed-launch retry; unknown, expired, forged, consumed and in-flight replay fail visibly; old generation numbers are not live authority | SetupDashboardHandoffStoreTests.ForgedShapeAndUnknownHandle_AreNotVerificationAuthority | behavioral | - |
<!-- LEDGER:END -->

## Deferred test builders

`DeviceIdentityBuilder` and `SetupContextBuilder` are intentionally **not** in
`OpenClaw.TestSupport` yet. `DeviceIdentity` is a stateful Ed25519 key/file
service (not a value type) and `SetupContext` needs setup logger/journal/command-runner
fakes. Both will be added alongside their subsystem PRs (gateway protocol and
SetupEngine, respectively) so `OpenClaw.TestSupport` does not take a heavy
dependency on `OpenClaw.SetupEngine` prematurely.
