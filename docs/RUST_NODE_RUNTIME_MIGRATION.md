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

## Intended follow-up slices

1. Define the versioned, authenticated local IPC messages against the shared
   OpenClaw node lifecycle fixtures.
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
- It does not add commands or change native capability ownership.
- It does not move the operator or MCP roles into Rust.

This separation lets the Windows Companion and the Windows tray share an
OpenClaw-owned Rust runtime while preserving the app-specific Windows surfaces.
