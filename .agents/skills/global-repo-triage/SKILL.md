---
name: global-repo-triage
description: "Run complete OpenClaw Windows Node issue and PR triage, including landing queues, low-hanging issues or PRs, proof pools, ownership audits, and release planning."
---

# OpenClaw Windows Node Global Triage

Run a complete maintainer triage of `openclaw/openclaw-windows-node`. Read every
open issue and pull request, identify safe landing lanes, schedule Windows proof
pools, and produce an evidence-backed Markdown report. Open the interactive
triage canvas only when the user asks for it.

This skill is repository-specific. If the current repository is not
`openclaw/openclaw-windows-node`, stop and say that this skill applies only to
the OpenClaw Windows Node repository.

## Trigger

Use when the user asks for:

- global triage, backlog triage, or a full repository sweep
- today's OpenClaw landing queue
- low-hanging issues or PRs
- what Karen or another maintainer can do in parallel
- release-train contents or a correction-release decision
- a comparison with a prior OpenClaw triage report

## Required output

Create these session artifacts:

```text
global-triage-YYYY-MM-DD.md
triage-YYYY-MM-DD\issues-summary.csv
triage-YYYY-MM-DD\prs-summary.csv
triage-YYYY-MM-DD\pr-<number>.patch
```

Keep raw JSON when practical. Do not add triage artifacts to the repository.
[`templates\global-triage-report.md`](templates/global-triage-report.md) owns the
report format. Read
[`examples\global-triage-2026-09-01.md`](examples/global-triage-2026-09-01.md)
only to calibrate depth; its data is history. Use
`templates\triage-state.template.json` only for the optional dashboard.

## Ground rules

1. **Read-only unless explicitly authorized.** Do not merge, close, label,
   comment, push, or rerun CI from a triage request alone. The coordinated
   read-only PR review sessions in section 7 are the only sessions created by
   default; implementation, proof, landing, and cleanup sessions still require
   an explicit user request or canvas action.
2. **Cover the entire backlog.** Fetch every open issue and PR. A report is not
   global if fetched and classified counts differ.
3. **Read discussions and code.** For every item that could be taken, closed,
   repaired, or assigned, inspect the body, comments, reviews, inline threads,
   timeline, checks, files, commits, and patch.
4. **Use the 90% TAKE bar.** TAKE requires clear value, bounded scope,
   current-head validation, and no missing custom proof.
5. **ClawSweeper is advisory.** Read its durable review comment and labels, then
   verify each finding yourself. Exact-head source review, proof, required
   validation, and branch protection can establish safety without bot wording.
6. **Do not blame a PR for a baseline flake.** Compare a failing test with the
   changed surface, the exact base commit, and nearby `main` runs. Rerun only
   after identifying the likely owner.
7. **Never treat unavailable proof as passed.** Report it as
   `NEEDS_HUMAN_TEST` or `Not verified / blocked`.
8. **Do not revive stale architecture wholesale.** Transplant the smallest
   useful fix onto current owners.
9. **Canvas actions are requests, not mutations.** The dashboard may refresh
   read-only GitHub state and route a guarded action request to one dedicated
   child project session per item. Reuse that session for later actions on the
   same PR or issue. The dashboard must never merge, close, label, comment, push,
   rerun CI, or delete a session directly.

## Decision vocabulary

| Decision | OpenClaw meaning |
|---|---|
| TAKE | At least 90% take confidence; validated and safe to land. |
| TAKE_AFTER_CHECKS | Likely landable after ordinary exact-head CI, tests, or a small maintainer edit. |
| NEEDS_HUMAN_TEST | Requires a named Windows proof pool, hardware, signing, installer, UI, Gateway, or MXC environment. |
| NEEDS_INFO | Current repro, route, topology, logs, ownership, or product decision is missing. |
| HOLD_FOR_AUTHOR | Direction is useful, but code, conflicts, scope, or proof must change. |
| DECLINE | Superseded, stale, unsafe, duplicative, speculative, or not worth maintaining. |

Report both **take confidence** and **recommendation confidence**. A confident
HOLD or DECLINE is not a high take-confidence item.

## 1. Start from the repository's own triage data

Read [`docs\REPOSITORY_TRIAGE.md`](../../../docs/REPOSITORY_TRIAGE.md) and
[`docs\PROOF_POOLS.md`](../../../docs/PROOF_POOLS.md) first. Read other docs
only when an item touches them: the affected rows of `docs\ARCHITECTURE.md`
(search for the owner; do not read the whole file), the relevant
`docs\TEST_COVERAGE.md` lanes, and `docs\RELEASING.md` when a release is in
scope. If the triage tooling itself looks wrong, run its
[local deterministic test](../../../docs/REPOSITORY_TRIAGE.md#local-deterministic-test).

Prefer the latest successful scheduled `Repository Triage Report` artifact as
the inventory baseline:

```powershell
$repo = "openclaw/openclaw-windows-node"
gh run list --repo $repo --workflow repository-triage.yml --limit 10
gh run download <run-id> --repo $repo --name repository-triage-report `
  --dir <session-artifact-dir>\triage-YYYY-MM-DD\scheduled
```

If the artifact is missing, stale, or partial, fetch directly with `gh`. Preserve
collection warnings. Never silently omit an item after an API failure.

## 2. Build complete summaries

Fetch all open work with pagination:

```powershell
gh issue list --repo $repo --state open --limit 1000 `
  --json number,title,author,labels,assignees,createdAt,updatedAt,url,comments
gh pr list --repo $repo --state open --limit 1000 `
  --json number,title,author,labels,createdAt,updatedAt,url,reviewDecision,mergeable,isDraft,baseRefName,headRefName,additions,deletions,changedFiles,statusCheckRollup
```

Write CSV summaries with these columns.

PR columns:

```text
N,Title,Author,Draft,Merge,Review,Files,Delta,Failed,Pending,Labels
```

Issue columns:

```text
N,Title,Author,Labels,Comments,Updated
```

For every potentially actionable PR:

```powershell
gh pr view <number> --repo $repo `
  --json number,title,author,body,url,state,isDraft,mergeable,mergeStateStatus,reviewDecision,baseRefName,headRefName,headRefOid,additions,deletions,changedFiles,files,commits,comments,reviews,statusCheckRollup,labels,createdAt,updatedAt
gh pr diff <number> --repo $repo --patch > <artifact-dir>\pr-<number>.patch
```

Also inspect unresolved inline threads and relevant timeline events. A
ClawSweeper comment may be edited in place, so use its current `updated_at` and
reviewed head SHA rather than assuming the oldest comment body is stale.

## 3. Classify OpenClaw-specific risk

Apply the repository's current owners and guardrails:

| Changed surface | Required scrutiny |
|---|---|
| `App.xaml.cs`, `ConnectionPage.xaml.cs`, chat provider/state | Read the affected `docs\ARCHITECTURE.md` rows; reject reintroduced closed responsibilities. |
| Gateway registry, credentials, pairing, setup | Read connection/onboarding/setup docs; protect device-token precedence and setup-managed ownership. |
| `system.run`, MXC, exec approvals | Treat as a security boundary; require real `validate-mxc-e2e.ps1` proof without `-AllowSkip`. |
| New/changed node command or MCP output | Require capability registration, MCP description, `winnode` docs/tests, discovery, and invocation proof. |
| WinUI behavior | Require current-head visible proof using isolated tray data and `windows-winui-interactive`. |
| Installer, signing, packaging, update policy | Treat as release risk; require signed-artifact and clean upgrade proof. |
| Local AI GPU/model/runtime | Require affected hardware proof, not only mocked detection. |
| Localization | Run all-locale key/placeholder/all-or-none tests and obtain visual proof or an explicit blocker. |

Bot, Copilot, repo-assist, and ClawSweeper-authored PRs are untrusted
contributions. Do not lower the bar because automation produced them.

## 4. Map required proof pools

Select every applicable pool from `.github\proof-pools.json`. Pool meanings and
the PR body headings are in
[`docs\PROOF_POOLS.md`](../../../docs/PROOF_POOLS.md#named-pools). Proof
declarations schedule work. They do not prove it ran.

## 5. Check validation and review

The landing bar is a passing exact-head `CI Gate` plus the declared proof pools.
Local lanes and commands are in the
[validation policy](../../../docs/TEST_COVERAGE.md#validation-policy). Review
requirements are in the
[review policy](../../../docs/AGENT_WORKFLOWS.md#when-review-is-required),
using the host's reviewer role. A section 7 adversarial review satisfies it.

## 6. Audit ownership and duplicate work

For each `status: 🚢 actively landing` item, name the owner or label actor,
identify linked open PRs, and warn against duplicate fixes. Expiry and
exclusion rules are in
[`docs\REPOSITORY_TRIAGE.md`](../../../docs/REPOSITORY_TRIAGE.md#active-ownership-expiry).

Do not mutate labels during report-only triage. If the user authorizes cleanup,
use the repository workflow's guarded
`remove-expired-active-ownership` operation rather than ad hoc label removal.

## 7. Run and publish adversarial reviews

Use direct review for small changes. Invoke
[`hanselman-code-review`](../hanselman-code-review/SKILL.md) for:

- more than 300 changed lines
- security, auth, setup, storage, release, signing, installer, shell, MXC,
  concurrency, or data-loss risk
- bot/repo-assist changes beyond a trivial dependency bump
- conflicting GitHub state or disputed findings
- any candidate below 95% recommendation confidence that might still be taken

### Default child-session topology

Every adversarially reviewed PR must run in one coordinated
child project session linked to that exact PR. The global-triage parent
coordinates and publishes results; it does not run the two model reviews itself.

Before starting review work:

1. Call `list_repositories` and resolve the project whose GitHub repository is
   exactly `openclaw/openclaw-windows-node`.
2. Call `list_sessions_and_chats` once and index existing project sessions by
   native `source_pr_repo` and `source_pr_number` linkage.
3. For each selected PR, reuse the one session linked to that exact repository
   and PR. Send the review request with `send_session_message`.
4. If no linked session exists, call `open_pr_session` with the exact repository
   and PR number, `coordinate_with_creator: true`, `notify_on_idle: "always"`,
   and an `autopilot` kickoff containing the complete review request.
5. If more than one linked session exists for a PR, stop that PR lane as
   ambiguous. Do not pick one or create another.

Launch independent PR review sessions in parallel. Keep dependent PRs serial
when reviewing one head without its required base would make the evidence
misleading. Never use one child session to review multiple PRs.

The child session must:

1. Re-fetch the exact PR head and complete GitHub evidence.
2. Save the exact-head patch in its own session artifacts.
3. Invoke `hanselman-code-review` there so both model reviews, cross-reference,
   and finding verification are owned by that PR session.
4. Preserve the read-only default and perform no GitHub mutation.
5. Send exactly one structured result back to the parent identifiers from the
   current cross-session message. A reused session must reply to the current
   sender, not its original creator.

Use this callback envelope:

```text
ADVERSARIAL_REVIEW_RESULT
{
  "schemaVersion": 1,
  "repo": "openclaw/openclaw-windows-node",
  "prNumber": 1234,
  "reviewedHeadSha": "<40-character exact head>",
  "status": "complete",
  "opusStatus": "complete",
  "codexStatus": "complete",
  "findings": [
    {
      "id": "1234-short-stable-slug",
      "issue": "Concrete verified issue",
      "opusSeverity": "HIGH",
      "codexSeverity": "",
      "consensus": "LOW",
      "fixConfidence": 95,
      "disposition": "accepted"
    }
  ],
  "finalDecision": "HOLD_FOR_AUTHOR",
  "takeConfidence": 45,
  "recommendationConfidence": 98,
  "nextAction": "Concrete owner and next action.",
  "summary": "Concise cross-model disposition.",
  "evidenceSummary": "Exact-head evidence used for the verdict.",
  "githubMutationPerformed": false
}
```

Use `status: "failed"` when either reviewer or exact-head verification fails.
Use `status: "stale"` when the head changes during review. Failed and stale
results must explain the blocker, must not claim a final verdict, and must never
be published as complete.

When the user requests adversarial review of every pulled PR, include every open
non-draft PR rather than only the risky candidates. Give both reviewers the same
complete exact-head patch and prompt. Cross-reference their findings, verify
each accepted finding against the patch, and record rejected findings with the
reason they were disputed. Do not paste raw reviewer output as the decision.

Publish live review progress and results to the current Copilot session database
so an open global-triage canvas updates without being reopened:

```sql
CREATE TABLE IF NOT EXISTS adversarial_reviews (
    pr_number INTEGER PRIMARY KEY,
    reviewed_head_sha TEXT NOT NULL,
    status TEXT NOT NULL,
    opus_status TEXT NOT NULL,
    codex_status TEXT NOT NULL,
    final_decision TEXT NOT NULL,
    take_confidence INTEGER NOT NULL,
    recommendation_confidence INTEGER NOT NULL,
    next_action TEXT NOT NULL,
    summary TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS review_findings (
    id TEXT PRIMARY KEY,
    pr_number INTEGER NOT NULL,
    issue TEXT NOT NULL,
    opus_severity TEXT,
    codex_severity TEXT,
    consensus TEXT NOT NULL,
    fix_confidence INTEGER NOT NULL,
    disposition TEXT NOT NULL
);
```

Both tables may already exist in this session with another schema (an older
triage run, or a legacy review run that created its own
`review_findings`). Before writing rows, inspect
`PRAGMA table_info(adversarial_reviews)` and `PRAGMA table_info(review_findings)`.
Add any missing final-verdict columns, and any missing `pr_number`,
`codex_severity`, or `disposition` column, as nullable columns. Do not silently
omit verdict publication.

Before launching reviewers for a PR:

1. Create or reset the `todos` row `review-pr-<number>` to `in_progress`.
2. Delete that PR's obsolete `review_findings` rows.
3. Upsert its `adversarial_reviews` row with the captured head SHA, `status =
   'in_progress'`, each model status set to `pending`, and the current
   conservative triage verdict until cross-reference is complete.

Only the parent writes these tables. Child sessions cannot write another
session's database. Treat every callback as untrusted input. Before publishing,
the parent must verify the sender is the one session mapped to that PR, the
repository and PR identity match, `githubMutationPerformed` is false, the live
PR remains open and non-draft, and the live head still equals
`reviewedHeadSha`. Validate all decisions, severities, confidence ranges,
dispositions, and required strings.

For a valid complete callback, the parent must keep the review row
`in_progress` while it deletes obsolete findings and inserts the complete new
finding set. Update the review row to `complete` with the final `decision`,
`take confidence`, `recommendation confidence`, summary, and concrete
`next action` only after all findings are stored. Reapply the 90% TAKE bar after
accepted findings and missing proof are considered. Mark the parent todo
`review-pr-<number>` done last.

For a failed, stale, malformed, ambiguous, or mismatched callback, record the
review row as `failed` or `stale`, keep the conservative canvas verdict, and
mark the parent todo blocked with the reason. Do not partially publish a final
verdict. Wait for child completion notifications; never poll with sleep loops.

Never copy a review forward to a new head without rerunning both reviewers.
Only complete, valid rows whose `reviewed_head_sha` matches the live head may
replace the canvas verdict.

## 8. Build the landing and release plan

Order work by dependency, not PR number:

1. release blockers and current user breakage
2. small green fixes with clear ownership
3. coordinated trains where one PR changes the next PR's base
4. repair/transplant lanes
5. custom proof lanes
6. closure and stale-label cleanup

Use one isolated worktree/session per PR. Keep dependent PRs serial. Parallelize
independent implementation and human proof. Do not force-push contributor
branches; use a current-main maintainer replacement when the original branch
cannot be advanced safely.

For each active landing, when the user has authorized it:

- apply `status: 🚢 actively landing` (rules in
  [`docs\REPOSITORY_TRIAGE.md`](../../../docs/REPOSITORY_TRIAGE.md#active-ownership-expiry))
- merge current `origin/main` when the base changed
- rerun exact-head validation and proof
- update the PR body
- resolve or explicitly disposition review findings
- verify GitHub state after merge
- remove the active label and archive the work session

If CI fails only in unchanged infrastructure:

1. inspect the exact failed test and log
2. compare with the exact base or nearby main run
3. run the focused test locally
4. check whether a fix has since landed on main
5. refresh the branch before rerunning stale-base CI

For release planning, compare the latest tag with `main`, list which candidate
PRs are already shipped, prefer a narrow correction for a bounded user-facing
fix, and exclude broad localization, feature, refactor, conflicted, or unproven
work. Tagging and verification follow the
[release checklist](../../../docs/RELEASING.md#release-checklist).

## 9. Write the report

Use [`templates\global-triage-report.md`](templates/global-triage-report.md)
exactly: its section order and table columns. Every row needs a concrete owner
and next action. Cover all open items, including low-priority and declined work.

## 10. Optionally publish the interactive dashboard

The checked-in canvas also supports direct opening before state is available:

```json
{ "canvasId": "openclaw-triage-dashboard", "instanceId": "triage-bootstrap" }
```

Omitted input, `null`, or `{}` opens an inert bootstrap surface with instructions.
It does not load files or examples, query GitHub, poll, or route item actions.
This is not a completed triage report. Nonempty input must still be a valid
schema-versioned state with at least one item; malformed state is not converted
into a bootstrap surface.

Only produce the interactive canvas when the user requests it in addition to the
static Markdown report. Create `global-triage-YYYY-MM-DD.json` as its session
state artifact.

Write `global-triage-YYYY-MM-DD.json` using
`templates\triage-state.template.json`. Every item needs:

- its PR or issue number, title, URL, decision, both confidence values, effort,
  risk, owner, and concrete next action
- exact reviewed head SHA for PRs
- expected required check names for PRs; issues must use an empty `expectedChecks`
  array
- review status and proof status
- every applicable proof-pool ID, validated against
  `.github\proof-pools.json`
- dependencies on other items in the same triage

Populate `report` so the dashboard tabs preserve the report template's
decision context:

- `changes`
- `ownership`
- `reviews`
- `automation`

Use `plan` as the single ordered execution plan. Each step may set `horizon`
(`today` or `later`; default `today`), `dependsOn` other plan-step IDs, and
`gates` that point to an item's `inventory`, `review`, `checks`, `proof`, or
`landing` stage (`landing` is for pull requests only). Dependencies must form an
acyclic graph. Do not repeat plan steps in `report`.

Then:

1. Call `list_canvas_capabilities` for `openclaw-triage-dashboard`.
2. Read the saved artifact. If a dashboard is already open (including a bootstrap
   panel), call `invoke_canvas_action` with its `instanceId`,
   `actionName: "update_state"`, and the parsed JSON object as `input`.
   Otherwise open `openclaw-triage-dashboard` with that object and one new ID
   such as `global-triage-YYYY-MM-DD`.
3. When triage decisions, proof status, review status, expected checks, or plan
   steps change, save the revised artifact and invoke `update_state` on the same
   panel with the full revised object. Do not accumulate replacement panels.
   Reopening an existing ID may only focus it, not replace its input.
4. Leave the canvas open. It refreshes GitHub PR and issue state at the declared
   30-to-300-second interval and pushes updates to the panel.

The saved artifact is the durable report; `update_state` changes the running
panel, not the host's stored open input. After an extension restart, reapply the
latest artifact with `update_state`. The extension does not discover files
automatically, and the template's illustrative decisions must never be treated
as reviewed evidence.

Item actions route to the item's child session; the extension sends the full
routing steps in each action prompt. `Prepare merge` only asks that session to
re-fetch evidence and request explicit confirmation. The extension never calls
a GitHub mutation command.

If refreshed evidence changes an item's state, the child sends the routing
parent a `TRIAGE_STATE_DELTA` result with `githubMutationPerformed: false`. The
parent validates it, applies it to the saved triage-state JSON artifact, and
invokes `update_state` on the existing dashboard instance ID. This callback
updates canvas state only. It does not authorize merge, close, label, comment,
push, rerun, session deletion, or any other GitHub mutation.

## Completion bar

Do not call the triage complete until:

- open issue and PR counts match the report rows
- actionable discussions, reviews, checks, and patches were inspected
- current-head versus stale-head evidence is distinguished
- issue-to-PR ownership and duplicate risks are mapped
- active ownership is audited
- proof pools and human hosts are scheduled explicitly
- a numbered day plan identifies safe parallelism and dependency order
- release contents and exclusions are explicit when a release is discussed
- the Markdown report, CSV summaries, and reviewed PR patches are saved

When the optional dashboard was requested, also require:

- the versioned triage-state JSON is saved
- the dashboard opens successfully and its fetched item count matches the state
  artifact
- every plan gate references an item and stage in the state artifact
- item actions remain child-session-routed and merge requests stay
  confirmation-gated
- every selected adversarial review has exactly one PR-linked child session and
  one validated terminal callback dispositioned by the parent
