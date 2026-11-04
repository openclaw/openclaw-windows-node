---
name: autoreview
description: Structured code review through the shared OpenClaw agent-skills installation.
---

# Auto Review

Use the canonical [autoreview skill](https://github.com/openclaw/agent-skills/tree/main/skills/autoreview).
Read the complete installed `~/.agents/skills/autoreview/SKILL.md` and the references
it requires before reviewing. If installed at another operator-selected location,
use that installation. This repository entrypoint contains no review implementation.

If the skill is missing, report it. Installation requires separate authorization:
reuse or clone `https://github.com/openclaw/agent-skills` and run
`python3 scripts/install-skills autoreview` from that checkout. On Windows, use
`python`; `--mode copy` is available when symlinks are unavailable.

Review is required only as described in the
[review policy](../../../docs/AGENT_WORKFLOWS.md#when-review-is-required).
Prefer the shared `scripts/autoreview` helper with Python from the repository being
reviewed, preserving its working directory. Follow the
[Windows invocation and result-reading recipe](references/windows.md) before
launch. Always use `--max-priority P3`, select mode/base explicitly, and supply
required context. Exit zero alone is not a clean P0-P3 assessment.

If Codex CLI is unavailable, use the canonical skill's Copilot subagent fallback
with the same scope and severity, and report its actual host and model. Never
use fallback to bypass failures or findings.

Keep shared fixes and instructions upstream in `openclaw/agent-skills`. Update
the shared checkout once for all symlinked consumers; reinstall copy-mode skills
after updating their source. Do not add repository-local helpers or test copies.
