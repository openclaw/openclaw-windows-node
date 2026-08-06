# Rust node runtime migration seam

The Windows node is already the native Windows capability host. Its WinUI,
operator controls, MCP server, approval UX, and Windows-native command handlers
should remain in this repository. The proposed OpenClaw Rust runtime can replace
the duplicated Gateway transport and node lifecycle without replacing those
Windows surfaces.

This change introduces the first executable seam:

- `INodeRuntimeClient` is the contract consumed by the Windows capability host.
- `INodeRuntimeClientFactory` selects the runtime implementation.
- `WindowsNodeClient` remains the default, so production behavior is unchanged.
- `NodeConnector` still owns connection arbitration and ensures capabilities and
  permissions are registered before the selected runtime begins its handshake.
- `NodeCapabilityDispatcher` is the shared Windows-owned execution path for
  command lookup, concurrency, cancellation, telemetry, and completion. The
  C# client now uses it. A Rust adapter is not eligible for runtime selection
  until adapter-level conformance proves it routes every decoded invocation
  through this dispatcher instead of copying capability policy.

The next fork-only evidence slice now implements the Windows half of the shared
sidecar contract without selecting it in production:

- `AuthenticatedSidecarChannel` independently reproduces the Rust framing and
  HMAC vector, enforces directional sequence and generation bounds, and retires
  permanently after inbound validation failure.
- `SidecarSupervisorHandshake` independently reproduces the Rust offer and
  accept vectors and lowers the active frame ceiling to the negotiated limit.
- `WindowsSidecarSupervisor` enforces handshake, one-time immutable
  configuration, and post-configuration message order.
- `WindowsSidecarCapabilityAdapter` requires an unchanged admission before
  dispatch, rejects wrong-node and undeclared work, and routes invocation and
  cancellation only through `NodeCapabilityDispatcher`.
- The copied fixtures are byte-exact evidence from OpenClaw fork PRs #193,
  #194, and #195 at the combined head `5bab2c9ecf6`. They are conformance
  inputs, not a second wire authority.

This proof deliberately does not implement `INodeRuntimeClient` or runtime
selection yet. The current shared messages do not project the complete pairing,
issued-token, health, Gateway-self, reconnect-authorization, and node-event
surface required by that interface. A production adapter also still needs a
verified Rust artifact, process supervision, protected bootstrap and credential
handoff, concrete local IPC, resource bounds, audit correlation, and rollback.

The proof also records one generic compatibility gap: the current Rust
`CommandRuntime` rejects all `system.*` registrations. That is appropriate for
the standalone experimental host, but it prevents the official Windows node's
existing `system.run`, `system.which`, and `system.notify` capabilities from
using the sidecar. Runtime PR3 therefore needs an explicit OpenClaw-authorized
command-namespace mechanism before the Windows adapter can be selected. The
Windows proof fails closed instead of bypassing that restriction.

## Intended follow-up slices

1. ~~Define the versioned, authenticated local IPC messages against the shared
   OpenClaw node lifecycle fixtures.~~ Implemented as fork conformance evidence.
2. Add an opt-in sidecar adapter that implements `INodeRuntimeClient` over that
   versioned, authenticated local IPC protocol. The Rust process owns Gateway
   connection, registration, invoke/result/progress/cancellation, reconnect, and
   runtime lifecycle.
3. Run the C# and Rust implementations through the same registration and
   invocation conformance fixtures, including cancellable blocked-connect and
   adapter-to-dispatcher routing tests. Keep the existing C# runtime as the
   default while the Rust path gathers real Gateway proof.
4. Switch the Windows node role to the Rust adapter behind an explicit rollout
   gate. C# continues to execute Windows-native capabilities and return results
   through the runtime contract.
5. Remove the duplicated C# Gateway node transport only after parity, rollout,
   and rollback criteria are met.

## Non-goals of this slice

- It does not add or vendor a Rust binary.
- It does not change the production runtime selection.
- It does not claim complete `INodeRuntimeClient` lifecycle or pairing parity.
- It does not add commands or change native capability ownership.
- It does not move the operator or MCP roles into Rust.

This separation lets the Windows Companion and the Windows tray share an
OpenClaw-owned Rust runtime while preserving the app-specific Windows surfaces.
