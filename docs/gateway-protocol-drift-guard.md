# Gateway protocol drift detection

Gateway protocol compatibility has two complementary checks:
**publication-triggered upstream observation with nightly reconciliation** discovers changes outside this repository;
**offline regression tests** protect the client behavior already recorded here.
A passing offline test is not evidence that the Windows client matches current
upstream or implements every Gateway feature.

Neither check changes which Gateway release Windows installs or pins a runtime
dependency. See the
[Gateway, node, and exec flow FAQ](OPENCLAW_GATEWAY_NODE_EXEC_FAQ.md#are-we-pinned-to-a-gateway-protocol-version-or-a-gateway-release)
for protocol negotiation and release-selection behavior.

## Why the existing test did not catch upstream drift

`GatewayProtocolDriftTests` loads the local
`tests/OpenClaw.Shared.Tests/Protocol/gateway-protocol-snapshot.json` and compares
it with Windows client source. It does **not** fetch upstream schemas, resolve
npm latest, or connect to a live Gateway.

Consequently, if upstream renames a field but both the checked-in expectations
and client still use its old name, those two local inputs still agree and the
test passes. The test detects a local contract regression, not an upstream
change that has never been incorporated into its expectations.

Coverage is also deliberately limited: the fixture's `scopePrefixes` are
`sessions.`, `agents.files.` and `commands.`. The tests do not cover
`chat.*`, `question.*`, authorization changes, or arbitrary contents of open
agent event payloads. New question events, for example, cannot fail a test
whose declared surface contains no question methods or events.

| Check | Inputs | What a passing result means |
| --- | --- | --- |
| Nightly observation | Live upstream main, npm Gateway release sources, published protocol schema, producers and reference-client source inventories | Collection and comparison succeeded. Changed surfaces still require compatibility review. |
| `GatewayProtocolDriftTests` | Checked-in expectations and Windows client source | The selected local method, field and parser contracts agree. |
| Behavioral and loopback tests | Selected payloads, fixtures and the real client | The scenarios exercised by those tests behave as asserted. They do not discover upstream changes by themselves. |

## Nightly upstream observation

The standalone **Upstream Gateway Protocol check** workflow is the upstream
change-discovery path. It accepts exact-publication notifications, runs nightly
and supports manual dispatch on the
default branch, separately from normal build CI and required PR checks.
Scheduling begins after the workflow lands on that branch; authenticated
issue-to-draft-PR execution must be verified by a publishing run.

It observes these tracks separately:

- **Main early warning:** exact upstream `openclaw/openclaw` source commit and
  watched blob hashes.
- **Released compatibility:** `openclaw@latest` and the corresponding source
  commit from digest-bound registry provenance.
- **Published schema:** the integrity-checked
  `@openclaw/gateway-protocol@latest` artifact. Its package version is independent
  of the Gateway release and wire protocol integer.

Schema comparison alone cannot establish feature completeness. The monitor
also watches method authorization, Gateway implementations, agent/auto-reply
producers and reference-client sources. This includes chat/questions and
behavior carried in open `agent.stream`/`data` payloads.

Changed content creates a deduplicated **review-candidate issue**, not an
automatic declaration of breakage. The implementation handoff uses the existing
`COPILOT_GITHUB_TOKEN` and requires explicit **supported**, **intentionally
unsupported**, or **pending** classifications. Confirmed gaps require
production code and fixture-Gateway changes where needed, with behavioral
tests in a linked draft implementation PR. Already-covered work must be linked,
not duplicated. A schema-only or empty PR is not a behavior fix.

Reports also assess current Windows source/test evidence independently of
upstream deltas. On this branch, the first-run report explicitly marks operator
question-answer production support pending. Producer-only request/waitAnswer
methods have reviewed role exemptions, not blanket Q&A support. Unknown methods
and event definitions remain pending. Supported decisions require reviewed
production dispatch/handler/construction and behavioral-test evidence, and are
invalidated when that evidence changes. Closing an issue does not erase pending
local support. See the monitor document for the policy and exact event contract.

Exact commits, package versions and hashes are reproducibility evidence, not
runtime dependency pins. For schedule, watch scope, cumulative review baselines,
deduplication, credentials, failure handling and activation proof, see
[Nightly upstream Gateway protocol monitor](upstream-gateway-protocol-monitor.md).

## Offline regression checks

The existing offline tests remain useful and stay in the normal
`OpenClaw.Shared.Tests` suite. They compare selected client methods, request
fields and response parsers with **checked-in test expectations**, not a live
upstream source of truth. Those expectations are updated alongside reviewed
implementation changes; they are not how the nightly monitor discovers drift.

| File | Responsibility |
| --- | --- |
| `.github/workflows/upstream-gateway-protocol.yml` | Independent nightly/manual upstream observation and issue/agent handoff. |
| `tests/OpenClaw.Shared.Tests/Protocol/gateway-protocol-snapshot.json` | Reviewed expectations for the offline test subset, plus historical provenance. |
| `tests/OpenClaw.Shared.Tests/Protocol/GatewayProtocolDriftTests.cs` | Static client-versus-expectations regression checks. |
| `OpenClawGatewayClient*.cs`, `GatewayProtocolModels.cs` | Production dispatch, payload construction and parsing checked by the offline tests. |

The offline tests enforce these contracts:

| Contract | What is checked |
| --- | --- |
| Method surface | In-scope request dispatches and handled notifications match the fixture. A `used` method must remain wired; a `planned` method must not already be wired. |
| Session response fields | The selected `sessions.list` parser fields exactly match `responseFields` in both directions. |
| Request parameters | Constructed wire keys match `requestFields` in both directions, except explicit `allowedExtraRequestFields`. |
| Response envelopes | Each method's own parser reads its declared envelope. An unrelated parser cannot satisfy the check. |
| Fixture integrity | Required foundation shapes, scope, unique methods and usage/provisional-field invariants remain valid. |
| Tri-state clears | Declared nullable-clear contracts retain omission, value and explicit-null behavior in their builders and state types. |

The request checks inspect actual dispatch/construction regions rather than
matching words anywhere in a file. If a helper constructs a payload,
`requestFieldsSource` must identify its unique body. Likewise,
`responseEnvelopeSource` identifies the method's specific parser. Comments,
log messages and unrelated string literals cannot stand in for real wiring.
If a refactor invalidates one of those source selectors, repair the selector
and verify the actual behavior rather than weakening the contract.

### What is not enforced by those tests

Per-item `itemFields`, comments, notes and provenance fields are not validated
against live upstream. Most response shapes outside the selected session
parser require their own behavioral tests. `windowsUsage: "planned"` records
an unimplemented method; it is not a claim of feature support.

`upstreamVerified` and `snapshotUpdated` in the fixture describe historical
maintenance of that fixture. They are **not** the latest nightly observation,
an expiry check, or evidence that current upstream still matches. Use the
nightly report and implementation PR proof for current compatibility evidence.

### Tri-state clear regression coverage

For fields modeled with a nullable-clear contract, the client uses
`PatchField<T>`:

| State | Client representation | Wire result |
| --- | --- | --- |
| Unset | Leave the patch field unspecified | Omit the field. |
| Set | Assign a value | Send the value; the builder omits blank strings where required. |
| Clear | Assign `SessionPatch.Clear` | Send explicit JSON `null`. |

The static check protects the declared `tristateContract` wiring.
`GatewayProtocolModelsTests` covers emitted payloads, and
`GatewayProtocolLiveRoundTripTests` exercises selected frames through a loopback
WebSocket. These are regression tests for modeled semantics, not a claim that
every field in today's upstream schema has the same nullability.

## Resolving an upstream finding

1. Review the nightly issue and its exact source/schema provenance. Distinguish
   released compatibility from main-only changes, and check existing
   implementation PRs before starting duplicate work.
2. Compare current production and fixture behavior. Record supported,
   intentionally unsupported and pending surfaces with evidence. An additive
   optional field is not automatically a breaking change.
3. Implement confirmed gaps and add focused behavioral tests through the real
   client. Update the offline expectations for affected in-scope contracts in
   the same PR. New surfaces outside that subset need appropriate tests;
   changing an unrelated snapshot cannot establish their support.
4. When updating the fixture, keep method usage, request/response fields, parser
   selectors and tri-state contracts aligned with the verified implementation.
   Record the exact reviewed upstream commit and update provenance only for
   what was actually checked. Do not refresh dates or expected fields merely
   to silence a failure.
5. Run focused checks, the required repository validation and relevant behavior
   proof. Keep the implementation PR draft and record blockers honestly until
   its required proof is available. The nightly workflow never merges it.

Run the offline regression checks locally with:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --filter GatewayProtocolDriftTests
```

That command remains intentionally network-independent. Use a manual run of
**Upstream Gateway Protocol check** for fresh upstream observation, not the
filtered offline test command.
