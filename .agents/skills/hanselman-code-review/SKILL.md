---
name: hanselman-code-review
description: "Adversarial dual-model code review that runs two AI models in parallel, cross-references findings, and produces a consensus severity table with fix confidence ratings. Use when asked for a thorough code review, adversarial review, dual-model review, confidence-rated review, or 'Hanselman review'."
---

# Hanselman Adversarial Code Review Skill

Run a rigorous, adversarial code review using two AI models in parallel.
Cross-reference their findings to separate signal from noise, producing a
consensus severity table with fix confidence ratings.

## When to Use

Invoke this skill when the user asks for:

- A thorough or deep code review
- An adversarial review
- A dual-model review
- A "Hanselman review"
- A confidence-rated review
- Cross-model validation of code changes

## Philosophy

Single-model reviews have blind spots. Two models reviewing independently
surface different classes of issues: security, correctness, and edge cases.
Cross-reference their findings:

- **HIGH consensus** (both models flag it): strong evidence; verify the issue.
- **LOW consensus** (only one model flags it): may be noise; use judgment.
- **Disputed** (a model flags it but evidence contradicts it): investigate
  before acting.

## Review Workflow

### Step 1: Identify the Scope

Determine what code to review:

- If the user specifies files, use those.
- If the user says "review my changes", use `git diff` or `git diff --staged`.
- For a PR, use `gh pr view` and `gh pr diff`, pinning the exact head and base.
- For a branch, diff against the base branch.

For a repeat review, compare the current head with the previously reviewed
head. Review changed code, affected callers/tests, and unresolved or disputed
findings rather than automatically repeating the full review. If the code is
unchanged and only the models changed, focus both reviewers on those findings
and high-risk decisions. State the delta scope and retain missing runtime-proof
gates.

### Step 2: Launch Two Parallel Reviews

Launch two `rubber-duck` agents via the app-native `task` tool in **background**
mode with different models. No standalone model CLI is required.

**Agent 1: Claude Opus 5.5**

```javascript
task({
  name: "opus-review",
  agent_type: "rubber-duck",
  mode: "background",
  model: "claude-opus-5.5",
  reasoning_effort: "high",
  prompt: "<full context + code + review instructions>"
})
```

**Agent 2: GPT-6 Astra**

```javascript
task({
  name: "astra-review",
  agent_type: "rubber-duck",
  mode: "background",
  model: "gpt-6-astra",
  context_tier: "long_context",
  reasoning_effort: "high",
  prompt: "<same context + code + review instructions>"
})
```

Both agents receive the **exact same prompt** so findings are comparable.
Include the full file contents or diff, project context, exact revisions,
constraints, and the severity definitions below.

### Step 3: Wait for Both to Complete

Use the background interval for independent work. Wait for each completion
notification, then read its result with `read_agent`; do not poll. Reconcile
both completed reviews before publishing a consensus.

### Step 4: Cross-Reference and Build the Consensus Table

Create a SQL table to track findings:

```sql
CREATE TABLE IF NOT EXISTS review_findings (
    id TEXT PRIMARY KEY,
    issue TEXT NOT NULL,
    opus_severity TEXT,
    astra_severity TEXT,
    consensus TEXT NOT NULL,
    fix_confidence INTEGER NOT NULL
);
```

If the session already has a findings table, inspect and reuse or migrate its
schema without discarding earlier findings. Record the actual model ID that
produced each review; do not relabel earlier findings as results from a
different model.

For each finding:

1. Check whether the other model found the same or a similar issue.
2. Assign consensus: both flagged it is `HIGH`; one flagged it is `LOW`;
   evidence contradicting a finding is `LOW (disputed)`.
3. Verify it against the actual code, dependency contract, and tests.
4. Assign fix confidence:
   - 99%: trivial fix, such as a wrong flag or missing null check.
   - 90-95%: straightforward fix requiring some design thought.
   - 70-80%: complex fix requiring research or trade-offs.
   - Below 70%: hard problem, intentional behavior, or architecture change.

### Step 5: Present the Results

**Both Models Agree: HIGH consensus**

| Issue | Opus 5.5 | GPT-6 Astra | Fix Confidence |
|-------|----------|-------------|----------------|
| Description of issue | SEVERITY | SEVERITY | XX% |

**Only One Model Flagged: LOW consensus**

| Issue | Opus 5.5 | GPT-6 Astra | Fix Confidence |
|-------|----------|-------------|----------------|
| Description of issue | SEVERITY or not flagged | SEVERITY or not flagged | XX% |

Use `not flagged` when a model did not identify the issue.

### Step 6: Act on Findings

- Ask which findings to fix unless the user has already authorized the fixes.
- Prioritize verified HIGH consensus issues.
- Explain one-model and disputed findings before acting.
- Do not broaden the task to satisfy a reviewer. Preserve the repository's
  scope and proof requirements.
- Fix confidence is the coordinator's assessment, not a model vote or a
  replacement for runtime proof.

## Review Instructions Template

Give both reviewers the same adapted prompt:

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

## Model Selection

The default pairing is **Claude Opus 5.5** (`claude-opus-5.5`) and
**GPT-6 Astra** (`gpt-6-astra`, `context_tier: "long_context"`), both with
`reasoning_effort: "high"`. Different model families provide independent
perspectives on the same evidence.

Use the tool's supported `long_context` value, not an invented model name or
model-name suffix. Do not assume a numeric context-window size. Preserve the
high reasoning default unless the user requests a different effort.

If a preferred model or context tier is unavailable, report the limitation and
use an available alternative only with the user's approval. Record the actual
model IDs and context tiers used; do not silently fall back to an older
generation.

## Important Notes

- Never skip cross-referencing. The value is in the comparison, not individual
  reviews.
- Log disputed findings and the reasoning for accepting or rejecting them.
- Do not fix LOW consensus issues without user authorization.
- A clean source review does not satisfy a missing Windows proof pool,
  current-head UI proof, or required CI check.
