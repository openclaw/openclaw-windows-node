---
name: openclaw-proof-validation
description: "Plan and collect OpenClaw Windows validation/proof: tests, rubber-duck review, UI evidence, MCP output, and gateway runtime proof."
---

# OpenClaw Proof and Validation

Use for changes that affect tray UX, Settings, onboarding, chat/canvas, Command Center, Windows node capabilities, local MCP, gateway connection/pairing, permissions, diagnostics, or agent-facing instructions.

If the validation host is macOS, read [PARALLELS.md](PARALLELS.md) for the optional local Parallels Windows VM workflow.

## Rules

- Required automated/focused tests are mandatory. Do not ask to skip them.
- Prefer isolated tray data so proof does not mutate `%APPDATA%\OpenClawTray`.
- Computer-use is usually a batched closeout proof pass, not a continuous dev-loop tool. Mid-development use is fine when explicitly requested or needed to unblock work; ask first whether to run it or provide manual screenshot/output steps.
- Local MCP is part of every Windows node command contract: command discovery and invocation must work through `winnode` or raw MCP JSON-RPC.
- Rubber-duck review is required before PR publication for non-trivial UI/MCP/node-command/setup/pairing/security/permissions/diagnostics work; it is also useful mid-development when extra design/testing validation is requested.
- Report blockers explicitly. Do not turn missing UI, MCP, gateway, camera, screen, or permission proof into success-shaped wording.

## Required validation

First follow [execution-target routing](../../../docs/TEST_COVERAGE.md#choose-the-local-execution-target):
app-only proof defaults to the isolated fixture Gateway; native-Gateway E2E
defaults to [named side-by-side patches](../native-gateway-from-source/SKILL.md#default-multi-instance-native-gateway-testing)
on an authorized, capable Windows client host, locally or in future cloud lanes.
Use separate VMs for OS/clean-image isolation or unavailable host capabilities,
not one VM per Gateway. Preserve required real MXC/WSL proof and explicit
authority for existing shared Gateways.

Select the changed contract before running checks. Use the
[read-only validation plan](../../../docs/TEST_COVERAGE.md#explain-the-validation-plan)
to distinguish hosted lanes, mandatory local code-change closeout, and custom
proof candidates. A candidate is neither scheduled nor executed proof.
Ordinary prose-only changes need documentation/skill checks, not native runtime
tests. Shipped `winnode` command-reference changes retain CLI/core checks.

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

Fresh worktrees may need a first run without `--no-restore`, or a project build first, so tests do not no-op before `bin\` exists.
Check actual report/execution counts, not only the exit code. Custom proof must
not contain skipped selected cases. Ordinary CI skips remain a separate policy,
not proof that the skipped behavior works.

For `winnode`, command descriptions, or new/renamed node commands, also run:

```powershell
dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore
```

## Proof checklist

### Select the runtime and its authority

Ordinary prose validation does not need an app. For supported deterministic app
exploration, prefer the [fixture workflow](../../../docs/GATEWAY_FIXTURE_TESTING.md#build-and-explore)
with an explicit current-build executable on an authorized Windows desktop.
It uses synthetic Gateway data, isolated profiles and a dedicated MCP port,
with owned-process cleanup. Sensitive node capabilities, automatic repair and
voice are disabled. It is not an OS sandbox, node-command proof, or real
Gateway pairing/provider proof. An interactive `ready` result is not a passed
scenario.

For live app proof outside that fixture, select the Gateway before launching.
For an isolated build that needs a real native Gateway, the default is the
[task-owned build and named-patch workflow](../native-gateway-from-source/SKILL.md#prepare-an-isolated-test-build):
build Companion in its own worktree, prepare a uniquely named Gateway patch in
a task-owned work root, and launch with that patch and separate app data.
The launcher's shared `npm-latest` / `packaged` defaults are not per-test
instances; `-Isolated` alone does not create a unique Gateway.
The [local launcher](../../../run-app-local.ps1) has these distinct effects:

| Option | Actual boundary |
|---|---|
| `-Isolated` / `-DataDir` | Selects separate profile data, not an OS sandbox or permission to change machine runtimes. `-Isolated` uses a stable worktree/branch directory, not a fresh profile on every run. |
| Default Gateway (no explicit selection) | Builds/registers an npm-latest development native Gateway, or reuses an existing patch selected by `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH` without rebuilding. |
| `-GatewayChannel Latest` | Builds/registers an npm-latest development native Gateway. Conflicts with a nonblank `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH`; clear that variable before selecting an explicit channel. |
| `-GatewayChannel Packaged` | Builds/registers a development Gateway from a published packaging release, not the installed Store runtime. Like `Latest`, conflicts with a nonblank `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH`. |
| `-UseStoreGateway` | Skips development Gateway packaging and selects the installed Store Gateway. It does not make the launched app read-only. |
| `-NoBuild` | Skips only the Companion build. Requires existing output with matching identity/configuration/RID; Gateway preparation can still run. |
| `-DryRun` | Returns before Gateway packaging and app launch, but after the optional Companion build and profile-directory creation. Not a read-only planner, even with `-NoBuild`. |

Local implementation authority does not authorize Gateway registration, installs,
or shared-runtime changes. Use the
[native Gateway skill](../native-gateway-from-source/SKILL.md) only with the
needed authority. Do not execute the launcher to discover its parameters;
inspect its PowerShell AST for read-only command checks. There is no
`-AllowNonMain` switch.

For example, only when use of the installed Store Gateway and a live app launch
are authorized:

```powershell
.\run-app-local.ps1 -Isolated -UseStoreGateway
```

Expect the launcher to print the selected profile, Gateway, output path and
process identity. Record the actual build/runtime identity; isolation alone is
not evidence that the binary matches the current source.

### Collect the selected proof

| Surface | Proof to collect |
|---|---|
| UI / WinUI | On an authorized isolated desktop, use the runtime selected above, exercise the changed path with computer-use or developer-provided screenshots/output, and include visible evidence or blocker. If the developer captures manually, provide exact steps and confirm screenshot/artifact links resolve after an authorized PR body update. |
| Local MCP | Enable **Local MCP Server**, run `winnode --list-tools`, then invoke the changed command with `winnode --command <name> --params '<json-object>'`. |
| Raw MCP HTTP | For protocol/server-shape changes, paste JSON-RPC `tools/list` and `tools/call` responses from `http://127.0.0.1:8765/`. |
| Gateway path | When relevant and available, prove `openclaw nodes invoke --command <name> --params '<json-object>'`; otherwise state the gateway blocker. |
| Rubber-duck | Inspect the current host's advertised tools/roles. When `task` advertises `rubber-duck`, use it for a bounded independent implementation/proof-plan review. Otherwise use an advertised independent review role and name the actual tool/role and limits; if none exists, report independent review blocked. Direct self-review is not independent review. Verify findings before changing code. |

For shared source-review mode, base, P0-P3 scope and fallback requirements, use
the [autoreview entrypoint](../autoreview/SKILL.md). Host tool availability does
not establish model access or a completed review. Keep source review separate
from live UI, MCP and Gateway proof.

## When local MCP proof is refused

Connection refused on `http://127.0.0.1:8765/` means the tray MCP server is not running. Do not reinstall `winnode`. Write PascalCase `settings.json` with `EnableMcpServer` true and `EnableNodeMode` false, launch the tray with `OPENCLAW_TRAY_DATA_DIR` and `OPENCLAW_SKIP_UPDATE_CHECK=1`, and pass that same data directory to `winnode`. `StartupSetupState.RequiresSetup` stays false when MCP is already enabled, so the first-run wizard does not block. An isolated data directory uses its own mutex. Wait until `mcp-token.txt` exists and the port accepts a connection.

`browser.proxy` stays off `tools/list` until the app has a gateway client, a non-empty shared gateway token, and a verified browser endpoint. `Unknown tool: browser.proxy` is that rule. A refused port is not successful discovery. The review can accept a concrete gateway-availability blocker. It does not accept the refused port as the `browser.proxy` call.

When the review asks for the production HTTP client, drive that client against a real socket. `HttpClient.Timeout` does not cancel a body read that starts after `HttpCompletionOption.ResponseHeadersRead`.

For isolated tray runs, copy the data directory printed by `run-app-local.ps1 -Isolated` and set it before MCP proof commands:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<isolated-data-dir-from-run-app-local>'
```

Raw token lookup:

```powershell
$tokenPath = if ($env:OPENCLAW_TRAY_DATA_DIR) {
    Join-Path $env:OPENCLAW_TRAY_DATA_DIR 'mcp-token.txt'
} else {
    Join-Path $env:APPDATA 'OpenClawTray\mcp-token.txt'
}
$token = Get-Content $tokenPath -Raw
```

## New Windows node command checklist

1. Register the command in the same `INodeCapability` path used by the gateway node.
2. Add/update `McpToolBridge.CommandDescriptions`.
3. Update `.agents/skills/winnode/SKILL.md` with input shape, output shape, side effects, permissions, and examples.
4. Add/update capability, MCP bridge, `winnode`, and UI/gateway tests as applicable.
5. Prove discovery and invocation with `winnode` or raw MCP JSON-RPC.
6. Prove the gateway path when the behavior is gateway-mediated and a gateway is available.

## PR proof package

This section owns evidence requirements and their limits. Use
[Windows PR preparation](../windows-pr-prepare/SKILL.md) to assemble that evidence
into the current PR template and report readiness without assuming publication
authority.

Before publishing or updating a PR, collect:

1. `## Validation` with exact commands and pass/fail counts.
2. `## Real behavior proof` with current-head after-change evidence that directly shows the changed behavior: copied live output, screenshot/video, developer-provided screenshot, copied UI diagnostics, `winnode`, raw MCP JSON-RPC, gateway invoke output, redacted runtime log, or linked artifact. For UI changes, prefer screenshots/video of the active changed state, not only adjacent or empty UI.
3. PR body proof must stay in sync with current behavior: remove stale screenshots/claims after design changes and verify embedded image/artifact links resolve.
4. Rubber-duck notes for non-trivial UI/MCP-sensitive work, or why skipped.
5. `Not verified / blocked` notes for focused proof or unavailable dependencies.

For inventory-bound filtered tests, use
[local execution evidence validation](../../../docs/PROOF_POOLS.md#validate-local-execution-evidence).
The summary checks source/report/artifact correspondence; it does not replace
native freeze/binary verification, visible UI inspection, or live Gateway proof,
and it never marks a whole pool complete.
