---
name: openclaw-testing
description: Pick the smallest tests for a Windows-node change and interpret test failures.
---

# OpenClaw Windows testing

Use this skill to plan tests for a change and to interpret failures. The
[validation policy](../../../docs/TEST_COVERAGE.md#validation-policy) decides
how much to run; this skill helps pick the right checks.

It does not authorize app launches, WSL or package changes, remote hosts, CI
dispatch, publication, cleanup, version reservation, signing, or Store
submission. Use [test-audit](../test-audit/SKILL.md) when writing or reviewing
tests, [proof validation](../openclaw-proof-validation/SKILL.md) for runtime
evidence, [Crabbox](../crabbox/SKILL.md) for authorized remote Windows
execution, and [native Gateway from source](../native-gateway-from-source/SKILL.md)
for native Gateway registration or compatibility work.

## Start with the changed contract

1. Find the production owner and its existing tests. Read only the affected
   [architecture ledger](../../../docs/ARCHITECTURE.md) rows.
2. Include staged, unstaged, and relevant untracked paths. A plan based only on
   committed changes is incomplete for local work.
3. Use the classifier as a read-only start, then add behavioral checks that path
   classification cannot infer. See
   [explain the validation plan](../../../docs/TEST_COVERAGE.md#explain-the-validation-plan).
   It lists hosted lanes and the core lane. Its proof-pool output is a
   candidate list, not authorization or completed evidence.

If source changes while validation is running, reassess affected results.
Rebuild and rerun only checks whose tested content or prerequisites changed.
Do not rerun an unchanged-code result just to produce an agent-owned result.

## Use the proportional ladder

Choose the [execution target](../openclaw-proof-validation/SKILL.md#choose-the-execution-target)
before any authorized runtime proof. Start at the smallest boundary that can
fail for the intended regression. Move up when the change crosses another
owner, transport, persistence boundary, architecture, or distribution format.
Run only what you changed or what failed, then one final check that covers the
change.

### 1. Documentation and skill checks

Follow the [docs checks](../../../docs/AGENTS.md#format-and-checks). They are
not runtime proof. Shipped `winnode` command-reference changes also follow the
[new command contract](../../../docs/WINDOWS_NODE_TESTING.md#new-command-mcp-contract).

### 2. Focused owner tests

Pick the project from the
[owner table](../test-audit/SKILL.md#choose-the-production-owner). Use
`dotnet test <project> --filter '<expression>'` for a class or behavior, then
run related siblings when shared setup or lifecycle could be involved.

Even filtered `OpenClaw.Tray.Tests` runs load automatic desktop-window cleanup.
Follow the [execution safety rules](../../../docs/TEST_COVERAGE.md#running-tests);
settings isolation alone does not make them safe on a shared desktop.

Tray Integration launches a real app process, so it needs runtime authority even
though its profile is isolated. It finds existing tray build output, so rebuild
the app from the current tree first, and scope the opt-in variable:

```powershell
$env:OPENCLAW_RUN_INTEGRATION = '1'
try {
  dotnet build .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -c Debug -r win-x64
  dotnet test .\tests\OpenClaw.Tray.IntegrationTests\OpenClaw.Tray.IntegrationTests.csproj -r win-x64
} finally {
  Remove-Item Env:\OPENCLAW_RUN_INTEGRATION -ErrorAction SilentlyContinue
}
```

Use temporary settings directories or set `OPENCLAW_TRAY_DATA_DIR` before the
test process starts. For real tray or setup proof, also isolate
`OPENCLAW_TRAY_APPDATA_DIR` and `OPENCLAW_TRAY_LOCALAPPDATA_DIR`. Do not let
tests read or mutate the normal user profile.

### 3. Core lane

Run missing [core-lane checks](../../../docs/TEST_COVERAGE.md#running-tests) at
the [code-publication gate](../../../AGENTS.md#before-creating-or-updating-a-pr),
not for drafting or metadata-only edits. It also helps for a cross-project
change or CI diagnosis. Settings-profile,
app-identity, or sandbox-containment changes also need the DevBuild variant
used by CI:

```powershell
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj `
  -c Debug -r win-x64 --no-restore -p:DevBuild=true `
  --filter 'FullyQualifiedName~SettingsManagerIsolationTests|FullyQualifiedName~AppRefactorContractTests.SandboxPage_'
```

### 4. WinUI and application-boundary proof

Pure view models and projections belong in focused unit tests. Mounted controls,
Windows App SDK behavior, accessibility, keyboard input, and visible rendering
need the UI suite or an interactive proof host. For accessibility changes, ask
first, then use the accessibility lane in
[formal validation paths](../../../docs/TEST_COVERAGE.md#formal-validation-paths).

For deterministic real-app chat and navigation behavior, ask first, then use the
[fixture smoke](../../../docs/GATEWAY_FIXTURE_TESTING.md#run-the-application-smoke)
and, for runtime regressions, the
[production-shaped Release proof](../../../docs/GATEWAY_FIXTURE_TESTING.md#production-shaped-release-proof).
The fixture proves the real app, Gateway client, chat stack, history loader,
local MCP, and mounted native UI against synthetic loopback data. It does not
prove live pairing, providers, node command execution, WSL, native Gateway/MXC,
installation, or update.

### 5. WSL, pairing, protocol, and node invocation

Connection and pairing changes need focused `OpenClaw.Connection.Tests` plus the
right real boundary. Local E2E (`OPENCLAW_RUN_E2E=1`) creates a run-scoped WSL
distro and startup registrations, so it needs runtime authority. Rebuild the
tray first, because E2E finds existing build output. These lanes exercise the
npm-latest WSL Gateway path. They are not native MSIX Gateway proof.

For app-owned WSL installation diagnostics, the non-mutating preflight is:

```powershell
.\scripts\validate-wsl-gateway.ps1 -Scenario PreflightOnly
```

`UpstreamInstall` launches the tray, installs or reuses the shared product
`OpenClawGateway` distro, and stops matching WSL keepalive processes. Its WSL
state is not isolated by the run-specific AppData directories. It requires
explicit runtime authority and an owned disposable host. `FreshMachine` and
`Recreate` also unregister `OpenClawGateway`, require
`-ConfirmDestructiveClean`, and require explicit destructive authority.

MXC proof must not skip. `-AllowSkip` only documents an incapable host and is
never merge validation. See the MXC lane in
[formal validation paths](../../../docs/TEST_COVERAGE.md#formal-validation-paths).
New node commands follow the
[new command contract](../../../docs/WINDOWS_NODE_TESTING.md#new-command-mcp-contract).

### 6. Build, architecture, packaging, and installer proof

Partial builds are fine while iterating. The final check must build what
changed.

- `dotnet build ... -r win-arm64` on x64 is cross-compile sanity only.
- Native ARM64 launch, WebView2, runtime loading, UI, and installer behavior
  require the `windows-11-arm64` proof pool.
- Release publish validates both architectures. x64 performs native dependency
  load probes. ARM64 release jobs, including the native `windows-11-arm`
  publish, pass `-SkipNativeLoadProbe`; they prove payload presence and
  signatures, not native ARM64 dependency loading or app behavior.

Packaging commands are in
[local Inno installer iteration](../../../DEVELOPMENT.md#local-inno-installer-iteration).
Certificate trust and package installation change machine state. Run them only
with that authority, and remove a task-created development certificate when the
test is complete:

```powershell
.\scripts\setup-dev-msix-cert.ps1 -Remove
```

Installer, MSIX, and repository tooling checks are listed in
[formal validation paths](../../../docs/TEST_COVERAGE.md#formal-validation-paths).
Source checks do not prove clean install, upgrade, repair, uninstall, signature
reputation, Store ingestion, package-private storage, or preservation. Use
`windows-clean-installer-upgrade` for exact signed installer or update
artifacts, `windows-11-sac-on` for enforcing Smart App Control, and the
disposable [Store migration procedure](../../../docs/STORE_MIGRATION_TESTING.md)
for real Inno-to-Store acceptance.

### 7. Release and cross-repository compatibility

`.github\workflows\ci.yml` is the source of truth for hosted lanes. For release
candidates, see
[cross-repository candidate compatibility](../../../DEVELOPMENT.md#cross-repository-candidate-compatibility).
An accepted `PROTOCOL_MISMATCH` records incompatibility, not behavioral proof,
and neither outcome proves the native packaged Gateway. Signed installer reputation, Store migration, real distribution, native Gateway
lifecycle, native ARM64, and cross-architecture upgrade claims stay blocked
until their proof pools or release gates run against the exact candidate
artifacts.

## Failure policy

- Treat a failure as a defect until evidence identifies product, test fixture,
  shared state, ordering, infrastructure, credentials, host capability, or stale
  source.
- Reproduce with the smallest owner test or the same CI shard and filter. Fix
  the cause, rerun the failed check, then one final check that covers the
  change.
- Never make a failure green with retries, longer arbitrary sleeps, weaker
  assertions, broader mocks, hidden skips, baseline expansion, or a different
  provider or architecture.
- A passing replay is not a proven fix. See the
  [pre-fix failure rule](../test-audit/SKILL.md#cost-and-validation).
- Zero tests, missing or stale TRX, all-skipped selections, and skipped custom
  proof are failures.
- Stop only a verified task-owned process for file locks. Do not terminate
  another session's tray, Gateway, WSL distro, service, or installer.
- Diagnose missing prerequisites with `.\scripts\setup-dev.ps1 -CheckOnly`.
  Installation requires authority.
- Keep opt-in environment variables scoped to the test. Unset
  `OPENCLAW_RUN_INTEGRATION`, `OPENCLAW_RUN_E2E`, and `OPENCLAW_RUN_MXC_E2E`
  when finished.
- Report exact commands, executed and skipped counts, and every unrun or blocked
  proof boundary. Do not turn fixture, local MCP, WSL, unsigned package, or
  cross-compile evidence into a broader claim.

## Source

Windows-owned adaptation of the MIT-licensed upstream
[OpenClaw testing skill](https://github.com/openclaw/openclaw/blob/050ea4d27043627e2b2b84b993dae6c341343238/.agents/skills/openclaw-testing/SKILL.md).
