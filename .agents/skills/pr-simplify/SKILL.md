---
name: pr-simplify
description: "Simplify your own Windows-node change to its smallest complete fix while preserving distinct regressions, platform contracts, and required proof. Use after implementation before PR preparation, after large repairs, or when asked to shrink a change or remove slop."
---

# Simplify a Windows-node change

Use this author-side pass after implementing a change and before preparing a
PR. After a large repair, simplify only the new changes and the owners they
touch. It precedes review and never replaces it. A review-only request produces
findings instead of edits. Inherit the task's scope, worktree ownership,
mutation authority, and runtime restrictions.

The diff is a proposal. Passing tests show that it works, not that every new
owner, abstraction, compatibility path, or test is necessary.

Read the root [repository instructions](../../../AGENTS.md), the nearest scoped
`AGENTS.md`, and only the architecture guides for the changed behavior.

## 1. Re-derive the smallest complete fix

Record the target merge base, current HEAD, staged and unstaged changes, and
relevant untracked files. Read the complete candidate diff, the reported
behavior, retained proof, the production owner, and its existing tests.

Ask:

> Starting from the base, what is the smallest complete fix using the owners
> already present? Would I still introduce this architecture?

State the defect, required invariant, and smallest complete fix in one sentence
each. Trace every changed hunk to that outcome. Remove unrelated cleanup or
move it to a separately authorized change.

Inspect the largest new classes, services, partials, state stores, adapters, DI
registrations, compatibility branches, and test fixtures first. Read their
direct callers and relevant history before treating them as redundant. When a
change moves hundreds of lines, crosses multiple owners, or combines product
behavior with infrastructure, identify the smallest coherent stage that can
land independently and reduce the candidate to that stage.

For files named in the [architecture ledger](../../../docs/ARCHITECTURE.md),
preserve every `authoritative` owner and never reintroduce a `closed`
responsibility. A real ownership transfer updates the ledger and protects the
vacated boundary; moving code to another partial file is not a simplification.

Done when every remaining hunk serves the stated fix and its preserved
invariant.

## 2. Remove unnecessary architecture

Apply each supported simplification in the changed system:

- **Keep one decision owner.** Move repeated caller policy into the existing
  operation that owns the decision. Migrate affected callers and remove the old
  implementation, export, cache, repair branch, and unreachable fallback.
- **Use existing state and seams.** Challenge every new manager, schema, cache,
  DTO, wrapper, and combined representation. Reuse an owner only when its
  authority, lifetime, atomicity, thread affinity, and cleanup match.
- **Delete forwarding machinery.** Remove wrappers that only rename or forward,
  parallel projections of authoritative state, fake lifecycle operations used
  only to obtain a result, and compatibility paths without a shipped contract.
- **Preserve necessary migration.** Do not remove a migration, rollback,
  identity, or cleanup path merely because a fresh profile passes.

Apply these Windows-node checks when their surface is in the diff:

### WinUI state, thread, and lifetime

Follow the [WinUI ownership guide](../../../src/OpenClaw.Tray.WinUI/AGENTS.md).
Views own layout, named-control application, and lifecycle forwarding.
ViewModels and presenters own observable state and pure projection. Services own
IO, persistence, timers, processes, Gateway calls, and hosting. `App` remains
the composition root.

Prefer the existing `IUiDispatcher` seam for presentation work. Do not simplify
away dispatcher rejection, cancellation, generation checks, activation and
deactivation, event unsubscription, or disposal ordering when those guard
window close, navigation replacement, shutdown, or stale async completion.
Do not replace a page applicator with a presenter that merely wraps its named
controls.

### Packaged and unpackaged behavior

Treat MSIX and unpackaged Inno/portable behavior as real product differences.
Before merging branches around package identity, startup, notifications,
updates, migration, or uninstall, inspect `PackageHelper`, `AppIdentity`, the
manifest, `installer.iss`, and the relevant tests. Preserve existing AUMIDs,
package identities, data locations, and migration compatibility.

The optional release updater remains admitted by channel before its backend is
loaded. Packaged (MSIX), `DEV_BUILD`, and Debug builds skip it before
`UpdateBackendLoader.Load()`. Removing that distinction or eagerly constructing
the vendor backend is not simplification.

### Gateway, WSL, pairing, and commands

Use the [connection architecture](../../../docs/CONNECTION_ARCHITECTURE.md) for
connection, credential, pairing, and reconnect changes.
`GatewayConnectionManager` remains the public lifecycle owner. Preserve
device-token precedence for operator and node WebSockets, shared-token
precedence for interactive HTTP surfaces, explicit approval boundaries, active
gateway generation fencing, and transaction rollback.

For an app-owned setup-managed WSL Gateway, keep supervision in the tray's
managed-local owners, not in the connection layer. Never adopt, restart, stop,
or clean an ambiguous localhost process or distro. Preserve operator stop and
disconnect intent, provenance gates, cancellation, rollback, and setup-owned
state. Read the [setup engine](../../../docs/SETUP_ENGINE_REDESIGN.md) and
[WSL administration boundary](../../../docs/WSL_GATEWAY_ADMIN.md) when those
contracts are touched.

Each command has one capability owner and authoritative registration seam.
Shared node capabilities are re-registered synchronously for every fresh
Gateway client, including reconnects, while local MCP reads the same capability
set; `winnode` consumes the local MCP contract. Preserve deliberate transport
exclusions: local-only `app.*` and `app.connection.*` capabilities must not be
widened to the remote Gateway transport, and `browser.proxy` remains
conditionally registered. Do not add a parallel implementation or remove the
registry, MCP description, shipped skill, focused tests, or applicable live
invocation proof required by the
[Windows node contract](../../../docs/WINDOWS_NODE_TESTING.md#new-command-mcp-contract).

### Installer, updater, and architecture

Installer, migration, cleanup, and updater paths preserve user state across
upgrade and migration. Automatic cleanup requires positive ownership. An exact
target may also be removed after explicit destructive confirmation; without
that override, uncertainty about migration, package presence, or ownership
fails closed and preserves state. Keep source checks distinct from signed
installer, Store, and migration runtime proof.

Treat x64 and ARM64 as separate native artifact contracts. Preserve RID-specific
dependencies, installer architecture gates, MSIX inputs, and output names. A
host-x64 pass does not justify deleting ARM64 handling. Use the
[validation inventory](../../../docs/TEST_COVERAGE.md#formal-validation-paths)
and [release matrix](../../../docs/RELEASING.md) for the affected packaging
surface.

### Polish the remaining code

Keep comments only for ownership, lifecycle, ordering, security, compatibility,
or non-obvious constraints. Prefer repository types and helpers over casts,
duplicate parsing, broad catches, arbitrary waits, and success-shaped fallback.
Keep aliases, retries, and shims only when a named shipped contract needs them.

When history leaves a path's purpose unclear, keep it and report the unresolved
decision. Do not turn simplification into a new product decision, breaking
contract, unrelated cleanup, or destructive state migration.

## 3. Keep tests for distinct failures

For each added or changed test group and fixture helper, name the observable
failure it catches. Follow the
[Windows test-audit skill](../test-audit/SKILL.md).

Keep regressions for the reported defect and distinct boundaries such as:

- UI-thread access, navigation replacement, window close, cancellation, restart,
  stale authority, disposal, and rendering;
- packaged versus unpackaged identity, startup, migration, update, and cleanup;
- operator versus node pairing, credential precedence, command discovery,
  local MCP versus Gateway exposure, conditional registration, permissions,
  invocation, and rejection;
- WSL ownership, provenance, rollback, explicit stop intent, and process
  cleanup;
- x64 versus ARM64 native dependencies and packaging output.

Keep source-shape or architecture guards that protect a concrete ledger closure
or shipped package/command contract. A unit test does not replace distinct
WinUI rendering, fixture transport, installer, architecture, or live Gateway
proof.

Remove duplicate same-path cases, mock echoes, incidental field-copy or wording
assertions, tests of obsolete implementations, and scaffolding or production
hooks used only by deleted logic. Consolidate useful cases into the existing
owner suite without weakening assertions, real-boundary coverage, or baselines.

Done when every remaining test group has a named failure and each removed test
is demonstrably covered or obsolete.

## 4. Reprove the reduced change

Batch the simplification edits, inspect the complete diff again, and run the
original reproduction plus the focused checks for every changed contract and
affected consumer. Use the
[validation planner](../../../docs/TEST_COVERAGE.md#explain-the-validation-plan)
and [proof skill](../openclaw-proof-validation/SKILL.md).

Code changes retain the repository's required build, Shared tests, and Tray
tests plus targeted lanes. Documentation-only skill edits use the documentation
and skill validators. Reuse earlier evidence only when its source, inputs,
binary, runtime, and asserted behavior remain unchanged. Check actual execution
counts and selected cases, not only exit code.

Done when superseded paths are unreachable, the reduced behavior has current
proof, and the full candidate diff has an explicit keep, simplify, remove, or
unresolved verdict. No edit is a valid outcome when the existing shape is
justified. Prefer net-negative code, not a deletion quota.

Report:

- the responsibility or machinery removed;
- the behavior and distinct failures still protected;
- production and test deltas against the merge base;
- a reduction against an earlier candidate revision, reported separately from
  the whole-change delta;
- exact validation commands, outcomes, and proof limits;
- necessary remaining growth and unresolved repository decisions.

Then use [Windows PR preparation](../windows-pr-prepare/SKILL.md) if a local PR
body or readiness handoff is requested. Do not commit, push, publish, update
GitHub metadata, or alter a shared runtime without separate explicit authority.

## Source and maintenance

Windows-owned adaptation of the MIT-licensed OpenClaw
[author-side simplification workflow](https://github.com/openclaw/openclaw/blob/dcfafb5c0016e0652771c239987baff8dbd09be0/.agents/skills/pr-simplify/SKILL.md).
Windows retains its architecture ledger, WinUI lifetime rules, package and
installer contracts, Gateway/WSL ownership, command-surface obligations,
multi-architecture release behavior, and mandatory local validation.
