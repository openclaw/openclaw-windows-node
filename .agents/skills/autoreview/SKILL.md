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

Prefer the shared `scripts/autoreview` helper with Python from the repository being
reviewed, preserving its working directory. Follow the
[Windows invocation and result-reading recipe](../../../docs/AGENT_WORKFLOWS.md#run-a-scoped-shared-review)
before launch. Always use `--max-priority P3`, select mode/base explicitly, and
supply required context. Check fresh final report/status, process outcome, and
coverage together; exit zero alone is not a clean P0-P3 assessment.

If Codex CLI is unavailable, follow the canonical skill's independent Copilot
subagent fallback with the same scope, context, and P0-P3 severity. Report its
actual host/model (or unverified identity), coverage, and limitations as a
narrative assessment, not helper status or isolation guarantees. Never use
fallback to bypass failures or findings. Follow all canonical and repository
review requirements.

Keep shared fixes and instructions upstream in `openclaw/agent-skills`. Update
the shared checkout once for all symlinked consumers; reinstall copy-mode skills
after updating their source. Do not add repository-local helpers or test copies.
