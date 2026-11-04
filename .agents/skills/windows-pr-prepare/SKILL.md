---
name: windows-pr-prepare
description: "Draft or publish a Windows-node PR. Drafting uses existing evidence; publishing code runs missing pre-PR checks. Publication needs explicit authority."
---

# Windows PR preparation

Use before opening a PR, writing its description or updating its body. Follow
this flow for the selected change, not a repository-wide audit. The preparation
output is a local body and a ready-or-blocked handoff. Publication and merge
require their own authorization.

For a description draft, status report, or metadata-only edit, use existing
evidence. Do not run simplification, reviewers, tests, or runtime proof just to
write text. List missing checks without claiming readiness. The remaining
validation and review steps apply when publishing code or when the user
explicitly asks to establish readiness.

## 1. Establish the claim, candidate and authority

Read [repository instructions](../../../AGENTS.md), the affected production owner
and existing tests. State the concrete behavior being changed and the invariant
to preserve. For instructions, identify the reader's task and authoritative
guidance instead of inventing a product behavior claim.

Record the repository, worktree, intended target and its observed tip, HEAD and
relevant staged, unstaged and untracked content. For uncommitted work, keep the
runner's record of the source it used, or capture the diff and file hashes and
label that association as manual. Do not require a commit just to obtain a SHA.

Permission to implement and validate is not permission to install, change
shared runtimes, commit, push, edit GitHub metadata or merge. Do not fetch,
rebase or switch branches merely to make evidence identities agree.

## 2. Select the smallest sufficient validation

Before publishing new or changed code, or establishing code readiness, run the
[pre-PR checks](../../../AGENTS.md#before-creating-or-updating-a-pr), skipping
any that already ran on the current code.

Use the [validation planner](../../../docs/TEST_COVERAGE.md#explain-the-validation-plan)
with all candidate changes, then the [proof skill](../openclaw-proof-validation/SKILL.md)
and [proof pools](../../../docs/PROOF_POOLS.md#declare-pools-in-a-pr) for applicable
obligations. A planned lane or declared pool is not an executed result.

Docs-only changes use the [docs checks](../../../docs/AGENTS.md#format-and-checks),
not builds or native runtime proof. Code adds focused checks that cover the
change under the [validation policy](../../../docs/TEST_COVERAGE.md#validation-policy);
CI is the full gate. The shipped `winnode` reference keeps its command
contract checks.

Select a runtime only when the claim needs one and authority permits it. A
separate app profile does not authorize installing or registering a Gateway or
changing a shared runtime. For MCP, follow the
[local MCP guide](../windows-app-mcp/SKILL.md), including fresh-process
profile/token/URL binding.

## 3. Check what actually ran

Apply the
[result rules](../../../docs/TEST_COVERAGE.md#accepting-result-reports): read
the exact invocation's outputs, distinguish the PR head from the tested source,
and reassess results after edits. For inventory-bound evidence, use the
[validator and limits](../../../docs/PROOF_POOLS.md#validate-local-execution-evidence).

Missing required review, UI or Gateway evidence stays `Not verified / blocked`.
For an unexpected failure, preserve the attempt and identify the next bounded
investigation or decision. Fix task-caused defects within scope; do not
automatically repair unrelated tests, rerun until green or create new PR layers.
A failure in another task blocks this task only if this task depends on that
result. Otherwise continue the authorized work without borrowing its evidence.

## 4. Review the intended scope

Code-change PRs get the dual-model review from the pre-PR checks, which
satisfies the [review policy](../../../docs/AGENT_WORKFLOWS.md#when-review-is-required).
When another review is requested, use an independent reviewer agent (the host's
review role) on the implementation and proof plan. For a scoped
source review with the shared helper, follow the
[Windows review recipe](../autoreview/references/windows.md): select mode and
base explicitly, include committed and dirty work when intended, and keep P0-P3
coverage.

Report actual scope, findings, dispositions and missing review. Self-checks are
not independent review, and an unavailable required review remains blocked.
Verify findings before fixing, then refresh affected checks and review coverage.
Reviews are advisory, not runtime proof or authority to publish.

## 5. Write the local body from the actual template

Read the current [Windows PR template](../../../.github/pull_request_template.md).
Copy it to a local session artifact; preserve headings, HTML comments, checklist
items and ordering, including security, compatibility and review conversations.
Follow its title convention. When referring to a PR, include its title in
parentheses after the number. Check only supported claims, not aspirational work.

The [proof package](../openclaw-proof-validation/SKILL.md#pr-proof-package)
defines what counts as evidence. This step assembles that evidence into the
current template:

- Lead with the current behavior, strongest evidence and meaningful limits.
  Keep risks and blockers visible.
- Put exact commands and actual outcomes/counts in Validation. Identify the
  tested source in Real Behavior Proof, including uncommitted work.
- For prose-only work, label source/link checks as static assessment and runtime
  proof as not applicable.
- Declare every applicable pool, or use ``- `none`: <reason>`` when no custom
  pool is required.

Keep supporting history in links or optional details, not an operational diary.
Compare the completed body with the template; do not drop unanswered fields or
claim pending checks passed. Apply the
[evidence/redaction boundaries](../../../docs/PROOF_POOLS.md#evidence-and-secret-boundaries)
and verify referenced artifacts. Private local paths are not public proof links.

## 6. Hand back readiness and stop

Report the exact candidate, local draft location, strongest evidence and remaining
limits. Say **ready for human review** only when applicable gates are satisfied.
A missing [required review](../../../docs/AGENT_WORKFLOWS.md#when-review-is-required)
means **blocked**, even when tests pass. Name any other **blocked**, **waiting for required CI**, or **candidate
changed** state and the next bounded decision. A complete local draft can still
describe blocked work; it is not a ready-to-merge claim.

Stop before publication without explicit authority for the exact operation.
A body-only update does not authorize pushing code. Preserve an existing PR's
head and base, including stacked or release targets. For a new contributor PR
without an established target, use the repository's fork/default-branch
convention; resolve ambiguity before publishing. Verify the intended
repository/base/head and live PR state, then use the host's supported publication
tools and their guard/failure contracts.
Preserve concurrent human edits; after an uncertain write, inspect the result
before retrying.
Verify published content and links without treating publication as proof.
