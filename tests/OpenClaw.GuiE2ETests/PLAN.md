# Plan: Automated GUI end-to-end testing of OpenClaw Windows Companion on Azure Windows

## Context

The repo already has strong non-GUI E2E coverage (`tests\OpenClaw.E2ETests`, real WSL gateway, driven over local MCP) and a real-process accessibility lane (`tests\OpenClaw.Tray.UITests\AccessibilityAppFixture.cs`) that launches `OpenClaw.Tray.WinUI.exe`, navigates via `openclaw://hub/<tag>` deep links and inspects the UI through UI Automation. What is missing is user-level GUI testing: nobody clicks buttons, types into fields, or asserts what a user sees. The proof pool `windows-winui-interactive` in `.github\proof-pools.json` still relies on a **manual** "exercise-changed-ui" step, and there is no automated Windows host provisioning; `.agents\skills\crabbox\SKILL.md` documents leasing Azure Windows hosts via the external Crabbox CLI but nothing in CI uses it.

Goal: a new GUI E2E test project driven by **FlaUI (UIA3)**, scenarios authored as **C# xUnit + page objects** with a **scenario catalog** so tests can be added incrementally, a **two-tier gateway** strategy (fake in-process WebSocket gateway for every run; real WSL gateway for nightly/on-demand), and an orchestration layer that runs the suite on **Azure Windows VMs leased through Crabbox** with an interactive desktop, wired into GitHub Actions.

Decisions already made by the user: Crabbox leases, FlaUI, two-tier gateway, C# xUnit + page objects + catalog.

## Verified ground truth (drives the design)

- Onboarding shows when `RequiresSetup(_settings)` or `OPENCLAW_FORCE_ONBOARDING=1` (`src\OpenClaw.Tray.WinUI\App.xaml.cs:839`). Seeding `EnableMcpServer=true` skips the wizard (what `AccessibilityAppFixture` does); an empty data dir triggers it.
- Isolated instances do not register the URI scheme; navigation is done by spawning a second `OpenClaw.Tray.WinUI.exe <uri>` with the same `OPENCLAW_TRAY_DATA_DIR` (`AccessibilityAppFixture.NavigateAsync`). Page readiness comes from `AccessibilityNavigationSignal.WritePageReady` (env `OPENCLAW_ACCESSIBILITY_NAVIGATION_SIGNAL`) plus `<Page>PageMarker` AutomationIds on 18 hub pages (inventory in `AccessibilityScanTests.PageTestData`).
- `OPENCLAW_UI_AUTOMATION=1` keeps the tray flyout from auto-dismissing (`TrayMenuWindow.xaml.cs:219`).
- Dialogs already have ids: `ExecApprovalDenyAction/AllowAlwaysAction/AllowOnceAction` (`Dialogs\ExecApprovalDialog.cs:182-203`), `PairingRejectAction/LaterAction/ApproveAction` (`Dialogs\PairingApprovalDialog.cs:153-169`). ConnectionPage has 44 ids (`AddGatewayHeaderAction`, `LobbyDirectTile`, `LobbySetupCodeTile`, `AddDirectUrl/Token/Name`, `AddSetupCodeInput/Decode`, `AddSave`, `ConnectionToggle`, `NodeModeToggle`, `StatusStrip`, `NodeReconnect`, `RecoveryApplyRepair`, ...).
- Gateway protocol the fake must speak (`src\OpenClaw.Shared\OpenClawGatewayClient.cs`, `ConnectEnvelopeBuilder.cs`, `WindowsNodeClient.cs`): optional `connect.challenge` event; `{"type":"req","method":"connect",...}` answered by `{"type":"res","ok":true,"payload":{"type":"hello-ok","protocol":4,...}}`; pairing-required is `ok:false` with `details.code = "PAIRING_REQUIRED"` then `device.pair.resolved`/`node.pair.resolved` events; node commands arrive as `node.invoke.request` events answered with `node.invoke.result`; exec approvals are `exec.approval.requested`/`exec.approval.resolve`. After hello-ok the client fires `health`, `sessions.list`, `node.list`, `usage.status`, etc. Reusable basis: `LoopbackGatewayServer` in `tests\OpenClaw.Shared.Tests\GatewayProtocolLiveRoundTripTests.cs` (`OnMethod`, `WaitFrameAsync`).
- `scripts\run-proof-tests.ps1` rejects zero-test and zero-pass runs. `scripts\validate-proof-pools.ps1:736` only allows the `$env:OPENCLAW_RUN_E2E = '1'; ` prefix for proof-test commands; it must be extended for `OPENCLAW_RUN_GUI_E2E`.
- No `Directory.Packages.props`; package versions go in the csproj. `tests\Directory.Build.props` sets net10.0 + TreatWarningsAsErrors.
- Crabbox: `warmup --desktop` is the only way to get an interactive desktop, and it must be requested at lease creation. SSH-run scripts execute in a non-interactive session, so UI tests must be launched into the desktop session.

## Deliverable 1: new test project `tests\OpenClaw.GuiE2ETests`

**csproj** mirrors `tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj`: TFM `net10.0-windows10.0.22621.0`, `Platforms x64;ARM64`, `RuntimeIdentifiers win-x64;win-arm64`, no `UseWinUI`, `FrameworkReference Microsoft.WindowsDesktop.App`, `WarningsNotAsErrors NU1904`. Packages: `FlaUI.Core`, `FlaUI.UIA3`, `Microsoft.WindowsAppSDK $(MicrosoftWindowsAppSDKVersion)`, `Microsoft.Windows.SDK.BuildTools`. ProjectReferences: `src\OpenClaw.Tray.WinUI` (puts the exe beside the test assembly), `src\OpenClaw.Shared`, `src\OpenClaw.Connection`, `tests\OpenClaw.TestSupport`, `tests\OpenClaw.E2ETests` (add `InternalsVisibleTo("OpenClaw.GuiE2ETests")` in `tests\OpenClaw.E2ETests\AssemblyInfo.cs` to reuse `E2ESetupFixture`, `FakeOllamaServer`, `McpClient`, `E2ETestGate`). `xunit.runner.json` with `parallelizeTestCollections=false`. Add to `openclaw-windows-node.slnx`.

**Folders**

- `Harness\`
  - `GuiE2EGate.cs`: `[GuiE2EFact]` (needs `OPENCLAW_RUN_GUI_E2E=1`), `[GatewayGuiE2EFact]` (also `OPENCLAW_RUN_E2E=1`), same "1|true" pattern as `E2EFactAttribute.cs`.
  - `Traits.cs`: `Tier` (`Fake`|`Gateway`), `Workflow` (`Smoke`,`Onboarding`,`Connection`,`Pairing`,`NodeMode`,`Sandbox`,`Settings`,`LocalAi`,`Chat`,`Tray`,`ExecApproval`,`Recovery`,`Updates`), `Pool`, `Quarantine`.
  - `GuiApp.cs`: the fixture. Port of `AccessibilityAppFixture` + `IsolatedTrayInstance`: isolated data/localappdata dirs, seeded `settings.json` and optional `gateways.json`, env (`OPENCLAW_TRAY_DATA_DIR`, `..._APPDATA_DIR`, `..._LOCALAPPDATA_DIR`, `OPENCLAW_MCP_PORT`, `OPENCLAW_SUPPRESS_EXTERNAL_BROWSER=1`, `OPENCLAW_SKIP_UPDATE_CHECK=1`, `OPENCLAW_LANGUAGE=en-US`, `OPENCLAW_UI_AUTOMATION=1`, `OPENCLAW_ACCESSIBILITY_NAVIGATION_SIGNAL`, optional `OPENCLAW_FORCE_ONBOARDING=1`), stdout/stderr capture, `FlaUI.Core.Application.Attach(pid)` with `UIA3Automation`, window finders (`HubWindow()`, `SetupWindow()`, `TrayMenuWindow()`, `ChatWindow()`, `FindDialog(id)`) by process id + title/marker, `NavigateAsync(tag, pageName, markerId)`, `SendDeepLinkAsync(uri)`, `RestartAsync()` for persistence scenarios, `Mcp` (`McpClient`) for `app.status` polling as a second oracle, `EnsureAlive()` reading `crash.log`.
  - `Wait.cs`: `Until(cond, timeout, interval, description)` with last-observed-state in failure message. No sleeps in tests.
  - `ArtifactSink.cs`: per-test folder `TestResults\GuiE2E\<runId>\<Class>.<Method>\`; `CaptureWindow(window, name)` via FlaUI `Capture` with the blank-image guard from `AccessibilityAppFixture`; UIA tree dump + redacted logs on failure; copies only `*.log/*.jsonl`, never `settings.json`, `gateways.json`, `device-key*`, `gateways\` (rules from `E2ESetupFixture.ShouldCopyArtifactFile` / `SanitizeForLog`).
  - `GuiScenarioBase.cs`: `IAsyncLifetime` base owning `GuiApp` + optional `FakeGatewayServer`; `RunAsync(body)` captures evidence on failure and rethrows.
  - `SettingsFile.cs`, `GatewaysFile.cs`: typed on-disk readers with `WaitForValueAsync` (saves are async); port `ReadActiveGatewayRecord`.
- `FakeGateway\`
  - `FakeGatewayServer.cs`: HttpListener WebSocket server on loopback, multi-connection (operator + node), records frames (`ReceivedFrames`, `WaitForMethodAsync`), `OnMethod`, `Broadcast(event, payload)`, `DropAllConnections()`, `Pause()/Resume()`, `RequirePairing` + `ApprovePending()`, `AcceptedTokens`, `SetupCode` (base64 `{url,bootstrapToken}` per `SetupCodeDecoder`), strict mode that fails on unknown methods.
  - `FakeGatewayProtocol.cs`: builders for `connect.challenge`, `hello-ok` (protocol 4, deviceToken, scopes, `snapshot.sessionDefaults.mainSessionKey`), `PAIRING_REQUIRED`, `health`, `chat.send` ack + canned `chat` event, `node.invoke.request` for `system.run`, `exec.approval.requested`.
  - `FakeGatewayDefaults.cs`: empty-but-valid responders for the post-hello request storm (`health`, `sessions.list`, `node.list`, `usage.status`, `usage.cost`, `commands.list`, `agents.list`, `channels.status`, `config.get`, `subscribe`).
  - `FakeGatewayServerTests.cs`: always-on `[Fact]`s exercising the fake with the real `OpenClawGatewayClient` (mirror of `GatewayProtocolLiveRoundTripTests`).
- `Pages\`: page objects (`HubShell`, `ConnectionPage`, `SettingsPage`, `PermissionsPage`, `SandboxPage`, `LocalAiPage`, `ChatPage`, `DebugPage`, `TrayMenu`, `Setup\SecurityNoticePage|WelcomePage|AdvancedSetupPage|CapabilitiesPage|ProgressPage|CompletePage`, `Dialogs\PairingApprovalDialog|ExecApprovalDialog|CommandPalette`). Each finds elements by `cf.ByAutomationId`, exposes intent methods (`AddDirectGateway(url, token)`), returns only after readiness signal or UIA condition. `AutomationIds.cs` holds all id constants.
- `Scenarios\`: one class per workflow (`OnboardingScenarios.cs`, `GatewayConnectionScenarios.cs`, `PairingScenarios.cs`, `NodeModeScenarios.cs`, `SandboxScenarios.cs`, `SettingsScenarios.cs`, `LocalAiScenarios.cs`, `ChatScenarios.cs`, `TrayScenarios.cs`, `ExecApprovalScenarios.cs`, `RecoveryScenarios.cs`, `UpdateScenarios.cs`, `Gateway\RealGatewayOnboardingScenarios.cs` in a `[Collection("RealGateway")]` sharing `E2ESetupFixture`).
- `Catalog\`: `gui-e2e-catalog.json`, `gui-e2e-catalog.schema.json`, `CatalogTests.cs` (always-on): every `[GuiE2EFact]`/`[GatewayGuiE2EFact]` has exactly one entry with matching `testId`, traits equal `tier/workflow/pool`, every listed AutomationId exists as a literal under `src\**` (grep from `OPENCLAW_REPO_ROOT`), quarantined entries carry `quarantineIssue`.

## Deliverable 2: scenario catalog format

```json
{ "$schema": "./gui-e2e-catalog.schema.json", "schemaVersion": 1,
  "scenarios": [{
    "id": "GUI-CONN-001",
    "testId": "OpenClaw.GuiE2ETests.Scenarios.GatewayConnectionScenarios.AddDirectGateway_ConnectsAndPersists",
    "title": "Add gateway by direct URL and token, connect",
    "workflow": "Connection", "tier": "Fake", "pool": "windows-winui-interactive", "shard": "connection",
    "automationIds": ["AddGatewayHeaderAction","LobbyDirectTile","AddDirectUrl","AddDirectToken","AddSave","StatusStrip"],
    "preconditions": ["isolated data dir, EnableMcpServer=true, no gateways.json", "FakeGatewayServer with AcceptedTokens=[test-token]"],
    "steps": ["navigate hub/connection", "click AddGatewayHeaderAction", "..."],
    "expected": ["StatusStrip = Connected", "gateways.json active url = fake url", "fake received connect with auth.token"],
    "evidence": ["screenshot:connection-connected", "trx", "fake-frames.jsonl"],
    "owner": "windows-node-maintainers", "addedIn": "0.1.0",
    "quarantine": false, "quarantineIssue": null }] }
```

Consumers: `CatalogTests.cs` (parity), `scripts\gui-e2e\Get-GuiE2EShards.ps1` (emits a JSON matrix of `shard` → xUnit `--filter` for CI), `scripts\gui-e2e\Export-GuiE2ECatalog.ps1` (regenerates the scenario table in `docs\GUI_E2E_TESTING.md` between `<!-- gui-e2e-catalog:begin/end -->` markers; `scripts\validate-docs.ps1` fails when stale), PR template hint under `## Required proof pools`.

## Deliverable 3: initial scenario set

| ID | Scenario | Tier | Key ids | Assertions (UIA + disk + fake frames + screenshot) |
|---|---|---|---|---|
| GUI-ONB-001 | First run, connect existing gateway via setup code | Fake, `OPENCLAW_FORCE_ONBOARDING=1` | new `SecurityNoticeContinue`, `WelcomeConnectExistingGatewayChoice`, `WelcomeNextButton`, new AdvancedSetup ids, `CompleteLaunchButton` | Setup window closes, Hub opens, gateways.json has fake URL, fake saw `connect` with `bootstrapToken`, `app.status` Connected |
| GUI-ONB-002 | First run, install local gateway | Gateway | `WelcomeInstallLocalGatewayChoice`, new `ProgressPageMarker`, `CompletePageMarker` | Progress steps succeed, real gateway Connected via `WaitForConnectionReady` |
| GUI-CONN-001 | Add gateway by direct URL+token | Fake | `AddGatewayHeaderAction`, `LobbyDirectTile`, `AddDirectUrl/Token/Name`, `AddSave`, `StatusStrip` | connect frame has `auth.token`; active record on disk; Connected |
| GUI-CONN-002 | Add gateway by setup code | Fake | `LobbySetupCodeTile`, `AddSetupCodeInput`, `AddSetupCodeDecode`, `AddSave` | decode shows URL; connect uses bootstrapToken; device key file exists (not copied) |
| GUI-CONN-003 | Connect/disconnect toggle | Fake | `ConnectionToggle`, `StatusStrip` | socket closes / new connect frame; `app.status` flips |
| GUI-PAIR-001 | Pairing approval dialog approve / reject | Fake `RequirePairing` | `PairingApproveAction`, `PairingRejectAction` | dialog appears; approve → `device.pair.resolved` → reconnect with deviceToken; reject → not paired |
| GUI-NODE-001 | Node mode + capability toggles persist | Fake | `NodeModeToggle`, PermissionsPage toggles | settings.json flags; node connect `caps`; survives `RestartAsync` |
| GUI-SBX-001 | Sandbox policy change | Fake | new `SandboxEnabledToggle`, `SandboxScopeExpander` | settings key changes; status title; persists |
| GUI-SET-001 | Settings toggles persist across restart | Fake | SettingsPage ids | `AutoStart`, theme, `GlobalHotkeyEnabled` on disk = UIA state after restart |
| GUI-LAI-001 | Local AI page with `FakeOllamaServer` | Fake (may become Gateway) | `LocalAiStart/Stop/Restart/ChangeModel` | status transitions; fake Ollama request count > 0 |
| GUI-CHAT-001 | Chat send + canned reply | Fake | `ChatComposerInput`, new `ChatComposerSend`, new transcript ids | fake sees `chat.send`; reply text visible |
| GUI-CHAT-002 | Quick send via `openclaw://agent?message=` | Fake | same | fake receives `chat.send` with message |
| GUI-TRAY-001 | Tray menu open + navigate | Fake | `TrayMenuPanel`, new `TrayMenu*` ids | `openclaw://tray` opens menu; click Settings → `SettingsPageMarker` |
| GUI-EXEC-001 | Exec approval allow once / deny | Fake | `ExecApprovalAllowOnceAction`, `ExecApprovalDenyAction` | fake sends `node.invoke.request system.run`; result frame ok with stdout / denied |
| GUI-REC-001 | Network recovery | Fake | `StatusStrip`, `NodeReconnect` | `DropAllConnections`+`Pause` → reconnecting; `Resume` → new connect frames, Connected within 60 s |
| GUI-UPD-001 | Check-for-updates semantics | Fake | `SettingsPageCheckUpdates` | no update dialog at startup in isolated mode; clicking shows graceful InfoBar offline |

Smoke subset (`Workflow=Smoke`, per PR, ≤5 min): launch+navigate, CONN-001, SET-001, CHAT-001.

## Deliverable 4: product-side changes in `src`

- Add AutomationIds: `SecurityNoticePage.xaml` (`SecurityNoticePageMarker`, `SecurityNoticeContinue`), `AdvancedSetupPage.xaml` (marker, inputs, `AdvancedSetupNext/Back`), `ProgressPage.xaml` (marker, steps panel), `CompletePage.xaml` (marker, `CompleteLaunchButton`, `CompleteStartupToggle`), `SetupWindow.xaml` (`SetupWindowRoot`), `SandboxPage.xaml` (marker if missing, `SandboxEnabledToggle`, `SandboxScopeExpander`), `TrayMenuWindow.xaml` (`TrayMenu<Action>` per button), chat composer/transcript (`ChatComposerSend`, `ChatTranscriptList`, message items with `Name` = text), `CommandPaletteDialog.xaml`, dialog roots (`ExecApprovalDialogRoot`, `PairingApprovalDialogRoot`), `ChatWebViewHost` on WebView2 host borders (asserted present, never traversed).
- Extend `AccessibilityNavigationSignal.WritePageReady` calls to setup wizard page transitions and dialog show (`Setup:WelcomePage`, `Dialog:ExecApproval`, `Dialog:Pairing`, `Tray:Menu`). Same env var, no new hook.
- Verify isolated-mode guards: AutoStart registry write and hotkey registration must no-op when `DataDirOverride != null`; add guard if missing.
- `scripts\validate-proof-pools.ps1:736`: allow prefix `(?:\$env:OPENCLAW_RUN_(?:E2E|GUI_E2E) = '1'; )*`; add a case to `scripts\test-proof-pool-validator.ps1`.
- `tests\OpenClaw.E2ETests\AssemblyInfo.cs`: `InternalsVisibleTo`.

## Deliverable 5: Crabbox orchestration (`scripts\gui-e2e\`)

Interactive-session problem: `crabbox run` executes over SSH in a non-interactive session; WinUI windows are not visible to UIA there and screenshots are black. Chosen mechanism: **scheduled task in the desktop session, created from SSH, polled via a completion marker** (`schtasks /Create /SC ONCE /RU <desktop-user> /IT /RL HIGHEST` then `/Run`; poll `TestResults\GuiE2E\done.json`). Fallback if `/IT` is refused on the image: `crabbox desktop launch ... -- powershell -File scripts\gui-e2e\Invoke-GuiE2E-Remote.ps1` and poll the same marker.

- `Test-GuiE2EHost.ps1`: hard prerequisite checks (WebView2 runtime registry key, .NET 10 SDK, an Active interactive session via `query user` and `explorer.exe`, current `SessionId` equals it unless `-SkipSessionCheck`, resolution ≥1600x900, DPI 96, `LogonUI` not running, `OPENCLAW_REPO_ROOT`). Writes `host-report.json`. Never skips.
- `Invoke-GuiE2E.ps1` (local + remote runner): `-Tier Fake|Gateway|All -Filter -RuntimeIdentifier -NoBuild`; builds tray + test project; sets gates; calls `scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.GuiE2ETests\OpenClaw.GuiE2ETests.csproj' -Filter 'Trait=Tier=Fake' -ResultName 'gui-e2e-fake' -RuntimeIdentifier win-x64`.
- `Invoke-GuiE2E-Remote.ps1`: runs inside the desktop session, tees to `remote.log`, writes `done.json {exitCode,started,finished}` in `finally`.
- `Invoke-GuiE2E-Crabbox.ps1` (controller): resolve `$Crabbox` per SKILL.md; `crabbox warmup --provider azure --target windows --windows-mode normal --desktop --keep --idle-timeout 90m --ttl 240m --timing-json` (or `-LeaseId`); `crabbox run --id <lease> --preflight --timing-json --script-stdin` with remote script: host check (`-SkipSessionCheck`), build once, register + run scheduled task, poll `done.json` up to `-TimeoutMinutes`, print `remote.log`, delete task; second `crabbox run` tars `TestResults\GuiE2E` + `TestResults\ProofPools\gui-e2e-*` back (or `crabbox results` if supported); `finally { crabbox stop }` unless `-KeepLease`. Never print `crabbox config path` contents or `az login` output.
- `Get-GuiE2EShards.ps1`, `Export-GuiE2ECatalog.ps1` (see Deliverable 2).

## Deliverable 6: CI

- New `.github\workflows\gui-e2e.yml`: triggers `workflow_dispatch` (`tier`, `filter`, `keep_lease`), nightly `schedule` `0 3 * * *`, `pull_request` labeled `gui-e2e`. Job on `windows-latest`, `permissions: id-token: write`, `concurrency: gui-e2e-azure`, `timeout-minutes: 150`. Steps: checkout, setup-dotnet 10, `azure/login@v3` (OIDC), obtain crabbox, `crabbox azure login --location ${{ vars.CRABBOX_AZURE_LOCATION }}`, `crabbox doctor`, `Invoke-GuiE2E-Crabbox.ps1`, TRX step summary (per-outcome counts, failed scenario ids), `upload-artifact gui-e2e-results` (`if: always()`), final `if: always()` `crabbox stop` by lease id from step output.
- `ci.yml`: add `gui-e2e-smoke` job on `windows-latest` (same restore/build as `e2etests`), `OPENCLAW_RUN_GUI_E2E=1`, filter `Trait=Workflow=Smoke|FullyQualifiedName~GuiE2ETests.Catalog|FullyQualifiedName~FakeGatewayServerTests`, 25 min, TRX zero-test guard, upload `TestResults\GuiE2E`. Runs per PR (the Accessibility lane already proves UIA on the real exe works on `windows-latest`). Full fake tier stays nightly/label-triggered on Crabbox until two weeks of green nightlies.
- `.github\proof-pools.json`: add `run-gui-e2e` proof-test command to `windows-winui-interactive` (`$env:OPENCLAW_RUN_GUI_E2E = '1'; .\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.GuiE2ETests\OpenClaw.GuiE2ETests.csproj' -Filter 'Trait=Tier=Fake' -ResultName 'gui-e2e-fake' -RuntimeIdentifier win-x64`), keep `exercise-changed-ui` manual for uncovered paths; add `run-gui-e2e-gateway` (`Trait=Tier=Gateway`, both env prefixes) to `windows-wsl-gateway-e2e`. Update `docs\PROOF_POOLS.md`.

## Deliverable 7: docs

New `docs\GUI_E2E_TESTING.md` (architecture, how to run locally, Crabbox lane, generated scenario table, extension guide, flakiness policy, evidence rules). Update `docs\TEST_COVERAGE.md`, `docs\PROOF_POOLS.md`, `DEVELOPMENT.md`, `AGENTS.md` (targeted validation rule: UI changes run `Invoke-GuiE2E.ps1 -Tier Fake` or declare `windows-winui-interactive`), `.github\pull_request_template.md` hint.

Extension guide (goes in the doc): 1) add/verify AutomationIds in `src` (PascalCase `<Surface><Element>[Action|Toggle|Input|Marker]`, stable across locales, never user content) and `Pages\AutomationIds.cs`; 2) add/extend page object with intent methods; 3) write scenario with gate attribute + `Tier/Workflow/Pool` traits inside `RunAsync`, one named screenshot minimum; 4) add catalog entry, run `CatalogTests` and `Export-GuiE2ECatalog.ps1`; 5) optionally tag `Workflow=Smoke` (cap 5 min). Flakiness: no in-test retries; explicit timeouts; ≥2 failures in last 10 nightlies without a product bug → `Quarantine=true` + issue, excluded from proof filters (`&Trait!=Quarantine=true` equivalent), fix or delete within 2 weeks. Evidence follows `windows-winui-interactive.evidencePolicy`: app-window capture only, never raw settings/gateways/device files, `SanitizeForLog` on logs and fake frames.

## Phased rollout

- **Phase 0, spike (2-3 days):** project skeleton, `GuiApp` attaches via FlaUI, one scenario (navigate to `hub/connection`, marker visible, non-blank screenshot). Run locally and in a scratch workflow on `windows-latest`. Confirm UIA tree exposes AutomationIds under the WinUI `DesktopChildSiteBridge`.
- **Phase 1 (~2 weeks):** `FakeGatewayServer` + its tests; AutomationIds in `src`; page objects for Hub/Connection/Settings/Chat/dialogs; CONN-001/002/003, SET-001, CHAT-001; catalog + `CatalogTests`; `gui-e2e-smoke` job. Verify 3 consecutive green local runs of `Invoke-GuiE2E.ps1 -Tier Fake`, smoke green in CI, and that `run-proof-tests.ps1` fails when the gate is unset (negative check).
- **Phase 2 (~2 weeks):** Crabbox scripts + scheduled-task mechanism, `gui-e2e.yml`, tier 2 ONB-002 on `E2ESetupFixture`. Verify nightly produces TRX + screenshots from the VM, `host-report.json` shows session/DPI checks passed, `crabbox list` shows no leaked leases.
- **Phase 3 (~2 weeks):** remaining scenarios, docs, proof-pool integration. Verify `scripts\validate-proof-pools.ps1`, `scripts\test-proof-pool-validator.ps1`, `scripts\validate-docs.ps1` pass.

## Verification (end to end)

1. `dotnet test tests\OpenClaw.GuiE2ETests` with no gate → only `CatalogTests` + `FakeGatewayServerTests` run; GUI scenarios skipped with reason.
2. `.\scripts\gui-e2e\Invoke-GuiE2E.ps1 -Tier Fake` locally → all fake-tier scenarios pass, `TestResults\ProofPools\gui-e2e-fake\gui-e2e-fake.trx` non-empty, per-scenario screenshots present and non-blank.
3. `.\scripts\gui-e2e\Invoke-GuiE2E-Crabbox.ps1 -Tier Fake` from a workstation with `az login` → lease warmed with desktop, host report passes, TRX + screenshots downloaded, lease stopped.
4. `gui-e2e.yml` `workflow_dispatch` run → artifact `gui-e2e-results` contains TRX, PNGs, redacted logs; step summary lists scenario ids.
5. PR with an intentionally removed AutomationId → `gui-e2e-smoke` and `CatalogTests` fail.

## Open items (need owner input, not invented)

- Crabbox binary distribution for CI (`vars.CRABBOX_DOWNLOAD_URL` + token, or private release asset) and which Azure federated identity may create VMs (likely new `AZURE_GUI_E2E_*` secrets, distinct from the release signing identity); `vars.CRABBOX_AZURE_LOCATION`.
- Whether the `--desktop` image allows `schtasks /IT` for the autologon user, its lock-screen policy and default DPI/resolution, and whether WSL2 is enabled inside the normal-mode desktop image (needed for tier 2 with a desktop; otherwise tier 2 GUI onboarding is limited to connect-existing against a gateway on a second WSL2 lease).
- Local AI page may need a WSL-backed runtime; LAI-001 may move to tier 2 or need an endpoint override.
- Update-check scenario depends on an update-feed override existing in `UpdateCoordinator`; otherwise only the offline negative is testable.
- Fake gateway protocol drift: keep responders minimal, run strict mode nightly, cross-reference `docs\gateway-protocol-drift-guard.md`.
