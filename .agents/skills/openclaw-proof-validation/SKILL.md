---
name: openclaw-proof-validation
description: Collect runtime and PR proof (UI, local MCP, Gateway) for a Windows-node behavior change.
---

# OpenClaw proof validation

Use when a change affects tray UX, Settings, onboarding, chat/canvas, Command
Center, Windows node capabilities, local MCP, Gateway connection or pairing,
permissions, or diagnostics. A docs or prose-only change needs no runtime
proof; use [technical-documentation](../technical-documentation/SKILL.md).

Tests follow the [validation policy](../../../docs/TEST_COVERAGE.md#validation-policy).
Review follows the [review policy](../../../docs/AGENT_WORKFLOWS.md#when-review-is-required).
New node commands follow the
[new command contract](../../../docs/WINDOWS_NODE_TESTING.md#new-command-mcp-contract).
If the validation host is macOS, read [PARALLELS.md](PARALLELS.md).

## Rules

- Prefer isolated tray data so proof does not mutate `%APPDATA%\OpenClawTray`.
- Ask before a computer-use pass, or offer manual screenshot or output steps
  instead.
- Report blockers explicitly. Do not turn missing UI, MCP, Gateway, camera,
  screen, or permission proof into success-shaped wording.
- Custom proof must not contain skipped selected cases.

## Choose the execution target

Choose by the behavior under test, not by a name containing `Mxc`. This is
testing guidance, not CI migration, host reservation, or a scheduler. Keep every
applicable proof pool.

| Behavior to prove | Default target and boundary |
|---|---|
| Build, unit/component logic, pure MXC policy and mocked capabilities | Task-owned host worktree with isolated test settings; no VM needed solely because the code concerns MXC |
| App layout, navigation, chat/history and fixture-covered client behavior | [Isolated app with the fixture Gateway](../../../docs/GATEWAY_FIXTURE_TESTING.md); not real pairing, node invocation, or MXC proof |
| Isolated test builds needing a real native Gateway | [Named side-by-side development patches](../native-gateway-from-source/SKILL.md#prepare-an-isolated-test-build) on one authorized, capable Windows 11 host with Developer Mode |
| Integration with an existing Gateway | Operator-approved endpoint and operations, with a task-owned app profile; discovery is not permission to reconfigure, restart, or use a shared runtime |
| Clean machine, signed-package install/upgrade, OS isolation, or unavailable host capabilities | A separate local or approved cloud Windows client VM; development patches do not prove the signed Store/MSIX lifecycle |
| Required WSL Gateway or MXC `system.run` path | The real WSL suite, including `.\scripts\validate-mxc-e2e.ps1`, on an authorized capable host or VM |

`run-app-local.ps1 -Isolated` isolates only the app profile. It does not
allocate a unique Gateway. Never stop another task's runtime or copy the user's
profile or credentials to make a test pass. Unsupported or skipped proof stays
blocked.

## Proof checklist

### Select the runtime and its authority

For deterministic app exploration, prefer the
[fixture workflow](../../../docs/GATEWAY_FIXTURE_TESTING.md#build-and-explore)
with an explicit current-build executable on an authorized Windows desktop. It
uses synthetic Gateway data and is not an OS sandbox, node-command proof, or
real Gateway pairing or provider proof. An interactive `ready` result is not a
passed scenario.

For live app proof outside the fixture, select the Gateway before launching.
For a real native Gateway, use the
[named-patch workflow](../native-gateway-from-source/SKILL.md#prepare-an-isolated-test-build).
The [local launcher](../../../run-app-local.ps1) options have these effects:

| Option | Actual boundary |
|---|---|
| `-Isolated` / `-DataDir` | Selects separate profile data, not an OS sandbox or permission to change machine runtimes. `-Isolated` uses a stable worktree/branch directory, not a fresh profile on every run. |
| Default Gateway (no explicit selection) | Builds/registers an npm-latest development native Gateway, or reuses an existing patch selected by `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH` without rebuilding. |
| `-GatewayChannel Latest` | Builds/registers an npm-latest development native Gateway. Conflicts with a nonblank `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH`; clear that variable before selecting an explicit channel. |
| `-GatewayChannel Packaged` | Builds/registers a development Gateway from a published packaging release, not the installed Store runtime. Like `Latest`, conflicts with a nonblank `OPENCLAW_NATIVE_GATEWAY_DEV_PATCH`. |
| `-UseStoreGateway` | Skips development Gateway packaging and selects the installed Store Gateway. It does not make the launched app read-only. |
| `-NoBuild` | Skips only the Companion build. Requires existing output with matching identity/configuration/RID; Gateway preparation can still run. |
| `-DryRun` | Returns before Gateway packaging and app launch, but after the optional Companion build and profile-directory creation. Not a read-only planner, even with `-NoBuild`. |

Local implementation authority does not authorize Gateway registration,
installs, or shared-runtime changes. Do not run the launcher to discover its
parameters; inspect its PowerShell AST instead. Record the build and runtime
identity the launcher prints. Isolation alone does not prove the binary matches
the current source.

### Collect the selected proof

| Surface | Proof to collect |
|---|---|
| UI / WinUI | On an authorized isolated desktop, exercise the changed path and capture screenshots, video, or output of the active changed state. If the developer captures it, give exact steps. |
| Local MCP | Run `winnode --list-tools`, then `winnode --command <name> --params '<json-object>'`. For setup, tokens, and a refused port, use [windows-app-mcp](../windows-app-mcp/SKILL.md). |
| Raw MCP HTTP | For protocol or server-shape changes, paste JSON-RPC `tools/list` and `tools/call` responses. |
| Gateway path | When relevant and available, prove `openclaw nodes invoke --command <name> --params '<json-object>'`; otherwise state the Gateway blocker. |

Keep source review separate from live UI, MCP, and Gateway proof.

## PR proof package

This section owns evidence requirements and their limits.
[Windows PR preparation](../windows-pr-prepare/SKILL.md) assembles them into
the PR template without assuming publication authority. Collect:

1. `## Validation`: exact commands and pass/fail counts.
2. `## Real behavior proof`: current-head evidence that directly shows the
   changed behavior (live output, screenshot or video, UI diagnostics,
   `winnode` or MCP JSON-RPC output, Gateway invoke output, redacted log, or a
   linked artifact). For UI, show the active changed state.
3. Keep PR proof in sync: remove stale screenshots or claims and confirm
   embedded links resolve.
4. Review notes when the review policy requires a review.
5. `Not verified / blocked` notes for focused proof or unavailable dependencies.

For inventory-bound filtered tests, use
[local execution evidence validation](../../../docs/PROOF_POOLS.md#validate-local-execution-evidence).
The summary checks source/report/artifact correspondence; it does not replace
native freeze/binary verification, visible UI inspection, or live Gateway proof,
and it never marks a whole pool complete.
