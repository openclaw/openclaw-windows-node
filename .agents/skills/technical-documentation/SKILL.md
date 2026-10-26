---
name: technical-documentation
description: Write, review, or restructure Windows node documentation and agent instructions using current source, task-focused explanations, and explicit fact preservation.
---

# Windows technical documentation

Use for repository documentation, contributor guidance, and agent instruction
files in the OpenClaw Windows node repository. This is a brownfield workflow:
preserve current ownership, terminology, and validation rather than importing a
generic documentation system. A request to audit or plan remains read-only;
rewriting requires authorization.

Do not turn a scoped documentation task into a repository-wide audit, code
cleanup, UI-copy rewrite, or release operation.

## Define the reader and outcome

Classify the work as build, review, or refactor. Name the primary reader and the
observable result they need before choosing the page type.

| Page | First useful result |
|---|---|
| Overview | The reader chooses the appropriate Windows surface or guide |
| Guide | One recommended workflow reaches an observable outcome |
| Reference | A precise contract answers a specific lookup |
| Troubleshooting | A symptom leads to a safe check, interpretation, and next action |
| Architecture | A contributor finds the authoritative owner and preserved invariant |
| Agent instruction | A task routes to its owner, constraints, and required evidence |

State what the page covers and what it does not. Explain the normal path before
alternatives. Put prerequisites, consent, and safety boundaries before the
affected command. Move rare cases and exhaustive contracts to named references
without removing facts that still matter.

## Load only the owning context

Read [documentation ownership](../../../docs/AGENTS.md), then the target page and
the source that owns its claims. Use this map to select context rather than
preloading the documentation collection.

| Surface | Read first | Verify against |
|---|---|---|
| Product overview, install, update, uninstall | [README](../../../README.md), [setup](../../../docs/SETUP.md) | `installer.iss`, updater code, uninstall scripts, current release assets |
| WinUI Settings, Workspace, Command Center, or tray UX | [WinUI ownership](../../../src/OpenClaw.Tray.WinUI/AGENTS.md), [design system](../../../DESIGN.md) | XAML/C#, localized `.resw` resources, accessibility guidance |
| Gateway, pairing, operator, node, or credentials | [operator and node concepts](../../../docs/OPERATOR_NODE_CONCEPTS.md), [connection architecture](../../../docs/CONNECTION_ARCHITECTURE.md) | connection owners, protocol contracts, focused tests |
| Node mode, permissions, sandbox, MCP, or `winnode` | [Windows node testing](../../../docs/WINDOWS_NODE_TESTING.md), [MCP mode](../../../docs/MCP_MODE.md) | capability registration, `McpToolBridge`, command tests |
| Local WSL Gateway setup or administration | [onboarding](../../../docs/ONBOARDING_WIZARD.md), [WSL Gateway admin](../../../docs/WSL_GATEWAY_ADMIN.md) | setup engine, setup UI, installed Gateway contract |
| Installer, MSIX, Store, version, or release flow | [releasing](../../../docs/RELEASING.md), [versioning](../../../docs/VERSIONING.md), [Store submission](../../../docs/MICROSOFT_STORE.md) | build scripts, workflows, manifests, packaging-repository ownership |
| Logs, support context, or connection diagnostics | [setup troubleshooting](../../../docs/SETUP.md#troubleshooting), [MCP connection diagnostics](../../../docs/MCP_MODE.md#current-command-surface) | activation routes and diagnostics services that produce the data |
| Architecture ownership or a moved responsibility | [architecture ledger](../../../docs/ARCHITECTURE.md) | named production owner, callers, tests, closed ledger rows |

Follow linked architecture context only when the documented contract crosses
that boundary.

## Use Windows terminology precisely

- **OpenClaw Windows Hub** names this repository and Windows product area.
  **OpenClaw Companion** names the installed desktop app.
- **Gateway**, **local WSL gateway**, **operator**, **node**, **pairing**,
  **reapproval**, and **Node Mode** have distinct meanings. Use
  [operator and node concepts](../../../docs/OPERATOR_NODE_CONCEPTS.md) rather
  than collapsing them into "the app is connected."
- Local MCP is a loopback transport and may work without Gateway pairing. Never
  use local MCP success as proof of operator or node connectivity.
- Use the current native surface names. Copy visible labels and capitalization
  from localized resources rather than normalizing an old screenshot, plan, or
  prose name.
- Write architecture display names as **x64** and **ARM64**. Use runtime
  identifiers such as `win-x64` and `win-arm64` exactly. Claim both
  architectures only for a build, installer, portable, MSIX, Store, or proof
  surface whose current owner actually supports both.

## Respect authored and generated boundaries

- Repository Markdown, `AGENTS.md`, and `.agents/skills` are authored guidance.
  Native UI copy remains owned by `.resw` resources and their callers; a docs
  edit must not create a competing UI string source.
- Edit a generator, manifest, workflow, or authoritative source instead of
  generated in-repository output such as build artifacts, copied skills, or
  derived catalogs. Preserve documented release-owner edits to generated
  GitHub release bodies, including the classification marker in
  [RELEASING.md](../../../docs/RELEASING.md), and require separate publication
  authority.
- Setup injects a managed Windows-node section into a Gateway workspace
  `AGENTS.md`. Document that contract in this repository; do not hand-edit a
  user's generated managed block as the repository fix.
- Preserve the full [`winnode` command reference](../winnode/SKILL.md). It ships
  beside the CLI as `skill.md` and is drift-checked against capability
  registration. Splitting or shortening it is a packaging and command-contract
  change, not an editorial shortcut.
- Keep [ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) as the single ownership
  ledger. A scoped guide links to it instead of creating another ledger.
- Keep editable Excalidraw sources and rendered SVGs paired. Do not introduce
  Mermaid or a separate diagram publication path.

## Verify every behavior-sensitive claim

- Check commands, flags, defaults, settings locations, architecture support,
  owners, errors, and recovery behavior against current source and tests.
- Separate current behavior, released behavior, historical measurements,
  proposals, and required proof. Include a date and source revision for
  historical evidence when known.
- Do not infer GitHub release publication, Microsoft Store availability,
  signing, rollout, Gateway compatibility, or x64/ARM64 parity from a successful
  build or workflow definition.
- Distinguish the app-owned locked-down WSL Gateway from an existing local,
  remote, or native Gateway. Commands run in Windows PowerShell and commands run
  as the WSL `openclaw` or `root` user are not interchangeable.
- A fixture, source assertion, local MCP result, or unit test proves only its
  named boundary. It does not establish real Gateway pairing, signed installer
  behavior, Store rollout, WinUI rendering, or native sandbox containment.
- For diagnostics, start from the visible symptom. Prefer redacted support
  context, bounded log excerpts, and credential-free status commands. Never
  paste tokens, full private configuration, unrelated logs, or machine-specific
  private state.
- Run repository scripts and contributor commands from the repository root.
  User-facing commands must not assume a checkout. Use bounded placeholders and
  an expected result, and mark shell transitions explicitly when a workflow
  enters WSL.

For a rewrite or split, use the
[fact-preservation procedure](references/refactoring.md).

## Validate and hand off

Run the repository documentation checks from the repository root:

```powershell
.\scripts\validate-docs.ps1
.\scripts\validate-agent-skills.ps1
.\scripts\test-agent-skills-validator.ps1
git diff --check
```

Use [formal test selection](../../../docs/TEST_COVERAGE.md#formal-validation-paths)
for executable changes, UI copy, generators, packaging, or shipped command
references. Ordinary prose does not require a full app build or native runtime
proof. Do not claim a command, installer, Gateway path, or UI flow was exercised
when only its source or documentation was checked.

Report the page purpose, audience and outcome, corrected claims, destinations of
moved facts, source owners checked, authored/generated boundary decisions,
validation results, and unresolved ownership questions. Publication needs
separate authority.

## Source and maintenance

Windows-owned guidance, adapted from the MIT-licensed OpenClaw
[technical-documentation skill](https://github.com/openclaw/openclaw/blob/a1fb89994ae04c434def18e8d45d81abdf672364/.agents/skills/technical-documentation/SKILL.md),
[documentation overlay](https://github.com/openclaw/openclaw/blob/a1fb89994ae04c434def18e8d45d81abdf672364/.agents/skills/technical-documentation/references/openclaw.md),
and [refactoring workflow](https://github.com/openclaw/openclaw/blob/a1fb89994ae04c434def18e8d45d81abdf672364/.agents/skills/openclaw-refactor-docs/SKILL.md).
This is not a synchronized copy. Windows owns its terminology, commands, proof
requirements, GitHub Markdown conventions, release surfaces, and paired
Excalidraw/SVG diagrams.
