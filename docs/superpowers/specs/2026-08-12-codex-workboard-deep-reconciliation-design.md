# Codex Workboard Deep Reconciliation Design

**Date:** 2026-08-12

**Status:** Approved design baseline

**Scope:** Historical portfolio onboarding and continuous Codex-to-Workboard reconciliation

## 1. Purpose

OpenClaw must build and maintain a useful Workboard view from both current and historical Codex work and the actual project folders on `E:`. The result must scale beyond fifty projects without turning every chat into a separate task or making task state dependent on prompt memory.

Workboard remains the sole authoritative task ledger. Codex chats and project repositories are evidence sources and execution links, not competing task databases.

## 2. Two Operating Modes

### 2.1 Portfolio onboarding scan

A one-time, resumable deep scan processes:

- all discoverable current and historical Codex thread metadata;
- bounded transcript pages needed to establish intent, decisions, blockers, and unfinished work;
- configured project roots on `E:`;
- project README files, plans, specifications, task documents, and other allowlisted text metadata;
- Git repository status, branches, recent bounded history, and tracked project structure.

The scan runs in bounded batches with durable checkpoints. It can stop and resume without duplicating cards or repeating completed analysis.

### 2.2 Continuous reconciliation

After onboarding, a lightweight reconciler processes new and changed Codex threads and linked active projects. It uses metadata first and reads transcript or project evidence only when classification requires it. It coalesces runs, applies exponential backoff when dependencies are unavailable, and performs idempotent no-op updates when nothing changed.

## 3. Canonical Task Model

One Workboard card represents one distinct actionable objective. Related Codex chats become execution records linked to that card.

The canonical link identity is:

```text
codex://thread/<thread-uuid>
```

The deterministic import key is:

```text
codex-thread:<thread-uuid>
```

Codex thread IDs must not replace Workboard `taskId`, `sessionKey`, or `runId` fields. Mutable Windows node IDs and host routes belong in reconciler routing state, not canonical identity.

Each generated or updated card records:

- project and board assignment;
- concise objective and current summary;
- status and priority when evidence supports them;
- linked Codex thread IDs and execution runs;
- source paths and evidence references;
- confidence, classification rationale code, and last source timestamp;
- idempotency and reconciliation generation;
- blockers, completion criteria, and artifacts when supported by evidence.

## 4. Classification and Deduplication

The processing pass identifies overarching objectives using both conversation evidence and project evidence. It compares normalized intent, project root, referenced plans/issues, linked artifacts, Git branch, time proximity, and existing Workboard links.

- High-confidence distinct objectives create cards automatically.
- High-confidence matches attach the Codex chat as another execution record to the existing card.
- Ambiguous matches create or update a `triage` candidate with proposed relationships; they are not silently merged.
- Speculative ideas, transient observations, duplicate status chats, and implementation details remain notes or evidence unless they form a distinct actionable objective.
- A merge operation preserves both source histories and is reversible through the Workboard audit trail.

The system never claims semantic certainty solely from a chat title.

## 5. Project Discovery and Scope

Configured project roots, initially under `E:\Work` and other explicitly approved `E:` locations, are scanned through allowlisted readers. The scanner does not traverse `C:`, secrets, build caches, dependency vendors, binary assets, `.git` object storage, or private application databases.

Each discovered repository receives a stable project identity derived from its approved canonical root and repository identity. Project renames and moves are reconciled without creating duplicate projects when repository evidence proves continuity.

Inactive and completed projects remain searchable but are excluded from normal focus views. The active project set is configurable and not hard-coded to the current project count.

## 6. Status Reconciliation

- A verified active linked Codex execution may move an eligible `ready` or `scheduled` card to `running`.
- Codex `idle`, `notLoaded`, process exit, or conversation completion never implies `review` or `done`.
- Reconciliation never overrides manual `blocked`, `review`, or `done` state.
- Completion requires Workboard criteria plus recorded evidence.
- An older source observation cannot overwrite a newer Workboard transition.
- Missing or unreachable Codex sessions do not delete, archive, block, or complete cards.
- After repeated successful full scans or an elapsed stale threshold, a missing execution link may be marked stale while preserving card state.
- Manual Workboard changes remain authoritative unless the user explicitly requests reclassification.

## 7. Reconciler Placement and Data Flow

A personal OpenClaw plugin stored on `E:` owns scanning and reconciliation. It runs beside Workboard in the Gateway and uses supported interfaces only:

- Gateway/node commands for Codex catalog and bounded transcript reads;
- Workboard RPC or agent tools for card reads and mutations;
- allowlisted filesystem and Git readers for project evidence.

It never writes Workboard SQLite or Codex private stores directly. Its private state contains only checkpoints, source hashes, routing data, confidence decisions, timestamps, and failure counters. This state supports replay but is not a second task ledger.

Workboard mutations emit the existing `plugin.workboard.changed` event, allowing Control UI to refresh through its native coalescing path.

## 8. Performance and Scale

- Historical discovery and analysis are paginated and checkpointed.
- Transcript bodies are fetched only when needed and are bounded by page, text, operation, and aggregate-byte limits.
- Project files are filtered by path, type, size, ignore rules, and source hash.
- Git inspection uses bounded history and porcelain/status interfaces; it does not scan object contents.
- Only active or recently changed links participate in frequent reconciliation.
- Background sync begins with a manual `Sync now` operation plus a conservative cadence no faster than once per minute while linked work is active.
- Node or Gateway failure triggers bounded exponential backoff with jitter.
- Before large historical import is exposed as a routine UI action, Workboard reads used by the scanner must be filtered or paginated rather than loading the entire archive repeatedly.

## 9. Notifications and User Experience

Routine discovery, card creation, linking, status projection, and successful reconciliation are silent. They update Workboard and Telegram's compact on-demand status without creating alerts.

Notification events are reserved for prolonged scanner failure, a conflict requiring owner judgment, blocked work, review-ready work, important completion, or existing reminder policy. No acknowledgement, confirmation, dismissal, or snoozing is required for reminders.

The onboarding UI reports batch progress, checkpoints, counts, and triage totals without exposing transcript bodies. The user can pause and resume the scan. Pausing does not discard completed work.

## 10. Reliability and Security

- Every card mutation has an idempotency key and records source, actor, prior state, new state, timestamp, and result.
- A single lease prevents overlapping onboarding or reconciliation writers.
- Crash recovery resumes from the last committed checkpoint.
- Source hashes and timestamps prevent stale overwrite.
- No secret values, transcript bodies, full file contents, or command arguments appear in logs or telemetry.
- Telegram Desktop remains outside the system boundary.
- Persistent implementation, state, reports, and evidence created by this project remain on `E:`; app-owned profile metadata is tolerated but not used as project storage.
- Direct SQLite writes, Codex-store mutation, private IPC, and unbounded filesystem traversal are prohibited.

## 11. Acceptance Tests

1. A bounded historical scan resumes after interruption without duplicate cards or links.
2. Current and historical chats representing one objective produce one canonical card with multiple execution records.
3. Distinct actionable objectives produce distinct cards.
4. Ambiguous grouping produces a triage item and does not merge cards.
5. Project README, plan, Git status/history, and Codex evidence contribute to classification without scanning excluded paths.
6. Repeated reconciliation is an idempotent no-op.
7. A verified active execution may move an eligible card to `running`; idle does not mark it complete.
8. Newer manual `blocked`, `review`, or `done` state wins over stale source observations.
9. Node disconnection preserves task state and later recovery repairs the projection.
10. Control UI refreshes through `plugin.workboard.changed`.
11. Routine scanning and synchronization send no Telegram notification.
12. Gateway restart preserves links, checkpoints, and queued work.
13. No direct Workboard or Codex SQLite mutation occurs.
14. All persistent scanner artifacts and evidence reside on `E:`.

## 12. Delivery Order

Implementation proceeds in two independently testable plans:

1. build the scanner, canonical link model, classification pipeline, resumable onboarding, and continuous read-side reconciliation;
2. integrate the approved Codex interruption/continuation operation with canonical Workboard cards and execution records.

The read-side reconciliation plan may ship while writable Codex control remains disabled. This prevents the portfolio onboarding work from weakening the execution security boundary.
