using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

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

    // ─── Lifecycle ───
    Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath, bool useV2Signature = false);
    Task DisconnectAsync();
}
