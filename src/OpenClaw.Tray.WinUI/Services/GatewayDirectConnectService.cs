using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal enum GatewayDirectConnectOutcome
{
    Connected,
    PairingRequired,
    Failed,
}

internal sealed record GatewayDirectConnectRequest(
    string GatewayUrl,
    string? SharedToken,
    string? FriendlyName,
    SshTunnelConfig? SshTunnel,
    string? EditingGatewayId = null,
    bool PreserveExistingSharedTokenWhenMissing = false);

internal sealed record GatewayDirectConnectResult(
    GatewayDirectConnectOutcome Outcome,
    GatewayConnectionSnapshot Snapshot,
    bool GatewayCommitted,
    string? Error = null);

/// <summary>
/// Owns the direct-connect commit/rollback workflow. WinUI surfaces provide input and render the
/// result; registry, identity, settings, manager, and runtime-tunnel state converge here.
/// </summary>
internal sealed class GatewayDirectConnectService
{
    private readonly IGatewayConnectionManager _connectionManager;
    private readonly GatewayRegistry _registry;
    private readonly SettingsManager _settings;
    private readonly Action _reconcileRuntimeTunnel;
    private readonly IOpenClawLogger _logger;
    private readonly TimeSpan _terminalTimeout;

    public GatewayDirectConnectService(
        IGatewayConnectionManager connectionManager,
        GatewayRegistry registry,
        SettingsManager settings,
        Action reconcileRuntimeTunnel,
        IOpenClawLogger logger,
        TimeSpan? terminalTimeout = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _reconcileRuntimeTunnel = reconcileRuntimeTunnel ??
            throw new ArgumentNullException(nameof(reconcileRuntimeTunnel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _terminalTimeout = terminalTimeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<GatewayDirectConnectResult> ConnectAsync(
        GatewayDirectConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!GatewayUrlHelper.IsValidGatewayUrl(request.GatewayUrl))
        {
            return Failed(
                GatewayConnectionSnapshot.Idle,
                gatewayCommitted: false,
                "Invalid gateway URL.");
        }

        using var lifecycleLease = await _connectionManager
            .BeginManualGatewayLifecycleOperationAsync(cancellationToken);

        var previousActiveId = _registry.ActiveGatewayId;
        var previousSnapshot = _connectionManager.CurrentSnapshot;
        var restorePreviousConnection =
            previousActiveId is not null &&
            string.Equals(previousSnapshot.GatewayId, previousActiveId, StringComparison.Ordinal) &&
            previousSnapshot.OperatorState == RoleConnectionState.Connected;
        var previousSettings = ConnectionSettingsSnapshot.Capture(_settings);
        var existing = request.EditingGatewayId is not null
            ? _registry.GetById(request.EditingGatewayId) ?? _registry.FindByUrl(request.GatewayUrl)
            : _registry.FindByUrl(request.GatewayUrl);
        var replacesCredentialRealm = existing is not null &&
            !IsSameCredentialRealm(existing, request, _registry);
        var candidateUsesNewIdentity = existing is null || replacesCredentialRealm;
        var recordId = candidateUsesNewIdentity
            ? Guid.NewGuid().ToString()
            : existing!.Id;
        var candidate = BuildCandidate(
            request,
            existing,
            recordId,
            preserveExistingSharedToken:
                request.PreserveExistingSharedTokenWhenMissing && !replacesCredentialRealm);
        DeviceTokenClearTransaction? identityRollback = null;
        var registryMutated = false;
        var candidateRegistryCommitted = false;
        var settingsMutated = false;
        var connectionAttemptStarted = false;

        try
        {
            await _connectionManager.DisconnectAsync();

            if (replacesCredentialRealm)
                _registry.Remove(existing!.Id);
            _registry.AddOrUpdate(candidate);
            _registry.SetActive(recordId);
            registryMutated = true;
            _registry.Save();
            candidateRegistryCommitted = true;

            if (!string.IsNullOrWhiteSpace(request.SharedToken))
            {
                var identityDir = _registry.GetIdentityDirectory(recordId);
                var clearResult = DeviceIdentityStore.BeginTransactionalTokenClear(identityDir, _logger);
                if (!clearResult.Success)
                {
                    throw new InvalidOperationException(
                        $"Stored device credentials could not be cleared safely: {clearResult.Error}");
                }
                identityRollback = clearResult.Transaction;
            }

            settingsMutated = true;
            ApplySettings(candidate);
            _reconcileRuntimeTunnel();

            connectionAttemptStarted = true;
            var snapshot = await ConnectAndWaitForTerminalStateAsync(
                recordId,
                cancellationToken);
            if (snapshot.OperatorState == RoleConnectionState.Error)
            {
                throw new InvalidOperationException(
                    snapshot.OperatorError ??
                    snapshot.NodeError ??
                    "Gateway connection failed.");
            }

            if (replacesCredentialRealm)
                DeleteIdentityDirectoryBestEffort(existing!.Id, "superseded gateway");

            return new GatewayDirectConnectResult(
                snapshot.OverallState == OverallConnectionState.PairingRequired ||
                snapshot.OperatorState == RoleConnectionState.PairingRequired ||
                snapshot.NodeState == RoleConnectionState.PairingRequired
                    ? GatewayDirectConnectOutcome.PairingRequired
                    : GatewayDirectConnectOutcome.Connected,
                snapshot,
                GatewayCommitted: true);
        }
        catch (Exception ex)
        {
            string? cleanupError = null;
            if (connectionAttemptStarted)
            {
                try
                {
                    await _connectionManager.DisconnectAsync();
                }
                catch (Exception cleanupException)
                {
                    cleanupError =
                        $"Failed to stop the rejected connection attempt: {cleanupException.Message}";
                }
            }

            var rollback = registryMutated
                ? Rollback(
                    previousActiveId,
                    candidateUsesNewIdentity,
                    existing,
                    candidate,
                    candidateRegistryCommitted,
                    settingsMutated,
                    previousSettings,
                    identityRollback)
                : new RollbackResult(CandidateRemainsCommitted: false, Error: null);
            string? connectionRestoreError = null;
            if (restorePreviousConnection &&
                !rollback.CandidateRemainsCommitted &&
                previousActiveId is not null)
            {
                try
                {
                    var restored = await ConnectAndWaitForTerminalStateAsync(
                        previousActiveId,
                        CancellationToken.None);
                    if (restored.OperatorState == RoleConnectionState.Error)
                    {
                        connectionRestoreError =
                            $"Failed to restore the previous gateway connection: " +
                            $"{restored.OperatorError ?? "Gateway connection failed."}";
                    }
                }
                catch (Exception restoreException)
                {
                    connectionRestoreError =
                        $"Failed to restore the previous gateway connection: {restoreException.Message}";
                }
            }
            var error = string.Join(
                " ",
                new[] { ex.Message, cleanupError, rollback.Error, connectionRestoreError }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));

            return Failed(
                _connectionManager.CurrentSnapshot,
                gatewayCommitted: rollback.CandidateRemainsCommitted,
                error);
        }
    }

    public void SynchronizeSettingsWithCommittedGateway(GatewayRecord committedGateway)
    {
        var active = _registry.GetActive()
            ?? throw new InvalidOperationException("The committed gateway is no longer active.");
        if (!string.Equals(active.Id, committedGateway.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The committed gateway was superseded before its settings could be synchronized.");
        }
        var previous = ConnectionSettingsSnapshot.Capture(_settings);
        try
        {
            ApplySettings(committedGateway);
            _reconcileRuntimeTunnel();
        }
        catch (Exception ex)
        {
            string? rollbackError = null;
            try
            {
                previous.Restore(_settings);
                _reconcileRuntimeTunnel();
            }
            catch (Exception rollbackException)
            {
                rollbackError = $" Settings rollback failed: {rollbackException.Message}";
            }

            throw new InvalidOperationException(
                $"Failed to synchronize gateway settings: {ex.Message}{rollbackError}",
                ex);
        }
    }

    private static GatewayRecord BuildCandidate(
        GatewayDirectConnectRequest request,
        GatewayRecord? existing,
        string recordId,
        bool preserveExistingSharedToken) =>
        new GatewayRecord
        {
            Id = recordId,
            Url = request.GatewayUrl,
            FriendlyName = string.IsNullOrWhiteSpace(request.FriendlyName)
                ? existing?.FriendlyName
                : request.FriendlyName,
            SharedGatewayToken = string.IsNullOrWhiteSpace(request.SharedToken)
                ? preserveExistingSharedToken
                    ? existing?.SharedGatewayToken
                    : null
                : request.SharedToken,
            BootstrapToken = null,
            SshTunnel = request.SshTunnel,
            LastConnected = existing?.LastConnected,
        }.PreserveAdvancedFields(existing);

    private static bool IsSameCredentialRealm(
        GatewayRecord existing,
        GatewayDirectConnectRequest request,
        GatewayRegistry registry)
    {
        var matchingUrlRecord = registry.FindByUrl(request.GatewayUrl);
        if (!string.Equals(matchingUrlRecord?.Id, existing.Id, StringComparison.Ordinal))
            return false;

        return IsSameSshCredentialEndpoint(existing.SshTunnel, request.SshTunnel);
    }

    private static bool IsSameSshCredentialEndpoint(
        SshTunnelConfig? current,
        SshTunnelConfig? candidate)
    {
        if (current is null || candidate is null)
            return current is null && candidate is null;

        return string.Equals(current.User, candidate.User, StringComparison.Ordinal) &&
            string.Equals(current.Host, candidate.Host, StringComparison.OrdinalIgnoreCase) &&
            current.SshPort == candidate.SshPort &&
            current.RemotePort == candidate.RemotePort;
    }

    private async Task<GatewayConnectionSnapshot> ConnectAndWaitForTerminalStateAsync(
        string recordId,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<GatewayConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, GatewayConnectionSnapshot snapshot)
        {
            if (string.Equals(snapshot.GatewayId, recordId, StringComparison.Ordinal) &&
                IsTerminal(snapshot))
            {
                completion.TrySetResult(snapshot);
            }
        }

        _connectionManager.StateChanged += OnStateChanged;
        try
        {
            await _connectionManager.ConnectAsync(recordId);
            var current = _connectionManager.CurrentSnapshot;
            if (string.Equals(current.GatewayId, recordId, StringComparison.Ordinal) &&
                IsTerminal(current))
            {
                return current;
            }

            var timeout = Task.Delay(_terminalTimeout, cancellationToken);
            var completed = await Task.WhenAny(completion.Task, timeout);
            if (completed == completion.Task)
                return await completion.Task;
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Timed out waiting for the gateway connection.");
        }
        finally
        {
            _connectionManager.StateChanged -= OnStateChanged;
        }
    }

    private static bool IsTerminal(GatewayConnectionSnapshot snapshot) =>
        snapshot.OverallState is OverallConnectionState.Connected
            or OverallConnectionState.Ready
            or OverallConnectionState.Degraded
            or OverallConnectionState.PairingRequired ||
        snapshot.OperatorState is RoleConnectionState.PairingRequired
            or RoleConnectionState.Error ||
        snapshot.NodeState == RoleConnectionState.PairingRequired;

    private RollbackResult Rollback(
        string? previousActiveId,
        bool candidateUsesNewIdentity,
        GatewayRecord? previousRecord,
        GatewayRecord candidate,
        bool candidateRegistryCommitted,
        bool settingsMutated,
        ConnectionSettingsSnapshot previousSettings,
        DeviceTokenClearTransaction? identityRollback)
    {
        try
        {
            if (candidateUsesNewIdentity)
                _registry.Remove(candidate.Id);
            if (previousRecord is not null)
                _registry.AddOrUpdate(previousRecord);
            _registry.SetActive(previousActiveId);
            _registry.Save();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Gateway direct-connect registry rollback failed: {ex.Message}");
            if (!candidateRegistryCommitted)
            {
                return new RollbackResult(
                    CandidateRemainsCommitted: false,
                    Error: $"Gateway update was not saved, and rollback persistence also failed: {ex.Message}");
            }

            if (previousRecord is not null &&
                !string.Equals(previousRecord.Id, candidate.Id, StringComparison.Ordinal))
            {
                _registry.Remove(previousRecord.Id);
            }
            _registry.AddOrUpdate(candidate);
            _registry.SetActive(candidate.Id);
            var reconciliationError = ReconcileSettings(candidate);
            return new RollbackResult(
                CandidateRemainsCommitted: true,
                Error: string.IsNullOrWhiteSpace(reconciliationError)
                    ? $"Gateway rollback failed; the new gateway remains active: {ex.Message}"
                    : $"Gateway rollback failed; the new gateway remains active: {ex.Message} " +
                      reconciliationError);
        }

        var errors = new List<string>();
        if (candidateUsesNewIdentity)
        {
            var cleanupError = DeleteIdentityDirectory(
                candidate.Id,
                "rejected gateway");
            if (cleanupError is not null)
                errors.Add(cleanupError);
        }
        else if (identityRollback is not null)
        {
            var restore = DeviceIdentityStore.RestoreTransactionalTokenClear(
                identityRollback,
                _logger);
            if (restore.Outcome == DeviceTokenRestoreOutcome.Superseded)
            {
                _logger.Info(
                    "Gateway direct-connect identity rollback skipped because newer credentials were written.");
            }
            else if (restore.Outcome == DeviceTokenRestoreOutcome.Failed)
            {
                errors.Add($"Identity rollback failed: {restore.Error}");
            }
        }

        if (settingsMutated)
        {
            try
            {
                previousSettings.Restore(_settings);
                _reconcileRuntimeTunnel();
            }
            catch (Exception ex)
            {
                errors.Add($"Settings rollback failed: {ex.Message}");
            }
        }

        return new RollbackResult(
            CandidateRemainsCommitted: false,
            Error: errors.Count == 0 ? null : string.Join(" ", errors));
    }

    private void DeleteIdentityDirectoryBestEffort(string gatewayId, string context)
    {
        var error = DeleteIdentityDirectory(gatewayId, context);
        if (error is not null)
            _logger.Warn(error);
    }

    private string? DeleteIdentityDirectory(string gatewayId, string context)
    {
        var identityDirectory = _registry.GetIdentityDirectory(gatewayId);
        try
        {
            if (Directory.Exists(identityDirectory))
                Directory.Delete(identityDirectory, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"Failed to remove the {context} identity directory: {ex.Message}";
        }
    }

    private string? ReconcileSettings(GatewayRecord candidate)
    {
        try
        {
            ApplySettings(candidate);
            _reconcileRuntimeTunnel();
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Gateway direct-connect settings reconciliation failed: {ex.Message}");
            return $"Candidate settings reconciliation failed: {ex.Message}";
        }
    }

    private void ApplySettings(GatewayRecord record)
    {
        _settings.GatewayUrl = record.Url;
        _settings.UseSshTunnel = record.SshTunnel is not null;
        if (record.SshTunnel is { } ssh)
        {
            _settings.SshTunnelUser = ssh.User;
            _settings.SshTunnelHost = ssh.Host;
            _settings.SshTunnelSshPort = ssh.SshPort;
            _settings.SshTunnelRemotePort = ssh.RemotePort;
            _settings.SshTunnelLocalPort = ssh.LocalPort;
        }
        _settings.SaveOrThrow();
    }

    private static GatewayDirectConnectResult Failed(
        GatewayConnectionSnapshot snapshot,
        bool gatewayCommitted,
        string error) =>
        new(
            GatewayDirectConnectOutcome.Failed,
            snapshot,
            gatewayCommitted,
            error);

    private sealed record ConnectionSettingsSnapshot(
        string GatewayUrl,
        bool UseSshTunnel,
        string SshUser,
        string SshHost,
        int SshPort,
        int SshRemotePort,
        int SshLocalPort)
    {
        public static ConnectionSettingsSnapshot Capture(SettingsManager settings) =>
            new(
                settings.GatewayUrl,
                settings.UseSshTunnel,
                settings.SshTunnelUser,
                settings.SshTunnelHost,
                settings.SshTunnelSshPort,
                settings.SshTunnelRemotePort,
                settings.SshTunnelLocalPort);

        public void Restore(SettingsManager settings)
        {
            settings.GatewayUrl = GatewayUrl;
            settings.UseSshTunnel = UseSshTunnel;
            settings.SshTunnelUser = SshUser;
            settings.SshTunnelHost = SshHost;
            settings.SshTunnelSshPort = SshPort;
            settings.SshTunnelRemotePort = SshRemotePort;
            settings.SshTunnelLocalPort = SshLocalPort;
            settings.SaveOrThrow();
        }
    }

    private sealed record RollbackResult(
        bool CandidateRemainsCommitted,
        string? Error);
}
