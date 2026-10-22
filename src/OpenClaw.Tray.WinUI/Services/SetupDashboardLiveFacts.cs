using OpenClaw.Connection;
using OpenClaw.Chat;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>Optional current-handshake observations, not a re-verification of historical generation numbers.</summary>
internal sealed record SetupDashboardLiveFacts(string GatewayId, string? AgentId, string? ModelRef)
{
    public bool Matches(GatewayAiSetupCompletion completion) =>
        GatewayId == completion.GatewayId &&
        (AgentId is null || AgentId == completion.AgentId) &&
        (ModelRef is null || ModelRef == completion.ModelRef);

    public static SetupDashboardLiveFacts? Capture(GatewayConnectionManager? manager)
    {
        var client = manager?.OperatorClient;
        if (client?.IsConnectedToGateway != true)
            return null;
        var gatewayId = manager!.CurrentSnapshot.GatewayId;
        var key = client.MainSessionKey;
        var session = key is null || client is not OpenClawGatewayClient concrete ? null :
            concrete.GetSessionList().FirstOrDefault(session => session.Key == key);
        if (gatewayId is null || !ReferenceEquals(client, manager.OperatorClient) ||
            gatewayId != manager.CurrentSnapshot.GatewayId)
            throw new InvalidOperationException("The Gateway connection changed during Dashboard admission.");
        return Project(gatewayId, key, session?.Model, session?.Provider);
    }

    internal static SetupDashboardLiveFacts Project(
        string gatewayId, string? mainSessionKey, string? sessionModel, string? sessionProvider = null)
    {
        var parts = mainSessionKey?.Split(':', 3);
        var agent = parts is ["agent", { Length: > 0 } id, { Length: > 0 }] ? id : null;
        // Model IDs can themselves contain slashes. Only the separate provider identifies their namespace.
        var model = !string.IsNullOrWhiteSpace(sessionModel) && !string.IsNullOrWhiteSpace(sessionProvider)
            ? ChatModelChoice.BuildSelectionId(sessionModel, sessionProvider) : null;
        return new(gatewayId, agent, model);
    }
}
