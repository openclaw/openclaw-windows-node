# Pinned onboarding handoff case study

Source inspection on 2026-11-09, not runtime proof or a supported-version matrix.
Suggestions here do not authorize changes or publication in another repository.
This is a scoped contract assessment, not another ownership ledger. Recheck
actual producer source and installed capabilities for every implementation task.

## Historical baseline and local boundaries

The October 30 [onboarding handoff proposal](https://gist.github.com/karkarl/b9511fe8c190808c740ccc63ff5edef5)
labels H1-H15 and explicitly describes proposals, not implemented guarantees.
Its October 29 observations and older source revisions cannot establish today's
shipping behavior. The following assessment focuses on H4-H14.

Windows-node source baseline: `9b8b7bf912f67630057cc2021c8b4c1309ccb0bd`.
H10 and H14 are not automatically missing Gateway/packaging features:

- H10 now has local typed handshake observations with transport generation,
  elapsed/phase timing and typed failure/retry hints in
  [GatewayHandshakeObservation](https://github.com/openclaw/openclaw-windows-node/blob/9b8b7bf912f67630057cc2021c8b4c1309ccb0bd/src/OpenClaw.Shared/GatewayHandshakeObservation.cs#L3-L33).
  [NativeGatewaySetupConnection](https://github.com/openclaw/openclaw-windows-node/blob/9b8b7bf912f67630057cc2021c8b4c1309ccb0bd/src/OpenClaw.SetupEngine/NativeGatewaySetupConnection.cs#L61-L97)
  bounds operator admission and forwards observations. These local generations
  do not independently establish a producer boot identity.
- H14 now has a monotonic execution deadline, retained draining work and
  uncertain-finalization retry guards in
  [SetupCompletionPreparation](https://github.com/openclaw/openclaw-windows-node/blob/9b8b7bf912f67630057cc2021c8b4c1309ccb0bd/src/OpenClaw.SetupEngine/SetupCompletionPreparation.cs#L14-L120).
  [SetupNativeCompletionTiming](https://github.com/openclaw/openclaw-windows-node/blob/9b8b7bf912f67630057cc2021c8b4c1309ccb0bd/src/OpenClaw.SetupEngine/SetupNativeCompletionTiming.cs#L30-L82)
  defines finite phase/execution budgets;
  [its tests](https://github.com/openclaw/openclaw-windows-node/blob/9b8b7bf912f67630057cc2021c8b4c1309ccb0bd/tests/OpenClaw.SetupEngine.Tests/SetupNativeCompletionTimingTests.cs#L11-L129)
  include virtual-clock deadline checks. This is source/test inspection, not a
  claim that the historical persisted publication-transaction proposal is
  fully implemented.

Do not reopen these as unchanged historical defects or transfer Companion
publication ownership to Gateway. Any remaining local crash-recovery work
requires tracing the current completion owner and persisted handoff contracts.

## Evidence limits

No installed Gateway/package version, Store rollout, clean-machine behavior,
cross-process crash outcome or native onboarding run was verified for this
documentation change. A source contract can be present without being installed;
absence from the scoped sources below is not proof of absence across all
versions or extensions.

## Gateway: existing support before proposals

Pinned `openclaw/openclaw` main:
`e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e`.

| Historical boundary | Current producer evidence | Disposition |
|---|---|---|
| H6-H7: startup versus admission | [Readiness predicates](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/gateway/server/readiness.ts#L53-L74) and [typed startup-unavailable contract](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/packages/gateway-protocol/src/startup-unavailable.ts#L1-L13) distinguish startup/admission and retry hints | Reuse existing probes and typed rejection. Neither means this caller is admitted; own authenticated hello remains necessary |
| H8: restart/drain | [Restart-unavailable reasons](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/packages/gateway-protocol/src/restart-unavailable.ts#L1-L27) distinguish restarting/suspending | Useful transient classification, not a retained supervisor restart outcome |
| H11: approval | [Approval under the pairing lock](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/infra/device-pairing-approval.ts#L76-L99) returns the mutation result; [device.pair.approve](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/gateway/server-methods/devices.ts#L382-L451) responds with requestId and approved device; the same owner exposes device.pair.list | The response is authoritative, not merely a best-effort event. After lost response, exact current device/role/scopes can be reconciled, but current state does not attribute a historical approval |
| H12: saved versus applied | [config.get projection](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/gateway/config-get-response.ts#L20-L37) separates authored-byte hash, saved resolved configRevisionHash and appliedConfigHash; [live-snapshot application](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/config/runtime-snapshot.ts#L318-L325) updates the applied hash after publishing the in-memory config | Saved/applied comparison already exists. Do not propose it as missing, equate raw hash with resolved revision, or claim application proves every dependent capability ready |
| H12: wizard cancellation | [Wizard status/cancel owner](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/gateway/server-methods/wizard.ts#L196-L229) and [cancellation settlement test](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/gateway/gateway-wizard-cancel.e2e.test.ts#L55-L77) retain admission ownership while the runner drains | Query status and actual settlement. Cancellation acknowledgment is still not config rollback |
| H9/H13: runtime generation | [Kernel boot identity](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/gateway/server-kernel.ts#L136-L186) is generated per boot; [hello schema](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/packages/gateway-protocol/src/schema/frames.ts#L99-L110) includes optional server.bootId and per-connection connId; [snapshot projection](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/gateway/server/health-state.ts#L70-L80) includes appliedConfigHash | Fresh authenticated hello plus appropriate saved/applied read can establish current generation/config/admission. No need for a new generic readiness platform |

### G1: pre-hello generation/provenance binding (H9, part of H7)

**Scoped gap:** [the challenge producer](https://github.com/openclaw/openclaw/blob/e23c587c16f3836e01f2a2c15e5fdaa3e1bc394e/src/gateway/server/connection.ts#L325-L335)
emits nonce, timestamp and capabilities, not bootId/connId. These identities
exist internally earlier, but the hello schema exposes them only after admission.
A consumer cannot use a post-hello boot identity to skip pre-hello provenance
checks safely. A bare new challenge UUID would still not authenticate the peer.

**Suggested producer update:** if early provenance binding is actually needed,
the Gateway connection/challenge owner should define a versioned,
capability-advertised generation binding to the existing trusted transport or
attestation. Bind nonce, boot and connection to that proof and to the final hello;
reject mismatches and replay. Do not simply add an unauthenticated bootId and
call it authority. This needs a producer/security contract decision, not a
Companion shortcut.

**Compatibility/rollout:** producer first; preserve existing full credential and
provenance checks on old/missing capabilities. Do not optimize those checks until
the installed version and trusted binding support it. The exact historical
preauth timeout and its contribution to observed failures remain unverified.

**Acceptance:** replay a challenge, change runtime between challenge and hello,
reuse a port, reorder old-generation messages, and test old/new peers; none may
bypass provenance or admit on an unrelated boot.

### G2: retained approval attribution after a lost response (H11)

**Scoped gap:** the checked devices handler exposes a request-scoped immediate
approval response and current-state listing, not a queryable retained outcome
indexed by the historical approval request. Repeating the identifier is not
proof of durable idempotency, and a later approval/revocation may change the
listed device.

**Suggested producer update, only if historical outcome is required:** extend
the pairing mutation/store owner with a versioned outcome query or idempotent
approval receipt, scoped to authorized caller/resource and exact role/scopes.
Define parameter conflict, concurrent deduplication, retention/expiry and
crash-safe effect/receipt commit (or explicit recoverable intent). Store
credential-free outcome metadata, not copies of device secrets. This is narrower
than introducing generic operations for every RPC.

**Compatibility/rollout:** advertise support before clients depend on it.
Existing clients continue using approve/list. Older producers can establish
current paired state for a next step that accepts it, but must report historical
attribution unavailable rather than blindly replay approval.

**Acceptance:** lose the response after commit, approve concurrently, change
parameters with the same key, crash between effect and receipt, revoke/reapprove,
and query after expiry. Preserve the request's outcome without confusing it with
current permission or granting a revoked capability.

**Not an additional producer gap:** H12/H13's basic saved/applied/boot primitives
already exist. Consumer correlation, freshness and installed-version support
still need tracing. A new atomic "did operation X apply" RPC is not justified
solely because clients must compare existing fields. Historical mutation
attribution or full readiness of sidecars would require additional evidence,
not an unsupported claim that the existing config fields are broken.

## Packaging: receipts exist, but are not an operation history

Pinned `openclaw/openclaw-windows-packaging` main:
`3d70637e7523eac6f38de4f703f86d4db592bf7b`.

| Historical boundary | Current producer evidence | Disposition |
|---|---|---|
| H4-H5: dispatch/lost acknowledgment | [Receipt v1](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/src/OpenClaw.Launcher/Gateway/GatewayLaunchAttempt.cs#L1-L18); [controller pending-before-dispatch and retention](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/src/OpenClaw.Launcher/Gateway/GatewayController.cs#L318-L428) | attemptId and pending state already exist. Unknown dispatch retains state instead of manufacturing failure |
| H5: receipt recovery | [Lost-acknowledgment integration test](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/tests/OpenClaw.Launcher.Tests/Gateway/GatewayLaunchAttemptTests.cs#L145-L251) | Recovery checks exact durable supervisor receipt, schema, attempt and helper. AttemptId maps to session RequestId, not a separate boot identifier |
| H6: listener wait | [WaitForListenerAsync](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/src/OpenClaw.Launcher/Gateway/GatewayController.cs#L507-L540) | TimeProvider-driven polling distinguishes process/listener outcomes and checks its listener budget between completed inspections. An individual inspection can outlive that budget; it is not an enforced per-call or end-to-end deadline and does not prove operator admission |
| H4/H8: cleanup ownership | [Conditional stop](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/src/OpenClaw.Launcher/Gateway/GatewayController.cs#L576-L602) and [replacement/inspection refusal tests](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/tests/OpenClaw.Launcher.Tests/Gateway/GatewayLaunchAttemptTests.cs#L43-L74) | Attempt mismatch and unverified ownership refuse cleanup. Keep these fences, including when inspection fails |

### P1: correlated restart and retained terminal outcomes (H4-H5, H8)

**Scoped gaps:** [RestartAsync](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/src/OpenClaw.Launcher/Gateway/GatewayController.cs#L471-L491)
does not accept/pass a caller attemptId to its new start. The
[state store](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/src/OpenClaw.Launcher/Gateway/GatewayStateStore.cs)
replaces the single current record. Thus current status and a start receipt do
not constitute a retained queryable restart outcome, immutable terminal history,
or attribution after replacement.

**Suggested producer update:** GatewayController/receipt/store owners should
expose a capability-versioned correlated restart, with expected current
generation/authority checked under the receiver's operation gate before stopping.
Carry its operation ID into the replacement start. Retain the minimal scoped
operation outcome with old/new process identity, supervisor transition/cause,
sequence, expiry and typed query results. Keep mutable live health separate.
Define same-key conflicts/deduplication and crash recovery before promising safe
restart replay. A generic durable workflow service is not required.

**Compatibility/rollout:** add a new advertised receipt/restart capability;
preserve receipt v1 for existing start consumers. Do not silently interpret an
old restart as providing a retained receipt. On older versions offer only the
current-state confirmation the actual next step accepts, otherwise block that
transition explicitly.

**Acceptance:** lose restart acknowledgment, crash before/after stop and child
dispatch, concurrently submit the same ID with equal/different parameters,
replace the runtime, query expired results and attempt stale cleanup. No
duplicate restart or stop of a newer owner; terminal outcome remains distinct
from later health.

### P2: explicit supervisor settlement if required (H5, H8)

**Scoped gap:** [SessionSupervisor](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/src/OpenClaw.SessionHost/SessionSupervisor.cs)
records starting/running/exited and waits on the process; the checked owner has
no cooperative cancel/drain request protocol. [Supervisor tests](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/tests/OpenClaw.Launcher.Tests/Session/SessionSupervisorTests.cs#L186-L220)
cover job-owned child termination. Job teardown is not a report that graceful
shutdown, descendant draining and application compensation all completed.

**Suggested producer update:** if the next step requires graceful settlement,
add a versioned scoped stop/cancel request and query result from the session
supervisor owner, distinguishing requested, application exited, descendants
drained and cleanup complete. Persist the terminal reason/result independently
of current health; completion may win cancellation. Preserve bounded hard-stop
policy as a separately reported result, never describe it as graceful success.

**Compatibility/rollout:** negotiate the stronger result; legacy job termination
remains only that guarantee. Require explicit authority for destructive stop.

**Acceptance:** cancel races natural exit, child descendants outlive parent,
supervisor dies mid-stop, cleanup fails/restarts, stale caller submits stop.
Assert exact completion class and no effect on a replacement generation.

### Unknown inspection is not permission to supersede

When LaunchPending survives and inspection returns Unknown, the
[adoption branch](https://github.com/openclaw/openclaw-windows-packaging/blob/3d70637e7523eac6f38de4f703f86d4db592bf7b/src/OpenClaw.Launcher/Gateway/GatewayController.cs#L318-L346)
can retain the old attempt rather than dispatch a fresh launch. This conservative
behavior is not evidence that no child exists. Do not clear the receipt after
N failed inspections or add force-replay as self-healing. A useful targeted
producer regression would inject inspection errors, recover the exact receipt
when possible and otherwise assert explicit indeterminate state and no duplicate
dispatch. A stronger recovery API must establish current receiver-side authority
and process identity before replacement.

H8 crash/exit cause retention across all supervisor paths and cleanup of request
files after external supervisor termination were not fully established here.
They remain test/research unknowns, not demonstrated installed defects. H10 and
H14 remain Companion-owned as described above; no upstream phase-event system
or Gateway-owned Companion publication transaction is proposed.
