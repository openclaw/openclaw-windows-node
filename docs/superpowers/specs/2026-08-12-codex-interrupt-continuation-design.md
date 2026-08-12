# Codex Interrupt and Continuation Design

**Date:** 2026-08-12

**Status:** Approved design baseline

**Scope:** Owner-authorized interruption and continuation of Codex work through OpenClaw

## 1. Purpose

OpenClaw must let the owner send a message to a linked Codex task from either Control UI or Telegram. The same user action behaves according to the task's actual execution state:

- if an OpenClaw-owned Codex turn is running, interrupt that exact turn, wait for the terminal interrupted state, and submit the message as a new turn;
- if the linked Codex thread is idle or stale, resume it and submit the message directly;
- if Codex Desktop or another runner owns an active turn, record the message as a queued Workboard instruction and apply it only after the thread becomes safely adoptable.

This phase does not provide in-place `turn/steer` behavior.

## 2. Architectural Boundary

OpenClaw's native Codex runtime owns all writable execution. The Windows Companion continues to expose only these read commands:

- `codex.appServer.threads.list.v1`
- `codex.appServer.thread.turns.list.v1`

The Companion must not advertise `turn/start`, `turn/steer`, `turn/interrupt`, `thread/resume`, or a generic App Server passthrough. Its catalog client is short-lived and does not own the approval and event stream required for safe writable turns.

New work should be launched through an OpenClaw-native Codex binding. Historical idle threads may be adopted by that runtime through supported Codex interfaces. An active thread owned by Codex Desktop or another runner is never controlled through a competing App Server process.

## 3. Identity and Ownership

Each controllable execution record contains:

- immutable Workboard card ID;
- immutable Codex thread UUID;
- OpenClaw session or ACP binding ID;
- current runtime owner identity;
- active turn ID when known;
- last observed lifecycle state and source timestamp;
- queued instruction ID, actor, source interface, timestamp, and idempotency key when applicable.

Codex thread IDs are external execution identifiers. They do not replace Workboard card IDs, Gateway session keys, or Workboard run IDs.

Only the owner Telegram identity or an authenticated Control UI operator with the required administrative scope may request interruption or continuation. Device possession alone is insufficient.

## 4. State-Aware Message Operation

The public operation is conceptually `send instruction to linked task`, not a raw Codex protocol call.

### 4.1 OpenClaw-owned active turn

1. Resolve the Workboard link and active native Codex binding.
2. Capture the exact thread ID, turn ID, binding generation, and authorization generation.
3. Send `turn/interrupt` for that exact turn.
4. Wait for the authoritative terminal event showing that turn is interrupted.
5. Revalidate binding, owner authorization, queued-instruction state, and thread ownership.
6. Start one new turn containing the bounded text instruction.
7. Mark the instruction consumed only after the new turn is accepted.

The operation must never convert an interrupt failure into a speculative new turn.

### 4.2 Idle or stale linked thread

1. Resolve the canonical Codex thread UUID and verify no active owner conflict.
2. Resume or adopt the thread through the native Codex runtime.
3. Establish a persistent binding capable of processing approvals and lifecycle events.
4. Start one new turn with the instruction.
5. Link the new execution run to the existing Workboard card.

### 4.3 Externally owned active turn

The instruction is durably queued on the Workboard execution record. The user receives a concise status that the instruction is waiting for the current external turn to finish. Reconciliation applies it once, in order, after ownership is safe. It does not interrupt the external process, start a competing turn, or repeatedly notify Telegram.

## 5. Ordering, Idempotency, and Failure Handling

- Each instruction has a caller-independent idempotency key and may be consumed once.
- Instructions for one Codex thread are serialized in creation order.
- Binding and authorization generations are revalidated immediately before interruption and immediately before starting the replacement turn.
- Revocation prevents dispatch and result delivery that have not crossed their final authorization boundary.
- An already accepted Codex interruption cannot be undone; the audit record reports that fact without including message content.
- Ambiguous transport outcomes are not retried automatically. Reconciliation first reads authoritative turn state.
- Process restart preserves queued instructions and consumption state.
- Failure messages contain stable outcome codes and resumable next actions, not prompts, transcript text, tokens, private App Server errors, or filesystem contents.

## 6. Interface and Notification Behavior

Control UI and Telegram invoke the same task-level operation. The originating interface receives the direct response.

- A Control UI instruction does not generate a routine Telegram alert.
- A Telegram instruction replies in Telegram.
- Routine interrupt, resume, queue, and start transitions update Workboard silently.
- A notification is permitted only when user action is required, an instruction remains blocked beyond policy, or significant work reaches review/completion under the existing notification rules.

## 7. Security Constraints

- Preserve the Companion's two-command read-only catalog surface.
- Do not rely on `allowWriteControls` as the sole security boundary; enforce authorization behaviorally at dispatch.
- Never accept caller-selected executables, environments, Codex homes, models, providers, working directories, sandboxes, or approval policies through this operation.
- Accept bounded non-empty text only in the first release; no files, images, audio, skills, mentions, or arbitrary metadata.
- Do not log instruction or transcript bodies.
- Require explicit Gateway reapproval for any changed native command surface.
- Never use private Desktop IPC, UI automation, process-memory access, or direct mutation of Codex internal stores.

## 8. Acceptance Tests

1. A message to an OpenClaw-owned running task interrupts the exact active turn and starts exactly one replacement turn.
2. A changed turn ID or binding generation prevents interruption and prevents a new turn.
3. A message to an idle linked thread resumes it and starts exactly one turn.
4. A message to a Desktop-owned active thread queues without issuing any write request to the Companion or Codex Desktop.
5. A queued instruction is consumed once after the external thread becomes adoptable, including across Gateway restart.
6. An ambiguous interrupt response does not cause an automatic retry or replacement turn.
7. Authorization revocation before dispatch or delivery fails closed.
8. Control UI-originated work produces no routine Telegram notification; Telegram-originated work replies to Telegram.
9. Logs, audit records, and error payloads contain no instruction or transcript content.
10. The Companion still advertises exactly the two existing read commands.

## 9. Delivery Boundary

This specification enables interruption and continuation only through OpenClaw-owned native Codex bindings. Shared control of a turn actively owned by Codex Desktop remains blocked until Codex provides a supported same-owner multi-client transport with complete approval and event handling.
