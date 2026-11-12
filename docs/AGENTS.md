# Documentation instructions

These rules apply to repository documentation. The root [AGENTS.md](../AGENTS.md)
still owns authorization, validation, runtime safety, and architecture policy.
For a documentation task, use the
[technical-documentation skill](../.agents/skills/technical-documentation/SKILL.md).

## Explain the reader's task

Choose the page's purpose before editing: overview, workflow guide, reference,
troubleshooting, historical record, or agent instruction. State its scope, then
give one recommended path with prerequisites, steps, observable success, and
failure recovery. Keep alternatives after the common path.

Use enough explanation to support a correct decision. Do not optimize for
line counts or replace readable prose with compressed operational fragments.
Keep safety warnings beside the action they constrain. Link exhaustive
contracts instead of duplicating them in each guide.

## Verify and preserve

- Check commands, UI locations, defaults, errors, and ownership against the
  current source. A proposal or old review is not proof of shipped behavior.
- Label historical measurements with their date and source revision when
  known. Unknown provenance stays unknown; never invent a SHA.
- For a rewrite, keep each behavior-sensitive fact, move it to a specific
  linked destination, or retire it with evidence. Use the skill's
  [preservation procedure](../.agents/skills/technical-documentation/references/refactoring.md).
- Keep [ARCHITECTURE.md](ARCHITECTURE.md) the single ownership ledger.
  A scoped guide routes to its rows; it does not restate a second ledger.
- Preserve command examples and their auth, isolation, timeout, and cleanup
  requirements. Never include live secrets, private settings, or unrelated logs.

## Format and checks

Use GitHub Markdown, relative links, descriptive headings, and language-tagged
code fences. Windows commands use PowerShell. No em dashes in maintained prose.
Architecture diagrams follow the existing
[paired Excalidraw/SVG policy](../DEVELOPMENT.md#documentation-and-diagrams);
do not introduce Mermaid or a separate publishing system.

Run `.\scripts\validate-docs.ps1` from the repository root. If skills are
affected, also run `.\scripts\validate-agent-skills.ps1`. Check `git diff --check`.
Executable and shipped command-reference changes also follow the
[test selection rules](TEST_COVERAGE.md#formal-validation-paths).
Source/link checks are not runtime or visual proof.
