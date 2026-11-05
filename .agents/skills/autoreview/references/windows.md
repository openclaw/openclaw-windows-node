# Windows shared-review recipe

Use this recipe when [review is required](../../../../docs/AGENT_WORKFLOWS.md#when-review-is-required)
or requested. The [shared skill][autoreview] owns helper behavior; read the
selected installation's `SKILL.md` and required references first. A missing
installation is a blocker. Review permission does not authorize installation,
commits, publication, hosted bot requests, or runtime changes.

## Select the candidate and context

Select the actual PR target or agreed base explicitly, not an assumed
`origin/main` or `auto`. The helper never fetches, so check available refs and
disclose stale or missing context.

| Intended scope | Explicit arguments | What is covered |
| --- | --- | --- |
| Only work since HEAD | `--mode local --max-priority P3` | HEAD through index and worktree, including untracked files |
| Full iterative candidate | `--mode local --base <merge-base-sha> --max-priority P3` | Pinned comparison through index and worktree, including untracked files |
| Committed branch | `--mode branch --base <observed-target-ref> --max-priority P3` | Merge-base to HEAD; excludes dirty and untracked work |
| One non-merge commit | `--mode commit --commit <commit-sha> --max-priority P3` | Parent to commit; merge commits are rejected |

The reviewer starts with an empty sandbox. Supply requirements through
`--prompt`, in-repo instructions through `--prompt-file`, and repo-relative
evidence through `--dataset`. For unchanged owners in branch or commit mode,
use `--source-context` or `--source-context-file` under the
[committed-context rules][review-context]. Missing required context means
incomplete coverage. Do not send secrets or bypass input and isolation checks.

## Run once with fresh outputs

Run from the repository root with Python 3.10+ and PowerShell 7.3+ using
`Windows` or `Standard` native argument passing; `python` must resolve to a
native `.exe`, not a batch shim. Use the selected shared installation (default
`$HOME\.agents\skills\autoreview`) and a fresh output directory outside the
repository.

```powershell
$helper = Join-Path $HOME '.agents\skills\autoreview\scripts\autoreview'
$base = git merge-base HEAD '<observed-target-ref>'
$runDir = Join-Path $HOME ('openclaw-review-runs\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runDir -ErrorAction Stop | Out-Null
python $helper --engine codex --mode local --base $base --max-priority P3 `
    --prompt '<task requirements, preserved invariants, and intended scope>' `
    --output (Join-Path $runDir 'review.txt') `
    --json-output (Join-Path $runDir 'review.json') `
    --status-output (Join-Path $runDir 'status.json')
"Review process exit: $LASTEXITCODE; outputs: $runDir"
```

## Read the final outcome, not just the exit

Report clean only when all of these hold:

1. The final JSON report and status are fresh outputs from this invocation.
2. Status `status` and report `review_status` both say `scoped-clean`,
   `report_produced` is true, and the process exit is 0 and matches status
   `exit_code`. `filtered` is not clean even with exit 0.
3. The run covered the intended candidate, base, P0-P3 severity, and context.
   Record mode, base, HEAD, helper identity, route and model (or unverified),
   output paths, findings, and limits.

Otherwise report the findings or blocker and verify findings before acting.
A clean review covers only the selected scope, not tests, runtime proof, or
merge approval. Later edits do not inherit an earlier review. See the
[results contract][review-results].

## Fallback

If Codex CLI is unavailable, follow the shared skill's
[Copilot subagent route][review-fallback] with the same scope, context, and
P0-P3 requirement. Report a narrative assessment and the actual host and model.
Never switch routes after launch to bypass failures or unfavorable findings.

These references were checked at `1444c91f6b455227993bc780d0933f78ae54f5b4`;
compare your selected installation before use.

[autoreview]: https://github.com/openclaw/agent-skills/blob/1444c91f6b455227993bc780d0933f78ae54f5b4/skills/autoreview/SKILL.md
[review-context]: https://github.com/openclaw/agent-skills/blob/1444c91f6b455227993bc780d0933f78ae54f5b4/skills/autoreview/references/git-and-context.md#committed-source-context
[review-results]: https://github.com/openclaw/agent-skills/blob/1444c91f6b455227993bc780d0933f78ae54f5b4/skills/autoreview/references/diagnostics-and-results.md
[review-fallback]: https://github.com/openclaw/agent-skills/blob/1444c91f6b455227993bc780d0933f78ae54f5b4/skills/autoreview/SKILL.md#copilot-subagent-review
