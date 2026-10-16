---
name: technical-documentation
description: Write, review, or restructure Windows node documentation and agent instructions using current source, task-focused explanations, and explicit fact preservation.
---

# Windows technical documentation

Use for repository docs, contributor instructions, and skill explanations.
A request to audit or plan remains read-only; rewriting requires authorization.

## Choose the reader's task

Read [documentation ownership](../../../docs/AGENTS.md), then the target page
and the source that owns its claims. Do not preload the entire docs collection.

| Page | First useful result |
|---|---|
| Overview | The reader chooses the appropriate guide |
| Guide | One recommended workflow reaches an observable outcome |
| Reference | A precise contract answers a specific lookup |
| Troubleshooting | A symptom leads to a check, interpretation, and next action |
| Agent instruction | A task routes to its owner, constraints, and required evidence |

State what the page covers and what it does not. Explain the normal path before
alternatives. Put prerequisites and safety boundaries before the affected
command; place rare cases and exhaustive contracts in named references.
Concise means less unnecessary explanation, not an arbitrary word or line cap.

## Verify the explanation

- Check commands, flags, defaults, settings locations, owners, and failure
  behavior against current source. Label an unverified claim rather than
  filling it in from a name or an old plan.
- Separate current behavior, historical measurements, proposals, and proof.
  A fixture or local MCP result does not establish real Gateway or UI behavior.
- Use complete PowerShell examples with bounded placeholders and an expected
  result. Never copy live credentials or machine-specific private state.
- Link the canonical explanation instead of restating its rules in several
  places. Keep the architecture ledger authoritative.
- Preserve the full `winnode` command reference: it is shipped as `skill.md`
  and checked against capability registration. A catalog split is a packaging
  change, not an editorial shortcut.

For a rewrite or split, use the [fact-preservation procedure](references/refactoring.md).
Do not turn a documentation request into code cleanup or a repo-wide audit.

## Validate and hand off

Run the existing checks from the repository root:

```powershell
.\scripts\validate-docs.ps1
.\scripts\validate-agent-skills.ps1
git diff --check
```

Use [test selection](../../../docs/TEST_COVERAGE.md#formal-validation-paths)
for executable changes or shipped command-reference changes. Do not run native
runtime proof for ordinary prose changes. Do not claim a command or UI flow was
exercised when only its source or documentation was checked.

Report the page's purpose, corrected claims, destinations of moved facts,
checks performed, and unresolved claims. Publication needs separate authority.

## Source and maintenance

Windows-owned guidance, informed by the MIT-licensed OpenClaw
[documentation overlay](https://github.com/openclaw/openclaw/blob/690c46084d507ce38502d401ef5d0d6a2fb9733a/.agents/skills/technical-documentation/references/openclaw.md)
and [refactoring workflow](https://github.com/openclaw/openclaw/blob/690c46084d507ce38502d401ef5d0d6a2fb9733a/.agents/skills/openclaw-refactor-docs/SKILL.md).
This is not a synchronized copy. Windows owns its commands, proof requirements,
GitHub Markdown conventions, and paired Excalidraw/SVG diagrams.
