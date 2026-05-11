using OpenClaw.Shared;
using OpenClawTray.Services.Connection;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClawTray.Services;

/// <summary>
/// Implements <see cref="IGatewayOperatorConnector"/> by delegating to the
/// app's <see cref="GatewayConnectionManager"/>. All connection events flow
/// through the manager's diagnostics pipeline, giving full visibility in the
/// Connection Status window during the WSL local-setup flow.
/// </summary>
public sealed class ConnectionManagerOperatorConnector : IGatewayOperatorConnector
{
    private readonly GatewayConnectionManager _manager;
    private readonly GatewayRegistry _registry;
    private readonly IOpenClawLogger _logger;
    private readonly TimeSpan _timeout;

    public ConnectionManagerOperatorConnector(
        GatewayConnectionManager manager,
        GatewayRegistry registry,
        IOpenClawLogger logger,
        TimeSpan? timeout = null)
    {
        _manager = manager;
        _registry = registry;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(35);
    }

    public async Task<GatewayOperatorConnectionResult> ConnectAsync(
        string gatewayUrl, string token, bool tokenIsBootstrapToken = false,
        CancellationToken cancellationToken = default)
    {
        // Ensure registry has a record for this gateway with the provided credential
        var normalized = GatewayUrlHelper.NormalizeForWebSocket(gatewayUrl);
        var existing = _registry.FindByUrl(normalized);
        var recordId = existing?.Id ?? Guid.NewGuid().ToString();
        var record = (existing ?? new GatewayRecord { Id = recordId }) with
        {
            Url = normalized,
            BootstrapToken = tokenIsBootstrapToken ? token : existing?.BootstrapToken,
            SharedGatewayToken = !tokenIsBootstrapToken ? token : existing?.SharedGatewayToken,
            IsLocal = true,
        };
        _registry.AddOrUpdate(record);
        _registry.SetActive(recordId);
        _registry.Save();

        // Ensure identity directory exists for credential resolution
        var identityDir = _registry.GetIdentityDirectory(recordId);
        if (!Directory.Exists(identityDir))
            Directory.CreateDirectory(identityDir);

        _logger.Info($"[SetupConnector] Connecting via manager to {GatewayUrlHelper.SanitizeForDisplay(normalized)}");

        // If already connected or in a non-idle state, disconnect first
        var current = _manager.CurrentSnapshot.OperatorState;
        if (current is not RoleConnectionState.Idle)
            await _manager.DisconnectAsync();

        var tcs = new TaskCompletionSource<GatewayOperatorConnectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, GatewayConnectionSnapshot s)
        {
            switch (s.OperatorState)
            {
                case RoleConnectionState.Connected:
                    tcs.TrySetResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
                    break;
                case RoleConnectionState.PairingRequired:
                    tcs.TrySetResult(new GatewayOperatorConnectionResult(
                        GatewayOperatorConnectionStatus.PairingRequired,
                        "Gateway requires pairing approval.",
                        s.OperatorPairingRequestId));
                    break;
                case RoleConnectionState.PairingRejected:
                    tcs.TrySetResult(new GatewayOperatorConnectionResult(
                        GatewayOperatorConnectionStatus.AuthFailed,
                        "Pairing was rejected."));
                    break;
                case RoleConnectionState.Error:
                    var error = s.OperatorError ?? "Connection error";
                    var status = error.Contains("auth", StringComparison.OrdinalIgnoreCase)
                        || error.Contains("token", StringComparison.OrdinalIgnoreCase)
                        || error.Contains("signature", StringComparison.OrdinalIgnoreCase)
                        ? GatewayOperatorConnectionStatus.AuthFailed
                        : GatewayOperatorConnectionStatus.Failed;
                    tcs.TrySetResult(new GatewayOperatorConnectionResult(status, error));
                    break;
            }
        }

        _manager.StateChanged += OnStateChanged;
        try
        {
            await _manager.ConnectAsync(recordId);
            if (!tcs.Task.IsCompleted)
                OnStateChanged(this, _manager.CurrentSnapshot);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            try
            {
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout — disconnect to clean up the manager's in-flight connection
                try { await _manager.DisconnectAsync(); } catch { }
                return new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Timeout, "Timed out waiting for operator handshake.");
            }
        }
        finally
        {
            _manager.StateChanged -= OnStateChanged;
        }
    }

    public async Task<GatewayOperatorConnectionResult> ConnectWithStoredDeviceTokenAsync(
        string gatewayUrl, CancellationToken cancellationToken = default)
    {
        _logger.Info("[SetupConnector] Reconnecting with stored device token via manager");

        var tcs = new TaskCompletionSource<GatewayOperatorConnectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, GatewayConnectionSnapshot s)
        {
            if (s.OperatorState == RoleConnectionState.Connected)
                tcs.TrySetResult(new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Connected));
            else if (s.OperatorState == RoleConnectionState.PairingRequired)
                tcs.TrySetResult(new GatewayOperatorConnectionResult(
                    GatewayOperatorConnectionStatus.PairingRequired,
                    "Pairing required after reconnect.",
                    s.OperatorPairingRequestId));
            else if (s.OperatorState == RoleConnectionState.Error)
                tcs.TrySetResult(new GatewayOperatorConnectionResult(
                    GatewayOperatorConnectionStatus.AuthFailed,
                    s.OperatorError ?? "Reconnect failed."));
        }

        _manager.StateChanged += OnStateChanged;
        try
        {
            // Reconnect — the credential resolver will pick up the stored device token
            await _manager.ReconnectAsync();
            if (!tcs.Task.IsCompleted)
                OnStateChanged(this, _manager.CurrentSnapshot);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            try
            {
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout — disconnect to clean up the manager's in-flight connection
                try { await _manager.DisconnectAsync(); } catch { }
                return new GatewayOperatorConnectionResult(GatewayOperatorConnectionStatus.Timeout, "Timed out waiting for operator reconnect.");
            }
        }
        finally
        {
            _manager.StateChanged -= OnStateChanged;
        }
    }
}
