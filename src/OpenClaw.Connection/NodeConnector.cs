using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Lightweight node connector that creates and manages a WindowsNodeClient.
/// Capability setup (canvas, screen capture, etc.) is handled by NodeService,
/// which has WinUI dependencies and remains in App.xaml.cs for now.
/// </summary>
public sealed class NodeConnector : INodeConnector
{
    private readonly IOpenClawLogger _logger;
    private readonly ConnectionDiagnostics? _diagnostics;
    private readonly SemaphoreSlim _connectSemaphore = new(1, 1);
    private CancellationTokenRegistration _connectionCancellationRegistration;
    private WindowsNodeClient? _client;
    private long _clientGeneration;
    private bool _disposed;

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
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
    public string? NodeDeviceId => _client?.FullDeviceId;
    public NodeConnectionMode Mode { get; private set; } = NodeConnectionMode.Disabled;

    /// <summary>The underlying node client, for capability registration by NodeService.</summary>
    public WindowsNodeClient? Client => _client;

    public Task ConnectAsync(
        string gatewayUrl,
        GatewayCredential credential,
        string identityPath,
        bool useV2Signature = false) =>
        ConnectAsync(gatewayUrl, credential, identityPath, useV2Signature, CancellationToken.None);

    public async Task ConnectAsync(
        string gatewayUrl,
        GatewayCredential credential,
        string identityPath,
        bool useV2Signature,
        CancellationToken cancellationToken)
    {
        if (_disposed) return;

        await _connectSemaphore.WaitAsync(cancellationToken);
        try
        {
            await ConnectCoreAsync(
                gatewayUrl,
                credential,
                identityPath,
                useV2Signature,
                cancellationToken);
        }
        finally
        {
            _connectSemaphore.Release();
        }
    }

    private async Task ConnectCoreAsync(
        string gatewayUrl,
        GatewayCredential credential,
        string identityPath,
        bool useV2Signature,
        CancellationToken cancellationToken)
    {
        if (_disposed) return;

        cancellationToken.ThrowIfCancellationRequested();
        DisconnectCurrentClient();
        var generation = Interlocked.Increment(ref _clientGeneration);

        Mode = NodeConnectionMode.Gateway;
        _logger.Info($"[NodeConnector] Connecting to {gatewayUrl}");

        // Use a diagnostic tee logger so node handshake logs appear in the Connection Status timeline
        IOpenClawLogger nodeLogger = _diagnostics != null
            ? new DiagnosticTeeLogger(_logger, _diagnostics)
            : _logger;

        var client = new WindowsNodeClient(
            gatewayUrl,
            credential.IsBootstrapToken ? "" : credential.Token,
            identityPath,
            nodeLogger,
            bootstrapToken: credential.IsBootstrapToken ? credential.Token : null);
        _client = client;

        // Share v2 signature flag from operator — avoid wasting a roundtrip on v3
        if (useV2Signature)
            client.UseV2Signature = true;

        _connectionCancellationRegistration = cancellationToken.Register(
            () => DisconnectIfCurrent(generation));
        cancellationToken.ThrowIfCancellationRequested();

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
                    client,
                    credential.IsBootstrapToken ? null : credential.Token));
        }
        catch (Exception ex)
        {
            _logger.Warn($"[NodeConnector] ClientCreated handler threw: {ex.Message}");
            _diagnostics?.Record("node", "ClientCreated handler failed; node connection aborted before handshake", ex.Message);
            DisconnectCurrentClient();
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
            return;
        }

        client.StatusChanged += (s, e) =>
        {
            if (IsCurrentClient(s, generation))
                StatusChanged?.Invoke(this, e);
        };
        client.PairingStatusChanged += (s, e) =>
        {
            if (IsCurrentClient(s, generation))
                PairingStatusChanged?.Invoke(this, e);
        };
        client.DeviceTokenReceived += (s, e) =>
        {
            if (IsCurrentClient(s, generation))
                DeviceTokenReceived?.Invoke(this, e);
        };

        try
        {
            await client.ConnectAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"[NodeConnector] Connect failed: {ex.Message}");
        }
    }

    public Task DisconnectAsync()
    {
        DisconnectCurrentClient();
        return Task.CompletedTask;
    }

    private bool IsCurrentClient(object? sender, long generation) =>
        Interlocked.Read(ref _clientGeneration) == generation &&
        ReferenceEquals(sender, _client);

    private void DisconnectIfCurrent(long generation)
    {
        if (Interlocked.Read(ref _clientGeneration) == generation)
            DisconnectInternal();
    }

    private void DisconnectCurrentClient()
    {
        _connectionCancellationRegistration.Dispose();
        _connectionCancellationRegistration = default;
        DisconnectInternal();
    }

    private void DisconnectInternal()
    {
        Interlocked.Increment(ref _clientGeneration);
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
        DisconnectCurrentClient();
    }
}
