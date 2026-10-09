# Nightly upstream Gateway protocol monitor

The **Upstream Gateway Protocol check** workflow runs at **05:41 UTC daily** and
supports manual dispatch on the default branch. It is independent of `ci.yml`,
has no push/PR trigger, and must not be added to required PR checks. It does not
change which Gateway version Windows installs or the separate npm-latest policy.

## Observation, review and implementation

1. A read-only job resolves upstream `openclaw/openclaw` main, `openclaw@latest`
   and `@openclaw/gateway-protocol@latest` on every run. The generated protocol
   schema is read directly from the integrity-checked npm tarball in memory.
   No upstream code, package install scripts or archive paths are executed.
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
4. The last reviewed (closed) monitor issue's retained observation supplies
   cumulative field/source deltas, even if a later job in that run failed.
   Deferred nights never advance this review baseline or lose unreviewed deltas.
   Without one, the monitor requests a **baseline review**, not an all-clear.
   Optional field additions are informational; removals, constraints, scopes
   and new surfaces require review, not an automatic claim of breakage.
5. A separate job creates one durable, labeled **review-candidate issue**.
   A content fingerprint excludes moving commit IDs, timestamps and version
   labels. Both open and closed issues deduplicate the same observation.
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
Closing the issue acknowledges that fingerprint. Reopen it to request another
attempt. Removing Copilot's assignee allows reassignment only when no linked
implementation PR exists. A linked PR must have a `Closes`/`Fixes`/`Resolves`
reference to the issue; unrelated mentions are not remediation evidence. Closed
implementation PRs with an open issue fail visibly and require reconciliation
instead of spawning duplicates. Do not close pending work merely to make the
monitor green.

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
2. Add the dedicated Actions secret **`UPSTREAM_GATEWAY_COPILOT_TOKEN`**, using
   a Copilot-enabled user token. GitHub documents fine-grained PAT access to this
   repository with metadata read and actions, contents, issues and pull requests
   read/write for issue assignment. Classic PATs use `repo`. Prefer the
   fine-grained, repository-scoped token. Installation tokens and the workflow
   `GITHUB_TOKEN` are not a substitute for the documented user authentication.
   Do not silently reuse the repository's unrelated `COPILOT_GITHUB_TOKEN`.
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

At implementation time, the dedicated secret was not configured. Authenticated
end-to-end issue-to-draft-PR execution is therefore **not verified** until
activation. The generic REST collaborator-assignee probe is not a valid test of
Copilot availability; the monitor verifies the actual documented assignment
response instead.

## Evidence, limits and troubleshooting

The `upstream-gateway-protocol-report` artifact retains `report.json` and
`report.md` for 90 days: exact source commits/blob hashes, npm package
versions/integrities, tarball/schema/provenance SHA256 hashes and full classified
deltas and the reviewed baseline fingerprint. Provenance is reproducibility metadata, not a runtime dependency pin.
Issue state, not artifact retention or a hand-maintained frozen snapshot,
controls duplicate suppression. If old artifacts expire, the next observation
requests a baseline comparison, but an already-reviewed fingerprint stays
deduplicated.

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
node --test .github\scripts\upstream-gateway-protocol.test.cjs
```

For read-only live collection (outputs outside the checkout):

```powershell
$env:GH_TOKEN = gh auth token
$env:OUTPUT_DIR = Join-Path $env:TEMP 'upstream-gateway-protocol-proof'
node .github\scripts\upstream-gateway-protocol.cjs
Remove-Item Env:GH_TOKEN
```

Optionally set `PREVIOUS_REPORT` to a previous `report.json` to inspect deltas.
Local collection never creates an issue, assigns an agent or opens a PR.
Repository-wide build/shared/tray validation remains required for code changes.
The offline [Gateway protocol drift guard](gateway-protocol-drift-guard.md)
continues to protect client-vs-snapshot invariants in ordinary CI; this monitor
does not replace or slow it.
