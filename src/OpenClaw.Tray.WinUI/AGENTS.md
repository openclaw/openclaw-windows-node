# WinUI ownership and proof

The root [AGENTS.md](../../AGENTS.md) owns universal policy. Read this guide
when changing a WinUI owner or a caller that changes its lifecycle or contract.

## Find the production owner

Before editing a file listed in the
[architecture ledger](../../docs/ARCHITECTURE.md), read its rows and preserve
every closed responsibility. App remains the composition root; pages apply
WinUI state. Use the existing service, policy, projection, or view model for
behavior instead of adding parallel state or forwarding wrappers.

The root [architecture table](../../AGENTS.md#architecture-context-for-new-agents)
routes connection, MCP, node, onboarding, setup, and WSL work. WinUI-specific
guides:

| Change | Read before editing |
|---|---|
| Chat rendering or interaction | [Chat visual design](../../docs/CHAT_VISUAL_DESIGN.md) and the affected ledger rows |
| Accessibility or user-visible copy | [Accessibility](../../docs/ACCESSIBILITY.md) and [localization](../../docs/LOCALIZATION.md) |
| Exported diagnostics or instrumentation | [Telemetry guardrails](../../docs/TELEMETRY.md) |

Trace callers across directories when the changed contract crosses an owner.
Do not read every linked guide for an unrelated visual or documentation task.
Record ownership transfers in the ledger.

## Large refactors

`App.xaml.cs` and `Pages\ConnectionPage.xaml.cs` are active god-file reduction
targets. When touching either file:

- Complete a real ownership transfer. A new partial file is not progress unless
  it introduces a narrower owner, pure projection, policy, service, or tested
  seam.
- Keep `App` as the composition root. Delegate cohesive behavior to focused
  services, but do not move startup ordering into another god object.
- Keep `ConnectionPage.xaml.cs` as the WinUI applicator until a pure row, plan,
  or workflow seam exists. Do not move named-control setters into a presenter
  that just wraps the page.
- Add characterization tests before moving startup, credential, pairing,
  node/MCP, tray action, or direct-connect rollback behavior. Source-text
  contract tests are acceptable for WinUI-only seams; prefer pure unit tests
  for policies and projections.
- Keep one seam per PR with a clear invariant protected by tests. Stop and
  re-plan if a PR moves hundreds of lines without behavior coverage.
- When you extract a responsibility, update the ledger in the same PR and add a
  guard test for high-regression closures. In the PR description, name the old
  owner, new owner, preserved invariant, and validation run.

## Characterize and prove the behavior

Protect the reported failure and relevant lifecycle before moving code. Prefer
pure tests for projections and policies, faithful fixtures for transport, and
current-build visible evidence for rendering. Concrete WinUI-only prohibited
patterns may use source-shape guards with a retirement condition.

Use the [proof skill](../../.agents/skills/openclaw-proof-validation/SKILL.md)
and [test inventory](../../docs/TEST_COVERAGE.md).
The [Gateway fixture](../../docs/GATEWAY_FIXTURE_TESTING.md) proves an isolated
application boundary, not real Gateway pairing, provider behavior, or MXC.
A passing MCP snapshot does not establish that the mounted UI rendered it.
Follow the root [runtime safety rules](../../AGENTS.md#runtime-and-test-safety).
