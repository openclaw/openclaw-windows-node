using OpenClaw.Shared;

namespace OpenClawTray.Services.Connection;

/// <summary>
/// Lightweight node connector that creates and manages a WindowsNodeClient.
/// Capability setup (canvas, screen capture, etc.) is handled by NodeService,
/// which has WinUI dependencies and remains in App.xaml.cs for now.
/// </summary>
public sealed class NodeConnector : INodeConnector
{
    private readonly IOpenClawLogger _logger;
    private readonly ConnectionDiagnostics? _diagnostics;
    private WindowsNodeClient? _client;
    private bool _disposed;

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;

    public NodeConnector(IOpenClawLogger logger, ConnectionDiagnostics? diagnostics = null)
    {
        _logger = logger;
        _diagnostics = diagnostics;
    }

    public bool IsConnected => _client?.IsConnected ?? false;
    public PairingStatus PairingStatus => _client switch
    {
        null => PairingStatus.Unknown,
        { IsPaired: true } => PairingStatus.Paired,
        { IsPendingApproval: true } => PairingStatus.Pending,
        _ => PairingStatus.Unknown
    };
    public string? NodeDeviceId => _client?.ShortDeviceId;
    public NodeConnectionMode Mode { get; private set; } = NodeConnectionMode.Disabled;

    /// <summary>The underlying node client, for capability registration by NodeService.</summary>
    public WindowsNodeClient? Client => _client;

    public async Task ConnectAsync(string gatewayUrl, GatewayCredential credential, string identityPath, bool useV2Signature = false)
    {
        if (_disposed) return;

        DisconnectInternal();

        Mode = NodeConnectionMode.Gateway;
        _logger.Info($"[NodeConnector] Connecting to {gatewayUrl}");

        // Use a diagnostic tee logger so node handshake logs appear in the Connection Status timeline
        IOpenClawLogger nodeLogger = _diagnostics != null
            ? new DiagnosticTeeLogger(_logger, _diagnostics)
            : _logger;

        _client = new WindowsNodeClient(
            gatewayUrl,
            credential.IsBootstrapToken ? "" : credential.Token,
            identityPath,
            nodeLogger,
            bootstrapToken: credential.IsBootstrapToken ? credential.Token : null);

        // Share v2 signature flag from operator — avoid wasting a roundtrip on v3
        if (useV2Signature)
            _client.UseV2Signature = true;

        // CRITICAL: fire ClientCreated BEFORE await _client.ConnectAsync() so subscribers
        // (NodeService) can register capabilities synchronously. WindowsNodeClient
        // serializes _registration.Capabilities/Commands into the outbound "connect"
        // message during the connect handshake — registering after that point means
        // the gateway sees an empty caps array for this session.
        try
        {
            ClientCreated?.Invoke(
                this,
                new NodeClientCreatedEventArgs(
                    _client,
                    credential.IsBootstrapToken ? null : credential.Token));
        }
        catch (Exception ex)
        {
            _logger.Warn($"[NodeConnector] ClientCreated handler threw: {ex.Message}");
        }

        _client.StatusChanged += (s, e) => StatusChanged?.Invoke(this, e);
        _client.PairingStatusChanged += (s, e) => PairingStatusChanged?.Invoke(this, e);

        try
        {
            await _client.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.Error($"[NodeConnector] Connect failed: {ex.Message}");
        }
    }

    public Task DisconnectAsync()
    {
        DisconnectInternal();
        return Task.CompletedTask;
    }

    private void DisconnectInternal()
    {
        var old = _client;
        _client = null;
        if (old != null)
        {
            try { old.Dispose(); }
            catch (Exception ex) { _logger.Warn($"[NodeConnector] Dispose error: {ex.Message}"); }
        }
        Mode = NodeConnectionMode.Disabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisconnectInternal();
    }
}
