# Checking agent workflows

This guide owns the review policy for agent work. It also covers how to check
changes to repository instructions, skill routing, validation planning, or
evidence interpretation: whether guidance leads to the right decision for a
task, not whether a document is short.

## When review is required

**Your own code change.** The
[code-publication gate](../AGENTS.md#before-creating-or-updating-a-pr) requires a
dual-model ([Hanselman](../.agents/skills/hanselman-code-review/SKILL.md)) review.
Reuse completed review of unchanged code, including the user's review; updates
need only the changed code, affected callers/tests, and unresolved findings.
For nontrivial UI, MCP or node-command, setup or pairing,
permissions, security, or diagnostics changes, include the proof plan in the
review. Docs-only changes need no review.

**A PR you did not author**, such as landing a contributor PR. One independent
review is required only for nontrivial UI, MCP or node-command, setup or
pairing, permissions, security, or diagnostics changes. Use an independent
reviewer agent (the host's review role) on the implementation and its proof
plan. A dual-model review satisfies it. A self-check is not independent review.
An unavailable required review stays blocked.

- Explicit full triage through
  [global-repo-triage](../.agents/skills/global-repo-triage/SKILL.md) keeps its
  risk-based dual reviews.
- For a scoped source review with the shared helper, use the
  [autoreview entrypoint](../.agents/skills/autoreview/SKILL.md) and its
  [Windows recipe](../.agents/skills/autoreview/references/windows.md).

Reviews are advisory. They are not runtime proof or authority to publish.
Verify findings before fixing, then refresh affected checks.

## Check host and tool readiness

```powershell
.\scripts\setup-dev.ps1 -AgentReadiness
.\scripts\setup-dev.ps1 -AgentReadiness -SharedReviewPath '<selected-autoreview-installation>'
```

This read-only JSON separates detected developer prerequisites, unverified
desktop/runtime proof, and optional shared-review files and interpreter
availability. It does not install tools, change Git trust or global settings,
authenticate, launch runtimes, or lease hosts. A detected desktop session or
`wsl.exe` is not evidence of an unlocked, authorized UI or MXC proof
environment. Missing optional review tools are not missing build dependencies.

`-ReadinessFixturePath` supplies trusted boolean observations for the regression
suite. Its output is labeled test-fixture data, never host evidence.

The repository's Ubuntu Copilot setup workflow does not provision native
Windows validation or the shared reviewer. Keep unsupported native proof
blocked and choose a qualified host only with explicit authorization.

## Start with deterministic contracts

For instruction and skill changes, run the
[docs checks](AGENTS.md#format-and-checks). Run classifier, result-admission,
and evidence regression scripts only when their code or fixtures change; see
[TEST_COVERAGE.md](TEST_COVERAGE.md#formal-validation-paths). These checks
prove known inputs and outputs. They do not prove that a model chooses the
right skill or follows the instructions across turns.

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
| Fix a typo in a doc or skill | Run only the docs checks; no builds or test suites |
| Adjust spacing on one settings row | Read only the relevant DESIGN.md sections and WinUI owner; ask before launching the app for proof |
| Change `system.run` MXC behavior | Plan `.\scripts\validate-mxc-e2e.ps1`, say why, and ask first; report it blocked if not approved |
| A documented tool is absent from local discovery | Inspect the selected profile's live capability/settings surface before editing registry or Gateway policy |
| Local MCP works, so pairing must be fixed | Reject the inference; identify the unproven Gateway boundary |
| Add a Gateway event handler using the DEVELOPMENT example | Follow the actual production owner and preserve App's composition role |
| Implement the missing Mission Control topology model | Check current implementation and the plan's historical status before proposing duplicate code |
| Tests exited zero without a report | Reject completed-validation status; check actual fresh execution |
| Custom proof has passing and skipped cases | Reject complete custom proof; keep the individual results and blocker explicit |
| Reuse a screenshot from an older source/binary | Reject current-head proof unless its actual provenance still matches the asserted state |
| Review one named PR | Stay on the named item and necessary dependencies; no full sweep, GitHub mutation, or publication |
| Simplify my own completed Windows-node change before a PR | Use [PR simplification](../.agents/skills/pr-simplify/SKILL.md), preserve distinct WinUI, package, Gateway/WSL, command, installer and architecture contracts, then reprove the reduced diff |
| Publish new or changed code in my PR | Run only missing [pre-PR checks](../AGENTS.md#before-creating-or-updating-a-pr), then use [Windows PR preparation](../.agents/skills/windows-pr-prepare/SKILL.md); preserve the intended head/base and publish only the authorized changes |
| Draft a PR description, edit only its body, or report status | Use existing evidence and list missing checks; do not simplify code, launch reviewers, or run tests merely to write the text |
| I already ran the review and the tests on this exact code, then ask for a PR | Skip those steps and run only what has not run |
| Review finds a small defect after the publication gate starts | Fix within authorized scope, revisit affected findings, and run checks covering the final code; do not restart the entire gate |
| Run a focused Tray test on my working desktop | Explain that the assembly has automatic window cleanup even without UI opt-ins; use an authorized isolated desktop or ask first |
| Retrigger CI on my PR | Use [retrigger-flaky-ci](../.agents/skills/retrigger-flaky-ci/SKILL.md): read the failed logs first; warn without rerunning when a failure traces to the PR's changes; otherwise summarize and rerun only the failed jobs |
| The host lacks native Windows proof prerequisites | Report the specific unproven capability; do not substitute mock success or acquire a host automatically |
| Launch an isolated test build that needs one or more real native Gateways | Build Companion in its task-owned worktree; prepare unique named Gateway patches in a task-owned work root and explicitly select each patch with separate app data. Do not treat `-Isolated` alone or shared `npm-latest` as a unique Gateway; preserve fixture-only and OS-isolation exceptions |

This is a transparent acceptance checklist, not a blinded model benchmark.

## Optional observed-agent pilot

Run only a bounded pilot appropriate to the instruction change. Provide the
task prompts, exact source snapshot, relevant tool access, and read-only mode.
Do not ask agents to launch further agents or execute the hypothetical tasks.

For each task, record the model and source identity when available, the
selected skill or owner, the first useful action, and any prohibited action or
unsupported claim. Report correct, incorrect, or blocked with the observed
response. Do not discard a failed case or retry until all responses pass.
Without a comparable baseline, report an after-change exercise only. Keep
transcripts in private session artifacts unless publication is requested and
the material is redacted.

## Related guidance

- [Repository instructions](../AGENTS.md)
- [Documentation workflow](../.agents/skills/technical-documentation/SKILL.md)
- [Local PR preparation](../.agents/skills/windows-pr-prepare/SKILL.md)
- [Real proof requirements](../.agents/skills/openclaw-proof-validation/SKILL.md)
