# Test Coverage Summary

**Last audited**: 2026-05-22<br>
**Framework**: xUnit / .NET 10.0<br>
**Required validation status**: passing (`.\build.ps1`, Shared tests, Tray tests)

## Required validation suites

These are the suites every agent must run after code changes, as documented in
`AGENTS.md`.

| Suite | Latest runtime result |
|---|---:|
| `OpenClaw.Shared.Tests` | 1,920 total: 1,891 passed, 29 skipped |
| `OpenClaw.Tray.Tests` | 1,178 total: 1,178 passed, 0 skipped |

Runtime totals come from `dotnet test` on 2026-05-22. They are higher than
method counts because some `[Theory]` tests expand into multiple cases.

## Test project inventory

| Project | Primary scope | Test methods |
|---|---|---:|
| `OpenClaw.Connection.Tests` | Gateway registry, credential resolution, connection manager/state machine, setup codes, pairing, diagnostics | 189 |
| `OpenClaw.Shared.Tests` | Shared models, gateway client, capabilities, MCP, exec approval, A2UI security, URL handling, notification categorization | 1,347 |
| `OpenClaw.Tray.Tests` | Tray state/UI helpers, settings isolation, onboarding, connection page behavior, localization, local gateway setup/uninstall | 786 |
| `OpenClaw.Tray.UITests` | Native WinUI/A2UI control and rendering coverage | 50 |
| `OpenClaw.WinNode.Cli.Tests` | Windows node CLI argument parsing, command behavior, JSON output, uninstall flow | 79 |
| `OpenClawTray.FunctionalUI.Tests` | Functional UI smoke coverage | 8 |
| `OpenClawTray.OnboardingV2.Tests` | Onboarding V2 page flow and state coverage | 9 |
| `OpenClaw.Tray.IntegrationTests` | Integration-test project scaffold; no `[Fact]`/`[Theory]` methods currently | 0 |

The method inventory is a source scan of `[Fact]` and `[Theory]` attributes. Use
`dotnet test` for authoritative runtime totals.

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
- **OpenClawTray.OnboardingV2.Tests** and **OpenClawTray.FunctionalUI.Tests** cover newer UI surfaces outside the main tray test project.

## Running tests

```powershell
# Required validation after code changes
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore

# All tests in the solution
dotnet test

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
