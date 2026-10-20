using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Owns a staged isolated native profile and runtime while the shared WinUI wizard owns
/// RPC and rendering. Successful wizard completion or a validated optional-setup handoff
/// permits final config/health verification before publication.
/// </summary>
public sealed class NativeGatewaySetupSession(
    GatewayRegistry registry,
    NativeGatewaySetupDraft draft,
    GatewayRecord record,
    NativeGatewayPackage package,
    INativeGatewaySetupHost host,
    INativeGatewayRuntime runtime) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _operatorTokenRecoveryUsed;
    private bool _wizardCompleted;
    private bool _optionalSetupDeferred;
    private bool _disposed;
    private bool _published;
    private string? _identityBinding;
    private IDisposable? _terminal;
    public GatewayRecord Record { get; } = record;
    public bool IsPublished => _published;
    public string IdentityDirectory => registry.GetIdentityDirectory(draft.GatewayId);
    public CancellationToken LifetimeToken => _lifetime.Token;
    public static string GetPairingGuidance(string? requestId)
    {
        var instruction = ApprovalRequestHelper.IsSafeRequestId(requestId)
            ? $"Run openclaw devices approve {requestId}."
            : "Run openclaw devices list, then approve the request for this Companion.";
        return $"This Companion needs pairing approval. Open the Gateway terminal below, which uses this setup profile. {instruction} Then choose Retry.";
    }

    public void OpenRecoveryTerminal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _terminal?.Dispose();
        _terminal = host.OpenRecoveryTerminal(package);
    }

    public Task PrepareWizardAsync(CancellationToken cancellationToken) => PrepareConnectionAsync(cancellationToken);

    public Task PrepareConnectionAsync(CancellationToken cancellationToken) => AuthorizeAsync(cancellationToken);

    internal async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await host.ValidateConfigurationAsync(package, cancellationToken);
            await AuthorizeCoreAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task AuthorizeAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await AuthorizeCoreAsync(linked.Token);
        }
        finally { _gate.Release(); }
    }

    public async Task ApproveWizardPairingAsync(string? requestId, CancellationToken cancellationToken)
    {
        if (!ApprovalRequestHelper.IsSafeRequestId(requestId))
            throw new InvalidOperationException("The Gateway did not supply a safe pairing request for this Companion.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await AuthorizeCoreAsync(linked.Token);
            await RequirePairingConfigurationAsync(linked.Token);
            var identity = new DeviceIdentity(IdentityDirectory);
            identity.Initialize();
            var normalizedId = requestId!.Trim();
            var listing = await host.ListDevicePairingRequestsAsync(package, linked.Token);
            ApprovalRequestHelper.RequireMatchingDeviceRequest(
                listing, normalizedId, identity.DeviceId, identity.PublicKeyBase64Url);
            // Recheck after the CLI read and before issuing the exact approval.
            await AuthorizeCoreAsync(linked.Token);
            await RequirePairingConfigurationAsync(linked.Token);
            var result = await host.ApproveDevicePairingAsync(package, normalizedId, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var approved = ApprovalRequestHelper.TryReadApprovedRequestId(result);
            if (!approved.Success || approved.RequestId != normalizedId)
                throw new InvalidOperationException("The Gateway did not confirm this Companion's pairing approval.");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Retires an operator device token the verified Gateway has just rejected so the
    /// next attempt can fall back to the shared setup credential and re-pair. Allowed
    /// once per staged session. Gateway ownership and the setup credential are
    /// re-verified first, so a token is never discarded because an unverified listener
    /// refused it.
    /// </summary>
    /// <returns><c>true</c> if the rejected token was retired.</returns>
    internal async Task<bool> RecoverRejectedOperatorTokenAsync(
        string rejectedToken, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operatorTokenRecoveryUsed, 1, 0) != 0)
            return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await AuthorizeCoreAsync(linked.Token);
            await RequirePairingConfigurationAsync(linked.Token);
            return DeviceIdentity.TryClearDeviceTokenForRoleIfMatches(
                IdentityDirectory, "operator", rejectedToken);
        }
        finally { _gate.Release(); }
    }

    private async Task RequirePairingConfigurationAsync(CancellationToken cancellationToken)
    {
        IsolatedGatewayConfiguration configuration =
            await host.CheckIsolatedPairingConfigurationAsync(package, cancellationToken);
        if (configuration.Port != new Uri(Record.Url).Port ||
            !string.Equals(configuration.Token, Record.SharedGatewayToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The isolated Gateway port or token changed. Restart Companion setup before pairing.");
        }
    }

    private async Task AuthorizeCoreAsync(CancellationToken cancellationToken)
    {
        RequireCurrentProfile();
        await RequirePairingConfigurationAsync(cancellationToken);
        // Never let loopback or an existing device token bypass package workload inspection.
        host.ReportProgress(NativeGatewaySetupStage.StartingGateway);
        await runtime.EnsureRunningAsync(Record, cancellationToken);
        host.ReportProgress(NativeGatewaySetupStage.VerifyingEndpoint);
        var provenance = await runtime.InspectAsync(Record, cancellationToken);
        if (provenance.Kind != GatewayEndpointProvenanceKind.ExpectedManagedGateway)
            throw new InvalidOperationException("Native gateway ownership could not be verified. No credential was sent.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal void RequireCurrentProfile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lifetime.Token.ThrowIfCancellationRequested();
        if (Record.Id != draft.GatewayId || Record.NativePackageFamilyName != draft.PackageFamilyName ||
            Record.NativeRuntimeContract != NativeGatewayPackageClient.IsolatedContract ||
            NativeGatewayPaths.ValidateRecord(Record).Port != draft.Port)
            throw new SetupNativeOwnershipException();
        if (_identityBinding is { } admitted)
            SetupCompletionAuthority.RequirePersistedIdentity(IdentityDirectory, admitted);
    }

    internal void AdmitIdentity(string? identityBinding)
    {
        if (string.IsNullOrWhiteSpace(identityBinding))
            throw new SetupNativeOwnershipException();
        var admitted = Interlocked.CompareExchange(ref _identityBinding, identityBinding, null);
        if (admitted is not null && admitted != identityBinding)
            throw new SetupNativeOwnershipException();
    }

    public void RequireCompletion(GatewayAiSetupCompletion proof)
    {
        RequireCurrentProfile();
        if (proof.GatewayId != Record.Id || proof.EndpointBinding != GatewayDashboardBinding.Capture(Record) ||
            proof.ModelTarget is not null || proof.VerifiedGeneration <= 0 ||
            !SetupCompletionAuthority.IsValid(proof.IdentityBinding, proof.SessionKey, proof.AgentId))
            throw new SetupNativeOwnershipException();
        SetupCompletionAuthority.RequirePersistedIdentity(IdentityDirectory, proof.IdentityBinding);
        if (_published)
        {
            registry.Load();
            var active = registry.GetActive();
            if (active?.Id != proof.GatewayId || GatewayDashboardBinding.Capture(active) != proof.EndpointBinding)
                throw new SetupNativeOwnershipException();
        }
    }

    public async Task<SetupVerifiedNativeRoute> VerifyAsync(GatewayAiSetupCompletion expected, CancellationToken ct)
    {
        RequireCompletion(expected);
        await using var connection = await NativeGatewaySetupConnection.ConnectAsync(this, ct);
        return await VerifyConnectionAsync(connection, expected, ct);
    }

    private static async Task<SetupVerifiedNativeRoute> VerifyConnectionAsync(
        NativeGatewaySetupConnection connection, GatewayAiSetupCompletion expected, CancellationToken ct)
    {
        SetupNativeVerification.RequireRoute(expected, connection.Route);
        var client = new GatewayAiSetupClient(connection, expected.ModelRef, expected.Intent, expected.RequiresManagedLocalAi);
        var result = new SetupVerifiedNativeRoute(
            await SetupNativeCompletionVerifier.VerifyModelAsync(client, expected.ModelRef, ct),
            connection.Route.SessionKey ?? "");
        SetupNativeVerification.RequireSame(expected, result);
        return result;
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            BeginWizard();
            _terminal?.Dispose();
            _terminal = null;
            // Upstream may have changed configuration before an error. Validate before restart.
            await host.ValidateConfigurationAsync(package, linked.Token);
            await runtime.RestartAsync(Record, linked.Token);
            await AuthorizeCoreAsync(linked.Token);
        }
        finally { _gate.Release(); }
    }

    public void MarkWizardCompleted() => _wizardCompleted = true;
    public void MarkOptionalSetupDeferred() => _optionalSetupDeferred = true;
    public void BeginWizard()
    {
        if (_published)
            throw new InvalidOperationException(
                "This Gateway setup has already been published. Retry completion instead of restarting onboarding.");
        _wizardCompleted = false;
        _optionalSetupDeferred = false;
    }

    public Task<GatewayRecord> CompleteAsync(
        CancellationToken cancellationToken, CapabilitiesConfig? capabilities = null) =>
        CompleteCoreAsync(cancellationToken, capabilities, null);

    public Task<GatewayRecord> CompleteVerifiedAsync(
        GatewayAiSetupCompletion proof, CapabilitiesConfig capabilities, CancellationToken cancellationToken,
        Func<IGatewayAiSetupTransport, CancellationToken, Task>? afterVerification = null)
    {
        RequireCompletion(proof);
        return CompleteCoreAsync(cancellationToken, capabilities, proof, afterVerification);
    }

    private async Task<GatewayRecord> CompleteCoreAsync(
        CancellationToken cancellationToken, CapabilitiesConfig? capabilities, GatewayAiSetupCompletion? proof,
        Func<IGatewayAiSetupTransport, CancellationToken, Task>? afterVerification = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            if (proof is null && !_wizardCompleted && !_optionalSetupDeferred)
                throw new InvalidOperationException("Finish the Gateway wizard before completing native setup.");
            if (_published)
                return Record;

            // Focused setup has drained its operator. Keep stop ownership through
            // RestartAsync; StopAsync would discard that lifecycle attribution.
            if (proof is null)
                await runtime.StopAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (capabilities is not null)
                await host.ApplyIsolatedCapabilitiesAsync(
                    package, capabilities.GetEnabledCommandIds().ToArray(), linked.Token);
            IsolatedGatewayConfiguration configured =
                await host.CheckIsolatedPairingConfigurationAsync(package, linked.Token);
            if (configured.Port != draft.Port || configured.Token != Record.SharedGatewayToken)
                throw new InvalidOperationException(
                    "The isolated Gateway credential or port changed during setup. " +
                    "Restart setup before publishing this Companion connection.");
            await host.ValidateConfigurationAsync(package, linked.Token);
            if (proof is not null)
                await runtime.RestartAsync(Record, linked.Token);
            await AuthorizeCoreAsync(linked.Token);
            await host.VerifyHealthAsync(package, linked.Token);
            if (proof is not null)
            {
                RequireCompletion(proof);
                // The owner gate stays held while a fresh connection verifies the restarted runtime.
                await using var connection = await NativeGatewaySetupConnection.ConnectForFinalizationAsync(
                    this, AuthorizeCoreAsync, linked.Token);
                await VerifyConnectionAsync(connection, proof, linked.Token);
                if (afterVerification is not null)
                    await afterVerification(connection, linked.Token);
            }
            // A verified Gateway stays up through publication so the restarted
            // Companion adopts it instead of waiting through another cold start.
            bool handOff = proof is not null;
            if (!handOff)
                await runtime.StopAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            registry.Load();
            var beforePublication = registry.GetSnapshot();
            if (registry.GetById(Record.Id) is not null)
                throw new SetupNativeOwnershipException();
            registry.AddOrUpdate(Record);
            registry.SetActive(Record.Id);
            registry.Save(beforePublication);
            _published = true;
            if (handOff)
                await runtime.ReleaseAsync(CancellationToken.None);
            // Keep the credential-free draft descriptor. CreateDraftAsync recognizes
            // the published ID and replaces it instead of taking over an active gateway.
            return Record;
        }
        finally
        {
            // A failed health/config check is retryable, but must not leak a running gateway.
            try { await runtime.StopAsync(CancellationToken.None); }
            finally { _gate.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _terminal?.Dispose();
            _terminal = null;
            await runtime.DisposeAsync();
        }
        finally { _gate.Release(); }
    }
}
