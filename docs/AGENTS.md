# Documentation instructions

These rules apply to repository documentation. The root [AGENTS.md](../AGENTS.md)
owns authorization, runtime safety, and architecture policy.
[TEST_COVERAGE.md](TEST_COVERAGE.md#validation-policy) owns validation.
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
code fences. Windows commands use PowerShell. Architecture diagrams follow the
[paired Excalidraw/SVG policy](../DEVELOPMENT.md#documentation-and-diagrams).

For documentation-only changes, always run `.\scripts\validate-docs.ps1` from
the repository root and `git diff --check`. Add
`.\scripts\validate-agent-skills.ps1` if a skill changed, and
`.\scripts\test-agent-skills-validator.ps1` only if the validator code or its
fixtures changed. Do not run builds or test suites. Executable and shipped
command-reference changes follow the
[test selection rules](TEST_COVERAGE.md#formal-validation-paths).
Source and link checks are not runtime or visual proof.
