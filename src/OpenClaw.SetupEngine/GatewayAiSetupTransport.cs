using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public sealed class GatewayAiSetupTransport(
    OpenClawGatewayClient client,
    Func<GatewayAiSetupRoute> routeProvider) : IGatewayAiSetupTransport
{
    public GatewayAiSetupRoute Route => routeProvider();
    public long Generation => client.ServerHandshakeGeneration;
    public bool IsConnected => client.IsConnectedToGateway && client.HasHandshakeSnapshot;
    public IReadOnlyCollection<string> Methods => client.AdvertisedServerMethods;
    public IReadOnlyCollection<string> OperatorScopes => client.GrantedOperatorScopes;

    public async Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var route = routeProvider();
        // Cancelling the local wait never implies rollback of an admitted gateway
        // operation. The focused client retains its session for cancel/reconciliation.
        var result = await client.SendWizardRequestAsync(method, parameters, timeoutMs).WaitAsync(cancellationToken);
        if (routeProvider() != route)
            throw new InvalidOperationException("The setup Gateway authority changed during the request.");
        return result;
    }
}
