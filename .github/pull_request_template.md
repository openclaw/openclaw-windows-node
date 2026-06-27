## Summary

<!-- Describe the problem and fix in 2-5 bullets. -->

- Problem:
- Why it matters:
- What changed:
- User impact:
- What did NOT change (scope boundary):

## Change Type (select all)

<!-- Select all that apply. -->

- [ ] Bug fix
- [ ] Feature
- [ ] Refactor
- [ ] Docs / instructions
- [ ] Tests / validation
- [ ] Security hardening
- [ ] Chore / infra

## Scope (select all touched areas)

- [ ] Tray / WinUI UX
- [ ] Windows node capability
- [ ] Local MCP / `winnode`
- [ ] Gateway / connection / pairing
- [ ] Setup / onboarding
- [ ] Permissions / privacy / security
- [ ] Tests / CI / docs

## Linked Issue/PR

- Closes #
- Related #
- [ ] Related to a bug or regression

## Validation

<!-- Include exact commands and pass/fail counts. Baseline after code changes:
- .\build.ps1
- dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
- dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
Add focused tests when relevant, for example WinNode CLI tests for command/MCP docs changes.
In fresh worktrees, make sure tests actually ran and report non-zero test counts.
-->

## Real behavior proof

<!-- Paste at least one current-head after-change proof item that directly demonstrates the changed behavior.
Use whatever proof best matches the change: copied live output, screenshot/video,
developer-provided screenshot, copied UI diagnostics, winnode output, raw MCP
JSON-RPC tools/list + tools/call, gateway invoke output, redacted runtime log,
or linked artifact. For UI changes, prefer screenshots/video of the active
changed state, not only adjacent or empty UI. -->

- Environment tested:
- PR head / commit tested:
- Exact steps or command run:
- Evidence after fix:
- Observed result:
- Screenshot/artifact links verified? (`Yes/No/N/A`)
- Not verified / blocked:

<!-- Optional: add rubber-duck review notes in addition to runtime proof above. -->

## Security Impact (required)

- New permissions/capabilities? (`Yes/No`)
- Secrets/tokens handling changed? (`Yes/No`)
- New/changed network calls? (`Yes/No`)
- Command/tool execution surface changed? (`Yes/No`)
- Data access scope changed? (`Yes/No`)
- If any `Yes`, explain risk + mitigation:

## Compatibility / Migration

- Backward compatible? (`Yes/No`)
- Config/env changes? (`Yes/No`)
- Migration needed? (`Yes/No`)
- If yes, exact upgrade steps:

## Review Conversations

- [ ] I replied to or resolved every bot review conversation I addressed in this PR.
- [ ] I left unresolved only conversations that still need reviewer or maintainer judgment.

If a bot review conversation is addressed by this PR, resolve that conversation yourself. Do not leave bot review conversation cleanup for maintainers.
