# AGENTS.md

## Required Validation After Every Change

All agents working in this repository must run validation after each code change before marking work complete.

Required steps:

1. Run full repo build:
   - `./build.ps1`
2. Run shared tests:
   - `dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore`
3. Run tray tests:
   - `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore`

If a command fails:

1. Fix the issue.
2. Re-run the failed command.
3. Re-run all required validation commands before completion.

Notes:

- If a build/test is blocked by an environmental lock (for example running executable locking output assemblies), stop/close the locking process and rerun.
- **First-run gotcha**: `dotnet test --no-restore` silently no-ops in a fresh worktree where the test `bin/` doesn't exist yet (reports "Build succeeded in 0.5s" then exits 0 with no tests run). For first-run validation, either omit `--no-restore` OR run `dotnet build` on the test project first. Subsequent reruns honor `--no-restore` correctly.
- In linked git worktrees, set `OPENCLAW_REPO_ROOT` to the worktree path before running tests that discover the repository root, for example:
  - `$env:OPENCLAW_REPO_ROOT='D:\github\openclaw-windows-node.<worktree-name>'`
- Tray tests must isolate `SettingsManager` from real user settings. Do not use `new SettingsManager()` in tests unless the test intentionally reads `%APPDATA%\OpenClawTray\settings.json`; pass a temp settings directory or set `OPENCLAW_TRAY_DATA_DIR` before the test process starts.
- Prefer isolated worktrees for PR validation. Use `git-wt` for worktree workflows; `wt.exe` may resolve to WorkTrunk instead of Windows Terminal, so use the full Windows Terminal path when explicitly launching Terminal.
- Do not claim completion without reporting validation results.

## Targeted Validation Paths

Run the required validation above for every code change, then add the targeted path that matches the touched subsystem.

### MXC / `system.run` / Windows node command execution

When changing MXC sandboxing, `system.run`, exec approvals, Windows node command execution, gateway setup/connect E2E behavior, or files under `src\OpenClaw.Shared\Mxc`, run:

```powershell
.\scripts\validate-mxc-e2e.ps1
```

The script sets `OPENCLAW_RUN_E2E` and `OPENCLAW_RUN_MXC_E2E` itself, then runs the real WSL Gateway -> Windows node -> `system.run` MXC E2E proofs. It fails if the MXC proof skips. Use `-AllowSkip` only to document that the current host is not MXC-capable; do not report an `-AllowSkip` run as merge validation for MXC-related work.

## UI, MCP, and PR Proof

Use `.agents/skills/openclaw-proof-validation/SKILL.md` when a change touches tray UX, Settings, onboarding, chat/canvas, Command Center, Windows node capabilities, MCP, gateway connection/pairing, permissions, diagnostics, or agent-facing instructions.

Policy:

- Required automated/focused tests are mandatory; do not ask to skip them.
- Prefer computer-use as a batched closeout proof pass before PRs. If UI proof is useful mid-development, first ask whether to run computer-use now or provide manual steps so the developer can capture screenshots/output.
- For UI claims, collect current-head visible proof of the active changed state: computer-use screenshot/video, developer-provided screenshot, copied UI diagnostics, or an explicit blocker.
- If the developer captures UI proof manually, run or point them at the current isolated app, provide exact reproduction/capture steps, and verify any PR screenshot/artifact links after updating the PR body.
- For node/MCP changes, prove discovery and invocation with `winnode --list-tools` plus `winnode --command ...`, or raw MCP JSON-RPC `tools/list` plus `tools/call`.
- For gateway-mediated behavior, prove the real gateway path when available; otherwise state the blocker and keep MCP proof.
- Run rubber-duck review before PR publication for non-trivial UI, MCP, node-command, setup, pairing, security, permissions, or diagnostics changes.
- PRs should include `## Validation` and `## Real behavior proof`; proof must directly show the changed behavior from the current PR head. Fill `Not verified / blocked` for focused proof or unavailable dependencies.

Every new Windows node call must be exposed, documented, and tested through MCP before completion:

1. Register the capability/command in the tray node capability registry.
2. Add/update `McpToolBridge.CommandDescriptions`.
3. Update `src/OpenClaw.WinNode.Cli/skill.md`.
4. Add/update capability, MCP bridge, `winnode`, and UI/gateway tests as appropriate.
5. Run required validation plus `dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore` when `winnode`, MCP output, or command docs change.

## Architecture Context for New Agents

Start with these docs before changing connection, pairing, node, MCP, or tray UX behavior:

- `docs/CONNECTION_ARCHITECTURE.md` - current gateway registry, connection manager, credential precedence, migration, MCP-only, and tray action behavior.
- `docs/MCP_MODE.md` - local MCP server mode and the `EnableNodeMode` / `EnableMcpServer` matrix.
- `docs/WINDOWS_NODE_TESTING.md` - Windows node capabilities, manual smokes, and gateway-dependent behavior.
- `docs/ONBOARDING_WIZARD.md` - first-run setup flow, setup-code/bootstrap pairing, and test isolation.

Important current facts:

- Gateway credentials are no longer stored in `SettingsData.Token` / `SettingsData.BootstrapToken`. `SettingsManager` may read legacy JSON fields only for one-time migration; new writes must go through `GatewayRegistry`.
- Active gateway records live in `%APPDATA%\OpenClawTray\gateways.json`; per-gateway identity files live under `%APPDATA%\OpenClawTray\gateways\<gateway-id>\device-key-ed25519.json`.
- Credential precedence is device token, then shared gateway token, then bootstrap token. Do not downgrade a paired device from its stored device token back to a bootstrap/shared token.
- `GatewayConnectionManager` owns operator/node connection state. UI surfaces should observe it or call its reconnect/disconnect APIs instead of constructing parallel gateway clients.
- Chat/canvas/tray actions must visibly route users to Connection settings when pairing is incomplete or credentials are missing; avoid silent no-ops.
- MCP-only mode (`EnableMcpServer=true`, `EnableNodeMode=false`) must start local `NodeService` without requiring a gateway credential.
