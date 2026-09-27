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
    bool PreserveExistingSharedTokenWhenMissing = false,
    string? BootstrapToken = null,
    bool NativeSetup = false);

internal sealed record GatewayDirectConnectResult(
    GatewayDirectConnectOutcome Outcome,
    GatewayConnectionSnapshot Snapshot,
    bool GatewayCommitted,
    string? Error = null,
    bool RollbackIncomplete = false);

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
        CancellationToken cancellationToken = default,
        GatewayValidationIdentity? preparedIdentity = null)
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
        cancellationToken.ThrowIfCancellationRequested();

        var previousActiveId = _registry.ActiveGatewayId;
        var previousRegistry = _registry.GetSnapshot();
        GatewayRegistrySnapshot? committedRegistry = null;
        var previousSnapshot = _connectionManager.CurrentSnapshot;
        var restorePreviousConnection =
            previousActiveId is not null &&
            string.Equals(previousSnapshot.GatewayId, previousActiveId, StringComparison.Ordinal) &&
            previousSnapshot.OperatorState == RoleConnectionState.Connected;
        var previousSettings = ConnectionSettingsSnapshot.Capture(_settings);
        var existing = ResolveExistingRecord(request);
        var replacesCredentialRealm = existing is not null &&
            !IsSameCredentialRealm(existing, request, _registry);
        // Onboarding keeps logical IDs and their sidecars for the same realm. A different native
        // realm is added separately, rather than deleting a previously configured gateway.
        var replaceRecord = replacesCredentialRealm && !request.NativeSetup;
        var candidateUsesNewIdentity = existing is null || replacesCredentialRealm;
        var recordId = candidateUsesNewIdentity
            ? Guid.NewGuid().ToString()
            : existing!.Id;
        var candidate = BuildCandidate(
            request,
            existing,
            recordId,
            preserveExistingSharedToken:
                (request.PreserveExistingSharedTokenWhenMissing || request.NativeSetup) && !replacesCredentialRealm);
        DeviceTokenClearTransaction? identityRollback = null;
        var registryMutated = false;
        var settingsMutated = false;
        var connectionAttemptStarted = false;
        var previousConnectionStopped = false;
        DeviceIdentityReplacementTransaction? candidateIdentityCreation = null;
        GatewayValidationIdentity? validationIdentity = null;
        DeviceIdentityReplacementTransaction? nativeIdentityRollback = null;

        try
        {
            if (request.NativeSetup)
            {
                validationIdentity = preparedIdentity ?? new GatewayValidationIdentity(
                    existing is not null && !replacesCredentialRealm
                        ? _registry.GetIdentityDirectory(existing.Id) : null);
                var validation = await _connectionManager.ValidateConnectionAsync(
                    candidate, validationIdentity, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (validation.Outcome != SetupCodeOutcome.Success)
                    return Failed(previousSnapshot, false, validation.ErrorMessage ?? "Gateway connection check failed.");
                candidate = candidate with
                {
                    RequiresV2Signature = candidate.RequiresV2Signature || validationIdentity.UseV2Signature
                };
            }
            cancellationToken.ThrowIfCancellationRequested();
            previousConnectionStopped = true;
            await _connectionManager.DisconnectAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (validationIdentity is not null)
            {
                if (candidateUsesNewIdentity)
                {
                    candidateIdentityCreation = validationIdentity.CopyTo(_registry.GetIdentityDirectory(recordId));
                }
                else
                    nativeIdentityRollback = validationIdentity.ReplaceExisting(_registry.GetIdentityDirectory(recordId));
            }
            cancellationToken.ThrowIfCancellationRequested();

            registryMutated = true;
            var records = previousRegistry.Records.Where(record => !replaceRecord || record.Id != existing!.Id).ToList();
            var index = records.FindIndex(record => record.Id == candidate.Id);
            if (index >= 0) records[index] = candidate;
            else records.Add(candidate);
            committedRegistry = _registry.ReplaceSnapshotAndSave(previousRegistry, new(records, recordId));

            if (!request.NativeSetup && !string.IsNullOrWhiteSpace(request.SharedToken))
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
                cancellationToken,
                request.NativeSetup);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot.OperatorState == RoleConnectionState.Error)
            {
                throw new InvalidOperationException(
                    snapshot.OperatorError ??
                    snapshot.NodeError ??
                    "Gateway connection failed.");
            }
            if (request.NativeSetup && snapshot.OperatorState != RoleConnectionState.Connected)
                throw new InvalidOperationException("Device approval is required. Approve this PC on the gateway, then check the connection again.");

            if (replaceRecord)
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

            var rollback = registryMutated || previousConnectionStopped
                ? Rollback(
                    previousRegistry,
                    committedRegistry ?? previousRegistry,
                    registryMutated,
                    candidateUsesNewIdentity,
                    candidateIdentityCreation is not null,
                    candidate,
                    settingsMutated,
                    previousSettings,
                    identityRollback)
                : new RollbackResult(CandidateRemainsCommitted: false, Error: null);
            if (candidateIdentityCreation is not null && !rollback.PreserveCandidateIdentity)
            {
                try { _registry.RemoveUnregisteredIdentity(recordId, candidateIdentityCreation); }
                catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException or
                    System.Text.Json.JsonException or InvalidDataException)
                {
                    cleanupError = string.Join(" ", new[] { cleanupError, "The unregistered candidate identity could not be removed." }
                        .Where(message => !string.IsNullOrWhiteSpace(message)));
                }
            }
            if (nativeIdentityRollback is not null && !rollback.PreserveCandidateIdentity)
            {
                var restore = DeviceIdentity.RestoreValidatedIdentity(nativeIdentityRollback);
                if (restore.Outcome != DeviceTokenRestoreOutcome.Restored)
                {
                    var identityError = restore.Outcome == DeviceTokenRestoreOutcome.Superseded
                        ? "Newer device credentials superseded the connection rollback. Review this gateway before continuing."
                        : $"Identity rollback failed: {restore.Error}";
                    cleanupError = string.Join(" ", new[] { cleanupError, identityError }
                        .Where(message => !string.IsNullOrWhiteSpace(message)));
                }
            }
            string? connectionRestoreError = null;
            if (previousConnectionStopped && restorePreviousConnection &&
                rollback.CanRestorePreviousConnection &&
                !rollback.CandidateRemainsCommitted &&
                previousActiveId is not null)
            {
                try
                {
                    var restored = await ConnectAndWaitForTerminalStateAsync(
                        previousActiveId,
                        CancellationToken.None,
                        request.NativeSetup);
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
                error,
                rollbackIncomplete: !string.IsNullOrWhiteSpace(cleanupError) ||
                    !string.IsNullOrWhiteSpace(rollback.Error) ||
                    !string.IsNullOrWhiteSpace(connectionRestoreError));
        }
        finally
        {
            if (preparedIdentity is null)
                validationIdentity?.Dispose();
        }
    }

    public async Task<SetupCodeResult> CheckAsync(
        GatewayDirectConnectRequest request, CancellationToken cancellationToken,
        GatewayValidationIdentity? preparedIdentity = null)
    {
        var existing = ResolveExistingRecord(request);
        var sameRealm = existing is not null && IsSameCredentialRealm(existing, request, _registry);
        var candidate = BuildCandidate(request, existing, existing?.Id ?? Guid.NewGuid().ToString(), sameRealm);
        var identity = preparedIdentity ?? CreateValidationIdentity(request);
        try { return await _connectionManager.ValidateConnectionAsync(candidate, identity, cancellationToken); }
        finally
        {
            if (preparedIdentity is null)
                identity.Dispose();
        }
    }

    public GatewayValidationIdentity CreateValidationIdentity(GatewayDirectConnectRequest request)
    {
        var existing = ResolveExistingRecord(request);
        return new(existing is not null && IsSameCredentialRealm(existing, request, _registry)
            ? _registry.GetIdentityDirectory(existing.Id) : null);
    }

    private GatewayRecord? ResolveExistingRecord(GatewayDirectConnectRequest request)
    {
        if (request.EditingGatewayId is not null)
            return _registry.GetById(request.EditingGatewayId) ?? _registry.FindByUrl(request.GatewayUrl);
        if (!request.NativeSetup)
            return _registry.FindByUrl(request.GatewayUrl);

        var matches = _registry.FindAllByUrl(request.GatewayUrl);
        return matches.FirstOrDefault(record => IsSameSshCredentialEndpoint(record.SshTunnel, request.SshTunnel))
            ?? matches.FirstOrDefault();
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

    internal static GatewayRecord BuildCandidate(
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
            BootstrapToken = request.BootstrapToken ??
                ((request.NativeSetup || string.IsNullOrWhiteSpace(request.SharedToken)) && preserveExistingSharedToken
                    ? existing?.BootstrapToken
                    : null),
            SshTunnel = request.SshTunnel,
            LastConnected = existing?.LastConnected,
        }.PreserveAdvancedFields(existing);

    /// <summary>
    /// True only when an edit keeps the same shared token inside the same credential realm.
    /// </summary>
    internal static bool ShouldPreserveUnchangedSharedToken(
        GatewayRecord? editing,
        string? submittedToken,
        string normalizedGatewayUrl,
        SshTunnelConfig? sshTunnel,
        GatewayRegistry registry)
    {
        if (editing is null)
            return false;

        var storedSharedToken = string.IsNullOrWhiteSpace(editing.SharedGatewayToken)
            ? null
            : editing.SharedGatewayToken.Trim();
        if (!string.Equals(submittedToken, storedSharedToken, StringComparison.Ordinal))
            return false;

        var request = new GatewayDirectConnectRequest(
            normalizedGatewayUrl,
            submittedToken,
            FriendlyName: null,
            sshTunnel,
            editing.Id);
        return IsSameCredentialRealm(editing, request, registry);
    }

    private static bool IsSameCredentialRealm(
        GatewayRecord existing,
        GatewayDirectConnectRequest request,
        GatewayRegistry registry)
    {
        if (!registry.FindAllByUrl(request.GatewayUrl)
            .Any(record => string.Equals(record.Id, existing.Id, StringComparison.Ordinal)))
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
        CancellationToken cancellationToken,
        bool nativeSetup = false)
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
        using var attempt = new CancellationTokenSource();
        using var cancellation = cancellationToken.Register(attempt.Cancel);
        if (nativeSetup)
            attempt.CancelAfter(_terminalTimeout);
        try
        {
            if (nativeSetup)
                await _connectionManager.ConnectAsync(recordId, attempt.Token);
            else
                await _connectionManager.ConnectAsync(recordId);
            cancellationToken.ThrowIfCancellationRequested();
            var current = _connectionManager.CurrentSnapshot;
            if (string.Equals(current.GatewayId, recordId, StringComparison.Ordinal) &&
                IsTerminal(current))
            {
                if (nativeSetup)
                    attempt.Token.ThrowIfCancellationRequested();
                return current;
            }

            var timeout = Task.Delay(_terminalTimeout, cancellationToken);
            var completed = await Task.WhenAny(completion.Task, timeout);
            if (completed == completion.Task)
            {
                if (nativeSetup)
                    attempt.Token.ThrowIfCancellationRequested();
                return await completion.Task;
            }
            if (nativeSetup)
                attempt.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Timed out waiting for the gateway connection.");
        }
        finally
        {
            // A successfully committed connection must outlive the editor's cancellation scope.
            attempt.CancelAfter(Timeout.InfiniteTimeSpan);
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
        GatewayRegistrySnapshot previousRegistry,
        GatewayRegistrySnapshot admittedRegistry,
        bool registryMutated,
        bool candidateUsesNewIdentity,
        bool candidateIdentityCreated,
        GatewayRecord candidate,
        bool settingsMutated,
        ConnectionSettingsSnapshot previousSettings,
        DeviceTokenClearTransaction? identityRollback)
    {
        try
        {
            if (registryMutated)
                _registry.ReplaceSnapshotAndSave(admittedRegistry, previousRegistry);
            else
            {
                var observed = _registry.AdoptPersistedSnapshot(admittedRegistry);
                if (!GatewayRegistry.HasSameSetupAuthority(observed, previousRegistry))
                    return ReconcileSupersedingSnapshot(observed, candidate);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Gateway direct-connect registry rollback failed: {ex.Message}");
            GatewayRegistrySnapshot persisted;
            try
            {
                try { persisted = _registry.AdoptPersistedSnapshot(admittedRegistry); }
                catch (InvalidOperationException)
                {
                    // A newer live writer may already have saved its own state. Observe it,
                    // but do not discard an unsaved live edit or reset its persistence baseline.
                    persisted = _registry.CapturePersistedSnapshot();
                }
            }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.Text.Json.JsonException or InvalidDataException)
            {
                return new(false,
                    "The saved gateway state could not be confirmed after rollback. Review Connection and reopen the app to reload saved gateways.",
                    CanRestorePreviousConnection: false, PreserveCandidateIdentity: true);
            }
            return ReconcileSupersedingSnapshot(persisted, candidate);
        }

        var errors = new List<string>();
        if (candidateUsesNewIdentity && !candidateIdentityCreated)
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

    private RollbackResult ReconcileSupersedingSnapshot(GatewayRegistrySnapshot persisted, GatewayRecord candidate)
    {
        var active = persisted.Records.FirstOrDefault(record => record.Id == persisted.ActiveId);
        var candidateIsActive = active is not null &&
            (active with { LastConnected = null }) == (candidate with { LastConnected = null });
        var error = ReconcileSettings(active);
        return new(candidateIsActive,
            "Gateway rollback failed. The saved gateway selection was reloaded; review Connection before continuing." +
                (error is null ? "" : " " + error),
            CanRestorePreviousConnection: false,
            PreserveCandidateIdentity: persisted.Records.Any(record => record.Id == candidate.Id));
    }

    private string? ReconcileSettings(GatewayRecord? candidate)
    {
        try
        {
            _registry.CapturePersistedSnapshot();
            var active = _registry.GetActive();
            if ((active is null) != (candidate is null) ||
                active is not null && candidate is not null &&
                (active with { LastConnected = null }) != (candidate with { LastConnected = null }))
                throw new InvalidOperationException("The persisted gateway changed again before settings reconciliation.");
            if (active is not null) SynchronizeSettingsWithCommittedGateway(active);
            else
            {
                _settings.UpdateAndSave(() =>
                {
                    _settings.GatewayUrl = "";
                    _settings.UseSshTunnel = false;
                });
                _reconcileRuntimeTunnel();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Gateway direct-connect settings reconciliation failed: {ex.Message}");
            return $"Saved gateway settings reconciliation failed: {ex.Message}";
        }
    }

    private void ApplySettings(GatewayRecord record)
    {
        _settings.UpdateAndSave(() =>
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
        });
    }

    private static GatewayDirectConnectResult Failed(
        GatewayConnectionSnapshot snapshot,
        bool gatewayCommitted,
        string error,
        bool rollbackIncomplete = false) =>
        new(
            GatewayDirectConnectOutcome.Failed,
            snapshot,
            gatewayCommitted,
            error,
            rollbackIncomplete);

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
            settings.UpdateAndSave(() =>
            {
                settings.GatewayUrl = GatewayUrl;
                settings.UseSshTunnel = UseSshTunnel;
                settings.SshTunnelUser = SshUser;
                settings.SshTunnelHost = SshHost;
                settings.SshTunnelSshPort = SshPort;
                settings.SshTunnelRemotePort = SshRemotePort;
                settings.SshTunnelLocalPort = SshLocalPort;
            });
        }
    }

    private sealed record RollbackResult(
        bool CandidateRemainsCommitted,
        string? Error,
        bool CanRestorePreviousConnection = true,
        bool PreserveCandidateIdentity = false);
}
