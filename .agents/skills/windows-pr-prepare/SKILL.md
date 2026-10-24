---
name: windows-pr-prepare
description: "Use before opening a Windows node PR, writing its description or updating its body. Prepare a local draft with validation and review evidence; publish only with explicit authority."
---

# Windows PR preparation

Use before opening a PR, writing its description or updating its body. Follow
this flow for the selected change, not a repository-wide audit. The preparation
output is a local body and a ready-or-blocked handoff. Publication and merge
require their own authorization.

## 1. Establish the claim, candidate and authority

Read [repository instructions](../../../AGENTS.md), the affected production owner
and existing tests. State the concrete behavior being changed and the invariant
to preserve. For instructions, identify the reader's task and authoritative
guidance instead of inventing a product behavior claim.

Record the repository, task worktree, intended target, observed target tip,
comparison base, HEAD and relevant staged, unstaged and untracked content.
For uncommitted work, retain the test or proof runner's record of the source it
used. If there is no such record, capture the diff and relevant file hashes when
running the check and label that association as manually recorded. Do not infer
an old run's source from today's files or require a commit just to obtain a SHA.
Missing comparison context stays explicit.

Separate permission to inspect, implement and validate from permission to install,
change shared runtimes, commit, push, upload, edit GitHub metadata or merge.
Do not fetch, rebase or switch branches merely to make evidence identities agree.

## 2. Select the smallest sufficient validation

Use the [validation planner](../../../docs/TEST_COVERAGE.md#explain-the-validation-plan)
with all candidate changes, then the [proof skill](../openclaw-proof-validation/SKILL.md)
and [proof pools](../../../docs/PROOF_POOLS.md#declare-pools-in-a-pr) for applicable
obligations. A planned lane or declared pool is not an executed result.

Ordinary prose uses [documentation and skill checks](../technical-documentation/SKILL.md),
not full-product builds or native runtime proof. Code retains mandatory local
closeout plus focused checks; the shipped `winnode` reference retains its command
contract checks.

Select a runtime only when the claim needs one and authority permits it. A
separate app profile does not authorize installing or registering a Gateway or
changing a shared runtime. For MCP, follow the
[local MCP guide](../windows-app-mcp/SKILL.md), including fresh-process
profile/token/URL binding.

## 3. Check what actually ran

Apply the existing
[result rules](../../../docs/TEST_COVERAGE.md#accepting-result-reports).
For inventory-bound evidence, use the
[validator and limits](../../../docs/PROOF_POOLS.md#validate-local-execution-evidence).

- Read outputs from the exact invocation, not the newest file or exit zero alone.
  Check executed cases, pass/fail/skip counts and failures. Label manual
  observations as manual; do not fabricate receipts.
- Distinguish the PR head, tested commit (including CI merge commits), uncommitted
  source used by the run, and loaded binary/runtime.
- After edits, commits or rebase, reassess which results and reviews still apply.
  Source equality can establish correspondence, but cannot turn an earlier
  review or runtime execution into a new run.
- Retain original failures. An instrumented pass does not erase them, and
  restoring source does not restore previously built binaries.
- Ordinary allowed skips are not complete custom/native proof. Identify the CI
  run and attempt when citing results; do not present older results as newly
  executed tests. Successful dispatch is not completed testing or review approval.

Missing required review, UI or Gateway evidence stays `Not verified / blocked`.
For an unexpected failure, preserve the attempt and identify the next bounded
investigation or decision. Fix task-caused defects within scope; do not
automatically repair unrelated tests, rerun until green or create new PR layers.
A failure in another task blocks this task only if this task depends on that
result. Otherwise continue the authorized work without borrowing its evidence.

## 4. Review the intended scope

Account for both source review and the required independent rubber-duck review:

- For source review, when requested or required, use the
  [bounded local-review recipe](../../../docs/AGENT_WORKFLOWS.md#run-a-scoped-shared-review).
  Select mode/base explicitly, include committed and dirty work when intended,
  and retain Windows P0-P3 coverage. Follow its context, final-output and fallback
  rules.
- Before publishing non-trivial UI, MCP, node-command, setup, pairing, security,
  permissions or diagnostics changes, obtain an independent rubber-duck review
  of the implementation and proof plan. Follow the
  [proof skill's review route](../openclaw-proof-validation/SKILL.md#collect-the-selected-proof)
  for available roles and explicit blockers. A source-review verdict alone does
  not establish that this review was performed.

Report actual scope, findings, dispositions and missing review. Self-checks are
not independent review, and an unavailable required review remains blocked.
Verify findings before fixing, then refresh affected checks and review coverage.
Reviews are advisory, not runtime proof or authority to publish.

## 5. Write the local body from the actual template

Read the current [Windows PR template](../../../.github/pull_request_template.md).
Copy it to a local session artifact; preserve headings, HTML comments, checklist
items and ordering, including security, compatibility and review conversations.
Follow its title convention. Check only supported claims, not aspirational work.

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
Missing required source or rubber-duck review means **blocked**, even when tests
pass. Name any other **blocked**, **waiting for required CI**, or **candidate
changed** state and the next bounded decision. A complete local draft can still
describe blocked work; it is not a ready-to-merge claim.

Stop before publication without explicit authority. If separately authorized,
verify the intended repository/base/head and live PR state, then use the host's
supported publication tools and their guard/failure contracts.
Preserve concurrent human edits; after an uncertain write, inspect the result
before retrying.
Verify published content and links without treating publication as proof.
