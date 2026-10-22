# Preserve facts while improving an explanation

Use when rewriting, shortening, or splitting a document, not for every typo.
Read the source that owns each behavior-sensitive claim before retiring it.

1. Identify the reader's task and choose the main page type.
2. Inventory the facts that must survive: commands, defaults, permissions,
   credentials, precedence, lifecycle, error/recovery behavior, examples,
   proof limitations, and links.
3. Give each fact one explicit disposition:

| Disposition | Required evidence |
|---|---|
| Keep | A destination section that still states the fact accurately |
| Move | A specific linked page/section containing the full retained contract |
| Retire | Current source showing the fact is obsolete, or a canonical duplicate |
| Unverified | The unanswered question and its owner; not a new claim of behavior |

4. Write the common workflow first. Keep necessary warnings near its steps.
   Move exhaustive lookup material and unusual recovery paths to references,
   not out of the repository.
5. Check that an unfamiliar reader can follow the workflow and recognize its
   outcome without reconstructing requirements from several unrelated pages.
6. Validate links, anchors, affected command contracts, and existing diagrams.

Record a compact keep/move/retire map in the change handoff or session artifacts.
Do not commit temporary audit notes or claim a whole paragraph is preserved
when only its topic survived.

Do not split the shipped `winnode` catalog without coordinating the sidecar,
CLI help, classifier, and `SkillMdDriftTests`. Do not split the architecture
ledger into competing ownership sources.

Method informed by the MIT-licensed upstream
[preservation workflow](https://github.com/openclaw/openclaw/blob/690c46084d507ce38502d401ef5d0d6a2fb9733a/.agents/skills/openclaw-refactor-docs/SKILL.md#L75-L130);
the Windows document owner maintains this adaptation.
