---
name: openclaw-testing
description: Choose proportional OpenClaw Windows node tests, builds, packaging checks, and runtime proof, then diagnose failures without weakening required gates.
---

# OpenClaw Windows testing

Use this skill to select and interpret validation for this repository. It covers
the Windows node, Companion WinUI app, setup engine, Gateway protocols, WSL
integration, packaging, installers, and release artifacts.

It does not authorize app launches, WSL or package changes, remote hosts, CI
dispatch, publication, or cleanup. Use [test-audit](../test-audit/SKILL.md) when
writing or reviewing tests, [proof validation](../openclaw-proof-validation/SKILL.md)
for runtime evidence, [Crabbox](../crabbox/SKILL.md) for authorized remote
Windows execution, and
[native Gateway from source](../native-gateway-from-source/SKILL.md) for native
Gateway registration or compatibility work.

## Start with the changed contract

1. Record the repository path, branch, full HEAD, intended base and SHA,
   ahead/behind counts, and dirty-file count.
2. Identify the production owner and its existing tests. Read the applicable
   architecture guide from [AGENTS.md](../../../AGENTS.md#architecture-context-for-new-agents).
3. Include staged, unstaged, and relevant untracked paths. A committed-diff-only
   plan is incomplete for local work.
4. Use the classifier as a read-only starting point, then add behavioral proof
   that path classification cannot infer:

```powershell
.\scripts\Get-CiChangeClassification.ps1 -EventName pull_request `
  -ChangedPaths 'src\OpenClaw.Connection\GatewayConnectionManager.cs' -Human
```

For a committed comparison, pass known `-BaseSha` and `-HeadSha` values instead
of `-ChangedPaths`. The classifier selects hosted lanes and the mandatory local
code-change closeout. Its proof-pool output is a candidate inventory, not
authorization or completed evidence.

If the source tree changes while validation is running, the affected result is
stale. Rebuild and rerun against the new exact tree.

## Use the proportional ladder

Choose the [execution target](../../../docs/TEST_COVERAGE.md#choose-the-local-execution-target)
before any authorized runtime proof. Fixture-covered app behavior stays on the
deterministic fixture. Isolated test builds that need a real native Gateway
default to the [task-owned build and named-patch workflow](../native-gateway-from-source/SKILL.md#prepare-an-isolated-test-build),
including single-instance tests. Build Companion in its own worktree, prepare a
unique patch with a task-owned work root, and select it explicitly with isolated
app data. The launcher's `-Isolated` switch alone does not isolate a Gateway
build or allocate a unique instance. Use separate VMs for OS/clean-image
isolation or missing host capabilities; keep required real WSL/MXC proof.

Start at the smallest boundary that can fail for the intended regression. Move
up when the change crosses another owner, transport, persistence boundary,
architecture, or distribution format. Code changes still end with the required
closeout even when focused tests pass.

### 1. Documentation and skill checks

For ordinary documentation-only changes:

```powershell
.\scripts\validate-docs.ps1
git diff --check
```

For changes under `.agents\skills`, also run:

```powershell
.\scripts\validate-agent-skills.ps1
.\scripts\test-agent-skills-validator.ps1
```

Do not launch the app or claim runtime behavior from documentation validation.
The shipped `winnode` skill is an exception: changes to its command contract
also require the CLI/core tests named in
[AGENTS.md](../../../AGENTS.md#ui-mcp-and-pr-proof).

### 2. Focused owner tests

Use `dotnet test <project> --filter '<expression>'` for a class or behavior,
then run relevant siblings when shared setup or lifecycle could be involved.

| Owner or risk | Focused project |
|---|---|
| Shared models, Gateway client, MCP, capabilities, approvals, A2UI security | `tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj` |
| Gateway registry, credentials, connection state, pairing, protocol signatures | `tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj` |
| Tray state, settings, onboarding helpers, localization, installer source contracts | `tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj` |
| Setup pipeline, WSL install policy, rollback, setup-code and provider workflow | `tests\OpenClaw.SetupEngine.Tests\OpenClaw.SetupEngine.Tests.csproj` |
| `winnode` parsing, JSON output, command descriptions and uninstall flow | `tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj` |
| Native WinUI, Reactor, A2UI rendering and accessibility | `tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj` |
| Functional UI surfaces | `tests\OpenClawTray.FunctionalUI.Tests\OpenClawTray.FunctionalUI.Tests.csproj` |
| Real-process tray and local MCP | `tests\OpenClaw.Tray.IntegrationTests\OpenClaw.Tray.IntegrationTests.csproj` |
| Gateway-mediated setup, recovery, pairing and invocation | `tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj` |

Examples:

```powershell
dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj `
  --filter 'FullyQualifiedName~PairingFlowTests'

dotnet test .\tests\OpenClaw.SetupEngine.Tests\OpenClaw.SetupEngine.Tests.csproj `
  --filter 'FullyQualifiedName~GatewayWizardRestartRecoveryPolicyTests'

$env:OPENCLAW_RUN_INTEGRATION = '1'
try {
  dotnet build .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj `
    -c Debug -r win-x64
  dotnet test .\tests\OpenClaw.Tray.IntegrationTests\OpenClaw.Tray.IntegrationTests.csproj `
    -r win-x64
} finally {
  Remove-Item Env:\OPENCLAW_RUN_INTEGRATION -ErrorAction SilentlyContinue
}
```

Use temporary settings directories or set `OPENCLAW_TRAY_DATA_DIR` before the
test process starts. For real tray/setup proof, also isolate
`OPENCLAW_TRAY_APPDATA_DIR` and `OPENCLAW_TRAY_LOCALAPPDATA_DIR`. Do not let
tests read or mutate the normal user profile. Tray Integration launches a real
app process, so it requires runtime authority even though its profile is
isolated. It locates existing tray build output rather than receiving the app
through a project reference, so rebuild the app from the current tree first.

### 3. Required local closeout for code

Every code change and every agent code-change closeout requires:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

In a fresh worktree, build each test project first or omit `--no-restore` on the
first run. A quick successful exit with no executed tests is not a pass. Inspect
the test totals or TRX.

Add the affected owner project even when it is not one of the two required
suites. For `winnode`, MCP output, or command-reference changes:

```powershell
dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore
```

Settings-profile, app-identity, or sandbox-containment changes also need the
DevBuild variant used by CI:

```powershell
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj `
  -c Debug -r win-x64 --no-restore -p:DevBuild=true `
  --filter 'FullyQualifiedName~SettingsManagerIsolationTests|FullyQualifiedName~AppRefactorContractTests.SandboxPage_'
```

### 4. WinUI and application-boundary proof

Pure view models and projections belong in focused unit tests. Mounted controls,
Windows App SDK behavior, accessibility, keyboard input, and visible rendering
need the UI suite or an interactive proof host.

Run the inventory-owned `windows-winui-interactive` accessibility proof command
with the repository wrapper:

```powershell
.\scripts\run-proof-tests.ps1 `
  -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' `
  -Filter 'Category=Accessibility' `
  -ResultName 'winui-accessibility' `
  -RuntimeIdentifier win-x64
```

This proof launches the real app, requires runtime authority and an available
Windows desktop, and emits inventory-bound evidence. CI runs the same
accessibility category through its own prebuilt test-lane command.

For deterministic real-app chat and navigation behavior, build the exact app and
use the fixture. Runtime regressions require both Debug and production-shaped,
non-Dev Release runs:

```powershell
.\build.ps1
$debugApp = '.\src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\win-x64\OpenClaw.Tray.WinUI.exe'
.\scripts\test-gateway-fixture.ps1 -AppPath $debugApp

.\build.ps1 -Project WinUI -Configuration Release
$releaseApp = '.\src\OpenClaw.Tray.WinUI\bin\Release\net10.0-windows10.0.22621.0\win-x64\OpenClaw.Tray.WinUI.exe'
.\scripts\test-gateway-fixture.ps1 `
  -AppPath $releaseApp -Configuration Release -Screenshots
```

Use `win-arm64` on a native ARM64 host. The fixture proves the real app,
Gateway client, chat stack, history loader, local MCP, and mounted native UI
against synthetic loopback data. It does not prove live pairing, providers,
node command execution, WSL, native Gateway/MXC, installation, or update.

For current-head visual, permission, native-input, or diagnostic claims, use the
`windows-winui-interactive` proof pool and the proof-validation skill. A state
snapshot or UI Automation result alone does not prove unclipped rendered pixels.

### 5. WSL, pairing, protocol, and node invocation

Connection and pairing changes need focused `OpenClaw.Connection.Tests` plus the
appropriate real boundary. Setup, bootstrap, WSL lifecycle, revocation, and
network recovery use the E2E project. Local E2E creates a run-scoped WSL distro
and run-specific startup registrations, so it requires runtime authority and
should run only when explicitly needed:

```powershell
$env:OPENCLAW_RUN_E2E = '1'
try {
  dotnet build .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj `
    -c Debug -r win-x64
  dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj -r win-x64 `
    --filter 'FullyQualifiedName~OpenClaw.E2ETests.Setup.SetupAndConnectTests'
} finally {
  Remove-Item Env:\OPENCLAW_RUN_E2E -ErrorAction SilentlyContinue
}
```

Use `RevocationAndRecoveryTests` or `NetworkRecoveryTests` in the filter when
that is the changed contract. These lanes install and exercise the npm-latest
WSL Gateway path. They locate existing tray build output rather than receiving
the app through a project reference, so rebuild it from the current tree first.
They are not native MSIX Gateway proof.

For app-owned WSL installation diagnostics, the non-mutating preflight is:

```powershell
.\scripts\validate-wsl-gateway.ps1 -Scenario PreflightOnly
```

`UpstreamInstall` launches the tray, installs or reuses the shared product
`OpenClawGateway` distro, and stops matching WSL keepalive processes. Its WSL
state is not isolated by the run-specific AppData directories. It requires
explicit runtime authority and an owned disposable host. `FreshMachine` and
`Recreate` additionally unregister `OpenClawGateway`, require
`-ConfirmDestructiveClean`, and require explicit destructive authority.

Changes to MXC sandboxing, `system.run`, approvals, Windows command execution,
or setup/connect behavior affecting MXC require:

```powershell
.\scripts\validate-mxc-e2e.ps1
```

The script fails skipped MXC proofs by default. `-AllowSkip` documents an
incapable host only and is not merge validation for MXC-related work.

Every new Windows node command also needs live local MCP discovery and invocation
through `winnode`, plus the real Gateway path when the command is
Gateway-mediated. Follow
[Windows node testing](../../../docs/WINDOWS_NODE_TESTING.md) and the
[proof skill](../openclaw-proof-validation/SKILL.md).

### 6. Build, architecture, packaging, and installer proof

`.\build.ps1` is the normal repository build. Use its project switches for a
smaller development loop, but do not substitute a partial build for required
closeout.

Architecture rules:

- `dotnet build ... -r win-arm64` on x64 is cross-compile sanity only.
- Native ARM64 launch, WebView2, runtime loading, UI, and installer behavior
  require the `windows-11-arm64` proof pool.
- Release publish validates both architectures. x64 performs native dependency
  load probes. ARM64 release jobs, including the native `windows-11-arm`
  publish, pass `-SkipNativeLoadProbe`; they prove payload presence and
  signatures, not native ARM64 dependency loading or app behavior.

Exact development packaging entrypoints:

```powershell
# Fast local Inno iteration
.\scripts\build-inno-local.ps1 -Arch x64 -Fast

# Both release-shaped Inno architectures
.\scripts\build-inno-local.ps1 -Arch All

# Side-by-side locally signed Dev MSIX
.\scripts\setup-dev-msix-cert.ps1
.\build.ps1 -Project WinUI -Msix Dev

# Unsigned Store submission packages for x64 and ARM64
.\build.ps1 -Project WinUI -Msix Store
```

Certificate trust and package installation change machine state. Run them only
with that authority, and remove a task-created development certificate when the
test is complete:

```powershell
.\scripts\setup-dev-msix-cert.ps1 -Remove
```

For source and artifact contract changes, use the matching checks instead of an
unrelated app smoke:

| Change | Exact check |
|---|---|
| Inno source or Pascal Script | `.\scripts\Test-InstallerScriptCompiles.ps1 -RequireCompiler` |
| Installer source contract | `.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj' -Filter 'FullyQualifiedName~InstallerIssAssertionTests' -ResultName 'installer-source'` |
| Store bundle construction | `.\scripts\test-msix-bundle.ps1` |
| MSIX version allocation | `.\scripts\test-msix-versioning.ps1` |
| MSIX artifact shape | `.\scripts\test-msix-ci-artifacts.ps1` |
| Dev MSIX release asset | `.\scripts\test-dev-msix-release.ps1` |
| Migration test package | `.\scripts\test-migration-test-msix.ps1` |
| Store submission authorization | `.\scripts\test-microsoft-store-submission.ps1` |
| Inno-to-Store helper behavior | `.\scripts\test-store-migration-test-script.ps1` |

Source checks do not prove clean install, upgrade, repair, uninstall, signature
reputation, Store ingestion, package-private storage, or preservation. Use
`windows-clean-installer-upgrade` for exact signed installer/update artifacts,
`windows-11-sac-on` for enforcing Smart App Control, and the disposable
[Store migration procedure](../../../docs/STORE_MIGRATION_TESTING.md) for real
Inno-to-Store acceptance.

Repository validation tooling has its own executable contracts:

| Change | Exact checks |
|---|---|
| Proof-pool inventory or scheduling | `.\scripts\validate-proof-pools.ps1`; `.\scripts\test-proof-pool-validator.ps1`; `.\scripts\test-validate-docs-proof-pool-flow.ps1` |
| Result admission, wrappers, or evidence summaries | `.\scripts\test-proof-evidence.ps1` |
| Agent readiness reporting | `.\scripts\test-agent-readiness.ps1` |
| Change classifier | `.\scripts\test-ci-change-classifier.ps1` |
| CI Gate result admission | `.\scripts\test-ci-gate-results.ps1` |
| Workflow contracts | `.\scripts\test-ci-workflow-contract.ps1` |
| Native Gateway source-build safety | `.\scripts\test-native-gateway-source-build.ps1` |
| npm Gateway channel selection | `.\scripts\test-gateway-npm-release.ps1` |
| Packaged Gateway provenance | `.\scripts\test-gateway-packaged-release.ps1` |
| MSIX preview source selection | `.\scripts\test-msix-preview-source-version.ps1` |
| Daily alpha version selection | `.\scripts\test-daily-alpha-version.ps1` |
| Alpha promotion policy and versioning | `.\scripts\test-alpha-promotion.ps1`; `.\scripts\test-alpha-promotion-versioning.ps1` |
| Release branch and stable correction versioning | `.\scripts\test-release-branch-versioning.ps1`; `.\scripts\test-stable-correction-release-validator.ps1` |

The documentation gate runs the proof-pool schema and parent-flow checks, not
the full malformed-contract matrix. Run the named focused checks for the
contract being changed.

### 7. Release and cross-repository compatibility

`.github\workflows\ci.yml` is the source of truth for hosted lanes. It separates:

- fast documentation, skill, classifier, packaging-contract, and release-policy checks
- core, tray/setup/integration, and UI/accessibility test jobs
- WSL setup, revocation, and network E2E shards
- x64 release publish smoke and native ARM64 release publish
- per-architecture Dev/Store MSIX builds and the Store bundle
- signed x64/ARM64 portable and Inno release assets
- the fail-closed CI Gate

Packaging, installer, project, workflow, dependency, classifier, or unknown-path
changes select full pull-request validation and x64 release smoke. Pushes to
`main` and tags also require ARM64 release publishing.

The reusable `.github\workflows\release-candidate-e2e.yml` proves one exact
OpenClaw package candidate against an exact Windows-node app and immutable test
harness. Only the recorded outcome `passed`, backed by the passing selected test,
proves setup, connect, and safe `system.which` invocation through the real WSL
Gateway. An explicitly allowed `protocol_mismatch` outcome may make the workflow
green, but it records incompatibility during fixture initialization and is not
behavioral proof. Neither outcome proves the native packaged Gateway. Preserve
candidate, app, workflow, artifact, and digest identities when interpreting it.

Release acceptance can exceed merge validation. Signed installer reputation,
Store migration, real distribution, native Gateway lifecycle, native ARM64, and
cross-architecture upgrade claims remain blocked until their named proof pools
or release gates run against the exact candidate artifacts. Validation never
grants permission to reserve versions, sign, publish, submit to the Store, or
dispatch workflows.

## Failure policy

- Treat a failure as a defect until evidence identifies product, test fixture,
  shared state, ordering, infrastructure, credentials, host capability, or stale
  source.
- Reproduce with the smallest owner test or the same CI shard/filter. Fix the
  cause, rerun the focused failure, then rerun every required closeout command
  affected by the fix.
- Do not make a failure green with retries, longer arbitrary sleeps, weaker
  assertions, broader mocks, hidden skips, baseline expansion, or a different
  provider or architecture.
- A passing replay is not a proven fix. Add or identify a regression that fails
  for the original defect when feasible.
- Check actual executed test counts and fresh reports. Zero tests, missing or
  stale TRX, all-skipped selections, and skipped custom proof are failures.
- Stop only a verified task-owned process for file locks. Do not terminate
  another session's tray, Gateway, WSL distro, service, or installer.
- Diagnose missing prerequisites with
  `.\scripts\setup-dev.ps1 -CheckOnly`. Installation requires authority.
- Keep opt-in environment variables scoped to the test and restore or remove
  them afterward. In particular, unset `OPENCLAW_RUN_INTEGRATION`,
  `OPENCLAW_RUN_E2E`, and `OPENCLAW_RUN_MXC_E2E` when finished.
- Report exact commands, pass/fail and executed/skipped counts, source identity,
  architecture, runtime or artifact identity, and every unrun or blocked proof
  boundary. Do not turn fixture, local MCP, WSL, unsigned package, or
  cross-compile evidence into a broader claim.

## Source and maintenance

Windows-owned adaptation of the MIT-licensed upstream
[OpenClaw testing skill](https://github.com/openclaw/openclaw/blob/050ea4d27043627e2b2b84b993dae6c341343238/.agents/skills/openclaw-testing/SKILL.md).
Windows retains mandatory local code closeout, .NET and WinUI suite ownership,
WSL/native Gateway separation, package identities, x64/ARM64 gates, named proof
pools, and repository publication boundaries.
