# AGENTS.md

## Working agreement

Read-only requests stay read-only. Permission to implement does not grant
permission to commit, push, publish, change GitHub metadata, or alter shared
runtimes. Preserve unrelated work and use the current task-owned worktree.

Start with the reported behavior, its production owner, and the existing tests.
Use the task routes below rather than reading every background document.
Delegate independent work only when it benefits from separate context; keep
one writer per worktree and give each task a bounded objective and evidence
requirement. A tool's successful exit is not proof that the intended work ran.

| Task | Entry point |
|---|---|
| Write, review, or restructure documentation/instructions | [Docs instructions](docs/AGENTS.md) and [technical-documentation](.agents/skills/technical-documentation/SKILL.md) |
| Write or review a regression test | [Test authoring](docs/TEST_COVERAGE.md#writing-a-regression-test) and [test-audit](.agents/skills/test-audit/SKILL.md) |
| Select tests and required proof | [Validation plan](docs/TEST_COVERAGE.md#explain-the-validation-plan) and [proof skill](.agents/skills/openclaw-proof-validation/SKILL.md) |
| Change a WinUI owner or its caller | [WinUI instructions](src/OpenClaw.Tray.WinUI/AGENTS.md) and the affected architecture ledger rows |
| Debug or drive the app through local MCP | [windows-app-mcp](.agents/skills/windows-app-mcp/SKILL.md); use [winnode](.agents/skills/winnode/SKILL.md) for the shipped command contract |
| Review code | [Shared autoreview](.agents/skills/autoreview/SKILL.md); use [Hanselman review](.agents/skills/hanselman-code-review/SKILL.md) when adversarial review is requested |
| Run remote native Windows proof | [Crabbox](.agents/skills/crabbox/SKILL.md), with explicit host/provider authority |
| Test against a source-built native Gateway | [native-gateway-from-source](.agents/skills/native-gateway-from-source/SKILL.md) |
| Uninstall or clean for a retest | [uninstall](.agents/skills/uninstall/SKILL.md), with target inventory and destructive confirmation |
| Audit the whole issue/PR landscape | [global-repo-triage](.agents/skills/global-repo-triage/SKILL.md); do not expand a single-item request into a sweep |

Use [agent readiness and workflow checks](docs/AGENT_WORKFLOWS.md) when changing
this guidance. These are coding-agent instructions, not the product's
end-user Gateway workspace `AGENTS.md` templates.

## Gateway MSIX Packaging

For Gateway MSIX packaging, use [openclaw-windows-packaging](https://github.com/openclaw/openclaw-windows-packaging).

## Required Validation After Every Change

All agents working in this repository must run validation after each code change before marking work complete.

Required steps:

1. Run full repo build:
   - `./build.ps1`
2. Run shared tests:
   - `dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore`
3. Run tray tests:
   - `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore`

This is the required local closeout subset for agents. CI also builds and runs additional connection, setup, CLI, UI, accessibility, integration, and E2E suites; see `docs/TEST_COVERAGE.md` for the broader test inventory and `.github/workflows/ci.yml` for the workflow source of truth.

If a command fails:

1. Fix the issue.
2. Re-run the failed command.
3. Re-run all required validation commands before completion.

Notes:

- If a build/test is blocked by a file lock, stop only a verified task-owned process and rerun. Do not stop another session's app or service; report that lock as a blocker.
- For missing local Windows prerequisites, start with `.\scripts\setup-dev.ps1 -CheckOnly`. Install with `.\scripts\setup-dev.ps1` only when authorized, then rerun validation. `-AgentReadiness` separately reports optional agent tooling and unverified runtime-proof capabilities without installing or authenticating.
- **First-run gotcha**: `dotnet test --no-restore` silently no-ops in a fresh worktree where the test `bin/` doesn't exist yet (reports "Build succeeded in 0.5s" then exits 0 with no tests run). For first-run validation, either omit `--no-restore` OR run `dotnet build` on the test project first. Subsequent reruns honor `--no-restore` correctly.
- In linked git worktrees, set `OPENCLAW_REPO_ROOT` to the worktree path before running tests that discover the repository root, for example:
  - `$env:OPENCLAW_REPO_ROOT='D:\github\openclaw-windows-node.<worktree-name>'`
- Tray tests must isolate `SettingsManager` from real user settings. Do not use `new SettingsManager()` in tests unless the test intentionally reads `%APPDATA%\OpenClawTray\settings.json`; pass a temp settings directory or set `OPENCLAW_TRAY_DATA_DIR` before the test process starts.
- Prefer isolated worktrees for PR validation. Use `git-wt` for worktree workflows; `wt.exe` may resolve to WorkTrunk instead of Windows Terminal, so use the full Windows Terminal path when explicitly launching Terminal.
- Do not claim completion without reporting validation results.

## Active Work Status

Apply these metadata updates only when authorized to update the named issue or
PR. Local implementation or a read-only review does not itself grant that authority.

- Apply the `status: 🚢 actively landing` label when a maintainer or delegated agent takes active ownership of an issue or pull request through implementation, conflict resolution, validation, or merge.
- Apply the label only to work that is actively moving. Do not use it for queued, parked, or indefinitely blocked items.
- Remove the label when active work pauses, ownership returns to the author, or the item closes or merges.
- This maintainer-ownership label may coexist with contributor-facing status labels such as `status: 📣 needs proof`.
- When referring to a pull request, include its title in parentheses after the number, for example `#1312 (improve(setup): install latest stable Gateway from npm)`.

## Targeted Validation Paths

Run the required validation above for every code change, then add the targeted path that matches the touched subsystem.

### MXC / `system.run` / Windows node command execution

When changing MXC sandboxing, `system.run`, exec approvals, Windows node command execution, gateway setup/connect E2E behavior, or files under `src\OpenClaw.Shared\Mxc`, run:

```powershell
.\scripts\validate-mxc-e2e.ps1
```

The script sets `OPENCLAW_RUN_E2E` and `OPENCLAW_RUN_MXC_E2E` itself, then runs the real WSL Gateway -> Windows node -> `system.run` MXC E2E proofs. It fails if the MXC proof skips. Use `-AllowSkip` only to document that the current host is not MXC-capable; do not report an `-AllowSkip` run as merge validation for MXC-related work.

## Coding rules

- Keep methods focused and reduce nested control flow when it improves readability.
- Prefer existing repository patterns and simple, idiomatic C# over speculative abstractions.
- Document non-obvious ownership, invariants, and public usage. Avoid boilerplate comments that only repeat the code.

## UI, MCP, and PR Proof

Use the [proof skill](.agents/skills/openclaw-proof-validation/SKILL.md) when a change touches tray UX, Settings, onboarding, chat/canvas, Command Center, Windows node capabilities, MCP, gateway connection/pairing, permissions, diagnostics, or agent-facing instructions. Match evidence to the changed contract; ordinary documentation changes do not require launching the app.

## User-Facing Copy

Do not use em dashes in user-facing prose, including UI text, error messages, CLI output, notifications, and agent-facing help. Use a period, colon, comma, parentheses, or a simple hyphen instead. A standalone em dash is allowed as an unavailable-value or status placeholder.

Policy:

- Required automated/focused tests are mandatory; do not ask to skip them.
- Prefer computer-use as a batched closeout proof pass before PRs. If UI proof is useful mid-development, first ask whether to run computer-use now or provide manual steps so the developer can capture screenshots/output.
- For UI claims, collect current-head visible proof of the active changed state: computer-use screenshot/video, developer-provided screenshot, copied UI diagnostics, or an explicit blocker.
- If the developer captures UI proof manually, run or point them at the current isolated app, provide exact reproduction/capture steps, and verify any PR screenshot/artifact links after updating the PR body.
- For node/MCP changes, prove discovery and invocation with `winnode --list-tools` plus `winnode --command ...`, or raw MCP JSON-RPC `tools/list` plus `tools/call`.
- For gateway-mediated behavior, prove the real gateway path when available; otherwise state the blocker and keep MCP proof.
- Run rubber-duck review before PR publication for non-trivial UI, MCP, node-command, setup, pairing, security, permissions, or diagnostics changes.
- PRs should include `## Required proof pools`, `## Validation`, and `## Real behavior proof`. Select every applicable pool ID from [`docs/PROOF_POOLS.md`](docs/PROOF_POOLS.md), or declare `none` with a reason. Proof must directly show the changed behavior from the current PR head. Fill `Not verified / blocked` for focused proof or unavailable dependencies.

Every new Windows node call must be exposed, documented, and tested through MCP before completion:

1. Register the capability/command in the tray node capability registry.
2. Add/update `McpToolBridge.CommandDescriptions`.
3. Update `.agents/skills/winnode/SKILL.md`.
4. Add/update capability, MCP bridge, `winnode`, and UI/gateway tests as appropriate.
5. Run required validation plus `dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore` when `winnode`, MCP output, or command docs change.

## Telemetry Guardrails

Read `docs/TELEMETRY.md` before adding or changing OpenTelemetry configuration, exporters, instrumentation helpers, span attributes, metric tags, or exported log fields.

- Do not add OpenTelemetry SDK/exporter package references to `OpenClaw.Shared`; shared helpers may use `System.Diagnostics.ActivitySource`, `Activity`, and `System.Diagnostics.Metrics` only.
- Do not export user content, prompts, screenshots, file contents, raw command input/output, credentials, API keys, gateway tokens, device tokens, or arbitrary existing local logs.
- Keep telemetry opt-in: an empty endpoint must mean no OpenTelemetry export, and export must go only to the user-configured endpoint.
- Prefer low-cardinality operational diagnostics: operation names, outcomes, durations, counts, protocol choices, and coarse error categories.
- Add focused tests for new span names, metric names, tag keys, log categories, filtering behavior, and endpoint/protocol behavior.
- For exporter or protocol changes, provide current-head collector proof for every affected signal and protocol, or document the blocker.

## Architecture Context for New Agents

Read the guide for the behavior and owner being changed, including callers
outside that owner's directory. Follow relevant references when a contract
crosses boundaries; unrelated rows are not a mandatory reading list.

| When changing | Required context |
|---|---|
| Any owner named in the living ledger | [ARCHITECTURE.md](docs/ARCHITECTURE.md): read its rows; preserve `authoritative` owners and never re-add `closed` responsibilities |
| Connection, credentials, migration, pairing, or reconnect | [CONNECTION_ARCHITECTURE.md](docs/CONNECTION_ARCHITECTURE.md) |
| MCP hosting or the node/MCP mode matrix | [MCP_MODE.md](docs/MCP_MODE.md) |
| Windows node capabilities or Gateway invocation | [WINDOWS_NODE_TESTING.md](docs/WINDOWS_NODE_TESTING.md) |
| First-run/setup-code/bootstrap pairing | [ONBOARDING_WIZARD.md](docs/ONBOARDING_WIZARD.md) |
| Setup pipeline, rollback, context injection, or setup CLI | [SETUP_ENGINE_REDESIGN.md](docs/SETUP_ENGINE_REDESIGN.md) |
| Multi-line WSL scripts through `RunInWslAsync` | [WSL_EXE_ARGV_PITFALL.md](docs/WSL_EXE_ARGV_PITFALL.md) |

## Architecture Guardrails for Large Refactors

`src\OpenClaw.Tray.WinUI\App.xaml.cs` and `src\OpenClaw.Tray.WinUI\Pages\ConnectionPage.xaml.cs` are active god-file reduction targets. When touching either file:

- **Read `docs/ARCHITECTURE.md` first.** It is the living ownership ledger. Before editing any file it lists, check its row(s); do not re-add anything marked `closed`. When you extract a responsibility, update the ledger in the same PR (flip/add the new owner to `authoritative`, mark the vacated responsibility `closed`) and add a guard test for high-regression closures. A PR that re-adds a `closed` responsibility must be rejected in review.
- Prefer completing a real ownership transfer over moving code to partial classes. A new partial file is not progress unless it introduces a narrower owner, pure projection, policy, service, or tested seam.
- Keep `App` as the composition root. Shrink it by delegating cohesive behavior to focused services, but do not relocate startup ordering into another god object.
- Keep `ConnectionPage.xaml.cs` as the WinUI applicator until a pure row/plan/workflow seam exists. Do not move named-control setters into a presenter that just wraps the page.
- Add characterization tests before moving startup, credential, pairing, node/MCP, tray action, or direct-connect rollback behavior. Source-text contract tests are acceptable for WinUI-only seams, but prefer pure unit tests for policies and projections.
- Keep PRs small and reviewable: one seam per PR, with a clear invariant protected by tests. Stop and re-plan if a PR moves hundreds of lines without behavior coverage.
- In PR descriptions and handoffs, name the old owner, new owner, preserved invariant, and validation run so future agents do not reintroduce duplicate paths or grow new god objects.

Important current facts:

- Gateway credentials are no longer stored in `SettingsData.Token` / `SettingsData.BootstrapToken`. `SettingsManager` may read legacy JSON fields only for one-time migration; new writes must go through `GatewayRegistry`.
- Active gateway records live in `%APPDATA%\OpenClawTray\gateways.json`; per-gateway identity files live under `%APPDATA%\OpenClawTray\gateways\<gateway-id>\device-key-ed25519.json`.
- Credential precedence is device token, then shared gateway token, then bootstrap token. Do not downgrade a paired device from its stored device token back to a bootstrap/shared token.
- `GatewayConnectionManager` owns operator/node connection state. UI surfaces should observe it or call its reconnect/disconnect APIs instead of constructing parallel gateway clients.
- Chat/canvas/tray actions must visibly route users to Connection settings when pairing is incomplete or credentials are missing; avoid silent no-ops.
- MCP-only mode (`EnableMcpServer=true`, `EnableNodeMode=false`) must start local `NodeService` without requiring a gateway credential.

## Autoreview priority

Use `--max-priority P3` with the shared autoreview helper to preserve this
repository's existing P0–P3 review gate. Follow the
[shared skill setup](.agents/skills/autoreview/SKILL.md) before running it.
