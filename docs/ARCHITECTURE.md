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

## Single-source owners

These are the canonical homes. Do not reintroduce private copies elsewhere.

| Concern | Canonical owner | Status |
| --- | --- | --- |
| Test temp directories | `OpenClaw.TestSupport.TempDirectory` | authoritative |
| Test env var save/restore | `OpenClaw.TestSupport.EnvironmentScope` | authoritative |
| CLI stdout/stderr/env capture | `OpenClaw.TestSupport.CliHarness` | authoritative |
| Loopback MCP server for tests | `OpenClaw.TestSupport.FakeMcpServer` | authoritative |
| Gateway record test data | `OpenClaw.Connection.Tests.GatewayRecordBuilder` | authoritative |
| Settings test data | `OpenClaw.TestSupport.SettingsDataBuilder` | authoritative |
| JSON `JsonElement` coercion (non-nullable fallback family) | `JsonReadHelpers` | authoritative |
| WSL/POSIX shell quoting | `WslShellQuoting` | authoritative |
| UI-thread marshaling for presentation code | `IUiDispatcher` | authoritative |
| Page view-model activation/deactivation + disposal lifetime | `NavigationScopeManager` | authoritative |
| Presentation-layer DI composition root | `AppServiceRegistration` (root `ServiceProvider`, owned by `App`) | authoritative |
| Settings snapshot read + batched save + non-echoing change notification | `ISettingsStore` | authoritative |
| Settings page load/persist view logic | `SettingsPageViewModel` | authoritative |
| Native tool identity, display arguments, payload extraction, and flattened-history projection | `NativeToolProjector` | authoritative |
| Managed-local listener provenance and strong-credential authorization | `ManagedLocalGatewayPortProvenanceService` | authoritative |
| Exact Gateway wizard terminal-restart compatibility and bounded retry policy | `GatewayWizardRestartRecoveryPolicy` | authoritative |
| Managed-local automatic repair eligibility and orchestration | `ManagedLocalGatewayAutoRepairMonitor` + `ManagedLocalGatewayRepairCoordinator` | authoritative |
| Capability UI metadata | `NodeCapabilityUiCatalog` (planned) | planned |
| Capability registration/gating | `NodeCapabilityRegistrationPolicy` (planned) | planned |
| Local MCP exposure policy | `McpCapabilityPolicy` (planned) | planned |
| Gateway connect envelope | `ConnectEnvelopeBuilder` (planned) | planned |
| Gateway request tracking | `PendingRequestRegistry` (planned) | planned |

## When you touch file X, extract toward Y

| If you are editing… | Do not grow it. Extract toward… |
| --- | --- |
| `src/OpenClaw.Tray.WinUI/App.xaml.cs` | `IWindowManager`, `ITrayController`, `IActivationRouter`, `ISettingsChangeCoordinator`, `AppBootstrapper` |
| `src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs` | `ChatSendQueue`, `ChatBridgeEventPump`, `ChatHistoryLoader`, `ChatSnapshotProjector`, `AttachmentMetadataStore`; pure native tool projection stays in `NativeToolProjector` |
| `src/OpenClaw.Tray.WinUI/Chat/OpenClawChatTimeline.cs` | `ReactorChatTimeline` (production `ItemsView` / `ItemContainer`), `ChatBubbleRenderer`, `ToolCallCardRenderer`, `PermissionRequestCard`, `AttachmentBubbleRenderer` |
| `src/OpenClaw.Tray.WinUI/Chat/OpenClawComposer.cs` | `ComposerViewModel`, `SlashCommandPalette`, `AttachmentPreviewStrip`, `VoiceComposerController` |
| `src/OpenClaw.Tray.WinUI/Pages/ConnectionPage.xaml.cs` | `ConnectionPagePlan` (pure), `ConnectionPageViewModel`, gateway row models |
| `src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml.cs` | settings read/persist → `SettingsPageViewModel` + `ISettingsStore`; keep gateway-uninstall, uptime timer, saved-indicator, and app-info in the view |
| `src/OpenClaw.Tray.WinUI/Services/NodeService.cs` | `McpServerHost`, `CanvasWindowManager`, `MediaCapabilityHost`, `RecordingConsentService`, `NodeCapabilityRegistry` |
| `src/OpenClaw.Shared/OpenClawGatewayClient.cs` | `PendingRequestRegistry`, `ConnectEnvelopeBuilder`, `GatewayMessageRouter`, per-domain API facades |
| `src/OpenClaw.Shared/Models.cs` | per-domain model files + `*Mapper` classes |
| `src/OpenClaw.Shared/Capabilities/SystemCapability.cs` | `ExecApprovalService` |
| `src/OpenClaw.Connection/GatewayConnectionManager.cs` | `NodeConnectionCoordinator`, `BootstrapTokenLifecycle`, `DevicePairApprovalCoordinator` |
| `src/OpenClaw.SetupEngine/SetupSteps.cs` | one file per step; `WslShellClient`, `GatewayConfigScriptBuilder`, `KeepaliveProcessManager`. WSL/POSIX quoting is done - use `WslShellQuoting`, never a local `ShellEscape`. |
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
| test-temp-dir | authoritative | scattered test files | hand-rolled Path.GetTempPath temp dirs in migrated tests | OpenClaw.TestSupport.TempDirectory | pre-existing un-migrated tests until adopted | temp dirs are created unique and best-effort deleted | TestSupportFixtureTests.TempDirectory_CreatesAndDeletes | behavioral | when all temp-dir tests are migrated |
| test-env-scope | authoritative | scattered test files | hand-rolled env var save/restore in migrated tests | OpenClaw.TestSupport.EnvironmentScope | pre-existing un-migrated tests until adopted | env vars set in a test are restored on dispose | TestSupportFixtureTests.EnvironmentScope_RestoresOriginal | behavioral | when all env-mutating tests are migrated |
| test-cli-harness | authoritative | CLI test projects | duplicated stdout/stderr/env capture tuples | OpenClaw.TestSupport.CliHarness | - | stdout/stderr/env lookup are captured consistently | TestSupportFixtureTests.CliHarness_CapturesAndLooksUp | behavioral | when CLI tests adopt the harness |
| test-fake-mcp | authoritative | OpenClaw.WinNode.Cli.Tests | private internal FakeMcpServer copy | OpenClaw.TestSupport.FakeMcpServer | - | one loopback MCP server captures method/body/auth and returns canned/timeout responses | TestSupportFixtureTests.FakeMcpServer_CapturesRequest | behavioral | when all MCP-round-trip tests share it |
| test-gateway-builder | authoritative | OpenClaw.Connection.Tests | per-file MakeRecord(id,url) helpers | OpenClaw.Connection.Tests.GatewayRecordBuilder | pre-existing MakeRecord until migrated | gateway record test data has one builder | TestSupportFixtureTests.GatewayRecordBuilder_BuildsRecord | behavioral | when MakeRecord helpers are removed |
| test-settings-builder | authoritative | scattered test files | ad hoc SettingsData construction in migrated tests | OpenClaw.TestSupport.SettingsDataBuilder | pre-existing un-migrated tests until adopted | settings test data starts from production defaults | TestSupportFixtureTests.SettingsDataBuilder_StartsFromDefaults | behavioral | when settings tests adopt the builder |
| json-read-helpers | authoritative | OpenClaw.Shared (multiple files) | duplicate non-nullable fallback-returning JsonElement getters | JsonReadHelpers | null-sentinel / non-negative / whitespace-absent / trimming variants stay separate | canonical non-nullable fallback JSON coercion; divergent-contract helpers are not blindly routed here | JsonReadHelpersTests.GetString_ReturnsNull_WhenPropertyMissing | behavioral | when the non-nullable fallback getters are all routed here |
| wsl-posix-quoting | authoritative | OpenClaw.SetupEngine/SetupSteps.cs | ad hoc ShellEscape with divergent wrap semantics | WslShellQuoting | - | WSL command lines use POSIX single-quote quoting via WslShellQuoting not cmd/PowerShell quoting | WslShellQuotingTests.QuotePosixSingleQuote_WrapsAndEscapesEmbeddedQuote | behavioral | when no code builds WSL command lines outside WslShellQuoting |
| setup-shellescape-closed | closed | src/OpenClaw.SetupEngine/SetupSteps.cs | private ShellEscape helpers with divergent wrap semantics | WslShellQuoting | - | SetupSteps builds WSL command lines only via WslShellQuoting; no local ShellEscape helper | SetupStepsShellEscapeClosureTests.SetupSteps_DoesNotReintroduce_PrivateShellEscape | source-shape | when SetupSteps.cs no longer builds any WSL command strings |
| wsl-distro-install-path | authoritative | OpenClaw.SetupEngine/SetupSteps.cs | inline Path.Combine wsl distro install-path derivation | DistroInstallPathPolicy | - | new installs use the strict supported name grammar; teardown accepts only unambiguous single-segment names whose canonical path is an immediate child of LocalDataDir\wsl with no aliases, case or Unicode collisions, or reparse points at the root or child | SetupStepsTests.DistroInstallPathPolicy_ResolvesImmediateChild | behavioral | - |
| managed-local-provenance | authoritative | scattered connection, setup, browser, and reconnect call sites | implicit loopback trust and duplicated strong-credential listener checks | ManagedLocalGatewayPortProvenanceService | callers request inspection, authorization, or conflict repair only | unknown, incomplete, conflicting, or changed Windows listener ownership never receives strong credentials or destructive remediation; relayless ownership requires a complete empty Windows snapshot, expected-distro systemd MainPID proof, and immediate complete empty revalidation | ManagedLocalGatewayPortProvenanceServiceTests.InteractiveCredentialGate_ExpectedCacheThenOwnerChanges_FailsClosed | behavioral | - |
| gateway-wizard-restart-recovery | authoritative | WizardPage + SetupWizardRunner reconnect call sites | duplicated exact-version terminal-restart classification and bounded provenance retry orchestration | GatewayWizardRestartRecoveryPolicy | WizardPage and SetupWizardRunner apply hosted and headless lifecycle and consume provenance inspection results | only managed-local restart-like disconnects may retry NoListener or the typed snapshot-changed race; other unknown or conflicting ownership fails immediately, retryable startup close 1013 stays inside the existing reconnect bound, and exact Gateway 2026.7.1 final model-check close 1012 completes only after a fresh hello-ok | GatewayWizardRestartRecoveryPolicyTests.Exact2026_7_1TerminalModelCheckServiceRestart_IsExpected | behavioral | when the 2026.7.1 terminal-restart compatibility path is removed |
| managed-local-repair | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs and direct reconnect callbacks | repair eligibility, restart budgets, port remediation, and reconnect verification | ManagedLocalGatewayAutoRepairMonitor + ManagedLocalGatewayRepairCoordinator | App composition and dependency callbacks only | explicit disconnect and gateway switches abort repair before restart or reconnect | ManagedLocalGatewayRepairCoordinatorTests.UserDisconnectedIntent_AbortsBeforeProbeOrRestart | behavioral | - |
| app-managed-local-repair-closed | closed | src/OpenClaw.Tray.WinUI/App.xaml.cs | managed-local repair loops, probing, restart budgeting, and verification implementation | ManagedLocalGatewayAutoRepairMonitor + ManagedLocalGatewayRepairCoordinator | service construction, callback adapters, and lifetime wiring only | App remains the composition root and does not regain repair implementation | AppRefactorContractTests.ManagedLocalGatewayRepair_StaysDelegatedToDedicatedOwners | source-shape | when App no longer constructs the managed-local repair services directly |
| app-window-manager | planned | src/OpenClaw.Tray.WinUI/App.xaml.cs | window creation/show/hide/shutdown | IWindowManager | composition/delegation only | startup/shutdown ordering deterministic; disposed once | none | review-only | extracted in Phase 3 |
| app-tray-controller | planned | src/OpenClaw.Tray.WinUI/App.xaml.cs | tray icon/menu/action routing | ITrayController | composition/delegation only | tray actions route unchanged | none | review-only | extracted in Phase 3 |
| app-activation-router | planned | src/OpenClaw.Tray.WinUI/App.xaml.cs | deep-link/toast/single-instance activation | IActivationRouter | composition/delegation only | activation routes land on the same UI/actions; current-user pipe security preserved | none | review-only | extracted in Phase 3 |
| native-tool-projector | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | pure native tool identity, allowlisted display arguments, payload extraction, and flattened-history detection/classification/summary | NativeToolProjector | provider calls the projector while retaining stateful live/history application and metadata cache behavior | unknown identities remain truthful Tool; title aliases are strict; display arguments are allowlisted, redacted, and bounded; live/history projection stays consistent | NativeToolProjectorTests.ExtractToolIdentity_TitleRequiresExactTrustedAlias | behavioral | - |
| provider-native-tool-projection-closed | closed | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | private static copies of native tool identity, display argument, payload, and flattened-history projection | NativeToolProjector | provider owns run/session/legacy-generation correlation, metadata cache persistence/upsert/migration/matching, active run IDs, and timeline state | provider does not regain pure native tool projection or duplicate NativeToolProjector compatibility wrappers | review-only: the provider retains stateful orchestration and calls the focused projector directly | review-only | when OpenClawChatDataProvider no longer applies native tool events or history |
| chat-send-queue | planned | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | send queue/admission/abort state | ChatSendQueue | - | queued send/abort/generation semantics preserved | none | review-only | extracted in Phase 4 |
| gateway-pending-requests | planned | src/OpenClaw.Shared/OpenClawGatewayClient.cs | request-id -> method/completion tracking | PendingRequestRegistry | - | request ids never leak after disconnect; thread-safe | none | review-only | extracted in Phase 4 |
| connect-envelope | planned | src/OpenClaw.Shared/OpenClawGatewayClient.cs + WindowsNodeClient.cs | connect message + auth precedence + signature version | ConnectEnvelopeBuilder | - | credential precedence never downgrades a device token; v3->v2 fallback preserved | none | review-only | extracted in Phase 4 |
| ui-dispatcher | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | UI-thread marshaling abstraction for presentation code | IUiDispatcher | App and existing WinUI code may call DispatcherQueue directly until the view-model migration | presentation view models depend on IUiDispatcher not a concrete DispatcherQueue | UiDispatcherContractTests.PageViewModel_ReceivesRegisteredDispatcher | behavioral | - |
| navigation-scope | authoritative | src/OpenClaw.Tray.WinUI/Windows/HubWindow.xaml.cs | page view-model activation/deactivation and disposal lifetime | NavigationScopeManager | HubWindow keeps frame navigation back-stack and rail selection | transient page view models are activated on navigation and deactivated then disposed on navigate-away | NavigationScopeManagerTests.NavigatingAway_DeactivatesAndDisposesPreviousViewModel | behavioral | - |
| composition-root | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | presentation-layer service construction and wiring | AppServiceRegistration | App remains the composition root and owns non-DI service lifetimes | one validated root ServiceProvider; App-owned singletons registered as instances are never disposed by the container | AppServiceRegistrationTests.Dispose_DoesNotDisposeAppOwnedInstanceSingletons | behavioral | - |
| node-summary-text | authoritative | src/OpenClaw.Tray.WinUI/App.xaml.cs | node-summary clipboard text formatting | NodeSummaryText | App keeps the clipboard side effect (building the DataPackage and setting clipboard content) | copied node-summary text is projected only by NodeSummaryText.Build (online/offline state, display-name fallback, short id, detail text, newline join) | NodeSummaryTextTests.Build_MultipleNodes_OneLinePerNodeJoinedByNewline | behavioral | - |
| reactor-chat-timeline | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatTimeline.cs | production chat message virtualization, row realization, and imperative scroll follow | ReactorChatTimeline through OpenClawReactorChatRoot and ReactorHostControl | OpenClawChatTimeline remains a legacy focused-test surface while its runtime route is migrated | the default chat route mounts one direct ReactorHostControl per XAML chat target; Reactor owns stable-key ItemsView and ItemContainer realization without a custom native list, collection reconciler, or scroll-layout mutation | review-only: user explicitly deferred new tests for this migration; required build and existing shared/tray suites still run | review-only | when Reactor timeline proof coverage replaces the legacy focused UI host coverage |
| chat-tool-activity-renderer | authoritative | src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs | production standalone tool-call and grouped activity presentation, summaries, disclosures, and detail rendering | ChatToolActivityPresentation + ToolCallCardRenderer | ReactorChatTimeline projects rows and delegates realization only | consecutive invocation grouping preserves source chronology; stable group identity comes from session, generation, and first tool entry; selectable output remains capped at 240px | ChatToolActivityPresentationTests.Project_GroupsOnlyConsecutiveSpansOfAtLeastTwoTools | behavioral | - |
| chat-history-replay-projection | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | array-valued history content ordering projection | ChatHistoryReplayProjection | provider applies projected text and tool parts to the reducer | interleaved text, calls, and results replay in source order without clearing active tool correlation | OpenClawChatDataProviderTests.LoadHistoryAsync_InterleavedContentParts_PreserveChronologyAndCorrelation | behavioral | - |
| assistant-media-protocol-projection | authoritative | src/OpenClaw.Shared/OpenClawGatewayClient.cs | structured assistant media content parsing and assistant-only legacy MEDIA directive redaction/projection | AssistantMediaDirectiveParser + ChatMediaContentInfo | gateway client preserves ordered typed media while tray presentation receives only safe filenames and metadata | user text never activates media directives; accepted local sources never enter visible assistant text or notifications; media-only messages survive live and history parsing | AssistantMediaDirectiveParserTests.Project_AssistantAbsolutePath_ProducesMediaWithoutExposingPath | behavioral | - |
| assistant-media-resolver | authoritative | src/OpenClaw.Shared/OpenClawGatewayClient.cs | authenticated structured artifact and legacy assistant-media byte retrieval | OpenClawGatewayClient.AssistantMedia | chat bridge exposes only lease-bound typed resolution results; renderer never receives credentials, tickets, or arbitrary URLs | accepts only current-connection results, matching media MIME families, managed ticket paths, and payloads within the 12 MiB image or 16 MiB playback caps | OpenClawGatewayClientAssistantMediaTests.ResolveLegacyMedia_UsesBearerMetadataAndSourceBoundTicket | behavioral | - |
| provider-assistant-media-parsing-closed | closed | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | parsing legacy MEDIA directives or structured Gateway media blocks | AssistantMediaDirectiveParser + OpenClawGatewayClient | provider owns message identity, streaming reconciliation, timeline application, and safe presentation metadata orchestration | provider consumes typed content parts and never reparses model text or exposes raw media sources | ChatAssistantContentPresentationTests.Project_UsesSafeFilenameWithoutExposingLegacySource | behavioral | when assistant message ingestion leaves OpenClawChatDataProvider |
| assistant-media-renderer | authoritative | src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs | assistant media card presentation, bounded image decode, retry, and row cancellation | ChatAssistantMediaRenderer | ReactorChatTimeline owns row placement and delegates media realization | at most four images render inline per message; unsupported or unresolved typed media remains visible as an accessible safe unavailable card; raw Gateway sources are never rendered | ChatAssistantContentPresentationTests.BuildRenderPlan_CapsImagesWithoutReorderingOtherMedia | behavioral | - |
| gateway-media-message-projection | authoritative | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | gateway media-envelope parsing, safe filename/MIME normalization, attachment signatures, and provenance-safe attachment descriptors | GatewayMediaMessageProjection + ChatAttachmentPresentation | provider applies the projection to live, reset, backfill, and history ingress and owns stateful echo correlation | gateway text never becomes a private marker; gateway descriptors have no preview key; only local opaque preview keys can access image bytes | GatewayMediaMessageProjectionTests.ValidEnvelope_ProjectsSafeDescriptorAndCleanProse | behavioral | - |
| provider-gateway-media-parsing-closed | closed | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | private gateway media-envelope parsing or descriptor construction | GatewayMediaMessageProjection | provider retains stateful pending-echo queues, reset gates, sidecar matching, and reducer application | all user ingress paths call the focused projection and do not independently parse gateway media text | review-only | review-only | when user-message ingestion leaves OpenClawChatDataProvider |
| reactor-tool-rendering-closed | closed | src/OpenClaw.Tray.WinUI/Chat/ReactorChatTimeline.cs | per-tool and grouped activity summary/detail rendering implementation | ToolCallCardRenderer | row projection, virtualization, hover state, assistant runs, and renderer delegation only | ReactorChatTimeline contains no tool detail renderer and delegates both standalone and grouped tool rows | ChatTimelinePresentationTests.ReactorTimeline_DelegatesToolAndActivityRenderingToFocusedOwner | source-shape | when ReactorChatTimeline is replaced as the production virtualization owner |
| functional-chat-default-mount | closed | src/OpenClaw.Tray.WinUI/Chat/FunctionalChatHostExtensions.cs | mounting the FunctionalUI chat tree as the default ChatPage or ChatWindow surface | ReactorChatHostExtensions and OpenClawReactorChatRoot | legacy FunctionalUI chat files may remain for focused compatibility coverage only | ChatPage and ChatWindow mount the Reactor root directly into their existing ChatHost Borders; no FunctionalUI component mounts or nests Reactor on the default path | review-only: user explicitly deferred new tests for this migration; required build and existing shared/tray suites still run | review-only | when legacy FunctionalUI chat surfaces are removed |
| settings-store | authoritative | src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml.cs | hand-rolled save/echo suppression flags for two-way settings binding | ISettingsStore | PermissionsPage and other surfaces may read SettingsManager directly until migrated | a save originating from Update does not echo Changed to the caller and external saves are republished on the UI thread | SettingsStoreTests.Update_DoesNotEchoChangedToSelf | behavioral | when all settings surfaces read and write through ISettingsStore |
| settings-page-vm | authoritative | src/OpenClaw.Tray.WinUI/Pages/SettingsPage.xaml.cs | settings load, persist, echo-guard, and auto-save wiring | SettingsPageViewModel | code-behind keeps gateway-uninstall, gateway-info and uptime timer, saved-indicator visual, and app-info population | each settings control persists its field through the store preserving mutate-save-notify order and does not re-persist on external change | SettingsPageViewModelTests.ExternalChange_ReloadsWithoutRePersisting | behavioral | when the Settings page holds no settings persistence logic in code-behind |
| exec-reusable-binding | authoritative | src/OpenClaw.Shared/ExecApprovals/ExecCommandResolution.cs | deriving durable allowlist identities and Allow Always patterns from multi-segment shell resolution | ExecReusableCommandBinder | ExecCommandResolver.Resolve stays the singular resolution used by the state machine and prompt display | at most one identity may be durably authorized per request and it is a fully qualified existing `.exe` image whose arguments are pinned by the generated rule | ExecReusableCommandBinderTests.MultiElementCarrierTail_Binds | behavioral | - |
| exec-multi-segment-allowlist-closed | closed | src/OpenClaw.Shared/ExecApprovals/ExecCommandResolution.cs | ResolveForAllowlist and ResolveAllowAlwaysPatterns feeding allowlist matching or Allow Always patterns | ExecReusableCommandBinder | the two methods remain compiled with their historical tests until removed but have no production callers | the approval pipeline derives AllowlistResolutions and AllowAlwaysPatterns only from ExecReusableCommandBinder.TryBind | ExecApprovalV2NormalizationPipelineOwnershipTests.Normalizer_DerivesDurableIdentity_OnlyFromReusableBinder | source-shape | when ResolveForAllowlist and ResolveAllowAlwaysPatterns are deleted |
| canonical-cmd-carrier | authoritative | src/OpenClaw.Shared/Mxc/MxcConfigBuilder.cs | recognizing the cmd.exe /d /s /c carrier and extracting its command payload | CanonicalCmdCarrier | MxcConfigBuilder keeps cmd command-mode switch detection and command-line construction | the approvals binder and the MXC command-line builder agree on which argv shapes are the canonical cmd carrier and what payload they carry | CanonicalCmdCarrierTests.BinderAndMxcBuilder_AgreeOnCarrierRecognition | behavioral | - |
| exec-carrier-transport-identity | authoritative | src/OpenClaw.Shared/ExecApprovals/ExecApprovalsCoordinator.cs | deciding what a trusted canonical cmd carrier executes once its inner payload is durably authorized | ExecReusableCommandBinder builds the execution argv; CanonicalCmdCarrier.PinnedCarrierMatchesRequest enforces it | the coordinator still owns prompt, policy, and persistence decisions | a durably approved carrier executes a reconstruction of the validated carrier so the MXC in-band PATH/TEMP bootstrap survives; exactly two tokens may differ from the request, argv[0] pinned to the resolved System32 or SysWOW64 cmd.exe and the payload executable token pinned to its resolved absolute path, with every other token and all interior spacing ordinal-identical so no metacharacter drift can be introduced | ExecReusableCommandBinderTests.TrustedCarrier_KeepsTransportSeparateFromIdentity | behavioral | when MXC accepts an explicit environment and the bound direct argv can be executed instead |
| cmd-payload-tokenization | authoritative | src/OpenClaw.Shared/ExecApprovals/ExecReusableCommandBinder.cs | parsing a cmd payload into tokens and rewriting its executable token | CmdPayloadTokenizer | ExecReusableCommandBinder.TryTokenizeStaticCmdPayload remains as a delegating wrapper for existing callers and tests | a payload rewrite is built from parsed token spans and is accepted only after re-parsing proves the argument list is unchanged except for the pinned executable | ExecReusableCommandBinderTests.PinnedCarrier_DoesNotRewriteArgumentsThatRepeatTheExecutableText | behavioral | - |
| exec-carrier-cwd-ambiguity-check | closed | src/OpenClaw.Shared/ExecApprovals/ExecReusableCommandBinder.cs | deciding whether a carrier payload may be durably approved when the working directory could shadow it | CanonicalCmdCarrier.TryBuildPinnedCarrier (payload executable pinning) | - | the approval-time working-directory check is deleted, not merely bypassed: ExecCommandResolver exposes no HasCurrentDirectoryCandidate, a trusted carrier's payload executable is pinned to its resolved absolute path so cmd has nothing to search for, and a post-approval shadow cannot win | ExecReusableCommandBinderTests.PinnedCarrier_IgnoresShadowInsertedAfterApproval | behavioral | - |
| exec-legacy-host-quarantine | authoritative | src/OpenClaw.Shared/ExecApprovals/ExecCommandToken.cs | deciding whether a provenance-less path-only allowlist entry authorizes an interpreter or code host | ExecAllowlistMatcher.MatchInternal via ExecCommandToken.IsLegacyQuarantinedHost | argument binding remains the security boundary for every rule this node generates | an allowlist entry with no source and no argPattern is inert when its resolved target is a command host the previous model refused, is never deleted or migrated, and is superseded only by an explicit allow-always sibling carrying source and argPattern | ExecAllowlistArgBindingTests.LegacyPathOnlyEntryForACommandHost_IsInert | behavioral | - |
<!-- LEDGER:END -->

## Deferred test builders

`DeviceIdentityBuilder` and `SetupContextBuilder` are intentionally **not** in
`OpenClaw.TestSupport` yet. `DeviceIdentity` is a stateful Ed25519 key/file
service (not a value type) and `SetupContext` needs setup logger/journal/command-runner
fakes. Both will be added alongside their subsystem PRs (gateway protocol and
SetupEngine, respectively) so `OpenClaw.TestSupport` does not take a heavy
dependency on `OpenClaw.SetupEngine` prematurely.
