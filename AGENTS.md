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
- In linked git worktrees, set `OPENCLAW_REPO_ROOT` to the worktree path before running tests that discover the repository root, for example:
  - `$env:OPENCLAW_REPO_ROOT='D:\github\moltbot-windows-hub.<worktree-name>'`
- Tray tests must isolate `SettingsManager` from real user settings. Do not use `new SettingsManager()` in tests unless the test intentionally reads `%APPDATA%\OpenClawTray\settings.json`; pass a temp settings directory or set `OPENCLAW_TRAY_DATA_DIR` before the test process starts.
- Prefer isolated worktrees for PR validation. Use `git-wt` for worktree workflows; `wt.exe` may resolve to WorkTrunk instead of Windows Terminal, so use the full Windows Terminal path when explicitly launching Terminal.
- Do not claim completion without reporting validation results.

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
