# Upstream Gateway publication and capability monitor

The **Upstream Gateway Protocol check** workflow runs at **05:41 UTC daily** and
reconciles publication notifications and supports manual dispatch on the default branch. It is independent of `ci.yml`,
has no push/PR trigger, and must not be added to required PR checks. It does not
change which Gateway version Windows installs or the separate npm-latest policy.

## Observation, review and implementation

1. A read-only job resolves upstream `openclaw/openclaw` main, `openclaw@latest`
   and `@openclaw/gateway-protocol@latest` on every run. The generated protocol
   schema is read directly from the integrity-checked npm tarball in memory.
   No upstream code, package install scripts or archive paths are executed.
   On a publication event, only the announced package uses its exact version
   instead of `latest`. The other package remains an independent latest track.
2. The report separates **main early warning**, **released Gateway compatibility**
   and the **independently versioned protocol package**. Since npm may omit
   `gitHead`, released source is resolved from registry-supplied SLSA provenance,
   bound to the exact package SHA512 digest and upstream repository. This is not
   independent signature verification. Missing/mismatched provenance fails the
   observation rather than substituting main or an assumed version tag.
3. Full live source inventories watch protocol schemas, Gateway method metadata
   and authorization, Gateway implementations, agent/auto-reply producers and
   reference UI sources. These intentionally broad source groups catch new
   files and open `agent.stream`/`data` behavior that schemas cannot describe.
   Test/fixture files are excluded. A missing group, relocated registry or
   truncated API tree fails explicitly. Other upstream directories are outside
   this monitor's automatic watch scope; this is not a completeness guarantee.
4. The last reviewed (closed, without the pending-Windows marker) monitor issue's retained observation supplies
   cumulative field/source deltas, even if a later job in that run failed.
   Deferred nights never advance this review baseline or lose unreviewed deltas.
   Without one, the monitor requests a **baseline review**, not an all-clear.
   Optional field additions are informational; removals, constraints, scopes
   and new surfaces require review, not an automatic claim of breakage.
5. Every observation assesses current Windows source and tests, even without
   a baseline or upstream changes. A separate job creates one durable, labeled
   **review-candidate issue**. The content fingerprint includes local capability
   evidence, support policy and the exact publication identity when present.
   Moving commit IDs and timestamps alone do not trigger reassessment.
   Open issues deduplicate repeated observations. Closing an issue with a pending
   tracked Windows gap candidate does not acknowledge away the gap: the same issue reopens.
   Merely unassessed surfaces do not force reopening or prevent a reviewed baseline.
   With a different observation and an existing open monitor issue, new work
   waits rather than spawning competing implementation agents. Every run still
   records current evidence. After the active issue closes, the newest
   unreviewed fingerprint is eligible even if the previous artifact has no diff.
6. A separate credentialed job assigns that issue to **Copilot cloud agent** via
   GitHub's documented issue-assignment API. Copilot must compare CURRENT
   production and fixture code, inspect existing work, classify relevant
   surfaces and implement confirmed gaps in a **linked draft implementation PR**.
   A snapshot-only/empty PR is not remediation. No automatic merge is configured.

Hash/source changes are review candidates, not proven defects. The implementation
agent must provide these explicit classifications on the issue/PR:

| Classification | Required evidence |
| --- | --- |
| Supported | Current production wiring and behavioral tests, including fixture parity where applicable. |
| Intentionally unsupported | Rationale, scope and visible fallback; do not silently discard relevant events. |
| Pending | Concrete missing behavior, implementation owner/PR or exact blocker. |

Review includes chat and question RPCs/events, scopes and authorization,
session/run/question IDs, choices and multiselect/presentation modes, expiry,
cancellation, answer submission and reconnect recovery. Questions are not
`agent` events with `stream=approval`. The fixture must exercise the real client
and preserve its default read-only isolation. Existing chat parity work should
be linked instead of duplicated.

If all changes are already supported or intentionally unsupported, the agent
records evidence and closes the issue without creating a placeholder PR.
Closing the issue acknowledges that fingerprint only when no tracked Windows gap
candidates remain pending. Otherwise a later run reopens it, subject to active-issue
backpressure. Removing Copilot's assignee allows reassignment only when no linked
implementation PR exists. A linked PR must have a `Closes`/`Fixes`/`Resolves`
reference to the issue; unrelated mentions are not remediation evidence. Closed
implementation PRs with an open issue fail visibly and require reconciliation
instead of spawning duplicates. Do not close pending work merely to make the
monitor green. Before assignment, the handoff also checks open PR titles for
question-answer, interactive Gateway/fixture and chat-parity work. It adds an idempotent
comment linking candidates and fails visibly with `related-work-needs-review`, without
starting another agent. Existing assignment/stall checks take precedence.
Title matches are a conservative collision check, not
proof that the PR implements all gaps. A maintainer must reconcile coverage;
unrelated mentions must not be presented as completed remediation.

## Exact publication event contract

The primary trigger is `repository_dispatch` with event type
`gateway-protocol-published` and exactly these `client_payload` fields:

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

`package_name` also accepts `@openclaw/gateway-protocol`. The version must be
an exact three-part published version, optionally with a prerelease suffix;
tags, ranges, build metadata, arbitrary URLs/refs and extra fields are rejected.
The receiver fetches that exact registry version, compares its SHA512 identity,
binds the source commit using registry SLSA provenance, and verifies the commit
in the fixed upstream GitHub repository. Payload fields never enter shell code.
It does not resolve the announced package back to moving `latest`.

The payload is a notification, not authority to run code or write. Observation
still has read-only permissions and runs trusted default-branch scripts only.
Publication and implementation remain separate jobs; the existing
`COPILOT_GITHUB_TOKEN` remains confined to handoff. Repeated exact deliveries
deduplicate when watched upstream and local evidence have not changed.
Nightly reconciliation catches missed notifications and retains main early warning.
Activation requires this workflow on the default branch and an upstream sender
authorized to send the event. Receiver tests do not establish sender delivery.

## Windows support evidence

`report.json.capabilities` contains the exact local head, dirty-source indicator,
SHA256 inventory of current tracked/untracked, non-ignored C# production/test
files, policy hash, combined upstream/local input hash, per-surface assessments,
and grouped feature obligations. `report.md` highlights the counts and grouped
pending candidates, including **question-answer**, on the first observation and
every unchanged-upstream run. All RPCs in the live method registry and all named
`*Event` definitions are assessed. New unknown surfaces remain pending relevance
review; they are not automatically required Windows features.
The pending count is not a defect count. The handoff is explicitly limited to
one evidenced, confirmed production capability gap per implementation PR;
unassessed surfaces remain review backlog rather than a bulk implementation task.

The reviewed local policy in `.github/scripts/gateway-capability-policy.json`
describes Windows roles, not a frozen upstream schema contract. Producer-only
`question.request` and `question.waitAnswer` have scoped intentional-unsupported
rationales and fallbacks. This exemption never covers operator list/get/resolve,
question events, scopes, choices/multiselect, IDs, submission or recovery.
Authorization-scope changes invalidate those exemptions.

The collector reuses the drift guard's index-aligned comment/literal masking
approach and distinguishes dispatch calls, event-case candidates and request
construction. These are navigation evidence only. Comments, dead strings, DTOs,
mock implementations and even real call sites cannot by themselves establish
support or reachability. No current surface has an automatically inferred
supported classification. Missing extraction is a review gap, not proof of
absence across all possible dynamic dispatch patterns.

A reviewed `supported` policy decision must supply `surface`, `rationale`,
the exact current `inputHash`, and `evidence` entries (`kind`, `file`, `sha256`)
covering `dispatch`, `handler`, `construction` and `behavioral-test`.
Production witnesses must be under `src/`, tests under `tests/`. Reviewers must
inspect reachable production ownership and actual tests, not merely fill hashes.
This is a reviewed attestation, not an automated test execution result.
Changed production/test or watched upstream evidence invalidates it to pending.
The initial policy intentionally has no supported attestations.
The write-authorized publisher recomputes the assessment from its trusted
checkout and refuses stale or altered artifact assessments before any write.

The broad source hashes intentionally over-invalidate reviewed support rather
than silently retaining it after a refactor. Main-only changes are early-warning
review input, not an assertion that the released Gateway broke.
Open event payloads and producer behavior not represented in schema still need
the cumulative upstream source-delta review. This is not whole-product coverage.

## Activation and authentication

Observation and issue publication use the scoped `GITHUB_TOKEN`:
`contents: read`, `actions: read`, `issues: read` for observation;
`issues: write` for publication.
They do not receive the agent credential, and no upstream source text is copied
into publication instructions. Artifacts/schema strings remain untrusted data.
Only trusted default-branch workflow code runs; checkout credentials are not persisted.

To activate implementation handoff, a maintainer must:

1. Enable Copilot cloud agent for this repository and for the sponsoring user,
   with sufficient premium-request budget and organization policy approval.
2. Reuse the repository's existing Actions secret **`COPILOT_GITHUB_TOKEN`**.
   No separate monitor secret is required. It must be a Copilot-enabled user
   token. GitHub documents fine-grained PAT access to this
   repository with metadata read and actions, contents, issues and pull requests
   read/write for issue assignment. Classic PATs use `repo`. Prefer the
   fine-grained, repository-scoped token. Installation tokens and the workflow
   `GITHUB_TOKEN` are not a substitute for the documented user authentication.
3. Manually dispatch with `report_only: true` and inspect the report. Then
   dispatch with `report_only: false`, verify the issue assignee and follow the
   Copilot session to its linked draft implementation PR.

Reference: [GitHub's Copilot issue-assignment API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api#using-the-issues-api).

Missing credentials, unavailable Copilot assignment, API errors or unconfirmed
assignment fail the workflow **after preserving the issue**. Retrying resumes
the same issue, not a new nightly task. An assigned issue with no linked PR
after 24 hours from its latest assignment event fails with an instruction to inspect Copilot logs, without
automatically spending on another attempt. Assignment success alone is reported
as **draft PR not yet observed**, never as completed remediation. Copilot
execution/validation is asynchronous and still subject to its environment,
permissions and the normal human review/merge process.

The repository already provides `COPILOT_GITHUB_TOKEN`; the monitor reuses it
only in the implementation handoff job. Authenticated end-to-end
issue-to-draft-PR execution remains **not verified** until the first publishing
run confirms assignment and a linked draft PR. The generic REST collaborator-assignee probe is not a valid test of
Copilot availability; the monitor verifies the actual documented assignment
response instead.

## Evidence, limits and troubleshooting

The `upstream-gateway-protocol-report` artifact retains `report.json` and
`report.md` for 90 days: exact source commits/blob hashes, npm package
versions/integrities, tarball/schema/provenance SHA256 hashes and full classified
deltas and the reviewed baseline fingerprint. Provenance is reproducibility metadata, not a runtime dependency pin.
Issue state, not artifact retention or a hand-maintained frozen snapshot,
controls duplicate suppression. If old artifacts expire, the next observation
requests a baseline comparison. A fully resolved fingerprint stays deduplicated;
still-pending local assessments remain actionable regardless of issue closure.

The collector caps each response/decompressed tar at 32 MiB, each source
inventory at 10,000 files and HTTP requests at 60 seconds. Jobs are bounded to
15 minutes for observation and 10 minutes per publication/handoff job, with one
repository-wide concurrency group. The publisher reads
at most 2,000 labeled issues or timeline records, then fails rather than
creating duplicates from incomplete history. State/label changes by maintainers
should be deliberate: removing the monitor marker or label breaks deduplication.
Versions with no changed watched source/schema content are deduplicated even
if unrelated upstream directories changed. Watch scope is not whole-repository
or whole-package compatibility coverage.

Observation failures also create or reopen one separate
`upstream-gateway-protocol-health` issue, linking the latest failed run without
copying untrusted error text. It does not launch a production implementation
agent. After fixing and verifying collection, a maintainer closes that health
issue. Manual `report_only` runs do not publish even health issues.

One active issue provides backpressure, not a permanent ignore list. If it
stalls, inspect the agent/PR, resolve the blocker and retry or close it with an
explicit unsupported decision. New content is recorded in artifacts while
waiting. The agent is not authorized to weaken the monitor or alter workflow
permissions to make its findings disappear.

## Local validation

No credentials or network are required for the deterministic tests:

```powershell
node --test .github\scripts\upstream-gateway-protocol.test.cjs .github\scripts\gateway-capabilities.test.cjs
```

For read-only live collection (outputs outside the checkout):

```powershell
$env:GH_TOKEN = gh auth token
$env:OUTPUT_DIR = Join-Path $env:TEMP 'upstream-gateway-protocol-proof'
node .github\scripts\upstream-gateway-protocol.cjs
Remove-Item Env:GH_TOKEN
```

Optionally set `PREVIOUS_REPORT` to a previous `report.json` to inspect deltas.
For read-only replay of a publication, set `GITHUB_EVENT_NAME=repository_dispatch`
and `GITHUB_EVENT_PATH` to a JSON file containing `action` and `client_payload`
from the contract above. The collector verifies it against live trusted services.
Local collection never creates an issue, assigns an agent or opens a PR.
Repository-wide build/shared/tray validation remains required for code changes.
The offline [Gateway protocol drift guard](gateway-protocol-drift-guard.md)
continues to protect client-vs-snapshot invariants in ordinary CI; this monitor
does not replace or slow it.
