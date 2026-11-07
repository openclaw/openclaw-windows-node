# Preserve facts while improving an explanation

Use when rewriting, shortening, or splitting a document, not for every typo.
Read the source that owns each behavior-sensitive claim before retiring it.

1. Save the old page or diff as the comparison baseline.
2. Identify the reader, intended outcome, and main page type.
3. Inventory source units before rewriting: headings, paragraphs, tables,
   commands, UI steps, examples, warnings, diagnostics, and links.
4. Inventory the facts that must survive: flags, defaults, permissions,
   credentials, architecture support, precedence, lifecycle, error/recovery
   behavior, destructive effects, proof limitations, and ownership.
5. Give each unit or dense claim one explicit disposition:

| Disposition | Required evidence |
|---|---|
| Keep | A destination section that still states the fact accurately |
| Move | A specific linked page and section containing the retained contract |
| Retire | Current source showing the fact is obsolete, duplicated, or out of scope |
| Regenerate | The authoritative generator or source changed instead of derived output |
| Unverified | The unanswered question and its owner; not a new claim of behavior |

6. Write the common workflow first. Keep necessary warnings near its steps.
   Move exhaustive lookup material and unusual recovery paths to references,
   not out of the repository.
7. Preserve authored/generated boundaries. Do not hand-edit copied build
   outputs, setup-managed workspace blocks, or derived catalogs when an owning
   source exists. Keep documented release-owner edits to generated GitHub
   release bodies, including required classification metadata, distinct from
   generated in-repository output and require publication authority.
8. Check that an unfamiliar reader can follow the workflow and recognize its
   outcome without reconstructing requirements from unrelated pages.
9. Compare old and new line by line for dense command, configuration, security,
   setup, release, and troubleshooting sections. A topic-level "covered" note
   is not enough when several independent facts were present.
10. Validate links, anchors, affected command contracts, diagrams, and any
    generator or drift test involved in the change.

Record a compact keep/move/retire map in the change handoff or session artifacts.
Do not commit temporary audit notes or claim a whole paragraph is preserved
when only its topic survived.

Do not split the shipped `winnode` catalog without coordinating the sidecar,
CLI help, classifier, and `SkillMdDriftTests`. Do not split the architecture
ledger into competing ownership sources. Do not copy native WinUI labels into a
new prose catalog; their `.resw` resources remain authoritative.

Method informed by the MIT-licensed upstream
[preservation workflow](https://github.com/openclaw/openclaw/blob/a1fb89994ae04c434def18e8d45d81abdf672364/.agents/skills/openclaw-refactor-docs/SKILL.md);
the Windows document owner maintains this adaptation.
