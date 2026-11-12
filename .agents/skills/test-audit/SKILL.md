---
name: test-audit
description: Write or review Windows regression tests for observable contracts, independent expectations, faithful fixtures, and distinct coverage without weakening native proof or architecture guards.
---

# Windows test audit

Use when adding, changing, or reviewing tests. A review does not authorize
test deletion or production refactoring.

## Authoring gate

Answer these questions:

1. What observable behavior or independent contract does it protect?
2. What credible regression makes it fail, and does it fail for that reason
   rather than a broken fixture?
3. Which production owner is exercised? Why does existing coverage miss this
   risk?
4. Are the expected values independent of the implementation under test?
5. Does another layer add a distinct transport, persistence, lifecycle,
   permission, or rendering risk, rather than repeat the same assertion?
6. Is this the cheapest faithful boundary, or does it add a process, desktop,
   WSL, installer, Gateway, or native capability boot that a smaller owner test
   can cover?

Prefer an existing table, fixture, or owner regression over another process
boot. Avoid mocks supplying the behavior being asserted, production helpers
computing their own expected values, and test-only exports without a genuine
production boundary.

Read [regression authoring](../../../docs/TEST_COVERAGE.md#writing-a-regression-test)
and use the existing [fixture guide](../../../docs/GATEWAY_FIXTURE_TESTING.md)
when the behavior crosses the application boundary.

## Choose the production owner

Keep one primary regression at the strongest faithful owner boundary:

| Boundary | Primary project or lane |
| --- | --- |
| Gateway registry, credentials, connection lifecycle, migration admission/records and persisted Local AI runtime state | `OpenClaw.Connection.Tests` |
| Protocol, MCP, capabilities, command policy and shared models | `OpenClaw.Shared.Tests` |
| Tray policy, settings, onboarding helpers, installer source contracts and update checks/backends | `OpenClaw.Tray.Tests` |
| Setup engine, WSL installation and Local AI setup/recovery policy | `OpenClaw.SetupEngine.Tests` |
| Shipped `winnode` arguments, JSON output and command documentation | `OpenClaw.WinNode.Cli.Tests` |
| Functional UI element graphs, factories and hook state without mounted native controls | `OpenClawTray.FunctionalUI.Tests` |
| Mounted native WinUI controls, UI Automation, accessibility and focused input | `OpenClaw.Tray.UITests` |
| Clean install, upgrade, uninstall and Store migration runtime behavior | The applicable formal validation path or proof pool |
| Real process, Gateway-mediated setup/connect or MXC behavior | Integration, E2E or named proof lane |

Add a second layer only when it proves a different failure mode. Local MCP
output does not prove Gateway pairing. A state snapshot is not a rendered UI
assertion. Synthetic Gateway proof is not live provider, MXC, installer, Store,
or production credential-resolution proof.

## Reject low-value proof

Reject or rewrite tests that rely on:

- self-comparisons, copied inventories, subject-generated expected values, or
  the same helper on both sides of an assertion;
- implementation names, private call shape, or exact delegation counts unless
  that ownership/delegation boundary is itself the protected contract;
- a mock or fake that supplies the result, ordering, persistence receipt, or
  policy decision being asserted;
- another test at every crossed layer without a distinct transport,
  persistence, permission, lifecycle, packaging, or rendering risk;
- production flags or hooks that bypass owner logic, or exports/visibility
  added only to expose internal behavior without a genuine production boundary;
- a broad source grep that pins incidental identifiers instead of a concrete
  prohibited pattern or independent shipped inventory.

Negative controls must fail at the intended guard, not during fixture setup.
Test names must describe the behavior actually exercised. If a production seam
exists only for obsolete tests and has no production-boundary or architecture
role, remove it with the tests when the task authorizes production changes;
otherwise report it rather than expanding it. Do not reject a narrow adapter
over process, WSL, OS, network, clock, or filesystem boundaries merely because
tests supply its only alternate implementation. Existing test assembly
visibility and ledger-recorded seams remain governed by their real contracts
and retirement conditions.

## Preserve valuable contracts

- Keep independent protocol, CLI, storage, migration, defaults, packaging,
  update, security, architecture, generated-resource, and user-visible
  ordering contracts.
- Static does not mean disposable. Source-shape guards are appropriate for a
  concrete prohibited pattern with the
  [ledger's retirement condition](../../../docs/ARCHITECTURE.md#how-to-use-this-document).
  Installer source assertions remain distinct from clean install, upgrade, and
  uninstall proof. The shipped `winnode` command-to-registry contract must
  remain covered.
- Use isolated settings, loopback listeners, owned processes, and existing
  `TempDirectory`, `EnvironmentScope`, `CliHarness`, and Gateway fixture
  helpers. Fakes may provide external input or record calls, but must not
  implement the behavior under test.
- Do not introduce arbitrary sleeps, blind polling, redundant process boots,
  or parallel native input tests on a shared desktop. Reuse suite-level
  fixtures when they preserve the same contract and isolation. Use a fresh
  process when startup, process-global state, ownership/termination, or an
  independent profile is the boundary under test.
- Do not make a failure green through weaker assertions, hidden skips,
  blind retries, broadened mocks, or an unrelated baseline update.

## Audit workflow

Audit coherent owner batches, not deletion counts. Before deleting or
consolidating proof:

1. Read the full test, production owner, callers, fixture and relevant history.
2. Identify the observable contract and every distinct remaining proof layer.
3. Confirm the candidate is duplicate, implementation-coupled, obsolete, or
   unable to fail for the claimed regression.
4. Preserve independent protocol, persistence, packaging, security,
   architecture, rendering and native-runtime contracts.
5. Remove obsolete test-only production seams in the same authorized change.

Baseline failures may identify product bugs. Do not delete or weaken the test
until the failure is classified.

## Cost and validation

For a bug fix, demonstrate the intended pre-fix failure and post-fix pass, or
report why baseline reproduction was unavailable. For a refactor, preserve
the contract without pinning incidental implementation names.

Run focused owner tests, then the
[required validation](../../../AGENTS.md#required-validation-after-every-change).
Respect explicit skip policies: an allowed ordinary CI skip is not complete
custom proof. A seconds-long test must protect a boundary that a cheaper layer
cannot. Measure wall time when cost or performance is the claim, but do not
import JavaScript timing budgets, worker counts, or release-only rules into
native Windows suites.

Report protected behavior, why the owner/layer is necessary, fixture limits,
exact commands, execution counts, measured cost when relevant, and remaining
proof gaps. Do not claim tests ran from an exit code alone.

## Source and maintenance

Windows-owned adaptation of concepts from the MIT-licensed upstream
[test-audit workflow](https://github.com/openclaw/openclaw/blob/690c46084d507ce38502d401ef5d0d6a2fb9733a/.agents/skills/test-audit/SKILL.md)
and [test-authoring guide](https://github.com/openclaw/openclaw/blob/690c46084d507ce38502d401ef5d0d6a2fb9733a/docs/help/testing/writing-tests.md).
Windows retains its mandatory local closeout, native proof, and legitimate
source guards. This file does not inherit upstream publication permissions.
