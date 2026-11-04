# Test Coverage and Validation

This file owns validation policy, the test project inventory, and the lanes
agents and contributors choose from. CI internals live in
[DEVELOPMENT.md](../DEVELOPMENT.md#cicd). Runtime proof lives in the
[proof skill](../.agents/skills/openclaw-proof-validation/SKILL.md).

## Validation policy

**Validate proportionally.** While working, run only the checks for what you changed or what failed. Before handing off, run one final check that covers the change. CI is the full gate, so don't repeat CI locally unless you're diagnosing a failure. Before anything that opens windows, takes focus, starts WSL or a Gateway, or runs long, say why and ask first.

A passing check on the final code counts; do not repeat it just for handoff.

- Documentation-only changes run the [docs checks](AGENTS.md#format-and-checks)
  only: no builds or test suites.
- At code publication, run only missing [core-lane checks](#running-tests)
  (build, Shared tests, Tray tests), reusing results that cover the current code.
  Drafting, status reports, and metadata-only PR edits do not trigger this lane.
  The full sequence and reuse rules are in the
  [pre-PR checks](../AGENTS.md#before-creating-or-updating-a-pr).
- Fix failures your change caused and rerun that check. Report baseline, flaky,
  or environment failures with evidence instead of repairing unrelated tests.
- A run that reports no executed tests is not a pass. See
  [accepting result reports](#accepting-result-reports).
- MXC proof must not skip. Running it locally needs the user's go-ahead;
  otherwise report it as blocked.

## Test project inventory

Projects marked **ask first** open windows, take focus, or start WSL or a
Gateway.

| Project | Scope | Notes |
|---|---|---|
| `OpenClaw.Shared.Tests` | Shared models, gateway client, capabilities, MCP, exec approval, A2UI security, URL handling | Headless. See its [README](../tests/OpenClaw.Shared.Tests/README.md) |
| `OpenClaw.Connection.Tests` | Gateway registry, credentials, connection manager/state machine, setup codes, pairing, diagnostics | Headless |
| `OpenClaw.Tray.Tests` | Tray state and UI helpers, settings isolation, onboarding, connection page, localization, local gateway setup | **Ask first on a shared desktop**: assembly-level window cleanup runs even for filtered tests; use an authorized isolated desktop and isolate settings |
| `OpenClaw.WinNode.Cli.Tests` | Windows node CLI parsing, command behavior, JSON output, `SkillMdDriftTests` | Headless |
| `OpenClaw.SetupEngine.Tests` | Setup engine, WSL gateway installation, setup-code and local setup policy | Headless except the opt-in cleanup visibility tests |
| `OpenClaw.Tray.UITests` | Native WinUI/A2UI rendering and Axe.Windows accessibility | **Ask first**: starts the app |
| `OpenClawTray.FunctionalUI.Tests` | Functional UI smoke coverage | **Ask first**: opens windows |
| `OpenClaw.Tray.IntegrationTests` | Real-process tray/MCP integration, gated by `OPENCLAW_RUN_INTEGRATION=1` | **Ask first**: starts processes |
| `OpenClaw.E2ETests` | Gateway-mediated setup/connect, revocation, and network recovery (`[E2EFact]`, `[MxcE2EFact]`) | **Ask first**: starts WSL and a Gateway |

`tests\PackagingTests\` holds PowerShell scripts, not a dotnet test project.

### Opt-in lanes inside headless projects

- `GatewayCleanupTaskTests` desktop visibility tests use
  `OPENCLAW_RUN_INTEGRATION=1` because their positive control deliberately
  displays a classic console. Ask first. Run them on an interactive desktop with
  no concurrent desktop automation. The contract is no visible window or
  taskbar entry, not absence of an HWND. Unavailable shell or provider access
  fails the proof instead of passing on a heuristic.
- The chat composer keyboard regression (`Category=NativeKeyboard`) uses
  `SendInput` on a visible, focused window and needs
  `OPENCLAW_RUN_KEYBOARD_PROOF=1` plus a dedicated `OPENCLAW_TRAY_DATA_DIR`.
  Ask first. Use an isolated desktop with no concurrent computer-use or human
  input, Caps Lock off, and a Latin keyboard layout. The tests check foreground
  ownership before each key sequence and fail rather than type into another
  window. On a proof host every selected case must pass with zero skips. IME
  composition is not covered. Unset the opt-in after the run.
- Markdown renderer record-shape tests belong to `UICollection` because their
  WinRT-backed types must not initialize concurrently with the shared Windows
  App SDK bootstrap.

### Fixture-backed application smoke

The [Gateway fixture harness](GATEWAY_FIXTURE_TESTING.md) runs the real desktop
app against deterministic synthetic Gateway responses, with no AI or real WSL
Gateway. It launches the app, so ask first.

## Formal validation paths

Pick the lanes that cover what changed. CI enforces impact-selected required
lanes, not every lane on every PR. Affected behavioral checks and declared
custom proof pools still need their own evidence.

| Lane | Entry point | Use when |
|---|---|---|
| Core lane | `.\build.ps1`, Shared tests, Tray tests (see [Running tests](#running-tests)) | Code-publication gate, reusing current-code evidence. Tray execution needs desktop authority. `build.ps1` may add a global git `safe.directory` entry; `-NoTrustRepository` skips it |
| Fixture-backed app smoke | `.\scripts\test-gateway-fixture.ps1 -AppPath '<built-app.exe>'` | Chat session switching, history, scrolling, and populated navigation. **Ask first**: launches the app |
| Proof-pool inventory | `.\scripts\validate-proof-pools.ps1`, `.\scripts\test-proof-pool-validator.ps1`, and `.\scripts\test-validate-docs-proof-pool-flow.ps1` | Inventory or proof scheduling changes; the docs gate runs core schema and parent-flow checks, while CI runs the full malformed-contract matrix |
| Agent skills | `.\scripts\validate-agent-skills.ps1`; add `.\scripts\test-agent-skills-validator.ps1` only when the validator code or fixtures change | Changes under `.agents\skills` |
| Result and evidence contracts | `.\scripts\test-proof-evidence.ps1` | Changes to report admission, generic proof, CI test wrappers, evidence summaries, or their source/artifact correspondence |
| Agent readiness | `.\scripts\test-agent-readiness.ps1` | Changes to read-only host and optional agent-tool readiness reporting |
| GitHub-hosted PR/main CI | `.github\workflows\ci.yml` | Every pull request and push to `main`; impact outputs select the test, E2E, and release lanes |
| Accessibility scan | `.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'Category=Accessibility' -ResultName 'winui-accessibility' -RuntimeIdentifier win-x64` | UI changes; see `docs\ACCESSIBILITY.md`. **Ask first**: starts the app |
| Local E2E | `OPENCLAW_RUN_E2E=1` with `OpenClaw.E2ETests` | Gateway setup/connect, recovery, or pairing changes. **Ask first**: starts WSL and a Gateway |
| Local MXC E2E | `.\scripts\validate-mxc-e2e.ps1` | MXC sandboxing, `system.run`, exec approvals, Windows node command execution. Fails when MXC proof skips. **Ask first**: starts WSL and a Gateway |
| Product WSL setup validation | `OPENCLAW_RUN_E2E=1` with `OpenClaw.E2ETests.Setup.SetupAndConnectTests` | Onboarding or setup-engine changes that must prove the product WSL install path. **Ask first**: starts WSL and a Gateway |
| Installer source checks | `.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj' -Filter 'FullyQualifiedName~InstallerIssAssertionTests' -ResultName 'installer-source'` | Installer source, payload, identity, cleanup, or protocol changes; pair with the installer pool for runtime claims |
| Inno compile | `.\scripts\Test-InstallerScriptCompiles.ps1 -RequireCompiler` | Inno source or Pascal Script changes |
| MSIX and Store scripts | `.\scripts\test-msix-bundle.ps1` (bundle), `test-msix-versioning.ps1` (version allocation), `test-msix-ci-artifacts.ps1` (artifact shape), `test-dev-msix-release.ps1` (Dev release asset), `test-migration-test-msix.ps1` (migration test package), `test-microsoft-store-submission.ps1` (submission authorization) | Changes to the matching packaging script or workflow; not install or Store proof |
| CI and release tooling | `.\scripts\test-ci-change-classifier.ps1` (classifier), `test-ci-gate-results.ps1` (CI Gate admission), `test-ci-workflow-contract.ps1` (workflow contracts), `test-native-gateway-source-build.ps1`, `test-gateway-npm-release.ps1`, `test-gateway-packaged-release.ps1`, `test-msix-preview-source-version.ps1`, `test-daily-alpha-version.ps1`, `test-alpha-promotion.ps1`, `test-alpha-promotion-versioning.ps1`, `test-release-branch-versioning.ps1`, `test-stable-correction-release-validator.ps1` | Changes to the matching script or release policy |
| Migration preservation source checks | `dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --filter FullyQualifiedName~InnoMigrationContractTests` | Migration exits and deletion authorization; not real installer proof |
| Migration record and cleanup scripts | `dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj --filter FullyQualifiedName~MigrationRecordTests` | Receipt checks and cleanup lock contention through real Windows PowerShell |
| Migration startup admission | `dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj --filter "FullyQualifiedName~InnoInstallationDetectorTests\|FullyQualifiedName~StoreMigrationStartupCoordinatorTests\|FullyQualifiedName~MigrationStartupRecordReaderTests\|FullyQualifiedName~MigrationVersionPolicyTests"` | Inno discovery, version/architecture admission, and pending-record precedence; not completed migration proof |
| Store migration helper fixtures | `.\scripts\test-store-migration-test-script.ps1` | Store migration helper changes; uses in-memory adapters and test-owned files. Not installer, signature, or Store UI proof |
| Disposable Inno to Store migration | `.\scripts\Enable-StoreMigrationTest.ps1` with [`STORE_MIGRATION_TESTING.md`](./STORE_MIGRATION_TESTING.md) | Signed-artifact handoff proof on a disposable machine only. Report missing artifacts or runtime as blocked |
| Local AI model fixture | `dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj --filter "FullyQualifiedName~SparseFixtureFileTests\|FullyQualifiedName~LocalAiPortLifecycleTests"` | Local AI fixture and port lifecycle changes; sparse files avoid model downloads and need a sparse-capable filesystem |

Capacity-dependent proof is declared through [proof pools](PROOF_POOLS.md).

### Choose the local execution target

Choose the target by the behavior under test. The routing table lives in the
[proof skill](../.agents/skills/openclaw-proof-validation/SKILL.md#choose-the-execution-target).

### Native packaged Gateway proof

A WSL E2E run does not prove the native packaged Gateway. Requirements are in
[native packaged Gateway proof](../.agents/skills/native-gateway-from-source/SKILL.md#native-packaged-gateway-proof).


### Explain the validation plan

Use the classifier in read-only planning mode:

```powershell
.\scripts\Get-CiChangeClassification.ps1 -EventName pull_request `
  -ChangedPaths 'src\OpenClaw.Cli\Program.cs' -Human
.\scripts\Get-CiChangeClassification.ps1 -EventName pull_request `
  -ChangedPaths 'README.md' -Explain
```

For a branch comparison, pass known `-BaseSha` and `-HeadSha` values.
`-ChangedPaths` is an explicit scenario, not discovery of current edits.
Include staged, unstaged, and relevant untracked changes when planning an
uncommitted task. Invalid revisions, empty or unknown inputs, and infrastructure
changes stay conservative.

The default JSON stays compatible with CI. `-Explain` adds path reasons, hosted
lanes, the core lane for code changes, and inventory proof candidates.
`-Human` renders the same decisions for a reader. The shipped `winnode`
reference keeps its CLI/core contract treatment.

Proof candidates are **not automatically required or scheduled**: compare each
pool's `requiredWhen`, prerequisites, and `doesNotProve` with the behavior
change. The planner does not run tests, authenticate, launch an app, acquire a
host, or dispatch CI.

## Writing a regression test

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
- Wait for real readiness, not a dispatcher yield. For example, onboarding card
  tests wait until the card is loaded, enabled, and attached to a `XamlRoot`.

### Accepting result reports

`Invoke-CiTest.ps1` and `run-proof-tests.ps1` use invocation-specific result
directories. Their expected TRX must be fresh, parseable, internally consistent,
and contain actual executed tests; successful process exit alone is insufficient.
File modification times and embedded TRX invocation times permit one second
of clock granularity tolerance. Reports older than that remain stale.
Generic custom proof requires every selected result to pass. Ordinary CI may
report supported skipped cases alongside executed passing tests, but all-skipped
or zero-execution runs fail. Hosted MXC skip policy is not native MXC proof.
Skipped counts come from actual result rows and must match total minus executed.
VSTest may leave its aggregate `notExecuted` counter at zero even when skipped
rows are present; a contradictory nonzero aggregate still fails validation.

When reporting results:

- Read outputs from the exact invocation, not the newest file. Check executed
  cases, pass/fail/skip counts, and failures. Label manual observations as
  manual; do not fabricate receipts.
- Distinguish the PR head, the tested commit (including CI merge commits), the
  uncommitted source used by the run, and the loaded binary or runtime.
- After edits, commits, or a rebase, reassess which results still apply. Source
  equality can establish correspondence but does not turn an earlier run into a
  new one.
- Keep original failures. An instrumented pass does not erase them, and
  restoring source does not restore previously built binaries.
- Identify the CI run and attempt when citing CI results. Successful dispatch
  is not completed testing.

See [local execution evidence](PROOF_POOLS.md#validate-local-execution-evidence)
for inventory-bound summaries and their assurance limits.

## Running tests

Core lane (at code publication; reuse checks already completed on current code):

**Tray tests are not desktop-safe merely because they are unit tests.**
`WinAppSdkGhostWindowCleanup` runs when the assembly loads and periodically
hides/closes newly detected blank shell windows, without proving test ownership.
A test filter or isolated settings directory does not disable that hook.
Use an authorized isolated desktop/host, or explain this side effect and obtain
permission before running on the user's desktop. Keep the Tray check blocked
if neither is available; continue safe work instead of launching UI implicitly.
Unset unrelated integration, keyboard, GPU, and E2E opt-ins for ordinary tests.

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1 -NoTrustRepository
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
# Only on an authorized desktop/host as described above:
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

Ask first before running these:

```powershell
# Whole solution. Includes UI, functional UI, and integration suites that start
# the tray. E2E is excluded from the solution.
dotnet test

# Local E2E. Starts WSL and a Gateway.
$env:OPENCLAW_RUN_E2E = "1"
dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj -r win-x64

# MXC proof. Starts WSL and a Gateway; fails when MXC proofs skip.
# -AllowSkip only documents an incapable host and is not merge validation.
.\scripts\validate-mxc-e2e.ps1
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

For these gaps, collect runtime proof through the
[proof skill](../.agents/skills/openclaw-proof-validation/SKILL.md) when the
change needs it. Ask first, because it launches the app. For node command
surfaces, follow the
[new-command contract](WINDOWS_NODE_TESTING.md#new-command-mcp-contract).
