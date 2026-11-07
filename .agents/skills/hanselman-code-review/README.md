# Hanselman Code Review Skill

Repository-owned adversarial review skill for OpenClaw Windows Node. It runs
independent app-native reviews with **GPT-6 Astra** (`gpt-6-astra`) and
**Claude Opus 5.5** (`claude-opus-5.5`), both at high reasoning effort. Astra
uses the supported `long_context` tier without assuming a numeric size.

## How it works

1. Pin one exact review scope, including the head and base revisions.
2. Give both reviewers the same code, context, constraints, and severity rules.
3. Run the two model families independently and in parallel.
4. Verify every finding against the code, tests, and dependency contracts.
5. Report shared findings as high consensus and one-model findings as low or
   disputed consensus, with a coordinator-assessed fix confidence.

## Usage

Ask for any of:

- "Do a Hanselman review of my changes."
- "Run an adversarial code review."
- "Dual-model review this pull request."
- "Review this change with confidence ratings."

Follow [SKILL.md](SKILL.md) for exact scope, identical reviewer prompts, model
selection, consensus tables, and fix-confidence assessment. Neither a Codex
CLI nor a Claude CLI is required.

## Output

```text
Both models agree: HIGH consensus

| Issue | Opus 5.5 | GPT-6 Astra | Fix confidence |
|---|---|---|---|
| Concrete verified issue | HIGH | HIGH | 95% |

Only one model flagged: LOW consensus

| Issue | Opus 5.5 | GPT-6 Astra | Fix confidence |
|---|---|---|---|
| Verified edge case | not flagged | MEDIUM | 80% |
```

Repeat reviews can target changed code and unresolved findings. Always record
the actual model IDs used. If a model is unavailable, request approval for an
alternative rather than silently falling back to an older model.

Both reviewers must complete on the same revision before the result is called
a Hanselman review. A failed reviewer or changed head makes the review
incomplete or stale, not clean. A completed source review remains advisory
until the coordinator verifies its findings.

This repository copy follows the existing OpenClaw proof and scope gates.
Source-review agreement does not replace runtime evidence.

## License

[MIT](LICENSE.md), copyright Scott Hanselman.
