using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared;

/// <summary>
/// Runtime boundary consumed by the Windows capability host.
/// </summary>
/// <remarks>
/// The current implementation is <see cref="WindowsNodeClient"/>. Keeping the
/// capability host behind this contract allows a future OpenClaw Rust sidecar
/// to own Gateway transport and node lifecycle while C# continues to own and
/// execute Windows-native capability handlers.
/// </remarks>
public interface INodeRuntimeClient : IDisposable
{
    bool UseV2Signature { get; set; }
    Func<CancellationToken, Task<ReconnectAuthorizationResult>>?
        HandshakeAuthorizationAsync { get; set; }
    Func<CancellationToken, Task<ReconnectAuthorizationResult>>?
        ReconnectAuthorizationAsync { get; set; }
    bool IsConnected { get; }
    string? NodeId { get; }
    string GatewayUrl { get; }
    IReadOnlyList<INodeCapability> Capabilities { get; }
    bool IsPendingApproval { get; }
    bool IsPaired { get; }
    string ShortDeviceId { get; }
    string FullDeviceId { get; }
    string DisplayName { get; }
    int RegisteredCapabilityCount { get; }
    int RegisteredCommandCount { get; }
    IEnumerable<string> RegisteredCommandsSample { get; }

    event EventHandler<ConnectionStatus> StatusChanged;
    event EventHandler<NodeInvokeCompletedEventArgs> InvokeCompleted;
    event EventHandler<NodeToolTelemetryCompletion> ToolTelemetryCompleted;
    event EventHandler<PairingStatusEventArgs> PairingStatusChanged;
    event EventHandler<JsonElement> HealthReceived;
    event EventHandler<GatewaySelfInfo> GatewaySelfUpdated;
    event EventHandler<DeviceTokenReceivedEventArgs> DeviceTokenReceived;
    event EventHandler TransportConnected;
    event EventHandler<GatewayErrorKind> ConnectionFailure;
    event EventHandler Disposed;

    void RegisterCapability(INodeCapability capability);
    void SetPermission(string permission, bool value);
    /// <summary>
    /// Connects the runtime. Cancellation must promptly abort the in-progress
    /// attempt and make the client safe to retire.
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync();
    Task<bool> SendNodeEventAsync(string eventName, JsonObject payload);
}
