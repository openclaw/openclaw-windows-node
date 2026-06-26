# Gateway Protocol Drift Guard

> Source of truth: [`openclaw/openclaw` — `packages/gateway-protocol/src/schema/{sessions,commands}.ts`](https://github.com/openclaw/openclaw/tree/main/packages/gateway-protocol/src/schema)

## Why this exists

The Windows companion can silently drift from the upstream OpenClaw gateway
protocol — when methods or session fields change upstream but the Windows client
(`OpenClawGatewayClient`) keeps sending the old shapes. Because the gateway
tolerates unknown/extra fields, such drift ships without any test failing.

The **drift guard** pins a hand-maintained mirror of the upstream gateway protocol
for the `sessions` / `files` / `commands` / compaction surface and fails the test
suite whenever the typed client and the pinned schema diverge.

The typed client implements the richer protocol surface —
`commands.list`, the extended `sessions.patch` field set, `sessions.files.list`/
`get`, and `sessions.compaction.list`/`get`/`branch`/`restore` — across
`OpenClawGatewayClient.Protocol.cs` and the DTO/payload builders in
`GatewayProtocolModels.cs`. This guard pins that whole surface.

It is intentionally **deterministic and repo-contained**: it requires neither a
clone of the upstream `openclaw/openclaw` repo nor any network access, so it runs
in the normal `OpenClaw.Shared.Tests` suite. It **complements** the behavioural
parser tests in `GatewayProtocolModelsTests` (which parse sample JSON and assert
DTO mapping) by statically guarding the method / request-field / response-envelope
*surface*, so an upstream rename or a dropped API is caught even when the parser
unit tests still pass on their fixed sample payloads.

## Files

| File | Role |
| --- | --- |
| `tests/OpenClaw.Shared.Tests/Protocol/gateway-protocol-snapshot.json` | The pinned canonical schema mirror. **Edit this when upstream changes.** |
| `tests/OpenClaw.Shared.Tests/Protocol/GatewayProtocolDriftTests.cs` | The guard. Cross-checks the snapshot against `OpenClawGatewayClient*.cs` + `GatewayProtocolModels.cs`. |

## What the guard checks

1. **Method surface** (`ScopedMethodSurface_matches_snapshot`)
   - Every in-scope method the client actually *wires* — request methods it
     **dispatches** (a method literal passed as a call argument to any dispatcher,
     e.g. `TrySendTrackedRequestAsync("sessions.patch", …)`,
     `TryRequestPayloadAsync("commands.list", …)`, or the private
     `MutateCompactionAsync("sessions.compaction.branch", …)` forwarder), plus
     notifications it **handles** via `case "…":` — must be pinned in the snapshot.
   - Every method pinned `windowsUsage: "used"` must still be wired in the way its
     `kind` implies: a **request** must still be *dispatched* (a leftover `case`
     label, a `method is "…"` predicate, or a log sentence is not enough), a
     **notification** must still be *handled*. Dropping one is the classic
     drift regression.
   - A `planned` method must **not** already be wired — if the client starts using
     it, the guard forces you to flip it to `used` and verify its response shape.
     (None currently — the typed client wires the whole surface.)
2. **`sessions.list` response shape** (`SessionsList_responseShape_matches_snapshot`)
   - The session wire-fields the client parses (scoped to the two session-parsing
     methods) must exactly equal the snapshot's `responseFields` (both directions).
3. **Request parameters** (`RequestParameterNames_arePresentInConstructionRegion`)
   - For each `used` request method, the set of wire keys the client actually
     constructs in the request-construction region must **exactly equal** the pinned
     `requestFields` (modulo a per-method `allowedExtraRequestFields` allowlist).
     The comparison is **bidirectional**:
     - **missing** (pinned − constructed): the client no longer sends a field the
       schema expects — e.g. renaming a wire key while the old name survives as a
       local/parameter (`new { sessionKey = key }` when `key` is still pinned) is
       caught, because the check compares *constructed keys*, not token presence.
     - **unexpected** (constructed − pinned − allowed): the client still constructs
       a stale/extra key that is no longer in the schema. Strict gateways
       (`additionalProperties:false`) reject the whole request on an unknown field,
       so this is real drift. Set `allowedExtraRequestFields` on a method to declare
       an intentional client-only extra the gateway tolerates.
   - A constructed wire key is an anonymous-object member (`new { sessionKey = key,
     path }`) or a dictionary string key (`payload["model"] = …`) in the region.
   - The region is the enclosing block of an actual dispatch call, or — when the
     payload is built by a helper rather than inline — the explicit
     `requestFieldsSource` builder body (the extended `sessions.patch` fields are
     checked against `SessionPatch.ToPayload`; the compaction `branch`/`restore`
     params against `MutateCompactionAsync`). `requestFieldsSource` must resolve
     **uniquely** to a body that actually constructs keys, and a dispatch from an
     expression-bodied member (where the region would degrade to the whole type)
     **fails closed** asking for a `requestFieldsSource`.
4. **Response envelope** (`ResponseEnvelopes_areReadByClient`)
   - For each `used` method that pins a `responseEnvelope`, the client must read
     that property — `TryGetProperty("…")` **or** `TryGetArray(payload, "…")` —
     **inside that method's own parser body** (pinned via `responseEnvelopeSource`),
     so one parser reading the same property name (e.g. several read `"checkpoint"`)
     cannot satisfy a different method's envelope. An upstream envelope rename forces
     client reconciliation.
5. **Snapshot integrity** (`Snapshot_isWellFormed_andCoversFoundationShapes`)
   - The snapshot stays well-formed, keeps covering every protocol-foundation shape
     (`commands.list`, `sessions.list`/`patch`/`compact`, `agents.files.list`/`get`,
     `sessions.files.list`/`get`, `sessions.compaction.list`/`get`/`branch`/
     `restore`), and keeps its honesty invariant: `provisionalResponseFields` may
     only be set while `windowsUsage: "planned"`.
6. **Tri-state clear contract** (`TriStateClearContract_isWiredInClient`)
   - Upstream `SessionsPatchParamsSchema` types every `sessions.patch` field as
     `Union([<value>, Null])`, where an explicit null **removes** a session
     override. The client models this with a tri-state `PatchField<T>`: **unset**
     (null reference) → omitted, a **value** → sent (blank string omitted), and
     `SessionPatch.Clear` → **explicit JSON null**. For any method declaring
     `requestFieldSemantics: "tristate-nullable"`, this guard statically verifies
     the builder source still wires the clear→null routing, the blank-omit guard,
     and the value-state gate, and that the `PatchField<T>` type still exposes its
     three states — so a refactor that removes the machinery is caught structurally
     here, independent of the behavioural emission tests. (Runtime emission is
     covered by `GatewayProtocolModelsTests`.)

> Extraction is intent-specific and resilient: request-method literals are read
> from real dispatch call sites (excluding logging sinks) and notifications from
> `case` labels — not comments, `method is "…"` predicates, or log strings. Each
> source is preprocessed into two index-aligned views: a *code* view with comments
> blanked (literal extraction, so commented-out code never counts) and a *masked*
> view with comments **and** string/char literals blanked (brace structure, so
> braces inside literals/comments cannot desync the matcher). A self-check asserts
> the masked view stays brace-balanced.

### Enforced vs. documentation-only fields

Not every field in the snapshot is cross-checked against the client. Refreshers
should know which edits are actually validated:

| Snapshot field | Enforced against the client? |
| --- | --- |
| `methods[].method` (in-scope) | **Yes** — must match what the client dispatches/handles. |
| `windowsUsage` (`used`/`planned`) | **Yes** — drives the dispatch/handle requirement and the planned-not-wired check. |
| `sessions.list.responseFields` | **Yes** — exact set vs. the session parser. |
| `requestFields` of `used` request methods | **Yes** — bidirectional: must exactly equal the wire keys constructed in the region (missing **and** unexpected both fail). |
| `allowedExtraRequestFields` | **Yes** — exempts the listed client-only extras from the "unexpected" direction; everything else still fails. |
| `requestFieldsSource` | **Yes** — must resolve uniquely to a body that constructs keys; that body is the region. |
| `responseEnvelope` of `used` methods | **Yes** — must be read (`TryGetProperty`/`TryGetArray`) by the pinned parser. |
| `responseEnvelopeSource` | **Yes** — required when `responseEnvelope` is pinned; must resolve uniquely to the parser body that reads the envelope (no whole-client fallback). |
| `tristateContract` (markers + state members) | **Yes** — for `requestFieldSemantics: "tristate-nullable"`, the builder source must implement every marker and the `PatchField<T>` type must expose every state. |
| `kind`, `scopePrefixes`, foundation coverage | **Yes** — snapshot-integrity invariants. |
| `itemFields` (per-item descriptor fields) | **No** — documentation-only; behaviour covered by `GatewayProtocolModelsTests` parsers. |
| `_comment`, `_note`, `_itemFieldsNote`, provenance | **No** — documentation-only. |


## Tri-state clear contract (`sessions.patch`)

Upstream `SessionsPatchParamsSchema` types every `sessions.patch` field as
`Union([<value>, Null])`. A field can therefore be in one of three states, and the
Windows client mirrors this with the tri-state `PatchField<T>`:

| State | How the client expresses it | Wire result |
| --- | --- | --- |
| **unset** | leave the `PatchField<T>` default / assign a null reference | field **omitted** from the request |
| **set** | assign a value (`patch.Model = "gpt-5"`) | field sent with the value (a **blank** string is omitted per `NonEmptyString`) |
| **clear** | assign the sentinel (`patch.Model = SessionPatch.Clear`) | field sent as **explicit JSON `null`** (removes the session override) |

The drift guard pins this contract in the snapshot under
`sessions.patch.tristateContract` and checks it statically (Guard 6): the
`SessionPatch` builder must keep the clear→null routing, the blank-omit guard, and
the value-state gate, and `PatchField<T>` must keep its `IsSpecified`/`IsClear`/
`HasValue` states. The actual JSON emission for each state is covered behaviourally
by `GatewayProtocolModelsTests` (`SessionPatch_ToPayload_ClearEmitsExplicitNull*`,
`…MixesSetAndClearAndUnset`, `PatchField_TriStateFlags`) and end-to-end by
`GatewayProtocolLiveRoundTripTests` (a loopback-WebSocket test capturing the real
wire frames) — the guard is the static, upstream-mirrored complement, not a duplicate.

## `windowsUsage` semantics

| Value | Meaning |
| --- | --- |
| `used` | The client dispatches/handles this method today. The guard **requires** it to remain wired up. |
| `planned` | Defined upstream but not yet consumed by the Windows client. The shape is pinned so future adoption matches upstream, but the client is not required to use it yet. (None currently — the typed client wires the whole surface.) |

## How to refresh the snapshot when upstream changes

When the upstream gateway protocol changes (a new `sessions.*` method, a renamed
session field, a new `commands.list` descriptor field, etc.):

1. Open the upstream schema on `main`:
   - `packages/gateway-protocol/src/schema/sessions.ts`
   - `packages/gateway-protocol/src/schema/commands.ts`
2. Update `tests/OpenClaw.Shared.Tests/Protocol/gateway-protocol-snapshot.json`:
   - Add/rename/remove methods under `methods[]`.
   - Update `requestFields` / `responseFields` / `responseEnvelope` to match the
     wire field names. Field/method names may use letters, digits and underscores.
   - When a method's request payload is built by a helper rather than inline at the
     dispatch site (e.g. `SessionPatch.ToPayload`, `MutateCompactionAsync`), point
     `requestFieldsSource` at a unique fragment of that builder's declaration so
     Guard C searches the right body.
   - When a method pins a `responseEnvelope`, also set `responseEnvelopeSource` to a
     unique fragment of the parser body that reads it (Guard D requires it).
   - If upstream changes a field's nullability semantics (e.g. a field becomes
     `Union([<value>, Null])`, making explicit null a "clear" signal), update the
     `tristateContract` markers for that method and confirm the client's tri-state
     `PatchField<T>` builder still implements them (Guard 6).
   - Bump `snapshotUpdated` to today's date and set `upstreamCommit` to the exact
     upstream schema commit SHA you mirrored (so the pin is reproducible/auditable;
     `upstreamRef: "main"` alone is not).
   - For a method upstream added that the Windows client doesn't consume yet, set
     `windowsUsage: "planned"`; pin its shape and, if the response fields are not
     yet verified against upstream, set `provisionalResponseFields: true`.
3. Run the guard:
   ```powershell
   $env:OPENCLAW_REPO_ROOT = (Get-Location).Path
   dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --filter GatewayProtocolDriftTests
   ```
4. If the guard fails, reconcile the typed client (`OpenClawGatewayClient.cs`,
   `OpenClawGatewayClient.Protocol.cs`, and the DTOs in `GatewayProtocolModels.cs` /
   `SessionInfo` in `Models.cs`) with the new schema — that reconciliation is the
   whole point of the guard. Update the snapshot only to reflect upstream, never to
   silence a real client gap.

## Known limitation: snapshot ↔ upstream staleness

Every guard checks the client against the **snapshot**, not against upstream
directly (a deliberate trade-off for the offline/repo-contained guarantee). If
upstream changes and nobody refreshes the snapshot, the tests stay green while the
client silently drifts from real upstream — the original regression class, moved
up one level.

Mitigations:

- The snapshot records `upstreamVerified` (date + the exact `sessions.ts`/`commands.ts`
  blob SHAs the pin was checked against) so each verification is auditable and
  reproducible. **Last verified 2026-06-23** against `openclaw/openclaw` main:
  all enforced request fields / response envelopes / the `sessions.patch` tri-state
  nullable contract matched exactly; the snapshot is a deliberate subset (see the
  `upstreamVerified.result` note for the upstream-only `sessions.patch` fields the
  Windows client does not yet implement).
- Treat a gateway-protocol bump in upstream as a trigger to re-verify and refresh,
  the same way `docs/gateway-node-integration.md` is refreshed.
- Re-verify by fetching `packages/gateway-protocol/src/schema/{sessions,commands}.ts`
  from `openclaw/openclaw` main, diffing against the pinned methods/fields, and
  updating `upstreamVerified` with the new blob SHAs.

## Scope

The guard deliberately covers only the `sessions` / `files` / `commands` /
compaction surface (`scopePrefixes` in the snapshot), which is where the
drift-critical session and command APIs live. Other gateway namespaces
(`cron.*`, `config.*`, `device.pair.*`, …) are out of scope for this guard.
