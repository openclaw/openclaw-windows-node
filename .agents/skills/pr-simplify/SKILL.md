---
name: pr-simplify
description: Shrink your own Windows-node change to its smallest complete fix. Use when asked or at code publication, not for drafting or metadata-only PR edits.
---

# Simplify a Windows-node change

Use when asked or at the
[code-publication gate](../../../AGENTS.md#before-creating-or-updating-a-pr).
Reuse simplification already completed by the user or an agent on unchanged
code; for updates, inspect the delta and affected owners rather than restarting
the whole change. It precedes review and never replaces it. A review-only
request produces findings, not edits. Inherit the task's scope, worktree
ownership, mutation authority, and runtime restrictions.

Passing tests show that the diff works, not that every new owner, abstraction,
compatibility path, or test is necessary.

## 1. Re-derive the smallest complete fix

Record the merge base, HEAD, staged, unstaged, and relevant untracked changes.
Read the complete diff, the reported behavior, the production owner, and its
existing tests. Ask: starting from the base, what is the smallest complete fix
using the owners already present? State the defect, the invariant, and that fix
in one sentence each, then trace every hunk to it. Move unrelated cleanup to a
separately authorized change.

Inspect the largest new classes, services, partials, adapters, and fixtures
first, with their callers and history. When a change moves hundreds of lines
or crosses several owners, reduce it to the smallest stage that can land alone.
For files in the [architecture ledger](../../../docs/ARCHITECTURE.md), check
the affected rows: keep `authoritative` owners and never reintroduce a `closed`
responsibility. Moving code to another partial file is not a simplification.

## 2. Remove unnecessary architecture

- **Keep one decision owner.** Move repeated caller policy into the operation
  that owns it, migrate callers, and delete the old path.
- **Use existing state and seams.** Reuse an owner only when its authority,
  lifetime, atomicity, thread affinity, and cleanup match.
- **Delete forwarding machinery** that only renames, forwards, or projects
  authoritative state, and compatibility paths without a shipped contract.
- **Preserve necessary migration.** Do not remove a migration, rollback,
  identity, or cleanup path because a fresh profile passes.

Windows-node checks, when their surface is in the diff:

- **WinUI:** follow the [WinUI instructions](../../../src/OpenClaw.Tray.WinUI/AGENTS.md).
  Do not simplify away dispatcher rejection, cancellation, generation checks,
  event unsubscription, or disposal ordering that guard window close,
  navigation, shutdown, or stale async completion.
- **Packaged and unpackaged:** treat MSIX and unpackaged Inno/portable behavior
  as real product differences. Before merging branches around package
  identity, startup, notifications, updates, migration, or uninstall, inspect
  `PackageHelper`, `AppIdentity`, the manifest, `installer.iss`, and the
  relevant tests. Preserve existing AUMIDs, package identities, data
  locations, and migration compatibility. Keep the updater's channel admission
  as recorded in the [architecture ledger](../../../docs/ARCHITECTURE.md).
- **Gateway and WSL:** follow the
  [connection architecture](../../../docs/CONNECTION_ARCHITECTURE.md) for
  credentials, pairing, and reconnect. Never adopt, restart, stop, or clean an
  ambiguous localhost process or distro.
- **Commands:** keep one capability owner and registration seam. Local-only
  `app.*` and `app.connection.*` capabilities must not be widened to the
  Gateway transport, and `browser.proxy` stays conditionally registered. Keep
  everything the
  [new command contract](../../../docs/WINDOWS_NODE_TESTING.md#new-command-mcp-contract)
  requires.
- **Installer and cleanup:** automatic cleanup requires positive ownership;
  uncertainty fails closed and preserves user state.
- **Architectures:** x64 and ARM64 are separate artifact contracts; a host-x64
  pass does not justify deleting ARM64 handling.

When history leaves a path's purpose unclear, keep it and report the
unresolved decision. Do not turn simplification into a new product decision,
breaking contract, or destructive state migration.

## 3. Keep tests for distinct failures

Name the observable failure each added or changed test group catches, and
follow [test-audit](../test-audit/SKILL.md). Remove duplicate same-path cases,
mock echoes, tests of obsolete implementations, and hooks used only by deleted
logic.

## 4. Reprove the reduced change

If simplification changed code, rerun the original repro and only affected
checks, per the [validation policy](../../../docs/TEST_COVERAGE.md#validation-policy).
Do not repeat a check here if the final core lane will cover it; run that lane
once on the final code as the last
[pre-PR check](../../../AGENTS.md#before-creating-or-updating-a-pr).
Reuse earlier evidence only when its source, binary, runtime, and asserted
behavior are unchanged. No edit is a valid outcome when the existing shape is
justified.

Report:

- what was removed and which distinct failures remain protected;
- exact validation commands, outcomes, and unresolved decisions.

Then use [Windows PR preparation](../windows-pr-prepare/SKILL.md) if a PR body
or readiness handoff is requested. Do not commit, push, publish, update GitHub
metadata, or alter a shared runtime without separate explicit authority.

Adapted from the upstream
[simplification workflow](https://github.com/openclaw/openclaw/blob/dcfafb5c0016e0652771c239987baff8dbd09be0/.agents/skills/pr-simplify/SKILL.md)
(MIT).
