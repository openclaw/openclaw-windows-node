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
