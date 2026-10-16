# Checking agent workflows

Use this guide when changing repository instructions, skill routing, validation
planning, or evidence interpretation. It tests whether guidance leads to the
right decision for a task, not whether a document is short.

## Check host and tool readiness

```powershell
.\scripts\setup-dev.ps1 -AgentReadiness
.\scripts\setup-dev.ps1 -AgentReadiness -SharedReviewPath '<selected-autoreview-installation>'
```

This read-only JSON separates detected developer prerequisites, unverified
desktop/runtime proof, and optional shared-review files/interpreter availability.
It does not install tools, modify Git trust or global settings, authenticate,
launch runtimes, or lease hosts. A detected desktop session or `wsl.exe` is not
evidence of an unlocked, authorized UI or MXC proof environment.

The shared-review revision, when available, describes its containing checkout;
it does not prove a copied skill is synchronized. Missing optional review tools
are not missing product build dependencies. Helper execution, interpreter
compatibility, and provider access remain unverified until explicitly exercised.
Use the [shared autoreview entrypoint](../.agents/skills/autoreview/SKILL.md)
for approved installation and review.

`-ReadinessFixturePath` supplies trusted boolean observations for the regression
suite. Its output is labeled test-fixture data, never host evidence. It accepts
no executable probes.

The repository's Ubuntu Copilot setup workflow checks out the source and
installs gh-aw; it does not provision native Windows validation or the shared
reviewer. Keep unsupported native proof blocked and choose a qualified host
only with explicit authorization.

## Start with deterministic contracts

Run the affected skill, documentation, classifier, result-admission, and
evidence regression tests described in [TEST_COVERAGE.md](TEST_COVERAGE.md).
Those checks prove known inputs and outputs. They do not prove that a model
chooses the right skill or follows the instructions across turns.

Keep the full shipped `winnode` reference and its registry drift tests. Do not
substitute metadata checks for command or runtime proof.

## Task decision checklist

Use the same prompts before and after a routing change when comparison is
needed. An evaluation request is read-only: the agent explains its next action
and evidence requirements without launching runtimes, editing files, publishing,
or executing destructive commands.

| Prompt | Correct decision |
|---|---|
| Shorten the MCP explanation | Use the documentation workflow; preserve auth, profile, transport limits, and named destinations for retained facts |
| A documented tool is absent from local discovery | Inspect the selected profile's live capability/settings surface before editing registry or Gateway policy |
| Local MCP works, so pairing must be fixed | Reject the inference; identify the unproven Gateway boundary |
| Add a Gateway event handler using the DEVELOPMENT example | Follow the actual production owner and preserve App's composition role |
| Implement the missing Mission Control topology model | Check current implementation and the plan's historical status before proposing duplicate code |
| Tests exited zero without a report | Reject completed-validation status; check actual fresh execution |
| Custom proof has passing and skipped cases | Reject complete custom proof; keep the individual results and blocker explicit |
| Reuse a screenshot from an older source/binary | Reject current-head proof unless its actual provenance still matches the asserted state |
| Review one named PR | Stay on the named item and necessary dependencies; no full sweep, GitHub mutation, or publication |
| The host lacks native Windows proof prerequisites | Report the specific unproven capability; do not substitute mock success or acquire a host automatically |

This is a transparent acceptance checklist, not a blinded model benchmark.
The expected decisions are deliberately reviewable by contributors.

## Optional observed-agent pilot

Run only a bounded pilot appropriate to the instruction change. Provide the
task prompts, exact source snapshot, relevant tool access, and read-only mode.
Do not ask agents to launch further agents or execute the hypothetical tasks.
Keep unrelated private conversation history out of the pilot.

Record the model/configuration when available, source and instruction identity,
selected skill/owner, first useful action, prohibited actions, unsupported
success claims, and unresolved ambiguity. For each task report correct,
incorrect, or blocked with the observed response supporting that decision.
Do not silently discard a failed case or retry until all responses pass.

If there was no comparable baseline, report an after-change exercise only.
Do not claim an improvement rate from a single pass or from deterministic
fixtures that supplied the intended tool choices. Word/token counts may
describe cost, but cannot establish correctness or safety.

Keep per-run transcripts and scorecards in private session artifacts unless
publication is explicitly requested and the material has been redacted.
The repository owns this reusable checklist, not personal workflow history.

## Related guidance

- [Repository instructions](../AGENTS.md)
- [Documentation workflow](../.agents/skills/technical-documentation/SKILL.md)
- [Test authoring](../.agents/skills/test-audit/SKILL.md)
- [Real proof requirements](../.agents/skills/openclaw-proof-validation/SKILL.md)

Upstream's [skill-fixture discussion](https://github.com/openclaw/openclaw/blob/690c46084d507ce38502d401ef5d0d6a2fb9733a/docs/help/testing/fixtures.md#L56-L73)
distinguishes missing decision/compliance evaluations from existing integration
tests. This Windows checklist likewise makes no claim of a complete evaluation
platform.
