### 2026-05-04T20:09:07-07:00: User directive — prototype is a reference for bug fixes

**By:** Mike Harsh (via Squad coordinator)
**What:** When fixing bugs in the clean worktree, agents should consult the prototype worktree at `C:\Users\mharsh\OneDrive - Microsoft\Desktop\OpenClawWindowsInstaller\openclaw-windows-node` (branch `pr-241-feedback-fixes`) as a reference. The prototype code went through real end-to-end validation, so when the clean port has a regression that the prototype didn't have, the prototype is the authoritative answer for what the working behavior looked like.
**Why:** Reduces wasted root-cause investigation time; the prototype already solved many of these problems empirically.

**Applies to all future spawns** that involve diagnosing or fixing behavior the prototype demonstrably worked. Prompts should explicitly include the prototype paths the agent should compare against. Spawn prompt template addition: under "INPUT ARTIFACTS", include relevant prototype source paths from `.squad/prototype-reference.md` whenever the bug touches a phase or surface the prototype validated.
