---
name: test-audit
description: Write or review Windows regression tests for observable contracts, independent expectations, faithful fixtures, and distinct coverage without weakening native proof or architecture guards.
---

# Windows test audit

Use when adding, changing, or reviewing tests. A review does not authorize
test deletion or production refactoring.

## Before adding a test

Answer these questions:

1. What observable behavior or independent contract does it protect?
2. What credible regression makes it fail, and does it fail for that reason
   rather than a broken fixture?
3. Which production owner is exercised? Why does existing coverage miss this
   risk?
4. Are the expected values independent of the implementation under test?
5. Does another layer add a distinct transport, persistence, lifecycle,
   permission, or rendering risk, rather than repeat the same assertion?

Prefer an existing table, fixture, or owner regression over another process
boot. Avoid mocks supplying the behavior being asserted, production helpers
computing their own expected values, and test-only exports without a genuine
production boundary.

Read [regression authoring](../../../docs/TEST_COVERAGE.md#writing-a-regression-test)
and use the existing [fixture guide](../../../docs/GATEWAY_FIXTURE_TESTING.md)
when the behavior crosses the application boundary.

## Preserve valuable contracts

- Keep independent protocol, CLI, storage, migration, defaults, packaging,
  security, architecture, and user-visible ordering contracts.
- Static does not mean disposable. WinUI source-shape guards are appropriate
  for a concrete prohibited pattern with the
  [ledger's retirement condition](../../../docs/ARCHITECTURE.md#how-to-use-this-document).
  The shipped `winnode` command-to-registry contract must remain covered.
- Local MCP output does not prove Gateway pairing. A state snapshot is not a
  rendered UI assertion. Synthetic Gateway proof is not live provider, MXC,
  installer, or production credential-resolution proof.
- Use isolated settings, loopback listeners, owned processes, and existing
  temporary-directory/environment helpers. Do not introduce arbitrary sleeps
  or parallel native input tests on a shared desktop.
- Do not make a failure green through weaker assertions, hidden skips,
  blind retries, broadened mocks, or an unrelated baseline update.

## Review and validation

For a bug fix, demonstrate the intended pre-fix failure and post-fix pass, or
report why baseline reproduction was unavailable. For a refactor, preserve
the contract without pinning incidental implementation names.

Run focused owner tests, then the
[required validation](../../../AGENTS.md#required-validation-after-every-change).
Respect explicit skip policies: an allowed ordinary CI skip is not complete
custom proof. Measure cost when performance is the claim; do not import
JavaScript timing budgets or worker rules into native Windows suites.

Report protected behavior, fixture limits, exact commands and execution counts,
and remaining proof gaps. Do not claim tests ran from an exit code alone.

## Source and maintenance

Windows-owned adaptation of concepts from the MIT-licensed upstream
[test-audit workflow](https://github.com/openclaw/openclaw/blob/690c46084d507ce38502d401ef5d0d6a2fb9733a/.agents/skills/test-audit/SKILL.md)
and [test-authoring guide](https://github.com/openclaw/openclaw/blob/690c46084d507ce38502d401ef5d0d6a2fb9733a/docs/help/testing/writing-tests.md).
Windows retains its mandatory local closeout, native proof, and legitimate
source guards. This file does not inherit upstream publication permissions.
