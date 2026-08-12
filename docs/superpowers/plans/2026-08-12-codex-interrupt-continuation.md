# Codex Interrupt and Continuation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an authorized owner send a bounded text instruction to a linked Codex task, interrupting an exact OpenClaw-owned active turn before starting one replacement turn or durably queueing the instruction when another runtime owns the turn.

**Architecture:** The native OpenClaw Codex plugin owns all writable App Server state and reuses its persistent binding, approval, and lifecycle machinery. Workboard stores a private ordered instruction queue and canonical Codex binding references through supported APIs. The Windows Companion remains a read-only catalog bridge and receives regression tests only.

**Tech Stack:** TypeScript, OpenClaw Codex and Workboard extensions, App Server JSON-RPC, SQLite migrations through Workboard, Vitest, .NET/xUnit regression verification for the Companion.

## Global Constraints

- Do not implement `turn/steer`; the user operation is interrupt-then-new-turn or idle continuation.
- Preserve exactly the Companion's approved read-only catalog surface, except for the separately specified historical read command from the reconciliation plan.
- Never control a turn actively owned by Codex Desktop or another runner through a competing App Server process.
- Only the owner Telegram identity or an authenticated Control UI operator with `operator.admin` may mutate Codex execution.
- Every interrupt targets the exact captured thread ID and turn ID and revalidates binding and authorization generations.
- Never fall back from interrupt failure or ambiguous transport state to starting another turn.
- Accept bounded non-empty text only; no files, images, audio, skills, mentions, arbitrary metadata, caller-selected runtime configuration, or generic protocol passthrough.
- Instruction and transcript bodies never appear in logs, telemetry, audit summaries, Workboard events, or errors.
- A Control UI instruction creates no routine Telegram alert; a Telegram instruction replies only through its originating Telegram route.
- All persistent project artifacts remain on `E:` and Telegram Desktop remains untouched.

---

### Task 1: Add private Codex binding and queued-instruction persistence to Workboard

**Repository:** `E:\OpenClaw\worktrees\openclaw-codex-session-access`

**Files:**
- Modify: `packages/workboard-contract/src/index.ts`
- Modify: `extensions/workboard/src/sqlite-store.ts`
- Modify: `extensions/workboard/src/store-inputs.ts`
- Create: `extensions/workboard/src/codex-instructions.ts`
- Create: `extensions/workboard/src/codex-instructions.test.ts`
- Modify: `extensions/workboard/src/gateway.ts`
- Modify: `extensions/workboard/src/gateway.test.ts`

**Interfaces:**
- Adds private persistence records `WorkboardCodexBinding` and `WorkboardCodexInstruction` without projecting instruction text into ordinary card/event/list results.
- Produces ordered operations `enqueue`, `peek`, `claim`, `consume`, `releaseIndeterminate`, and `listBinding` with per-card/per-thread serialization.
- Each instruction contains UUID, card ID, thread UUID, private text payload, actor identity, origin route descriptor, timestamp, idempotency key, state, and consumption reference. At-rest protection follows the existing Gateway state-directory permission and backup policy; the body is never projected through ordinary Workboard APIs.

- [ ] **Step 1: Write migration, ordering, redaction, and restart RED tests**

Cover schema upgrade from the current version, duplicate idempotency, FIFO order, compare-and-set claim, crash/reopen persistence, body absence from card/events/log projections, and concurrent consumers.

- [ ] **Step 2: Run RED**

```powershell
cd E:\OpenClaw\worktrees\openclaw-codex-session-access
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/workboard/src/codex-instructions.test.ts extensions/workboard/src/gateway.test.ts extensions/workboard/src/sqlite-store-policy.test.ts
```

- [ ] **Step 3: Implement the private queue and scoped Gateway methods**

Register read/write methods under `operator.admin`; public responses expose instruction ID/state/timestamps but never text. Bindings use canonical `codex://thread/<uuid>` identity and do not repurpose `taskId`, `sessionKey`, or `runId`.

- [ ] **Step 4: Run GREEN and type checks**

```powershell
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/workboard/src/codex-instructions.test.ts extensions/workboard/src/gateway.test.ts extensions/workboard/src/sqlite-store-policy.test.ts
pnpm tsgo:extensions
```

- [ ] **Step 5: Commit**

```powershell
git add packages/workboard-contract/src/index.ts extensions/workboard/src
git commit -m "feat(workboard): persist codex task instructions"
```

### Task 2: Build the native owned-turn controller

**Repository:** `E:\OpenClaw\worktrees\openclaw-codex-session-access`

**Files:**
- Create: `extensions/codex/src/task-control.ts`
- Create: `extensions/codex/src/task-control.test.ts`
- Modify: `extensions/codex/src/app-server/attempt-client-cleanup.ts`
- Modify: `extensions/codex/src/app-server/attempt-client-cleanup.test.ts`
- Modify: `extensions/codex/src/app-server/run-attempt-turn-request.ts`
- Modify: `extensions/codex/src/app-server/run-attempt.steering.test.ts`
- Modify: `extensions/codex/src/app-server/session-binding.ts`
- Modify: `extensions/codex/src/app-server/session-binding.test.ts`

**Interfaces:**
- Produces `CodexTaskController.sendInstruction({ cardId, instructionId, actor, origin }, signal)`.
- Produces explicit outcomes `started`, `queued_external_owner`, `indeterminate_interrupt`, `authorization_revoked`, and `binding_changed`.
- Replaces public use of best-effort interruption with an authoritative exact-turn interrupt operation that distinguishes terminal interruption from timeout/indeterminate transport.

- [ ] **Step 1: Write exact-order RED tests**

Prove the controller holds `bindingStore.withLease`, captures thread/turn/binding/auth generations, sends one exact interrupt, waits for terminal `interrupted`, revalidates, then sends one `turn/start`. Prove changed IDs, revocation, timeout, disconnect, and ambiguous responses never start a turn.

- [ ] **Step 2: Run RED**

```powershell
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/codex/src/task-control.test.ts extensions/codex/src/app-server/attempt-client-cleanup.test.ts extensions/codex/src/app-server/run-attempt.steering.test.ts extensions/codex/src/app-server/session-binding.test.ts
```

- [ ] **Step 3: Implement the minimum controller**

Reuse the persistent App Server client, approval bridge, lifecycle controller, and turn-start path already owned by `run-attempt`. Do not launch a second App Server and do not call `codex.cli.session.resume` or `codex.terminal.resume.v1`.

- [ ] **Step 4: Run GREEN**

Run the Step 2 command. Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add extensions/codex/src/task-control.ts extensions/codex/src/task-control.test.ts extensions/codex/src/app-server
git commit -m "feat(codex): interrupt owned turns before continuation"
```

### Task 3: Support idle adoption through the native App Server runtime

**Repository:** `E:\OpenClaw\worktrees\openclaw-codex-session-access`

**Files:**
- Create: `extensions/codex/src/task-adoption.ts`
- Create: `extensions/codex/src/task-adoption.test.ts`
- Modify: `extensions/codex/src/task-control.ts`
- Modify: `extensions/codex/src/task-control.test.ts`
- Modify: `extensions/codex/src/session-catalog-node-continue.ts`
- Modify: `extensions/codex/src/session-catalog.test.ts`

**Interfaces:**
- `adoptIdleCodexTask` verifies current catalog status, external ownership, canonical project root, and admin authorization before establishing an OpenClaw-native persistent App Server binding.
- Node CLI resume and terminal resume remain excluded from bounded task control.

- [ ] **Step 1: Write idle/external-owner RED tests**

Cover idle adoption/start, stale/notLoaded revalidation, active external owner queue-only behavior, concurrent adoption deduplication, project-root mismatch, approval roundtrip, and restart-visible binding.

- [ ] **Step 2: Run RED**

```powershell
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/codex/src/task-adoption.test.ts extensions/codex/src/task-control.test.ts extensions/codex/src/session-catalog.test.ts
```

- [ ] **Step 3: Implement native adoption**

Create the binding through the same App Server runtime used by normal OpenClaw-native attempts. If the installed platform cannot establish a supported persistent binding for the source thread, return `queued_external_owner` and leave the instruction durable; never fall back to CLI resume.

- [ ] **Step 4: Run GREEN**

Run the Step 2 command. Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add extensions/codex/src/task-adoption.ts extensions/codex/src/task-adoption.test.ts extensions/codex/src/task-control.ts extensions/codex/src/task-control.test.ts extensions/codex/src/session-catalog-node-continue.ts extensions/codex/src/session-catalog.test.ts
git commit -m "feat(codex): adopt idle linked tasks safely"
```

### Task 4: Expose one authorized task-level send operation

**Repository:** `E:\OpenClaw\worktrees\openclaw-codex-session-access`

**Files:**
- Create: `extensions/codex/src/task-control-gateway.ts`
- Create: `extensions/codex/src/task-control-gateway.test.ts`
- Modify: `extensions/codex/index.ts`
- Modify: `extensions/codex/src/command-authorization.ts`
- Modify: `extensions/codex/src/command-authorization.test.ts`

**Interfaces:**
- Gateway method `codex.task.sendInstruction` consumes `{ cardId, text, idempotencyKey }` and server-derived actor/origin context.
- Text is normalized, non-empty, and bounded to an exact constant defined in `task-control-gateway.ts`.
- Authorization requires owner identity for Telegram or `operator.admin` for Control UI; route metadata comes from trusted Gateway context, never caller JSON.

- [ ] **Step 1: Write authorization and origin RED tests**

Cover owner Telegram, non-owner rejection, Control UI admin, device-only rejection, forged origin fields, unknown fields, whitespace-only text, oversize text, duplicate idempotency, and sanitized errors.

- [ ] **Step 2: Run RED**

```powershell
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/codex/src/task-control-gateway.test.ts extensions/codex/src/command-authorization.test.ts
```

- [ ] **Step 3: Implement and register the operation**

Enqueue first, then invoke the controller. The direct response reports only outcome, card ID, instruction ID, and safe next action. Do not register a Windows node command or a generic Codex method tool.

- [ ] **Step 4: Run GREEN and type checks**

```powershell
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/codex/src/task-control-gateway.test.ts extensions/codex/src/command-authorization.test.ts
pnpm tsgo:extensions
```

- [ ] **Step 5: Commit**

```powershell
git add extensions/codex/index.ts extensions/codex/src/task-control-gateway.ts extensions/codex/src/task-control-gateway.test.ts extensions/codex/src/command-authorization.ts extensions/codex/src/command-authorization.test.ts
git commit -m "feat(codex): add authorized task instruction operation"
```

### Task 5: Reconcile queued instructions after ownership changes

**Repository:** `E:\OpenClaw\worktrees\openclaw-codex-session-access`

**Files:**
- Create: `extensions/codex/src/task-instruction-reconciler.ts`
- Create: `extensions/codex/src/task-instruction-reconciler.test.ts`
- Modify: `extensions/codex/index.ts`
- Modify: `extensions/codex/src/task-control.ts`
- Modify: `extensions/codex/src/task-control.test.ts`

**Interfaces:**
- `CodexTaskInstructionReconciler` coalesces wakeups, claims one FIFO instruction per thread, rechecks ownership, delegates to `CodexTaskController`, and consumes only after accepted `turn/start`.
- Indeterminate outcomes remain durable and require state readback before retry.

- [ ] **Step 1: Write restart and race RED tests**

Cover Gateway restart, external active-to-idle transition, duplicate wakeups, lease loss, instruction added during scan, indeterminate interrupt, consumption after accepted start, and authorization revoked while queued.

- [ ] **Step 2: Run RED**

```powershell
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/codex/src/task-instruction-reconciler.test.ts extensions/codex/src/task-control.test.ts
```

- [ ] **Step 3: Implement coalesced reconciliation**

Use bounded backoff and explicit state readback. Do not generate routine Telegram messages; any blocked-age event goes through existing notification policy rather than direct channel delivery.

- [ ] **Step 4: Run GREEN**

Run the Step 2 command. Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add extensions/codex/index.ts extensions/codex/src/task-instruction-reconciler.ts extensions/codex/src/task-instruction-reconciler.test.ts extensions/codex/src/task-control.ts extensions/codex/src/task-control.test.ts
git commit -m "feat(codex): reconcile queued task instructions"
```

### Task 6: Lock the Windows Companion read-only boundary

**Repository:** `E:\OpenClaw\worktrees\windows-codex-session-access`

**Files:**
- Modify: `tests/OpenClaw.Shared.Tests/CodexCatalogPolicySurfaceTests.cs`
- Modify: `tests/OpenClaw.Tray.Tests/NodeCapabilityRegistryTests.cs`
- Modify: `docs/WINDOWS_NODE_TESTING.md`
- Modify: `docs/ARCHITECTURE.md`

**Interfaces:**
- Produces regression proof that the Companion exposes only the approved catalog commands from the reconciliation plan and no resume/start/interrupt/steer/generic passthrough command.

- [ ] **Step 1: Write/extend the failing source-policy assertion**

Make the test enumerate the effective Codex command snapshot and explicitly reject strings matching `turn.start`, `turn.interrupt`, `turn.steer`, `thread.resume`, `codex.cli.session.resume`, and generic App Server invoke names.

- [ ] **Step 2: Run the focused tests**

```powershell
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore --filter "FullyQualifiedName~CodexCatalogPolicySurfaceTests"
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore --filter "FullyQualifiedName~NodeCapabilityRegistryTests"
```

Expected: the assertions pass without production write-command changes; if they fail, remove the unintended surface before continuing.

- [ ] **Step 3: Update the architecture/testing documentation**

Describe OpenClaw-owned write execution, external-active queueing, and the Companion prohibition.

- [ ] **Step 4: Run `git diff --check` and commit**

```powershell
git diff --check
git add tests/OpenClaw.Shared.Tests/CodexCatalogPolicySurfaceTests.cs tests/OpenClaw.Tray.Tests/NodeCapabilityRegistryTests.cs docs/WINDOWS_NODE_TESTING.md docs/ARCHITECTURE.md
git commit -m "test: lock codex task control out of companion"
```

### Task 7: Full verification, reviews, and disposable end-to-end proof

**Repositories:** native OpenClaw and Windows Companion worktrees.

- [ ] **Step 1: Run native focused and full verification**

```powershell
cd E:\OpenClaw\worktrees\openclaw-codex-session-access
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/workboard/src/codex-instructions.test.ts extensions/codex/src/task-control.test.ts extensions/codex/src/task-adoption.test.ts extensions/codex/src/task-control-gateway.test.ts extensions/codex/src/task-instruction-reconciler.test.ts
pnpm format:check
pnpm lint
pnpm build
pnpm test:extensions
```

- [ ] **Step 2: Run Companion verification**

```powershell
cd E:\OpenClaw\worktrees\windows-codex-session-access
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore
```

- [ ] **Step 3: Dispatch security and quality reviews**

Review exact-turn ownership, authorization generation, interrupt/start ordering, indeterminate outcomes, instruction confidentiality, queue durability, route-derived notification behavior, cross-plugin trust, and Companion command invariants. Fix every Critical or Important finding through focused RED/GREEN and scoped re-review.

- [ ] **Step 4: Run disposable live proofs**

Using only disposable Workboard cards and Codex threads:

1. send from Control UI to an OpenClaw-owned active turn and prove exact interrupt then one new turn, with no Telegram delivery event;
2. send from Telegram to an idle linked task and prove one resumed turn and one Telegram response;
3. send to a Desktop-owned active task and prove durable queueing with zero Companion write invocations;
4. let the external task become idle and prove one queued instruction is consumed after safe adoption;
5. restart Gateway between enqueue and adoption and prove exactly-once behavior.

Record only IDs, state transitions, counts, timestamps, and hashes under `E:\OpenClaw\personal-work-system\evidence`; never retain prompts or transcript bodies.
