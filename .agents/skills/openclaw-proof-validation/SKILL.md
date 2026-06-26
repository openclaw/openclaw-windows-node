---
name: openclaw-proof-validation
description: "Plan and collect OpenClaw Windows validation/proof: tests, rubber-duck review, UI evidence, MCP output, and gateway runtime proof."
---

# OpenClaw Proof and Validation

Use this skill when a change touches any user-visible tray surface, Settings, onboarding, chat/canvas, Command Center, Windows node capability, local MCP server behavior, gateway pairing/connection behavior, permissions, diagnostics, or agent-facing skill/instruction surface.

## Contract

- Validate the behavior through the surface the user or agent will actually use.
- Prefer an isolated tray data directory so validation does not mutate the user's normal `%APPDATA%\OpenClawTray` profile.
- Use computer-use / desktop automation tools for visible WinUI behavior during a batched closeout proof pass by default, not continuously during normal development. Mid-development computer-use is still appropriate when the developer explicitly asks for extra validation or UI proof is necessary to unblock the work; ask first whether to run computer-use now or provide manual instructions so the developer can run the UI path and share screenshots/output. Code inspection and unit tests are not a substitute for exercising the UI, but repeated desktop acquisition can block the user's other work.
- Use local MCP validation for node capability changes even when gateway validation is unavailable.
- Treat local MCP as part of the contract for every new Windows node call. A command is not complete until it appears in MCP `tools/list`, works through `winnode` or a raw MCP `tools/call`, and is documented in the `winnode` skill reference.
- Run rubber-duck review before PR publication for non-trivial UI, MCP, node-command, setup, pairing, security, permissions, or diagnostics changes; also use it mid-development when the developer wants extra design/testing validation.
- Always enforce automated/focused tests. Do not ask whether to skip required validation.
- Report blockers explicitly. Do not make success-shaped claims when a UI, permission, gateway, camera, screen, or MCP dependency could not be exercised.

## Required baseline

Run the repo-required validation before closeout:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

In a fresh worktree, run each test project once without `--no-restore` or build it first so `dotnet test --no-restore` cannot no-op before `bin\` exists.

If the change touches `winnode`, command descriptions, or a new/renamed node command, also run:

```powershell
dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore
```

## UI smoke checklist

Run this as one larger closeout pass after implementation and automated/focused tests are complete by default. Do not keep the desktop locked for iterative development unless the user explicitly asks for live UI pairing, wants extra validation, or the work is blocked without visible UI proof. When UI proof is useful mid-development, recommend it and ask whether to run computer-use now or provide manual steps/screenshots checklist.

1. Launch the tray from the current worktree:

   ```powershell
   .\run-app-local.ps1 -Isolated
   ```

2. Use computer-use / desktop automation to exercise the changed path:
   - Open the tray or the target deep link, for example `openclaw://settings`, `openclaw://commandcenter`, or `openclaw://chat`.
   - Verify labels, status, enabled/disabled states, error messages, and navigation match the intended behavior.
   - Save/restart when persistence is part of the change.
   - Capture enough visible evidence in the final report to show the UI path was actually exercised.

3. Check logs when behavior depends on background services:

   ```powershell
   Get-Content "$env:LOCALAPPDATA\OpenClawTray\openclaw-tray.log" -Tail 80
   ```

## Rubber-duck closeout

Before PR publication for non-trivial UI/MCP-sensitive work, ask a rubber-duck reviewer to look for logic gaps in the implementation and proof plan. It is also appropriate mid-development when the developer wants extra validation before continuing. Provide the reviewer with:

1. The changed files and intended behavior.
2. The automated validation commands and results.
3. The planned UI/computer-use, MCP, raw MCP, and gateway proof.
4. Any known blockers or intentionally out-of-scope proof.

Treat rubber-duck output as advisory: verify any finding against the code and scope before making changes.

## Local MCP smoke checklist

1. In the tray Settings UI, enable **Local MCP Server** and save. MCP-only mode must work without requiring a gateway credential.
2. Confirm the endpoint and token are available in Settings, or read the isolated token file written by the tray.
3. If the tray was launched with `.\run-app-local.ps1 -Isolated`, copy the isolated data directory printed by the launch script and set it in every proof shell before calling `winnode` or reading the token:

   ```powershell
   $env:OPENCLAW_TRAY_DATA_DIR = '<isolated-data-dir-from-run-app-local>'
   ```

   Then read the raw MCP token from `$env:OPENCLAW_TRAY_DATA_DIR\mcp-token.txt`. Use `$env:APPDATA\OpenClawTray\mcp-token.txt` only for non-isolated runs.
4. Verify the live tool surface. `winnode --list-tools` is the preferred operator-friendly proof:

   ```powershell
   winnode --list-tools
   ```

5. Invoke the changed command or the nearest representative command through `winnode`:

   ```powershell
   winnode --command system.notify --params '{"title":"OpenClaw validation","body":"MCP smoke succeeded"}'
   ```

6. Raw MCP server output is also valid proof, and is preferred for HTTP-level protocol changes. Include both `tools/list` and `tools/call` JSON-RPC output when the server shape itself is what changed:

   ```powershell
   $tokenPath = if ($env:OPENCLAW_TRAY_DATA_DIR) { Join-Path $env:OPENCLAW_TRAY_DATA_DIR 'mcp-token.txt' } else { Join-Path $env:APPDATA 'OpenClawTray\mcp-token.txt' }
   $token = Get-Content $tokenPath -Raw
   curl.exe -s http://127.0.0.1:8765/ -H "Authorization: Bearer $token"
   curl.exe -s -X POST http://127.0.0.1:8765/ `
     -H "Authorization: Bearer $token" `
     -H "Content-Type: application/json" `
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
   curl.exe -s -X POST http://127.0.0.1:8765/ `
     -H "Authorization: Bearer $token" `
     -H "Content-Type: application/json" `
     -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"system.notify","arguments":{"title":"OpenClaw validation","body":"Raw MCP smoke succeeded"}}}'
   ```

## New command / MCP update checklist

Use this checklist whenever adding, renaming, or changing a Windows node command:

1. Register the capability in the same `INodeCapability` path used by the gateway node.
2. Ensure the command appears via local MCP `tools/list`; the bridge auto-discovers registered commands, but `McpToolBridge.CommandDescriptions` must still describe each command.
3. Update `src/OpenClaw.WinNode.Cli/skill.md` with the command name, argument shape, output contract, side effects, and security/permission notes.
4. Add/update tests for the capability, MCP bridge behavior, `winnode` invocation/argument parsing, and any WinUI or gateway-visible behavior.
5. Run `winnode --list-tools` or raw MCP `tools/list` and paste the relevant command entry in PR proof.
6. Run `winnode --command <changed-command> --params '<json-object>'` or raw MCP `tools/call` and paste the result or exact blocker in PR proof.
7. If the command is gateway-mediated, also prove `openclaw nodes invoke --command <changed-command> --params '<json-object>'` when a gateway is available.

## Gateway-path smoke checklist

When the change affects gateway-mediated node behavior, validate the gateway path in addition to MCP when a gateway is available:

```powershell
openclaw nodes invoke --command <command-name> --params '<json-object>'
```

If the gateway is unavailable, document that the gateway-path smoke was blocked and include the local MCP proof instead.

## Closeout evidence

Final status for UI/MCP-sensitive changes should include:

| Evidence | Required detail |
|---|---|
| Automated validation | Build and test commands that passed, or exact blocker |
| Rubber-duck review | Final implementation/proof reviewed for non-trivial UI/MCP-sensitive work, or why skipped |
| Computer-use UI smoke | Batched closeout pass: surface opened and user-visible path exercised |
| MCP smoke | `winnode --list-tools` plus changed command, or raw MCP `tools/list` plus `tools/call` JSON-RPC output |
| Gateway smoke | Result when relevant and available, otherwise blocker |

## PR proof package

Before publishing or updating a PR, collect evidence in a shape reviewers and ClawSweeper can reuse:

1. `## Validation` with exact commands and pass/fail counts.
2. `## Real behavior proof` with copied after-change live output. Prefer terminal output from real commands, JSON-RPC responses, copied diagnostics, or rendered UI state over prose-only claims.
3. For non-trivial UI/MCP-sensitive changes, include rubber-duck review notes or the reason it was skipped.
4. For UI changes, include visible proof from a batched closeout pass: agent-run computer-use screenshot/video, developer-provided screenshots, copied UI diagnostics from the changed surface, or a precise note explaining why visual proof was blocked.
5. For runtime path changes, show the path explicitly, for example `Gateway -> node.invoke -> Windows node -> system.run -> result`.
6. For MCP/node changes, include both tool discovery and invocation proof. `winnode --list-tools` plus `winnode --command ...` is preferred; raw MCP server `tools/list` plus `tools/call` JSON-RPC output is equally valid and better when proving protocol-level behavior.
7. For security, network, auth, CSP, permission, camera, screen, or sandbox claims, include visible diagnostics or command output that demonstrates the gate.
8. Fill the PR template's `Not verified / blocked` proof bullet when proof is intentionally focused or a gateway/hardware/permission dependency is unavailable.
