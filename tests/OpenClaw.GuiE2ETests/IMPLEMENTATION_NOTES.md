# Implementation notes for the GUI E2E lane

Companion to [PLAN.md](PLAN.md). Everything here was verified against the
repository at the time of writing (branch `dev/plarroy/gui-e2e-plan`). Re-verify
line numbers with `grep` before relying on them; symbol names are the stable part.

## 1. Code to port, with exact sources

| Need | Copy from | What to take |
|---|---|---|
| Launch isolated tray, seed settings, deep-link navigation, nav-signal file parsing, page-marker wait, foreground + `CopyFromScreen` screenshot with blank-image guard, crash-log detection, P/Invokes | `tests\OpenClaw.Tray.UITests\AccessibilityAppFixture.cs` | `StartProcess` (env set, lines ~338-368), `NavigateAsync` + `WaitForNavigationSignalAsync` + `ReadNavigationSignals` (tab-separated `guid\tPageName` lines), `WaitForPageMarkerAsync`, `CaptureHubScreenshotIfRequested` (sampled-colour guard), `EnsureTargetIsAlive`, `WaitForHubWindow` |
| Isolated dirs with separate LOCALAPPDATA, MCP port allocation, MCP-ready loop, tool-availability wait, stdout/stderr capture, log copy, on-disk gateway record reader | `tests\OpenClaw.E2ETests\IsolatedTrayInstance.cs` | `WriteSettings` dictionary, `SpawnTray`, `WaitForMcpReadyAsync` (reads `mcp-token.txt`, GETs `http://127.0.0.1:{port}/`), `ReadActiveGatewayRecord`, `ReadCredentialState`, `WaitForConnectionReady` (polls MCP `app.status` for `connectionStatus`, `nodeConnected`, `nodePaired`), `LocateTrayExe`, `FindFreePort` |
| Loopback WebSocket gateway stub with method responders and frame capture | `tests\OpenClaw.Shared.Tests\GatewayProtocolLiveRoundTripTests.cs` (`LoopbackGatewayServer`, ~line 355) | HttpListener bind-with-retry, `OnMethod(string, Func<JsonElement, object>)`, `_frames` queue, `WaitFrameAsync(method, occurrence, timeoutMs)` |
| Artifact redaction and secret-file exclusion | `tests\OpenClaw.E2ETests\Setup\E2ESetupFixture.cs` | `SanitizeForLog` (~line 900: redacts `token|authorization|secret|password` values, `Bearer` tokens, any 48+ char base64-ish run), `ShouldCopyArtifactFile` (~line 966: never `gateways.json`, `settings.json`, `device-key*`, anything under a `gateways\` segment), `CopyLogsFrom`, `AllocateFreePort`, `OPENCLAW_E2E_TRAY_EXE` override handling |
| MCP JSON-RPC client | `tests\OpenClaw.E2ETests\McpClient.cs` | reuse as-is via `InternalsVisibleTo` |
| Fake Ollama HTTP server | `tests\OpenClaw.E2ETests\FakeOllamaServer.cs` | reuse as-is |
| Gate attribute pattern | `tests\OpenClaw.E2ETests\E2EFactAttribute.cs` | `E2ETestGate.IsEnabled` "1|true" check; `MxcE2EFactAttribute` shows GITHUB_ACTIONS-aware skipping |
| Page inventory (tag, page name, marker id) for all 18 hub pages | `tests\OpenClaw.Tray.UITests\AccessibilityScanTests.cs` `PageTestData()` | copy the list into `Pages\AutomationIds.cs` |

Existing internals in `tests\OpenClaw.E2ETests` are `internal`; add
`[assembly: InternalsVisibleTo("OpenClaw.GuiE2ETests")]` to
`tests\OpenClaw.E2ETests\AssemblyInfo.cs`.

## 2. App hooks the harness relies on

| Env var | Effect | Where |
|---|---|---|
| `OPENCLAW_TRAY_DATA_DIR`, `OPENCLAW_TRAY_APPDATA_DIR`, `OPENCLAW_TRAY_LOCALAPPDATA_DIR` | isolate all state; `DataDirOverride != null` also disables URI-scheme registration and the startup update check | `src\OpenClaw.Tray.WinUI\App.xaml.cs` (~684, ~699) |
| `OPENCLAW_FORCE_ONBOARDING=1` | always show the setup wizard at startup | `App.xaml.cs:839-840` |
| `OPENCLAW_ACCESSIBILITY_NAVIGATION_SIGNAL=<file>` | app appends `guid\tPageName` when a hub page is ready | `src\OpenClaw.Tray.WinUI\Services\AccessibilityNavigationSignal.cs`, called from `Windows\HubWindow.xaml.cs` |
| `OPENCLAW_UI_AUTOMATION=1` | tray flyout does not auto-dismiss on deactivate | `Windows\TrayMenuWindow.xaml.cs:219` |
| `OPENCLAW_MCP_PORT` | fixed loopback MCP port; token written to `<datadir>\mcp-token.txt` | `IsolatedTrayInstance.WaitForMcpReadyAsync` |
| `OPENCLAW_SUPPRESS_EXTERNAL_BROWSER=1` | no browser launches during tests | used by `IsolatedTrayInstance.SpawnTray` |
| `OPENCLAW_SKIP_UPDATE_CHECK=1`, `OPENCLAW_LANGUAGE=en-US` | belt-and-braces; language pins UIA `Name` strings | `AccessibilityAppFixture.StartProcess` |
| `OPENCLAW_E2E_TRAY_EXE` | override exe path (must be an existing `.exe`) | `E2ESetupFixture.ResolveTrayExecutable` |

Wizard skip rule: `StartupSetupState.RequiresSetup` returns false when
`EnableMcpServer=true` is seeded, so the minimal seed used by
`AccessibilityAppFixture` (`EnableMcpServer=true, GlobalHotkeyEnabled=false, AutoStart=false`)
lands directly on the Hub. An empty data dir triggers onboarding naturally.

Deep-link routes handled by `src\OpenClaw.Tray.WinUI\Services\DeepLinkHandler.cs`:
`hub/<tag>`, `tray|tray-menu|menu`, `settings`, `chat`, `check-updates`,
`agent?message=` (send). Scheme constant: `OpenClawTray.AppIdentity.ProtocolScheme`
(`openclaw`, or `openclaw-dev` for DevBuild). Because isolated instances do
not register the scheme, send a link by starting a second
`OpenClaw.Tray.WinUI.exe <uri>` with the same data-dir env; it forwards over
IPC and exits.

## 3. Fake gateway wire contract

Sources: `src\OpenClaw.Shared\ConnectEnvelopeBuilder.cs` (~292-330),
`src\OpenClaw.Shared\OpenClawGatewayClient.cs` (hello-ok ~2309-2378, errors
~2603-2690, events ~3255-3320, challenge ~3520-3540),
`src\OpenClaw.Shared\WindowsNodeClient.cs` (invoke ~488-540, result ~645-665),
`src\OpenClaw.Connection\SetupCodeDecoder.cs`. Protocol constants live in
`GatewayProtocolContract` (`MinimumSupportedVersion`, `MaximumSupportedVersion`).

Client to gateway, first frame after socket open (or after challenge):

```json
{ "type": "req", "id": "<guid>", "method": "connect",
  "params": {
    "minProtocol": 3, "maxProtocol": 4,
    "client": { "id": "openclaw-windows-tray|node-host", "version": "...", "platform": "windows",
                "deviceFamily": "...", "mode": "operator|node", "displayName": "..." },
    "role": "operator|node", "scopes": ["..."], "caps": ["..."], "commands": ["..."], "permissions": {},
    "auth": { "token": "..." } | { "bootstrapToken": "..." } | { "deviceToken": "..." },
    "locale": "en-US", "userAgent": "openclaw-windows-tray/<v>|openclaw-windows-node/<v>",
    "device": { "id": "...", "publicKey": "...", "signature": "...", "nonce": "...", "signedAt": ... } } }
```

Gateway to client, optional challenge (client answers with the connect above):

```json
{ "type": "event", "event": "connect.challenge", "payload": { "nonce": "<random>", "ts": 1700000000000 } }
```

Successful handshake. `id` must equal the connect request id.

```json
{ "type": "res", "id": "<connect id>", "ok": true,
  "payload": { "type": "hello-ok", "protocol": 4,
               "auth": { "deviceToken": "<token>", "scopes": ["operator.admin"] },
               "device": { "id": "<echo device id>" },
               "snapshot": { "sessionDefaults": { "mainSessionKey": "agent:main:main" } } } }
```

Fields read by the client: `protocol`, `auth.deviceToken` or `auth.deviceTokens[]`
(entries `{role, deviceToken, scopes}`), `device.id`, `snapshot.sessionDefaults.mainSessionKey`.
Omitting `auth.deviceToken` is valid for direct-token auth.

Pairing required (client then waits for the resolved event and reconnects):

```json
{ "type": "res", "id": "<connect id>", "ok": false,
  "error": { "message": "pairing required", "details": { "code": "PAIRING_REQUIRED", "requestId": "<pair id>" } } }
```

```json
{ "type": "event", "event": "device.pair.resolved", "payload": { "requestId": "<pair id>", "decision": "approved" } }
{ "type": "event", "event": "node.pair.resolved",   "payload": { "requestId": "<pair id>", "decision": "approved" } }
```

Node invocation (gateway to node) and its answer (node to gateway):

```json
{ "type": "event", "event": "node.invoke.request",
  "payload": { "id": "<req id>", "nodeId": "<device id>", "command": "system.run", "paramsJSON": "{\"command\":\"cmd /c echo gui-e2e\"}" } }
```

```json
{ "type": "req", "id": "<guid>", "method": "node.invoke.result",
  "params": { "id": "<req id>", "nodeId": "<device id>", "ok": true, "payload": { ... }, "error": null } }
```

Exec approvals: gateway emits events `exec.approval.requested` /
`exec.approval.resolved` (payload has `id`); the operator client resolves with
`{"method":"exec.approval.resolve","params":{"id":"<approval id>","decision":"allow-once|allow-always|deny"}}`.

Setup code: `Convert.ToBase64String(UTF8({"url":"ws://127.0.0.1:<port>","bootstrapToken":"<token>"}))`.
Limits enforced by the decoder: total ≤ 2048 chars, token ≤ 512 chars.

Post-handshake request storm from the operator client that the fake should
answer with empty-but-valid payloads (`{"type":"res","id":...,"ok":true,"payload":{...}}`):
`health`, `sessions.list`, `node.list`, `usage.status`, `usage.cost`,
`agents.list`, `config.get`, `skills.status`, `node.pair.list`, `cron.list`,
`chat.history`, `sessions.subscribe`. Chat: request `chat.send` (params include
`sessionKey`, `message`); reply with `{"runId":"<guid>","status":"started"}`
then push a `chat` event carrying the assistant message. Return
`{"ok":false,"error":{"message":"unsupported"}}` for anything else so the UI
fails fast instead of hanging. `docs\gateway-protocol-drift-guard.md` describes
how protocol drift is caught; keep fake responders minimal.

## 4. Repo conventions that will bite

- `tests\Directory.Build.props`: `TargetFramework net10.0`, `Nullable`,
  `TreatWarningsAsErrors=true`, `NuGetAuditMode=all`. Any new warning fails the
  build. `System.Drawing.Common` advisory NU1904 arrives transitively through the
  Tray.WinUI reference; add `<WarningsNotAsErrors>$(WarningsNotAsErrors);NU1904</WarningsNotAsErrors>`
  exactly as `tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj` does.
- Do **not** set `<UseWinUI>true</UseWinUI>` in a test project. It turns the
  project into a WinExe and breaks the test host (explained in the UITests csproj comment).
- TFM for anything referencing Tray.WinUI: `net10.0-windows10.0.22621.0`, with
  `<Platforms>x64;ARM64</Platforms>` and `<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>`.
  CI builds with `-r win-x64`; without declared RIDs a `--no-restore` build fails with NETSDK1047.
- First-run gotcha (AGENTS.md): `dotnet test --no-restore` silently no-ops when
  the test `bin\` does not exist yet. Build the test project first or omit `--no-restore`.
- Polling loops and best-effort cleanup need a `// slopwatch-ignore: SW00x <reason>`
  comment (SW003 for best-effort teardown, SW004 for bounded delays). See
  `tests\OpenClaw.Connection.Tests\GatewayConnectionManagerTests.cs:31` and `:1484`.
- Test naming: `*Tests.cs`; real-behaviour proofs use `*ProofTests.cs`.
- xUnit traits are used for lane selection (`[Trait("Category", "Accessibility")]`
  in CI filters). Filter syntax on the CLI: `--filter "Trait=Tier=Fake"`; xUnit
  2.x actually matches `Tier=Fake` as a trait name/value pair, so verify the
  exact filter string against `dotnet test --list-tests` before wiring CI.
- `scripts\run-proof-tests.ps1` is the only permitted way to run proof tests
  from a proof pool. It writes `TestResults\ProofPools\<name>\<name>.trx` and
  fails on zero tests or zero passes. `scripts\validate-proof-pools.ps1:736`
  allows only this command shape:
  `^(?:$env:OPENCLAW_RUN_E2E = '1'; )?.\scripts\run-proof-tests.ps1 -Project '<p>' -Filter '<f>' -ResultName '<kebab>'(?: -RuntimeIdentifier (?:win-x64|win-arm64))?$`
  Extend the env prefix group for `OPENCLAW_RUN_GUI_E2E` and add a case in
  `scripts\test-proof-pool-validator.ps1`.
- No `Directory.Packages.props`; pin package versions directly in the csproj.
  Windows App SDK version comes from the `$(MicrosoftWindowsAppSDKVersion)` property.
- Add the new project to `openclaw-windows-node.slnx`. E2E tests are
  intentionally excluded from the default `dotnet test` at repo root; check how
  `tests\OpenClaw.E2ETests` is handled in the slnx and CI before deciding
  whether the GUI project follows the same pattern (recommended: include it, since
  its always-on `CatalogTests` and `FakeGatewayServerTests` are cheap and the
  GUI scenarios self-skip without the gate).

## 5. Existing AutomationId inventory (what you can use today)

- Hub pages: every page in `AccessibilityScanTests.PageTestData()` has a
  `<Page>PageMarker` except ChatPage, which uses `ChatComposerInput` as marker.
- `Pages\ConnectionPage.xaml` (44): `PendingApprovalsBanner`, `StatusStrip`,
  `ConnectionPageMarker`, `StripPrimaryAction`, `StripTerminalAction`,
  `StripDashboardAction`, `ConnectionToggle`, `GatewayHostOpenTerminalAction`,
  `GatewayHostStartAction`, `GatewayHostStopAction`, `GatewayHostRestartAction`,
  `NodeCard`, `NodeModeToggle`, `NodeTrustApproveCopyAction`, `NodeApproveCopyAction`,
  `NodeReconnect`, `RecoveryCard`, `RecoveryRestartTunnel`, `RecoveryEditTunnel`,
  `RecoveryApplyRepair`, `RecoveryRepairResult`, `RecoveryCopyApprove`,
  `RecoveryConnect`, `RecoveryDisconnect`, `SavedGatewaysCard`, `GatewaysScanAction`,
  `AddGatewayHeaderAction`, `WelcomeAddTilesCard`, `WelcomeInstallLocalGateway`,
  `LobbyDirectTile`, `LobbySetupCodeTile`, `WelcomeScanAction`, `AddGatewayPanel`,
  `AddGatewayBack`, `AddMethodSelector`, `AddDirectUrl`, `AddDirectToken`,
  `AddDirectName`, `AddRemoteHelpLink`, `AddSetupCodeInput`, `AddSetupCodeDecode`,
  `AddInstallLocalGateway`, `AddSecurityAdvice`, `AddSave`.
- `Pages\SettingsPage.xaml` (26, includes `SettingsPageCheckUpdates`),
  `Pages\LocalAiPage.xaml` (21: `LocalAiStart`, `LocalAiStop`, `LocalAiRestart`,
  `LocalAiOpenLogs`, `LocalAiChangeModel`, ...), `Pages\DebugPage.xaml` (14),
  `Pages\PermissionsPage.xaml` (8 static plus `{Binding RemoveRuleAutomationId}`).
  Run `grep -o 'AutomationId="[A-Za-z]*"' <file>` for the full lists.
- Setup wizard: `WelcomePage.xaml` (`WelcomeInstallLocalGatewayChoice`,
  `WelcomeInstallCheckProgress`, `WelcomeLocalAiAvailable`,
  `WelcomeConnectExistingGatewayChoice`, `WelcomeBackButton`, `WelcomeNextButton`),
  `CapabilitiesPage.xaml` (9 `LocalAi*` ids), `CompletePage.xaml`
  (`LocalAiCompletionSummary` only). `SecurityNoticePage`, `AdvancedSetupPage`,
  `ProgressPage` have none.
- Dialogs: `Dialogs\ExecApprovalDialog.cs` sets `ExecApprovalDenyAction`,
  `ExecApprovalAllowAlwaysAction`, `ExecApprovalAllowOnceAction`;
  `Dialogs\PairingApprovalDialog.cs` sets `PairingRejectAction`,
  `PairingLaterAction`, `PairingApproveAction`.
- Tray: `TrayMenuWindow.xaml` has only `TrayMenuPanel`.
- Chat composer (code-built in `Chat\ReactorChatComposer.cs`): `ChatComposerInput`,
  `ChatComposerAttach`, `ChatComposerSpeakerToggle`.

## 6. Validation the repo requires before a PR

From `AGENTS.md` and `.agents\skills\openclaw-proof-validation\SKILL.md`:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

Plus, for this work: build and run the new project, and run
`.\scripts\validate-proof-pools.ps1`, `.\scripts\test-proof-pool-validator.ps1`
and `.\scripts\validate-docs.ps1` whenever `.github\proof-pools.json` or docs change.

PR body must contain `## Required proof pools`, `## Validation` and
`## Real Behavior Proof` (template: `.github\pull_request_template.md`). Select
pool IDs from `docs\PROOF_POOLS.md` or declare `none` with a reason. Never
present a skipped test as validation. Use isolated tray data for any proof run
(`.\run-app-local.ps1 -Isolated -AllowNonMain`).

Architecture rules: read `docs\ARCHITECTURE.md` before touching any listed god
object; `HubWindow.xaml.cs` and `App.xaml.cs` are large, so keep product-side
edits to AutomationIds and `WritePageReady` calls.

## 7. Known unknowns to resolve early

- FlaUI attach to a WinUI 3 window: content lives under a
  `Microsoft.UI.Content.DesktopChildSiteBridge` child; scope searches to the page
  marker's parent to keep `FindFirstDescendant` fast.
- Whether `AutoStart` and hotkey registration are guarded in isolated mode;
  grep `DataDirOverride` in the settings/autostart services before running
  GUI-SET-001 on a shared machine.
- The exact xUnit trait filter string that works with `dotnet test --filter`.
- Crabbox desktop image capabilities (see PLAN.md open items).
