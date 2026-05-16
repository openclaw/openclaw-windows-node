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
