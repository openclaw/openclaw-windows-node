namespace OpenClawTray.Services;

/// <summary>
/// Result of an operator connection attempt to a gateway.
/// </summary>
public enum GatewayOperatorConnectionStatus
{
    Connected,
    PairingRequired,
    AuthFailed,
    Timeout,
    Failed,
}

public sealed record GatewayOperatorConnectionResult(
    GatewayOperatorConnectionStatus Status,
    string? ErrorMessage = null,
    string? PairingRequestId = null);

/// <summary>
/// Abstraction for connecting as an operator to a gateway.
/// Implemented by <see cref="ConnectionManagerOperatorConnector"/>.
/// </summary>
public interface IGatewayOperatorConnector
{
    Task<GatewayOperatorConnectionResult> ConnectAsync(
        string gatewayUrl, string token,
        bool tokenIsBootstrapToken = false,
        CancellationToken cancellationToken = default);

    Task<GatewayOperatorConnectionResult> ConnectWithStoredDeviceTokenAsync(
        string gatewayUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstraction for connecting a Windows node to a gateway.
/// Implemented by <see cref="ConnectionManagerWindowsNodeConnector"/>.
/// </summary>
public interface IWindowsNodeConnector
{
    Task ConnectAsync(
        string gatewayUrl, string token,
        string? bootstrapToken,
        CancellationToken cancellationToken = default);
}
