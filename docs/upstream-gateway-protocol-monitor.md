# Upstream Gateway capability monitor

The **Upstream Gateway Protocol check** workflow observes publication events,
reconciles nightly at **05:41 UTC**, and supports manual dispatch on the default
branch. It is separate from build CI and required PR checks. It never changes
which Gateway version Windows installs, pins a runtime dependency, merges a PR,
or activates a deployment.

## Receiver architecture

The collector deterministically compares three independent tracks:

- **Main early warning:** an immutable `openclaw/openclaw` main commit and watched
  source/blob inventory. Schema, method authorization, Gateway implementations,
  agent/auto-reply producers and reference UI sources are watched; tests and
  fixtures are excluded. Missing groups or truncated trees fail explicitly.
- **Released identity:** `openclaw@latest`, exact integrity and registry-supplied
  SLSA publication-workflow identity. Released-source inventory and deltas remain
  `null`, not empty success results.
- **Published schema:** the independently versioned
  `@openclaw/gateway-protocol@latest` JSON schema, read in memory from its
  SHA512-checked tarball. No upstream source, archive paths or package scripts
  are executed. On a publication event only the announced package is pinned
  to its exact version; the other track remains independently latest.

Before any candidate publication or handoff, the write boundary validates the
report, recomputes local capability evidence, checks the recorded commit against
upstream main ancestry, rereads its immutable tree, and rechecks the exact npm
schema artifact. This authorizes **investigation only**, not a statement of
released-source compatibility. Sender `source_commit`, registry `gitHead`,
version tags and SLSA `workflowSha` are not independently verified
source-to-package bindings. Registry SLSA signatures are not independently
verified here. The agent must independently confirm relevant behavior in current
production and inspect existing work before proposing an implementation.

For example, the historical `2026.9.9` publications recorded workflow revision
`2b988ee83444f08ccaf37aa5e98f370726c75f6e`, while the version tag pointed to
`bcfc88812a35243893585dbeca87ca41b48272ca`. Those are different facts, not
interchangeable source evidence. Version-1 reports that mislabeled tooling
inventories as released source remain excluded from baselines.

## One tracker, focused investigations

One permanent issue has the `upstream-gateway-tracker` label and canonical
`upstream-gateway-tracker:v1` marker. The monitor replaces only its delimited
bot-owned section when candidate evidence or status changes. Human prose outside
that section and all discussion survive. Run IDs, package versions, timestamps
and unrelated daily commits do not refresh its body. Every run still retains
its full observation artifact. Keep this tracker open; do not assign it to Copilot.

Focused candidate issues use the separate `upstream-gateway-protocol` label and
`upstream-gateway-candidate:v1` marker. Identity is the relevant family contract,
including referenced schema definitions, shared envelopes and reviewed family
policy. It excludes publication metadata, the entire main inventory and broad
Windows C# hashes. A closed exact candidate acknowledges that evidence and is
never automatically reopened just because conservative static assessment remains
pending. A changed relevant contract can become a new candidate.

Before creating work, bounded queries reconcile candidate history, generic open
monitor work, open issues, existing Copilot assignments and open PRs. Question,
interactive Gateway and chat-parity overlap is checked in titles **and bodies**
for the question-answer family. Related work is linked in the tracker rather
than duplicated; it is not proof of complete support. Closed overlap no longer
defers new evidence. Unrelated work is not suppressed by that family check.

One monitor candidate is worked at a time. The handoff also checks the
repository Agent tasks API for queued, in-progress, idle and waiting-for-user
tasks before starting anything. It uses `POST /agents/repos/{owner}/{repo}/tasks`
with **`create_pull_request: false`**, not issue assignment, because assignment
can create an automatic PR. Candidate issues remain human tracking artifacts.
The prompt requires these outcomes:

| Finding | Required outcome |
| --- | --- |
| Supported | Production and behavioral-test evidence; close the candidate without a PR. |
| Intentionally unsupported | Scoped rationale and visible fallback; no PR. |
| Existing work | Link the issue/task/PR and explain coverage; do not duplicate it. |
| Confirmed missing current behavior | At most one focused **draft** implementation PR, with a closing reference, production/fixture changes where needed, and required proof. |
| Blocked or unassessed | Concrete blocker or remaining review scope; never claim support or create an empty PR. |

The API flag prevents an automatic PR at task creation. Later agent behavior
is constrained by instructions and review, not an absolute technical guarantee
that the agent cannot open a PR. A task receipt is not implementation or proof.

The candidate bot-owned section records a reservation **before** the task POST,
then the confirmed task ID. An uncertain response, rejected request or interrupted
run never automatically retries that reservation. Inspect the task service and
record the matching task ID or an explicit disposition before proceeding. Do
not simply delete the reservation and rerun. Task artifact pull IDs are database
IDs, not PR numbers; the monitor uses issue timeline closing references for PR
reconciliation. Legacy assignments and linked draft PRs are reused, never replaced. Copilot
is recognized by its stable GitHub account ID, including the returned `Copilot`
identity rather than the request-login spelling.
Failed/timed-out tasks, completed tasks without disposition, closed PRs with open
candidates, and tasks stalled beyond 24 hours require attention rather than
another task. The workflow concurrency group serializes this monitor, not all
human or third-party GitHub activity; state is reread before side effects.
GitHub keeps only one pending run per concurrency group, so bursts can coalesce
publication events. Nightly polling reconciles current state, not every canceled
exact version; this is not a durable publication-event queue.

## Assessment and review baseline

The current policy explicitly tracks question-answer operator behavior. Producer
`question.request` and `question.waitAnswer` exemptions have scoped rationales
and fallbacks; they do not exempt operator list/get/resolve, lifecycle events,
scopes, IDs, choices/multiselect, cancellation, expiry, answer submission or
reconnect recovery. Authorization changes invalidate those role exemptions.
Other unknown methods/events remain visible review backlog, not a demand to
implement every schema surface. Main-only inventory changes likewise remain
early warning rather than automatic PR stimulus. This is not whole-product
Windows support certification.

`report.json.capabilities` retains local head/dirty state, production/test SHA256
inventory, policy/input hashes, wiring candidates and grouped assessments.
Comments, dead strings, DTOs, fixture implementations and even dispatch calls
alone never establish support. A reviewed supported policy decision must bind
the current input hash and production dispatch/handler/construction plus
behavioral-test witnesses. Broad evidence changes invalidate that attestation;
they do not themselves mint a new family candidate.

The last retained closed legacy full-review issue without a pending marker can
still supply cumulative comparison deltas. Scoped candidate closure and the
permanent tracker do **not** advance a whole-inventory baseline. Without a valid
reviewed baseline, the report requests baseline review rather than assuming an
all-clear. Expired artifacts cannot provide a comparison baseline; issue state
still deduplicates reviewed candidates independently of artifact retention.

## Activate deliberately

1. Merge the receiver first. Writes are disabled until a repository owner sets
   Actions variable **`UPSTREAM_GATEWAY_MONITOR_ENABLED=true`**. Leave it unset
   while reviewing observations. Manual `report_only` defaults to `true`; it
   suppresses candidate, tracker, health and task writes even after activation.
2. Configure the existing upstream **ClawSweeper GitHub App** for the selected
   Windows target repository with **Contents: write** (required for repository
   dispatch), and keep using its existing upstream private-key secret. Do not
   add the App credential to Windows receiver jobs. Merge/configure the sender
   separately and verify an actual delivery; receiver tests cannot prove it.
3. Windows observation uses `GITHUB_TOKEN` with contents/actions/issues read.
   Publication and task-state persistence use the Windows workflow token with
   **issues: write** and pull-requests read, never the App private key.
4. Enable Copilot cloud agent for the repository and sponsoring user. The
   preview Agent tasks POST currently requires **Copilot Business or Enterprise**
   and organization approval/budget. Reuse **`COPILOT_GITHUB_TOKEN`**, a user
   access token with repository **Agent tasks: read/write**. Installation tokens
   and `GITHUB_TOKEN` are not substitutes for that API. The token is confined to
   task API calls in the handoff step; ordinary issue writes retain the workflow
   token. There is no issue-assignment fallback.
5. Run `report_only: true` and inspect evidence and source limits. When satisfied,
   the owner can enable the variable, run `report_only: false`, and inspect the
   tracker, candidate, task ID, disposition and any linked draft PR. Do not
   claim authenticated task creation or no-PR disposition until that controlled
   activation has actually been observed. Unset the variable to stop new writes.

Nightly reconciliation does not require a new package publication or a working
sender. Missing Copilot credentials still leave useful tracker/candidate issues
visible after issue publication, then fail handoff explicitly without implying
an agent started. Token permissions and paid-plan eligibility must be checked
for the actual configured token, not inferred from another authenticated GET.

Reference: [GitHub Agent tasks API](https://docs.github.com/en/rest/agent-tasks/agent-tasks).
The receiver sends API version `2026-03-10`; the preview contract may change.

## Exact publication notification

`repository_dispatch` uses event type `gateway-protocol-published` and exactly
these `client_payload` fields:

```json
{
  "schema_version": 1,
  "package_name": "openclaw",
  "package_version": "2026.9.9",
  "package_integrity": "<npm sha512 SRI>",
  "source_repository": "openclaw/openclaw",
  "source_commit": "<40 lowercase hex characters>"
}
```

The other accepted package is `@openclaw/gateway-protocol`. Versions are exact
three-part versions with optional prerelease suffixes, not tags, ranges, build
metadata, refs or URLs. Extra fields are rejected. The receiver checks exact
registry integrity, publisher-workflow provenance and declared commit existence
in the fixed upstream repository. Existence is not package-source binding.
Payload fields never enter shell code and no notification authorizes executing
contributor code. Trusted default-branch scripts own observation and publication.

## Evidence and recovery

The `upstream-gateway-protocol-report` artifact retains `report.json`, `report.md`
and notification readiness for 90 days. Main hashes, exact package identities,
schema/tarball/provenance hashes and full deltas are reproducibility evidence,
not runtime dependency pins. Readiness accepts a validated current dispatch or
a successful observe job among the latest 10 retained default-branch dispatch
runs. Missing delivery is **unverified** and API failure **unknown**, both
warnings, not Gateway defects. Receipt does not prove sender identity, continued
delivery or package-source binding.

Collection/reconciliation failure uses one separate
`upstream-gateway-protocol-health` issue when writes are enabled. Inspect the
failed job and close it after verified recovery. The obsolete permanent
source-binding activation failure is no longer emitted; package-source limits
remain explicit in reports and instructions. Existing old health issues are
not silently rewritten into compatibility approvals.

Responses and decompressed tarballs are bounded to 32 MiB, source inventory to
10,000 files, and HTTP requests to 60 seconds. Observation has a 15-minute job
budget; publication/handoff have 10 minutes each. Issue/timeline/task queries
stop at 2,000 records and fail closed rather than treating partial history as
absence. Preserve labels, identity markers and bot-owned delimiters. Missing or
ambiguous canonical state needs manual reconciliation, not another tracker.

## Validation

Deterministic transport-boundary tests require no credentials or live service:

```powershell
node --test .github\scripts\upstream-gateway-protocol.test.cjs .github\scripts\gateway-capabilities.test.cjs
```

For authorized read-only live collection, choose a task-owned output directory:

```powershell
$env:GH_TOKEN = gh auth token
$env:OUTPUT_DIR = Join-Path $env:TEMP 'upstream-gateway-protocol-proof'
node .github\scripts\upstream-gateway-protocol.cjs
Remove-Item Env:GH_TOKEN
```

`PREVIOUS_REPORT` optionally selects an earlier `report.json`. To replay a
publication, set `GITHUB_EVENT_NAME=repository_dispatch` and `GITHUB_EVENT_PATH`
to a JSON file containing the event action and payload above. Collection remains
read-only and verifies against trusted services. Repository build/shared/tray
validation remains required; transport fixtures are not live task-creation proof.
The [offline drift guard](gateway-protocol-drift-guard.md) stays in ordinary CI.
