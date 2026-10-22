---
name: windows-pr-prepare
description: "Prepare a truthful local Windows node PR draft from the current template, selected validation and source-bound evidence. Stops before publication."
---

# Windows PR preparation

Use when preparing a PR or refreshing its local draft. Follow this flow for the
selected change, not a repository-wide audit. The output is a local body and a
ready-or-blocked handoff, not publication or merge approval.

## 1. Establish the claim, candidate and authority

Read [repository instructions](../../../AGENTS.md), the affected production owner
and existing tests. State the concrete behavior being changed and the invariant
to preserve. For instructions, identify the reader's task and authoritative
guidance instead of inventing a product behavior claim.

Record the repository, task worktree, intended target, observed target tip,
comparison base, HEAD and relevant staged, unstaged and untracked content.
Preserve precommit identity using the evidence owner's source capture, or label
manual provenance with the captured diff/content hashes. Do not require a commit
just to obtain a SHA. Missing comparison context stays explicit.

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
contract checks. Select a runtime only when the claim needs one and authority
permits it. Profile isolation is not machine-change permission. For MCP, follow
the [local MCP guide](../windows-app-mcp/SKILL.md), including fresh-process
profile/token/URL binding; do not copy a partial launcher recipe here.

## 3. Check what actually ran

Use exact invocation outputs, not the newest file or exit zero alone. Check
executed cases, pass/fail/skip counts, failures and source/runtime identity under
the existing [result rules](../../../docs/TEST_COVERAGE.md#accepting-result-reports).
For inventory-bound evidence, use its
[validator and limits](../../../docs/PROOF_POOLS.md#validate-local-execution-evidence).
Keep manually recorded observations labeled as such; do not fabricate receipts.

Keep PR head, tested commit (including CI merge commits), executed dirty snapshot
and loaded binary/runtime distinct. After edits, commits or rebase, reassess
which results and reviews still apply. Source equality can establish
correspondence; it cannot restamp an earlier review or runtime execution as new.
Retain original failures. An instrumented pass does not erase them, and restoring
source does not restore previously built binaries.

Ordinary allowed skips are not complete custom/native proof. CI carry-forward
results and successful dispatch are not fresh execution or reviewer approval.
Missing required review, UI or Gateway evidence stays `Not verified / blocked`.
For an unexpected failure, preserve the attempt and surface a bounded next
decision. Fix task-caused defects within scope; do not automatically repair
unrelated tests, rerun until green or create new PR layers. Independent approved
work need not wait behind an unrelated validation stream.

## 4. Review the intended scope

When requested or required, use the existing
[bounded local-review recipe](../../../docs/AGENT_WORKFLOWS.md#run-a-scoped-shared-review).
Select mode/base explicitly, include committed and dirty work when intended,
and retain Windows P0-P3 coverage. Follow that owner's final-output, context and
fallback rules, not a second review implementation. Report actual scope,
findings/dispositions and missing review; self-checks are not independent review.
Verify findings before fixing, then refresh affected checks and review coverage.
Reviews are advisory, not runtime proof or authority to publish.

## 5. Write the local body from the actual template

Read the current [Windows PR template](../../../.github/pull_request_template.md)
and any applicable template under `.github\PULL_REQUEST_TEMPLATE\` if present.
Copy it to a local session artifact; preserve headings, HTML comments, checklist
items and ordering, including security, compatibility and review conversations.
Follow its title convention. Check only supported claims, not aspirational work.

Lead with the current behavior, strongest current evidence and meaningful
limits. Put exact commands and actual outcomes/counts in Validation; identify
the tested snapshot honestly in Real Behavior Proof, including uncommitted work.
For prose-only work, label source/link checks as static assessment and runtime
proof as not applicable. Declare every applicable pool, or use
``- `none`: <reason>`` when no custom pool is required.

Keep supporting history in links or optional details, not an operational diary.
Keep risks and blockers visible. Compare the completed body with the template;
do not drop unanswered fields or claim pending checks passed. Apply the
[evidence/redaction boundaries](../../../docs/PROOF_POOLS.md#evidence-and-secret-boundaries)
and verify referenced artifacts. Private local paths are not public proof links.
The body is durable context for humans and ClawSweeper, not a special Windows
evidence-manifest adapter. Do not revive retired repair or automerge bot commands.

## 6. Hand back readiness and stop

Report the exact candidate, local draft location, strongest evidence and remaining
limits. Say **ready for human review** only when applicable gates are satisfied;
otherwise name **blocked**, **waiting for required CI**, or **candidate changed**
and the next bounded decision. A complete local draft can still describe blocked
work; it is not a ready-to-merge claim.

Stop before publication without explicit authority. If separately authorized,
verify the intended repository/base/head and live PR state, then use the host's
supported publication tools and their guard/failure contracts. Preserve concurrent
human edits; after an uncertain write, inspect the result before retrying.
Verify published content and links without treating publication as proof.
