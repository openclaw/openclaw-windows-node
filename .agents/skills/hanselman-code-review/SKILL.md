---
name: hanselman-code-review
description: Dual-model code review at code publication or on explicit request. Reuse unchanged-code reviews; do not trigger for drafts or metadata-only PR edits.
---

# Hanselman dual-model code review

Run two independent reviews of the same revision with different model
families, then verify their findings against evidence and report what needs
attention.

## When to use

Use at the [code-publication gate](../../../AGENTS.md#before-creating-or-updating-a-pr),
reusing review already completed by the user or an agent on unchanged code. Also
use it when the user asks for a Hanselman, dual-model, or adversarial review, or
when [global-repo-triage](../global-repo-triage/SKILL.md) calls it. For whether
a review is required at all, see the
[review policy](../../../docs/AGENT_WORKFLOWS.md#when-review-is-required).

## Review workflow

### Step 1: Pin the scope

Use the files the user names, `git diff` / `git diff --staged` for local
changes, `gh pr diff` for a PR, or a diff against the base branch. Include
relevant untracked files. Capture the base, head, and dirty content before
building the reviewer prompt, then compare the source again after both reviews.
If content changed, mark affected conclusions stale and review that delta;
unchanged content does not need another review just because its commit SHA changed.

For a repeat review, review the changed code, affected callers and tests, and
unresolved or disputed findings. State the delta scope.

### Step 2: Launch two parallel reviews

Launch two background agents with the host's independent review role (for
example `code-review`, or `general-purpose` with review-only instructions). If
the host has no such role, report the review as blocked. Use the two models
from [Model selection](#model-selection), and give both the **exact same
prompt** from the [template](#review-instructions-template), including the
full diff or files, project context, exact revisions, and constraints.

```javascript
task({
  name: "review-<model>",
  agent_type: "<host review role>",
  mode: "background",
  model: "<model from Model selection>",
  reasoning_effort: "high",
  prompt: "<shared review prompt>"
})
```

### Step 3: Wait for both to complete

Do independent work while they run, and read each result with `read_agent`
after its completion notification; do not poll. A Hanselman review requires
valid output from both models on the same revision. If either fails or returns
an incomplete assessment, report the review as incomplete. Keep any completed
output as advisory evidence; do not build a consensus table or substitute a
model without the user's approval.

### Step 4: Reconcile against evidence

Deduplicate findings and verify each against the actual code, dependency
contract, and tests. Keep valid single-reviewer findings; agreement is not a
severity or correctness measure. Record why disputed findings were accepted or
rejected. Distinguish reproduced defects, static support, and unresolved questions.

Use the review response as the record. Do not create a SQL table, extra artifact,
or numerical confidence score just for this review. A caller such as full
repository triage may separately require its own dashboard data contract.

### Step 5: Present the results

Lead with the verdict and the important findings. Include the reviewed source,
actual model IDs, and missing coverage. Do not describe an incomplete or stale
run as clean. When there are findings, use a compact table; otherwise say none
were found without manufacturing rows.

| Severity | Finding and location | Evidence | Suggested fix |
|----------|----------------------|----------|---------------|
| HIGH / MEDIUM / LOW | Concrete issue | Reproduced / static / unresolved | Bounded action |

### Step 6: Act on findings

- Ask which findings to fix unless the user already authorized the fixes.
- Prioritize verified findings by impact, not reviewer agreement. Fix within
  existing implementation authority; ask about scope or product decisions.
- Do not broaden the task to satisfy a reviewer.
- Model output is advisory: verify every accepted finding before acting. A
  clean review does not satisfy a missing proof pool, current-head UI proof,
  or required CI check.

## Review instructions template

```text
Review the following code changes for [PROJECT NAME].
This is a [LANGUAGE/FRAMEWORK] project with these constraints: [CONSTRAINTS].
Exact head: [HEAD]. Base or previously reviewed head: [BASE].

Focus on bugs, security issues, race conditions, and correctness problems.
Ignore style and formatting.
Write every review comment in simplified technical English. Use short sentences
and common words, explain necessary technical terms, and avoid jargon or
unnecessary detail.

[FULL CODE OR DIFF HERE]

For each issue found, classify severity as:
- CRITICAL: will crash, corrupt data, or create a security vulnerability
- HIGH: likely bug that will manifest in real use
- MEDIUM: edge case that could bite in specific scenarios
- LOW: minor improvement, theoretical concern, or robustness enhancement

For each issue, include:
1. What the issue is
2. Where it is (file and line/region)
3. Why it matters (impact and reachability)
4. Suggested fix
```

## Model selection

The default pairing is **Claude Opus 5.5** (`claude-opus-5.5`) and
**GPT-6 Astra** (`gpt-6-astra`, `context_tier: "long_context"`), both with
`reasoning_effort: "high"`. Use the tool's supported `long_context` value, not
an invented model name or suffix.

If a model or context tier is unavailable, report it and use an alternative
only with the user's approval. Record the actual model IDs and context tiers;
never fall back silently.
