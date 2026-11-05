# OpenClaw.Shared.Tests

Unit and focused integration coverage for `OpenClaw.Shared`.

Do not maintain per-file test counts in this README. They drift whenever
theories or focused regressions are added. The repository-wide project
inventory and validation lanes live in
[`docs/TEST_COVERAGE.md`](../../docs/TEST_COVERAGE.md).
Use the current run's report for completion claims. For new regressions, follow
[test authoring](../../docs/TEST_COVERAGE.md#writing-a-regression-test).

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

## Notable test contracts

- **Command output-drain timing**: the unsupported-Windows runner fixture gates launcher release, then requires completion within three seconds of root exit while a descendant retains stdout, stderr, or both. Ten repetitions per case check short output and PID/start-time-bound child cleanup. The nonparallel `MxcOwnership` collection isolates these wall-clock budgets from unrelated tests. Native SDK finalization has separate managed ownership tests and opt-in runtime proof.
- **Fixture process identity**: MXC pipe-fixture metadata, cleanup ownership checks, and the opt-in native MXC descendant-identity check query the observed full executable image through a retained process handle, not the loader's nullable `MainModule` or a requested launch path. `ProcessImagePathTests` checks a real, never-resumed Windows process with no initialized loader view, a different command-line image name, caller-handle lifetime, and explicit native query failure. This covers unavailable loader metadata; it does not reproduce the timing of a normally scheduled child or prove native MXC containment.
- **Gateway and WebSocket close handling**: the signing-identity regression covers native and managed loopback WebSockets with a bounded client-initiated disconnect and a queued health request. The fixture drains application frames already in flight until the client's close frame, replies, and closes its transport under one deadline rather than relying on the client's EOF fallback timer. A separate regression checks the close frame and actual TCP EOF on both transports, including fragmented text and binary data queued before close.
- **CI hang watchdog**: the CI Shared-test step uses a five-minute hang watchdog and a 15-minute outer step limit. A hang fails the run and preserves the interrupted test sequence without collecting a memory dump. It is not retried or skipped.

Some integration tests require explicit environment gates or local Windows
capabilities and may skip on unsupported hosts. A successful zero-test
`--no-restore` run in a fresh worktree is not proof; build the project first or
omit `--no-restore`.
