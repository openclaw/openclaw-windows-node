# AGENTS.md

These are coding-agent instructions, not the product's end-user Gateway
workspace `AGENTS.md` templates.

## Working agreement

Read-only requests stay read-only. Permission to implement does not grant
permission to commit, push, publish, change GitHub metadata, or alter shared
runtimes; each skill keeps its own authorization rules. Preserve unrelated work
and use the current task-owned worktree, with one writer per worktree. Start
with the reported behavior, its production owner, and the existing tests. A
tool's successful exit is not proof that the intended work ran.

## Where to start

| Task | Entry point |
|---|---|
| Write or review docs and instructions | [Docs instructions](docs/AGENTS.md) and [technical-documentation](.agents/skills/technical-documentation/SKILL.md) |
| Write or review a regression test | [Test authoring](docs/TEST_COVERAGE.md#writing-a-regression-test) and [test-audit](.agents/skills/test-audit/SKILL.md) |
| Choose checks | [Validation policy](docs/TEST_COVERAGE.md#validation-policy) and [openclaw-testing](.agents/skills/openclaw-testing/SKILL.md) |
| Runtime, UI, MCP, or Gateway proof | [openclaw-proof-validation](.agents/skills/openclaw-proof-validation/SKILL.md) |
| Review code | [Review policy](docs/AGENT_WORKFLOWS.md#when-review-is-required); tools: [autoreview](.agents/skills/autoreview/SKILL.md), [hanselman-code-review](.agents/skills/hanselman-code-review/SKILL.md) |
| Publish code in a PR, or draft/edit its description | [windows-pr-prepare](.agents/skills/windows-pr-prepare/SKILL.md); code publication uses the [pre-PR checks](#before-creating-or-updating-a-pr) |
| Failing PR CI | [retrigger-flaky-ci](.agents/skills/retrigger-flaky-ci/SKILL.md) |
| Change a WinUI owner or its caller | [WinUI instructions](src/OpenClaw.Tray.WinUI/AGENTS.md) |
| Debug Companion, setup, connection, node, packaging, or `system.run` | [openclaw-debugging](.agents/skills/openclaw-debugging/SKILL.md) |
| Drive the app through local MCP | [windows-app-mcp](.agents/skills/windows-app-mcp/SKILL.md) and [winnode](.agents/skills/winnode/SKILL.md) |
| Test against a source-built native Gateway | [native-gateway-from-source](.agents/skills/native-gateway-from-source/SKILL.md) |
| Remote native Windows proof | [crabbox](.agents/skills/crabbox/SKILL.md), with explicit host authority |
| Uninstall or clean for a retest | [uninstall](.agents/skills/uninstall/SKILL.md), with inventory and confirmation |
| Full issue and PR triage | [global-repo-triage](.agents/skills/global-repo-triage/SKILL.md); do not turn a single-item request into a sweep |
| Labels and active ownership | [Repository triage](docs/REPOSITORY_TRIAGE.md#active-ownership-expiry) |
| Gateway MSIX packaging | [openclaw-windows-packaging](https://github.com/openclaw/openclaw-windows-packaging) |
| Missing local prerequisites | `.\scripts\setup-dev.ps1 -CheckOnly`; install only when authorized |
| Change this guidance | [Agent workflow checks](docs/AGENT_WORKFLOWS.md) |

## Validation

**Validate proportionally.** While working, run only the checks for what you changed or what failed. Before handing off, run one final check that covers the change. CI is the full gate, so don't repeat CI locally unless you're diagnosing a failure. Before anything that opens windows, takes focus, starts WSL or a Gateway, or runs long, say why and ask first.

A passing check on the final code counts; do not repeat it just for handoff.

Docs-only changes: run `.\scripts\validate-docs.ps1` and `git diff --check`.
Add `.\scripts\validate-agent-skills.ps1` if a skill changed, and
`.\scripts\test-agent-skills-validator.ps1` if the validator code or fixtures
changed. No builds or test suites.

Fix failures your change caused and rerun that check. Report baseline, flaky,
or environment failures with evidence instead of chasing them. Do not claim
completion without reporting what ran. Lanes and commands:
[validation policy](docs/TEST_COVERAGE.md#validation-policy) and
[formal validation paths](docs/TEST_COVERAGE.md#formal-validation-paths).

## Before creating or updating a PR

Run this gate before publishing new or changed code in a PR, or when explicitly
asked to establish code readiness. Local implementation, status reports, draft
descriptions, and metadata-only PR edits do not trigger it: use existing evidence
and name missing checks instead.

Run only missing steps, in order. Reuse results from the user or an agent when
they cover the current code; record their source rather than rerunning to obtain
your own result. A new commit SHA alone does not invalidate unchanged content.

1. [pr-simplify](.agents/skills/pr-simplify/SKILL.md) on your own change.
2. A dual-model review
   ([hanselman-code-review](.agents/skills/hanselman-code-review/SKILL.md)) of
   the change, or of what changed since the last review when updating. It meets
   the [review policy](docs/AGENT_WORKFLOWS.md#when-review-is-required).
3. The [core lane](docs/TEST_COVERAGE.md#running-tests) (build, Shared and Tray
   tests) on the final code, after any review fixes.

Do not restart the whole gate after a fix: revisit affected review findings and
checks. The review needs no desktop access. The core lane is not desktop-safe:
Tray tests include window cleanup even without a UI opt-in. Follow the
[test execution safety rules](docs/TEST_COVERAGE.md#running-tests); use an
authorized isolated desktop or ask before desktop-affecting execution. Missing
permission blocks that check, not local drafting or other safe work.

Docs-only publication needs only the docs checks above.

## Runtime and test safety

- Stop only verified task-owned processes, by PID. On a file lock held by
  anything else, report a blocker.
- Never touch another session's app, settings, desktop, Gateway, or WSL.
- Tests isolate `SettingsManager` with a temp settings directory or
  `OPENCLAW_TRAY_DATA_DIR`; do not use `new SettingsManager()` in tests.
- In linked worktrees, set `$env:OPENCLAW_REPO_ROOT` to the worktree path
  before tests that discover the repository root.
- Destructive cleanup goes through the [uninstall](.agents/skills/uninstall/SKILL.md)
  skill with an inventory and confirmation.

## MXC and system.run

For MXC sandboxing, `system.run`, exec-approval, or `src\OpenClaw.Shared\Mxc`
changes, run `.\scripts\validate-mxc-e2e.ps1`. It builds and starts a real WSL
Gateway, so say why and ask first; without approval, report it as blocked. It
fails when the MXC proof skips. `-AllowSkip` only documents a non-capable host
and is not merge validation.

## Architecture Context for New Agents

Read only the affected [ARCHITECTURE.md](docs/ARCHITECTURE.md) rows, including
callers outside the owner's directory. Never re-add a `closed` responsibility,
and update the ledger in the same PR when ownership moves. Keep `App` as the
composition root. WinUI refactor rules for `App.xaml.cs` and
`ConnectionPage.xaml.cs` live in
[WinUI instructions](src/OpenClaw.Tray.WinUI/AGENTS.md#large-refactors).

| When changing | Read |
|---|---|
| Connection, credentials, migration, pairing, or reconnect | [CONNECTION_ARCHITECTURE.md](docs/CONNECTION_ARCHITECTURE.md) |
| MCP hosting or the node/MCP mode matrix | [MCP_MODE.md](docs/MCP_MODE.md) |
| Windows node capabilities or Gateway invocation | [WINDOWS_NODE_TESTING.md](docs/WINDOWS_NODE_TESTING.md) |
| First-run, setup-code, or bootstrap pairing | [ONBOARDING_WIZARD.md](docs/ONBOARDING_WIZARD.md) |
| Setup pipeline, rollback, or setup CLI | [SETUP_ENGINE_REDESIGN.md](docs/SETUP_ENGINE_REDESIGN.md) |
| Multi-line WSL scripts through `RunInWslAsync` | [WSL_EXE_ARGV_PITFALL.md](docs/WSL_EXE_ARGV_PITFALL.md) |

Write gateway credentials only through `GatewayRegistry`. WebSocket connections
use the device token, then the shared token, then the bootstrap token. HTTP chat
prefers the shared token. Never downgrade a paired device token. See
[credential precedence](docs/CONNECTION_ARCHITECTURE.md#credential-precedence).
Gateway records live in `%APPDATA%\OpenClawTray\gateways.json`, with identity
files under `gateways\<gateway-id>\`. `GatewayConnectionManager` owns connection
state. Chat, canvas, and tray actions route to Connection settings when pairing
is incomplete. MCP-only mode starts `NodeService` without a gateway credential.

## Coding rules

Prefer existing repository patterns and simple, idiomatic C#. Timeouts are
named constants that an environment variable can override. Literals shared
across modules go in a constants file.

## Product contracts

- New Windows node command: follow the
  [MCP contract checklist](docs/WINDOWS_NODE_TESTING.md#new-command-mcp-contract).
- Native copy goes in `.resw` resources for all supported languages. For shared
  catalogs, see [copy ownership](docs/LOCALIZATION.md#copy-ownership-and-shared-catalogs).
- No em dashes in product strings (UI, errors, CLI output, notifications, agent
  help). A standalone em dash is allowed as a placeholder.
- Telemetry: read [TELEMETRY.md](docs/TELEMETRY.md) first. Keep export opt-in,
  add no OpenTelemetry SDK packages to `OpenClaw.Shared`, and never export
  content or credentials.

For small UI changes, read only the relevant DESIGN.md sections.


<!-- colophon:start -->
## Design system

This repository has a living design system at [`DESIGN.md`](DESIGN.md). These files are the **source of truth for design** - tokens, component intent, and principles - and are framework-agnostic. `components.jsonc` shows design intent for preview; it is **not** shipping code.
**Read it before creating or changing any UI.** To ship, port the design into this app's implementation using the port target(s) below - don't copy `components.jsonc` verbatim.

- `DESIGN.md` - design tokens and rationale: brand, colors, typography, spacing, radii, shadows, principles.
- `.agents/design/components.jsonc` - the component patterns to reuse (structure, variants, states).

Generate UI from these tokens and patterns: use token names (e.g. `accent`, `ink`, spacing step `4`, radius `md`), not raw hex or ad-hoc px; reuse the documented components instead of inventing new ones; honor the brand voice; and avoid the system's listed anti-references. If you need a value the system doesn't cover, add it to `DESIGN.md` rather than hard-coding a one-off.

Use the **Colophon** tool/canvas to inspect, edit, and preview this system when available. Install the optional plugin with `copilot plugin install karkarl/colophon`. Without the extension, read and edit the files directly; Colophon is not required.

**Port targets** - how a design becomes shipping code here (design stays the source of truth; these say what each surface ships as and which reference/skill to port it with):
- Default: ships as Native WinUI 3 / C# (Windows Fluent) - port via https://github.com/microsoft/win-dev-skills (helper agent: win-dev-skills)
- area "chat", components ChatBubble, ChatComposer, ChatThread: ships as Reactor (Microsoft.UI.Reactor) - port via https://github.com/microsoft/microsoft-ui-reactor (no helper agent yet)
- Canonical implementation owner: OpenClaw Windows (openclaw/openclaw-windows-node)
- Examples kept aligned via: Tokens are captured by manual inspection of the app's WinUI 3 XAML/C# Fluent resources; re-inspect and update this file when the app's ThemeResource mappings or SystemAccentColor usage change.

Colors in `DESIGN.md` are **preview-only** swatches. When a color has a `resource` (e.g. a WinUI `ThemeResource` key), bind that resource in code - never hard-code the preview hex - so light, dark, and high-contrast themes stay correct. The shipping implementation is canonical; treat `components.jsonc` and the token values as derived visual examples, not a parallel upstream source.

<sub>Managed by [Colophon](https://github.com/karkarl/colophon) - edit `DESIGN.md` to change the system; this block only points to it.</sub>
<!-- colophon:end -->
