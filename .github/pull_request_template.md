<!--
Optional linked context:
Add a visible `Closes #<issue-number>` or `Related: #<issue-number>` line
below this comment.

Required PR title:
type: user-facing description
Use a parenthesized scope only when it adds clarity:
fix(auth): login redirect loops when session cookie is expired

Types: feat, fix, improve, refactor, docs, chore.
For fixes, describe the user-visible symptom and trigger:
fix: task list fails to load when user has no environments
Avoid implementation details such as:
fix: add null check to task query
-->

<details>
<summary>Additional instructions</summary>

**MUST:** Keep **Allow edits from maintainers** enabled for this PR so maintainers
can help update the branch when needed.

</details>

## What Problem This Solves

<!--
Describe the concrete user, product, or operational problem.
For fixes, begin with:
"Fixes an issue where users <do X> would <experience Y> when <condition>."
or:
"Resolves a problem where..."

Name the affected UI surface or workflow. Do not describe the code-level cause here.
-->

## Why This Change Was Made

<!--
In one or two sentences, explain the complete shipped solution, key design
decisions, and relevant boundaries or non-goals. Include implementation detail
only when it helps reviewers understand user-visible behavior or risk.
Avoid file-by-file narration.
-->

## User Impact

<!--
State what users, operators, or developers can now do or expect. Lead with the
concrete benefit and use user-facing language. If there is no user-visible
impact, say so plainly.
-->

## Evidence

<!--
Show the most useful proof that this change works. Screenshots, screencasts,
terminal output, focused tests, CI results, live observations, redacted logs,
and artifact links are all useful. Include before/after evidence for visual
changes when it clarifies the result.

Reviewers will inspect the code, tests, and CI. Use this section to make the
validation easy to understand, not to restate the diff.
-->

## Change Type

- [ ] Bug fix
- [ ] Feature
- [ ] Refactor
- [ ] Docs or instructions
- [ ] Tests or validation
- [ ] Security hardening
- [ ] Chore or infrastructure

## Scope

- [ ] Tray or WinUI UX
- [ ] Windows node capability
- [ ] Local MCP or `winnode`
- [ ] Gateway, connection, or pairing
- [ ] Setup or onboarding
- [ ] Permissions, privacy, or security
- [ ] Tests, CI, or docs

## Validation

<!--
Include exact commands and pass/fail counts. Baseline after code changes:
- `./build.ps1`
- `dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore`
- `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore`

Add focused tests when relevant. In fresh worktrees, confirm tests actually ran
and report non-zero test counts.
-->

## Real Behavior Proof

<!--
Paste at least one current-head proof item that directly demonstrates the
changed behavior. Use copied live output, screenshots or video, UI diagnostics,
`winnode` output, raw MCP JSON-RPC, gateway invoke output, redacted runtime
logs, or linked artifacts.
-->

- Environment tested:
- PR head or commit tested:
- Exact steps or command run:
- Evidence after fix:
- Observed result:
- Screenshot or artifact links verified? (`Yes`/`No`/`N/A`)
- Not verified or blocked:

## Security Impact

- New permissions or capabilities? (`Yes`/`No`)
- Secrets or tokens handling changed? (`Yes`/`No`)
- New or changed network calls? (`Yes`/`No`)
- Command or tool execution surface changed? (`Yes`/`No`)
- Data access scope changed? (`Yes`/`No`)
- If any answer is `Yes`, explain the risk and mitigation:

## Compatibility and Migration

- Backward compatible? (`Yes`/`No`)
- Config or environment changes? (`Yes`/`No`)
- Migration needed? (`Yes`/`No`)
- If yes, list the exact upgrade steps:

## Review Conversations

- [ ] I replied to or resolved every bot review conversation addressed by this PR.
- [ ] I left unresolved only conversations that still need maintainer judgment.
