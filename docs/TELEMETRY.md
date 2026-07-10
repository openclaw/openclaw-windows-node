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

## Adding new instrumentation

Before adding new exported telemetry:

1. Identify the diagnostic question the signal answers.
2. List every attribute/tag/log field and classify why it is safe.
3. Prefer enums, constants, or reviewed helper APIs for shared names.
4. Add focused tests for names, outcomes, and filtering behavior.
5. Update this document if the change creates a new convention or expands the telemetry boundary.
6. Include validation and real behavior proof when the change affects user-visible configuration or exporter behavior.
