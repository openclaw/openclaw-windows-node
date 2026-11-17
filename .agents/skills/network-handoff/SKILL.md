---
name: network-handoff
description: Use before generating, changing, or reviewing networking, IPC, or cross-process handoffs, including requests, retries, deadlines, reconnect, process launch, configuration apply, cancellation, and cleanup. Establish confirmation, identity, authority, reconciliation, and producer compatibility before implementation.
---

# Network and handoff contracts

For code-generating and reviewing agents, across all networking and IPC, not
just onboarding. The outcome is the smallest change whose next-step postcondition
is established by the actual producer contract. Read-only work stays read-only;
this skill grants no runtime, installation, upstream-write, or publication authority.

## Trace before editing

1. Name the reported behavior and the next step it must safely enable. Follow
   the real caller, transport, receiver, side effect, state owner, and tests.
   Read the affected rows in [the ownership ledger](../../../docs/ARCHITECTURE.md)
   and the owning guide linked from [AGENTS.md](../../../AGENTS.md).
   Do not create another ownership ledger or a parallel client/state store.
2. Record worktree/base/HEAD and producer repository/source revision. Separately
   establish the installed producer build, negotiated protocol/capabilities and
   selected resource/profile. Main-branch source is not installed-version proof.
   Historical documents are hypotheses to recheck, not shipping specifications.
3. Inventory existing request/result schemas, status queries, event delivery,
   persistence, authorization, retries, limits, cancellation and cleanup owners.
   Read implementations and tests, not only method names or an exit code.
4. Write the boundary invariants below in the task's plan or private evidence.
   Mark unknown or inapplicable fields explicitly. Do not invent infrastructure
   for fields the changed contract does not need.

| Question | Required decision |
|---|---|
| What does the acknowledgment establish? | Received, durably accepted, executing, committed, or ready; define persistence/failure scope |
| What does the next step require? | Exact resource, generation, desired/applied revision, freshness and authorization |
| What identifies and authorizes work? | Separate request/operation ID, resource identity, runtime boot/connection generation, revision, and authority/fencing |
| Who knows the outcome? | Authoritative query and its consistency, retention, unknown/expired/not-found meanings; event recovery if used |
| Who may retry or clean up? | One coordinating retry owner, mutation idempotency contract, receiver-side ownership check, cancellation/compensation owner |
| What bounds waiting and execution? | Per-call limit, remaining end-to-end budget, no-progress definition, backend lifetime and cleanup lifetime |

## Choose the minimal sufficient protocol

A bounded synchronous call is enough when it returns the required committed
result with documented semantics. An existing authoritative read plus bounded
polling may be simpler than a watch. Long-running work should acknowledge
acceptance with a queryable operation handle rather than hold the initial RPC
until readiness. Distinguish volatile receipt from durable acceptance explicitly.
Do not add streaming, a broker, a database or a general operation framework
unless the boundary's requirements actually need it.

Apply the following invariants to the affected path:

1. **Identity is not authority.** Correlate replies to their request, resource,
   boot/connection generation and revision as applicable. A UUID, PID, open port
   or matching config is not permission. Preserve credential and provenance
   checks. The receiver must atomically validate current ownership/fencing with
   its side effect, through its existing serialization/conditional-write seam.
   A client precheck alone leaves a race.
2. **State establishes correctness; events accelerate observation.** Use an
   authoritative query with adequate freshness/consistency. Snapshot+watch needs
   a producer-supported revision/cursor bridge, or subscribe/buffer plus a
   documented snapshot barrier. Recover by resnapshot on reconnect or expired
   history; reject duplicate, reordered and stale-generation events. Never
   assume arbitrary events have etcd's delivery guarantees. Without a safe
   bridge use bounded polling if the query suffices.
3. **Operation outcome and runtime health differ.** Terminal operation outcomes
   are immutable for their documented retention lifetime. Observation sequence
   increases within its declared scope, but health/readiness may regress.
   A new boot is a new generation, even on the same endpoint. An old successful
   launch does not prove that the current runtime is ready.
4. **Timeout is an observation result.** Before dispatch, expiry may prevent
   work. After dispatch, timeout/disconnect may leave the outcome unknown,
   including effects already committed. Keep that uncertainty separate from
   authoritative terminal failure. Query by identity; distinguish history
   expiry from proof of absence. Replay only under an applicable idempotency
   contract. Never roll back merely because an acknowledgment was lost.
5. **Bound every call and the whole wait.** Propagate the remaining end-to-end
   duration, accounting for queueing, connection/authentication, reads, polling
   and backoff. Measure local elapsed time monotonically; do not compare
   unsynchronized process clocks as a deadline protocol. Use existing named,
   configurable repository limit patterns with measured/justified values.
   Responsiveness heartbeat is not useful progress. Only documented forward
   progress renews a no-progress allowance; nothing renews the outer ceiling.
   Client wait expiry does not implicitly terminate accepted backend work.
   Specify backend execution, result retention and cleanup ownership separately.
6. **Idempotency must be implemented, not inferred.** Establish caller/resource
   scope, same-key/different-parameters conflict, concurrent deduplication,
   retention/expiry and the crash-safe relationship of effect and result record.
   If external effects cannot commit atomically with the record, require
   recoverable intent/reconciliation and name residual ambiguity. UUIDs and
   in-memory caches do not establish durable exactly-once execution.
7. **Retry at one coordinating layer.** Inventory SDK, transport and workflow
   retries to avoid multiplication. Define bounded attempts/rate, transient
   classification, capped backoff with jitter and supported retry hints within
   the remaining budget. Query retries and mutation retries are different;
   authentication/conflict/provenance failures are not generic transient errors.
8. **Cancellation is not rollback.** Distinguish cancellation requested,
   execution stopped, descendants drained and cleanup/compensation complete.
   Completion may win the race. Query the actual terminal outcome. Compensation
   must be explicit, idempotent, resumable and scoped to current authority at
   the mutation boundary. Never restore an old snapshot over concurrent edits.
9. **Prove the next step, not a proxy.** Saved config is not applied config.
   Alive, listener owned, startup ready, operator admitted and capability
   approved are different predicates. Bind the required predicate to current
   generation/revision/freshness and the actual caller's authorization.
   Current matching state can permit a next step when the contract says so;
   it does not prove which historical operation caused it.

## Inspect producer support and disposition gaps

Use the existing supported contract, including capability/version negotiation,
before declaring a feature missing. Implement only consumer behavior justified
by the supported producer versions. An honest compatibility path may offer a
weaker explicitly named guarantee if the next step accepts it. Otherwise report
the precise unavailable confirmation and stop that transition, not the whole
unrelated product.

For each genuine producer gap, record:

- Repository, authoritative producer file/symbol, pinned SHA and source/test
  links; distinguish implementation, documented guarantee, runtime proof and
  installed-version unknowns.
- Required missing contract, existing support checked, and why consumer reads,
  local IDs or timing cannot establish it. Separate historical attribution from
  present-state reconciliation.
- Minimal versioned producer proposal: schema/semantics, authorization/fencing,
  persistence/retention, query/recovery and errors only as needed for this path.
- Compatibility and rollout: capability discovery, producer-first deployment,
  supported older versions, consumer gating and explicit blocked/degraded state.
  Do not guess a minimum released version from a source commit.
- Acceptance tests covering the failure window that requires producer support.
  Suggest the update to its owning repository; do not silently implement a
  consumer workaround or mutate another repository without authority.

The [pinned onboarding case study](references/upstream-gaps.md) illustrates this
disposition, not a permanent capability catalog. Recheck it for the task's
actual producer revision. [Research notes](references/principles.md) explain
the source-supported principles without importing vendor-specific requirements.

No sleeps, longer timeouts, log parsing, unconditional cleanup or speculative
fallback may substitute for required producer guarantees. Diagnostic logs can
explain a failure, not become an undocumented protocol. Preserve necessary
timeouts and all credential/provenance checks.

## Select failure proof and hand off

Use the existing test harness and the [test authoring](../test-audit/SKILL.md),
[test selection](../openclaw-testing/SKILL.md) and
[proof](../openclaw-proof-validation/SKILL.md) routes. Select applicable cases,
including receiver-side tests where a consumer mock cannot prove the guarantee:

| Failure injection | Observable assertion |
|---|---|
| Drop acknowledgment after acceptance/commit | Reconcile same identity; no blind mutation replay or false terminal failure |
| Duplicate/reorder events; disconnect at snapshot/subscribe | No missed required transition or stale state regression; recover from reconnect/history expiry |
| Crash between effect and receipt; concurrent same key | Recover intent/outcome without duplicate effect; reject changed parameters; expose expiry ambiguity |
| Stale cleanup or delayed write after a new owner | Receiver rejects obsolete authority at the side-effect boundary; newer state survives |
| Cancel as completion commits; child still running | Terminal winner preserved; requested/stopped/drained/compensated remain distinct |
| New boot reuses PID/port; stale config/health reply | Reject stale generation/revision; verify fresh own-identity admission as required |
| Exact timeout/no-progress boundaries with virtual clocks | Finite calls and outer ceiling; wall-clock jumps/heartbeats do not extend budget; pre/post-dispatch outcomes differ |
| Retry storm, unsupported producer, authorization failure | One bounded retry owner; no multiplied retries or invented compatibility success |

For a small read-only query change, retain only the relevant invariants/cases
and explain exclusions; do not demand a full onboarding E2E suite. For runtime
claims, collect current-head evidence at the real changed boundary or name the
blocker. A fixture proves only its simulated contract.

Handoff: changed owner and invariant, exact source/build/version evidence,
confirmation actually achieved, commands/results, unverified boundaries and
upstream dispositions. Do not claim stronger guarantees than the producer.
