# Test Coverage Summary

**Last audited**: 2026-07-06<br>
**Framework**: xUnit / .NET 10.0<br>
**Required validation status**: passing (`.\build.ps1`, Shared tests, Tray tests)

## Required validation suites

These are the suites every agent must run after code changes, as documented in
`AGENTS.md`.

| Suite | Latest runtime result |
|---|---:|
| `OpenClaw.Shared.Tests` | 2,734 total: 2,703 passed, 31 skipped |
| `OpenClaw.Tray.Tests` | 1,584 total: 1,584 passed, 0 skipped |

Runtime totals come from `dotnet test` on 2026-07-06. They are higher than
method counts because some `[Theory]` tests expand into multiple cases.

## Test project inventory

| Project | Primary scope | Test methods |
|---|---|---:|
| `OpenClaw.Connection.Tests` | Gateway registry, credential resolution, connection manager/state machine, setup codes, pairing, diagnostics | 325 |
| `OpenClaw.Shared.Tests` | Shared models, gateway client, capabilities, MCP, exec approval, A2UI security, URL handling, notification categorization | 1,977 |
| `OpenClaw.Tray.Tests` | Tray state/UI helpers, settings isolation, onboarding, connection page behavior, localization, local gateway setup/uninstall | 1,240 |
| `OpenClaw.Tray.UITests` | Native WinUI/A2UI control, rendering, and accessibility scan coverage | 64 |
| `OpenClaw.WinNode.Cli.Tests` | Windows node CLI argument parsing, command behavior, JSON output, uninstall flow | 89 |
| `OpenClaw.SetupEngine.Tests` | Setup engine, WSL gateway installation, setup-code, and local setup policy coverage | 284 |
| `OpenClawTray.FunctionalUI.Tests` | Functional UI smoke coverage | 10 |
| `OpenClaw.E2ETests` | Gateway-mediated setup/connect, revocation recovery, and network recovery suites | 21 |
| `OpenClaw.Tray.IntegrationTests` | Real-process tray/MCP integration tests gated by `OPENCLAW_RUN_INTEGRATION=1` | 18 |

The method inventory is a source scan of `[Fact]`, `[Theory]`, and repo custom xUnit attributes such as `[E2EFact]`, `[MxcE2EFact]`, and `[IntegrationFact]`. Use `dotnet test` for authoritative runtime totals.

## Coverage highlights

### OpenClaw.Shared.Tests

- **Model and display formatting** - activity glyphs, app version display, session labels, gateway usage/node display, channel status, and rich text helpers.
- **Gateway and WebSocket behavior** - gateway client parsing, session keys, WebSocket base handling, URL normalization, local gateway classification, and token sanitization.
- **Capabilities and MCP** - app/canvas/screen/camera/system capabilities, MCP auth token reset, MCP HTTP server, MCP tool bridge, MXC availability, MXC policy building, and command runners.
- **Exec approval** - legacy policy coverage plus V2 evaluator, input validation, normalization, prompt adapter, routing, coordinator, store, environment sanitizing, and shell-wrapper parsing.
- **A2UI and web bridge** - A2UI capability security, asset hash pinning, web bridge message handling, and channel payload/status tests.
- **Security and localization-adjacent helpers** - HTTP URL validation/risk evaluation, device identity, identity migration, notification categorization, speech language normalization, and non-fatal action handling.

### OpenClaw.Tray.Tests

- **Tray UI and state** - app state, menu display/position/sizing, tray tooltip formatting, activity streams, async list loading, diagnostics contracts, markup regressions, and chat timeline/markdown handling.
- **Connection and pairing** - connection manager node connector tests, connection page approval/channel metrics/row state, operator and Windows tray node pairing approval, and gateway action transport.
- **Settings and startup** - settings round-trip/isolation, consent and settings save, auto-start defaults, startup setup state, existing config guard policy, and local setup progress stage mapping.
- **Onboarding and local gateway setup** - onboarding completion/chat bootstrapper/existing config guard, wizard flow/selection/error/step parsing, setup code decoding, local gateway setup diagnostics, uninstall, WSL keep-alive, and auto-pair flags.
- **Localization and resources** - localization key parity, capability page localization, fluent icon catalog coverage, and installer assertion tests.

### Additional projects

- **OpenClaw.Connection.Tests** keeps connection architecture tests separate from tray UI concerns.
- **OpenClaw.Tray.UITests** covers A2UI/native WinUI rendering behavior that is awkward to validate through pure unit tests.
- **OpenClaw.WinNode.Cli.Tests** covers the standalone Windows node CLI contract.
- **OpenClaw.SetupEngine.Tests** covers gateway setup and local WSL installation policy.
- **OpenClawTray.FunctionalUI.Tests** covers newer UI surfaces outside the main tray test project.
- **OpenClaw.E2ETests** uses custom `[E2EFact]` / `[MxcE2EFact]` attributes that inherit xUnit `FactAttribute`; CI exercises them with shard filters.
- **OpenClaw.Tray.IntegrationTests** uses custom `[IntegrationFact]` attributes and runs only when `OPENCLAW_RUN_INTEGRATION=1`.
- **PackagingTests** is a PowerShell-script lane under `tests\PackagingTests\`, not a dotnet test project.

## Formal validation paths

Use the smallest lane that proves the changed subsystem, but always include the
required closeout lane for code changes.

| Lane | Entry point | Required when |
|---|---|---|
| Required closeout | `.\build.ps1`, Shared tests, Tray tests | Every code change and every agent closeout |
| GitHub-hosted PR/main CI | `.github\workflows\ci.yml` | Every pull request and push to `main`; runs normal E2E shards but skips MXC proofs on hosted runners |
| Accessibility scan | `dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj -r win-x64 --filter Category=Accessibility` | UI changes and CI quality gate; runs real-process Axe.Windows scans; see `docs\ACCESSIBILITY.md` |
| Local E2E | `OPENCLAW_RUN_E2E=1` with `OpenClaw.E2ETests` | Gateway setup/connect, recovery, or pairing changes that need real WSL Gateway coverage |
| Local MXC E2E | `.\scripts\validate-mxc-e2e.ps1` | MXC sandboxing, `system.run`, exec approvals, Windows node command execution, gateway setup/connect changes that affect MXC |
| Product WSL setup validation | `.\scripts\validate-wsl-gateway.ps1` | Tray onboarding/setup-engine changes that must prove the product WSL install path |
| Packaging script checks | `powershell -File .\tests\PackagingTests\Test-InnoUninstallOrdering.ps1` | Installer script changes that affect uninstall or cleanup ordering |

## Running tests

```powershell
# Required validation after code changes
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
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
