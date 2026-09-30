using System.Text.Json;
using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

public sealed class GatewayAiSetupTransport(
    OpenClawGatewayClient client,
    Func<GatewayAiSetupRoute> routeProvider,
    Func<CancellationToken, Task>? authorize = null,
    Action<GatewayAiSetupRoute>? requireRestartAuthority = null) : IGatewayAiSetupTransport
{
    public static async Task<IGatewayAiSetupTransport> BorrowNativeAsync(
        string dataDir, GatewayConnectionManager manager, string gatewayId, CancellationToken ct,
        string? expectedEndpointBinding = null)
    {
        var registry = new GatewayRegistry(dataDir);
        registry.Load();
        var record = registry.GetActive();
        SetupGatewaySessionBinding.RequireExpected(record, gatewayId, expectedEndpointBinding);
        if (record?.Id != gatewayId || record.NativePackageFamilyName is null)
            throw new SetupNativeOwnershipException();
        var binding = new SetupGatewaySessionBinding(record);
        void RequireOwner()
        {
            registry.Load();
            binding.RequireCurrent(registry.GetActive());
            if (manager.CurrentSnapshot.GatewayId is { } current && current != gatewayId)
                throw new SetupNativeOwnershipException();
        }
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ready.CancelAfter(TimeSpan.FromSeconds(20));
        while (manager.OperatorClient is not { IsConnectedToGateway: true, HasHandshakeSnapshot: true })
        {
            RequireOwner();
            if (manager.CurrentSnapshot.OperatorState is RoleConnectionState.Error or RoleConnectionState.PairingRequired)
                throw new InvalidOperationException("The native Gateway connection needs attention before setup can open.");
            await Task.Delay(100, ready.Token);
        }
        RequireOwner();
        var borrowed = await manager.RequireNativeSetupClientAsync(record, ct);
        var identity = registry.GetIdentityDirectory(gatewayId);
        GatewayAiSetupRoute CaptureRoute()
        {
            RequireOwner();
            if (!ReferenceEquals(borrowed, manager.ConcreteOperatorClient))
                throw new SetupNativeOwnershipException();
            var route = binding.GetRoute(registry.GetActive(), identity,
                borrowed.MainSessionKey, borrowed.AuthenticatedSigningDeviceId);
            SetupCompletionAuthority.RequirePersistedIdentity(identity, route.IdentityBinding);
            return route;
        }
        var admitted = CaptureRoute();
        GatewayAiSetupRoute Route()
        {
            var current = CaptureRoute();
            if (current != admitted) throw new SetupNativeOwnershipException();
            return current;
        }
        return new GatewayAiSetupTransport(borrowed, Route,
            async token => { await manager.RequireNativeSetupClientAsync(record, token); },
            expected =>
            {
                RequireOwner();
                if (!ReferenceEquals(borrowed, manager.ConcreteOperatorClient))
                    throw new SetupNativeOwnershipException();
                binding.RequirePersistedAuthority(registry.GetActive(), identity, expected);
            });
    }

    public GatewayAiSetupRoute Route => routeProvider();
    public long Generation => client.ServerHandshakeGeneration;
    public bool IsConnected => client.IsConnectedToGateway && client.HasHandshakeSnapshot;
    public IReadOnlyCollection<string> Methods => client.AdvertisedServerMethods;
    public IReadOnlyCollection<string> OperatorScopes => client.GrantedOperatorScopes;

    public void RequireRestartAuthority(GatewayAiSetupRoute expected)
    {
        if (requireRestartAuthority is not null)
            requireRestartAuthority(expected);
        else if (Route != expected)
            throw new SetupNativeOwnershipException();
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var route = routeProvider();
        if (authorize is not null)
            await authorize(cancellationToken);
        if (routeProvider() != route)
            throw new SetupNativeOwnershipException();
        // Cancelling the local wait never implies rollback of an admitted gateway
        // operation. The focused client retains its session for cancel/reconciliation.
        var result = await client.SendWizardRequestAsync(method, parameters, timeoutMs).WaitAsync(cancellationToken);
        if (routeProvider() != route)
            throw new InvalidOperationException("The setup Gateway authority changed during the request.");
        return result;
    }
}
