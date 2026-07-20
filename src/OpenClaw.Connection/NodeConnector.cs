using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>
/// Lightweight node connector that creates and manages a WindowsNodeClient.
/// Capability setup (canvas, screen capture, etc.) is handled by NodeService,
/// which has WinUI dependencies and remains in App.xaml.cs for now.
/// </summary>
public sealed class NodeConnector : INodeConnector, INodeConnectorTelemetryEvents
{
    private readonly IOpenClawLogger _logger;
    private readonly ConnectionDiagnostics? _diagnostics;
    private readonly SemaphoreSlim _connectSemaphore = new(1, 1);
    private readonly object _clientLifecycleLock = new();
    private WindowsNodeClient? _client;
    private long _clientGeneration;
    private bool _disposed;

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<PairingStatusEventArgs>? PairingStatusChanged;
    public event EventHandler<DeviceTokenReceivedEventArgs>? DeviceTokenReceived;
    public event EventHandler<NodeClientCreatedEventArgs>? ClientCreated;
    public event EventHandler? TransportConnected;
    public event EventHandler<GatewayErrorKind>? ConnectionFailure;

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

        // Share v2 signature flag from operator - avoid wasting a roundtrip on v3
        if (useV2Signature)
            client.UseV2Signature = true;

        long generation;
        lock (_clientLifecycleLock)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                try { client.Dispose(); }
                catch (Exception ex) { _logger.Warn($"[NodeConnector] Candidate dispose error: {ex.Message}"); }
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            generation = Interlocked.Increment(ref _clientGeneration);
            _client = client;
            Mode = NodeConnectionMode.Gateway;
        }

        using var cancellationRegistration = cancellationToken.Register(
            () => DisconnectIfCurrent(generation));
        cancellationToken.ThrowIfCancellationRequested();

        // CRITICAL: fire ClientCreated BEFORE await _client.ConnectAsync() so subscribers
        // (NodeService) can register capabilities synchronously. WindowsNodeClient
        // serializes _registration.Capabilities/Commands into the outbound "connect"
        // message during the connect handshake - registering after that point means
        // the gateway sees an empty caps array for this session.
        try
        {
            lock (_clientLifecycleLock)
            {
                ThrowIfNotCurrent(client, generation, cancellationToken);
                ClientCreated?.Invoke(
                    this,
                    new NodeClientCreatedEventArgs(
                        client,
                        credential.IsBootstrapToken ? null : credential.Token));
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            !IsCurrentClient(client, generation))
        {
            throw;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested ||
                !IsCurrentClient(client, generation))
            {
                return;
            }

            _logger.Warn($"[NodeConnector] ClientCreated handler threw: {ex.Message}");
            _diagnostics?.Record("node", "ClientCreated handler failed; node connection aborted before handshake", ex.Message);
            DisconnectCurrentClient();
            StatusChanged?.Invoke(this, ConnectionStatus.Error);
            return;
        }

        client.StatusChanged += (s, e) =>
            ForwardIfCurrent(s, generation, e, StatusChanged);
        client.TransportConnected += (s, _) =>
            ForwardIfCurrent(s, generation, EventArgs.Empty, TransportConnected);
        client.ConnectionFailure += (s, e) =>
            ForwardIfCurrent(s, generation, e, ConnectionFailure);
        client.PairingStatusChanged += (s, e) =>
            ForwardIfCurrent(s, generation, e, PairingStatusChanged);
        client.DeviceTokenReceived += (s, e) =>
            ForwardIfCurrent(s, generation, e, DeviceTokenReceived);

        try
        {
            Task connectTask;
            lock (_clientLifecycleLock)
            {
                ThrowIfNotCurrent(client, generation, cancellationToken);
                connectTask = client.ConnectAsync();
            }
            await connectTask;
            lock (_clientLifecycleLock)
                ThrowIfNotCurrent(client, generation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested ||
                !IsCurrentClient(client, generation))
            {
                return;
            }

            _logger.Error($"[NodeConnector] Connect failed: {ex.Message}");
        }
    }

    public Task DisconnectAsync()
    {
        DisconnectCurrentClient();
        return Task.CompletedTask;
    }

    private bool IsCurrentClient(object? sender, long generation)
    {
        lock (_clientLifecycleLock)
        {
            return Interlocked.Read(ref _clientGeneration) == generation &&
                ReferenceEquals(sender, _client);
        }
    }

    // Validation and dispatch stay atomic so a retired client cannot publish after its
    // replacement. Subscribers must remain synchronous and must not block on connector
    // lifecycle work while this lock is held.
    //
    // Lock ordering: _connectSemaphore (async, serialises connect/disconnect) may be
    // held when _clientLifecycleLock is acquired, but subscribers never acquire
    // _connectSemaphore. Among monitor locks, _clientLifecycleLock is the outermost
    // in the connector's acquisition graph. Subscribers may acquire their own locks
    // (GatewayConnectionManager._telemetryLock, GatewayRegistry._lock,
    // ConnectionDiagnostics._lock) but code holding those locks must not
    // synchronously enter connector lifecycle operations, preserving a consistent
    // acquisition order that prevents deadlock. Subscriber handlers must return
    // promptly; current subscribers use fire-and-forget async dispatch for heavy
    // work and perform only lightweight synchronous preambles.
    private void ForwardIfCurrent<T>(
        object? sender,
        long generation,
        T args,
        EventHandler<T>? handler)
    {
        lock (_clientLifecycleLock)
        {
            if (Interlocked.Read(ref _clientGeneration) == generation &&
                ReferenceEquals(sender, _client))
            {
                handler?.Invoke(this, args);
            }
        }
    }

    private void ForwardIfCurrent(
        object? sender,
        long generation,
        EventArgs args,
        EventHandler? handler)
    {
        lock (_clientLifecycleLock)
        {
            if (Interlocked.Read(ref _clientGeneration) == generation &&
                ReferenceEquals(sender, _client))
            {
                handler?.Invoke(this, args);
            }
        }
    }

    private void DisconnectIfCurrent(long generation)
    {
        lock (_clientLifecycleLock)
        {
            if (Interlocked.Read(ref _clientGeneration) == generation)
                DisconnectInternalCore();
        }
    }

    private void DisconnectCurrentClient()
    {
        DisconnectInternal();
    }

    private void DisconnectInternal()
    {
        lock (_clientLifecycleLock)
            DisconnectInternalCore();
    }

    private void DisconnectInternalCore()
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

    private void ThrowIfNotCurrent(
        WindowsNodeClient client,
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Read(ref _clientGeneration) != generation ||
            !ReferenceEquals(client, _client))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_clientLifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            DisconnectInternalCore();
        }
    }
}
