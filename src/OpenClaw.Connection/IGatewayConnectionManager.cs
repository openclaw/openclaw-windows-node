using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Single owner of the complete connection lifecycle for the active gateway.
/// Manages operator connection, node connection, credential resolution,
/// state transitions, and diagnostics.
/// </summary>
public interface IGatewayConnectionManager : IDisposable
{
    // ─── State ───
    GatewayConnectionSnapshot CurrentSnapshot { get; }
    string? ActiveGatewayUrl { get; }

    // ─── Events ───
    event EventHandler<GatewayConnectionSnapshot> StateChanged;
    event EventHandler<ConnectionDiagnosticEvent> DiagnosticEvent;
    event EventHandler<OperatorClientChangedEventArgs> OperatorClientChanged;

    // ─── Lifecycle ───
    Task ConnectAsync(string? gatewayId = null);
    Task DisconnectAsync();
    Task ReconnectAsync();
    Task SwitchGatewayAsync(string gatewayId);

    /// <summary>
    /// Drive the node connection for the active gateway and await its terminal state.
    /// Operator must already be Connected (caller responsibility — usually the easy-button
    /// setup engine, which only invokes this after PairOperator completes).
    /// <para>
    /// Behavior:
    ///   - If the node is already Connected + Paired, returns immediately.
    ///   - Otherwise calls the internal node-start path (bypassing the
    ///     auto-start <c>shouldStartNodeConnection</c> gate) and waits for the
    ///     manager's snapshot to reach <c>NodeState=Connected, NodePairingStatus=Paired</c>.
    ///   - Throws <see cref="InvalidOperationException"/> if the operator is not connected
    ///     or there is no node connector wired.
    ///   - Throws <see cref="InvalidOperationException"/> on terminal node failure
    ///     (Error / PairingRejected) with the snapshot's error message.
    ///   - Throws <see cref="TimeoutException"/> after the default 35s window if the
    ///     caller did not pass a cancellation token; respects the caller's token otherwise.
    /// </para>
    /// </summary>
    Task EnsureNodeConnectedAsync(CancellationToken cancellationToken = default);

    // ─── Setup ───
    Task<SetupCodeResult> ApplySetupCodeAsync(string setupCode);
    Task<SetupCodeResult> ConnectWithSharedTokenAsync(string gatewayUrl, string token, SshTunnelConfig? sshTunnel = null);

    // ─── Operator Client Access ───
    /// <summary>
    /// The active operator client for data requests. Null when disconnected.
    /// </summary>
    IOperatorGatewayClient? OperatorClient { get; }

    // ─── Diagnostics ───
    ConnectionDiagnostics Diagnostics { get; }
}
