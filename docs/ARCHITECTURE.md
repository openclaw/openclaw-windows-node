# OpenClaw Windows node — architecture ledger

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
  observable state, commands, pure projection. WinUI-free where practical — no
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
| Capability UI metadata | `NodeCapabilityUiCatalog` (planned) | planned |
| Capability registration/gating | `NodeCapabilityRegistrationPolicy` (planned) | planned |
| Local MCP exposure policy | `McpCapabilityPolicy` (planned) | planned |
| Gateway connect envelope | `ConnectEnvelopeBuilder` (planned) | planned |
| Gateway request tracking | `PendingRequestRegistry` (planned) | planned |

## When you touch file X, extract toward Y

| If you are editing… | Do not grow it. Extract toward… |
| --- | --- |
| `src/OpenClaw.Tray.WinUI/App.xaml.cs` | `IWindowManager`, `ITrayController`, `IActivationRouter`, `ISettingsChangeCoordinator`, `AppBootstrapper` |
| `src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs` | `ChatSendQueue`, `ChatBridgeEventPump`, `ChatHistoryLoader`, `ChatSnapshotProjector`, `AttachmentMetadataStore` |
| `src/OpenClaw.Tray.WinUI/Chat/OpenClawChatTimeline.cs` | `TimelineScrollController`, `ChatBubbleRenderer`, `ToolCallCardRenderer`, `PermissionRequestCard`, `AttachmentBubbleRenderer` |
| `src/OpenClaw.Tray.WinUI/Chat/OpenClawComposer.cs` | `ComposerViewModel`, `SlashCommandPalette`, `AttachmentPreviewStrip`, `VoiceComposerController` |
| `src/OpenClaw.Tray.WinUI/Pages/ConnectionPage.xaml.cs` | `ConnectionPagePlan` (pure), `ConnectionPageViewModel`, gateway row models |
| `src/OpenClaw.Tray.WinUI/Services/NodeService.cs` | `McpServerHost`, `CanvasWindowManager`, `MediaCapabilityHost`, `RecordingConsentService`, `NodeCapabilityRegistry` |
| `src/OpenClaw.Shared/OpenClawGatewayClient.cs` | `PendingRequestRegistry`, `ConnectEnvelopeBuilder`, `GatewayMessageRouter`, per-domain API facades |
| `src/OpenClaw.Shared/Models.cs` | per-domain model files + `*Mapper` classes |
| `src/OpenClaw.Shared/Capabilities/SystemCapability.cs` | `ExecApprovalService` |
| `src/OpenClaw.Connection/GatewayConnectionManager.cs` | `NodeConnectionCoordinator`, `BootstrapTokenLifecycle`, `DevicePairApprovalCoordinator` |
| `src/OpenClaw.SetupEngine/SetupSteps.cs` | one file per step; `WslShellClient`, `GatewayConfigScriptBuilder`, `KeepaliveProcessManager`. WSL/POSIX quoting is done — use `WslShellQuoting`, never a local `ShellEscape`. |
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
  the `tests/` source tree — the consistency test scans for it, so renaming or
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
| wsl-distro-install-path | authoritative | OpenClaw.SetupEngine/SetupSteps.cs | inline Path.Combine wsl distro install-path derivation | DistroInstallPathPolicy | - | distro install path is a validated immediate child of LocalDataDir\wsl with no reparse-point ancestors before any recursive delete or wsl --install | SetupStepsTests.DistroInstallPathPolicy_ResolvesImmediateChild | behavioral | - |
| app-window-manager | planned | src/OpenClaw.Tray.WinUI/App.xaml.cs | window creation/show/hide/shutdown | IWindowManager | composition/delegation only | startup/shutdown ordering deterministic; disposed once | none | review-only | extracted in Phase 3 |
| app-tray-controller | planned | src/OpenClaw.Tray.WinUI/App.xaml.cs | tray icon/menu/action routing | ITrayController | composition/delegation only | tray actions route unchanged | none | review-only | extracted in Phase 3 |
| app-activation-router | planned | src/OpenClaw.Tray.WinUI/App.xaml.cs | deep-link/toast/single-instance activation | IActivationRouter | composition/delegation only | activation routes land on the same UI/actions; current-user pipe security preserved | none | review-only | extracted in Phase 3 |
| chat-send-queue | planned | src/OpenClaw.Tray.WinUI/Chat/OpenClawChatDataProvider.cs | send queue/admission/abort state | ChatSendQueue | - | queued send/abort/generation semantics preserved | none | review-only | extracted in Phase 4 |
| gateway-pending-requests | planned | src/OpenClaw.Shared/OpenClawGatewayClient.cs | request-id -> method/completion tracking | PendingRequestRegistry | - | request ids never leak after disconnect; thread-safe | none | review-only | extracted in Phase 4 |
| connect-envelope | planned | src/OpenClaw.Shared/OpenClawGatewayClient.cs + WindowsNodeClient.cs | connect message + auth precedence + signature version | ConnectEnvelopeBuilder | - | credential precedence never downgrades a device token; v3->v2 fallback preserved | none | review-only | extracted in Phase 4 |
<!-- LEDGER:END -->

## Deferred test builders

`DeviceIdentityBuilder` and `SetupContextBuilder` are intentionally **not** in
`OpenClaw.TestSupport` yet. `DeviceIdentity` is a stateful Ed25519 key/file
service (not a value type) and `SetupContext` needs setup logger/journal/command-runner
fakes. Both will be added alongside their subsystem PRs (gateway protocol and
SetupEngine, respectively) so `OpenClaw.TestSupport` does not take a heavy
dependency on `OpenClaw.SetupEngine` prematurely.
