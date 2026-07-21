# Telemetry conventions

OpenClaw telemetry exists to help users and developers diagnose the companion app, Windows node integration, gateway connectivity, and related setup flows. It must not become background consumer tracking.

Telemetry support should be explicit, reviewable, and safe by default. A change that adds new exported telemetry is a product and privacy decision, not just an implementation detail.

## User control

- Export must be disabled by default.
- Export must only start after the user configures an endpoint.
- An empty endpoint must mean no OpenTelemetry export.
- Export must go only to the endpoint the user configured.
- UI copy should describe telemetry as diagnostics/observability, not analytics.

## Signal ownership

OpenClaw uses shared instrumentation names so traces, metrics, and logs can be correlated consistently:

- tray service name: `openclaw-windows-tray`
- Windows node service name: `openclaw-windows-node`
- shared activity source name: `openclaw`
- shared meter name: `openclaw`

The tray app owns OpenTelemetry SDK and exporter setup. Shared libraries may define instrumentation helpers using `System.Diagnostics.ActivitySource`, `System.Diagnostics.Activity`, and `System.Diagnostics.Metrics`, but must not take a dependency on OpenTelemetry SDK or exporter packages.

## Data boundary

Telemetry fields should be low-cardinality operational diagnostics:

- component or operation names
- protocol choices
- coarse status and outcome values
- durations and counts
- coarse error categories or exception type names

Telemetry must not include:

- user prompts, chat contents, or document contents
- screenshots, camera frames, audio, clipboard contents, or raw UI text
- file contents or raw document text
- credentials, API keys, gateway tokens, bootstrap tokens, or device tokens
- full command input/output unless a specific command contract has been reviewed for safe export
- arbitrary existing local logs exported wholesale

When in doubt, do not export the field. Prefer a coarse category over a raw value.

## Traces

Use traces for bounded operations where duration and outcome matter. Span names should be stable operation names, not dynamically generated strings. Do not put user input into span names.

Recommended span attributes:

- `openclaw.source`
- `openclaw.outcome`
- `openclaw.status`
- `openclaw.reason`
- `openclaw.error.category`
- `error.type`

Use the named constants in `OpenClawTelemetryTagKey` for shared attributes instead of duplicating string literals. Use local span attributes only when the values are reviewed as safe and useful for diagnostics.

## Metrics

Use metrics for counts and distributions that remain meaningful when aggregated. Metric names should be stable and low cardinality. Metric tags must follow the same data boundary as trace attributes.

Do not use metric tags for identifiers that can explode cardinality, such as user IDs, file paths, URLs with user-controlled path segments, device IDs, or per-request IDs.

## Logs

OpenTelemetry logs should be structured and allowlisted. Exported logs should use explicit safe attributes instead of relying on formatted message text.

The OpenTelemetry log pipeline should not export:

- formatted messages that may contain interpolated values
- logging scopes
- arbitrary existing local file logs
- categories outside reviewed telemetry namespaces

If a new log category should be exported, add it deliberately and review the structured fields it can emit.

## Gateway connection lifecycle

The tray exports gateway lifecycle diagnostics when an endpoint is configured:

- operator connect traces: `openclaw.connection.operator.connect` and
  `openclaw.connection.operator.reconnect`
- Windows node connect traces: `openclaw.connection.node.connect` and
  `openclaw.connection.node.reconnect`
- coarse operator phase spans:
  `openclaw.connection.operator.prepare`,
  `openclaw.connection.operator.transport`, and
  `openclaw.connection.operator.handshake`
- coarse Windows node phase spans:
  `openclaw.connection.node.prepare`,
  `openclaw.connection.node.transport`, and
  `openclaw.connection.node.handshake`
- metrics: `openclaw.connection.attempts`,
  `openclaw.connection.attempt.duration`, and
  `openclaw.connection.state.transitions`
- structured state logs in the `OpenClaw.Telemetry.Connection` category

Lifecycle attributes are limited to role, operation, outcome, coarse error
category, and finite operator/node/overall states. Gateway URLs, IDs, device
IDs, pairing request IDs, credentials, error messages, and diagnostic-ring
text are not exported.

The operator phase spans distinguish local credential/client/tunnel preparation,
WebSocket transport establishment, and the gateway challenge/hello handshake.
The Windows node initiates its gateway connection: its prepare span includes
credential resolution, client creation, and synchronous capability registration;
its transport span covers the outbound WebSocket; and its handshake span covers
the gateway's `connect.challenge`, the signed connect request, and `hello-ok`.

A node attempt succeeds only after `hello-ok` yields connected and paired
readiness. Pending approval completes the attempt as `pairing_required`; human
approval wait time is not included in an open span. If the existing node client
later begins automatic transport recovery, the actual retry is recorded as
`openclaw.connection.node.reconnect` beginning with the transport phase.
Manager-driven starts, including the fresh connection after approval, remain
`openclaw.connection.node.connect`.

An attempt with outcome `superseded` was replaced by a newer local lifecycle
request before it completed. This is not a gateway or authentication failure.
It exists to make overlapping connection orchestration visible instead of
silently dropping work that had already started. A short `superseded` span
followed by a normal connection span commonly means an automatic or previously
queued start raced with a newer explicit start; the replacement attempt owns the
eventual connection result.

Pairing and classified gateway failures complete from their specific events
before generic connection status handling. If an active attempt instead ends
with an unclassified `Disconnected` status, telemetry uses `server_close` as a
finite, reasonless fallback because that status carries no close cause.
`Disconnected` covers both orderly remote closes and premature transport loss,
so `server_close` does not prove that the gateway intentionally closed the
connection. Other network failures report `Error` and use
`network_unreachable`; this fallback can therefore be less specific without
changing connection behavior.

The phase spans intentionally do not trace signing, serialization, response
parsing, capability details, or token persistence as separate operations.

## Chat lifecycle

The tray exports native chat lifecycle diagnostics when an endpoint is configured:

- traces: `openclaw.chat.turn`, `openclaw.chat.queue.wait`, `openclaw.chat.send`,
  `openclaw.chat.response.wait`, `openclaw.chat.response.receive`,
  `openclaw.chat.history.load`, and `openclaw.chat.history.backfill`
- counters: `openclaw.chat.turns`, `openclaw.chat.send.attempts`,
  `openclaw.chat.history.loads`, `openclaw.chat.history.backfills`, and
  `openclaw.chat.remote_turns.dropped`, and
  `openclaw.chat.terminal_events.dropped`
- duration histograms: `openclaw.chat.turn.duration`,
  `openclaw.chat.queue.wait.duration`, `openclaw.chat.send.duration`,
  `openclaw.chat.response.wait.duration`,
  `openclaw.chat.response.receive.duration`,
  `openclaw.chat.history.load.duration`, and
  `openclaw.chat.history.backfill.duration`

A local turn starts when the tray admits a valid request to direct dispatch or its
local queue. An observed remote turn starts at a gateway lifecycle start carrying
a run ID. Turn correlation uses message, run, and thread identifiers only inside
the process; those identifiers are never attached to exported signals. Each turn
span is explicitly created as a root, while each sampled local send attempt is
explicitly parented to its turn.

Turn completion is exactly once. Assistant final, lifecycle end, lifecycle error,
send rejection, queue cancellation, explicit abort, reset/supersession,
disconnect, and disposal race through an atomic tracker; the first applicable
terminal transition removes correlation state, and later duplicate signals are
ignored. Assistant-final events do not contain a run ID, so the provider captures
the active run under its existing state lock before completing telemetry. A remote
lifecycle start without a run ID is not traced using an unsafe thread fallback;
it increments `openclaw.chat.remote_turns.dropped` with the finite reason
`missing_run_id`. A missing-run start for an already-dispatched local turn is not
misclassified as a dropped remote turn.

Terminal lifecycle events are never matched by thread alone. If a terminal event
has no run ID or conflicts with the active run, it cannot complete a potentially
newer turn. The provider drops malformed lifecycle and legacy job terminals
before they can clear active-run, timeline, queue, or telemetry state, logs a
content-free warning, and increments `openclaw.chat.terminal_events.dropped`.
A later terminal carrying the exact active run ID can still complete the turn;
otherwise unresolved turns remain eligible for safe reset, disconnect, or
disposal cleanup. Assistant-final chat events are separate: their protocol shape
does not carry a run ID, so the provider captures the authoritative active run
under its state lock rather than accepting a thread-only agent terminal.

Each `openclaw.chat.send` span represents one `chat.send` RPC attempt. A valid
retryable deferral is `outcome=success` with admission status `deferred`, because
the RPC completed and returned a recognized decision. Local requeue is not a
separate exported admission status. Accepted responses use `accepted`; terminal
rejection, cancellation, and exceptions use `rejected`, `canceled`, and
`exception`. Unknown values map to `other`.

Response timing is split into two sibling child spans under the turn:

- `openclaw.chat.response.wait` starts at accepted local admission or observed
  lifecycle start and ends at the first recognized assistant, reasoning, or tool
  output.
- `openclaw.chat.response.receive` starts at that first inbound event and ends
  with the turn's authoritative terminal transition.

If a turn terminates before visible output, the wait span closes with
`openclaw.chat.response.first_output=none` and no receive span is emitted.
Repeated chunks do not create additional spans. Phase duration metrics are
recorded at turn completion so they carry the final bounded turn outcome. A wait
span that reaches first output reports its own phase outcome as `success`; its
duration metric still uses the enclosing turn's final outcome for aggregation.
Output received before accepted admission or lifecycle start does not synthesize
a wait or receive phase.
Unknown admission statuses, routine status/error events, and unknown future
event types do not start or transition response phases. The `other` output value
is reserved for future event types only after they are explicitly reviewed and
classified as visible response output.

Each contiguous local queue or requeue period emits an
`openclaw.chat.queue.wait` sibling span under the turn. A segment that reaches
dispatch completes with `outcome=success`; a segment still queued when the turn
terminates uses the turn's final outcome. Deferred sends therefore show multiple
queue-wait spans rather than one span that incorrectly includes intervening send
attempts.

The queue-wait duration metric remains cumulative across all queue/retry segments.
The tray adds each segment when dispatch begins and emits the total when the turn
completes so it can carry the final outcome. Direct sends accepted on their first
attempt emit neither queue-wait spans nor queue-wait measurements. Consequently,
the metric timestamp is the turn completion time, not the instant queue congestion
occurred.

Full transcript loads and targeted remote-message backfills are separate
operations. Full loads use source `initial` for the first load of that transcript
in the current connection generation or `forced` when deliberately bypassing
the loaded-history cache. Backfills use the finite reason `remote_turn` or
`reset_reconciliation`. Receiving `sessions.list`
hydrates session-picker metadata only and does not emit a history-load operation.
At startup the tray loads the current/default session transcript; another
session's transcript is loaded when that session is selected. Reconnect
invalidates transcript freshness and refreshes the selected session through the
same demand-driven path rather than loading every known session. Explicit
single-session reset, abort, and remote-turn reconciliation may still issue
targeted history requests.

Chat attributes are restricted to:

- `openclaw.source`: `local`, `remote`, `initial`, or `forced`, as applicable
- `openclaw.outcome`: `success`, `failure`, or `canceled`
- `openclaw.reason`: `assistant_final`, `lifecycle_end`, `lifecycle_error`,
  `send_rejected`, `queued_canceled`, `abort_requested`, `reset`, `superseded`,
  `disconnected`, `disposed`, or `other`
- `openclaw.chat.admission.status`: `accepted`, `deferred`, `rejected`,
  `canceled`, `exception`, or `other`
- `openclaw.chat.backfill.reason`: `remote_turn` or `reset_reconciliation`
- `openclaw.chat.remote_turn.drop.reason`: `missing_run_id`
- `openclaw.chat.terminal_event.drop.reason`: `missing_run_id` or
  `mismatched_run_id`
- `openclaw.chat.response.first_output`: `none`, `assistant`, `reasoning`,
  `tool`, or `other`
- `error.type`: exception type only, never the exception message

Chat telemetry does not export prompts, responses, transcript contents, IDs,
model/provider names, attachment metadata, filenames, tool names, token usage,
URLs, error messages, or local chat log text. No chat log category is added to
the OpenTelemetry log allowlist.

## Windows node tool calls

Gateway `node.invoke` and local MCP `tools/call` share one node-side telemetry
contract:

- root trace: `openclaw.node.tool.invoke`
- dispatch child: `openclaw.node.tool.execute`
- `system.run` children of the dispatch span:
  `openclaw.node.tool.system_run.authorize` and
  `openclaw.node.tool.system_run.run`
- counter: `openclaw.node.tool.invocations`
- duration histogram: `openclaw.node.tool.duration`
- dropped failure-log counter: `openclaw.node.tool.logs.dropped`
- failure/cancellation log category: `OpenClaw.Telemetry.NodeTool`

The root begins when a recognized invocation reaches node dispatch and ends
after its gateway or MCP response is delivered or delivery fails. Gateway
background execution and MCP HTTP delivery use explicit activity contexts; they
do not depend on ambient activity flowing across those boundaries. The
invocation tracker uses one monotonic clock for the root and duration metric and
completes exactly once.

Reviewed attributes are:

- `openclaw.node.tool.name`: a registered command or `unknown`
- `openclaw.node.tool.transport`: `gateway` or `mcp`
- `openclaw.outcome`: `success`, `failure`, or `canceled`
- `openclaw.error.category`: a finite typed category
- `openclaw.node.tool.system_run.approval.pipeline`: `legacy` for the existing
  approval policy or `v2` for the opt-in direct-argv approval pipeline; present
  only for `system.run` traces and failure/cancellation logs
- `openclaw.node.tool.sandbox.requested`: whether sandboxing was configured
- `openclaw.node.tool.sandbox.applied`: whether the command was known to run
  inside the sandbox; omitted when an infrastructure failure makes that unknown
- `openclaw.node.tool.sandbox.provider`: `mxc` when MXC was selected
- `openclaw.node.tool.sandbox.technology`: `windows_appcontainer` for the
  currently wired MXC backend
- `openclaw.node.tool.sandbox.denial.reason`: a finite host-side pre-execution
  reason: `direct_argv_unsupported`, `custom_environment_unsupported`,
  `effective_shell_changed`, `fallback_shell_unapproved`, or
  `unsupported_sandbox_request`
- `openclaw.node.tool.sandbox.fallback.target`: `unsandboxed` when an unavailable
  MXC backend caused compatibility fallback
- `openclaw.node.tool.sandbox.fallback.reason`: `mxc_unavailable` for that
  fallback
- `error.type`: exception type only

Failure categories are `invalid_request`, `unsupported_command`, `node_busy`,
`permission_denied`, `exec_policy_denied`, `command_unavailable`,
`capability_unavailable`, `sandbox_denied`, `sandbox_unavailable`,
`sandbox_failure`, `command_failed`, `timeout`, `capability_failure`,
`transport_failure`, `internal_failure`, and `other`. Metrics use only command,
transport, outcome, and error category.

Classification uses typed control flow only. An explicit capability diagnostic
wins, followed by typed command-runner diagnostics; an otherwise unsuccessful
capability response becomes `capability_failure`. Error messages, exception
messages, command output, and payload text are never parsed to infer a category.
V2 exec approval results map as follows:

- `SecurityDeny`, `AskDeny`, `AllowlistMiss`, and `UserDenied`:
  `exec_policy_denied`
- `ValidationFailed`: `invalid_request`
- `ResolutionFailed`: `command_unavailable`
- `Unavailable`: `capability_unavailable`
- `InternalError`: `internal_failure`
- `Allow`: no approval failure category

Telemetry does not change protocol semantics. In particular, a nonzero or
timed-out `system.run` remains a successful gateway/MCP RPC whose payload has
`success=false`; telemetry records `command_failed` or `timeout`. A contained
nonzero exit is `command_failed` with `sandbox.requested=true` and
`sandbox.applied=true`, not a sandbox denial. The current MXC result contract
cannot distinguish a command failure caused by an in-container policy from
other nonzero process exits without unsafe message parsing or a sandbox
protocol change.

The tray exports one structured log only for a failed or canceled invocation.
Forwarding uses a nonblocking queue capped at 256 entries. Full queues drop the
newest entry and increment `openclaw.node.tool.logs.dropped` with
`openclaw.node.tool.log.drop.reason=queue_full`. Entries are stamped with the
current exporter generation and are discarded rather than sent to a replacement
sink. Disabled-endpoint and stale-generation drops are expected lifecycle
behavior and do not increment the dropped-log counter.

Node tool telemetry never exports request, node, session, or gateway IDs;
arguments; command lines; shell input; paths; environment names or values;
payloads; stdout or stderr; error or exception messages; credentials; URLs; or
gateway details. Unsupported caller-provided command names are reported as
`unknown`, preventing user-controlled metric cardinality.

## Endpoint handling

The endpoint setting is a collector endpoint, not a credential or request-parameter store. Accept plain `http` and `https` collector URLs with optional path prefixes. Reject URLs with embedded user info, query strings, or fragments.

Examples of acceptable endpoint shapes:

```text
http://localhost:4317
https://collector.example.com:4318
https://collector.example.com/otlp
```

Supported OTLP protocols:

- OTLP/gRPC
- OTLP/HTTP protobuf

For gRPC, pass the configured endpoint to the exporter unchanged.

For HTTP/protobuf, treat the configured endpoint as a collector base URL and derive signal-specific paths:

- traces: `/v1/traces`
- metrics: `/v1/metrics`
- logs: `/v1/logs`

If users need authenticated collectors, prefer a local collector or proxy that handles upstream authentication. Direct authenticated exporter support should be added as an explicit feature with appropriate secret storage and redaction, not by embedding credentials in the endpoint URL.

Plain `http://` endpoints are useful for local development collectors such as `localhost`. Prefer `https://` for remote collectors unless the user intentionally controls and trusts the network path.

Automatic startup and settings application should deduplicate an unchanged endpoint. The diagnostics UI may offer an explicit resend action so users can repeat the bounded probe after a collector outage; local SDK flush completion must not be described as collector acknowledgement.

## Adding new instrumentation

Before adding new exported telemetry:

1. Identify the diagnostic question the signal answers.
2. List every attribute/tag/log field and classify why it is safe.
3. Prefer enums, constants, or reviewed helper APIs for shared names.
4. Add focused tests for names, outcomes, and filtering behavior.
5. Update this document if the change creates a new convention or expands the telemetry boundary.
6. Include validation and real behavior proof when the change affects user-visible configuration or exporter behavior.
