using OpenClaw.Connection;
using OpenClaw.Shared;
using System.Diagnostics;

namespace OpenClaw.SetupEngine;

/// <summary>Temporary operator session for setup, with registry-owned credentials and endpoint provenance.</summary>
public sealed class SetupGatewaySession : IAsyncDisposable
{
    private readonly string _dataDir;
    private readonly string _identityPath;
    private readonly SetupGatewaySessionBinding _binding;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    public OpenClawGatewayClient Client { get; }
    public GatewayHostAccessPlan HostAccessPlan { get; }

    private SetupGatewaySession(string dataDir, GatewayRecord record, SetupGatewaySessionBinding binding,
        string identityPath, OpenClawGatewayClient client)
    {
        _dataDir = dataDir;
        _identityPath = identityPath;
        _binding = binding;
        Client = client;
        HostAccessPlan = GatewayHostAccessClassifier.Classify(record);
    }

    public GatewayAiSetupRoute GetRoute()
    {
        var registry = new GatewayRegistry(_dataDir);
        registry.Load();
        var route = _binding.GetRoute(registry.GetActive(), _identityPath, Client.MainSessionKey, Client.AuthenticatedSigningDeviceId);
        SetupCompletionAuthority.RequirePersistedIdentity(_identityPath, route.IdentityBinding);
        return route;
    }

    public static void RequireCompletionGateway(string dataDir, GatewayAiSetupCompletion completion)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var active = registry.GetActive();
        if (active is null || active.Id != completion.GatewayId ||
            GatewayDashboardBinding.Capture(active) != completion.EndpointBinding ||
            !SetupCompletionAuthority.IsValid(completion.IdentityBinding, completion.SessionKey, completion.AgentId))
            throw new InvalidOperationException("The verified Gateway changed before setup completed. Return to that Gateway and verify again.");
        SetupCompletionAuthority.RequirePersistedIdentity(registry.GetIdentityDirectory(active.Id), completion.IdentityBinding);
    }

    public static async Task<SetupGatewaySession> ConnectAsync(
        string dataDir, Func<bool>? expectedRestart = null, CancellationToken ct = default,
        string? expectedGatewayId = null, GatewayAiSetupCompletion? expectedCompletion = null,
        bool rejectNativeGateway = false)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var record = registry.GetActive() ?? throw new InvalidOperationException("No active gateway record found.");
        if (rejectNativeGateway && record.NativePackageFamilyName is not null)
            throw new InvalidOperationException(
                "Native onboarding requires its setup-owned runtime. Return to native Gateway setup.");
        var binding = new SetupGatewaySessionBinding(record);
        void RequireCurrentGateway()
        {
            var current = new GatewayRegistry(dataDir);
            current.Load();
            binding.RequireCurrent(current.GetActive());
            if (expectedCompletion is not null)
                RequireCompletionGateway(dataDir, expectedCompletion);
        }
        LocalAiOnboardingUse.RequireGateway(expectedGatewayId, record.Id);
        RequireCurrentGateway();
        var identityPath = registry.GetIdentityDirectory(record.Id);
        var deviceToken = DeviceIdentity.TryReadStoredDeviceToken(identityPath);
        var token = deviceToken ?? record.SharedGatewayToken ?? record.BootstrapToken
            ?? throw new InvalidOperationException("No gateway credential found.");
        var gatewayUrl = binding.Endpoint;
        var provenanceService = new ManagedLocalGatewayPortProvenanceService(NullLogger.Instance);
        var needsProvenance = record.SshTunnel is null &&
            GatewayRecordEditing.ResolveManagedDistroName(record) is not null &&
            GatewayRecordEditing.IsLoopbackEndpoint(record.Url);
        if (deviceToken is null && needsProvenance)
        {
            var provenance = await provenanceService.InspectAsync(record, ct);
            if (provenance.Kind != GatewayEndpointProvenanceKind.ExpectedManagedGateway)
                throw new InvalidOperationException("The managed gateway address is not owned by the verified WSL gateway; no credential was sent.");
        }
        RequireCurrentGateway();
        var client = new OpenClawGatewayClient(gatewayUrl, token, logger: NullLogger.Instance, identityPath: identityPath,
            requireExistingIdentity: expectedCompletion is not null)
        {
            UseV2Signature = true
        };
        async Task<ReconnectAuthorizationResult> AuthorizeHandshakeAsync(CancellationToken cancellationToken)
        {
            RequireCurrentGateway();
            if (!needsProvenance)
                return ReconnectAuthorizationResult.AllowedResult;
            var provenance = expectedRestart?.Invoke() == true
                ? await GatewayWizardRestartRecoveryPolicy.WaitForExpectedManagedGatewayAsync(
                    token => provenanceService.InspectAsync(record, token), 30, TimeSpan.FromSeconds(1), cancellationToken)
                : await provenanceService.InspectAsync(record, cancellationToken);
            RequireCurrentGateway();
            return provenance.Kind == GatewayEndpointProvenanceKind.ExpectedManagedGateway
                ? ReconnectAuthorizationResult.AllowedResult
                : new ReconnectAuthorizationResult(false, GatewayErrorKind.LocalPortConflict, provenance.Detail);
        }
        client.ReconnectAuthorizationAsync = AuthorizeHandshakeAsync;
        client.HandshakeAuthorizationAsync = AuthorizeHandshakeAsync;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void StatusChanged(object? sender, ConnectionStatus status)
        {
            if (status == ConnectionStatus.Connected)
                connected.TrySetResult();
            else if (status is ConnectionStatus.Error or ConnectionStatus.Disconnected)
                connected.TrySetException(new InvalidOperationException("Could not connect to the gateway."));
        }
        client.StatusChanged += StatusChanged;
        using var cancellation = ct.Register(static state => ((OpenClawGatewayClient)state!).Dispose(), client);
        try
        {
            ct.ThrowIfCancellationRequested();
            RequireCurrentGateway();
            await client.ConnectAsync().WaitAsync(ct);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            ct.ThrowIfCancellationRequested();
            RequireCurrentGateway();
            return new(dataDir, record, binding, identityPath, client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        finally { client.StatusChanged -= StatusChanged; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try { await Client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            Trace.TraceWarning("AI setup gateway disconnect timed out. Disposing the owned connection.");
        }
        finally { Client.Dispose(); }
    }
}
