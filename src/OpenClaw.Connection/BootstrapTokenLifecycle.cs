using OpenClaw.Shared;

namespace OpenClaw.Connection;

internal readonly record struct EndpointCredentialAuthorization(
    bool Allowed,
    GatewayErrorKind FailureKind,
    string Detail,
    EndpointOwnershipProof? OwnershipProof = null)
{
    internal static EndpointCredentialAuthorization AllowedResult { get; } =
        new(true, GatewayErrorKind.Unknown, string.Empty);

    internal static EndpointCredentialAuthorization AllowWithProof(
        EndpointOwnershipProof ownershipProof) =>
        new(true, GatewayErrorKind.Unknown, string.Empty, ownershipProof);
}

internal sealed record EndpointOwnershipProof(
    string Kind,
    long? TunnelGeneration,
    int? ProcessId,
    DateTime? ProcessStartTimeUtc,
    string? ProcessPath)
{
    internal static EndpointOwnershipProof ForSshTunnel(long generation) =>
        new("ssh", generation, null, null, null);

    internal static EndpointOwnershipProof ForManagedGateway(
        GatewayEndpointProvenance provenance) =>
        new(
            "managed-local",
            null,
            provenance.ProcessId,
            provenance.ProcessStartTimeUtc,
            provenance.ProcessPath);
}

internal interface IEndpointCredentialSecurity
{
    Task<EndpointCredentialAuthorization> AuthorizeCredentialAsync(
        GatewayRecord record,
        GatewayCredential credential,
        CancellationToken cancellationToken);

    async Task<ReconnectAuthorizationResult> AuthorizeCredentialHandoffAsync(
        GatewayRecord expectedRecord,
        GatewayCredential credential,
        EndpointOwnershipProof? expectedOwnership,
        Func<bool> isCurrentAttempt,
        CancellationToken operationCancellationToken,
        CancellationToken handshakeCancellationToken,
        string role)
    {
        if (!isCurrentAttempt())
        {
            return new(
                false,
                GatewayErrorKind.Unknown,
                $"{role} connection attempt was superseded.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellationToken,
            handshakeCancellationToken);
        var authorization = await AuthorizeCredentialAsync(
                expectedRecord,
                credential,
                linkedCts.Token)
            .ConfigureAwait(false);
        if (!isCurrentAttempt())
        {
            return new(
                false,
                GatewayErrorKind.Unknown,
                $"{role} connection attempt was superseded.");
        }

        if (authorization.Allowed &&
            expectedOwnership is not null &&
            authorization.OwnershipProof != expectedOwnership)
        {
            return new(
                false,
                GatewayErrorKind.LocalPortConflict,
                $"Endpoint ownership changed after preflight, so {role} credentials were not sent.");
        }

        return new(
            authorization.Allowed,
            authorization.FailureKind,
            authorization.Detail);
    }

    Task<bool> IsRecoverySafeEndpointAsync(
        GatewayRecord record,
        CancellationToken cancellationToken);
}

internal sealed class GatewayAttemptLease : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _disposed;

    internal GatewayAttemptLease(SemaphoreSlim semaphore)
    {
        _semaphore = semaphore;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _semaphore.Release();
    }
}

internal interface IGatewayAttemptLeaseSource
{
    Task<GatewayAttemptLease?> AcquireCurrentAttemptAsync(
        GatewayAttemptStamp attempt,
        CancellationToken cancellationToken);
}

internal enum OperatorReconnectReason
{
    PostBootstrapHandoff,
    OperatorTokenMismatch
}

internal sealed record OperatorReconnectRequest(
    GatewayAttemptStamp Attempt,
    OperatorReconnectReason Reason);

internal interface IOperatorReconnectScheduler
{
    void ScheduleOperatorReconnect(OperatorReconnectRequest request);
}

internal interface IV2SignatureRequirementSink
{
    void RememberGatewayNeedsV2Signature(
        string gatewayRecordId,
        bool markActiveAttempt);
}

internal sealed record OperatorCredentialSelection(
    GatewayCredentialResolution Resolution,
    bool UsedBootstrapToken);

internal enum DeviceTokenHandlingOutcome
{
    Stored,
    IgnoredStale,
    IdentityLoadFailure,
    StoreFailure
}

internal sealed record DeviceTokenHandlingResult(
    DeviceTokenHandlingOutcome Outcome,
    string? Detail = null);

internal sealed class BootstrapTokenLifecycle
{
    private readonly GatewayRegistry _registry;
    private readonly IDeviceIdentityStore? _identityStore;
    private readonly IGatewayAttemptLeaseSource _attemptLeases;
    private readonly IEndpointCredentialSecurity _endpointSecurity;
    private readonly IOperatorReconnectScheduler _reconnectScheduler;
    private readonly IV2SignatureRequirementSink _v2SignatureSink;
    private readonly IOpenClawLogger _logger;
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly object _stateLock = new();

    private string? _forceBootstrapForGatewayRecordId;
    private GatewayAttemptStamp? _activeAttempt;
    private bool _activeConnectUsedBootstrapToken;
    private bool _postBootstrapOperatorReconnectScheduled;
    private string? _operatorTokenRecoveryAttemptedGatewayId;
    private int _stopped;

    internal BootstrapTokenLifecycle(
        GatewayRegistry registry,
        IDeviceIdentityStore? identityStore,
        IGatewayAttemptLeaseSource attemptLeases,
        IEndpointCredentialSecurity endpointSecurity,
        IOperatorReconnectScheduler reconnectScheduler,
        IV2SignatureRequirementSink v2SignatureSink,
        IOpenClawLogger logger,
        ConnectionDiagnostics diagnostics)
    {
        _registry = registry;
        _identityStore = identityStore;
        _attemptLeases = attemptLeases;
        _endpointSecurity = endpointSecurity;
        _reconnectScheduler = reconnectScheduler;
        _v2SignatureSink = v2SignatureSink;
        _logger = logger;
        _diagnostics = diagnostics;
    }

    internal void ForceBootstrapForNextConnect(string gatewayRecordId)
    {
        lock (_stateLock)
            _forceBootstrapForGatewayRecordId = gatewayRecordId;
    }

    internal void ClearForcedBootstrap(string gatewayRecordId)
    {
        lock (_stateLock)
        {
            if (_forceBootstrapForGatewayRecordId == gatewayRecordId)
                _forceBootstrapForGatewayRecordId = null;
        }
    }

    internal OperatorCredentialSelection SelectOperatorCredential(
        GatewayRecord record,
        GatewayCredentialResolution resolved)
    {
        var selected = resolved;
        lock (_stateLock)
        {
            if (_forceBootstrapForGatewayRecordId == record.Id &&
                !string.IsNullOrWhiteSpace(record.BootstrapToken))
            {
                var credential = new GatewayCredential(
                    record.BootstrapToken!,
                    IsBootstrapToken: true,
                    CredentialResolver.SourceBootstrapToken)
                {
                    ResolutionStatus = GatewayCredentialResolutionStatus.BootstrapRequired,
                    ResolutionDetail = "Using setup-code bootstrap token for this connection."
                };
                selected = new GatewayCredentialResolution(
                    credential,
                    GatewayCredentialResolutionStatus.BootstrapRequired,
                    BootstrapRequired: true,
                    Detail: credential.ResolutionDetail);
                _forceBootstrapForGatewayRecordId = null;
            }
        }

        return new OperatorCredentialSelection(
            selected,
            selected.Credential?.IsBootstrapToken == true);
    }

    internal void BeginOperatorConnect(
        GatewayAttemptStamp attempt,
        bool usedBootstrapToken)
    {
        lock (_stateLock)
        {
            _activeAttempt = attempt;
            _activeConnectUsedBootstrapToken = usedBootstrapToken;
            _postBootstrapOperatorReconnectScheduled = false;
        }
    }

    internal void ResetOperatorRecoveryAfterHandshake(GatewayAttemptStamp attempt)
    {
        lock (_stateLock)
        {
            if (_activeAttempt == attempt &&
                _operatorTokenRecoveryAttemptedGatewayId == attempt.GatewayRecordId)
            {
                _operatorTokenRecoveryAttemptedGatewayId = null;
            }
        }
    }

    internal async Task<DeviceTokenHandlingResult> HandleDeviceTokenReceivedAsync(
        GatewayAttemptStamp attempt,
        string identityPath,
        DeviceTokenReceivedEventArgs token,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _stopped) != 0)
            return new(DeviceTokenHandlingOutcome.IgnoredStale);

        using var lease = await _attemptLeases.AcquireCurrentAttemptAsync(
            attempt,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return new(DeviceTokenHandlingOutcome.IgnoredStale);

        _diagnostics.Record(
            "credential",
            $"Device token received for {token.Role}",
            $"Scopes={string.Join(",", token.Scopes ?? [])}");

        if (_identityStore is not null)
        {
            try
            {
                _identityStore.StoreToken(
                    identityPath,
                    token.Token,
                    token.Scopes,
                    token.Role);
                _logger.Info($"[ConnMgr] Persisted {token.Role} device token via identity store");
            }
            catch (DeviceIdentityLoadException ex)
            {
                var detail = BuildIdentityFailureDetail(ex);
                _logger.Error(
                    $"[ConnMgr] Stored device identity load failed while persisting {token.Role} token: {detail}");
                _diagnostics.Record(
                    "identity",
                    "Stored device identity could not be loaded while persisting a device token",
                    detail);
                return new(DeviceTokenHandlingOutcome.IdentityLoadFailure, detail);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    $"[ConnMgr] Failed to persist {token.Role} device token: {ex.Message}");
                _diagnostics.Record(
                    "identity",
                    $"Failed to persist {token.Role} device token",
                    ex.Message);
                return new(DeviceTokenHandlingOutcome.StoreFailure, ex.Message);
            }
        }

        TryClearBootstrapTokenUnderLease(
            attempt.GatewayRecordId!,
            identityPath);
        TrySchedulePostBootstrapOperatorReconnectUnderLease(
            attempt,
            identityPath,
            token);
        return new(DeviceTokenHandlingOutcome.Stored);
    }

    internal async Task<bool> TryClearAfterDurablePairingAsync(
        GatewayAttemptStamp attempt,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _stopped) != 0 ||
            attempt.GatewayRecordId is null)
        {
            return false;
        }

        using var lease = await _attemptLeases.AcquireCurrentAttemptAsync(
            attempt,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return false;

        var identityPath = _registry.GetIdentityDirectory(attempt.GatewayRecordId);
        return TryClearBootstrapTokenUnderLease(
            attempt.GatewayRecordId,
            identityPath);
    }

    internal async Task<bool> TryScheduleOperatorTokenRecoveryAsync(
        GatewayAttemptStamp attempt,
        string identityPath,
        string message,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _stopped) != 0 ||
            attempt.GatewayRecordId is null ||
            GatewayErrorClassifier.ClassifyWithCode(message) !=
                GatewayErrorKind.DeviceTokenMismatch)
        {
            return false;
        }

        using var lease = await _attemptLeases.AcquireCurrentAttemptAsync(
            attempt,
            cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return false;

        lock (_stateLock)
        {
            if (_operatorTokenRecoveryAttemptedGatewayId == attempt.GatewayRecordId)
                return false;
        }

        var record = _registry.GetById(attempt.GatewayRecordId);
        if (record is null)
            return false;

        var hasSharedToken = !string.IsNullOrWhiteSpace(record.SharedGatewayToken);
        var hasBootstrapToken = !string.IsNullOrWhiteSpace(record.BootstrapToken);
        if (!hasSharedToken && !hasBootstrapToken)
            return false;

        if (!await _endpointSecurity.IsRecoverySafeEndpointAsync(
                record,
                cancellationToken).ConfigureAwait(false))
        {
            _diagnostics.Record(
                "credential",
                "Skipped operator token recovery: endpoint not trusted for credential fallback");
            return false;
        }

        if (!DeviceIdentity.TryClearDeviceToken(identityPath, _logger))
            return false;

        lock (_stateLock)
            _operatorTokenRecoveryAttemptedGatewayId = attempt.GatewayRecordId;
        var fallbackLabel = hasSharedToken ? "shared gateway token" : "bootstrap token";
        _diagnostics.Record(
            "credential",
            $"Cleared stale operator device token; reconnecting with {fallbackLabel}");
        _reconnectScheduler.ScheduleOperatorReconnect(
            new OperatorReconnectRequest(
                attempt,
                OperatorReconnectReason.OperatorTokenMismatch));
        return true;
    }

    internal void Stop()
    {
        Interlocked.Exchange(ref _stopped, 1);
    }

    private bool TryClearBootstrapTokenUnderLease(
        string gatewayRecordId,
        string identityPath)
    {
        var record = _registry.GetById(gatewayRecordId);
        if (record?.BootstrapToken is null)
            return false;

        if (!TryReadStoredTokenPresence(
                identityPath,
                "operator",
                "clearing bootstrap credentials",
                out var hasOperatorToken)
            || !TryReadStoredTokenPresence(
                identityPath,
                "node",
                "clearing bootstrap credentials",
                out var hasNodeToken))
        {
            return false;
        }

        if (!hasOperatorToken || !hasNodeToken)
        {
            _diagnostics.Record(
                "credential",
                "Retaining bootstrap token until role tokens are durable",
                $"operatorToken={hasOperatorToken}; nodeToken={hasNodeToken}");
            return false;
        }

        try
        {
            var updated = _registry.UpdateAndSave(
                gatewayRecordId,
                gateway => gateway with { BootstrapToken = null });
            if (updated is null)
                return false;

            _diagnostics.Record(
                "credential",
                "Cleared bootstrap token — operator and node tokens are durable");
        }
        catch (Exception ex)
        {
            _logger.Warn(
                $"[ConnMgr] Failed to persist cleared bootstrap token: {ex.Message}");
            _diagnostics.Record(
                "credential",
                "Failed to persist cleared bootstrap token",
                ex.Message);
            return false;
        }

        return true;
    }

    private void TrySchedulePostBootstrapOperatorReconnectUnderLease(
        GatewayAttemptStamp attempt,
        string identityPath,
        DeviceTokenReceivedEventArgs token)
    {
        lock (_stateLock)
        {
            if (_activeAttempt != attempt ||
                !_activeConnectUsedBootstrapToken ||
                _postBootstrapOperatorReconnectScheduled)
            {
                return;
            }
        }

        if (!TryReadStoredTokenPresence(
                identityPath,
                "operator",
                "scheduling the post-bootstrap operator reconnect",
                out var hasOperatorToken))
        {
            return;
        }

        var record = _registry.GetById(attempt.GatewayRecordId!);
        var canReconnectWithSharedToken =
            !string.IsNullOrWhiteSpace(record?.SharedGatewayToken);
        if (!hasOperatorToken && !canReconnectWithSharedToken)
            return;
        if (token.Role != "operator" &&
            !(token.Role == "node" &&
              !hasOperatorToken &&
              canReconnectWithSharedToken))
        {
            return;
        }

        lock (_stateLock)
        {
            if (_activeAttempt != attempt ||
                _postBootstrapOperatorReconnectScheduled)
            {
                return;
            }
            _postBootstrapOperatorReconnectScheduled = true;
        }

        var detail = hasOperatorToken
            ? "using persisted operator device token"
            : "using preserved shared gateway token";
        _v2SignatureSink.RememberGatewayNeedsV2Signature(
            attempt.GatewayRecordId!,
            markActiveAttempt: true);
        _diagnostics.Record(
            "credential",
            "Bootstrap handoff complete — reconnecting operator role",
            detail);
        _reconnectScheduler.ScheduleOperatorReconnect(
            new OperatorReconnectRequest(
                attempt,
                OperatorReconnectReason.PostBootstrapHandoff));
    }

    private bool TryReadStoredTokenPresence(
        string identityPath,
        string role,
        string operation,
        out bool hasToken)
    {
        try
        {
            hasToken = DeviceIdentity.HasStoredDeviceTokenForRole(
                identityPath,
                role,
                _logger);
            return true;
        }
        catch (DeviceIdentityLoadException ex)
        {
            hasToken = false;
            var detail = BuildIdentityFailureDetail(ex);
            _logger.Error(
                $"[ConnMgr] Stored device identity load failed while {operation}: {detail}");
            _diagnostics.Record(
                "identity",
                $"Stored device identity could not be loaded while {operation}",
                detail);
            return false;
        }
    }

    private static string BuildIdentityFailureDetail(DeviceIdentityLoadException ex)
    {
        var cause = ex.InnerException;
        return cause is null
            ? ex.GetType().Name
            : $"{cause.GetType().Name}: {cause.Message}";
    }
}
