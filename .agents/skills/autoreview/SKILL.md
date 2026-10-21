---
name: autoreview
description: Structured code review through the shared OpenClaw agent-skills installation.
---

# Auto Review

Use the canonical [autoreview skill](https://github.com/openclaw/agent-skills/tree/main/skills/autoreview).
Read the complete installed `~/.agents/skills/autoreview/SKILL.md` and the references
it requires before reviewing. If installed at another operator-selected location,
use that installation. This repository entrypoint contains no review implementation.

If the skill is missing, reuse or clone `https://github.com/openclaw/agent-skills`
and run `python3 scripts/install-skills autoreview` from that checkout. On Windows,
use `python`; `--mode copy` is available when symlinks are unavailable.

Prefer the shared `scripts/autoreview` helper with Python from the repository being
reviewed, preserving its working directory. If Codex CLI is unavailable, follow
the canonical skill's independent Copilot subagent fallback: prefer an accessible
Codex model, or use a comparable available model. Model access through Copilot
does not require a separate Codex CLI installation.

Keep this repository's P0-P3 review scope (`--max-priority P3` for helper runs).
For a subagent review, explicitly supply the same severity scope and report the
actual host/model and limitations. Do not claim helper isolation or validated
machine status for a subagent review, or use fallback to bypass safety or
isolation failures. Follow all other canonical and repository review requirements.

Keep shared fixes and instructions upstream in `openclaw/agent-skills`. Update
the shared checkout once for all symlinked consumers; reinstall copy-mode skills
after updating their source. Do not add repository-local helpers or test copies.
