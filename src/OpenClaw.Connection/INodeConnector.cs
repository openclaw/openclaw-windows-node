using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Manages the node-side connection for a given gateway.
/// Owns the WindowsNodeClient lifecycle but delegates capability
/// setup to NodeService (which has WinUI dependencies).
/// </summary>
public interface INodeConnector : IDisposable
{
    // ─── State ───
    bool IsConnected { get; }
    PairingStatus PairingStatus { get; }
    string? NodeDeviceId { get; }
    NodeConnectionMode Mode { get; }

    // ─── Events ───
    event EventHandler<ConnectionStatus> StatusChanged;
    event EventHandler<PairingStatusEventArgs> PairingStatusChanged;
    event EventHandler<DeviceTokenReceivedEventArgs> DeviceTokenReceived;

    /// <summary>
    /// Raised right after a new <see cref="WindowsNodeClient"/> is constructed
    /// but BEFORE its <c>ConnectAsync()</c> call. Subscribers (typically
    /// <c>NodeService</c>) must register the node's capabilities on the new
    /// client synchronously so the outbound "connect" handshake includes
    /// populated <c>caps</c>/<c>commands</c> arrays - otherwise the gateway
    /// sees the node as having no advertised commands.
    ///
    /// Fires on first connect AND on every reconnect (the connector destroys
    /// and re-creates the client on every <see cref="ConnectAsync"/>).
    /// </summary>
    event EventHandler<NodeClientCreatedEventArgs> ClientCreated;

    // ─── Lifecycle ───
    Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath, bool useV2Signature = false);

    /// <summary>
    /// Starts a cancellable node connection attempt. Implementations must stop the
    /// superseded attempt and must not forward events from its retired client.
    /// </summary>
    Task ConnectAsync(
        string gatewayUrl,
        GatewayCredential credential,
        string identityPath,
        bool useV2Signature,
        CancellationToken cancellationToken);

    Task DisconnectAsync();
}

/// <summary>
/// Optional telemetry milestones exposed by production node connectors.
/// </summary>
public interface INodeConnectorTelemetryEvents
{
    event EventHandler TransportConnected;
    event EventHandler<GatewayErrorKind> ConnectionFailure;
}

public sealed class NodeClientCreatedEventArgs : EventArgs
{
    public NodeClientCreatedEventArgs(WindowsNodeClient client, string? bearerToken)
    {
        Client = client;
        BearerToken = bearerToken;
    }

    public WindowsNodeClient Client { get; }
    public string? BearerToken { get; }
}
