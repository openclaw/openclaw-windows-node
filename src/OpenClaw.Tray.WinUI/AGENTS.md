# WinUI ownership and proof

The root [AGENTS.md](../../AGENTS.md) owns universal policy. Read this guide
when changing a WinUI owner or a caller that changes its lifecycle or contract.

## Find the production owner

Before editing a file listed in the
[architecture ledger](../../docs/ARCHITECTURE.md), read its rows and preserve
every closed responsibility. App remains the composition root; pages apply
WinUI state. Use the existing service, policy, projection, or view model for
behavior instead of adding parallel state or forwarding wrappers.

| Change | Read before editing |
|---|---|
| Gateway connection, credentials, pairing, or reconnect | [Connection architecture](../../docs/CONNECTION_ARCHITECTURE.md) |
| First-run setup or hosted Gateway workflow | [Onboarding](../../docs/ONBOARDING_WIZARD.md) and [setup engine](../../docs/SETUP_ENGINE_REDESIGN.md) |
| Node capability or local MCP exposure | [MCP mode](../../docs/MCP_MODE.md) and [Windows node testing](../../docs/WINDOWS_NODE_TESTING.md) |
| Chat rendering or interaction | [Chat visual design](../../docs/CHAT_VISUAL_DESIGN.md) and the affected ledger rows |
| Accessibility or user-visible copy | [Accessibility](../../docs/ACCESSIBILITY.md) and [localization](../../docs/LOCALIZATION.md) |
| Exported diagnostics or instrumentation | [Telemetry guardrails](../../docs/TELEMETRY.md) |
| Multi-line WSL execution | [WSL argv pitfall](../../docs/WSL_EXE_ARGV_PITFALL.md) |

Trace callers across directories when the changed contract crosses an owner.
Do not read every linked guide for an unrelated visual or documentation task.
Record actual ownership transfers in the existing ledger, not in this file.

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

Do not start a shared app, change real settings, or interact with another
session's desktop/processes to obtain proof. Use a qualified, authorized
isolated run, or report the specific blocked assertion.
