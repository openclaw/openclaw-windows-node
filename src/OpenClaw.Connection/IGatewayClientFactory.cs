using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Lifecycle interface for a gateway client instance, owned exclusively by the manager.
/// Not exposed to UI consumers. Testable via mock implementations.
/// </summary>
public interface IGatewayClientLifecycle : IDisposable
{
    /// <summary>The underlying client for data access (events, requests). Cast-safe to OpenClawGatewayClient.</summary>
    OpenClawGatewayClient DataClient { get; }

    /// <summary>Raised when the WebSocket transport status changes.</summary>
    event EventHandler<ConnectionStatus> StatusChanged;

    /// <summary>Raised when gateway rejects authentication.</summary>
    event EventHandler<string> AuthenticationFailed;

    /// <summary>Connect the WebSocket and begin the handshake.</summary>
    Task ConnectAsync(CancellationToken ct);
}

/// <summary>
/// Factory for creating configured gateway client instances.
/// Stateless — creates instances but does not manage their lifecycle.
/// </summary>
public interface IGatewayClientFactory
{
    /// <summary>
    /// Create a gateway client for the given URL and credential.
    /// The manager owns the returned lifecycle handle.
    /// </summary>
    IGatewayClientLifecycle Create(
        string gatewayUrl,
        GatewayCredential credential,
        string identityPath,
        IOpenClawLogger logger);
}
