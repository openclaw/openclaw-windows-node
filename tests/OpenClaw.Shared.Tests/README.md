# OpenClaw.Shared.Tests

Unit and focused integration coverage for `OpenClaw.Shared`.

Do not maintain per-file test counts in this README. They drift whenever
theories or focused regressions are added. The repository-wide source inventory
and latest required-suite runtime totals live in
[`docs/TEST_COVERAGE.md`](../../docs/TEST_COVERAGE.md).

## Run the suite

From the repository root:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj
```

After the project has already restored and built:

```powershell
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
```

Run a focused class or method:

```powershell
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj `
  --filter "FullyQualifiedName~ExecApprovalsCoordinatorTests"
```

## Coverage areas

- Gateway WebSocket protocol parsing and request construction
- Session, channel, usage, node, command, and display models
- Device identity, token storage, signing, and migration
- Node capability contracts for system, canvas, screen, camera, location,
  speech, device, browser proxy, and app commands
- Local MCP authentication, JSON-RPC dispatch, cancellation, and telemetry
- Windows V2 exec approval validation, normalization, evaluation, prompting,
  persistence, and execution-boundary revalidation
- MXC availability, config/policy construction, command routing, and focused
  integration behavior
- URL, canvas, web-bridge, notification, and secret-handling security helpers
- Audio model download integrity and speech-language helpers

Some integration tests require explicit environment gates or local Windows
capabilities and may skip on unsupported hosts. A successful zero-test
`--no-restore` run in a fresh worktree is not proof; build the project first or
omit `--no-restore`.
