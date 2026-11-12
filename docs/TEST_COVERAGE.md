# Test Coverage Summary

**Historical count snapshot**: 2026-08-06 (source revision not recorded)<br>
**Framework**: xUnit / .NET 10.0<br>
**Snapshot validation status**: passing (`.\build.ps1`, Shared tests, Tray tests)

These dated totals are not current-head validation. Use fresh result reports
for completion claims. The maintained sections below describe suite ownership,
selection, and proof boundaries; they do not imply that every lane ran.

## Required validation suites

These are the suites every agent must run after code changes, as documented in
`AGENTS.md`.

| Suite | Historical runtime result |
|---|---:|
| `OpenClaw.Shared.Tests` | 3,444 total: 3,412 passed, 32 skipped |
| `OpenClaw.Tray.Tests` | 2,165 total: 2,165 passed, 0 skipped |

Runtime totals come from `dotnet test` on 2026-08-06. They are higher than
method counts because some `[Theory]` tests expand into multiple cases.

## Test project inventory

| Project | Primary scope | Test methods at snapshot |
|---|---|---:|
| `OpenClaw.Connection.Tests` | Gateway registry, credential resolution, connection manager/state machine, setup codes, pairing, diagnostics | 452 |
| `OpenClaw.Shared.Tests` | Shared models, gateway client, capabilities, MCP, exec approval, A2UI security, URL handling, notification categorization | 2,373 |
| `OpenClaw.Tray.Tests` | Tray state/UI helpers, settings isolation, onboarding, connection page behavior, localization, local gateway setup/uninstall | 1,717 |
| `OpenClaw.Tray.UITests` | Native WinUI/A2UI control, rendering, and accessibility scan coverage | 89 |
| `OpenClaw.WinNode.Cli.Tests` | Windows node CLI argument parsing, command behavior, JSON output, uninstall flow | 89 |
| `OpenClaw.SetupEngine.Tests` | Setup engine, WSL gateway installation, setup-code, and local setup policy coverage | 387 |
| `OpenClawTray.FunctionalUI.Tests` | Functional UI smoke coverage | 19 |
| `OpenClaw.E2ETests` | Gateway-mediated setup/connect, revocation recovery, and network recovery suites | 21 |
| `OpenClaw.Tray.IntegrationTests` | Real-process tray/MCP integration tests gated by `OPENCLAW_RUN_INTEGRATION=1` | 19 |

The method inventory is a source scan of `[Fact]`, `[Theory]`, and repo custom
xUnit attributes such as `[WindowsFact]`, `[E2EFact]`, `[MxcE2EFact]`,
`[IntegrationFact]`, and `[IntegrationTheory]`. Use `dotnet test` for
authoritative runtime totals.

## Coverage highlights

### Fixture-backed application smoke

The [Gateway fixture harness](GATEWAY_FIXTURE_TESTING.md) adds a middle tier:
the real desktop app and production Gateway/chat stack consume deterministic
synthetic Gateway responses, with no AI or real WSL Gateway. Protocol tests
run in Shared; profile/preflight and concurrent-app tests live in Tray
Integration; native sidebar/240-message/late-history proofs live in Tray UI.
Use `.\scripts\test-gateway-fixture.ps1 -AppPath '<built-app.exe>'` for the
opt-in real-app lane. It rejects skipped or zero-test runs and preserves
per-run artifacts. Existing MCP-only integration defaults remain unchanged.

### Cross-repository candidate compatibility

The reusable [`release-candidate-e2e.yml`](../.github/workflows/release-candidate-e2e.yml)
workflow tests one exact OpenClaw package against an independently identified
Windows app. `windows_node_workflow_sha` pins both the workflow and the maintained
setup/test harness. `windows_node_source_sha` identifies only the app under test:
a verified published ZIP/MSIX in release mode, or a separately checked-out and
built Tray in main mode. Fixing the harness does not rebuild historical releases.

The fixture installs the candidate (including exact beta/alpha versions), waits
for operator/node readiness, and runs
`FullSetup_SafeNodeInvocation_RoutesThroughRealGateway` to invoke `system.which`
through the real Gateway. Current-feature capability inventories and recovery
scenarios remain in ordinary product E2E, not historical-app compatibility.

Success requires an executed, passing test. Only an opted-in lane may accept
`PROTOCOL_MISMATCH`: the initial fixture must observe the exact structured app
error, write its receipt, and fail the selected test with the dedicated exception.
Skipped tests, setup timeouts, unrelated failures, and run-level errors fail the
lane. [`Resolve-ReleaseCandidateE2EOutcome.ps1`](../scripts/Resolve-ReleaseCandidateE2EOutcome.ps1)
owns result admission; its executable regression cases run in fast-validation CI.
Evidence names include the app source kind and release tag or source SHA so
stable, prerelease, and main invocations can upload in the same caller run.

### OpenClaw.Shared.Tests

- **Command output-drain timing** - The unsupported-Windows runner fixture gates launcher release, then requires completion within three seconds of root exit while a descendant retains stdout, stderr, or both. Ten repetitions per case check short output and PID/start-time-bound child cleanup. The nonparallel `MxcOwnership` collection isolates these wall-clock budgets from unrelated tests. Native SDK finalization has separate managed ownership tests and opt-in runtime proof.
- **Fixture process identity** - MXC pipe-fixture metadata, cleanup ownership checks, and the opt-in native MXC descendant-identity check query the observed full executable image through a retained process handle, not the loader's nullable `MainModule` or a requested launch path. `ProcessImagePathTests` checks a real, never-resumed Windows process with no initialized loader view, a different command-line image name, caller-handle lifetime, and explicit native query failure. This deterministically covers unavailable loader metadata; it does not reproduce the timing of a normally scheduled child or prove native MXC containment.
- **Model and display formatting** - activity glyphs, app version display, session labels, gateway usage/node display, channel status, and rich text helpers.
- **Gateway and WebSocket behavior** - gateway client parsing, session keys, WebSocket base handling, URL normalization, local gateway classification, and token sanitization. The signing-identity regression covers native and managed loopback WebSockets with a bounded client-initiated disconnect and a queued health request. The fixture drains application frames already in flight until the client's close frame, replies, and closes its transport under one deadline rather than relying on the client's EOF fallback timer. A separate fixture regression checks the close frame and actual TCP EOF on both transports, including fragmented text and binary data queued before close.
- **Capabilities and MCP** - app/canvas/screen/camera/system capabilities, MCP auth token reset, MCP HTTP server, MCP tool bridge, MXC availability, MXC policy building, and command runners.
- **Exec approval** - legacy policy coverage plus V2 evaluator, input validation, normalization, prompt adapter, routing, coordinator, store, environment sanitizing, and shell-wrapper parsing.
- **A2UI and web bridge** - A2UI capability security, asset hash pinning, web bridge message handling, and channel payload/status tests.
- **Security and localization-adjacent helpers** - HTTP URL validation/risk evaluation, device identity, identity migration, notification categorization, speech language normalization, and non-fatal action handling.

The CI Shared-test step uses the existing five-minute hang watchdog and a
15-minute outer step limit. A hang fails the run and preserves the interrupted
test sequence without collecting a memory dump; it is not retried or skipped.

### OpenClaw.Tray.Tests

The composer lifetime- and voice-disposal reentrancy regressions use dedicated
workers so their bounded reentrancy assertions (`OPENCLAW_TRAY_TEST_REENTRANCY_TIMEOUT_SECONDS`,
default 10 s) measure controller lock ownership rather than shared thread-pool
scheduling latency.

- **Tray UI and state** - app state, menu display/position/sizing, tray tooltip formatting, activity streams, async list loading, diagnostics contracts, markup regressions, and chat timeline/markdown handling.
- **Connection and pairing** - connection manager node connector tests, connection page approval/channel metrics/row state, operator and Windows tray node pairing approval, and gateway action transport.
- **Settings and startup** - settings round-trip/isolation, consent and settings save, auto-start defaults, startup setup state, existing config guard policy, and local setup progress stage mapping.
- **Onboarding and local gateway setup** - onboarding completion/chat bootstrapper/existing config guard, wizard flow/selection/error/step parsing, setup code decoding, local gateway setup diagnostics, uninstall, WSL keep-alive, and auto-pair flags.
- **Localization and resources** - localization key parity, capability page localization, fluent icon catalog coverage, and installer assertion tests.

### Additional projects

Gateway cleanup task registration, refresh, quoting and failure propagation run
in the ordinary SetupEngine suite. The registration fixture replaces only the
persistence boundary and cannot register a real scheduled task. The two desktop
visibility tests use the existing integration opt-in because their positive
control deliberately displays a classic console. Run them on an interactive
Windows desktop without concurrent desktop automation:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
$env:OPENCLAW_RUN_INTEGRATION = '1'
dotnet test .\tests\OpenClaw.SetupEngine.Tests\OpenClaw.SetupEngine.Tests.csproj --no-restore --filter 'FullyQualifiedName~GatewayCleanupTaskTests'
```

The visibility contract is no on-screen visible window or taskbar entry, not
absence of an HWND. WinEvent observations and periodic window sampling cover
startup through worker exit; native UI Automation checks the shell taskbar,
including visible custom-shell surfaces when present. The visible-console positive control
must be detected by both window observers and actual taskbar enumeration.
Unavailable shell/provider access fails the selected proof instead of treating
a window-style heuristic as taskbar evidence. Sampling gaps remain a limit of
the measurement, not proof that an arbitrarily short transient is impossible.

The chat composer's real keyboard regression is opt-in because it uses Windows
`SendInput` on a visible, focused proof window. Run it only on an isolated
interactive desktop with no concurrent computer-use or human input, Caps Lock
off, and a Latin keyboard layout:

```powershell
$env:OPENCLAW_RUN_KEYBOARD_PROOF = '1'
$env:OPENCLAW_TRAY_DATA_DIR = '<dedicated isolated test data directory>'
.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'Category=NativeKeyboard' -ResultName 'composer-keyboard' -RuntimeIdentifier win-x64
```

The tests check foreground ownership before every key sequence and fail rather
than inject into another window. They exercise Shift+Enter at the beginning,
middle, and end, selection replacement, typing after the newline, repeated
newlines, draft synchronization after rendering, zero sends for Shift+Enter,
and exactly one send with no extra newline for plain Enter. Optional
`OPENCLAW_VISUAL_TEST=1` and `OPENCLAW_VISUAL_TEST_DIR=<artifact directory>` save
the active multiline composer through the existing capture harness.
Ordinary CI skips this lane; a skip is not keyboard proof. On a proof host every
selected case must pass with zero skips. This lane does not synthesize an IME
composition: record actual IME commit behavior separately or report it blocked.
Unset the opt-in after the run.

- **OpenClaw.Connection.Tests** keeps connection architecture tests separate from tray UI concerns.
- **OpenClaw.Tray.UITests** covers A2UI/native WinUI rendering behavior that is awkward to validate through pure unit tests.
  Markdown renderer record-shape tests also belong to `UICollection`: their WinRT-backed types must not initialize concurrently with the shared Windows App SDK bootstrap. A smoke-test contract guards both Markdown test classes' collection membership.
- Onboarding AI card-action tests wait up to 10 seconds for the target card to be loaded, enabled, click-enabled, and attached to the page's non-null XamlRoot. Readiness timeouts report each condition; regression tests hold each readiness flag false before releasing it and verify that a permanently blocked card never invokes its action. A dispatcher yield alone is not a control-loaded guarantee.
- Native current-model UIA coverage requires the standard disabled HRESULT (`UIA_E_ELEMENTNOTENABLED`, `0x80040200`) even when the client worker carries unrelated COM error info. It verifies that the same card remains available and mounted without activation, that non-clickable cards do not advertise Invoke, and that enabled native invocation activates the selected model.
- **OpenClaw.WinNode.Cli.Tests** covers the standalone Windows node CLI contract.
- **OpenClaw.SetupEngine.Tests** covers gateway setup and local WSL installation policy.
- **OpenClawTray.FunctionalUI.Tests** covers newer UI surfaces outside the main tray test project.
- **OpenClaw.E2ETests** uses custom `[E2EFact]` / `[MxcE2EFact]` attributes that inherit xUnit `FactAttribute`; CI exercises them with shard filters.
- **OpenClaw.Tray.IntegrationTests** uses custom `[IntegrationFact]` attributes and runs only when `OPENCLAW_RUN_INTEGRATION=1`.
- **PackagingTests** is a legacy PowerShell-script lane under
  `tests\PackagingTests\`, not a dotnet test project.

## Formal validation paths

Use the smallest lane that proves the changed subsystem, but always include the
required closeout lane for code changes.

| Lane | Entry point | Required when |
|---|---|---|
| Required closeout | `.\build.ps1`, Shared tests, Tray tests | Every code change and every agent closeout |
| Fixture-backed app smoke | `.\scripts\test-gateway-fixture.ps1 -AppPath '<built-app.exe>'` | Chat session switching/history/scrolling and populated navigation changes; run both Debug and production-shaped Release for runtime regressions |
| Proof-pool inventory | `.\scripts\validate-proof-pools.ps1`, `.\scripts\test-proof-pool-validator.ps1`, and `.\scripts\test-validate-docs-proof-pool-flow.ps1` | Every inventory or proof scheduling change; the documentation gate runs core schema and parent-flow checks, while CI runs the full malformed-contract matrix |
| Agent skills | `.\scripts\validate-agent-skills.ps1` and `.\scripts\test-agent-skills-validator.ps1` | Changes under `.agents\skills`; validates skill metadata, agent-facing prose, and local links |
| Result and evidence contracts | `.\scripts\test-proof-evidence.ps1` | Changes to report admission, generic proof, CI test wrappers, evidence summaries, or their source/artifact correspondence |
| Agent readiness | `.\scripts\test-agent-readiness.ps1` | Changes to read-only host and optional agent-tool readiness reporting |
| GitHub-hosted PR/main CI | `.github\workflows\ci.yml` | Every pull request and push to `main`; explicit conservative impact outputs select the required test, E2E, and release-publish lanes |
| Accessibility scan | `.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'Category=Accessibility' -ResultName 'winui-accessibility' -RuntimeIdentifier win-x64` | UI changes and CI quality gate; runs real-process Axe.Windows scans; see `docs\ACCESSIBILITY.md` |
| Local E2E | `OPENCLAW_RUN_E2E=1` with `OpenClaw.E2ETests` | Gateway setup/connect, recovery, or pairing changes that need real WSL Gateway coverage |
| Local MXC E2E | `.\scripts\validate-mxc-e2e.ps1` | MXC sandboxing, `system.run`, exec approvals, Windows node command execution, gateway setup/connect changes that affect MXC |
| Product WSL setup validation | `OPENCLAW_RUN_E2E=1` with `OpenClaw.E2ETests.Setup.SetupAndConnectTests` | Tray onboarding/setup-engine changes that must prove the current product WSL install path |
| Installer source checks | `.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj' -Filter 'FullyQualifiedName~InstallerIssAssertionTests' -ResultName 'installer-source'` | Installer source, payload, identity, cleanup, or protocol changes; pair with the clean installer/upgrade pool for runtime claims |
| Migration preservation source checks | `dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --filter FullyQualifiedName~InnoMigrationContractTests` | Protects completion/checker-failure exits and deletion authorization, including unsafe source mutations; not a substitute for real installer proof |
| Migration record and cleanup scripts | `dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj --filter FullyQualifiedName~MigrationRecordTests` | Exercises real Windows PowerShell receipt checks and cleanup lock contention. The subprocess deadline defaults to 30 seconds so a genuine regression fails fast; the cleanup lock-contention theory overrides it to 120 seconds because its failure path writes log and result files that a loaded runner can stall on. Timeout handling attempts process-tree termination before fixture teardown and reports output, script logs, the observed process state, and cleanup failures without replacing the original timeout |
| Migration startup admission | `dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj --filter "FullyQualifiedName~InnoInstallationDetectorTests\|FullyQualifiedName~StoreMigrationStartupCoordinatorTests\|FullyQualifiedName~MigrationStartupRecordReaderTests\|FullyQualifiedName~MigrationVersionPolicyTests"` | Read-only exact Inno discovery, version/architecture admission, pending-record precedence and preservation; pair with packaged preview guidance proof, not a claim of completed migration |
| Store migration helper fixtures | `.\scripts\test-store-migration-test-script.ps1` | Actual helper execution with in-memory registry/version adapters, blocked process commands and test-owned files. Covers complete version compatibility before mutation, native draft/atomic-sibling preservation, explicit unknown-file quarantine, exclusive non-tray lock contention, lock/receipt preservation, opt-in reset, path-specific reparse refusal, relabel/revert drift, WhatIf and failure paths. Runs in Windows fast-validation CI; not installer, signature, Store UI or gateway-preservation proof |
| Disposable Inno to Store migration | `.\scripts\Enable-StoreMigrationTest.ps1` with [`STORE_MIGRATION_TESTING.md`](./STORE_MIGRATION_TESTING.md) | Manual signed-artifact, installer and visible handoff proof on a disposable machine. Select eligible source/target metadata and matching architecture; alpha ProductVersion cannot be fixed by registry relabeling. Helper preflight is not completed migration proof. Record exact artifacts, actual outcomes and preservation evidence, or report the missing artifact/runtime prerequisite as blocked |
| Local AI model fixture | `dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj --filter "FullyQualifiedName~SparseFixtureFileTests\|FullyQualifiedName~LocalAiPortLifecycleTests"` | Test-only sparse files preserve the catalog-sized logical length and readable zeros without model downloads. Checks cover Windows sparse allocation, independent mutation, overwrite refusal, and cleanup. Windows requires a sparse-capable filesystem and fails explicitly if marking the fixture sparse fails; Unix uses the holes created by `SetLength`. Production receipt, size, and hash checks remain unchanged |
| Legacy installer runtime ordering | `.\tests\PackagingTests\Test-InnoUninstallOrdering.ps1` | Deprecated. It targets the old `[UninstallRun]` layout, while current cleanup is owned by `[Code]`; do not use it as current installer runtime proof |

The hosted setup/connect filter also includes
`SetupAndConnectTestsSearchProof.ReleasedGateway_SearchDenialAndDashboardRoutes`.
It reuses the run's disposable WSL Gateway, seeds one synthetic conversation
without calling an AI provider, and exercises the production metadata,
transcript and schema readers. A separately scoped operator connection must
receive `FORBIDDEN` for transcript search. Preinstalled headless Edge uses a
fresh fixture-owned profile to render authenticated Dashboard root and Skills
routes and capture screenshots. Credentials are sent through local CDP, not
browser command-line arguments; only the test's synthetic session is deleted.
Browser profile storage is excluded from collected artifacts; only its
screenshots and bounded proof outcomes are retained.
The browser helper discovers the initial `about:blank` page through the owned
loopback debugger's `/json/list` endpoint and connects directly to that page.
It does not create another target through the browser-level WebSocket. Startup
failures report the stage, elapsed time, process state, stderr character count
and exception type, never raw Edge output or debugger URLs.
The existing fixture owns Gateway and profile teardown. This is a real released
WSL Gateway/browser boundary, not native packaged-Gateway or Store upgrade proof.
The run's `gateway-release.json`, result report and screenshots identify actual
execution; compilation or a locally gated skip is not successful runtime proof.
Local execution still requires the existing WSL E2E runtime authority.

For browser-only startup proof, set `OPENCLAW_RUN_E2E=1` and run
`dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj --filter FullyQualifiedName~SetupAndConnectTestsBrowserStartup`.
This class does not use the setup fixture or provision a Gateway. Its native
smoke owns one temporary Edge process/profile and verifies page evaluation and
PNG capture; discovery tests cover readiness, cancellation and endpoint
isolation. The hosted setup/connect filter includes this class. This is not
authenticated Dashboard route proof.

Capacity-dependent Windows validation is named in
[`PROOF_POOLS.md`](./PROOF_POOLS.md). Pull requests declare the exact pool IDs
they need. A declaration does not claim that the pool ran, and unavailable
proof must remain reported as blocked.

### Choose the local execution target

Choose by the behavior under test, not merely a name containing `Mxc`.
This is testing guidance, not automatic CI migration, host reservation or a new
scheduler. Keep mandatory code-change closeout and every applicable proof pool.

| Behavior to prove | Default target and boundary |
|---|---|
| Build, unit/component logic, pure MXC policy and mocked capabilities | Task-owned host worktree with isolated test settings; no VM needed solely because the code concerns MXC |
| App layout, navigation, chat/history and fixture-covered client behavior | [Isolated app with the deterministic fixture Gateway](GATEWAY_FIXTURE_TESTING.md); cheapest app-only default, not real pairing, node invocation or MXC proof |
| Isolated test builds needing a real native Gateway, including single-instance and multi-instance E2E | [Task-owned builds with named side-by-side development patches](../.agents/skills/native-gateway-from-source/SKILL.md#prepare-an-isolated-test-build) on one authorized, capable Windows 11 client host with Developer Mode; separate packages/sessions/profiles/ports, not one Hyper-V VM per Gateway |
| Integration with an existing Gateway | Explicitly operator-approved endpoint and operations, with a task-owned app profile; discovery is not permission to reconfigure, restart or use a shared runtime |
| Clean-machine/image, signed-package install/upgrade, genuine OS isolation or unavailable host capabilities | Separate reusable local or approved cloud Windows client VM; development patches do not prove signed Store/MSIX lifecycle |
| Required WSL Gateway or MXC `system.run` path | Existing real WSL suite, including `.\scripts\validate-mxc-e2e.ps1` where required, on an authorized capable host/VM; native patch startup does not replace it |

The named-patch default also applies when selecting future cloud native-Gateway
E2E targets, after actual Windows client/session/listener capabilities are
qualified. Do not assume `windows-latest` or Windows Server is MXC-ready or
claim the cloud lane ran. Unsupported or skipped proof remains blocked.

For real native Gateway tests, build Companion in the task-owned worktree and
prepare the Gateway with an explicit unique `-Patch` and task-owned `-WorkRoot`.
Launch that binary with `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH` and separate app
data. `run-app-local.ps1 -Isolated` alone isolates only the app profile; it does
not allocate a unique Gateway and can reuse the shared `npm-latest` slot.
Keep fixture-only app tests on the cheaper synthetic path.

Serialize deployment/cache mutation and initial port selection/startup, then
allow independent already-prepared instances to serve concurrent tests within
host capacity. Package separation does not remove CPU/RAM costs, isolate the OS,
or grant concurrent desktop-input ownership. Use unique task-owned names, explicit
Companion patch selection, distinct app data/endpoints and ownership-checked
cleanup as described in the canonical skill. Never stop another task's runtime
or copy the user's profile/credentials to make a test pass.

### Explain the validation plan

Use the existing classifier in read-only planning mode:

```powershell
.\scripts\Get-CiChangeClassification.ps1 -EventName pull_request `
  -ChangedPaths 'src\OpenClaw.Cli\Program.cs' -Human
.\scripts\Get-CiChangeClassification.ps1 -EventName pull_request `
  -ChangedPaths 'README.md' -Explain
```

For actual branch comparison, pass known `-BaseSha` and `-HeadSha` values.
`-ChangedPaths` is an explicit scenario, not discovery of all current edits.
Include staged, unstaged, and relevant untracked changes when planning an
uncommitted task. Invalid revisions, empty/unknown inputs, and infrastructure
changes remain conservative.

The default JSON stays compatible with CI. `-Explain` adds schema-versioned
path reasons, hosted lanes, mandatory local closeout, and inventory proof
candidates. `-Human` renders the same decisions for a reader. Code changes
still require full build, Shared, and Tray even when hosted lanes are narrower.
The shipped `winnode` reference keeps its CLI/core contract treatment.
Hosted lanes describe impact-selected CI Gate jobs, including release metadata
and MSIX jobs where selected. Event/authorization-conditioned reservation and
publication jobs remain governed by the workflow, not a path-only prediction.

Non-documentation plans list the inventory pools for applicability review.
They are **not automatically required or scheduled**: use each pool's
`requiredWhen`, prerequisites, and `doesNotProve` against the behavior change.
The planner does not run tests, authenticate, launch an app, acquire a host,
or dispatch CI. The [proof skill](../.agents/skills/openclaw-proof-validation/SKILL.md)
owns the evidence checklist.

### CI impact fast paths

Core, Tray, UI, and the three E2E jobs start as soon as classification selects
them, in parallel with `fast-validation`, and keep their cancellation guards.
CI Gate still requires `fast-validation` to succeed, so this removes it from
the critical path without relaxing acceptance. A `fast-validation` failure no
longer skips selected test lanes; their runner time is the accepted cost.
Release asset preparation still waits for `fast-validation`.

The setup/connect, revocation recovery and network recovery E2E jobs each run once
against npm Latest, matching normal WSL setup.
`scripts\Resolve-CiGateway.ps1` resolves Latest once in change-classification and
supplies the exact version to the existing candidate-version setup path, so setup
and assertions use the same version even if the tag moves during a run. No npm
resolution failure falls back to the Store or another channel. Each job's artifact
includes `gateway-release.json` with the npm selector, exact version, integrity and
tarball URL. Resolution runs only when E2E is selected, without downloading an MSIX
or consulting native packaging releases. Every selected E2E job contributes to the
existing CI Gate.

These are real WSL Gateway-to-Windows-node lanes, not native MSIX runtime proof.
There is no duplicate Packaged WSL lane and no native Latest/Packaged runtime matrix.
Native unit/helper tests and upstream package build/install checks are not substitutes
for live isolated-session startup, pairing and recovery on a capable Windows client.
Native developer builds use the verified npm archive for Latest, or released app
bytes and the release packaging commit for Packaged. `scripts\test-gateway-npm-release.ps1` covers
channel selection, source/Store overrides, exact pins, archive integrity and
provenance without network access or changing installed packages.
`scripts\test-gateway-packaged-release.ps1` covers release/tag resolution, embedded
payload identity, asset digest, extraction safety and inventory-verified cache reuse.

The `change-classification` job emits explicit booleans for `core_tests`,
`tray_tests`, `ui_tests`, `setup_e2e`, `revocation_e2e`, `network_e2e`,
`x64_release`, `arm64_release`, and `full`. Jobs consume those outputs directly.
The summary classification is `docs_only`, `fast_only`, `targeted`, or `full`; it is not the
authority for individual job conditions.

`fast_only` applies only to the exact repository-tooling paths
`.github/scripts/repository-triage.cjs` and
`.github/scripts/repository-triage.test.cjs`, optionally mixed with safe
documentation. These files serve repository reports and ownership-label
maintenance, not the shipped product, and their Node tests run in the
always-on `fast-validation` job. All product and release lane outputs remain
false; tooling code is not classified as documentation. This is not an
allowlist for other scripts, actions, workflows, extensions, or untested tools.

Mixed tooling and product changes retain the union of product lanes, including
the WinNode skill's core-test requirement. Unknown paths and build, workflow,
project, dependency, classifier, or gate infrastructure still select `full`.
Non-PR events still require full validation including ARM64.
`fast-validation` and `proof-pool-contracts` must both succeed even for
`fast_only`. The proof-pool selector is unchanged: any changed proof boundary
still runs its regression matrix, including when mixed with allowlisted tooling.
Hosted timing savings are not verified until a tooling-only PR exercises this path.

The classifier uses conservative project boundaries:

| Change surface | Core / CLI | Tray / setup / integration | UI / accessibility | Setup E2E | Recovery E2E | Release publish |
|---|---:|---:|---:|---:|---:|---:|
| Maintained docs or non-executable agent-skill content |  |  |  |  |  |  |
| `OpenClaw.Cli`, `OpenClaw.WinNode.Cli` | Yes |  |  |  |  |  |
| Tray WinUI, including pure service logic |  | Yes | Yes |  |  |  |
| Chat, FunctionalUI, XAML, resources, pages, controls |  | Yes | Yes |  |  |  |
| SetupEngine, onboarding, setup, pairing |  | Yes | When UI-bound | Yes |  |  |
| Connection, gateway, node, MCP, WSL paths | As applicable | Yes | When UI-bound | Yes | Yes |  |
| Broad Shared or protocol changes | Yes | Yes | Yes | Yes | Yes |  |
| Test-only change | Corresponding lane | Corresponding lane | Corresponding lane | Corresponding shard | Corresponding shard |  |
| Packaging, project/build files, installer, workflow or classifier contracts | Yes | Yes | Yes | Yes | Yes | x64 |
| Push to `main` or a tag | Yes | Yes | Yes | Yes | Yes | x64 and ARM64 |

Known mixed changes take the union of their lanes. Any unknown path, a mixed
change containing an unrecognized path, invalid or unavailable revisions, an
empty diff, workflow/build infrastructure, project/package metadata, installer
input, or classifier/contract change selects `full` fail-closed. A full pull
request runs all test and E2E lanes plus the x64 publish smoke. Pushes to `main`
and tags also run ARM64 publish.

The former serialized Windows `test` job is split into three clean-runner lanes.
Each lane restores and builds its own minimal project graph and publishes a
separate TRX artifact:

| Lane | Projects |
|---|---|
| `core-tests` | Shared, Connection, WinNode CLI |
| `tray-tests` | Tray, SetupEngine, Tray Integration |
| `ui-tests` | FunctionalUI, Tray UI, Axe.Windows accessibility |

The core lane uses a dedicated runner-temp NuGet package directory and a key
scoped to its complete transitive project graph plus the SDK, NuGet, and imported
MSBuild manifests that affect restore. It has no fallback to the repository-wide
cache. Tray, UI, E2E, and release lanes retain the existing repository-wide
NuGet cache behavior. Only downloaded NuGet packages are cached; build outputs,
test results, source, SDK installs, and Node packages remain uncached. Tray
settings remain isolated through `OPENCLAW_TRAY_DATA_DIR`, integration tests
retain `OPENCLAW_RUN_INTEGRATION=1`, UI tests install WindowsAppRuntime, and
every test project continues to publish TRX output.

The Tray UI step uses a five-minute VSTest hang watchdog and a fifteen-minute
outer step timeout. A hung test fails the step and leaves a test-sequence
artifact for diagnosis instead of waiting for GitHub's six-hour job limit.
Memory dumps are disabled because they can contain credentials or user data.
Other `Invoke-CiTest.ps1` callers retain their existing timeout behavior unless
they opt in with `-HangTimeoutSeconds`.

`fast-validation` still runs for every invocation and owns repository hygiene,
documentation, agent-skill, classifier, gate, workflow, and release-ordering
contracts. The heavyweight malformed proof-pool matrix moved to the independent
`proof-pool-contracts` job. Its existing decision helper still fails closed,
but ordinary product code no longer waits for the matrix. The three existing
E2E shards are unchanged in scope and have separate job conditions; no E2E
shards were added.

### WSL latest versus packaged native Gateway proof

The WSL E2E fixture installs npm `latest` by default.
`OPENCLAW_E2E_GATEWAY_VERSION` selects explicit candidate validation, not a native
package test. Running the packaging repository's pinned npm version in WSL does
not prove Microsoft Store acquisition, package identity, MXC session setup, or
the packaged Gateway lifecycle.

Both E2E tray fixtures read `gateways.json` through `GatewayRegistryFile`, which
joins the production `PersistenceFileLease` before opening the file. This keeps
fixture reads out of an in-flight atomic replacement without retrying IO or
hiding malformed JSON. The fixture-free `SetupAndConnectTestsRegistryReads`
regressions cover writer/reader ordering, concurrent saves and error propagation;
they do not replace the real WSL Gateway setup/connect proofs.

Actual package E2E needs a disposable Windows account/host with a successful
MXC session capability probe and a verified installable MSIX from
[`openclaw-windows-packaging`](https://github.com/openclaw/openclaw-windows-packaging).
Record the packaging commit, release policy, payload commit/version, architecture,
package name/family/publisher/version, and verified artifact digest. An older
development registration or unsigned Store-submission archive is not proof of
the current released package.

The current packaging contract owns an agent-session Gateway through
`clawctl setup` and `clawctl gateway-service start/status/restart/stop`.
Configuration belongs inside that session's profile. Companion's existing
host-child native runtime and same-user listener ancestry checks are a different
contract and cannot be relabeled as proof of the isolated package. The integration
must demonstrate authenticated Companion connectivity and ownership/lifecycle
behavior against the real session endpoint before claiming native E2E coverage.
Missing package provenance, installation consent/isolation, capable runner, or
session endpoint integration is a blocker, not a passing skip or an npm fallback.

Investigation snapshot (2026-09-24): packaging commit
`3f2e3268ca0961a08a12e31a25b0c2c174548955` selects upstream `2026.9.4` and MSIX
revision 4 (`2026.9.404.0`) with the Store identity
`OpenClawFoundation.OpenClawGateway`. Its
[producer run](https://github.com/openclaw/openclaw-windows-packaging/actions/runs/36033664419)
failed verified-release-tag admission and produced no artifacts; the corresponding
release was unavailable. The published revision-3 package uses the old identity,
so it cannot stand in for revision-4 Store proof. The hosted Windows image in that
run was build 26100, below packaging's MXC fallback minimum 26340.9212, with no
successful session capability proof. A matching signed package, a disposable
session-capable test account/runner, and the Companion lifecycle integration
described above remain prerequisites. The existing developer package/profile
must not be replaced or repurposed to make this test pass.

Release metadata is produced by the small `metadata` job only when release
publish validation is required. It preserves GitVersion `semVer`,
`majorMinorPatch`, `isPrerelease`, `isStableCorrection`, and stable-correction
tag validation. The x64 and ARM64 publish jobs depend only on classification
and metadata, so they can run in parallel with tests and E2E. Ordinary product
pull requests produce no release artifact. Packaging/build/release-sensitive
pull requests run only x64 publish smoke; main and tags run x64 plus ARM64.

MSIX packaging also consumes the readonly preview or the single official
tagged-release reservation before starting its x64/ARM64 matrix.
`scripts\test-msix-versioning.ps1` covers allocation arithmetic, durable state,
retry/idempotence, competing claims, provenance, preview isolation, and failure
boundaries without modifying remote refs.
`scripts\test-msix-preview-source-version.ps1` covers stable and numeric
correction tags, the canonical upstream Latest API request, fail-closed release
validation, and token-safe errors. Artifact and alpha-staging contracts verify
that the actual package versions and metadata match the selected allocation.
`test-ci-workflow-contract.ps1` executes the actual preview/write context
guards, including fork, PR, branch, tag, cancellation, and failure cases, and
requires fork previews to read the canonical upstream reservation ledger.

The always-running **CI Gate** validates every classifier output against the
corresponding job result. A required lane must succeed, an unrequired lane must
be skipped, and classification, fast validation, proof-contract selection,
missing outputs, cancellation, or unexpected results fail closed.

#### Timing and runner-minute model

The pre-split full PR baseline is run `33815204229`: the serialized `test` job
took 22 minutes 11 seconds, including 6 minutes 48 seconds for the malformed
proof-contract matrix and 3 minutes 28 seconds for accessibility. Existing E2E
shards took 14 minutes 43 seconds, 9 minutes 33 seconds, and 11 minutes 7
seconds. x64 and ARM64 publishes took 5 minutes 13 seconds and 6 minutes 17
seconds. The merged docs/skill fast path is run `33819700640` at 51 seconds.

Using those step baselines, expected pull request critical paths are:

| Change class | Expected wall-clock | Expected runner cost relative to old full PR |
|---|---:|---:|
| Docs / agent-skill prose | About 1 minute | About 2% |
| CLI-only | 5 to 7 minutes | About 10% |
| Tray logic | 8 to 12 minutes | About 25% |
| UI / XAML | 8 to 12 minutes | About 25% |
| Setup / connection / Shared | 10 to 16 minutes, governed by required E2E | About 55% to 85% |
| Build or workflow infrastructure | 10 to 16 minutes | Within 15% of the old full runner total |

The full test split duplicates checkout, SDK setup, and cache restore, but removes
the serialized proof matrix from product paths. Estimated full-infrastructure
runner minutes remain below the 30% rejection threshold, while ordinary code
latency falls to the longest required lane instead of the sum of every lane and
release publish.

## Writing a regression test

### Fixed file-worker owner checks

`OpenClaw.FileWorker.Tests` exercises the production native filesystem owner
in-process on isolated repository-local test directories. It does not launch
Companion, the worker executable, MXC, a Gateway, or an installer. Run it on
Windows:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
dotnet test .\tests\OpenClaw.FileWorker.Tests\OpenClaw.FileWorker.Tests.csproj
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --filter 'FullyQualifiedName~FileTransfer'
```

The core CI lane restores, builds and runs the worker suite alongside Shared,
Connection and winnode tests. It collects a separate FileWorker TRX report.
The fixed worker has no native speech dependency and uses the SDK's
self-contained .NET runtime. Its manifest generator validates both direct and
delayed PE imports and fails if a payload acquires an external VC runtime
dependency. Companion's native speech runtime requirements remain unchanged.
Before inventory generation, it removes PDB debug symbols from the worker
publish directory, including stale symbols from earlier publishes. Build-output
symbols remain available separately. This keeps the copied payload and its
exact manifest consistent across MSIX (which strips PDBs), Inno and portable
distributions for both configurations and worker RIDs. `FileWorkerManifestTests`
checks symbol removal, repeat generation and preservation of every runtime
file and digest at this build boundary. Actual MSIX archive inventory and
embedded-manifest checks remain a separate packaging acceptance boundary.
Release worker EXE/DLL signatures are verified before manifest hashing.
`FileWorkerManifestTests.ReleaseManifestRejectsUnsignedWorkerBeforeWritingDigests`
checks rejection for both RIDs; workflow contracts verify the signed input reaches
portable/Inno and MSIX builds without republishing the worker. These checks do
not prove Azure signing or installed-package acceptance.
Companion packages each worker payload in a manifest-addressed RID slot, and its
embedded manifest selects that exact slot. Obsolete dependencies in a prior
slot are neither loaded nor silently adopted. Resolver tests retain full
digest/inventory checks inside the selected slot. Installer-shaped protected
LocalAppData sources exercise contained preparation into the executor-private
persistent code cache without relaxing source denies or expanding data rights.
Native metadata admission and hardlink fixtures use extended-length Windows
paths, so long worktrees do not depend on the machine's `LongPathsEnabled`
policy. The metadata regression also denies file-data reads to verify that
long-path support preserves attributes-only admission.

The resolver and MXC reparse tests create real symbolic links. Run these tests
with symbolic-link privileges (Developer Mode or an elevated test process);
without those privileges they fail during fixture creation. Do not replace
their alias-rejection assertions with skips or change machine policy
automatically.
Source and cached-file pins, full digests/inventories, atomic publication,
tamper/alias rejection, bounded inventory and failure/cancellation ownership
are covered at the shared owner boundary. The legacy per-operation-copy
negative control fails the repeated-success regression on distinct executable
directories; the cache keeps the same published native file identity across
calls. Ordinary test-owned subprocess fixtures exercise concurrent builders,
abrupt parent death after publication, real preparation interruption, restart
backpressure and distinct manifest versions. Their fixed 64 MiB dependency
fixture gives interruption a real copy boundary without launching any code
from the payload. They use the existing resolver filesystem constructor, not
a fake trust or permission implementation. These tests do not prove power-loss
durability, MXC containment or installed-worker runtime loading.
Active-failure regressions observe nonempty output during that real 64 MiB
copy, then cancel or place a native byte-range read lock on the pinned source.
They prove same-version retry after identity-backed unpublished rollback and
that a real copy I/O error survives the executor as a cache-specific command
failure. Separate failure-first fixtures inject added, replaced, hardlinked or
reparse children, and a create obstacle containing an independently identified
foreign marker. They do not also request cancellation: verification may observe
tampering before a later cancellation, and that failure must remain a typed
cache cleanup failure. Cancellation-first cases hold a real apphost verification
open pending on a native file oplock. They add a foreign child, retain a
non-delete-sharing read handle, or independently change the native EA, then
request cancellation before acknowledging the pending open. These cases prove
both the original cancellation token and the distinct cleanup refusal, without
depending on mutation-versus-cancellation scheduling or supplying a fake owner
result. Cleanup must retain the exact foreign, replacement, alias and locked
file identities and bytes, and reject execution on retry. A changed native EA
blocks rollback of otherwise owned children, rather than deleting an entry
whose publication is no longer disproven. A matching no-obstacle control
requires ordinary cancellation, successful rollback and same-version retry;
the oplock fixture itself must not manufacture a cleanup failure.
Post-publication cancellation uses a read adapter over real pinned streams.
The fixture independently observes the published native EA and matches the
stream's full identity before holding read completion. A handshake requests
cancellation before verification resumes, without replacing publication or the
digest. Assertions require the original token, no inner exception, unchanged
entry and child identities, and verified reuse by the default reader. A separate
no-cancellation control proves that the held read can complete successfully.

A read-only child handle held across completion
must not block publication or weaken the lease's write/delete veto. The
corresponding pre-fix owner case failed with the directory-rename publisher.
Other owner cases inspect the non-executable initial EA in an unowned legacy
root, reject complete but unpublished code, and reject added, tampered or
identically replaced children without cancellation. Abrupt process death at
directory creation must leave that initial veto, never a recordless legacy
fallback. These ordinary in-process failure cases are
distinct from abrupt parent death, whose persisted preparation remains blocked.
Upgrade regressions advance through thirteen independently versioned manifests,
retain eight real resolver leases, and run simultaneous new-version builders
against an old pinned lease. Native identity checks cover added, replaced,
hardlinked, reparse, sparse and missing children, caller JSON ownership claims,
corrupt/transplanted native records, replaced entry directories and a copied root
record with the wrong native identity. Unknown legacy roots are never adopted.
Real Windows readonly-deletion failures exercise both first-apphost rejection
(unchanged inventory and metadata) and partial deletion with a persisted
`deleting` record across subprocess exit. A same-version resolver must finish an
owned interrupted deletion before publishing a fresh complete entry, without
requiring another upgrade. Readonly or actively held remaining children block
that recovery without recreating the apphost or changing the remaining inventory.
Both same-version recovery and later-version admission reject a substituted
remaining child. These are native disposition failure/restart tests, not manufactured SDK
termination receipts, forced mid-instruction process death or power-loss proof.

`WorkerCacheProcessTests` launches the actual built FileWorker apphost with
`--stdio`, reaches the binary fetch listener, and retains no resolver lease.
It independently demonstrates that a nonshared read/delete open can succeed
while the worker is live, yet non-POSIX image disposition returns Windows error
5. Capacity admission must preserve the complete live entry, permit verified
reuse, and reclaim it only after the actual worker exits. The ordinary fixture
uses the framework-dependent apphost; current self-contained archive execution,
installed activation and native MXC policy remain separate proof boundaries.
The worker test project's aliased Shared reference exercises the real resolver
without conflating its types with the worker's linked wire contracts.
Its cache is isolated under the system temp directory rather than the worktree
because apphost launch has a separate Windows process-path limit. Process
evidence uses `RUNNER_TEMP` in CI and the owned fixture directory when that
variable is absent locally.
Concurrent file/system.run requests, private-data exclusion under broad write
grants, caller override rejection and logical/physical private denies are
covered separately from immutable sensitive-root protection.

The worker suite covers native bindings, retained handles, no-clobber creation,
in-place overwrite identity, preflight non-mutation, duplex start/ACK/EOF
semantics, gzip-tar interoperability, data-only policy and retained-upload
cleanup. Native regressions cover no-follow junction listing with whole-archive
rejection, final-parent ACL inheritance with a synthetic SID, private staging
and uploads, atomic EA creation, interrupted create/delete phases, cancellation,
replacement/unknown-content preservation and complete legacy-record migration.
These interruption fixtures close real native handles in adverse parent/child
order; they do not kill a worker process or prove power-loss recovery. Native EA
support through the installed MXC worker remains a separate runtime boundary.
Shared transport fixtures cover generic Gateway casing/depth and large results,
explicit file serialization failures on both invoke envelopes, unchanged generic
MCP response sizes, bounded deep file requests, and remembered-only retention
scheduling. Shared tests use independent upstream contract literals for base64,
bindings and metadata, plus the private process codec and typed semantic
outcome. Shared executor tests hold a real Windows directory handle to prove
that transient scratch sharing violations settle before completion; a
persistently locked directory produces a bounded deferred-cleanup warning instead of
holding an execution slot indefinitely. Later admission retries only the
ended operation whose native identity/ancestry it still owns. Cleanup
regressions preserve live/unrelated/replaced/reparse targets, and enforce
the 16-lease backpressure bound. An uncertain launch/finalization retains
its payload pins until owner shutdown, without authorizing deletion.
Scratch parent-process crash recovery remains unimplemented; a later process
never adopts old operation directories. Published code persists by design and
is reverified, never automatically replaced or pruned. Uncertain preparation
is non-executable and consumes cache capacity; it is not guessed safe to delete.
See [the cache and cleanup contract](WINDOWS_NODE_TESTING.md) for admission
bounds and safe repair requirements. Neither ordinary owner suite establishes SDK
interception, real installed-distribution loading, native ARM64 execution, Gateway/MCP
framing, or visible Sandbox status.
Those remain distinct build, transport and runtime proof boundaries.
The opt-in `MxcRuntimeNamespaceNativeTests` adds actual SDK deny/child-grant
and overlapping-policy proof, with owned process identities and fixture ACL
checks. It is not whole-app or installed-worker proof.
The two original cases use PowerShell `-File`. A separately named
`NativeSdk_InlinePayloadControlExercisesTheSameNamespaceAssertions` diagnostic
case sends the same synthetic script and literal arguments through
`-EncodedCommand`, retaining the same direct-SDK policy, readiness handshake,
deny/scratch assertions and ACL checks. It does not set execution policy,
replace a failed file-based case, or establish script-file authorization.
Run and report it alongside the original cases when investigating startup
`AuthorizationManager` failures. Inline success with file-based failure narrows
the failing boundary; it is not overall namespace or FileWorker acceptance.
The artifacts identify `LaunchMode` and use a distinct inline-control prefix.
Ordinary `MxcNamespaceLaunchTests` protect the unchanged file argv and literal
inline payload construction without launching PowerShell or MXC.

CLI `FileWorkerProcessTests` add the concrete process boundary: the production
MCP listener, file capability, shared executor, private binary protocol and
actual compiled worker are called through `winnode` in isolated test folders.
All eight commands, streaming create/fetch, create-exists and semantic failures
are exercised. The test deliberately selects the positive-unsupported host
adapter; it does not assert that the current machine lacks MXC support and
does not launch Companion, MXC, a Gateway or an installer. Payload trust is
covered separately by resolver/packaging checks, not by the test resolver.

### Regression authoring

Use the [test-audit skill](../.agents/skills/test-audit/SKILL.md) when adding or
reviewing tests. Start with the observable contract, its production owner, a
credible regression, and the risk not covered by existing tests.

- Assert independent expected values, not values computed by the same helper.
  A mock should control external input, not supply the result being asserted.
- For a bug fix, show the test failing for the intended pre-fix defect and
  passing after repair, or explicitly report unavailable baseline proof.
  A fixture setup failure is not the negative control.
- Prefer one faithful owner regression. Add another layer only for a distinct
  transport, persistence, permission, lifecycle, or rendering risk.
- Reuse `TempDirectory`, `EnvironmentScope`, `CliHarness`, and the
  [Gateway fixture](GATEWAY_FIXTURE_TESTING.md) rather than introducing duplicate
  state or process hosts. Bind test listeners to loopback.
- Keep legitimate independent static contracts. WinUI source-shape guards
  protect concrete prohibited patterns and require the
  [ledger's retirement condition](ARCHITECTURE.md#how-to-use-this-document).
  The shipped winnode-to-capability registry check is not disposable.
- A snapshot is not a rendering assertion. The fixture's session/history
  tests observe the selected mounted controls and visible text; they still
  do not prove live Gateway pairing, provider behavior, installer state, or MXC.
- Preserve serial native-input/desktop constraints. Measure cost when making
  a performance claim; do not hide a race with sleeps, blind retries, weaker
  assertions, or blanket skips.

### Accepting result reports

`Invoke-CiTest.ps1` and `run-proof-tests.ps1` use invocation-specific result
directories. Their expected TRX must be fresh, parseable, internally consistent,
and contain actual executed tests; successful process exit alone is insufficient.
File modification times and embedded TRX invocation times permit one second
of clock granularity tolerance. Reports older than that remain stale; each run
still uses a fresh invocation directory and validates the embedded timestamps.
Generic custom proof requires every selected result to pass. Ordinary CI may
report supported skipped cases alongside executed passing tests, but all-skipped
or zero-execution runs fail. Existing hosted MXC skip policy is not native MXC
proof.
Skipped counts come from actual result rows and must match total minus executed.
VSTest may leave its aggregate `notExecuted` counter at zero even when skipped
rows are present; a contradictory nonzero aggregate still fails validation.

See [local execution evidence](PROOF_POOLS.md#validate-local-execution-evidence)
for inventory-bound summaries and their assurance limits.

## Running tests

Native reveal tests require Windows client-area animations to be enabled.
The required `AnimationPreferenceEnabled` CI selection establishes that
preference in `Invoke-CiTest.ps1 -AnimationPreference Enabled -RequireAllTestsPassed`,
not in production code or the testhost. Ordinary UI tests keep their original
Windows preference. The parent records and verifies the
original value, holds a per-user/session cross-process lock, starts the test
subprocess with the verified preference, and restores/read-checks the original
even when the testhost fails. It never writes the persistent profile setting.
Restoration or prerequisite failures fail the command. Ordinary Tray UI wrapper
invocations hold the same lock without mutating the preference; unrelated managed
suites are not serialized. This coordinates cooperating wrappers, not direct
`dotnet test` commands or unrelated Windows processes.
Do not terminate the parent wrapper while it owns the preference. A force-killed
parent cannot restore it, so recover the recorded original or a known-good
setting before proceeding. Detectable mutex abandonment fails closed, but if the
last handle disappeared the next owner can create a fresh mutex; this is not
durable crash recovery.

The separate required `PageHeroAnimationsDisabledTests` step runs with
`-AnimationPreference Disabled -RequireAllTestsPassed`; its environment opt-in
keeps it out of ordinary runs, while missing, skipped or failed selected proof
cannot pass that step. The six animation-enabled regressions retain their
native assertions and are not skipped. Local or self-hosted use additionally
requires explicit `-AllowUserAnimationPreferenceChange`; app-data isolation
does not isolate this Windows logon-session preference. After building, an
authorized local focused run can use:

```powershell
.\scripts\Invoke-CiTest.ps1 -Project tests\OpenClaw.Tray.UITests `
  -Runtime win-arm64 -ResultsDirectory TestResults\PageHero `
  -TrxFileName PageHero.trx -Filter 'FullyQualifiedName~PageHeroTests' `
  -AnimationPreference Enabled -AllowUserAnimationPreferenceChange -RequireAllTestsPassed
```

```powershell
# Required validation after code changes
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\scripts\validate-proof-pools.ps1
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore

# All local-dev tests in the solution. E2E is intentionally excluded from the
# solution and runs in CI before merge; run it locally only when explicitly needed.
dotnet test

# Explicit local E2E run
$env:OPENCLAW_RUN_E2E = "1"
dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj -r win-x64

# Formal MXC validation path. This sets the required integration/E2E env vars
# itself and fails when MXC proofs skip unless -AllowSkip is explicitly supplied.
.\scripts\validate-mxc-e2e.ps1

# Accessibility scan, matching the CI quality gate.
dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj -r win-x64 --filter Category=Accessibility

# Single project
dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj

# Specific test class
dotnet test --filter "FullyQualifiedName~MenuDisplayHelperTests"

# Verbose output
dotnet test --logger "console;verbosity=detailed"
```

In a fresh worktree, run the project once without `--no-restore` or build it
first so `dotnet test --no-restore` cannot no-op before `bin\` exists.

Test-owned TCP listeners must bind only to loopback addresses. A successful
wildcard bind from `testhost.exe` can trigger a per-worktree Windows Defender
Firewall consent dialog and block unattended validation. Tests for production
LAN-bind conflict handling should occupy loopback first so the production
wildcard bind fails without opening a network-reachable listener.

## Not fully covered by automated tests

- Real shell tray hover/click behavior against Explorer.
- Full live gateway/node pairing against a remote gateway.
- Long-running soak behavior for reconnects, high-frequency activity updates,
  and memory usage over multi-day sessions.
- Manual visual acceptance for complex WinUI surfaces where screenshot
  comparison would be brittle.

For these gaps, affected changes must include the manual UI/MCP smoke described
in `AGENTS.md` and `.agents/skills/openclaw-proof-validation/SKILL.md`: launch
the tray from the current worktree, use computer-use / desktop automation for
visible WinUI paths, and validate local MCP with `winnode --list-tools` plus the
changed command when node capabilities are involved.

When node command surfaces change, include
`OpenClaw.WinNode.Cli.Tests` in focused validation because `SkillMdDriftTests`
guards the capability registry, MCP descriptions, and `winnode` skill reference
from drifting apart.
