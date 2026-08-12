# Codex Workboard Deep Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a resumable, E-drive-hosted reconciliation system that scans historical and current Codex work plus project evidence, groups it into canonical Workboard objectives, and continuously projects safe lifecycle changes.

**Architecture:** A personal `codex-workboard-reconciler` OpenClaw plugin owns discovery, classification, checkpoints, and orchestration. It uses a narrow supported Workboard reconciliation facade and bounded Codex catalog commands; it never imports Workboard internals or writes either SQLite store directly. Historical archived discovery is added as a separate Windows node command so the existing two read commands retain their exact semantics and security contract.

**Tech Stack:** TypeScript, OpenClaw plugin SDK, Vitest, SQLite-backed plugin state through supported SDK APIs, .NET 10/C#/xUnit for the Windows node catalog extension, Git CLI read-only porcelain/log commands.

## Global Constraints

- Workboard remains the sole authoritative task ledger.
- All persistent implementation, state, reports, and evidence created by this project remain on `E:`.
- Never write Workboard SQLite or Codex private stores directly.
- Preserve the semantics of `codex.appServer.threads.list.v1` and `codex.appServer.thread.turns.list.v1`.
- Historical discovery must be bounded, paginated, resumable, and separately authorized.
- High-confidence matches may link automatically; ambiguous matches go to `triage` and are never silently merged.
- Idle, `notLoaded`, process exit, or missing sessions never imply `review` or `done`.
- Reconciliation never overrides manual `blocked`, `review`, or `done` state.
- Routine scanning and reconciliation send no Telegram notification.
- Do not traverse `C:`, secrets, build caches, dependency vendors, binary assets, `.git` object storage, or private application databases.
- Do not log transcript bodies, file contents, secrets, or command arguments.
- Telegram Desktop remains outside the system boundary.

---

### Task 1: Add the Workboard reconciliation contract and paginated facade

**Repository:** `E:\OpenClaw\worktrees\openclaw-codex-session-access`

**Files:**
- Modify: `packages/workboard-contract/src/index.ts`
- Create: `extensions/workboard/src/reconciliation.ts`
- Create: `extensions/workboard/src/reconciliation.test.ts`
- Modify: `extensions/workboard/src/gateway.ts`
- Modify: `extensions/workboard/src/gateway.test.ts`
- Modify: `extensions/workboard/runtime-api.ts`

**Interfaces:**
- Produces `WorkboardExternalExecutionLink`, `WorkboardReconciliationObservation`, `WorkboardReconciliationPage`, and `WorkboardReconciliationApplyResult` contract types.
- Produces Gateway RPC methods `workboard.reconciliation.list` under `operator.read` and `workboard.reconciliation.apply` under `operator.write`.
- `list` consumes `{ cursor?: string; limit?: number; tenant?: string; boardId?: string; terminal?: boolean }` with `limit` in `1..100` and returns stable ID-ordered pagination.
- `apply` consumes one idempotent external observation with `sourceUrl`, `tenant`, `idempotencyKey`, `sourceUpdatedAt`, proposed card/link fields, and expected Workboard revision.

- [ ] **Step 1: Write failing contract and Gateway tests**

Add tests proving pagination is stable, limits reject `0` and `101`, duplicate idempotency keys return the existing result, stale `sourceUpdatedAt` is a no-op, and `blocked`/`review`/`done` cannot be changed by reconciliation.

- [ ] **Step 2: Run the focused tests and record RED**

Run:

```powershell
cd E:\OpenClaw\worktrees\openclaw-codex-session-access
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/workboard/src/reconciliation.test.ts extensions/workboard/src/gateway.test.ts
```

Expected: failure because the reconciliation contracts and RPC methods do not exist.

- [ ] **Step 3: Implement the minimum facade**

Implement stable opaque cursors, bounds, compare-and-set behavior, idempotent create/link/update, and status policy in `reconciliation.ts`. Route all mutations through `WorkboardStore`; do not expose `WorkboardStore` to other plugins and do not open SQLite from the reconciler.

- [ ] **Step 4: Run focused tests and type checks GREEN**

Run:

```powershell
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/workboard/src/reconciliation.test.ts extensions/workboard/src/gateway.test.ts
pnpm tsgo:extensions
```

Expected: all selected tests and extension type checks pass.

- [ ] **Step 5: Commit**

```powershell
git add packages/workboard-contract/src/index.ts extensions/workboard/runtime-api.ts extensions/workboard/src/reconciliation.ts extensions/workboard/src/reconciliation.test.ts extensions/workboard/src/gateway.ts extensions/workboard/src/gateway.test.ts
git commit -m "feat(workboard): add reconciliation facade"
```

### Task 2: Add a separately authorized archived Codex catalog command

**Repository:** `E:\OpenClaw\worktrees\windows-codex-session-access`

**Files:**
- Modify: `src/OpenClaw.Shared/Capabilities/CodexSessionCapability.cs`
- Modify: `src/OpenClaw.Shared/Codex/CodexSessionCatalogService.cs`
- Modify: `src/OpenClaw.Shared/Codex/CodexAppServerProtocol.cs`
- Modify: `src/OpenClaw.Shared/Mcp/McpToolBridge.cs`
- Modify: `src/OpenClaw.WinNode.Cli/skill.md`
- Modify: `src/OpenClaw.Tray.WinUI/Services/NodeCapabilityRegistry.cs`
- Modify: `tests/OpenClaw.Shared.Tests/CodexSessionCapabilityTests.cs`
- Modify: `tests/OpenClaw.Shared.Tests/CodexCatalogPolicySurfaceTests.cs`
- Modify: `tests/OpenClaw.Tray.Tests/NodeCapabilityRegistryTests.cs`
- Modify: `tests/OpenClaw.Shared.Tests/McpToolBridgeTests.cs`
- Modify: `tests/OpenClaw.WinNode.Cli.Tests/SkillMdDriftTests.cs`
- Modify: `docs/WINDOWS_NODE_TESTING.md`

**Interfaces:**
- Produces `codex.appServer.threads.history.list.v1` with `{ cursor?, limit?, searchTerm?, archived }`, where `archived` is required and `limit` is `1..100`.
- The command returns the same bounded projected metadata envelope as the existing list command.
- It never returns transcript bodies and does not alter existing v1 list eligibility or defaults.
- Advertisement requires `ReadOnly` or `ReadAndSteer`, Codex executable availability, Gateway allowlisting, and node command-surface reapproval.

- [ ] **Step 1: Write failing policy and behavior tests**

Add tests proving the original commands still force `archived:false`, the new command requires an explicit boolean, rejects unknown fields, returns only projected metadata, respects page/byte budgets, and is canceled/revoked by the registry generation.

- [ ] **Step 2: Run focused tests and record RED**

Run:

```powershell
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore --filter "FullyQualifiedName~CodexSessionCapabilityTests|FullyQualifiedName~CodexCatalogPolicySurfaceTests"
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore --filter "FullyQualifiedName~NodeCapabilityRegistryTests"
```

Expected: failure because the history command is absent.

- [ ] **Step 3: Implement the bounded command without weakening v1**

Add a distinct command handler and protocol parameter builder. Preserve field projection, launch-time trust validation, response budgets, cancellation, final delivery authorization, and sanitized errors. Add the canonical MCP description and update the CLI skill so drift tests cover the command.

- [ ] **Step 4: Run focused tests GREEN**

Run the two commands from Step 2. Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/OpenClaw.Shared src/OpenClaw.Tray.WinUI/Services/NodeCapabilityRegistry.cs tests/OpenClaw.Shared.Tests tests/OpenClaw.Tray.Tests docs/WINDOWS_NODE_TESTING.md
git commit -m "feat: add bounded codex history catalog"
```

### Task 3: Teach the native Codex catalog policy about the history command

**Repository:** `E:\OpenClaw\worktrees\openclaw-codex-session-access`

**Files:**
- Modify: `extensions/codex/src/session-catalog-parsing.ts`
- Modify: `extensions/codex/src/session-catalog-types.ts`
- Modify: `extensions/codex/src/session-catalog.ts`
- Modify: `extensions/codex/src/session-catalog.test.ts`

**Interfaces:**
- Produces `CODEX_APP_SERVER_THREADS_HISTORY_LIST_COMMAND`.
- Produces an internal reconciler-only history enumeration function that invokes the history command only when the caller holds `operator.admin` and supplies an explicit `archived` partition.
- Does not add archived sessions to the ordinary Control UI session catalog.

- [ ] **Step 1: Write failing policy tests**

Prove ordinary catalog listing never invokes the history command; the internal history path requires `operator.admin`, paginates both archived partitions explicitly, rejects cursor loops, and fails closed if the node does not advertise the exact command.

- [ ] **Step 2: Run RED**

```powershell
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/codex/src/session-catalog.test.ts
```

- [ ] **Step 3: Implement the internal history path**

Keep it out of `SessionCatalogProvider.list`; expose it only through the plugin-private reconciliation registration used in Task 8.

- [ ] **Step 4: Run GREEN and type checks**

```powershell
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/codex/src/session-catalog.test.ts
pnpm tsgo:extensions
```

- [ ] **Step 5: Commit**

```powershell
git add extensions/codex/src/session-catalog-parsing.ts extensions/codex/src/session-catalog-types.ts extensions/codex/src/session-catalog.ts extensions/codex/src/session-catalog.test.ts
git commit -m "feat(codex): add guarded history catalog path"
```

### Task 4: Scaffold the E-drive personal reconciliation plugin

**Repository:** `E:\OpenClaw\personal-work-system`

**Files:**
- Create: `plugins/codex-workboard-reconciler/openclaw.plugin.json`
- Create: `plugins/codex-workboard-reconciler/package.json`
- Create: `plugins/codex-workboard-reconciler/tsconfig.json`
- Create: `plugins/codex-workboard-reconciler/index.ts`
- Create: `plugins/codex-workboard-reconciler/src/config.ts`
- Create: `plugins/codex-workboard-reconciler/src/config.test.ts`
- Modify: `README.md`

**Interfaces:**
- Plugin ID: `codex-workboard-reconciler`.
- Config includes `projectRoots`, `excludedDirectoryNames`, `maxFileBytes`, `historyPageSize`, `batchSize`, `minimumAutoLinkConfidence`, `activeCadenceSeconds`, and `staleAfterSuccessfulScans`.
- Defaults include only explicitly approved `E:` roots and reject any resolved path outside them.

- [ ] **Step 1: Scaffold the package and write failing manifest/config tests**

Create the manifest, package metadata, TypeScript config, and test first. Test canonical path resolution, rejection of `C:`, traversal, reparse escapes, invalid bounds, and unknown config keys. Point development dependencies at the exact native OpenClaw worktree version; do not fetch a floating OpenClaw release.

- [ ] **Step 2: Run RED**

```powershell
cd E:\OpenClaw\personal-work-system
pnpm --dir plugins/codex-workboard-reconciler exec vitest run src/config.test.ts
```

- [ ] **Step 3: Scaffold and implement config parsing**

Follow the OpenClaw plugin manifest convention, not the Codex `.codex-plugin` marketplace format. Register no scanner service until configuration validates.

- [ ] **Step 4: Run GREEN and validate plugin loading**

```powershell
pnpm --dir plugins/codex-workboard-reconciler exec vitest run src/config.test.ts
wsl.exe -d OpenClawGateway -- openclaw plugins inspect codex-workboard-reconciler --runtime --json
```

Expected: tests pass and runtime inspection reports the plugin loaded without secrets.

- [ ] **Step 5: Commit**

```powershell
git add plugins/codex-workboard-reconciler README.md
git commit -m "feat: scaffold codex workboard reconciler"
```

### Task 5: Implement checkpointed source discovery

**Repository:** `E:\OpenClaw\personal-work-system`

**Files:**
- Create: `plugins/codex-workboard-reconciler/src/state.ts`
- Create: `plugins/codex-workboard-reconciler/src/state.test.ts`
- Create: `plugins/codex-workboard-reconciler/src/codex-source.ts`
- Create: `plugins/codex-workboard-reconciler/src/codex-source.test.ts`
- Create: `plugins/codex-workboard-reconciler/src/project-source.ts`
- Create: `plugins/codex-workboard-reconciler/src/project-source.test.ts`

**Interfaces:**
- `ReconciliationStateStore.load/saveCheckpoint/withLease` persists versioned cursor, source hash, route, scan generation, timestamps, and failure counts only.
- `CodexSource.scanBatch(checkpoint, signal)` returns bounded thread metadata and lazily fetches transcript evidence only on classifier request.
- `ProjectSource.scanBatch(checkpoint, signal)` returns allowlisted text metadata, repository identity, porcelain status, current branch, and bounded recent commit summaries.

- [ ] **Step 1: Write failing source and restart tests**

Cover lease exclusion, atomic checkpoint commit, crash/resume, cursor-loop rejection, source-hash no-op, excluded paths, symlink/reparse escape, file-size limits, and Git command argv allowlisting.

- [ ] **Step 2: Run RED**

```powershell
pnpm --dir plugins/codex-workboard-reconciler exec vitest run src/state.test.ts src/codex-source.test.ts src/project-source.test.ts
```

- [ ] **Step 3: Implement minimal bounded readers and state**

Store state below the plugin's E-backed OpenClaw state root. Do not store transcript or file bodies after classification; persist hashes and references only.

- [ ] **Step 4: Run GREEN**

Run the Step 2 command. Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add plugins/codex-workboard-reconciler/src
git commit -m "feat: add resumable reconciliation sources"
```

### Task 6: Implement objective classification and conservative deduplication

**Repository:** `E:\OpenClaw\personal-work-system`

**Files:**
- Create: `plugins/codex-workboard-reconciler/src/classifier.ts`
- Create: `plugins/codex-workboard-reconciler/src/classifier.test.ts`
- Create: `plugins/codex-workboard-reconciler/src/evidence.ts`
- Create: `plugins/codex-workboard-reconciler/src/evidence.test.ts`

**Interfaces:**
- `classifyObjective(input): ObjectiveDecision` returns `create`, `link`, `triage`, or `ignore` with a bounded rationale code and confidence in `0..1`.
- Automatic linking requires confidence at or above configured threshold plus compatible canonical project identity.
- Triage preserves candidate card IDs and evidence references without merging.

- [ ] **Step 1: Write table-driven RED tests**

Include related chats on one branch, distinct objectives in one project, title-only ambiguity, speculative ideas, duplicate status chats, renamed repository continuity, and conflicting project roots.

- [ ] **Step 2: Run RED**

```powershell
pnpm --dir plugins/codex-workboard-reconciler exec vitest run src/classifier.test.ts src/evidence.test.ts
```

- [ ] **Step 3: Implement deterministic feature extraction and decision policy**

Keep model-assisted summarization behind an injected interface; deterministic safety gates decide whether automatic mutation is allowed.

- [ ] **Step 4: Run GREEN**

Run the Step 2 command. Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add plugins/codex-workboard-reconciler/src/classifier.ts plugins/codex-workboard-reconciler/src/classifier.test.ts plugins/codex-workboard-reconciler/src/evidence.ts plugins/codex-workboard-reconciler/src/evidence.test.ts
git commit -m "feat: classify canonical workboard objectives"
```

### Task 7: Apply reconciliation safely to Workboard

**Repository:** `E:\OpenClaw\personal-work-system`

**Files:**
- Create: `plugins/codex-workboard-reconciler/src/workboard-client.ts`
- Create: `plugins/codex-workboard-reconciler/src/workboard-client.test.ts`
- Create: `plugins/codex-workboard-reconciler/src/reconciler.ts`
- Create: `plugins/codex-workboard-reconciler/src/reconciler.test.ts`

**Interfaces:**
- `WorkboardReconciliationClient.list/apply` uses only `api.runtime.gateway.request(...)` against the Task 1 RPC facade; the Gateway supplies the plugin runtime's authenticated context and the plugin never reads or stores an operator token.
- `Reconciler.runBatch(mode, signal)` supports `onboarding` and `continuous`, applies one lease, and commits a checkpoint only after Workboard acknowledges idempotent mutations.

- [ ] **Step 1: Write failing orchestration tests**

Prove one objective creates one card with multiple `codex://thread/` links; ambiguous work becomes triage; active may move eligible status to `running`; idle never completes; stale manual state wins; missing nodes preserve card state; repeated batches are no-ops; and no notification API is called.

- [ ] **Step 2: Run RED**

```powershell
pnpm --dir plugins/codex-workboard-reconciler exec vitest run src/workboard-client.test.ts src/reconciler.test.ts
```

- [ ] **Step 3: Implement the client and reconciler**

Use compare-and-set revisions and `sourceUpdatedAt`. Mark a link stale only after the configured count of successful full scans; dependency failure does not increment missing-source evidence.

- [ ] **Step 4: Run GREEN**

Run the Step 2 command. Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add plugins/codex-workboard-reconciler/src/workboard-client.ts plugins/codex-workboard-reconciler/src/workboard-client.test.ts plugins/codex-workboard-reconciler/src/reconciler.ts plugins/codex-workboard-reconciler/src/reconciler.test.ts
git commit -m "feat: reconcile codex evidence into workboard"
```

### Task 8: Register manual onboarding and continuous service controls

**Repositories:** `E:\OpenClaw\worktrees\openclaw-codex-session-access`, then `E:\OpenClaw\personal-work-system`

**Files:**
- Modify: `extensions/codex/index.ts`
- Modify: `extensions/codex/src/session-catalog.ts`
- Modify: `extensions/codex/src/session-catalog.test.ts`
- Modify: `plugins/codex-workboard-reconciler/index.ts`
- Create: `plugins/codex-workboard-reconciler/src/service.ts`
- Create: `plugins/codex-workboard-reconciler/src/service.test.ts`
- Create: `runbooks/codex-workboard-reconciliation.md`
- Modify: `README.md`

**Interfaces:**
- The Codex plugin registers a private reconciliation provider for bounded history enumeration and transcript-on-demand; it is not a public node command passthrough.
- The personal plugin registers `Sync now`, `Pause`, `Resume`, and status operations, plus a coalesced service cadence no faster than 60 seconds while linked work is active.
- Status returns counts, phase, checkpoint, and triage totals only.

- [ ] **Step 1: Write failing registration/service tests**

Cover admin scope, single-flight coalescing, pause persistence, restart resume, exponential backoff with jitter bounds, no routine notification, and transcript reads only after explicit classifier demand.

- [ ] **Step 2: Run RED in each repository**

```powershell
cd E:\OpenClaw\worktrees\openclaw-codex-session-access
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/codex/src/session-catalog.test.ts
cd E:\OpenClaw\personal-work-system
pnpm --dir plugins/codex-workboard-reconciler exec vitest run src/service.test.ts
```

- [ ] **Step 3: Implement registration, controls, and runbook**

Document start, pause, status, recovery, E-drive paths, excluded-data policy, and the fact that Telegram Desktop is unused.

- [ ] **Step 4: Run GREEN and commit each repository**

```powershell
cd E:\OpenClaw\worktrees\openclaw-codex-session-access
node scripts/run-vitest.mjs run --config test/vitest/vitest.unit.config.ts extensions/codex/src/session-catalog.test.ts
git add extensions/codex
git commit -m "feat(codex): register reconciliation source"

cd E:\OpenClaw\personal-work-system
pnpm --dir plugins/codex-workboard-reconciler exec vitest run src/service.test.ts
git add plugins/codex-workboard-reconciler runbooks/codex-workboard-reconciliation.md README.md
git commit -m "feat: operate codex workboard reconciliation"
```

### Task 9: Full verification, security review, and disposable live proof

**Repositories:** all three repositories above.

- [ ] **Step 1: Run full Windows Companion verification**

```powershell
cd E:\OpenClaw\worktrees\windows-codex-session-access
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore
git diff --check HEAD~1 HEAD
```

- [ ] **Step 2: Run native OpenClaw verification**

```powershell
cd E:\OpenClaw\worktrees\openclaw-codex-session-access
pnpm format:check
pnpm lint
pnpm build
pnpm test:extensions
```

- [ ] **Step 3: Run personal plugin verification**

```powershell
cd E:\OpenClaw\personal-work-system
pnpm --dir plugins/codex-workboard-reconciler exec vitest run
wsl.exe -d OpenClawGateway -- openclaw config validate
wsl.exe -d OpenClawGateway -- openclaw plugins inspect codex-workboard-reconciler --runtime --json
```

- [ ] **Step 4: Dispatch final security and quality reviews**

Review the three-repository change set for authorization, stale-write races, path escapes, content leakage, direct-store access, cursor exhaustion, duplicate creation, status authority, notification silence, and rollback behavior. Fix every Critical or Important finding with a focused RED/GREEN loop and scoped re-review.

- [ ] **Step 5: Run a disposable live proof**

Use one disposable test repository on `E:` and two disposable Codex chats describing one objective. Interrupt and resume the onboarding scanner, then prove one canonical Workboard card contains two source links, Control UI refreshes, repeated sync is a no-op, no Telegram notification is emitted, and no source transcript/file body appears in logs. Remove only disposable proof data after recording redacted counts and hashes under `E:\OpenClaw\personal-work-system\evidence`.
