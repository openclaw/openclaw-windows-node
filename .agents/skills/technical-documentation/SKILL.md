---
name: technical-documentation
description: Write, review, or restructure Windows-node docs and agent instructions.
---

# Windows technical documentation

Use for repository documentation, contributor guidance, and agent instruction
files. Preserve current ownership, terminology, and validation rather than
importing a generic documentation system. A request to audit or plan remains
read-only; rewriting requires authorization.

Do not turn a scoped documentation task into a repository-wide audit, code
cleanup, UI-copy rewrite, or release operation.

## Define the reader and outcome

Name the primary reader and the observable result they need. State what the
page covers and what it does not. Explain the normal path before alternatives,
put prerequisites, consent, and safety boundaries before the affected command,
and move rare cases to named references without removing facts that still
matter.

## Load only the owning context

Read [documentation ownership](../../../docs/AGENTS.md), then the target page and
the source that owns its claims. Use this map to select context rather than
preloading the documentation collection.

| Surface | Read first | Verify against |
|---|---|---|
| Product overview, install, update, uninstall | [README](../../../README.md), [setup](../../../docs/SETUP.md) | `installer.iss`, updater code, uninstall scripts, current release assets |
| WinUI Settings, Workspace, Command Center, or tray UX | [WinUI ownership](../../../src/OpenClaw.Tray.WinUI/AGENTS.md), the relevant [design system](../../../DESIGN.md) sections or components (the full guide only for broad redesigns) | XAML/C#, localized `.resw` resources, accessibility guidance |
| Gateway, pairing, operator, node, or credentials | [operator and node concepts](../../../docs/OPERATOR_NODE_CONCEPTS.md), [connection architecture](../../../docs/CONNECTION_ARCHITECTURE.md) | connection owners, protocol contracts, focused tests |
| Node mode, permissions, sandbox, MCP, or `winnode` | [Windows node testing](../../../docs/WINDOWS_NODE_TESTING.md), [MCP mode](../../../docs/MCP_MODE.md) | capability registration, `McpToolBridge`, command tests |
| Local WSL Gateway setup or administration | [onboarding](../../../docs/ONBOARDING_WIZARD.md), [WSL Gateway admin](../../../docs/WSL_GATEWAY_ADMIN.md) | setup engine, setup UI, installed Gateway contract |
| Installer, MSIX, Store, version, or release flow | [releasing](../../../docs/RELEASING.md), [versioning](../../../docs/VERSIONING.md), [Store submission](../../../docs/MICROSOFT_STORE.md) | build scripts, workflows, manifests, packaging-repository ownership |
| Logs, support context, or connection diagnostics | [setup troubleshooting](../../../docs/SETUP.md#troubleshooting), [MCP connection diagnostics](../../../docs/MCP_MODE.md#current-command-surface) | activation routes and diagnostics services that produce the data |
| Architecture ownership or a moved responsibility | [architecture ledger](../../../docs/ARCHITECTURE.md) | named production owner, callers, tests, closed ledger rows |

## Use Windows terminology precisely

- **OpenClaw Windows Hub** names this repository and Windows product area.
  **OpenClaw Companion** names the installed desktop app.
- **Gateway**, **local WSL gateway**, **operator**, **node**, **pairing**,
  **reapproval**, and **Node Mode** have distinct meanings. Use
  [operator and node concepts](../../../docs/OPERATOR_NODE_CONCEPTS.md) rather
  than collapsing them into "the app is connected."
- Local MCP is a loopback transport and may work without Gateway pairing. Never
  use local MCP success as proof of operator or node connectivity.
- Copy visible labels and capitalization from localized resources rather than
  an old screenshot, plan, or prose name.
- Write architecture display names as **x64** and **ARM64**. Use runtime
  identifiers such as `win-x64` and `win-arm64` exactly. Claim both
  architectures only where the current owner supports both.

## Respect authored and generated boundaries

- Native UI copy stays owned by `.resw` resources and their callers. Do not
  create a competing UI string source or a prose catalog of labels.
- Edit the generator, manifest, workflow, or authoritative source instead of
  generated output. Preserve release-owner edits to generated GitHub release
  bodies, including the classification marker in
  [RELEASING.md](../../../docs/RELEASING.md); publishing them needs separate
  authority.
- Setup injects a managed Windows-node section into a Gateway workspace
  `AGENTS.md`. Document that contract here; do not hand-edit a user's managed
  block as the repository fix.
- The [`winnode` command reference](../winnode/SKILL.md) ships beside the CLI
  and is drift-checked against capability registration. Do not split or
  shorten it without coordinating the sidecar, CLI help, classifier, and
  `SkillMdDriftTests`.
- Keep [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) as the single ownership
  ledger.
- Diagrams follow [documentation ownership](../../../docs/AGENTS.md#format-and-checks).

## Verify every behavior-sensitive claim

- Check commands, flags, defaults, settings locations, architecture support,
  owners, errors, and recovery behavior against current source and tests.
- Separate current, released, historical, and proposed behavior. Do not infer
  release publication, Store availability, signing, Gateway compatibility, or
  x64/ARM64 parity from a successful build or workflow definition.
- Distinguish the app-owned locked-down WSL Gateway from an existing local,
  remote, or native Gateway. Windows PowerShell commands and commands run as
  the WSL `openclaw` or `root` user are not interchangeable.
- A fixture, source assertion, local MCP result, or unit test proves only its
  named boundary.
- Never paste tokens, full private configuration, unrelated logs, or
  machine-specific private state.

For a rewrite or split, use the
[fact-preservation procedure](references/refactoring.md).

## Validate and hand off

Run the [documentation checks](../../../docs/AGENTS.md#format-and-checks). Use
[formal validation paths](../../../docs/TEST_COVERAGE.md#formal-validation-paths)
for executable changes, UI copy, generators, packaging, or shipped command
references. Ordinary prose needs no app build. Do not claim a command,
installer, Gateway path, or UI flow was exercised when only its source or
documentation was checked.

Report corrected claims, where moved facts went, source owners checked, and
validation results. Publication needs separate authority.

Adapted from the upstream
[technical-documentation skill](https://github.com/openclaw/openclaw/blob/a1fb89994ae04c434def18e8d45d81abdf672364/.agents/skills/technical-documentation/SKILL.md)
(MIT); not a synchronized copy.
