# Repository Triage Automation

The `Repository Triage Report` workflow inventories every open issue and pull
request once per day. It writes a Markdown job summary plus Markdown and JSON
artifacts. Scheduled runs are report-only.

## Report contents

Pull request rows report:

- mergeability and stale-base state from GitHub's merge state, with one bounded
  refresh when GitHub initially returns a lazy `UNKNOWN` value;
- aggregate check state and pass, fail, and pending counts;
- proof labels, including the contributor-facing needs-proof state;
- human, bot, or repo-assist authorship;
- docs-only, dependency patch/minor, bot/repo-assist, platform-specific, and
  release/package lanes;
- assignees or the actor who applied `status: 🚢 actively landing`;
- issues referenced by closing keywords or explicit related references.

Issue rows report:

- stale/no-repro, platform-specific, and release/package lanes;
- active ownership state;
- open pull requests found through current PR text, GitHub closing references,
  or relevant cross-reference timeline events.

When an issue already has an open linked pull request, the report routes it as
owned by that PR and warns against starting a duplicate fix. The workflow uses
GitHub's canonical closing references and current PR bodies for all issues. It
fetches timelines only for items carrying the active ownership label, avoiding
a repository-wide timeline crawl. A timeline cross-reference counts only when
the source is an open pull request in this repository and its current body
still explicitly references the issue. This prevents incidental comments and
historical edited-away references from claiming ownership.

Nested PR file and check pagination, lazy merge-state refreshes, and ownership
timeline requests are isolated per item. A legal null connection or transient
API failure marks the affected row partial and adds an auditable collection
warning instead of suppressing the rest of the report. Missing ownership
history always blocks cleanup.

## Active ownership expiry

`status: 🚢 actively landing` means a maintainer or delegated agent is actively
moving an item through implementation, validation, or merge. The automation
considers that ownership expired after **7 full days** with no trusted human
activity after the label was applied.

An expired label is reported on every run. It is removable only when all of
these safeguards pass:

1. A maintainer manually dispatches the workflow with
   `remove-expired-active-ownership`.
2. The item is still open and the exact allowlisted label is still present.
3. The latest label application is at least 7 full days old.
4. Label application history and a non-bot label actor are available.
5. The item has no assignee.
6. The latest dated trusted owner, member, or collaborator activity is at least
   7 full days old. A post-label commit without an attributable actor or server
   timestamp blocks removal because its inactivity age cannot be proven.
7. The item does not have `no-stale`, `P0`, or any canonical security taxonomy
   label (`security`, `impact:security`, `clawsweeper:needs-security-review`, or
   `merge-risk: 🚨 security-boundary`).

The cleanup job checks out the repository default branch, runs the deterministic
tests, then fetches each candidate item and timeline again immediately before
mutation. One candidate's API failure is recorded as a skip and does not hide
the other candidates. A concurrent removal is treated as an idempotent success.
Every removal or skip is journaled incrementally, then written to the job
summary and a 90-day audit artifact. The artifact upload runs even when the
cleanup step fails after a mutation, and unexpected failures are rethrown only
after the durable report files are written. The output directory is created
before collection begins, and a top-level failure path writes a minimal report
and cleanup journal before rethrowing the original error. The upload step warns
rather than replacing an earlier checkout or test failure when no artifact
exists.

For pull requests, timeline activity resets the inactivity clock only when its
actor is the maintainer who applied the active-ownership label or GitHub
explicitly identifies the actor as an owner, member, or collaborator.
External-author pushes do not claim maintainer ownership. Relevant activity
with no actor identity blocks cleanup conservatively.

## Security and permissions

The workflow does not run on pull request code. Scheduled and normal manual
reports receive only `contents: read`, `checks: read`, `issues: read`, and
`pull-requests: read`. They also receive `statuses: read` so legacy commit
status contexts can be included alongside check runs.

The separate cleanup job runs only after the explicit manual cleanup operation.
It receives `issues: write` and `pull-requests: write` because GitHub exposes
labels through those resources. The checked-in implementation has no merge,
close, comment, branch update, or general label-routing operation. It can
remove only `status: 🚢 actively landing`.

## Local deterministic test

```powershell
node --test .github/scripts/repository-triage.test.cjs
```

Representative report output:

```text
| #1253 fix(local-ai): use CUDA for allocatable GPU memory | MERGEABLE | yes | SUCCESS (12 pass, 0 fail, 0 pending) | human | platform-specific | none | #1243 |
| #1243 Change Local AI setup after installed | platform-specific | maintainer; expired; trusted activity recorded | #1253 | Existing open PR #1253. Avoid duplicate fixes. |
```
